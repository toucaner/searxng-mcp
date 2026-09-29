#region MODULE_CONTRACT [DOMAIN(Entry): McpWebSearchService; CONCEPT(Bootstrap): Kestrel host + MCP streamable HTTP transport; TECH(ASP.NET Core): WebApplication]
/**
 * [GREP_SUMMARY]: Program, entrypoint, WebApplication, Kestrel, streamable-http, WithHttpTransport, MapMcp, healthz, DI wiring, options validation, ValidateOnStart
 * [STRUCTURE]: > CreateBuilder(args) -> o AddOptions<SearXNG/McpServer/CategoryInference>.Validate.ValidateOnStart
 *              -> + AddSingleton<CB> + AddHttpClient<SearXNGClient>(15s) + AddHttpClient("FetchExtract")(15s) + AddHttpClient<ICategoryInferenceClient>
 *              -> + AddSingleton<ISearchResultProcessor> + AddMcpServer().WithHttpTransport(30s).WithTools<WebSearchTools>()
 *              -> = Build() -> MapMcp("/mcp") + MapGet("/healthz") -> Run("http://0.0.0.0:{Port}") [long-lived service]
 *
 * <summary>
 * [PURPOSE]: Bootstrap the MCP server as a long-lived streamable-HTTP service on Kestrel (M10, ADR-033).
 * One `docker run -d -p 8080:8080 mcp-web-search` container serves any number of MCP clients over HTTP —
 * the server lifetime is decoupled from client launches (supersedes the M1–M9 stdio process-per-session design).
 * </summary>
 * <remarks>
 * [INVARIANTS]: All M2–M9 DI registrations are preserved verbatim (options with ValidateOnStart, CB singleton,
 * typed/named HttpClients, pipeline, tools). Kestrel binds to McpServerSettings.Port; the MCP endpoint is mapped
 * at McpServerSettings.Path. `public partial class Program { }` at file end makes the top-level-statements entry
 * point accessible to WebApplicationFactory&lt;Program&gt; in tests (ADR-033 §0.3).
 * [RATIONALE]: Q: Why JIT instead of Native AOT? A: The MCP streamable-HTTP transport (ModelContextProtocol.AspNetCore)
 * hosts Kestrel, which Native AOT does not support — M10 transitions the project to JIT (ADR-033 supersedes SPEC §5.1).
 * Q: Why a dedicated /healthz endpoint? A: The MCP /mcp endpoint is POST-only (GET → 405); Docker HEALTHCHECK needs
 * a lightweight GET probe without MCP protocol overhead (ADR-033 §0.6).
 * [CHANGES]: LAST_CHANGE: M10 — stdio→streamable HTTP rewrite (ADR-033): WebApplication + WithHttpTransport + MapMcp
 * + /healthz + `public partial class Program { }`; removed `using Microsoft.Extensions.Hosting;` (redundant under
 * Web SDK implicit usings); added MODULE_CONTRACT per csharp-conventions.
 * </remarks>
 */
#endregion MODULE_CONTRACT

using McpWebSearchService;
using McpWebSearchService.Configuration;
using McpWebSearchService.Services;
using ModelContextProtocol.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Sinks.Syslog;
using OpenTelemetry.Metrics;

// M10 (ADR-033): the MCP server now runs as a long-lived HTTP service (streamable HTTP transport on
// Kestrel) instead of a per-session stdio process. This decouples its lifetime from opencode/harness:
// a single `docker run -d -p <port>:<port> mcp-web-search` container serves any number of MCP clients
// that connect over HTTP. JIT (PublishAot=false) is required — the AspNetCore transport hosts Kestrel,
// which Native AOT does not support. Supersedes the earlier stdio + AOT design (ADR-001..032 recaps).

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();

// Wazuh syslog integration (UDP 514): forward Warning+ logs to the Wazuh manager.
// Host/port/appName are configurable via the WazuhLogging section (appsettings.json / env vars).
var wazuhHost = builder.Configuration["WazuhLogging:Host"] ?? "<ip>";
var wazuhPort = int.TryParse(builder.Configuration["WazuhLogging:Port"], out var p) ? p : 514;
var wazuhApp = builder.Configuration["WazuhLogging:AppName"] ?? "mcp-web-search";
var wazuhEnabled = !bool.TryParse(builder.Configuration["WazuhLogging:Enabled"], out var e) || e;

if (wazuhEnabled)
{
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .WriteTo.Console()
        .WriteTo.UdpSyslog(wazuhHost, wazuhPort, wazuhApp, SyslogFormat.RFC5424)
        .CreateLogger();
    builder.Host.UseSerilog();
    WazuhAudit.Configure(wazuhHost, wazuhPort, wazuhApp);
}

// M2: register strongly-typed options with startup validation (ADR-006 config binder source-gen).
builder.Services.AddOptions<SearXNGSettings>()
    .Bind(builder.Configuration.GetSection("SearXNG"))
    .Validate(s => !string.IsNullOrWhiteSpace(s.BaseUrl) && Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out _), "SearXNG.BaseUrl must be a non-empty absolute URI")
    .Validate(s => s.MaxResults > 0, "SearXNG.MaxResults must be > 0")
    .Validate(s => !string.IsNullOrWhiteSpace(s.DefaultLanguage), "SearXNG.DefaultLanguage must be non-empty")
    .ValidateOnStart();

