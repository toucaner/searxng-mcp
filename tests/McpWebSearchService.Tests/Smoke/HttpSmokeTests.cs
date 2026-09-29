#region MODULE_CONTRACT [DOMAIN(Test): HttpSmokeTests; CONCEPT(Smoke test): HTTP-level MCP streamable-transport verification]
/**
 * [GREP_SUMMARY]: HttpSmokeTests, xunit facts, WebApplicationFactory, TestServer, JSON-RPC initialize handshake, tools/list, streamable-http, Mcp-Session-Id, SSE data: parsing
 * [STRUCTURE]: > IClassFixture<WebApplicationFactory<Program>> -> o CreateClient() + Accept "application/json, text/event-stream"
 *              -> POST BuildJsonRpcRequest(initialize) to /mcp = SSE/JSON body + Mcp-Session-Id header
 *              -> POST BuildJsonRpcRequest(tools/list) w/ session-id = 2 tools { web_search, fetch_and_extract } w/ non-empty descriptions
 *
 * <summary>
 * [PURPOSE]: Smoke tests (M10 AC5) that verify the assembled application serves MCP over streamable HTTP:
 * the real Program.cs boots in an in-memory TestServer (WebApplicationFactory&lt;Program&gt;), and real JSON-RPC
 * requests (initialize, tools/list) are POSTed to /mcp and their real responses parsed — not just HTTP 200.
 * Replaces StdioSmokeTests (M8): the stdio transport no longer exists under ADR-033 (HTTP + JIT).
 * </summary>
 * <remarks>
 * [INVARIANTS]: One WebApplicationFactory per test class (IClassFixture) — the TestServer host is shared, but each
 * test creates its OWN MCP session (fresh initialize → fresh Mcp-Session-Id), so tests are independent.
 * The streamable HTTP response to a POST is either application/json (single JSON-RPC object) or
 * text/event-stream (SSE: "event: message" + "data:" lines); ExtractJsonRpcPayload handles both empirically
 * (verified against the running server: SDK 1.2.0 answers POSTs with SSE containing exactly one data: line).
 * [RATIONALE]: Q: Why WebApplicationFactory instead of spawning a process? A: Boots the REAL Program.cs (all DI,
 * options validation, MCP endpoint) in-process — no flaky process/port management, ms-per-test speed, works under
 * `dotnet test` on any dev machine (ADR-033 §0.3). Q: Why typed JSON-RPC builders instead of raw strings? A:
 * System.Text.Json serialization of anonymous objects keeps the wire format correct by construction and readable.
 * [CHANGES]: LAST_CHANGE: M10 — initial creation (ADR-033), replaces StdioSmokeTests.cs.
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace McpWebSearchService.Tests.Smoke;

#region CLASS_HttpSmokeTests [DOMAIN(Test): MCP streamable-HTTP smoke tests via in-memory TestServer]
/// <summary>
/// [PURPOSE]: Smoke tests for the assembled application's streamable HTTP transport (M10 AC5).
/// Verify that the server responds to JSON-RPC initialize + tools/list over POST /mcp with valid,
/// semantically-correct responses (protocolVersion, serverInfo, exactly two tools with descriptions).
/// </summary>
public sealed class HttpSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    #region FIELDS [DOMAIN(Test): HTTP client bound to the in-memory TestServer]
    /// <summary>HTTP client whose base address is the in-memory TestServer origin (no real port).</summary>
    private readonly HttpClient _client;

    /// <summary>MCP streamable-HTTP endpoint path (McpServerSettings.Path default, ADR-033).</summary>
    private const string McpPath = "/mcp";
    #endregion FIELDS

    #region METHOD_Ctor [TECH(Mvc.Testing): WebApplicationFactory<Program>]
    /// <summary>
    /// [PURPOSE]: Create the shared HTTP client for the in-memory TestServer and declare the MCP
    /// streamable-HTTP Accept header (both application/json and text/event-stream are valid responses).
    /// </summary>
    /// <param name="factory">Shared WebApplicationFactory — boots the real Program.cs once per test class.</param>
    public HttpSmokeTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
    }
    #endregion METHOD_Ctor

    #region TEST_METHOD_InitializeHandshake [IMP:8][HttpSmokeTests] AC5a — JSON-RPC initialize over HTTP succeeds with protocolVersion + serverInfo (stateless)
    /// <summary>
    /// Verifies that POSTing a JSON-RPC "initialize" request to /mcp returns a valid JSON-RPC 2.0 response
    /// (result.protocolVersion + result.serverInfo non-empty). In stateless mode (M11), no Mcp-Session-Id
    /// header is returned — subsequent requests do not require a session id.
    /// </summary>
    [Fact]
    public async Task Initialize_Handshake_Succeeds()
    {
        // Arrange: typed JSON-RPC initialize request (MCP protocol requires this first).
        var requestBody = BuildJsonRpcRequest(
            id: 1,
            method: "initialize",
            @params: new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "http-smoke-test", version = "1.0" }
            });

        // Act: POST to the MCP streamable-HTTP endpoint.
        var (payload, sessionId) = await PostMcpAsync(requestBody, sessionId: null);

        // Assert: valid JSON-RPC 2.0 response with result.protocolVersion + result.serverInfo.
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.True(root.TryGetProperty("result", out var result), "initialize response must contain 'result' (not 'error').");

        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("protocolVersion").GetString()),
            "result.protocolVersion must be non-empty.");

        var serverInfo = result.GetProperty("serverInfo");
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("name").GetString()),
            "result.serverInfo.name must be non-empty.");
        Assert.False(string.IsNullOrWhiteSpace(serverInfo.GetProperty("version").GetString()),
            "result.serverInfo.version must be non-empty.");

        // M11 (stateless): Mcp-Session-Id is optional — no assertion on sessionId.
    }
    #endregion TEST_METHOD_InitializeHandshake

    #region TEST_METHOD_ToolsList [IMP:8][HttpSmokeTests] AC5b — tools/list returns exactly two tools with non-empty descriptions (ADR-028)
    /// <summary>
    /// Verifies that "tools/list" (sent without a session id in stateless mode) returns exactly
    /// two tools — web_search and fetch_and_extract — each with a non-empty description (ADR-028).
    /// </summary>
    [Fact]
    public async Task ToolsList_ReturnsTwoToolsWithDescriptions()
    {
        // M11 (stateless): no session id needed — send tools/list directly.
        var listBody = BuildJsonRpcRequest(id: 1, method: "tools/list", @params: new { });
        var (payload, _) = await PostMcpAsync(listBody, sessionId: null);

        // Assert: exactly two tools with the SPEC §2 names and non-empty descriptions.
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.True(root.TryGetProperty("result", out var result), "tools/list response must contain 'result' (not 'error').");

        var tools = result.GetProperty("tools");
        Assert.True(tools.GetArrayLength() == 2, "Expected exactly 2 tools (web_search + fetch_and_extract).");

        var names = tools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "fetch_and_extract", "web_search" }, names);

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString() ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()),
                $"Tool '{name}' has an empty description (ADR-028 requires non-empty).");
        }
    }
    #endregion TEST_METHOD_ToolsList

    #region METHOD_PostMcpAsync [TECH(HttpClient): JSON-RPC over streamable HTTP]
    /// <summary>
    /// [PURPOSE]: POST a JSON-RPC body to the MCP endpoint and return the extracted JSON-RPC payload
    /// (SSE data: lines collapsed, or plain JSON) plus the Mcp-Session-Id response header.
    /// </summary>
    /// <param name="jsonBody">Serialized JSON-RPC request object.</param>
    /// <param name="sessionId">Mcp-Session-Id from a prior initialize (null for the first request).</param>
    /// <returns>(JSON-RPC payload string, session id header value or null).</returns>
    private async Task<(string Payload, string? SessionId)> PostMcpAsync(string jsonBody, string? sessionId)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, McpPath) { Content = content };
        if (!string.IsNullOrEmpty(sessionId))
            request.Headers.Add("Mcp-Session-Id", sessionId);

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        string? headerSessionId;
        response.Headers.TryGetValues("Mcp-Session-Id", out var values);
        headerSessionId = values?.FirstOrDefault();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"MCP endpoint returned {(int)response.StatusCode} for {request.Method}: {body}");

        return (ExtractJsonRpcPayload(body), headerSessionId);
    }
    #endregion METHOD_PostMcpAsync

    #region METHOD_BuildJsonRpcRequest [TECH(System.Text.Json): typed JSON-RPC builder]
    /// <summary>
    /// [PURPOSE]: Build a JSON-RPC 2.0 request object via System.Text.Json (typed anonymous object — no raw
    /// string construction). The <c>params</c> member is serialized as an empty object when null, per the
    /// MCP examples for tools/list-style calls.
    /// </summary>
    /// <param name="id">JSON-RPC request id (must match the response id).</param>
    /// <param name="method">JSON-RPC method (e.g. "initialize", "tools/list").</param>
    /// <param name="@params">Method parameters (serialized as-is; empty object when null).</param>
    /// <returns>Serialized JSON-RPC 2.0 request string.</returns>
    private static string BuildJsonRpcRequest(int id, string method, object? @params) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = @params ?? new { }
        });
    #endregion METHOD_BuildJsonRpcRequest

    #region METHOD_ExtractJsonRpcPayload [TECH(SSE): streamable-HTTP response decoding]
    /// <summary>
    /// [PURPOSE]: Extract the JSON-RPC payload from a streamable HTTP response body. The MCP SDK may answer
    /// a POST with application/json (a single JSON object) or text/event-stream (SSE: "event:" lines plus
    /// "data:" lines carrying the JSON). Per the SSE spec, multiple data: lines of one event are joined with
    /// newlines; in practice SDK 1.2.0 emits exactly one data: line per response (verified empirically).
    /// </summary>
    /// <param name="body">Raw response body (JSON or SSE).</param>
    /// <returns>The JSON-RPC payload as a single parseable string.</returns>
    private static string ExtractJsonRpcPayload(string body)
    {
        var trimmed = body.Trim();

        var isSse = trimmed.StartsWith("event:", StringComparison.Ordinal)
                    || trimmed.Contains("data:", StringComparison.Ordinal);
        if (!isSse)
            return trimmed; // plain application/json response.

        var dataLines = trimmed
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].TrimStart());

        return string.Join("\n", dataLines);
    }
    #endregion METHOD_ExtractJsonRpcPayload
}
#endregion CLASS_HttpSmokeTests
