# fake-ollama-to-cloud

A tiny local proxy that makes a **cloud-hosted, OpenAI-compatible API** look like a local **Ollama** server.

It listens on `http://127.0.0.1:11434` — Ollama's default address — and answers the handful of Ollama HTTP endpoints that most Ollama clients actually use. Under the hood, every request is translated into an OpenAI-compatible `chat/completions` call, sent to whatever cloud endpoint you configure (with the `Authorization: Bearer <key>` header it requires), and the response is translated back into Ollama's JSON/NDJSON shape.

## Why

Plenty of tools — IDE plugins, CLI agents, chat UIs — only know how to speak to **Ollama's local, unauthenticated API**. They have no field for a base URL with a different port/path, no field for an API key, no concept of an `Authorization` header. If the only model you have access to is a cloud-hosted one (Azure OpenAI, OpenAI, or any other OpenAI-compatible endpoint), those tools simply can't reach it.

This proxy is the adapter: it runs where the tool expects Ollama to be, holds the cloud endpoint's URL/model/API key in one place, and does the header and request/response translation the tool itself can't. From the tool's point of view, it's just talking to Ollama.

## Features

- Mimics the core Ollama HTTP API:
  - `POST /api/chat` — chat-style requests, streaming and non-streaming
  - `POST /api/generate` — single-prompt requests, streaming and non-streaming
  - `GET /api/tags` — model listing (proxies the upstream `GET /models`, with a configured fallback)
  - `POST /api/show` — synthesized model metadata (there's no real Modelfile for a cloud model)
  - `GET /` and `GET /api/version` — the basic liveness checks Ollama clients tend to probe
- Streams responses as NDJSON exactly like real Ollama, so streaming clients work unmodified.
- Translates Ollama's multimodal `images` (base64 image list) into OpenAI's `image_url` content-part format, including MIME-type sniffing (PNG/JPEG/GIF/WEBP), so vision requests get proxied too.
- Client-supplied model names are passed straight through to the upstream API — the proxy doesn't force a single model.
- Ships as a single self-contained `.exe` — no .NET runtime install required on the target machine.

## Configuration

Settings live under the `CloudApi` section of `src/OllamaCloudProxy/appsettings.json` (next to the published `.exe` as `appsettings.json`), and every key can be overridden with an environment variable instead of editing the file:

| Setting | Env var override | Purpose |
|---|---|---|
| `CloudApi:BaseUrl` | `CloudApi__BaseUrl` | Base URL of the OpenAI-compatible API, e.g. `https://api.openai.com/v1` or an Azure OpenAI `.../openai/v1` endpoint |
| `CloudApi:ApiKey` | `CloudApi__ApiKey` | Sent upstream as `Authorization: Bearer <ApiKey>` |
| `CloudApi:DefaultModel` | `CloudApi__DefaultModel` | Used when a client doesn't specify a model, and as the fallback for `/api/tags` |
| `CloudApi:Models` | `CloudApi__Models__0`, `CloudApi__Models__1`, ... | Fallback model list for `/api/tags` if the upstream `GET /models` call fails or isn't supported |

The listen address defaults to `http://127.0.0.1:11434` and can be overridden the standard ASP.NET Core way (an `"urls"` key in `appsettings.json`, an `ASPNETCORE_URLS`/`urls` environment variable, or `--urls` on the command line).

Example `appsettings.json`:

```json
{
  "CloudApi": {
    "BaseUrl": "https://api.openai.com/v1",
    "ApiKey": "sk-...",
    "DefaultModel": "gpt-4o-mini",
    "Models": []
  }
}
```

## Building and running

Requires the .NET 10 SDK (pinned via `global.json`).

Code is formatted with [CSharpier](https://csharpier.com/), installed as a local dotnet tool:

```
dotnet tool restore
dotnet csharpier format .
```

Publish a self-contained, single-file executable:

```
publish.cmd
```

This builds `artifacts\ollama-cloud-proxy.exe` (plus its `appsettings.json`) for `win-x64`. Edit `artifacts\appsettings.json` with your cloud endpoint's details, then run:

```
artifacts\ollama-cloud-proxy.exe
```

Point any Ollama-speaking tool at `http://127.0.0.1:11434` and it will behave as if talking to a local Ollama instance, while requests are actually served by your configured cloud model.

## Limitations

- `/api/embeddings` is not implemented.
- Ollama's `context` field (opaque token-context resume for `/api/generate`) has no OpenAI equivalent and is dropped — each request is treated as stateless.
- `/api/show`'s response is synthesized (format, architecture, etc. are placeholders), since that metadata doesn't exist for a cloud-hosted model.
