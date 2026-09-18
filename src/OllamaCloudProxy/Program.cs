using Microsoft.Extensions.Configuration.Memory;
using OllamaCloudProxy.Configuration;
using OllamaCloudProxy.Endpoints;
using OllamaCloudProxy.Services;

var builder = WebApplication.CreateBuilder(args);

// Default listen address (Ollama's own default port), lowest priority so it's overridden by
// appsettings.json's "urls" key, ASPNETCORE_URLS/urls env var, or --urls on the command line.
builder.Configuration.Sources.Insert(
    0,
    new MemoryConfigurationSource
    {
        InitialData = new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:11434" },
    }
);

builder.Services.Configure<CloudApiOptions>(
    builder.Configuration.GetSection(CloudApiOptions.SectionName)
);
builder.Services.AddHttpClient(CloudApiClient.HttpClientName);
builder.Services.AddSingleton<CloudApiClient>();

var app = builder.Build();

var cloudApiConfig = app
    .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CloudApiOptions>>()
    .Value;
if (string.IsNullOrWhiteSpace(cloudApiConfig.BaseUrl))
{
    app.Logger.LogError(
        "CloudApi:BaseUrl is not configured. Set it in appsettings.json or the CloudApi__BaseUrl environment variable."
    );
}
if (string.IsNullOrWhiteSpace(cloudApiConfig.ApiKey))
{
    app.Logger.LogWarning(
        "CloudApi:ApiKey is empty. Requests to the upstream API will be sent without an Authorization header."
    );
}

await FailFastIfOllamaAlreadyRunningAsync(app.Configuration["urls"], app.Logger);

app.MapOllamaEndpoints();

app.Run();

// A real Ollama install commonly auto-starts (tray app / login item) and grabs 11434 before this
// proxy gets a chance to. When that happens, Kestrel's bind fails, the console window closes, and
// every request silently goes to real Ollama instead - which reports its own "model not found" for
// whatever cloud model name was requested, with no indication the proxy never ran at all. Detect
// that specific situation up front and refuse to start with an actionable message instead.
static async Task FailFastIfOllamaAlreadyRunningAsync(string? urls, ILogger logger)
{
    foreach (var url in (urls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            continue;

        var probeHost = uri.Host is "0.0.0.0" or "+" or "*" or "::" ? "127.0.0.1" : uri.Host;
        var probeUri = new Uri($"{uri.Scheme}://{probeHost}:{uri.Port}/api/version");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(probeUri);
        }
        catch
        {
            continue; // Nothing answering there (yet) - let Kestrel's own bind attempt be authoritative.
        }

        if (!response.IsSuccessStatusCode)
            continue;

        var body = await response.Content.ReadAsStringAsync();
        if (body.Contains("cloud-proxy", StringComparison.OrdinalIgnoreCase))
            continue; // Looks like another instance of this same proxy, not real Ollama.

        logger.LogCritical(
            "Something that looks like a real Ollama instance is already listening at {ProbeUri} "
                + "(responded: {Body}). This proxy needs to own that port itself. Stop the real "
                + "Ollama app first (quit it from the system tray, or `ollama stop`/kill the process), "
                + "then start this proxy again.",
            probeUri,
            body.Trim()
        );
        Environment.Exit(1);
    }
}
