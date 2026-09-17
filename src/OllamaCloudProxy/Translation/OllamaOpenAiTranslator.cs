using System.Text.Json.Nodes;

namespace OllamaCloudProxy.Translation;

/// <summary>
/// Pure JSON-shape translation between Ollama's local API wire format and the
/// OpenAI-compatible /v1/chat/completions wire format. No I/O here.
/// </summary>
public static class OllamaOpenAiTranslator
{
    /// <summary>Builds an OpenAI-compatible chat completion request from an Ollama /api/chat request body.</summary>
    public static JsonObject ChatRequestToOpenAi(JsonObject ollamaRequest, string model, bool stream)
    {
        var openAi = new JsonObject
        {
            ["model"] = model,
            ["stream"] = stream,
        };

        if (ollamaRequest["messages"] is JsonArray messages)
        {
            var mapped = new JsonArray();
            foreach (var node in messages)
            {
                if (node is not JsonObject msg) continue;

                var mappedMsg = new JsonObject
                {
                    ["role"] = msg["role"]?.DeepClone() ?? "user",
                    ["content"] = BuildContent(msg["content"]?.GetValue<string>(), msg["images"] as JsonArray),
                };

                // Best-effort passthrough for tool-calling shapes; OpenAI and Ollama agree closely here.
                if (msg["tool_calls"] is { } toolCalls) mappedMsg["tool_calls"] = toolCalls.DeepClone();
                if (msg["tool_call_id"] is { } toolCallId) mappedMsg["tool_call_id"] = toolCallId.DeepClone();
                if (msg["name"] is { } name) mappedMsg["name"] = name.DeepClone();

                mapped.Add(mappedMsg);
            }
            openAi["messages"] = mapped;
        }

        if (ollamaRequest["tools"] is { } tools) openAi["tools"] = tools.DeepClone();

        ApplySamplingOptions(openAi, ollamaRequest["options"] as JsonObject);

        return openAi;
    }

