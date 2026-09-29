# M8 — E2E testing and acceptance

**Stage:** M8
**Predecessors:** M6, M7
**Successors:** —
**Requirement source:** `SPEC.md` §6.1 (step 8), general acceptance criteria for the whole system

## Goal

Final verification of the entire system as a whole: running in Docker, MCP interaction over stdio/HTTP,
correct handling of SearXNG timeouts/unavailability, and conformance to all hard constraints from
`AGENTS.md` and `SPEC.md`.

## Context and constraints

- Integration/E2E tests on top of the assembled system (M6 + M7), not component unit tests (those were written in M2–M6).
- Verification of the SPEC §1 topology: `LLM Agent <-> MCP Service (stdio/HTTP) <-> SearXNG (HTTP)`.
- SearXNG in E2E — a mock or container (does not depend on an external instance, for reproducibility).
- Bash permissions: `docker` requires operator confirmation; `dotnet test*` is auto-allowed.
- This is the acceptance stage for `@qa` (read-only, veto right). `@code` implements the E2E tests and `tests/test_guide.md`.

## Draft Code Graph

```xml
<DraftCodeGraph>
  <Tests_E2E_cs FILE="Tests/E2E/McpServerE2E.cs" TYPE="E2E_TEST">
    <annotation>Start the MCP server, exchange over stdio/HTTP, search scenarios.</annotation>
    <McpServerE2E_CLASS NAME="McpServerE2E" TYPE="TEST_CLASS">
      <McpServerE2E_WebSearch_ReturnsResults_METHOD NAME="WebSearch_ReturnsResults" TYPE="IS_METHOD_OF_CLASS" />
      <McpServerE2E_WebSearch_SearXNGUnavailable_METHOD NAME="WebSearch_SearXNGUnavailable" TYPE="IS_METHOD_OF_CLASS" />
      <McpServerE2E_FetchAndExtract_ReturnsText_METHOD NAME="FetchAndExtract_ReturnsText" TYPE="IS_METHOD_OF_CLASS" />
      <McpServerE2E_TimeoutHandling_METHOD NAME="TimeoutHandling" TYPE="IS_METHOD_OF_CLASS" />
    </McpServerE2E_CLASS>
  </Tests_E2E_cs>

  <Tests_E2E_SearXNGMock_cs FILE="Tests/E2E/SearXNGMock.cs" TYPE="TEST_FIXTURE">
    <annotation>HTTP mock of SearXNG for reproducible E2E scenarios.</annotation>
    <SearXNGMock_CLASS NAME="SearXNGMock" TYPE="TEST_FIXTURE" />
  </Tests_E2E_SearXNGMock_cs>

  <Tests_test_guide_md FILE="Tests/test_guide.md" TYPE="QA_GUIDE">
    <annotation>Guide for @qa: key inputs, expected [IMP:7-10] LDD markers, acceptance-criteria mapping.</annotation>
  </Tests_test_guide_md>
</DraftCodeGraph>
```

## Step-by-step Data Flow

1. Prepare `SearXNGMock` — an HTTP server / `HttpMessageHandler` mock returning fixed SearXNG JSON responses (success, empty, delay >15s, 500, connection refused).
2. Implement the E2E tests (`McpServerE2E`):
   - `WebSearch_ReturnsResults`: mock SearXNG → MCP `web_search` call → correct `SearchResultDto[]` (passing through the M5 pipeline).
   - `WebSearch_SearXNGUnavailable`: the mock returns a failure → MCP response "Search service temporarily unavailable" (SPEC §4.2).
   - `FetchAndExtract_ReturnsText`: fetch a URL → cleaned text ≤5000 chars.
   - `TimeoutHandling`: a mock with a delay >15s → the 15s timeout fires → a clear error to the LLM, no hang.
3. Prepare `Tests/test_guide.md` for `@qa`: key inputs, expected `[IMP:7-10]` LDD markers, and mapping to the acceptance criteria from `DevelopmentPlan.md`.
4. (With the operator) Docker E2E: `docker build` + `docker run` with `SearXNG:BaseUrl` pointed at the mock — verify startup and the MCP exchange.
5. Final AOT verification inside the Docker image: run the native binary without the .NET runtime.

## Acceptance Criteria

- [ ] E2E test `WebSearch_ReturnsResults`: the MCP call returns the expected `SearchResultDto[]` (the M5 post-processing is applied — dedup/filter are visible in the result).
- [ ] E2E test `WebSearch_SearXNGUnavailable`: the MCP response is the string "Search service temporarily unavailable" (not an exception).
- [ ] E2E test `FetchAndExtract_ReturnsText`: returns cleaned text ≤5000 chars.
- [ ] E2E test `TimeoutHandling`: on a >15s delay — the timeout fires within ≤15s, the LLM receives a clear error and does not hang.
- [ ] `SearXNGMock` covers the scenarios: success, empty response, delay, 5xx, connection refused.
- [ ] `Tests/test_guide.md` exists and contains: key inputs, expected `[IMP:7-10]` markers, acceptance-criteria mapping.
- [ ] (With the operator) `docker build` + `docker run` — the MCP server starts in the AOT image and answers an MCP request (stdio/HTTP interaction).
- [ ] Full `dotnet test` run — 100% PASS (unit from M2–M6 + E2E from M8).
- [ ] LDD telemetry: the logs contain `[IMP:7-10]` Belief State markers on all key paths (search, unavailability, timeout).
- [ ] `@qa` semantic verification: the logs match the contracts (not just green tests) — a `SUCCESS` or `BLOCK` verdict.
- [ ] All hard constraints from `AGENTS.md` are confirmed: .NET 10, AOT (no warnings), `JsonSerializerContext` (no Newtonsoft), 15s timeout, Circuit Breaker, the Normalize→Dedup→Filter pipeline, `[McpServerToolType]`/`[McpServerTool]`.

## Risks

- MCP exchange over stdio: testing requires launching a process and exchanging JSON-RPC — may need a framework/helper; if complex, limit to the HTTP transport if the M1 template supports it.
- Docker E2E requires operator confirmation (`docker` is not auto-allowed) — plan for the time; the AOT build and the unit part can be verified without Docker.
- A timeout test with a real 15s delay slows down the suite — use a time-controlled mock or mark it as slow and run it separately.
- GREEN TEST TRAP (from `mode-debug`): `@qa` must verify that the logs semantically match the contracts, not just that tests are green — this is the exit criterion for the stage.
