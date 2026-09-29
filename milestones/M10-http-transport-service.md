# M10 — MCP server as a standalone HTTP service (moving away from AOT)

**Stage:** M10
**Predecessors:** M1–M9 (the whole accumulated stack: config, DTO, HTTP client, pipeline, tools, docker, e2e, category-inference)
**Successors:** —
**Requirement source:** operator request — "MCP must be decoupled from opencode or another harness launches". The current scheme (stdio) tightly couples the lifetime of the MCP server to the lifetime of the client: the client starts the `docker run -i --rm` container, and the server dies on stdin EOF. A long-running service is required, to which clients connect over HTTP.

## Goal

Move `McpWebSearchService` from the **stdio transport** (process-per-session, managed by opencode) to the
**streamable HTTP transport** (Kestrel), running as a long-lived container service:

```
opencode/harness  --HTTP-->  mcp-web-search (docker run -d, :<port>)  --HTTP-->  SearXNG (<ip>:<port>)
```

One container serves an arbitrary number of MCP clients; its lifetime is not tied to client launches. The
HTTP transport requires ASP.NET Core (Kestrel), which requires **JIT** (Native AOT does not support this
stack). M10 therefore also moves the project away from AOT.

## Key facts already gathered (do not re-verify from scratch)

### 1. The MCP SDK's HTTP API confirmed by reflection

- The core SDK `ModelContextProtocol 1.2.0.0` does **NOT** contain an HTTP transport method. The available server-builder methods (`McpServerBuilderExtensions`) are `WithStdioServerTransport`, `WithStreamServerTransport(Stream, Stream)`. The `ModelContextProtocol.Server.StreamableHttpServerTransport` class exists but does not connect to Kestrel out of the box.
- The HTTP integration lives in a **separate package `ModelContextProtocol.AspNetCore`** (NuGet, stable version **1.2.0**, matching core 1.2.0). Reflection of the package gave the exact API:
  - `Microsoft.Extensions.DependencyInjection.HttpMcpServerBuilderExtensions.WithHttpTransport(IMcpServerBuilder, Action<HttpServerTransportOptions>)` — returns `IMcpServerBuilder`.
  - `Microsoft.AspNetCore.Builder.McpEndpointRouteBuilderExtensions.MapMcp(IEndpointRouteBuilder, string pattern)` — returns `IEndpointConventionBuilder`.
  - The package pulls in `Microsoft.AspNetCore.Http.Abstractions`, `.Routing`, `.Authentication`, `.Authorization`, etc. → **Kestrel/WebApplication is required**.
- `HttpServerTransportOptions` has a `ConnectionRequestTimeout` property (`TimeSpan`) — used in the `Program.cs` draft.

### 2. The stdout-capture problem on Windows (not a blocker, but factor it into tests)

- `docker run -i` on this Windows host + a PowerShell redirect **loses the container's stdout** (JSON-RPC responses do not reach the file with `> file`). It is proven the server works: stderr shows `initialize`/`tools/list`/`tools/call web_search` handlers completed, LDD `[IMP:9][10] WebSearch[DONE]: serialized 10 SearchResultDto`, `IsError = False`, exit 0.
- Reliable JSON-response capture was achieved via a **node probe** (node v24.20.0 is in PATH): `spawn('docker', ['run','-i','--rm', ...], {stdio:['pipe','pipe','pipe']})` + write JSON-RPC to `child.stdin`, read `child.stdout`. This is a working template for future HTTP/stdio smoke tests or manual verification. The probe file is `mcp_probe.cjs` (at the repo root, temporary — can be deleted).

### 3. The currently "stuck" container has already finished

- Container `af54fd42` (`hopeful_varahamihira`, image `mcp-web-search:latest`, 178MB, ID `15ef62aaf8c8`) — a stdio run that exited normally on stdin EOF and was removed by `--rm`. The image `mcp-web-search:latest` **exists** in the local Docker (an old AOT build) — when moving to HTTP it must be rebuilt (JIT + EXPOSE).