    /// <summary>Builds an OpenAI-compatible chat completion request from an Ollama /api/generate request body.</summary>
    public static JsonObject GenerateRequestToOpenAi(JsonObject ollamaRequest, string model, bool stream)
    {
        var openAi = new JsonObject
        {
            ["model"] = model,
            ["stream"] = stream,
        };

        var messages = new JsonArray();

        var system = ollamaRequest["system"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(system))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        var prompt = ollamaRequest["prompt"]?.GetValue<string>() ?? "";
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = BuildContent(prompt, ollamaRequest["images"] as JsonArray),
        });

        openAi["messages"] = messages;
        // Note: Ollama's "context" field (opaque token-context resume) has no OpenAI equivalent
        // and is intentionally dropped; each call is treated as stateless.

        ApplySamplingOptions(openAi, ollamaRequest["options"] as JsonObject);

        return openAi;
    }

    /// <summary>
    /// Builds an OpenAI-compatible "content" value: a plain string for text-only messages, or a
    /// content-parts array (text + image_url) when Ollama's per-message/per-request "images"
    /// field (a list of base64-encoded image bytes, no data-URI wrapper) is present.
    /// </summary>
    private static JsonNode BuildContent(string? text, JsonArray? images)
    {
        text ??= "";
        if (images is null || images.Count == 0) return text;

        var parts = new JsonArray();
        if (text.Length > 0)
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        foreach (var imageNode in images)
        {
            var base64 = imageNode?.GetValue<string>();
            if (string.IsNullOrEmpty(base64)) continue;

            // Ollama sends raw base64; tolerate an already-wrapped data URI too, just in case.
            var commaIndex = base64.IndexOf(',');
            var rawBase64 = base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0
                ? base64[(commaIndex + 1)..]
                : base64;

            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{GuessImageMimeType(rawBase64)};base64,{rawBase64}" },
            });
        }

        return parts;
    }

    /// <summary>Sniffs an image's MIME type from the leading characters of its base64 encoding.</summary>
    private static string GuessImageMimeType(string base64)
    {
        if (base64.StartsWith("iVBORw0KGgo", StringComparison.Ordinal)) return "image/png";
        if (base64.StartsWith("/9j/", StringComparison.Ordinal)) return "image/jpeg";
        if (base64.StartsWith("R0lGOD", StringComparison.Ordinal)) return "image/gif";
        if (base64.StartsWith("UklGR", StringComparison.Ordinal)) return "image/webp";
        return "image/jpeg";
    }

    private static void ApplySamplingOptions(JsonObject openAi, JsonObject? options)
    {
        if (options is null) return;

        if (options["temperature"] is { } temperature) openAi["temperature"] = temperature.DeepClone();
        if (options["top_p"] is { } topP) openAi["top_p"] = topP.DeepClone();
        if (options["seed"] is { } seed) openAi["seed"] = seed.DeepClone();
        if (options["stop"] is { } stop) openAi["stop"] = stop.DeepClone();
        if (options["presence_penalty"] is { } presencePenalty) openAi["presence_penalty"] = presencePenalty.DeepClone();
        if (options["frequency_penalty"] is { } frequencyPenalty) openAi["frequency_penalty"] = frequencyPenalty.DeepClone();

        if (options["num_predict"] is JsonValue numPredict && numPredict.TryGetValue<int>(out var maxTokens) && maxTokens > 0)
        {
            openAi["max_tokens"] = maxTokens;
        }
    }

    /// <summary>Extracts the assistant message (role, content) from a non-streaming OpenAI chat completion response.</summary>
    public static (string Role, string Content, string? FinishReason, JsonObject? Usage) ParseOpenAiChatResponse(JsonObject openAiResponse)
    {
        var choice = openAiResponse["choices"]?.AsArray().Count > 0 ? openAiResponse["choices"]![0] as JsonObject : null;
        var message = choice?["message"] as JsonObject;

        var role = message?["role"]?.GetValue<string>() ?? "assistant";
        var content = message?["content"]?.GetValue<string>() ?? "";
        var finishReason = choice?["finish_reason"]?.GetValue<string>();
        var usage = openAiResponse["usage"] as JsonObject;

        return (role, content, finishReason, usage);
    }

    /// <summary>Extracts the incremental delta content (if any) and finish_reason (if any) from one OpenAI streaming chunk.</summary>
    public static (string? DeltaContent, string? FinishReason) ParseOpenAiStreamChunk(JsonObject chunk)
    {
        var choices = chunk["choices"] as JsonArray;
        if (choices is null || choices.Count == 0) return (null, null);

        var choice = choices[0] as JsonObject;
        var delta = choice?["delta"] as JsonObject;
        var content = delta?["content"]?.GetValue<string>();
        var finishReason = choice?["finish_reason"]?.GetValue<string>();

        return (content, finishReason);
    }

    // ---- Ollama-shaped response builders ----

    public static JsonObject BuildOllamaChatResponse(
        string model, string role, string content, string? doneReason, JsonObject? usage, long totalDurationNs)
    {
        var response = new JsonObject
        {
            ["model"] = model,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = new JsonObject { ["role"] = role, ["content"] = content },
            ["done"] = true,
            ["done_reason"] = doneReason ?? "stop",
            ["total_duration"] = totalDurationNs,
        };
        ApplyUsage(response, usage);
        return response;
    }

    public static JsonObject BuildOllamaChatStreamChunk(string model, string deltaContent)
    {
        return new JsonObject
        {
            ["model"] = model,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = deltaContent },
            ["done"] = false,
        };
    }

    public static JsonObject BuildOllamaChatStreamDoneChunk(string model, string? doneReason, long totalDurationNs)
    {
        return new JsonObject
        {
            ["model"] = model,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "" },
            ["done"] = true,
            ["done_reason"] = doneReason ?? "stop",
            ["total_duration"] = totalDurationNs,
        };
    }

    public static JsonObject BuildOllamaGenerateResponse(
        string model, string content, string? doneReason, JsonObject? usage, long totalDurationNs)
    {
        var response = new JsonObject
        {
            ["model"] = model,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["response"] = content,
            ["done"] = true,
            ["done_reason"] = doneReason ?? "stop",
            ["total_duration"] = totalDurationNs,
        };
        ApplyUsage(response, usage);
        return response;
    }

    public static JsonObject BuildOllamaGenerateStreamChunk(string model, string deltaContent)
    {
        return new JsonObject
        {
            ["model"] = model,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["response"] = deltaContent,
            ["done"] = false,
        };
    }

    public static JsonObject BuildOllamaGenerateStreamDoneChunk(string model, string? doneReason, long totalDurationNs)
    {
        return new JsonObject
        {
            ["model"] = model,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["response"] = "",
            ["done"] = true,
            ["done_reason"] = doneReason ?? "stop",
            ["total_duration"] = totalDurationNs,
        };
    }

    private static void ApplyUsage(JsonObject response, JsonObject? usage)
    {
        if (usage is null) return;
        if (usage["prompt_tokens"] is { } promptTokens) response["prompt_eval_count"] = promptTokens.DeepClone();
        if (usage["completion_tokens"] is { } completionTokens) response["eval_count"] = completionTokens.DeepClone();
    }
}
