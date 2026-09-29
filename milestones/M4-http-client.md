# M4 — SearXNG HTTP client

**Stage:** M4
**Predecessors:** M2, M3
**Successors:** M6
**Can run in parallel with:** M5
**Requirement source:** `SPEC.md` §4.2, §6.1 (step 3)

## Goal

Implement the typed HTTP client `SearXNGClient` via `IHttpClientFactory` with a 15-second timeout and a
Circuit Breaker (Polly or a manual implementation), returning a clear "search service temporarily
unavailable" error to the LLM when SearXNG is down.

## Context and constraints

- `IHttpClientFactory` + typed client `SearXNGClient` with a `SearchAsync(SearchRequest, CancellationToken)` method.
- **15-second timeout** (SPEC §4.2) — at the `HttpClient.Timeout` level or via a Polly timeout.
- Circuit Breaker (Polly or manual): fail fast if SearXNG is down, without hanging the LLM.
- When unavailable, the MCP tool returns a clear error for the LLM: **"Search service temporarily unavailable"** (the exact string from SPEC §4.2).
- Uses the M3 DTOs (`SearchRequest`, `SearXNGResponse`) and `McpJsonContext` for deserialization.
- `BaseUrl` comes from `IOptions<SearXNGSettings>` (M2).
- AOT safety: Polly v8 (AOT-compatible) or a manual breaker without reflection.

## Draft Code Graph

```xml
<DraftCodeGraph>
  <Services_ISearXNGClient_cs FILE="Services/ISearXNGClient.cs" TYPE="INTERFACE">
    <annotation>Contract of the typed HTTP client.</annotation>
    <ISearXNGClient_INTERFACE NAME="ISearXNGClient" TYPE="INTERFACE">
      <ISearXNGClient_SearchAsync_METHOD NAME="SearchAsync" TYPE="IS_METHOD_OF_INTERFACE">
        <annotation>Executes a request to SearXNG and returns the results.</annotation>
      </ISearXNGClient_SearchAsync_METHOD>
    </ISearXNGClient_INTERFACE>
  </Services_ISearXNGClient_cs>

  <Services_SearXNGClient_cs FILE="Services/SearXNGClient.cs" TYPE="SERVICE">
    <annotation>Typed HTTP client: 15s timeout, Circuit Breaker, JSON via McpJsonContext.</annotation>
    <SearXNGClient_CLASS NAME="SearXNGClient" TYPE="CLASS" IMPLEMENTS="ISearXNGClient">
      <SearXNGClient_SearchAsync_METHOD NAME="SearchAsync" TYPE="IS_METHOD_OF_CLASS">
        <CrossLinks>
          <Link TARGET="Models_SearXNGResponse_cs" TYPE="CONSUMES_DTO" />
          <Link TARGET="Serialization_McpJsonContext_cs" TYPE="USES_SERIALIZER" />
          <Link TARGET="Configuration_SearXNGSettings_cs" TYPE="READS_CONFIG" />
        </CrossLinks>
      </SearXNGClient_SearchAsync_METHOD>
    </SearXNGClient_CLASS>
  </Services_SearXNGClient_cs>

  <Services_SearXNGUnavailableException_cs FILE="Services/SearXNGUnavailableException.cs" TYPE="EXCEPTION">
    <annotation>Domain error for SearXNG unavailability; mapped to the message for the LLM.</annotation>
    <SearXNGUnavailableException_CLASS NAME="SearXNGUnavailableException" TYPE="EXCEPTION" />
  </Services_SearXNGUnavailableException_cs>
</DraftCodeGraph>
```

## Step-by-step Data Flow

1. Create `ISearXNGClient` with a `SearchAsync(SearchRequest, CancellationToken): Task<IReadOnlyList<SearXNGResult>>` method (or `SearXNGResponse`).
2. Implement `SearXNGClient`:
   - Constructor: `HttpClient`, `IOptions<SearXNGSettings>`, `ILogger<SearXNGClient>`.
   - `HttpClient.Timeout = TimeSpan.FromSeconds(15)` (or a Polly timeout policy).
   - `SearchAsync`: build the URL from `BaseUrl` + query parameters (`q`, `categories`, `time_range`, `language`, `format=json`), send a GET, deserialize via `McpJsonContext`.
