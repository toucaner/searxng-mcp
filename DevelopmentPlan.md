# Development Plan — M8: E2E testing and acceptance

**Stage:** M8 (End-to-End testing + final acceptance)
**Predecessors:** M2 (Configuration — complete), M3 (DTO + Serialization — complete), M4 (HTTP Client — complete), M5 (Post-processing Pipeline — complete), M6 (MCP Tools Integration — complete), M7 (Dockerization + AOT publish — complete)
**Successors:** — (FINAL milestone)
**Requirement source:** `SPEC.md` §2 (tools), §4.2 (timeout/CB), §6.1 step 8 (testing); `milestones/M8-e2e-testing-acceptance.md`; `AGENTS.md` (ADR-001..ADR-028, Role Boundary Contract, hard technical constraints).

**PURPOSE:**
Final verification of the entire system as a whole. M2–M6 covered the components with unit tests in isolation (110 tests, manual mocks). M8 adds **integration/E2E tests** on top of the assembled system: the full chain `WebSearchTools → SearXNGClient → CircuitBreaker → HttpClient(mock) → SearXNGResult → SearchResultProcessor → SearchResultDto → JSON string` with real DI wiring and a **capturing logger** to verify LDD `[IMP:7-10]` markers (GREEN TEST TRAP prevention). Additionally: 1–2 stdio smoke tests that verify real process startup + JSON-RPC handshake + tools/list. Docker E2E is operator-gated (DEFERRED, like M7 AC7/AC8). `@code` implements the E2E tests + `tests/test_guide.md`; `@qa` performs the final acceptance (read-only, veto).

---

## 0. Context and architectural decisions

### 0.1. Carrying over active ADRs (ADR-001..ADR-025 — one line each)

- **ADR-001:** `RuntimeIdentifiers` (plural). M8 does not modify the csproj. Production RID — `linux-x64` in Docker (operator-gated).
- **ADR-002:** Local AOT verification — `win-x64` proxy. M8 re-verifies AOT-clean after the possible addition of `[Description]` to the tool methods (ADR-028).
- **ADR-003:** `InvariantGlobalization=true` is retained. M8 has no effect.
- **ADR-004:** The binary name `McpWebSearchService` is fixed. The M8 stdio smoke test uses `dotnet run` (not the binary directly).
- **ADR-005:** Solution — `.slnx`. M8 does not add a project (tests are added to the existing `McpWebSearchService.Tests`).
- **ADR-006:** `EnableConfigurationBindingGenerator=true` + `Hosting 10.0.9`. M8 has no effect.
- **ADR-007:** Manual Circuit Breaker (no Polly). The M8 integration tests verify the CB lifecycle through the full chain.
- **ADR-008:** `Microsoft.Extensions.Http` 10.0.9. M8 integration tests use `AddHttpClient<ISearXNGClient, SearXNGClient>().ConfigurePrimaryHttpMessageHandler(mockHandler)`.
- **ADR-009:** Fuzzy comparison — Jaccard (M5). M8 integration tests verify the pipeline through the real `SearchResultProcessor`.
- **ADR-010:** Engine priority — hardcoded array (M5). M8 integration tests verify the pipeline end-to-end.
- **ADR-011:** Tracking params — prefix check (M5). M8 has no direct effect.
- **ADR-012:** StripHtml — `[GeneratedRegex]` (M5). M8 has no effect.
- **ADR-013:** Truncation — `MaxSnippetLength = 300` (M5). M8 has no effect.
- **ADR-014:** Pipeline composition — DTO mapping in the middle (M5). M8 integration tests verify the pipeline order observably.
- **ADR-015:** Thread safety — stateless singleton (M5). M8 has no effect.
- **ADR-016:** `TitleSimilarity` — a separate `internal static class` (M5). M8 has no effect.
- **ADR-017:** `CircuitBreakerState` DI — `AddSingleton<CircuitBreakerState>()` (M6). M8 integration tests resolve it from the real DI container.
- **ADR-018:** `web_search` return — pre-serialized JSON `string` (M6). M8 integration tests deserialize the JSON string to verify content.
- **ADR-019:** Tool name derivation — SDK `SnakeCaseLower` (M6). The M8 stdio smoke test verifies `web_search` + `fetch_and_extract` in tools/list.
- **ADR-020:** Parameter names — C# idiomatic `timeRange` (M6). The M8 stdio smoke test verifies the param names in the tools/list schema.
- **ADR-021:** `language` default — `null` param → config fallback (M6). The M8 integration test verifies the config fallback.
- **ADR-022:** `fetch_and_extract` — separate `HttpClient` + `HtmlTextExtractor` (M6). The M8 integration test uses a mock handler for the "FetchExtract" named client.
- **ADR-023:** Runtime dependencies — full runtime-deps set (M7). M8 Docker E2E (DEFERRED) verifies in the container.
- **ADR-024:** Dockerfile — `debian:bookworm-slim` runtime base (M7). M8 Docker E2E (DEFERRED).
- **ADR-025:** `.dockerignore` — exclude tests/, .opencode/, build artifacts (M7). M8 has no effect.

### 0.2. ADR-026: E2E testing approach — DI-integration primary (AC1–AC4) + stdio-smoke secondary (AC5)

- **Status:** Active (M8)
- **Context:** Milestone M8 requires E2E/integration tests on top of the assembled system. Two viable approaches (from the orchestrator context):
  - **(A) stdio-based:** spawn `dotnet run` as a child process, send JSON-RPC over stdin/stdout. Pro: maximum fidelity (tests REAL transport + REAL host startup + REAL DI wiring). Con: process management, stdio parsing, protocol handshake overhead, harder mock injection (need a real HTTP mock server on a port, not just a DelegatingHandler). The orchestrator confirmed feasibility (initialize handshake + tools/list work).
  - **(B) DI-integration:** build the full DI container (replicate Program.cs wiring with a mock HttpMessageHandler registered via `ConfigurePrimaryHttpMessageHandler`), resolve `WebSearchTools` from DI, call methods directly. Pro: fast, reliable, easy mock injection, tests real DI wiring + real pipeline. Con: doesn't test stdio transport / process startup.
- **Decision:** Do BOTH, weighted differently:
  1. **(B) DI-integration tests** (primary, AC1–AC4): build a real `ServiceCollection` replicating the `Program.cs` DI wiring (Options, CircuitBreakerState singleton, `AddHttpClient<ISearXNGClient, SearXNGClient>` with `ConfigurePrimaryHttpMessageHandler(mockHandler)`, `AddHttpClient("FetchExtract")` with a mock handler, `AddSingleton<ISearchResultProcessor, SearchResultProcessor>`, `AddSingleton<WebSearchTools>`). Resolve `WebSearchTools` from DI. Call `WebSearch()` / `FetchAndExtract()` directly. Mock SearXNG via a `DelegatingHandler` (same pattern as `SearXNGClientTests.MockHttpMessageHandler`). This tests the FULL integration chain: `WebSearchTools → SearXNGClient → CircuitBreaker → HttpClient(mock) → SearXNGResult → SearchResultProcessor → SearchResultDto → JSON string`. Use a **capturing logger** (see ADR-027) to verify `[IMP:7-10]` markers.
  2. **(A) stdio-smoke tests** (secondary, AC5): spawn `dotnet run --no-build` as a child process with `ProcessStartInfo.RedirectStandardInput/Output/Error = true`. Send JSON-RPC `initialize` → `tools/list` → (optionally) `tools/call web_search`. Parse JSON-RPC responses from stdout. Verify: (a) process starts within 10s, (b) `initialize` returns `protocolVersion` + `serverInfo`, (c) `tools/list` returns exactly 2 tools (`web_search`, `fetch_and_extract`) with correct param names. 2 smoke tests max — keep it lean. SearXNG mock for stdio smoke = NOT needed (we only test process startup + handshake + tools/list, not actual search — search is covered by DI-integration tests). If `tools/call` is tested via stdio, the real SearXNG BaseUrl in `appsettings.json` is used (may fail — acceptable, we test the error-handling path: "Search service temporarily unavailable" or real results depending on the network). Preferred: stdio smoke tests do NOT call `tools/call` — only `initialize` + `tools/list`.
- **Rationale:**
  - DI-integration tests cover the milestone's core ACs (AC1–AC4) reliably and fast (~ms per test, no process-spawn overhead). They test REAL DI wiring, the REAL pipeline (SearchResultProcessor, not mocked), the REAL CircuitBreaker, REAL JSON serialization. The ONLY mock is at the HTTP boundary (DelegatingHandler) — exactly the right isolation point.
  - stdio-smoke tests cover transport + process startup (AC5) — already proven feasible by the orchestrator. 2 tests only: process-starts-and-initializes, tools-list-correct. This satisfies the milestone's "E2E" intent without making the suite fragile (stdio parsing is brittle; keeping it to 2 smoke tests limits the blast radius).
  - Combining both: the full topology `LLM Agent <-> MCP Service (stdio) <-> SearXNG (HTTP)` is tested — stdio-smoke covers the left arrow, DI-integration covers the right arrow + the internal pipeline.
- **Consequence:**
  - Two test files: `tests/McpWebSearchService.Tests/Integration/IntegrationTests.cs` (DI-integration, AC1–AC4) + `tests/McpWebSearchService.Tests/StdioSmokeTests.cs` (stdio smoke, AC5).
  - `IntegrationTests.cs` needs a `TestWebHostFactory` helper (builds the DI container with mock handlers) + `CapturingLogger` (ADR-027).
  - `StdioSmokeTests.cs` needs a `McpProcessFixture` (IAsyncLifetime) that spawns `dotnet run --no-build` and manages the stdin/stdout streams.
  - The test project csproj may need `Microsoft.Extensions.Http` added (for the `ConfigurePrimaryHttpMessageHandler` extension) — check whether it is already transitively available via the main project reference.
- **Verify:** `dotnet test` runs both integration + stdio-smoke + the existing 110 unit tests. All pass. Integration tests complete in <5s total. Stdio smoke tests complete in <15s total (process spawn + handshake).

### 0.3. ADR-027: Capturing logger — `CapturingLogger<T>` for LDD `[IMP:7-10]` verification

