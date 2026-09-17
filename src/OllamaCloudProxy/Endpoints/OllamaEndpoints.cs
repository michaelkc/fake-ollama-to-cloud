using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OllamaCloudProxy.Configuration;
using OllamaCloudProxy.Services;
using OllamaCloudProxy.Translation;

namespace OllamaCloudProxy.Endpoints;

/// <summary>Maps the subset of Ollama's local HTTP API that this proxy understands.</summary>
public static class OllamaEndpoints
{
    public static void MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Text("Ollama is running"));
        app.MapGet("/api/version", () => Results.Json(new { version = "0.0.0-cloud-proxy" }));

        app.MapPost("/api/chat", HandleChatAsync);
        app.MapPost("/api/generate", HandleGenerateAsync);
        app.MapGet("/api/tags", HandleTagsAsync);
        app.MapPost("/api/show", HandleShowAsync);
    }

    private static async Task HandleChatAsync(
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        CloudApiClient cloudApi,
        IOptionsMonitor<CloudApiOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("OllamaEndpoints.Chat");
        var ollamaRequest = await ReadJsonBodyAsync(httpRequest, cancellationToken);
        if (ollamaRequest is null)
        {
            await WriteBadRequestAsync(httpResponse, "Request body must be a JSON object.");
            return;
        }

        var model = ResolveModel(ollamaRequest, options.CurrentValue);
        var stream = ollamaRequest["stream"]?.GetValue<bool>() ?? true;
        var openAiRequest = OllamaOpenAiTranslator.ChatRequestToOpenAi(ollamaRequest, model, stream);

        var stopwatch = Stopwatch.StartNew();

        if (!stream)
        {
            JsonObject openAiResponse;
            try
            {
                openAiResponse = await cloudApi.ChatCompletionAsync(openAiRequest, cancellationToken);
            }
            catch (CloudApiException ex)
            {
                logger.LogWarning(ex, "Upstream chat completion failed");
                await WriteUpstreamErrorAsync(httpResponse, ex);
                return;
            }

            var (role, content, finishReason, usage) = OllamaOpenAiTranslator.ParseOpenAiChatResponse(openAiResponse);
            var ollamaResponse = OllamaOpenAiTranslator.BuildOllamaChatResponse(
                model, role, content, finishReason, usage, ElapsedNanoseconds(stopwatch));

            await Results.Json(ollamaResponse).ExecuteAsync(httpResponse.HttpContext);
            return;
        }

        httpResponse.ContentType = "application/x-ndjson";
        string? doneReason = null;
        try
        {
            await foreach (var chunk in cloudApi.ChatCompletionStreamAsync(openAiRequest, cancellationToken))
            {
                var (deltaContent, finishReason) = OllamaOpenAiTranslator.ParseOpenAiStreamChunk(chunk);
                if (finishReason is not null) doneReason = finishReason;
                if (string.IsNullOrEmpty(deltaContent)) continue;

                var ollamaChunk = OllamaOpenAiTranslator.BuildOllamaChatStreamChunk(model, deltaContent);
                await WriteNdjsonLineAsync(httpResponse, ollamaChunk, cancellationToken);
            }
        }
        catch (CloudApiException ex)
        {
            logger.LogWarning(ex, "Upstream chat completion stream failed");
            // Headers are already sent for a streaming response; end the stream rather than trying to change status code.
            return;
        }

        var doneChunk = OllamaOpenAiTranslator.BuildOllamaChatStreamDoneChunk(model, doneReason, ElapsedNanoseconds(stopwatch));
        await WriteNdjsonLineAsync(httpResponse, doneChunk, cancellationToken);
    }

    private static async Task HandleGenerateAsync(
        HttpRequest httpRequest,
        HttpResponse httpResponse,
        CloudApiClient cloudApi,
        IOptionsMonitor<CloudApiOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("OllamaEndpoints.Generate");
        var ollamaRequest = await ReadJsonBodyAsync(httpRequest, cancellationToken);
        if (ollamaRequest is null)
        {
            await WriteBadRequestAsync(httpResponse, "Request body must be a JSON object.");
            return;
        }

        var model = ResolveModel(ollamaRequest, options.CurrentValue);
        var stream = ollamaRequest["stream"]?.GetValue<bool>() ?? true;
        var openAiRequest = OllamaOpenAiTranslator.GenerateRequestToOpenAi(ollamaRequest, model, stream);

        var stopwatch = Stopwatch.StartNew();

        if (!stream)
        {
            JsonObject openAiResponse;
            try
            {
                openAiResponse = await cloudApi.ChatCompletionAsync(openAiRequest, cancellationToken);
            }
            catch (CloudApiException ex)
            {
                logger.LogWarning(ex, "Upstream generate completion failed");
                await WriteUpstreamErrorAsync(httpResponse, ex);
                return;
            }

            var (_, content, finishReason, usage) = OllamaOpenAiTranslator.ParseOpenAiChatResponse(openAiResponse);
            var ollamaResponse = OllamaOpenAiTranslator.BuildOllamaGenerateResponse(
                model, content, finishReason, usage, ElapsedNanoseconds(stopwatch));

            await Results.Json(ollamaResponse).ExecuteAsync(httpResponse.HttpContext);
            return;
        }

        httpResponse.ContentType = "application/x-ndjson";
        string? doneReason = null;
        try
        {
            await foreach (var chunk in cloudApi.ChatCompletionStreamAsync(openAiRequest, cancellationToken))
            {
                var (deltaContent, finishReason) = OllamaOpenAiTranslator.ParseOpenAiStreamChunk(chunk);
                if (finishReason is not null) doneReason = finishReason;
                if (string.IsNullOrEmpty(deltaContent)) continue;

                var ollamaChunk = OllamaOpenAiTranslator.BuildOllamaGenerateStreamChunk(model, deltaContent);
                await WriteNdjsonLineAsync(httpResponse, ollamaChunk, cancellationToken);
            }
        }
        catch (CloudApiException ex)
        {
            logger.LogWarning(ex, "Upstream generate completion stream failed");
            return;
        }

        var doneChunk = OllamaOpenAiTranslator.BuildOllamaGenerateStreamDoneChunk(model, doneReason, ElapsedNanoseconds(stopwatch));
        await WriteNdjsonLineAsync(httpResponse, doneChunk, cancellationToken);
    }

    private static async Task<IResult> HandleTagsAsync(
        CloudApiClient cloudApi, IOptionsMonitor<CloudApiOptions> options, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("OllamaEndpoints.Tags");
        var opts = options.CurrentValue;

        List<string> modelIds;
        try
        {
            modelIds = await cloudApi.ListModelIdsAsync(cancellationToken);
            if (modelIds.Count == 0) modelIds = FallbackModelIds(opts);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream GET /models failed; falling back to configured model list");
            modelIds = FallbackModelIds(opts);
        }

        var models = new JsonArray();
        var nowIso = DateTime.UtcNow.ToString("o");
        foreach (var id in modelIds)
        {
            models.Add(new JsonObject
            {
                ["name"] = id,
                ["model"] = id,
                ["modified_at"] = nowIso,
                ["size"] = 0,
                ["digest"] = "",
                ["details"] = new JsonObject
                {
                    ["parent_model"] = "",
                    ["format"] = "api",
                    ["family"] = "cloud",
                    ["families"] = null,
                    ["parameter_size"] = "unknown",
                    ["quantization_level"] = "none",
                },
            });
        }

        return Results.Json(new JsonObject { ["models"] = models });
    }

    private static async Task<IResult> HandleShowAsync(
        HttpRequest httpRequest, IOptionsMonitor<CloudApiOptions> options, CancellationToken cancellationToken)
    {
        var body = await ReadJsonBodyAsync(httpRequest, cancellationToken);
        var model = body is not null ? ResolveModel(body, options.CurrentValue) : (options.CurrentValue.DefaultModel ?? "cloud-model");

        // Real model metadata (Modelfile, template, architecture, etc.) doesn't exist for a cloud-hosted
        // model, so this is a minimally-shaped, synthesized response rather than a faithful proxy.
        var response = new JsonObject
        {
            ["modelfile"] = $"# {model} is proxied to a cloud API; no local Modelfile exists.",
            ["parameters"] = "",
            ["template"] = "",
            ["details"] = new JsonObject
            {
                ["parent_model"] = "",
                ["format"] = "api",
                ["family"] = "cloud",
                ["families"] = null,
                ["parameter_size"] = "unknown",
                ["quantization_level"] = "none",
            },
            ["model_info"] = new JsonObject
            {
                ["general.architecture"] = "cloud-proxy",
                ["general.basename"] = model,
            },
            ["capabilities"] = new JsonArray("completion"),
        };

        return Results.Json(response);
    }

    private static string ResolveModel(JsonObject ollamaRequest, CloudApiOptions options)
    {
        var requested = ollamaRequest["model"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        return options.DefaultModel ?? "cloud-model";
    }

    private static List<string> FallbackModelIds(CloudApiOptions options)
    {
        if (options.Models.Length > 0) return [.. options.Models];
        return [options.DefaultModel ?? "cloud-model"];
    }

    private static async Task<JsonObject?> ReadJsonBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var node = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken);
            return node as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task WriteNdjsonLineAsync(HttpResponse response, JsonObject line, CancellationToken cancellationToken)
    {
        await response.WriteAsync(line.ToJsonString(), cancellationToken);
        await response.WriteAsync("\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static async Task WriteBadRequestAsync(HttpResponse response, string message)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsJsonAsync(new { error = message });
    }

    private static async Task WriteUpstreamErrorAsync(HttpResponse response, CloudApiException ex)
    {
        response.StatusCode = ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway;
        await response.WriteAsJsonAsync(new { error = ex.Body });
    }

    private static long ElapsedNanoseconds(Stopwatch stopwatch) => stopwatch.Elapsed.Ticks * 100L;
}
