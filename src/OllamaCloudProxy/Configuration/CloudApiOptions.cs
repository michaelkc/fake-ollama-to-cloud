namespace OllamaCloudProxy.Configuration;

/// <summary>
/// Settings for the upstream, cloud-hosted, OpenAI-compatible API this proxy forwards requests to.
/// Bound from the "CloudApi" configuration section (appsettings.json, or env vars such as
/// CloudApi__BaseUrl, CloudApi__ApiKey, CloudApi__DefaultModel, CloudApi__Models__0).
/// </summary>
public sealed class CloudApiOptions
{
    public const string SectionName = "CloudApi";

    /// <summary>
    /// Base URL of the OpenAI-compatible API, e.g. "https://api.openai.com/v1".
    /// Requests are sent to "{BaseUrl}/chat/completions", "{BaseUrl}/models", etc.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>Bearer token sent as "Authorization: Bearer {ApiKey}" to the upstream API.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Model name to report/use when a client doesn't specify one explicitly.
    /// Client-supplied model names are otherwise passed straight through to the upstream API.
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// Fallback list of model names to advertise from /api/tags if the upstream API's
    /// GET /models call fails or isn't supported. If empty, falls back to just DefaultModel.
    /// </summary>
    public string[] Models { get; set; } = [];
}