3. Implement the Circuit Breaker: Polly v8 (`AddResilienceHandler`) or a manual consecutive-failure counter → when the breaker is open, throw `SearXNGUnavailableException` immediately.
4. Register in DI: `services.AddHttpClient<ISearXNGClient, SearXNGClient>(...)` + the resilience handler.
5. At the MCP tool call site (M6), `SearXNGUnavailableException` is mapped to the response string "Search service temporarily unavailable" (not propagated as an exception to the LLM).

## Acceptance Criteria

- [x] `SearXNGClient` implements `ISearXNGClient` and is registered via `AddHttpClient<,>`. *(verified M4 — `Services/ISearXNGClient.cs`: interface with `SearchAsync(SearchRequest, CancellationToken): Task<IReadOnlyList<SearXNGResult>>`; `Services/SearXNGClient.cs`: class implementing ISearXNGClient; DI: `builder.Services.AddHttpClient<ISearXNGClient, SearXNGClient>()` (ADR-008))*
- [x] The 15-second timeout is enforced (test with a mock HTTP handler, slow response → timeout within ≤15s). *(verified M4 — `SearXNGClient.cs`: `HttpClient.Timeout = TimeSpan.FromSeconds(15)` (lines 70–72); test: slow response → TaskCanceledException wrapped as SearXNGUnavailableException)*
- [x] Circuit Breaker: after N consecutive failures (configurable threshold), subsequent calls fail immediately with `SearXNGUnavailableException` without hitting the network. *(verified M4 — ADR-007: `Services/CircuitBreakerState.cs`: manual CB, no Polly; `FailureThreshold=3`, `OpenDuration=30s`; 3 states: Closed/Open/HalfOpen; lock-based thread safety (ADR-007))*
- [x] On timeout/unavailability, a domain error is returned rather than a raw `HttpRequestException` escaping to the LLM. *(verified M4 — `Services/SearXNGUnavailableException.cs`: domain exception with `DefaultMessage = "Search service temporarily unavailable"` (SPEC §4.2); wraps HttpRequestException/TaskCanceledException with the inner exception preserved for logging)*
- [x] The SearXNG response is deserialized via `McpJsonContext` (not reflection). *(verified M4 — `SearXNGClient.SearchAsync`: `JsonSerializer.Deserialize<SearXNGResponse>(responseStream, McpJsonContext.Default.SearXNGResponse)`)*
- [x] Unit tests (xUnit): successful search (mock handler), timeout, Circuit Breaker open/closed/half-open, empty SearXNG response. *(verified M4 — 7 tests in `tests/McpWebSearchService.Tests/SearXNGClientTests.cs`: circuit breaker lifecycle (closed→open at threshold, half-open probe after recovery, open rejection), BuildUrl params, HTTP error wrapping (4xx/5xx → SearXNGUnavailableException), timeout wrapping)*
- [x] LDD logging: `[IMP:1-10]` with mandatory `[IMP:9-10]` for Belief State (success/unavailability). *(verified M4 — `SearXNGClient.cs`: [IMP:1][START], [IMP:7] stages, [IMP:9] belief state, [IMP:10] COMPLETE; per DevelopmentPlan §0.10)*
- [x] AOT publish without warnings (Polly v8 is AOT-compatible, or a manual breaker without reflection). *(verified M4 — manual CB (ADR-007) = 0 × IL####; `dotnet publish -c Release -r win-x64 /p:PublishAot=true`: clean)*
- [x] Code follows `csharp-conventions`. *(verified M4 — all 4 files (`ISearXNGClient.cs`, `SearXNGClient.cs`, `CircuitBreakerState.cs`, `SearXNGUnavailableException.cs`) have #region MODULE_CONTRACT, XML docs with [PURPOSE]/[INVARIANTS]/[RATIONALE]/[CHANGES])*

## Risks

- Polly v7 may emit AOT warnings → use Polly v8 or a manual breaker.
- SearXNG requires `format=json` in the query — forgetting it yields HTML; cover with a test using a real response shape.
- `HttpClient.Timeout` vs Polly timeout: pick a single source of truth, do not duplicate.