- **Status:** Active (M8)
- **Context:** M8 AC requires `[IMP:7-10]` LDD markers present in the logs on all key paths (search, unavailability, timeout). Existing unit tests use `NullLogger` / `TestLogger` (no-op) — they verify OUTPUT but not LOG CONTENT. This is the GREEN TEST TRAP (mode-debug): 100% green tests without semantic verification. `@qa` must verify the logs match the contracts, not just that the tests are green.
- **Decision:** Create `tests/McpWebSearchService.Tests/Infrastructure/CapturingLogger.cs` — `internal sealed class CapturingLogger<T> : ILogger<T>`. Captures all log entries into a `List<LogEntry>` (thread-safe via `lock`). `LogEntry` record: `{ LogLevel Level, string Message, Exception? Exception }`. `IsEnabled` returns `true` for all levels (capture everything). The `formatter` callback produces the formatted message string (same as production logging). Integration tests inject `CapturingLogger<WebSearchTools>` / `CapturingLogger<SearXNGClient>` / `CapturingLogger<SearchResultProcessor>` into the DI container (replacing the default `ILogger<T>`) and then assert on captured log entries: `Assert.Contains(capturedLogs, e => e.Message.Contains("[IMP:9]") && e.Message.Contains("WebSearch"))`.
- **Rationale:**
  - `NullLogger` suppresses all logs → cannot verify LDD markers → GREEN TEST TRAP.
  - `TestLogger` (existing, no-op `Log` method) → same problem.
  - A capturing logger lets `@qa` verify that the code ACTUALLY logged the belief-state markers on the expected paths. This is the semantic trace verification that mode-debug/mode-qa require.
  - A `lock`-based `List<LogEntry>` is sufficient — integration tests are sequential within a test method (no concurrent log writes from multiple threads in the test's synchronous path).
- **Consequence:**
  - New file: `tests/McpWebSearchService.Tests/Infrastructure/CapturingLogger.cs` — `internal sealed class CapturingLogger<T> : ILogger<T>` + `internal record LogEntry(LogLevel Level, string Message, Exception? Exception)`.
  - `CapturingLogger` is injected into DI via `services.AddSingleton<ILogger<WebSearchTools>>(capturingLogger)` etc. before resolving `WebSearchTools`.
  - Integration tests assert on log content: `[IMP:9]` on the success path, `[IMP:7]` on the unavailability path, `[IMP:9]` on the CB-open path.
  - `csharp-conventions` apply: `#region`, XML docs, GREP_SUMMARY, STRUCTURE.
- **Verify:** Integration tests contain `Assert.Contains(capturedLogs, e => e.Message.Contains("[IMP:9]"))` style assertions. `@qa` verifies these assertions exist and pass.

### 0.4. ADR-028: Tool descriptions — add `[Description]` to `WebSearch` and `FetchAndExtract` methods

- **Status:** Active (M8)
- **Context:** The orchestrator code review found that the `web_search` and `fetch_and_extract` tool descriptions are EMPTY (`"description":""` in the tools/list response). SPEC §2.1/§2.2 define the LLM-facing descriptions:
  - `web_search`: "Searches the internet for current information. Use it when you need data not in your knowledge base, or when you need to check the latest news."
  - `fetch_and_extract`: "Loads the content of a web page by URL and returns cleaned text. Use it when the search snippet is insufficient."
  - Currently, `[Description]` attributes exist on PARAMETERS (categories, timeRange, language) but NOT on the METHODS themselves. The SDK generates the tool description from `[McpServerTool(Description = "...")]` OR potentially from a method-level `[Description]`. Need to verify which attribute the SDK reads for the tool-level description.
- **Decision:** Add a `[Description("...")]` attribute (from `System.ComponentModel`) to both the `WebSearch` and `FetchAndExtract` methods, with the SPEC §2.1/§2.2 descriptions. If the MCP SDK reads `[McpServerTool(Description = "...")]` instead, use that. The `@code` agent will verify which attribute produces the non-empty description in `tools/list` (the stdio-smoke test AC5 verifies this). This is a MINOR fix to `Services/WebSearchTools.cs` — 2 attribute additions, no logic change. AOT safety is preserved (`[Description]` is a plain attribute, no reflection at runtime — the SDK reads it at tool-registration time via source-generated code).
- **Rationale:**
  - SPEC §2.1/§2.2 explicitly define the LLM-facing descriptions. Empty descriptions reduce LLM tool-selection accuracy.
  - The fix is trivial (2 attributes) and AOT-safe.
  - The M8 stdio-smoke test (AC5) verifies non-empty descriptions in `tools/list` — this creates a test that prevents regression.
- **Consequence:**
  - `Services/WebSearchTools.cs` modified: 2 `[Description]` (or `[McpServerTool(Description = ...)]`) attributes added.
  - `dotnet publish -c Release -r win-x64 /p:PublishAot=true` re-verified — 0 × `IL####` (attributes are AOT-safe).
  - The stdio-smoke test verifies the `tools/list` response contains a non-empty `description` for both tools.
- **Verify:** `dotnet run` → `tools/list` → both tools have a non-empty `description` matching SPEC §2.1/§2.2. `dotnet publish -c Release -r win-x64 /p:PublishAot=true` → 0 × `IL####`.

### 0.5. Timeout test strategy — short HttpClient.Timeout + mock handler delay (ADR from the milestone risk)

- **Decision:** The timeout integration test uses a SHORT `HttpClient.Timeout` (e.g. 500ms) configured on the mock-backed HttpClient, with a mock handler that introduces `Task.Delay(2000)`. This triggers the timeout path in `SearXNGClient` (TaskCanceledException → SearXNGUnavailableException) without waiting 15 real seconds. The production 15s timeout is unit-tested in `SearXNGClientTests` (M4, already exists). The integration test verifies the FULL CHAIN behavior on timeout: `SearXNGClient → SearXNGUnavailableException → WebSearchTools → "Search service temporarily unavailable"` string + `[IMP:7]` log marker.
- **Rationale:** A real 15s delay makes the suite slow (CI + local dev friction). The timeout LOGIC is already unit-tested; the integration test verifies the ERROR PROPAGATION chain, not the exact 15s duration. A short timeout (500ms) + delay (2000ms) is sufficient.
- **Consequence:** The integration test `TimeoutHandling` completes in <3s. No `[Trait("Category","Slow")]` needed.

### 0.6. Test project csproj — check for the `Microsoft.Extensions.Http` dependency

- **Decision:** The test project (`McpWebSearchService.Tests.csproj`) references the main project (`src/McpWebSearchService/McpWebSearchService.csproj`), which has `Microsoft.Extensions.Http` 10.0.9 (ADR-008). The extension method `ConfigurePrimaryHttpMessageHandler` comes from `Microsoft.Extensions.Http`. Since the test project has a `ProjectReference` to the main project, `Microsoft.Extensions.Http` is transitively available. NO new PackageReference is needed in the test csproj.
- **Rationale:** A `ProjectReference` provides transitive package availability for compilation. `ConfigurePrimaryHttpMessageHandler` is an extension on `IHttpClientBuilder` from `Microsoft.Extensions.Http.DependencyInjectionExtensions`.
- **Consequence:** No csproj change for the test project. If compilation fails (extension not found), add `<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.9" />` as a fallback.

---

## 1. Draft Code Graph

```xml
<DraftCodeGraph>
  <!-- M8 Test File 1: Capturing Logger (GREEN TEST TRAP prevention) -->
  <Tests_CapturingLogger_cs FILE="tests/McpWebSearchService.Tests/Infrastructure/CapturingLogger.cs" TYPE="TEST_FIXTURE" MILESTONE="M8">
    <annotation>
      Capturing logger for LDD [IMP:7-10] semantic verification (ADR-027). internal sealed class CapturingLogger&lt;T&gt; : ILogger&lt;T&gt;.
      Captures all log entries into thread-safe List&lt;LogEntry&gt;. LogEntry record: { LogLevel, Message, Exception? }.
      Injected into DI container replacing default ILogger&lt;T&gt;. Integration tests assert on captured log content.
    </annotation>
    <keywords>test, capturing logger, ldd, imp, semantic verification, green test trap</keywords>

    <CapturingLogger_CLASS NAME="CapturingLogger" TYPE="TEST_FIXTURE_CLASS">
      <annotation>internal sealed class. Generic&lt;T&gt;. Thread-safe via lock.</annotation>

      <CapturingLogger_LogEntries_PROPERTY NAME="LogEntries" TYPE="PROPERTY" DATATYPE="IReadOnlyList&lt;LogEntry&gt;">
        <annotation>Read-only access to captured log entries for assertion.</annotation>
      </CapturingLogger_LogEntries_PROPERTY>

      <CapturingLogger_Log_METHOD NAME="Log" TYPE="IS_METHOD_OF_CLASS">
        <annotation>Captures formatted message + exception into List&lt;LogEntry&gt; under lock.</annotation>
      </CapturingLogger_Log_METHOD>

      <CapturingLogger_IsEnabled_METHOD NAME="IsEnabled" TYPE="IS_METHOD_OF_CLASS">
        <annotation>Returns true for all LogLevel values — capture everything.</annotation>
      </CapturingLogger_IsEnabled_METHOD>
    </CapturingLogger_CLASS>

    <LogEntry_RECORD NAME="LogEntry" TYPE="TEST_RECORD">
      <annotation>internal record LogEntry(LogLevel Level, string Message, Exception? Exception).</annotation>
    </LogEntry_RECORD>
  </Tests_CapturingLogger_cs>

  <!-- M8 Test File 2: Integration Tests (DI-based, AC1-AC4) -->
  <Tests_IntegrationTests_cs FILE="tests/McpWebSearchService.Tests/Integration/IntegrationTests.cs" TYPE="INTEGRATION_TEST" MILESTONE="M8">
    <annotation>
      DI-integration tests (ADR-026 approach B). Builds full ServiceCollection replicating Program.cs DI wiring
      with mock HttpMessageHandler at the HTTP boundary. Resolves WebSearchTools from DI. Tests the FULL chain:
      WebSearchTools → SearXNGClient → CircuitBreaker → HttpClient(mock) → SearXNGResult → SearchResultProcessor → SearchResultDto → JSON string.
      Uses CapturingLogger (ADR-027) to verify [IMP:7-10] LDD markers on key paths.
      Covers AC1 (WebSearch_ReturnsResults_WithPipeline), AC2 (WebSearch_SearXNGUnavailable_RussianError),
      AC3 (FetchAndExtract_ReturnsText), AC4 (TimeoutHandling), AC6 (SearXNGMock scenarios),
      AC8 (LDD [IMP:7-10] markers), AC10 (hard constraints).
    </annotation>
    <keywords>test, integration, di, websearchtools, searxngclient, circuitbreaker, pipeline, capturing logger, ldd</keywords>

    <IntegrationTests_CLASS NAME="IntegrationTests" TYPE="TEST_CLASS">
      <annotation>public class. IDisposable (dispose ServiceProvider per test). Uses TestHostFactory helper.</annotation>

      <IntegrationTests_WebSearch_ReturnsResults_WithPipeline_METHOD NAME="WebSearch_ReturnsResults_WithPipeline" TYPE="IS_METHOD_OF_CLASS">
        <annotation>AC1: Mock SearXNG returns 5 raw results (with duplicates, HTML snippets, blocked domains). WebSearch returns JSON string. Deserialize → verify pipeline applied: dedup reduced count, HTML stripped, snippets ≤300, blocked domains removed, Take(MaxResults). Verify [IMP:9] log marker present.</annotation>
        <CrossLinks>
          <Link TARGET="Tests_CapturingLogger_cs" TYPE="USES_FIXTURE" />
          <Link TARGET="Tools_WebSearchTools_cs_WebSearchTools_WebSearch_METHOD" TYPE="CALLS_METHOD" />
        </CrossLinks>
      </IntegrationTests_WebSearch_ReturnsResults_WithPipeline_METHOD>

      <IntegrationTests_WebSearch_SearXNGUnavailable_RussianError_METHOD NAME="WebSearch_SearXNGUnavailable_RussianError" TYPE="IS_METHOD_OF_CLASS">
        <annotation>AC2: Mock SearXNG returns HTTP 500. Trigger 3 failures → CircuitBreaker OPEN. 4th call → SearXNGUnavailableException → WebSearch returns JSON-serialized "Search service temporarily unavailable". Verify [IMP:7] log marker present on unavailability path.</annotation>
        <CrossLinks>
          <Link TARGET="Tests_CapturingLogger_cs" TYPE="USES_FIXTURE" />
          <Link TARGET="Tools_WebSearchTools_cs_WebSearchTools_WebSearch_METHOD" TYPE="CALLS_METHOD" />
        </CrossLinks>
      </IntegrationTests_WebSearch_SearXNGUnavailable_RussianError_METHOD>

      <IntegrationTests_FetchAndExtract_ReturnsText_METHOD NAME="FetchAndExtract_ReturnsText" TYPE="IS_METHOD_OF_CLASS">
        <annotation>AC3: Mock "FetchExtract" HttpClient returns HTML page. FetchAndExtract returns plain text ≤5000 chars. Verify HTML stripped, entities decoded, truncated with ellipsis. Verify [IMP:8][IMP:9] log markers present.</annotation>
        <CrossLinks>
          <Link TARGET="Tests_CapturingLogger_cs" TYPE="USES_FIXTURE" />
          <Link TARGET="Tools_WebSearchTools_cs_WebSearchTools_FetchAndExtract_METHOD" TYPE="CALLS_METHOD" />
        </CrossLinks>
      </IntegrationTests_FetchAndExtract_ReturnsText_METHOD>

      <IntegrationTests_TimeoutHandling_METHOD NAME="TimeoutHandling" TYPE="IS_METHOD_OF_CLASS">
        <annotation>AC4: Mock SearXNG handler delays 2000ms. HttpClient.Timeout = 500ms. WebSearch → SearXNGUnavailableException → "Search service temporarily unavailable" string. No hang. Completes <3s. Verify [IMP:9] log marker present on timeout path.</annotation>
        <CrossLinks>
          <Link TARGET="Tests_CapturingLogger_cs" TYPE="USES_FIXTURE" />
          <Link TARGET="Tools_WebSearchTools_cs_WebSearchTools_WebSearch_METHOD" TYPE="CALLS_METHOD" />
        </CrossLinks>
      </IntegrationTests_TimeoutHandling_METHOD>

      <IntegrationTests_SearXNGMock_Scenarios_METHOD NAME="SearXNGMock_CoversAllScenarios" TYPE="IS_METHOD_OF_CLASS">
        <annotation>AC6: [Theory] — mock returns: success with results, empty results, delay (timeout), 500, connection refused. Each scenario produces correct WebSearch response. Verifies mock covers all SearXNGMock scenarios from milestone.</annotation>
      </IntegrationTests_SearXNGMock_Scenarios_METHOD>

      <IntegrationTests_LDDMarkers_AllKeyPaths_METHOD NAME="LDDMarkers_AllKeyPaths" TYPE="IS_METHOD_OF_CLASS">
        <annotation>AC8: Verify [IMP:7-10] markers present in captured logs on: search success, search unavailability, timeout, fetch_and_extract success. Semantic verification — not just green tests.</annotation>
        <CrossLinks>
          <Link TARGET="Tests_CapturingLogger_cs" TYPE="USES_FIXTURE" />
        </CrossLinks>
      </IntegrationTests_LDDMarkers_AllKeyPaths_METHOD>

      <IntegrationTests_HardConstraints_Verified_METHOD NAME="HardConstraints_Verified" TYPE="IS_METHOD_OF_CLASS">
        <annotation>AC10: Verify hard constraints from AGENTS.md: .NET 10 target, JsonSerializerContext used (no Newtonsoft), 15s timeout configured, CircuitBreaker present, pipeline Normalize→Dedup→Filter observable, [McpServerToolType]/[McpServerTool] attributes present. Static assertions + behavioral verification.</annotation>
      </IntegrationTests_HardConstraints_Verified_METHOD>
    </IntegrationTests_CLASS>
  </Tests_IntegrationTests_cs>

  <!-- M8 Test File 3: Stdio Smoke Tests (AC5) -->
  <Tests_StdioSmokeTests_cs FILE="tests/McpWebSearchService.Tests/StdioSmokeTests.cs" TYPE="SMOKE_TEST" MILESTONE="M8">
    <annotation>
      Stdio-based smoke tests (ADR-026 approach A). Spawns `dotnet run --no-build` as child process,
      sends JSON-RPC over stdin, parses responses from stdout. 2 smoke tests: process startup + initialize handshake,
      tools/list returns 2 tools with correct names + params + descriptions. Tests REAL transport + REAL host startup.
      IAsyncLifetime for process lifecycle. Tests ADR-019 (tool name derivation) + ADR-020 (param names) + ADR-028 (descriptions).
    </annotation>
    <keywords>test, smoke, stdio, json-rpc, mcp, process, initialize, tools/list, tool names, descriptions</keywords>

    <StdioSmokeTests_CLASS NAME="StdioSmokeTests" TYPE="TEST_CLASS">
      <annotation>public class. IAsyncLifetime (InitializeAsync spawns process, DisposeAsync kills it). Process: dotnet run --no-build.</annotation>

      <StdioSmokeTests_ProcessStarts_InitializeHandshake_METHOD NAME="ProcessStarts_InitializeHandshake" TYPE="IS_METHOD_OF_CLASS">
        <annotation>AC5a: Spawn dotnet run → send JSON-RPC initialize → verify response: protocolVersion non-empty, serverInfo.name = "web-search-mcp", serverInfo.version non-empty. Process starts within 10s.</annotation>
      </StdioSmokeTests_ProcessStarts_InitializeHandshake_METHOD>

      <StdioSmokeTests_ToolsList_ReturnsTwoTools_METHOD NAME="ToolsList_ReturnsTwoTools" TYPE="IS_METHOD_OF_CLASS">
        <annotation>AC5b: After initialize → send tools/list → verify exactly 2 tools: web_search (params: query, categories, timeRange, language), fetch_and_extract (params: url). Verify non-empty descriptions (ADR-028). Verify tool names are snake_case (ADR-019).</annotation>
      </StdioSmokeTests_ToolsList_ReturnsTwoTools_METHOD>
    </StdioSmokeTests_CLASS>
  </Tests_StdioSmokeTests_cs>

  <!-- M8 Test File 4: Test Host Factory (DI container builder helper) -->
  <Tests_TestHostFactory_cs FILE="tests/McpWebSearchService.Tests/Infrastructure/TestHostFactory.cs" TYPE="TEST_FIXTURE" MILESTONE="M8">
    <annotation>
      Helper that builds a ServiceCollection replicating Program.cs DI wiring with mock HttpMessageHandler.
      Registers: SearXNGSettings + McpServerSettings via AddOptions + Bind(in-memory config),
      CircuitBreakerState singleton, AddHttpClient&lt;ISearXNGClient, SearXNGClient&gt; with ConfigurePrimaryHttpMessageHandler(mockHandler),
      AddHttpClient("FetchExtract") with ConfigurePrimaryHttpMessageHandler(mockHandler),
      ISearchResultProcessor → SearchResultProcessor singleton,
      WebSearchTools singleton. Returns ServiceProvider + CapturingLoggers for assertion.
    </annotation>
    <keywords>test, host factory, di, service collection, mock handler, capturing logger</keywords>

    <TestHostFactory_CLASS NAME="TestHostFactory" TYPE="TEST_FIXTURE_CLASS">
      <annotation>internal static class. Method: Build(mockSearXNGHandler, mockFetchHandler, settings) → (ServiceProvider, CapturingLoggers).</annotation>

      <TestHostFactory_Build_METHOD NAME="Build" TYPE="IS_METHOD_OF_CLASS">
        <annotation>Builds DI container with mock handlers. Returns (ServiceProvider, CapturingLogger&lt;WebSearchTools&gt;, CapturingLogger&lt;SearXNGClient&gt;, CapturingLogger&lt;SearchResultProcessor&gt;).</annotation>
        <CrossLinks>
          <Link TARGET="Tests_CapturingLogger_cs" TYPE="USES_FIXTURE" />
          <Link TARGET="Program_Main_METHOD" TYPE="REPLICATES_DI" />
        </CrossLinks>
      </TestHostFactory_Build_METHOD>
    </TestHostFactory_CLASS>
  </Tests_TestHostFactory_cs>
</DraftCodeGraph>
```

---

## 2. Step-by-step Data Flow

1. **Create `CapturingLogger.cs`** (ADR-027):
   - `internal sealed class CapturingLogger<T> : ILogger<T>` — captures all log entries into `List<LogEntry>` (thread-safe via `lock`).
   - `internal record LogEntry(LogLevel Level, string Message, Exception? Exception)` — captured log entry.
   - `LogEntries` property returns `IReadOnlyList<LogEntry>` for assertion.
   - `IsEnabled(LogLevel)` returns `true` for all levels.
   - `Log<TState>(...)` formats the message via the `formatter` callback and stores `new LogEntry(logLevel, formattedMessage, exception)`.
   - `csharp-conventions`: `#region CLASS_CapturingLogger`, `#region METHOD_Log`, XML docs, GREP_SUMMARY, STRUCTURE.

2. **Create `TestHostFactory.cs`** (ADR-026 helper):
   - `internal static class TestHostFactory` with method `Build(HttpMessageHandler mockSearXNGHandler, HttpMessageHandler mockFetchHandler, SearXNGSettings? settings = null)`.
   - Creates a `ServiceCollection`, registers:
     - In-memory `IConfiguration` with `SearXNG` + `McpServer` sections (or uses the provided settings).
     - `AddOptions<SearXNGSettings>().Bind(configuration.GetSection("SearXNG"))` + validation (same as Program.cs).
     - `AddOptions<McpServerSettings>().Bind(...)` + validation.
     - `AddSingleton<CircuitBreakerState>()` (ADR-017).
     - `AddHttpClient<ISearXNGClient, SearXNGClient>().ConfigurePrimaryHttpMessageHandler(mockSearXNGHandler).ConfigureHttpClient(c => c.Timeout = ...)` (timeout configurable for the timeout test).
     - `AddHttpClient("FetchExtract").ConfigurePrimaryHttpMessageHandler(mockFetchHandler).ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15))` (ADR-022).
     - `AddSingleton<ISearchResultProcessor, SearchResultProcessor>()`.
     - `CapturingLogger<WebSearchTools>`, `CapturingLogger<SearXNGClient>`, `CapturingLogger<SearchResultProcessor>` registered as `ILogger<T>`.
     - `AddSingleton<WebSearchTools>()` (resolvable from DI — all deps injected).
   - Returns `(ServiceProvider, CapturingLogger<WebSearchTools>, CapturingLogger<SearXNGClient>, CapturingLogger<SearchResultProcessor>)`.

3. **Create `IntegrationTests.cs`** (AC1–AC4, AC6, AC8, AC10):
   - **`WebSearch_ReturnsResults_WithPipeline`** (AC1): Mock SearXNG returns JSON with 5+ raw results including: duplicate URLs (different engines), HTML in content, a blocked-domain URL, a short snippet (<20 chars). Build DI via `TestHostFactory`. Resolve `WebSearchTools`. Call `WebSearch("test query")`. Deserialize the JSON string → `SearchResultDto[]`. Assert: duplicates removed (count < raw count), HTML stripped (no `<` in snippets), snippets ≤300 chars, blocked domain absent, count ≤ MaxResults. Assert `[IMP:9]` in `CapturingLogger<WebSearchTools>.LogEntries`.
   - **`WebSearch_SearXNGUnavailable_RussianError`** (AC2): Mock SearXNG returns HTTP 500. Call `WebSearch` 3 times → CircuitBreaker reaches threshold → 4th call: CB OPEN → SearXNGUnavailableException → WebSearch returns JSON-serialized `"Search service temporarily unavailable"`. Deserialize → verify the exact error string. Assert `[IMP:7]` in the captured logs.
   - **`FetchAndExtract_ReturnsText`** (AC3): Mock "FetchExtract" handler returns an HTML page (with `<script>`, `<style>`, tags, entities). Call `FetchAndExtract("http://example.com/page")`. Assert: plain text returned, ≤5000 chars, no HTML tags, entities decoded, truncated with an ellipsis if >5000. Assert `[IMP:8]` or `[IMP:9]` in the captured logs.
   - **`TimeoutHandling`** (AC4): Mock SearXNG handler `Task.Delay(2000)`. `TestHostFactory` configures `HttpClient.Timeout = 500ms`. Call `WebSearch`. Assert: returns `"Search service temporarily unavailable"` (via SearXNGUnavailableException). Completes in <3s (no hang). Assert `[IMP:9]` in the captured logs.
   - **`SearXNGMock_CoversAllScenarios`** (AC6): `[Theory]` with 5 scenarios: (1) success with results, (2) empty results `{"results":[]}`, (3) delay > timeout, (4) HTTP 500, (5) connection refused (handler throws `HttpRequestException`). Each produces the correct WebSearch response.
   - **`LDDMarkers_AllKeyPaths`** (AC8): Run search success + search unavailability + timeout + fetch_and_extract success. Assert the captured logs contain `[IMP:7-10]` markers on each path. Semantic verification.
   - **`HardConstraints_Verified`** (AC10): Assert: `WebSearchTools` has `[McpServerToolType]` + methods have `[McpServerTool]` (reflection or type inspection). Assert `McpJsonContext` is used (no `Newtonsoft.Json` import). Assert `CircuitBreakerState` is a singleton. Assert the pipeline order is observable (an HTML snippet with 25 plain chars passes, 15 chars filtered). Assert .NET 10 target (runtime version or compile-time constant).

4. **Create `StdioSmokeTests.cs`** (AC5):
   - `public class StdioSmokeTests : IAsyncLifetime` — spawns `dotnet run --no-build` in `InitializeAsync`, kills the process in `DisposeAsync`.
   - **`ProcessStarts_InitializeHandshake`** (AC5a): Send JSON-RPC `initialize` over stdin → read the response from stdout → parse JSON → assert `protocolVersion` non-empty, `serverInfo.name` = `"web-search-mcp"`, `serverInfo.version` non-empty. Process starts within 10s (timeout on the stdout read).
   - **`ToolsList_ReturnsTwoTools`** (AC5b): After initialize → send `tools/list` → parse the response → assert exactly 2 tools. Assert `web_search` exists with params `query`, `categories`, `timeRange`, `language` (ADR-020). Assert `fetch_and_extract` exists with param `url`. Assert both have a non-empty `description` (ADR-028). Assert the tool names are snake_case (ADR-019).
   - JSON-RPC framing: each request is a single line of JSON (newline-delimited). The response is also newline-delimited JSON on stdout. Use `StreamReader.ReadLine()` with a timeout.
   - Process: `Process.Start(new ProcessStartInfo("dotnet", "run --no-build") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })`.

5. **Modify `Services/WebSearchTools.cs`** (ADR-028):
   - Add `[Description("Searches the internet for current information. Use it when you need data not in your knowledge base, or when you need to check the latest news.")]` to the `WebSearch` method (or `[McpServerTool(Description = "...")]` — @code verifies which the SDK reads).
   - Add `[Description("Loads the content of a web page by URL and returns cleaned text. Use it when the search snippet is insufficient.")]` to the `FetchAndExtract` method.
   - No logic change. Update `[CHANGES]` in MODULE_CONTRACT: `LAST_CHANGE: M8 — added [Description] attributes (ADR-028)`.

6. **Update `tests/test_guide.md`** — M8 section: key inputs, expected LDD markers, AC mapping (see deliverable 3 below).

7. **AOT re-verification** (architect-executed or @code-executed):
   - `dotnet publish -c Release -r win-x64 /p:PublishAot=true` → 0 × `warning IL####` (ADR-028 attributes are AOT-safe, but verify).

8. **Full `dotnet test` run** — 110 unit (M2–M6) + M8 integration + M8 stdio-smoke = ALL PASS.

9. **(With the operator) Docker E2E** — DEFERRED (AC7, AC9):
   - `docker build -t mcp-web-search .` + `docker run --rm -i mcp-web-search` → send JSON-RPC `initialize` → verify response. Operator-gated.

10. **Final `@qa` acceptance** — read-only verification of all ACs, semantic log audit, `SUCCESS` or `BLOCK` verdict.

---

## 3. Acceptance Criteria

- [ ] **AC1:** Integration test `WebSearch_ReturnsResults_WithPipeline`: mock SearXNG returns raw results with duplicates/HTML/blocked-domains → `WebSearch` returns a JSON string → deserialize → verify the M5 pipeline applied (dedup reduced count, HTML stripped, snippets ≤300, blocked domains absent, Take(MaxResults)).
- [ ] **AC2:** Integration test `WebSearch_SearXNGUnavailable_RussianError`: mock SearXNG HTTP 500 × 3 → CircuitBreaker OPEN → 4th call returns JSON-serialized `"Search service temporarily unavailable"` (not an exception, the exact error string per SPEC §4.2).
- [ ] **AC3:** Integration test `FetchAndExtract_ReturnsText`: mock "FetchExtract" handler returns HTML → `FetchAndExtract` returns plain text ≤5000 chars (HTML stripped, entities decoded, truncated with an ellipsis if >5000).
- [ ] **AC4:** Integration test `TimeoutHandling`: mock SearXNG delays 2000ms + HttpClient.Timeout=500ms → `WebSearch` returns `"Search service temporarily unavailable"` within <3s (no hang). The timeout triggers SearXNGUnavailableException → error string.
- [ ] **AC5:** Stdio smoke tests: (a) `ProcessStarts_InitializeHandshake` — `dotnet run` starts, JSON-RPC `initialize` returns a valid response with `protocolVersion` + `serverInfo`. (b) `ToolsList_ReturnsTwoTools` — `tools/list` returns exactly `web_search` + `fetch_and_extract` with correct param names (camelCase, ADR-020) + non-empty descriptions (ADR-028).
- [ ] **AC6:** Integration test `SearXNGMock_CoversAllScenarios` `[Theory]`: the mock covers 5 scenarios — success, empty response, delay (timeout), 5xx, connection refused. Each produces the correct WebSearch response.
- [ ] **AC7:** (With the operator) **DEFERRED** — Docker E2E: `docker build` + `docker run` → the MCP server starts in the AOT container + responds to JSON-RPC `initialize` over stdio. Operator-gated (`docker` is NOT auto-allowed).
- [ ] **AC8:** Integration test `LDDMarkers_AllKeyPaths`: the captured logs contain `[IMP:7-10]` markers on all key paths — search success (`[IMP:9]`), search unavailability (`[IMP:7]`), timeout (`[IMP:9]`), fetch_and_extract success (`[IMP:8][IMP:9]`). Semantic verification via `CapturingLogger` (ADR-027).
- [ ] **AC9:** (With the operator) **DEFERRED** — Docker AOT verification: the native binary runs in the container without the .NET runtime. Operator-gated. (Covered by M7 AC7/AC8 — M8 re-verifies if the Docker E2E is performed.)
- [ ] **AC10:** Integration test `HardConstraints_Verified`: all hard constraints from AGENTS.md confirmed — .NET 10 target, AOT-clean (0 × IL####, re-verified after ADR-028), `JsonSerializerContext` (no Newtonsoft), 15s timeout configured, Circuit Breaker present, pipeline Normalize→Dedup→Filter observable, `[McpServerToolType]`/`[McpServerTool]` attributes present.
- [ ] **AC11:** `tests/test_guide.md` updated with an M8 section: key inputs, expected LDD markers, AC mapping table.
- [ ] **AC12:** `AppGraph.xml` updated — M8 test nodes appended (IntegrationTests, StdioSmokeTests, CapturingLogger, TestHostFactory). `Future_M8_Tests` placeholder replaced.
- [ ] **AC13:** `Services/WebSearchTools.cs` modified — `[Description]` attributes added to the `WebSearch` + `FetchAndExtract` methods (ADR-028). `dotnet publish -c Release -r win-x64 /p:PublishAot=true` → 0 × `warning IL####`.
- [ ] **AC14:** Full `dotnet test` run — 100% PASS (110 unit from M2–M6 + M8 integration + M8 stdio-smoke). No regressions.
- [ ] **AC15:** Semantic verification by `@qa`: logs match contracts (not only green tests) — `SUCCESS` or `BLOCK` verdict in `tests/qa_report.md`.

---

## 4. Risks and mitigations

| Risk | Mitigation |
|---|---|
| stdio-smoke tests flaky (process spawn timing, stdout buffering, newline-delimited JSON parsing) | ADR-026: stdio-smoke is SECONDARY (only 2 tests: initialize + tools/list). DI-integration tests are PRIMARY (cover AC1–AC4 reliably). Stdio tests use `StreamReader.ReadLine()` with a 10s timeout. If flaky on CI, mark `[Trait("Category","Stdio")]` for selective exclusion. |
| `ConfigurePrimaryHttpMessageHandler` extension not found in the test project (missing `Microsoft.Extensions.Http`) | ADR-026 §0.6: transitive via the ProjectReference to the main project. If compilation fails, add `<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.9" />` to the test csproj. |
| MCP SDK may read the tool description from `[McpServerTool(Description=...)]` NOT `[Description]` (System.ComponentModel) | ADR-028: @code verifies which attribute produces a non-empty description in `tools/list`. If `[Description]` doesn't work, use `[McpServerTool(Description = "...")]`. The stdio-smoke test AC5b verifies the result. |
| Timeout test slow if a real 15s delay is used | ADR-026 §0.5: short `HttpClient.Timeout` (500ms) + mock `Task.Delay(2000)`. Completes <3s. The production 15s is unit-tested in M4. |
| CapturingLogger not thread-safe → race condition in parallel test execution | ADR-027: `lock`-based `List<LogEntry>`. xUnit runs test classes in parallel by default, but methods within a class are sequential. Each test method creates its own `TestHostFactory` → its own `CapturingLogger` instance. No shared state. |
| `dotnet run --no-build` in stdio-smoke requires a prior `dotnet build` | Test setup: `dotnet build` before `dotnet test`. The CI pipeline builds before testing. If `--no-build` fails, remove it (use `dotnet run` — slower but always works). |
| Docker E2E (AC7, AC9) operator-gated → cannot be verified automatically by @qa | AC7/AC9 marked DEFERRED (same pattern as M7 AC7/AC8). @qa verifies AC1–AC6, AC8, AC10–AC15. AC7/AC9 — the operator performs them manually, results recorded in `tests/qa_report.md` as DEFERRED. |
| Adding `[Description]` to WebSearchTools breaks AOT (unlikely — the attribute is plain metadata) | ADR-028: re-verify `dotnet publish -c Release -r win-x64 /p:PublishAot=true` → 0 × `IL####`. `[Description]` is `System.ComponentModel.DescriptionAttribute` — no reflection at runtime, the SDK reads it at registration via source-generated code. |
| GREEN TEST TRAP — tests pass but the logs don't match the contracts | ADR-027: `CapturingLogger` captures actual log output. Integration tests assert on log content (`[IMP:7-10]` markers). `@qa` performs semantic verification (mode-qa Step 2: LDD telemetry). AC15 enforces this. |

---

## 5. Delegation to @code

**M8 requires an `@code` dispatch** to implement the E2E tests + modify `WebSearchTools.cs`:

### Files created/modified by @code:

1. **`tests/McpWebSearchService.Tests/Infrastructure/CapturingLogger.cs`** (NEW) — `CapturingLogger<T>` + `LogEntry` record. `csharp-conventions` apply.
2. **`tests/McpWebSearchService.Tests/Infrastructure/TestHostFactory.cs`** (NEW) — DI container builder with mock handler injection. `csharp-conventions` apply.
3. **`tests/McpWebSearchService.Tests/Integration/IntegrationTests.cs`** (NEW) — 7 test methods (AC1–AC4, AC6, AC8, AC10). `csharp-conventions` apply.
4. **`tests/McpWebSearchService.Tests/StdioSmokeTests.cs`** (NEW) — 2 smoke tests (AC5a, AC5b). `csharp-conventions` apply.
5. **`Services/WebSearchTools.cs`** (MODIFY) — add `[Description]` attributes to `WebSearch` + `FetchAndExtract` (ADR-028). Update `[CHANGES]`.
6. **`tests/test_guide.md`** (MODIFY) — append the M8 section (AC mapping, LDD markers, key inputs).

### Constraints for @code:

- **The `csharp-conventions` skill APPLIES** — all `.cs` files (test + modify) follow `#region`, XML docs, GREP_SUMMARY, STRUCTURE, LDD markers.
- **Do NOT modify** `Program.cs`, `src/McpWebSearchService/McpWebSearchService.csproj`, or the M2–M7 `.cs` files (except `WebSearchTools.cs` for ADR-028).
- **Do NOT add** new PackageReferences to the test csproj (only if `ConfigurePrimaryHttpMessageHandler` does not compile — fallback per §0.6).
- **AOT safety**: after adding the `[Description]` attributes, verify `dotnet publish -c Release -r win-x64 /p:PublishAot=true` → 0 × `IL####`.
- **LDD in tests**: integration test methods must contain `[IMP:7-10]` markers in XML comments (telemetry for the QA audit) — follow the pattern from the existing tests (see `WebSearchToolsTests.cs` `#region TEST_METHOD_*`).
- **Anti-Loop Protocol**: `.test_counter.json` = 2 (stale from M5). @code resets the counter before starting work (managed by the orchestrator).
- **StdioSmokeTests**: `dotnet run --no-build` requires a prior `dotnet build`. @code runs `dotnet build` before `dotnet test`.

### Implementation order (recommended):

1. `CapturingLogger.cs` (no dependencies)
2. `TestHostFactory.cs` (depends on CapturingLogger)
3. `IntegrationTests.cs` (depends on TestHostFactory + CapturingLogger)
4. `StdioSmokeTests.cs` (no dependencies on other M8 files — standalone)
5. `WebSearchTools.cs` modification (ADR-028 — add `[Description]`)
6. `tests/test_guide.md` update
7. `dotnet build` + `dotnet test` — full run
8. `dotnet publish -c Release -r win-x64 /p:PublishAot=true` — AOT re-verification (AC13)

---

# Development Plan — M9: Semantic category selection via a local LLM

**Stage:** M9 (Category Inference via Local LLM)
**Predecessors:** M2 (Configuration), M6 (MCP Tools Integration), M8 (E2E + acceptance) — all complete
**Successors:** —
**Requirement source:** `SPEC.md` §2.1 (`categories`); `milestones/M9-category-inference-llm.md`; `AGENTS.md` (ADR-006/007/008/014/018/026/027/028 + ADR-032, hard constraints).
**PURPOSE:** When the agent omits `categories`, `McpWebSearchService` selects the SearXNG category **semantically by the query** via a local LLM (M8 diagnostics: `general` unstable, `it` stable). Explicit agent categories keep unconditional priority; LLM unavailability → deterministic `general` fallback; strict AOT safety maintained.

## 0. Context and architectural decisions (M9)
- **ADR-032 (new):** semantic category selection via a local LLM — see `AGENTS.md`. Key points:
  1. The `categories` default changes from `"general"` to `null` in `WebSearch`; an explicit category short-circuits with zero LLM latency.
  2. `ICategoryInferenceClient` → `LocalLlmCategoryInference` (OpenAI-compatible `/v1/chat/completions`), a dedicated AOT-safe `LocalLlmJsonContext` (**internal** context + internal DTOs — resolved CS0053), strict closed-set validation, **never throws → null**, per-call timeout = `TimeoutSeconds` via linked CTS, and mandatory `chat_template_kwargs: {"enable_thinking": false}` (probe-proven for Qwen3 reasoning models).
  3. `SearchCategoryClassifier` — `internal static class` (TitleSimilarity pattern): explicit → LLM → `general`.
  4. `CategoryInferenceSettings` — sealed, public setters (ADR-006 binder), configurable `Categories[]` (not hardcoded).
- **Carried-forward ADRs (still active):** ADR-001..030 all continue to hold. `LocalLlmJsonContext` uses the same source-gen discipline as McpJsonContext (ADR-014 spirit); DI via `AddHttpClient` (ADR-008); `EnableConfigurationBindingGenerator` (ADR-006) covers the new settings class; `InternalsVisibleTo` (ADR-008) exposes internals to the test project.

## 1. Draft Code Graph (M9) — ref: `milestones/M9-category-inference-llm.md`
New: `Configuration/CategoryInferenceSettings.cs` (CONFIG), `Services/ICategoryInferenceClient.cs` (INTERFACE), `Services/LocalLlmCategoryInference.cs` (SERVICE), `Services/SearchCategoryClassifier.cs` (SERVICE, internal static), `Models/LocalLlmDto.cs` (DTO), `Serialization/LocalLlmJsonContext.cs` (AOT context, internal). Modified: `Services/WebSearchTools.cs` (classifier integration), `Program.cs` (DI), `appsettings.json` (config). Tests: `SearchCategoryClassifierTests.cs`, `LocalLlmCategoryInferenceTests.cs`, `IntegrationTests.cs` (+1 optional AC), `TestHostFactory.cs` (+MockLlmHandler).

## 2. Step-by-step Data Flow (M9)
1. `appsettings.json`: `SearXNG.DefaultLanguage = "auto"`; add `CategoryInference{BaseUrl, Model, TimeoutSeconds:5, EnableFallback:true, Categories[]}`.
2. `Program.cs`: `AddOptions<CategoryInferenceSettings>().Bind(...).Validate(...).ValidateOnStart()`; `AddHttpClient<ICategoryInferenceClient, LocalLlmCategoryInference>()`.
3. `LocalLlmCategoryInference`: POST `/v1/chat/completions` (model, messages, temperature:0, max_tokens:10, `chat_template_kwargs.enable_thinking:false`); parse `choices[0].message.content`; **strict closed-set validation**; null-on-failure; TimeoutSeconds linked CTS; LDD `[IMP:5-9]`.
4. `SearchCategoryClassifier`: explicit → LLM → `general` (EnableFallback-gated). `internal static`.
5. `WebSearchTools`: `categories` default `null`; `Categories = SearchCategoryClassifier.InferCategoryAsync(query, categories, client, enableFallback, ct)`.
6. Tests + guide; ADR-032; `dotnet build`/`test`; AOT publish.

## 3. Acceptance Criteria (M9) — ref milestone §Acceptance Criteria
(a) appsettings DefaultLanguage == "auto" + valid CategoryInference; (b) CategoryInferenceSettings sealed/public-setters; (c) LocalLlmCategoryInference validates to the closed set, null-on-failure, `[IMP:5-9]`; (d) SearchCategoryClassifier internal static, explicit → LLM → general; (e) WebSearchTools uses the classifier, explicit priority; (f) SearchCategoryClassifierTests a/b/c/d; (g) LocalLlmCategoryInferenceTests success/HTTP-fail/timeout/bad-JSON; (h) optional IntegrationTests no-category IT query → categories=it; (i) test_guide.md M9; (j) AGENTS.md ADR-032; (k) `dotnet test` 100% PASS; (l) AOT 0×IL####; (m) control MCP-stdio calls; (n) @qa semantic verification SUCCESS/BLOCK.

## 4. Risks and mitigations (M9)
| Risk | Mitigation |
|---|---|
| AOT LLM client (SDK libs with reflection) | Typed HttpClient + `LocalLlmJsonContext` source-gen; `ReadFromJsonAsync(JsonTypeInfo)`. Verified clean publish. |
| Reasoning-model latency/one-word failure | Send `chat_template_kwargs.enable_thinking:false` + temperature:0 + max_tokens:10 (probe). Timeout 5s default. |
| LLM address/model not fixed in env | Configurable `CategoryInference` section; operator sets BaseUrl/Model; Docker uses `host.docker.internal` or a network address. |
| Regression of non-IT queries | `general` fallback preserves prior behaviour (test (b)); explicit priority intact. |
| GREEN TEST TRAP | Integration test asserts `[IMP:4][5]` category marker + LLM log; @qa semantic log audit. |

## 5. Delegation to @code (M9)
Files: 6 new `.cs` (Configuration/Services/Models/Serialization) + modify WebSearchTools/Program/appsettings + 2 new test files + update TestHostFactory/IntegrationTests + update test_guide.md. Constraints: `csharp-conventions` apply; AOT safety (source-gen only); no new PackageReference; `LocalLlmJsonContext` internal (DTOs internal); verify `dotnet test` + `dotnet publish -c Release -r win-x64 /p:PublishAot=true` 0×IL####.

---

# Development Plan — M10: MCP server as a standalone HTTP service (moving away from AOT)

**Stage:** M10 (HTTP transport + JIT — supersedes the AOT hard-constraint)
**Predecessors:** M1–M9 (the whole accumulated stack: config, DTO, HTTP client, pipeline, tools, docker, e2e, category-inference)
**Successors:** — (transition milestone: stdio+AOT → streamable-HTTP+JIT)
**Requirement source:** `milestones/M10-http-transport-service.md`; `AGENTS.md` (ADR-001..032 carried forward + ADR-033 new); operator requirement (long-lived service, decoupled from client launches).

**PURPOSE:**
Move `McpWebSearchService` from the stdio transport (process-per-session, managed by opencode) to the streamable HTTP transport (Kestrel), running as a long-lived container service. A single container `docker run -d -p 8080:8080 mcp-web-search` serves an arbitrary number of MCP clients over HTTP. The HTTP transport requires ASP.NET Core (Kestrel) → JIT (Native AOT does not support this stack) — M10 simultaneously moves the project away from AOT. Partial edits are already in the working tree (csproj, Program.cs, McpServerSettings, appsettings.json) but are NOT built and NOT verified. `@code` finishes them, verifies `dotnet build`+`dotnet test`, replaces the stdio tests with an HTTP smoke test, and rewrites the Dockerfile + manifest.

---

## 0. Context and architectural decisions (M10)

### 0.1. ADR-033: HTTP transport + JIT (supersedes the AOT hard-constraint wording)

See `AGENTS.md` ADR-033 for the full decision. Key points:
1. Transport: `AddMcpServer().WithHttpTransport(o => o.ConnectionRequestTimeout=30s).WithTools<WebSearchTools>()` + `app.MapMcp(mcpPath)` + `app.Run($"http://0.0.0.0:{port}")`. Kestrel binds to `McpServerSettings.Port` (default 8080), MCP endpoint mapped at `McpServerSettings.Path` (default "/mcp").
2. SDK `Microsoft.NET.Sdk.Web` + `PublishAot=false` — the AOT hard-constraint (SPEC §5.1) is CANCELLED/REPLACED by JIT.
3. Source-gen `JsonSerializerContext` (McpJsonContext, LocalLlmJsonContext) RETAINED — good practice under JIT, no churn, no reflection.
4. Dockerfile: JIT build (`sdk:10.0` + framework-dependent publish) + `aspnet:10.0` runtime (supersedes ADR-023 manual apt-get + ADR-024 `debian:bookworm-slim`).
5. Manifest: `.mcp/server.json` `remotes[]` with a `streamable-http` URL (supersedes `packages[].transport` stdio command).
6. Tests: `StdioSmokeTests.cs` → `HttpSmokeTests.cs` via `WebApplicationFactory<Program>` (Microsoft.AspNetCore.Mvc.Testing 10.0.11).
7. `/healthz` endpoint: lightweight GET for the Docker HEALTHCHECK (no MCP protocol overhead).

### 0.2. Carried-forward ADRs (ADR-001..032 — active vs superseded by ADR-033)

| ADR | Status under M10 | Rationale |
|---|---|---|
| ADR-001 (`RuntimeIdentifiers` plural) | **KEPT** (harmless) | RIDs used for self-contained publish; not needed for framework-dependent but harmless. |
| ADR-002 (Cross-OS AOT via win-x64 proxy) | **SUPERSEDED** | JIT publish is cross-OS. The `win-x64` proxy is no longer special. AOT verification `dotnet publish -r win-x64 /p:PublishAot=true` is NO LONGER REQUIRED. |
| ADR-003 (`InvariantGlobalization=true`) | **KEPT** (harmless under JIT) | `ToLowerInvariant()` works; no culture-aware comparison needed. No churn. |
| ADR-004 (binary name `McpWebSearchService`) | **KEPT** | Still used as the assembly/DLL name. `ENTRYPOINT ["dotnet", "McpWebSearchService.dll"]`. |
| ADR-005 (`.slnx`) | **KEPT** | Unaffected. |
| ADR-006 (`EnableConfigurationBindingGenerator` + Hosting 10.x) | **PARTIALLY SUPERSEDED** | The AOT-safety rationale is no longer a hard constraint, BUT the source-gen binder is KEPT (harmless, reflection-free). Hosting 10.0.9 KEPT (required by `WebApplication.CreateBuilder`). |
| ADR-007 (Manual Circuit Breaker, no Polly) | **CONSCIOUS CARRY-FORWARD** | The AOT rationale weakens (Polly v8 is JIT-safe), but the manual CB is already implemented + tested + zero deps. No benefit to switching. |
| ADR-008–016 (business logic, pipeline, DTOs) | **KEPT** (unaffected) | HTTP transport doesn't change SearXNGClient, SearchResultProcessor, TitleSimilarity, etc. |
| ADR-017 (CB DI registration) | **KEPT** | `AddSingleton<CircuitBreakerState>()` still valid. |
| ADR-018 (web_search return pre-serialized JSON string) | **CONSCIOUS CARRY-FORWARD** | The AOT rationale weakens, but the string approach is harmless + already implemented. Keep. |
| ADR-019–028 (tool registration, params, descriptions) | **KEPT** (unaffected) | `[McpServerToolType]`/`[McpServerTool]`, SnakeCaseLower, param names, descriptions — all unchanged. |
| ADR-029 (Dockerfile explicit-csproj) | **LESSON CARRIES FORWARD** | M10 rewrites the Dockerfile, but the "publish the .csproj explicitly, not the .slnx" lesson applies. |
| ADR-030 (web-search-mcp as research tool) | **KEPT** (meta-protocol, unaffected) | The server itself is the research tool — the transport change doesn't affect agent usage. |
| ADR-032 (category inference via LLM) | **KEPT** (unaffected) | `LocalLlmCategoryInference`, `SearchCategoryClassifier` — all work under JIT. Source-gen `LocalLlmJsonContext` retained. |

### 0.3. HTTP-smoke test strategy — `WebApplicationFactory<Program>` (in-memory TestServer)

- **Approach:** `Microsoft.AspNetCore.Mvc.Testing 10.0.11` provides `WebApplicationFactory<TEntryPoint>` — boots the REAL `Program.cs` (with ALL DI registrations, options validation, MCP endpoint) in an in-memory `TestServer` (no real port, no process spawn). The test creates an `HttpClient` via `factory.CreateClient()` and POSTs JSON-RPC to `/mcp`.
- **Why this approach (vs spawning a process + real HTTP):** (1) Standard ASP.NET Core integration test pattern — reliable under `dotnet test`, no flaky process/port management. (2) Exercises the REAL MCP streamable HTTP handler, REAL endpoint routing, REAL DI wiring, REAL options validation — everything except the real Kestrel network stack. (3) Fast (ms per test, no process startup). (4) No Docker container needed — works on any dev machine with `dotnet test`.
- **Required changes:**
  1. `Program.cs`: add `public partial class Program { }` at the end — makes the top-level-statements-generated `Program` class public, enabling `WebApplicationFactory<Program>` in the test project.
  2. Test csproj: add `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.11" />`. The test project stays `Microsoft.NET.Sdk` (NOT `Microsoft.NET.Sdk.Web` — the package works with the plain SDK via a ProjectReference to the main Web project). If compilation issues arise, switch to `Microsoft.NET.Sdk.Web`.
  3. Replace `StdioSmokeTests.cs` with `HttpSmokeTests.cs`:
     - `public sealed class HttpSmokeTests : IClassFixture<WebApplicationFactory<Program>>`
     - Constructor: `_client = factory.CreateClient();`
     - `Initialize_Handshake_Succeeds`: POST JSON-RPC `initialize` to `/mcp` with `Content-Type: application/json`, `Accept: application/json, text/event-stream` → assert a valid JSON-RPC response (parse the body — may be JSON or SSE `data:` lines). Extract `Mcp-Session-Id` from the response headers.
     - `ToolsList_ReturnsTwoToolsWithDescriptions`: POST JSON-RPC `tools/list` to `/mcp` with the `Mcp-Session-Id` header → assert exactly 2 tools (`web_search`, `fetch_and_extract`) with non-empty descriptions.
- **Streamable HTTP protocol note:** MCP streamable HTTP is POST-based. The `initialize` response includes an `Mcp-Session-Id` header. `tools/list` requires this header. The response body may be `application/json` (single response) or `text/event-stream` (SSE with `data:` lines). @code should handle both: read the body as a string, look for the JSON-RPC response (either directly or within a `data:` prefix). Verify the exact header names by testing against the running server.
- **Config note:** `WebApplicationFactory<Program>` reads `appsettings.json` from the main project's content root. The real SearXNG/LLM URLs pass `ValidateOnStart` (valid absolute URIs). The host starts in TestServer — `app.Run(url)` is intercepted (TestServer ignores the URL). `McpServer.Port=8080` is irrelevant in the test (TestServer doesn't bind to real ports). If the content root is wrong, @code may need `factory.WithWebHostBuilder(b => b.UseContentRoot(...))`.

### 0.4. Dockerfile — JIT + `aspnet:10.0` base (supersedes the AOT Dockerfile)

```dockerfile
# Stage 1: Build — .NET 10 SDK (JIT, NOT AOT)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# JIT publish (framework-dependent — runtime provided by aspnet:10.0).
# NOT AOT: no /p:PublishAot=true. NOT self-contained: /p:SelfContained=false.
# Publish .csproj explicitly (ADR-029 lesson — .dockerignore excludes tests/ from context).
RUN dotnet publish src/McpWebSearchService/McpWebSearchService.csproj -c Release -o /app/publish /p:SelfContained=false /p:PublishSingleFile=false

# Stage 2: Runtime — ASP.NET Core 10 (includes Kestrel + ICU + OpenSSL + ca-certificates)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "McpWebSearchService.dll"]
```

- **Rationale:** `aspnet:10.0` includes the .NET 10 runtime + ASP.NET Core shared framework (Kestrel) + all native deps (ICU, OpenSSL, ca-certificates) — supersedes ADR-023 manual `apt-get install` + ADR-024 `debian:bookworm-slim` base. Framework-dependent publish (`/p:SelfContained=false`) produces only the app DLL + dependencies (no runtime) — a smaller app layer, runtime from the base image. `curl` installed for the HEALTHCHECK. `EXPOSE 8080` for the MCP HTTP port. `ENTRYPOINT ["dotnet", "McpWebSearchService.dll"]` — JIT (NOT the `./McpWebSearchService` native binary).

### 0.5. `.mcp/server.json` — `streamable-http` transport via `remotes[]`

Per the MCP server.json schema (2025-10-17, [SOURCE: webfetch, schema URL, ts=2026-09-08T12:00:00Z]):
- `StreamableHttpTransport`: `{ "type": "streamable-http", "url": "..." }` (required: `type`, `url`; optional: `headers`).
- `ServerDetail.remotes[]`: top-level field for already-running HTTP endpoints (vs `packages[]` for installable stdio packages).

```json
{
  "$schema": "https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json",
  "description": "MCP server for web search via SearXNG. It provides the web_search and fetch_and_extract tools.",
  "name": "io.github.toucaner/searxng-mcp",
  "version": "0.1.0-beta",
  "remotes": [
    {
      "type": "streamable-http",
      "url": "http://localhost:8080/mcp"
    }
  ],
  "repository": {
    "url": "https://github.com/toucaner/searxng-mcp",
    "source": "github"
  }
}
```

- **Note:** `packages[]` (stdio) removed — the server is a long-lived Docker container, not a package to install. `remotes[]` declares the running HTTP endpoint. @code verifies opencode accepts this manifest format (if opencode requires `packages[]`, fall back to `packages[].transport.type = "streamable-http"` with `url`).

### 0.6. `/healthz` endpoint for the HEALTHCHECK

- `app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))` — a minimal GET endpoint returning 200 OK.
- Added to `Program.cs` AFTER `app.MapMcp(mcpPath)` and BEFORE `app.Run(...)`.
- **Rationale:** Probing `/mcp` with a GET would return 405 (the MCP endpoint is POST-only). A dedicated `/healthz` endpoint is lightweight, has no MCP protocol overhead, and is the standard pattern for containerized services. The Docker `HEALTHCHECK` uses `curl -f http://localhost:8080/healthz`.

### 0.7. `InvariantGlobalization=true` + `EnableConfigurationBindingGenerator=true` — kept under JIT

- **`InvariantGlobalization=true` (ADR-003):** KEPT. Harmless under JIT — `ToLowerInvariant()` works, no culture-aware comparison needed. No churn.
- **`EnableConfigurationBindingGenerator=true` (ADR-006):** KEPT. Generates a compile-time binder (reflection-free) — harmless under JIT, avoids reflection. `Microsoft.Extensions.Hosting 10.0.9` KEPT (required by `WebApplication.CreateBuilder`). No churn.

---

## 1. Draft Code Graph (M10)

```xml
<DraftCodeGraph>
  <!-- M10 MODIFIED: Program.cs — WebApplication + streamable HTTP transport -->
  <Program_cs FILE="src/McpWebSearchService/Program.cs" TYPE="ENTRYPOINT" MILESTONE="M10_REWRITE">
    <annotation>
      M10 REWRITE: stdio → streamable HTTP transport on Kestrel (ADR-033).
      WebApplication.CreateBuilder(args) → AddOptions (SearXNG/McpServer/CategoryInference, ValidateOnStart)
      → AddSingleton&lt;CircuitBreakerState&gt; → AddHttpClient&lt;ISearXNGClient, SearXNGClient&gt; (15s)
      → AddHttpClient("FetchExtract") (15s) → AddHttpClient&lt;ICategoryInferenceClient, LocalLlmCategoryInference&gt;
      → AddSingleton&lt;ISearchResultProcessor, SearchResultProcessor&gt;
      → AddMcpServer().WithHttpTransport(o => ConnectionRequestTimeout=30s).WithTools&lt;WebSearchTools&gt;()
      → app.MapMcp(mcpPath) → app.MapGet("/healthz") → app.Run($"http://0.0.0.0:{port}").
      `public partial class Program { }` at end for WebApplicationFactory&lt;Program&gt; test accessibility.
      JIT (PublishAot=false). All M2–M9 DI registrations preserved.
    </annotation>
    <Program_Main_METHOD NAME="Main" TYPE="ENTRYPOINT_METHOD">
      <annotation>Top-level statements compile to Main. Blocks on app.Run until shutdown. Port/Path from McpServerSettings.</annotation>
      <CrossLinks>
        <Link TARGET="Tools_WebSearchTools_cs" TYPE="REGISTERS_TOOL_TYPE" />
        <Link TARGET="Configuration_McpServerSettings_cs" TYPE="BINDS_OPTIONS" />
      </CrossLinks>
    </Program_Main_METHOD>
  </Program_cs>

  <!-- M10 MODIFIED: McpServerSettings — added Port + Path -->
  <Configuration_McpServerSettings_cs FILE="src/McpWebSearchService/Configuration/McpServerSettings.cs" TYPE="SETTINGS" MILESTONE="M10_DELTA">
    <annotation>
      M10 delta: added Port (int, default 8080, validation 0&lt;Port≤65535) + Path (string, default "/mcp", non-empty).
      Bound to "McpServer" section. sealed class, public setters (ADR-006 binder). Validation in Program.cs ValidateOnStart.
    </annotation>
    <McpServerSettings_Port_PROPERTY NAME="Port" TYPE="PROPERTY" DATATYPE="int" DEFAULT="8080">
      <annotation>TCP port the Kestrel host binds (M10, ADR-033). Range-validated (0, 65535].</annotation>
    </McpServerSettings_Port_PROPERTY>
    <McpServerSettings_Path_PROPERTY NAME="Path" TYPE="PROPERTY" DATATYPE="string" DEFAULT="/mcp">
      <annotation>URL path the MCP streamable-HTTP endpoint is mapped at (M10, ADR-033). Clients connect to http://host:{Port}{Path}.</annotation>
    </McpServerSettings_Path_PROPERTY>
  </Configuration_McpServerSettings_cs>

  <!-- M10 NEW: HttpSmokeTests — replaces StdioSmokeTests -->
  <Tests_HttpSmokeTests_cs FILE="tests/McpWebSearchService.Tests/Smoke/HttpSmokeTests.cs" TYPE="SMOKE_TEST" MILESTONE="M10">
    <annotation>
      HTTP smoke tests (ADR-033). Uses WebApplicationFactory&lt;Program&gt; (Microsoft.AspNetCore.Mvc.Testing 10.0.11)
      to boot the real Program.cs in an in-memory TestServer. POSTs JSON-RPC to /mcp (initialize → tools/list).
      Tests REAL MCP streamable HTTP handler + REAL endpoint routing + REAL DI wiring.
      Replaces StdioSmokeTests.cs (which spawned dotnet run + sent JSON-RPC over stdio — no longer works under HTTP transport).
      IClassFixture&lt;WebApplicationFactory&lt;Program&gt;&gt;. 2 test methods.
    </annotation>
    <keywords>test, smoke, http, json-rpc, mcp, streamable-http, webapplicationfactory, testserver, initialize, tools/list</keywords>

    <HttpSmokeTests_CLASS NAME="HttpSmokeTests" TYPE="TEST_CLASS">
      <annotation>public sealed class. IClassFixture&lt;WebApplicationFactory&lt;Program&gt;&gt;.</annotation>

      <HttpSmokeTests_Initialize_Handshake_Succeeds_METHOD NAME="Initialize_Handshake_Succeeds" TYPE="IS_METHOD_OF_CLASS">
        <annotation>POST JSON-RPC initialize to /mcp → assert valid JSON-RPC response with protocolVersion + serverInfo. Response may be JSON or SSE — handle both.</annotation>
      </HttpSmokeTests_Initialize_Handshake_Succeeds_METHOD>

      <HttpSmokeTests_ToolsList_ReturnsTwoToolsWithDescriptions_METHOD NAME="ToolsList_ReturnsTwoToolsWithDescriptions" TYPE="IS_METHOD_OF_CLASS">
        <annotation>POST JSON-RPC tools/list to /mcp (with Mcp-Session-Id from initialize) → assert exactly 2 tools (web_search + fetch_and_extract) with non-empty descriptions (ADR-028).</annotation>
      </HttpSmokeTests_ToolsList_ReturnsTwoToolsWithDescriptions_METHOD>
    </HttpSmokeTests_CLASS>
  </Tests_HttpSmokeTests_cs>

  <!-- M10 REPLACED: StdioSmokeTests → HttpSmokeTests -->
  <Tests_StdioSmokeTests_cs FILE="tests/McpWebSearchService.Tests/StdioSmokeTests.cs" TYPE="SMOKE_TEST" STATUS="REPLACED_IN_M10">
    <annotation>
      M8 stdio smoke tests (ADR-026 approach A). REPLACED in M10 by HttpSmokeTests.cs (ADR-033).
      The stdio transport no longer exists — the server runs as an HTTP service on Kestrel.
      @code: delete this file, create HttpSmokeTests.cs in its place.
    </annotation>
  </Tests_StdioSmokeTests_cs>
</DraftCodeGraph>
```

---

## 2. Step-by-step Data Flow (M10)

1. **Verify/adapt partial edits (M10.2 — Transport):**
   - `Program.cs`: confirm `WebApplication.CreateBuilder(args)` + all DI registrations (SearXNG/McpServer/CategoryInference options with ValidateOnStart, CircuitBreakerState, typed HttpClients, SearchResultProcessor, MCP server with `WithHttpTransport` + `WithTools<WebSearchTools>`). Confirm `app.MapMcp(mcpPath)` + `app.Run($"http://0.0.0.0:{port}")` compile (namespaces `ModelContextProtocol.AspNetCore`, `Microsoft.AspNetCore.Builder`). Remove `using Microsoft.Extensions.Hosting;` if unused (check: `ValidateOnStart()` may require it — verify; if `ImplicitUsings=enable` + Web SDK covers it, remove; if not, keep).
   - Add `public partial class Program { }` at the end of `Program.cs` (for `WebApplicationFactory<Program>`).
   - Add `app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));` before `app.Run(...)`.
   - Run `dotnet build` — the draft was NEVER built; this is the first compilation check. Fix any compile errors.

2. **Replace StdioSmokeTests → HttpSmokeTests (M10.5 — Tests):**
   - Delete `tests/McpWebSearchService.Tests/StdioSmokeTests.cs`.
   - Create `tests/McpWebSearchService.Tests/Smoke/HttpSmokeTests.cs`:
     - `public sealed class HttpSmokeTests : IClassFixture<WebApplicationFactory<Program>>`.
     - Constructor: `_client = factory.CreateClient();` + set `Accept: application/json, text/event-stream`.
     - `Initialize_Handshake_Succeeds`: POST JSON-RPC `initialize` to `/mcp` → parse the response (JSON or SSE) → assert `protocolVersion` + `serverInfo`. Extract the `Mcp-Session-Id` header.
     - `ToolsList_ReturnsTwoToolsWithDescriptions`: POST JSON-RPC `tools/list` to `/mcp` with `Mcp-Session-Id` → assert 2 tools with non-empty descriptions.
   - Add `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.11" />` to the test csproj.
   - Update the `IntegrationTests.HardConstraints_Verified` doc comment: remove the "AOT (PublishAot=true)" mention → "source-gen JsonSerializerContext (no Newtonsoft)". Assertions unchanged (AOT-independent).

3. **Rewrite the Dockerfile (M10.3 — Container):**
   - Build stage: `mcr.microsoft.com/dotnet/sdk:10.0` (NOT `sdk:10.0-aot`). Publish: `dotnet publish src/McpWebSearchService/McpWebSearchService.csproj -c Release -o /app/publish /p:SelfContained=false /p:PublishSingleFile=false` (NO `/p:PublishAot=true`).
   - Final stage: `mcr.microsoft.com/dotnet/aspnet:10.0` (NOT `debian:bookworm-slim` + manual apt-get). Install `curl` for the HEALTHCHECK. `EXPOSE 8080`. `HEALTHCHECK CMD curl -f http://localhost:8080/healthz || exit 1`. `ENTRYPOINT ["dotnet", "McpWebSearchService.dll"]`.

4. **Update the .mcp/server.json manifest (M10.4 — Manifest):**
   - Replace the `packages[]` stdio transport with `remotes[]` streamable-http: `{ "type": "streamable-http", "url": "http://localhost:8080/mcp" }`.

5. **Build + test:**
   - `dotnet build` — confirm compilation (the draft was never built).
   - `dotnet test` — confirm the M2–M9 regression + M10 HTTP-smoke tests pass. No AOT publish needed (ADR-002 superseded — `dotnet publish -r win-x64 /p:PublishAot=true` is NO LONGER REQUIRED).

6. **(Operator-gated) Docker E2E:**
   - `docker build -t mcp-web-search .` → `docker run -d -p 8080:8080 mcp-web-search` → `curl http://localhost:8080/healthz` → 200 OK. MCP `initialize` + `tools/list` via HTTP POST → valid JSON-RPC.

7. **Final @qa verification** — read-only, semantic log audit, SUCCESS or BLOCK.

---

## 3. Acceptance Criteria (M10)

- [ ] **AC1:** `src/McpWebSearchService/McpWebSearchService.csproj`: SDK `Microsoft.NET.Sdk.Web`, `PublishAot=false`, `ModelContextProtocol.AspNetCore 1.2.0`. `InvariantGlobalization=true` + `EnableConfigurationBindingGenerator=true` kept (ADR-003/006 conscious carry-forward).
- [ ] **AC2:** `Program.cs`: `WebApplication.CreateBuilder` + `WithHttpTransport` + `MapMcp` + `MapGet("/healthz")` + `public partial class Program {}`. Port/Path from config. All M2–M9 DI registrations preserved.
- [ ] **AC3:** `McpServerSettings`: `Port` (int, default 8080, validation 0<Port≤65535) + `Path` (string, default "/mcp", non-empty). `appsettings.json` contains `McpServer.Port` + `McpServer.Path`.
- [ ] **AC4:** `dotnet build` — PASS (the draft was NEVER built; first compilation check). `dotnet test` — PASS (M2–M9 regression + M10 HTTP-smoke). No AOT publish required (ADR-002 superseded).
- [ ] **AC5:** `HttpSmokeTests.cs` (replaces `StdioSmokeTests.cs`): `WebApplicationFactory<Program>` + POST JSON-RPC to `/mcp` → (a) `Initialize_Handshake_Succeeds` returns a valid JSON-RPC response with `protocolVersion` + `serverInfo`; (b) `ToolsList_ReturnsTwoToolsWithDescriptions` returns exactly `web_search` + `fetch_and_extract` with non-empty descriptions (ADR-028). Test csproj has `Microsoft.AspNetCore.Mvc.Testing 10.0.11`.
- [ ] **AC6:** `IntegrationTests.HardConstraints_Verified` — doc comment updated (remove the AOT mention); assertions unchanged (AOT-independent: types/attributes/JsonSerializerContext/no-Newtonsoft/CB/pipeline/DTO). All M8/M9 integration tests pass.
- [ ] **AC7:** `Dockerfile`: JIT build (`sdk:10.0`, NOT `sdk:10.0-aot`; framework-dependent publish `/p:SelfContained=false /p:PublishSingleFile=false`, NO `/p:PublishAot=true`) + `aspnet:10.0` runtime (NOT `debian:bookworm-slim`) + `EXPOSE 8080` + `HEALTHCHECK` probing `/healthz` + `ENTRYPOINT ["dotnet", "McpWebSearchService.dll"]`.
- [ ] **AC8:** `.mcp/server.json`: `remotes[].type == "streamable-http"`, `remotes[].url == "http://localhost:8080/mcp"` (NOT `packages[].transport` stdio).
- [ ] **AC9:** ADR-033 documented in `AGENTS.md` (Status/Context/Decision/Consequence/Verify). Superseded ADRs (ADR-002, 006, 007, 018) explicitly marked. SPEC §5.1 AOT constraint cancelled/replaced.
- [ ] **AC10:** `DevelopmentPlan.md` M10 section appended. `AppGraph.xml` updated (Program.cs HTTP transport, StdioSmokeTests → HttpSmokeTests, Dockerfile JIT, McpServerSettings Port/Path, mcp_server.json streamable-http). `tests/test_guide.md` M10 section appended.
- [ ] **AC11:** (Operator-gated) `docker run -d -p 8080:8080 mcp-web-search` → HTTP service starts; `curl http://localhost:8080/healthz` → 200 OK; MCP `initialize` + `tools/list` via HTTP POST → valid JSON-RPC.
- [ ] **AC12:** @qa semantic verification: logs match contracts (LDD `[IMP:7-10]` markers on key paths), HTTP-smoke verifies the real MCP handshake + tools/list. SUCCESS or BLOCK in `tests/qa_report.md`.

---

## 4. Risks and mitigations (M10)

| Risk | Mitigation |
|---|---|
| Draft Program.cs never compiled — compile errors possible | AC4: `dotnet build` is the FIRST check. @code fixes compile errors (missing namespaces, unused usings, API mismatches). The draft is close but unverified. |
| `WebApplicationFactory<Program>` content root wrong → appsettings.json not found | @code may need `factory.WithWebHostBuilder(b => b.UseContentRoot(path))`. If the main project's `appsettings.json` is not found, options validation fails (OptionsValidationException). Verify the content root. |
| MCP streamable HTTP protocol specifics (session ID header name, SSE vs JSON response) | @code verifies by testing against the running server. The `initialize` response may be SSE (`text/event-stream`) with `data:` lines containing JSON-RPC. Read the body as a string, parse accordingly. `Mcp-Session-Id` header name — verify via response headers. |
| The `aspnet:10.0` image doesn't have `curl` for the HEALTHCHECK | The Dockerfile installs `curl` via `apt-get install -y --no-install-recommends curl`. Verified: `aspnet:10.0` is based on `debian:bookworm-slim` — `apt-get` available. |
| Framework-dependent publish with csproj `SelfContained=true` → need override | The Dockerfile publish command includes `/p:SelfContained=false /p:PublishSingleFile=false` to override the csproj defaults. @code verifies the publish output is framework-dependent (no runtime bundled). |
| opencode may not accept `remotes[]` in `.mcp/server.json` | @code verifies opencode accepts the `remotes[]` format. If not, fall back to `packages[].transport.type = "streamable-http"` with a `url` field. The MCP schema supports both. |
| Regression of M2–M9 tests under JIT (source-gen assumptions break) | Source-gen `JsonSerializerContext` works under JIT (compile-time code gen → JIT-compiled at runtime). No AOT-specific assumptions break. `dotnet test` confirms the regression. |
| GREEN TEST TRAP — HTTP-smoke tests pass but don't verify the real MCP protocol | AC12: @qa verifies the HTTP-smoke tests POST real JSON-RPC (initialize + tools/list) and parse real responses — not just HTTP 200. Semantic verification of response content. |

---

## 5. Delegation to @code (M10)

**M10 requires an `@code` dispatch** to finish/verify the partial edits, replace the tests, and rewrite the Dockerfile + manifest:

### Files created/modified by @code:

1. **`Program.cs`** (MODIFY — verify/adapt the draft + add 2 lines): confirm `WebApplication.CreateBuilder` + `WithHttpTransport` + `MapMcp` compile; add `public partial class Program { }` at the end; add `app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));` before `app.Run(...)`. Clean up the unused `using Microsoft.Extensions.Hosting;` if the build succeeds without it. `csharp-conventions` apply (already followed in the draft).
2. **`tests/McpWebSearchService.Tests/StdioSmokeTests.cs`** (DELETE): replaced by HttpSmokeTests.
3. **`tests/McpWebSearchService.Tests/Smoke/HttpSmokeTests.cs`** (NEW): `WebApplicationFactory<Program>` + 2 HTTP-smoke tests (initialize + tools/list). `csharp-conventions` apply.
4. **`tests/McpWebSearchService.Tests/McpWebSearchService.Tests.csproj`** (MODIFY): add `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.11" />`.
5. **`tests/McpWebSearchService.Tests/Integration/IntegrationTests.cs`** (MODIFY — doc comment only): update the `HardConstraints_Verified` doc comment (remove the AOT mention). Assertions unchanged.
6. **`Dockerfile`** (REWRITE): JIT build + aspnet:10.0 runtime + EXPOSE 8080 + HEALTHCHECK + curl + ENTRYPOINT dotnet (see §0.4).
7. **`.mcp/server.json`** (REWRITE): `remotes[]` streamable-http (see §0.5).
8. **`tests/test_guide.md`** (MODIFY — appended by @architect, @code may adjust).

### Constraints for @code:

- **The `csharp-conventions` skill APPLIES** — all new/modified `.cs` files follow `#region`, XML docs, GREP_SUMMARY, STRUCTURE, LDD markers.
- **Do NOT modify** the M2–M9 `.cs` files with business logic (SearXNGClient, SearchResultProcessor, WebSearchTools, CircuitBreakerState, etc.) — they work under JIT unchanged. The only exception: `Program.cs` (M10 rewrite) + `IntegrationTests.cs` (doc comment only).
- **Do NOT run `dotnet publish -r win-x64 /p:PublishAot=true`** — AOT verification is NO LONGER REQUIRED (ADR-002 superseded). Verification under JIT: `dotnet build` + `dotnet test`.
- **Docker build/run** — operator-gated (`docker` is NOT auto-allowed in opencode.json). @code does not run `docker build`; the operator does it manually (AC11).
- **Anti-Loop Protocol**: `.test_counter.json` is reset by the orchestrator before M10 starts.
- **`HttpSmokeTests` protocol**: @code verifies the exact MCP streamable HTTP protocol (header names, SSE vs JSON response) by testing against the running server or by inspecting the `ModelContextProtocol.AspNetCore` package source. The test must POST real JSON-RPC and parse real responses — not just check HTTP 200.

### Implementation order (recommended):

1. `Program.cs` — verify/adapt the draft, add `public partial class Program {}` + the `/healthz` endpoint. Run `dotnet build` — first compilation check.
2. Test csproj — add `Microsoft.AspNetCore.Mvc.Testing 10.0.11`.
3. Delete `StdioSmokeTests.cs`, create `HttpSmokeTests.cs`.
4. Update the `IntegrationTests.HardConstraints_Verified` doc comment.
5. `dotnet build` + `dotnet test` — full regression (M2–M9 + M10 HTTP-smoke).
6. `Dockerfile` — rewrite for JIT + aspnet:10.0.
7. `.mcp/server.json` — rewrite for streamable-http `remotes[]`.
8. (Operator-gated) `docker build` + `docker run` + `curl /healthz` + MCP HTTP handshake.
