# McpWebSearchService

A .NET 10 MCP server that gives AI agents **web search**: it proxies queries to a SearXNG instance,
post-processes the results, and infers the search category via a local LLM. It runs as a long-lived
Kestrel service.

## Architecture

```
MCP client ──POST /mcp──► McpWebSearchService (ASP.NET Core, Kestrel)
                              ├─ WebSearchTools ────────── 2 MCP tools
                              ├─ ISearXNGClient ────────── SearXNG (HTTP, 15 s + circuit breaker)
                              └─ ICategoryInferenceClient ── local LLM (OpenAI-compatible)
```

Stateless streamable-HTTP transport: one container serves any number of MCP clients. If SearXNG/LLM is
unavailable, `web_search` returns a clear error instead of hanging the agent. Endpoints: `POST /mcp`,
`GET /healthz`, `GET /metrics`.

## MCP tools

| Tool | Purpose |
|---|---|
| `web_search` | Search via SearXNG (`query`, optional `categories`/`timeRange`/`language`); returns post-processed results |
| `fetch_and_extract` | Fetch a URL and return cleaned plain text (≤ 5000 chars) |

`categories`: when omitted, inferred semantically from the query via the local LLM (falls back to `general`).

## Stack

- .NET 10 / ASP.NET Core Minimal API on Kestrel; `ModelContextProtocol` 1.2.0 (streamable HTTP)
- SearXNG backend via `IHttpClientFactory` (15 s timeout + circuit breaker)
- `System.Text.Json` source generation + configuration binding source generator
- OpenTelemetry (Prometheus `/metrics`), Serilog → UDP syslog (Wazuh)

## Build and run

```bash
dotnet build McpWebSearchService.slnx
dotnet run --project src/McpWebSearchService
```

Docker: `docker build -t mcp-web-search .` then `docker run -d -p 8080:8080 mcp-web-search`;
or `cp .env.example .env` (fill in values) then `docker compose up -d`.

Tests: `dotnet test McpWebSearchService.slnx`.

## Configuration

Settings come from `appsettings.json`, overridden by environment variables (`SearXNG:BaseUrl` → `SearXNG__BaseUrl`).
Key sections: `SearXNG`, `CategoryInference`, `McpServer`, `WazuhLogging`, `Otlp`.
Deployment values are supplied via `.env` (gitignored) or env vars.
