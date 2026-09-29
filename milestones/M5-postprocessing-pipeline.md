# M5 — Search result post-processing pipeline

**Stage:** M5
**Predecessors:** M2, M3
**Successors:** M6
**Can run in parallel with:** M4
**Requirement source:** `SPEC.md` §3, §6.1 (step 5)

## Goal

Implement `SearchResultProcessor`, which post-processes SearXNG results **before** returning them to the
LLM (to save context window): URL normalization → deduplication (exact + fuzzy) → filtering (blocklist +
empty snippets + limit).

## Context and constraints

- Pipeline order (SPEC §3, §6.1): `NormalizeUrl → Deduplicate → Filter`.
- **URL normalization** (SPEC §3.1.1):
  - lowercase the host;
  - remove the standard port (80/443);
  - remove tracking parameters (`utm_*`, `gclid`, `fbclid`, etc.);
  - remove the fragment (`#section`);
  - strip the trailing slash in the path.
- **Exact-match dedup** (SPEC §3.1.2): group by normalized URL; keep the result from the priority engine
  (Google > Bing > Yandex) or the one with the most complete snippet. `HashSet<string>` for O(1).
- **Fuzzy title match** (SPEC §3.1.3): title similarity ≥90% (Levenshtein or Jaccard); across different
  domains (mirrors) — keep the first found.
- **Filtering** (SPEC §3.2):
  - blocklist of domains from `SearXNGSettings.BlockedDomains`;
  - empty/short snippet (`content` < 20 chars) — drop;
  - `Take(N)` where N = `SearXNGSettings.MaxResults`.
- **Context pollution** (SPEC §4.3): snippets are plain text (strip HTML/scripts), truncated to 200–300 chars.
- DTOs from M3: `SearXNGResult` → `SearchResultDto`.

## Draft Code Graph

```xml
<DraftCodeGraph>
  <Services_ISearchResultProcessor_cs FILE="Services/ISearchResultProcessor.cs" TYPE="INTERFACE">
    <annotation>Contract of the post-processing pipeline.</annotation>
    <ISearchResultProcessor_INTERFACE NAME="ISearchResultProcessor" TYPE="INTERFACE">
      <ISearchResultProcessor_Process_METHOD NAME="Process" TYPE="IS_METHOD_OF_INTERFACE">
        <annotation>SearXNGResult[] → SearchResultDto[] (Normalize → Dedup → Filter).</annotation>
      </ISearchResultProcessor_Process_METHOD>
    </ISearchResultProcessor_INTERFACE>
  </Services_ISearchResultProcessor_cs>

  <Services_SearchResultProcessor_cs FILE="Services/SearchResultProcessor.cs" TYPE="SERVICE">
    <annotation>Pipeline NormalizeUrl → Deduplicate → Filter; HashSet O(1).</annotation>
    <SearchResultProcessor_CLASS NAME="SearchResultProcessor" TYPE="CLASS" IMPLEMENTS="ISearchResultProcessor">
      <SearchResultProcessor_NormalizeUrl_METHOD NAME="NormalizeUrl" TYPE="IS_METHOD_OF_CLASS">
        <annotation>Host lowercase, standard port, UTM/tracking, fragment, trailing slash.</annotation>
        <CrossLinks>
          <Link TARGET="SearchResultProcessor_Deduplicate_METHOD" TYPE="CALLS_METHOD" />
        </CrossLinks>
      </SearchResultProcessor_NormalizeUrl_METHOD>
      <SearchResultProcessor_Deduplicate_METHOD NAME="Deduplicate" TYPE="IS_METHOD_OF_CLASS">
        <annotation>Exact by normalized URL (engine priority / full snippet) + fuzzy ≥90% by titles.</annotation>
        <CrossLinks>
          <Link TARGET="SearchResultProcessor_Filter_METHOD" TYPE="CALLS_METHOD" />
        </CrossLinks>
      </SearchResultProcessor_Deduplicate_METHOD>
      <SearchResultProcessor_Filter_METHOD NAME="Filter" TYPE="IS_METHOD_OF_CLASS">
        <annotation>Blocklist + empty snippet (&lt;20) + Take(MaxResults).</annotation>
        <CrossLinks>
          <Link TARGET="Configuration_SearXNGSettings_cs" TYPE="READS_CONFIG" />
        </CrossLinks>
      </SearchResultProcessor_Filter_METHOD>
      <SearchResultProcessor_StripHtml_METHOD NAME="StripHtml" TYPE="IS_METHOD_OF_CLASS">
        <annotation>Plain text: strip HTML/scripts, truncate to 200–300 chars.</annotation>
      </SearchResultProcessor_StripHtml_METHOD>
    </SearchResultProcessor_CLASS>
  </Services_SearchResultProcessor_cs>

  <Services_TitleSimilarity_cs FILE="Services/TitleSimilarity.cs" TYPE="UTILITY">
    <annotation>Fuzzy title match: Levenshtein/Jaccard, threshold ≥90%.</annotation>
    <TitleSimilarity_CLASS NAME="TitleSimilarity" TYPE="CLASS" />
  </Services_TitleSimilarity_cs>
</DraftCodeGraph>
```

## Step-by-step Data Flow

1. `NormalizeUrl(string url): string` — implement all 5 normalization rules; output a canonical URL key.
2. `Deduplicate(IEnumerable<SearXNGResult>)`:
   - exact: `Dictionary<string, SearXNGResult>` / `HashSet<string>` by normalized URL; on collision —
     engine priority (the defined order Google>Bing>Yandex>…) or the longest snippet;
   - fuzzy: after exact — compare titles (`TitleSimilarity.Similarity ≥ 0.9`) for results with
     **different** domains; keep the first found.