builder.Services.AddOptions<McpServerSettings>()
    .Bind(builder.Configuration.GetSection("McpServer"))
    .Validate(s => !string.IsNullOrWhiteSpace(s.Name), "McpServer.Name must be non-empty")
    .Validate(s => !string.IsNullOrWhiteSpace(s.Version), "McpServer.Version must be non-empty")
    .Validate(s => s.Port > 0 && s.Port <= 65535, "McpServer.Port must be in (0, 65535]")
    .Validate(s => !string.IsNullOrWhiteSpace(s.Path), "McpServer.Path must be non-empty")
    .ValidateOnStart();

// M9 (ADR-032): local LLM category inference options.
builder.Services.AddOptions<CategoryInferenceSettings>()
    .Bind(builder.Configuration.GetSection("CategoryInference"))
    .Validate(s => !string.IsNullOrWhiteSpace(s.BaseUrl) && Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out _), "CategoryInference.BaseUrl must be a non-empty absolute URI")
    .Validate(s => !string.IsNullOrWhiteSpace(s.Model), "CategoryInference.Model must be non-empty")
    .Validate(s => s.TimeoutSeconds > 0, "CategoryInference.TimeoutSeconds must be > 0")
    .Validate(s => s.Categories is { Length: > 0 }, "CategoryInference.Categories must contain at least one category")
    .ValidateOnStart();

// ADR-017: CircuitBreakerState singleton — shared by SearXNGClient.
builder.Services.AddSingleton<CircuitBreakerState>();

// M4: Typed HttpClient for the SearXNG backend (ADR-008).
builder.Services.AddHttpClient<ISearXNGClient, SearXNGClient>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));

// ADR-022 (M6): Named HttpClient "FetchExtract" for fetch_and_extract.
builder.Services.AddHttpClient("FetchExtract")
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));

// M9 (ADR-032): Typed HttpClient for the local LLM category-inference client (per-call timeout injected internally).
builder.Services.AddHttpClient<ICategoryInferenceClient, LocalLlmCategoryInference>()
    .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

// M4/M6: post-processing pipeline + MCP tools.
builder.Services.AddSingleton<ISearchResultProcessor, SearchResultProcessor>();

// M10 (ADR-033): register the MCP server with the streamable HTTP transport instead of stdio.
// The HTTP endpoint is enabled + mapped below via MapMcp at the configured path.
// IdleTimeout: idle MCP sessions are closed after 30s of inactivity (HttpServerTransportOptions.IdleTimeout —
// verified API surface of ModelContextProtocol.AspNetCore 1.2.0; the plan's ConnectionRequestTimeout does not exist).
builder.Services
    .AddMcpServer()
    .WithHttpTransport(o => { o.IdleTimeout = TimeSpan.FromSeconds(30); o.Stateless = true; })
    .WithTools<WebSearchTools>();

// OpenTelemetry: metrics → Prometheus exporter on /metrics
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel")
        .AddPrometheusExporter());

var app = builder.Build();

// Map the MCP streamable-HTTP endpoint at the configured path (default "/mcp").
// ADR-033: this is what clients connect to over HTTP, e.g. http://host:<port>/mcp.
var mcpSettings = app.Services.GetRequiredService<IOptions<McpServerSettings>>().Value;
var mcpPath = mcpSettings.Path.StartsWith('/') ? mcpSettings.Path : "/" + mcpSettings.Path;

// Wazuh audit: log every request to the /mcp endpoint.
app.Use(async (context, next) =>
{
    await next();
    if (context.Request.Path.Equals(mcpPath, StringComparison.OrdinalIgnoreCase))
    {
        WazuhAudit.McpRequest(context.Request.Method, context.Response.StatusCode, context.Connection.RemoteIpAddress?.ToString() ?? "?");
    }
});

app.MapMcp(mcpPath);

// M10 (ADR-033 §0.6): dedicated lightweight health endpoint for Docker HEALTHCHECK.
// The MCP /mcp endpoint is POST-only (GET → 405), so the probe uses this minimal GET instead.
app.MapGet("/healthz", () =>
{
    WazuhAudit.Health("mcp-web-search", "OK");
    return Results.Ok(new { status = "healthy" });
});

// OpenTelemetry Prometheus metrics endpoint at /metrics
app.MapPrometheusScrapingEndpoint();

// Bind Kestrel to the configured port (McpServer__Port env var or McpServer section in appsettings.json).
app.Run($"http://0.0.0.0:{mcpSettings.Port}");

// Flush any buffered Serilog events on shutdown.
Log.CloseAndFlush();

// M10 (ADR-033 §0.3): makes the top-level-statements entry point public so the test project can use
// WebApplicationFactory<Program> (Microsoft.AspNetCore.Mvc.Testing) to boot this exact Program in an
// in-memory TestServer — no process spawn, no real port, reliable under `dotnet test`.
public partial class Program { }