## Partially completed edits (working-tree state at the time of the stop)

> **IMPORTANT:** these edits are already in the code but are **NOT built and NOT verified** (no build/tests were run after them). The implementing agent must either finish the work and run `dotnet build`+`dotnet test`, or revert with `git checkout` and start clean. Nothing from M10 is committed.

1. `McpWebSearchService.csproj`:
   - SDK `Microsoft.NET.Sdk` → `Microsoft.NET.Sdk.Web`.
   - `PublishAot` → `false` (was `true`). ADR-033 comment added.
   - `InvariantGlobalization` left `true`; `EnableConfigurationBindingGenerator` left `true`.
   - Added `<PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.2.0" />`.
   - `PackageTags`/`Description` updated (stdio → http).
2. `Program.cs` — rewritten to `WebApplication.CreateBuilder` + `WithHttpTransport` + `MapMcp`:
   - All DI registrations kept (options with ValidateOnStart, `CircuitBreakerState`, `AddHttpClient<ISearXNGClient>`, `AddHttpClient("FetchExtract")`, `AddHttpClient<ICategoryInferenceClient>`, `SearchResultProcessor`).
   - `AddMcpServer().WithHttpTransport(o => o.ConnectionRequestTimeout = TimeSpan.FromSeconds(30)).WithTools<WebSearchTools>()`.
   - `app.MapMcp(mcpPath)` + `app.Run($"http://0.0.0.0:{mcpSettings.Port}")`.
   - `McpServerSettings` must now have `Port` and `Path` (otherwise `ValidateOnStart` fails).
3. `Configuration/McpServerSettings.cs` — added `Port` (default 8080) and `Path` (default "/mcp"), validation `Port > 0 && Port <= 65535`, `Path` non-empty.
4. `appsettings.json` — added `Port: 8080`, `Path: "/mcp"` to `McpServer`.

## Remaining work (M10.2 → M10.5)

### M10.2 — Transport (finish/verify)
- Confirm that `app.Run($"http://0.0.0.0:{port}")` is a valid way to set the port for `WebApplication` (alternatives: `builder.WebHost.UseUrls(...)` or `ASPNETCORE_URLS`). `Port` validation is already added in Program.cs.
- Run `dotnet build` and `dotnet test` — **mandatory after the edits** (the draft was never built).
- Possible `Program.cs` refinements: remove `using Microsoft.Extensions.Hosting;` if unnecessary; verify that `MapMcp`/`WithHttpTransport` resolve (namespaces `ModelContextProtocol.AspNetCore`, `Microsoft.AspNetCore.Builder`).

### M10.3 — Container/deploy
- `Dockerfile`: replace the AOT build stage (`mcr.microsoft.com/dotnet/sdk:10.0-aot` + `/p:PublishAot=true`) with a JIT build (`mcr.microsoft.com/dotnet/sdk:10.0` + `dotnet publish McpWebSearchService.csproj -c Release -r linux-x64 -o /app/publish`); the final stage becomes `mcr.microsoft.com/dotnet/aspnet:10.0` (or `dotnet/runtime` + the ASP.NET Core shared framework) instead of `debian:bookworm-slim` + manual `apt-get install`. Add `EXPOSE 8080` + `HEALTHCHECK` (an HTTP probe on `/mcp` or a dedicated health endpoint).
- Local run: `docker run -d -p 8080:8080 --name mcp-web-search mcp-web-search` — a long-lived service.

