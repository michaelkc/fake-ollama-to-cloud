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

app.MapOllamaEndpoints();

app.Run();
