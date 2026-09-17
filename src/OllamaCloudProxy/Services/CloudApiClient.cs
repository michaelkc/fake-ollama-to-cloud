using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OllamaCloudProxy.Configuration;

namespace OllamaCloudProxy.Services;

/// <summary>Thin wrapper around the upstream OpenAI-compatible cloud API.</summary>
public sealed class CloudApiClient
{
    public const string HttpClientName = "cloud-api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<CloudApiOptions> _options;

    public CloudApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<CloudApiOptions> options
    )
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    private HttpClient CreateClient()
    {
        var opts = _options.CurrentValue;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(opts.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                opts.ApiKey
            );
        }
        return client;
    }

    /// <summary>POSTs a non-streaming chat completion request and returns the parsed JSON response.</summary>
    public async Task<JsonObject> ChatCompletionAsync(
        JsonObject requestBody,
        CancellationToken cancellationToken
    )
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(
                requestBody.ToJsonString(),
                Encoding.UTF8,
                "application/json"
            ),
        };

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CloudApiException((int)response.StatusCode, body);
        }

        return JsonNode.Parse(body) as JsonObject
            ?? throw new CloudApiException(502, "Upstream returned an unparsable response body.");
    }

    /// <summary>
    /// POSTs a streaming (SSE) chat completion request and yields each parsed "data: {...}" chunk as JSON.
    /// The literal "data: [DONE]" terminator is consumed, not yielded.
    /// </summary>
    public async IAsyncEnumerable<JsonObject> ChatCompletionStreamAsync(
        JsonObject requestBody,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(
                requestBody.ToJsonString(),
                Encoding.UTF8,
                "application/json"
            ),
        };

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new CloudApiException((int)response.StatusCode, errorBody);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;
            if (line.Length == 0)
                continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line["data:".Length..].Trim();
            if (payload is "[DONE]")
                yield break;
            if (payload.Length == 0)
                continue;

            JsonObject? chunk;
            try
            {
                chunk = JsonNode.Parse(payload) as JsonObject;
            }
            catch (JsonException)
            {
                continue; // Skip malformed/partial lines rather than failing the whole stream.
            }

            if (chunk is not null)
                yield return chunk;
        }
    }

    /// <summary>Calls GET /models and returns the list of model ids reported by the upstream API.</summary>
    public async Task<List<string>> ListModelIdsAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync("models", cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonNode.Parse(body) as JsonObject;
        var data = json?["data"] as JsonArray;

        var ids = new List<string>();
        if (data is not null)
        {
            foreach (var item in data)
            {
                var id = (item as JsonObject)?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }
        return ids;
    }
}

public sealed class CloudApiException(int statusCode, string body)
    : Exception($"Cloud API returned HTTP {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}