### M10.4 — Manifest/clients
- `.mcp/server.json`: `transport.type` → `http` (or `streamable-http`), URL `http://host:8080/mcp` instead of `docker run -i ...`. Clarify the actual MCP manifest syntax for the HTTP transport (the current file's `transport.command` field is stdio-specific).
- Verify that opencode/harness can connect to a streamable-HTTP MCP.

### M10.5 — Tests/documentation
- `tests/McpWebSearchService.Tests/StdioSmokeTests.cs` — **must be replaced**: it spawns `dotnet run` and sends JSON-RPC over stdio; after the move to HTTP, the server no longer reads stdin. Replace with an HTTP smoke test: start the process/container, POST JSON-RPC to `http://localhost:<port>/mcp` (streamable HTTP). Or move it to separate tests and not run it during a normal `dotnet test` (integration/operator tests).
- `IntegrationTests.HardConstraints_Verified` — the real asserts do not depend on AOT (types/attributes/JsonSerializerContext/no Newtonsoft/CB/pipeline/DTO) and remain valid; only update the doc comment (it currently mentions AOT).
- Documentation: a new **ADR-033** (HTTP transport + JIT, supersedes the AOT approach — the "AOT-safe" wordings of ADR-002/006/007/018 cease to be a hard constraint); update `AGENTS.md`, `DevelopmentPlan.md`, `tests/test_guide.md`, `tests/qa_report.md`. Cancel/replace the AOT constraint in SPEC §5.1 (JIT).

## Verification template (node probe) for the HTTP handshake

After building/starting the HTTP service, verify as follows (bypassing the PowerShell stdout-capture bug):

```js
// node probe: POST JSON-RPC to http://localhost:8080/mcp
// initialize -> tools/list -> tools/call web_search
```

Expected `initialize` result: `serverInfo: { name: "McpWebSearchService", version: "1.0.0.0" }`, `tools/list` → `web_search` + `fetch_and_extract`, `tools/call web_search("dotnet 10 native aot")` → an array of `SearchResultDto` (10 results) from the external SearXNG.

## Acceptance Criteria

- [ ] `McpWebSearchService.csproj`: SDK `Microsoft.NET.Sdk.Web`, `PublishAot=false`, `ModelContextProtocol.AspNetCore 1.2.0`.
- [ ] `Program.cs`: `WebApplication` + `WithHttpTransport` + `MapMcp`; port/path from config; all DI registrations preserved.
- [ ] `McpServerSettings`: `Port` + `Path` with validation; `appsettings.json` contains them.
- [ ] `dotnet build` + `dotnet test` — PASS (the M2–M9 regression is not broken; the stdio smoke is replaced/adapted).
- [ ] `Dockerfile`: JIT build + ASP.NET runtime base + `EXPOSE 8080` + `HEALTHCHECK`.
- [ ] `docker run -d -p 8080:8080 mcp-web-search` → a long-lived service; the HTTP handshake over `http://localhost:8080/mcp` is confirmed.
- [ ] `.mcp/server.json`: HTTP transport (url), not `docker run -i`.
- [ ] ADR-033 recorded; the AOT constraint in SPEC/AGENTS.md is cancelled/replaced; `qa_report.md` reflects M10.

## Risks

- **ASP.NET Core + MCP SDK version compatibility** — half-resolved: the `ModelContextProtocol.AspNetCore 1.2.0` package exists and matches core 1.2.0, but the actual build/runtime integration (`WithHttpTransport` + `MapMcp`) must be confirmed by the first `dotnet build` (the draft was never built).
- **Port/address from config** — `app.Run($"http://0.0.0.0:{port}")` — verify this works (or switch to `UseUrls`/`ASPNETCORE_URLS`).
- **Streamable HTTP vs SSE** — MCP clients (opencode/harness) must support the chosen mode (the package provides streamable HTTP). Confirm the manifest syntax and client compatibility.
- **Regression of the stdio path** — the old `StdioSmokeTests` will stop working; adapt/replace them, otherwise `dotnet test` fails.
- **AOT artifacts** — the `mcp-web-search:latest` image (old AOT) must be rebuilt; old ADRs marked "AOT-safe" — cancel them via the new ADR, do not edit in place.
- **GREEN TEST TRAP** — `@qa` must verify semantics (LDD `[IMP:9-10]`, HTTP handshake responses), not just green tests.