3. `Filter(IEnumerable<SearchResultDto>)`:
   - drop domains from `SearXNGSettings.BlockedDomains`;
   - drop snippets <20 chars (after `StripHtml`);
   - `Take(MaxResults)`.
4. `StripHtml(string snippet): string` — remove tags/scripts, truncate to 200–300 chars.
5. `Process(SearXNGResult[]): SearchResultDto[]` — composition: `Normalize → Dedup → Map-to-Dto → StripHtml → Filter → Take`.
6. Register in DI: `services.AddSingleton<ISearchResultProcessor, SearchResultProcessor>()`.

## Acceptance Criteria

- [x] `NormalizeUrl`: covers all 5 rules; unit tests for each case (lowercase host, port 80/443, `utm_*`/`gclid`/`fbclid`, `#frag`, trailing slash). *(verified M5 — `SearchResultProcessor.NormalizeUrl`: lowercase host `.ToLowerInvariant()` (line 176), port strip (lines 182–184), tracking params `StartsWith("utm_")` + `TrackingParams.Contains` (ADR-011, lines 226–228), fragment not appended (line 239), trailing slash trim (lines 195–198); 10 [Theory] cases)*
- [x] Exact dedup: for the same normalized URL, the priority-engine result is kept (the order is pinned in tests); `HashSet`/`Dictionary` (O(1)) is used. *(verified M5 — ADR-010: `Dictionary<string, SearXNGResult>` (line 252), engine priority array `["google", "bing", ...]`, collision → replace if higher priority / longest snippet; 4 Facts in tests)*
- [x] Fuzzy dedup: titles ≥90% similarity across different domains → the first is kept; the threshold is configurable. *(verified M5 — ADR-009: `TitleSimilarity.AreSimilar` (Jaccard, threshold 0.90) across different domains (lines 288–311); same domain → keep both; 3 Facts)*
- [x] Filter: blocklist from config, snippet <20 chars dropped, `Take(MaxResults)` enforced. *(verified M5 — blocklist case-insensitive via `StringComparison.OrdinalIgnoreCase` (lines 409–416), snippet `< MinSnippetLength=20` (line 420), `.Take(_settings.MaxResults)` (line 144); tests: blocklist remove + case-insensitive (2 Facts), empty-snippet boundary 19/20 chars (3 Tests), Take limits (2 Facts))*
- [x] Snippets: HTML/scripts removed, length ≤300 chars (plain text). *(verified M5 — ADR-012: `StripHtml` strips script/style blocks → tags → `WebUtility.HtmlDecode` → collapse whitespace; ADR-013: `MaxSnippetLength=300`, word-boundary cut + ellipsis `\u2026`; tests: `<b>bold</b>`→"bold", `<script>alert(1)</script>text`→"text", `a &amp; b`→"a & b")*
- [x] Pipeline order strictly `Normalize → Dedup → Filter` (cover with a test verifying the order through observable effects). *(verified M5 — ADR-014: exact order in `Process`: Deduplicate (line 120), MapToDto+StripHtml+TruncateSnippet (lines 129–131), Filter+Take (lines 138/144); dedup on SearXNGResult, filter on SearchResultDto; tests: HTML snippet strips to <20 chars → filtered; both pipeline-order Facts pass)*
- [x] Unit tests (xUnit): normalization cases, exact-dup, fuzzy-dup, blocklist, empty-snippet, limit, HTML-strip, truncate. *(verified M5 — 37 tests total: `SearchResultProcessorTests.cs` = 28 methods (NormalizeUrl Theory 6 groups/10 cases, engine priority 4 Facts, fuzzy dedup 3 Facts, blocklist 2 Facts, empty-snippet 3 Tests, Take 2 Facts, StripHtml 4 Theory, Truncate 3 Tests, pipeline-order 2 Facts, empty-input 2 Facts, LDD 1 Fact); `TitleSimilarityTests.cs` = 9 methods (identical/disjoint/empty/partial/threshold/custom)*
- [x] LDD: `[IMP:9-10]` at the pipeline stages (after normalize / dedup / filter — Belief State). *(verified M5 — `SearchResultProcessor.cs`: [IMP:1][START], [IMP:7] after Deduplicate (line 123) + Filter (lines 136/141), [IMP:9] belief state (lines 114+146), [IMP:10] COMPLETE (lines 115+147); test `LddMarkers_ContainBeliefStateAndComplete` verifies both markers in log output)*
- [x] AOT publish without warnings. *(verified M5 — `dotnet publish -c Release -r win-x64 /p:PublishAot=true`: 0 × IL####; native binary 12.2 MB (no regression from the M4 baseline))*
- [x] Code follows `csharp-conventions`. *(verified M5 — all files (`ISearchResultProcessor.cs`, `SearchResultProcessor.cs`, `TitleSimilarity.cs`) have #region MODULE_CONTRACT + GREP_SUMMARY/STRUCTURE, XML docs with [PURPOSE]/[INVARIANTS]/[RATIONALE]/[CHANGES], per-class/file/method regions; tests same pattern)*

## Risks

- Levenshtein on long titles is expensive; bound the length or use Jaccard over tokens (AOT-safe, no native deps).
- Engine order — hardcode as an explicit priority array in code (not in config; SPEC §3.1.2 example); cover with a test.
- Trailing slash and UTM — combinations; cover with table-driven `[Theory]` tests.
