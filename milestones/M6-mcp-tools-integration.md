# M6 — MCP tools integration

**Stage:** M6
**Predecessors:** M4, M5
**Successors:** M7, M8
**Requirement source:** `SPEC.md` §2, §6.1 (step 4)

## Goal

Wire everything together: implement the `WebSearchTools` class with `[McpServerToolType]` /
`[McpServerTool]` attributes, exposing two tools — `web_search` and `fetch_and_extract` — and connect the
HTTP client (M4) with the post-processing pipeline (M5) into a single call chain.

## Context and constraints

- MCP SDK: declaration via `[McpServerToolType]` on the class and `[McpServerTool]` on the methods.
- **`web_search`** (SPEC §2.1):
  - parameters: `query` (string, required), `categories` (string, opt, default `'general'`),
    `time_range` (string, opt), `language` (string, opt, default from config);
  - return: a JSON array of `SearchResultDto` (`title`, `url`, `snippet`, `source_engine`).
- **`fetch_and_extract`** (SPEC §2.2, optional):
  - parameter: `url` (string, required);
  - return: cleaned page text (limit ~5000 tokens/characters).
- On `SearXNGUnavailableException` (from M4), the tool returns the string **"Search service temporarily unavailable"** — it does NOT propagate the exception to the LLM (SPEC §4.2).
- Result serialization — via `McpJsonContext` (M3).
- MCP server registration in `Program.cs`: `services.AddMcpServer()` + tools (per the M1 template).

## Draft Code Graph

```xml
<DraftCodeGraph>
  <Tools_WebSearchTools_cs FILE="Tools/WebSearchTools.cs" TYPE="MCP_TOOLS">
    <annotation>[McpServerToolType]; web_search + fetch_and_extract.</annotation>
    <WebSearchTools_CLASS NAME="WebSearchTools" TYPE="CLASS" ATTRIBUTE="McpServerToolType">
      <WebSearchTools_WebSearch_METHOD NAME="WebSearch" TYPE="IS_METHOD_OF_CLASS" ATTRIBUTE="McpServerTool">
        <annotation>web_search: query/categories/time_range/language → SearchResultDto[].</annotation>
        <CrossLinks>
          <Link TARGET="Services_ISearXNGClient_cs" TYPE="CALLS_METHOD" />
          <Link TARGET="Services_ISearchResultProcessor_cs" TYPE="CALLS_METHOD" />
          <Link TARGET="Serialization_McpJsonContext_cs" TYPE="USES_SERIALIZER" />
        </CrossLinks>
      </WebSearchTools_WebSearch_METHOD>
      <WebSearchTools_FetchAndExtract_METHOD NAME="FetchAndExtract" TYPE="IS_METHOD_OF_CLASS" ATTRIBUTE="McpServerTool">
        <annotation>fetch_and_extract: url → cleaned text (≤5000 chars).</annotation>
        <CrossLinks>
          <Link TARGET="Services_ISearXNGClient_cs" TYPE="CALLS_METHOD" />
        </CrossLinks>
      </WebSearchTools_FetchAndExtract_METHOD>
    </WebSearchTools_CLASS>
  </Tools_WebSearchTools_cs>

  <Program_cs FILE="Program.cs" TYPE="ENTRYPOINT">
    <annotation>Wiring: AddMcpServer + SearXNGClient + SearchResultProcessor + Options.</annotation>
    <Program_Main_METHOD NAME="Main" TYPE="IS_METHOD_OF_CLASS">
      <CrossLinks>
        <Link TARGET="Tools_WebSearchTools_cs" TYPE="REGISTERS_TOOL" />
        <Link TARGET="Services_ISearXNGClient_cs" TYPE="REGISTERS_SERVICE" />
        <Link TARGET="Services_ISearchResultProcessor_cs" TYPE="REGISTERS_SERVICE" />
        <Link TARGET="Configuration_SearXNGSettings_cs" TYPE="BINDS_CONFIG" />
      </CrossLinks>
    </Program_Main_METHOD>
  </Program_cs>
</DraftCodeGraph>
```

## Step-by-step Data Flow

1. Create `WebSearchTools` and mark it `[McpServerToolType]`.
2. Implement `WebSearch(query, categories, time_range, language)`:
   - build a `SearchRequest` (default `categories="general"`, `language` from `SearXNGSettings.DefaultLanguage` if not set);
   - call `ISearXNGClient.SearchAsync` → `SearXNGResult[]`;
   - run it through `ISearchResultProcessor.Process` → `SearchResultDto[]`;
   - return via the MCP SDK (serialization via `McpJsonContext`);
   - `catch (SearXNGUnavailableException)` → return the string "Search service temporarily unavailable".
3. Implement `FetchAndExtract(url)`:
   - fetch the URL (via `HttpClient`/`IHttpClientFactory` or a `SearXNGClient` method), extract plain text, truncate to ~5000 chars;
   - load errors → a clear message for the LLM.
4. In `Program.cs`, wire all services: `AddMcpServer()` + tool registration + `AddHttpClient<ISearXNGClient, SearXNGClient>` (from M4) + `AddSingleton<ISearchResultProcessor, SearchResultProcessor>` (from M5) + `Configure<SearXNGSettings>`/`Configure<McpServerSettings>` (from M2).
5. Unit tests with mocks of `ISearXNGClient` and `ISearchResultProcessor`: verify parameter mapping, unavailability handling, and result shape.

## Acceptance Criteria

- [x] `WebSearchTools` is marked `[McpServerToolType]`; the `WebSearch` and `FetchAndExtract` methods are `[McpServerTool]`.
- [x] `web_search` accepts `query` (required), `categories`, `time_range`, `language`; returns a JSON array of `SearchResultDto`.
- [x] `fetch_and_extract` accepts `url`; returns cleaned text (≤5000 chars).
- [x] On `SearXNGUnavailableException`, `web_search` returns the string "Search service temporarily unavailable" (does not propagate the exception).
- [x] `language` defaults to `SearXNGSettings.DefaultLanguage`; `categories` defaults to `"general"`.
- [x] `Program.cs` registers: MCP server + tools + `ISearXNGClient` (with resilience) + `ISearchResultProcessor` + Options.
- [x] The service starts (`dotnet run`) and answers the `web_search` MCP call (verified via stdio/HTTP per the M1 template).
- [x] Unit tests (xUnit): mock `ISearXNGClient`+`ISearchResultProcessor` — success, unavailability, empty result, parameter mapping.
- [x] LDD: `[IMP:9-10]` at the key stages (client call, processing, return; belief state on unavailability).
- [x] AOT publish without warnings.
- [x] Code follows `csharp-conventions`.

## Risks

- MCP SDK: the exact tool registration form depends on the M1 template — follow the template, do not deviate.
- `fetch_and_extract` is marked optional in SPEC; if time/complexity is high, implement it minimally (HTTP GET + simple text extraction) but do not remove the tool entirely.
- MCP response serialization: ensure `McpJsonContext` is used, not the SDK's reflection path (verify with an AOT publish).
