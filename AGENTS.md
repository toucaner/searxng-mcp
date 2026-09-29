# AGENTS.md

High-signal notes for OpenCode sessions working in this repo. Read before acting.

## Repo status

**Current layout (M12 restructure, ADR-034):** the main service project lives under `src/McpWebSearchService/` (`McpWebSearchService.csproj`, `Program.cs`, `WazuhAudit.cs`, `appsettings.json`, `packages.lock.json`, and folders `Configuration/`, `Models/`, `Serialization/`, `Services/`); tests live under `tests/McpWebSearchService.Tests/` (grouped by domain: `Configuration/`, `Models/`, `Services/`, `Integration/`, `Smoke/`, `Infrastructure/`); repo-level files (`McpWebSearchService.slnx`, `Dockerfile`, `.dockerignore`, `.mcp/server.json`, `README.md`, `AppGraph.xml`, `SPEC.md`, `milestones/`) stay at the repo root.

Historical (M1): the scaffold was generated at the repo root from the `mcpserver --aot` template (commit `23476ee`) — `McpWebSearchService.csproj` / `.slnx` / `Program.cs` / `Tools/RandomNumberTools.cs`. Business logic (config, HttpClient, pipeline, tools) was added in M2–M9.

Target system (per `SPEC.md`): a .NET 10 C# **MCP server** (Model Context Protocol) that proxies web search to a SearXNG instance. Topology: `LLM Agent <-> MCP Service (stdio/HTTP) <-> SearXNG (HTTP)`.

Note: `.opencode/` is **gitignored** (see root `.gitignore`). The agent workflow, skills, and rules below are local-only config, not shared via git. `AGENTS.md`, `SPEC.md`, `milestones/`, `DevelopmentPlan.md`, `AppGraph.xml` ARE git-tracked and carry cross-session memory.

## OpenCode workflow (GRACE Protocol) — mandatory phase order

Default agent is `@orchestrator` (`.opencode/opencode.json`). The orchestrator is a **stateless routing coordinator** — it reads state files and dispatches to 4 hidden subagents (`@architect` / `@code` / `@debug` / `@qa`, all `mode: subagent, hidden: true`) via the `task` tool. The user cannot bypass the orchestrator (no Tab-switch, no `@`-autocomplete for the 4 phase agents). Full methodology in `.opencode/rules/rules.md` (section `$START_ORCHESTRATOR_PROTOCOL`).

Phases are gated by the orchestrator:

0. `@orchestrator` — ALWAYS (default_agent). Re-reads state files every turn, routes to the appropriate subagent, enforces anti-loop.
1. `@architect` — when orchestrator detects missing/stale `DevelopmentPlan.md`, OR on `scope=decompose:<milestone>` request. Produces `DevelopmentPlan.md` + `AppGraph.xml`. Does NOT write implementation code. Does NOT delegate to `@code` (returns artifacts to orchestrator).
2. `@code` — only when an approved `DevelopmentPlan.md` exists. Writes code + xUnit tests. Updates `## Progress` in `tests/test_guide.md`.
3. `@debug` — on test failures / runtime errors. Adds `// BUG_FIX_CONTEXT: [HYPOTHESIS: ...]` before fix and `// BUG_FIX_CONTEXT: [scar]` after. Returns `RESUME_TASK_ID: <id>` if fix is incomplete (orchestrator resumes the session).
4. `@qa` — final verification. Read-only (`edit: deny`), holds `BLOCK` veto right. Writes verdict to `tests/qa_report.md` before returning.

Rules + agent prompts live in `.opencode/rules/rules.md` and `.opencode/agent/*.md`. Skill references live in `.opencode/skills/` (notably `csharp-conventions`, `devplan-protocol`, `graph-protocol`, `mode-code`, `mode-debug`, `mode-qa`).

Anti-loop: test-fix attempts are tracked in `.test_counter.json` (incremented by `@debug`, read by `@orchestrator` before every `@debug` dispatch). At attempt 3 use search; attempt 4 change strategy (orchestrator dispatches `@architect` re-plan first); attempt 5+ STOP and ask the operator (`doom_loop: ask` fires).

State files (file-based state — orchestrator is stateless): `DevelopmentPlan.md`, `AppGraph.xml`, `tests/test_guide.md`, `tests/qa_report.md` (all git-tracked); `.test_counter.json`, `.opencode/state/phase_log.md` (runtime / gitignored).

## Role Boundary Contract (authoritative — overrides generic skill wording)

`mode-architect` Step 5 says "DELEGATE to @code" and "You do NOT write implementation code". Taken literally this is misleading — `@architect` is granted `bash: dotnet *: allow` (wider than `@code`'s `dotnet build*/test*: allow`). The boundary is by **type of work**, not by a blanket ban on `dotnet`.

| Work type | Agent | Examples |
|---|---|---|
| Routing & anti-loop guardianship | `@orchestrator` | Task-dispatch to {architect, code, debug, qa}, reads `.test_counter.json` + `phase_log.md` + `qa_report.md`, decomposes large milestones via `@architect` |
| Architectural artifacts | `@architect` | `DevelopmentPlan.md`, `AppGraph.md`, ADRs (this file), `## Decomposition` section (on orchestrator request) |
| Scaffolding & feasibility validation | `@architect` | `dotnet new`, template probes, `dotnet publish` for AOT-trim-warning audit, dependency-graph scans for AOT-safety, `dotnet run` smoke-test |
| Feature implementation (`.cs` with business logic) | `@code` | `SearXNGSettings`, `SearXNGClient`, `SearchResultProcessor`, `WebSearchTools`, `JsonSerializerContext`, xUnit tests |
| Bug diagnosis & semantic-trace verification | `@debug` | LDD `[IMP:9-10]` analysis, `BUG_FIX_CONTEXT` scars, `RESUME_TASK_ID` for mid-fix continuation |
| Independent acceptance verification | `@qa` | AC-by-AC verification, `BLOCK` veto, `dotnet test`, LDD audit, writes `tests/qa_report.md` |

**Rule of thumb:** if the work produces a new `.cs` file carrying business logic (not a template-generated placeholder), it belongs to `@code`. If the work is `dotnet`-driven validation of architectural assumptions (AOT-clean? deps safe? binary name correct? host starts?), it belongs to `@architect`. Template scaffolding with no business logic (M1) is `@architect`-context, not `@code`-context. Routing, state-file reads, and anti-loop gating belong to `@orchestrator` and are NEVER delegated to phase agents.

**This contract is the source of truth.** When a fresh `@architect` session reads `mode-architect` Step 5, it MUST consult this section before deciding whether to delegate `dotnet`-work. Do not re-derive the boundary from the skill text alone.

## Commands & permissions

Bash is locked down in `opencode.json` — only `dotnet *` is auto-allowed for `@architect`; `@code` allows `dotnet build*` / `dotnet test*`; `@debug`/`@qa` allow `dotnet test*`. **Everything else (docker, curl, git commit, npm) prompts the operator.** Plan accordingly and prefer `dotnet`-based verification.

- Build / test: `dotnet build`, `dotnet test` (run a single test via `dotnet test --filter "FullyQualifiedName~MyClass.MyFact"`).
- AOT publish (per SPEC §5.1): `dotnet publish -c Release -r linux-x64 -o /app/publish /p:PublishAot=true`.
- Scaffolding template (per SPEC §6.1): `Microsoft.McpServer.ProjectTemplates`.

## Hard technical constraints (from SPEC — do not violate)

- **Target framework: .NET 10**, template `Microsoft.McpServer.ProjectTemplates`, **Native AOT** (`PublishAot=true`, RID `linux-x64`).
- **AOT safety is non-negotiable.** Use `System.Text.Json` with `JsonSerializerContext` source generators for all DTOs. **No `Newtonsoft.Json`**, no reflection-based serialization, no dynamic code gen. Strictly typed DTOs only.
- MCP tools are declared with `[McpServerToolType]` / `[McpServerTool]` attributes. Two tools per SPEC §2: `web_search` (query, categories, time_range, language) and `fetch_and_extract` (url). Return DTO is `SearchResultDto { Title, Url, Snippet, SourceEngine? }` (record).
- HTTP client: `IHttpClientFactory` with a typed `SearXNGClient` (`SearchAsync(SearchRequest)`). **15s timeout** + Circuit Breaker (Polly or manual) — return a clear "Search service temporarily unavailable" error to the LLM; never hang the agent.
- Config via `IOptions<SearXNGSettings>` bound to `appsettings.json`. Schema (SPEC §5.2): `SearXNG:{BaseUrl, MaxResults, BlockedDomains[], DefaultLanguage}`, `McpServer:{Name, Version}`.
- Post-processing pipeline in `SearchResultProcessor` **before** returning to LLM (saves context window): `NormalizeUrl` → `Deduplicate` → `Filter`. Details in SPEC §3 — URL normalization (lowercase host, strip standard ports, UTM/tracking params, fragments, trailing slash), exact-match dedup with engine priority, fuzzy title match (≥90%) across domains, blocklist + empty-snippet (<20 chars) filter + `Take(N)`. Use `HashSet<string>` for O(1) dedup. Snippets truncated to ~200–300 chars; plain text only (strip HTML/scripts).

## Code conventions (enforced by skills — brief)

Every `.cs` file/class/method must follow `.opencode/skills/csharp-conventions/`:

- `#region` / `#endregion` wrap every class and method (`#region CLASS_[Name]`, `#region METHOD_[Name]`, `#region MODULE_CONTRACT`).
- File header carries `GREP_SUMMARY` + `STRUCTURE` (compact symbol diagram) markup.
- XML docs with `<summary>`, `<remarks>` containing `[PURPOSE]`, `[INVARIANTS]`, `[RATIONALE]`, `[CHANGES]`.
- **LDD logging via `ILogger`**: format `[IMP:1-10][MethodName][Step] Message`. `[IMP:9-10]` (AI Belief State) is **mandatory** — `@debug`/`@qa` rely on these markers for semantic trace verification.
- No `...`, no `pass`, no `todo` in implementation bodies — code must be complete.

**Application scope:** conventions apply to `.cs` files carrying business logic (M2+). Template-generated files (`Program.cs`, `Tools/RandomNumberTools.cs` in M1) are NOT to be retrofitted with `#region`/XML/LDD — they will be replaced or rewritten in their milestone (M5/M6), at which point the new files follow conventions from creation.

## Architectural Decisions Register (ADR)

Cross-session memory of decisions that constrain future milestones. Each ADR is authoritative unless explicitly superseded by a later ADR. When `@architect` produces a new `DevelopmentPlan.md`, it MUST carry forward any still-active ADRs (do not silently drop them).

### ADR-001: `RuntimeIdentifiers` (plural), not singular `RuntimeIdentifier`
- **Status:** Active (M1, commit `23476ee`)
- **Context:** M1 milestone required `RuntimeIdentifier=linux-x64` in `.csproj`. The `mcpserver --aot` template emits `<RuntimeIdentifiers>win-x64;win-arm64;osx-arm64;linux-x64;linux-arm64;linux-musl-x64</RuntimeIdentifiers>` (plural).
- **Decision:** Keep plural `RuntimeIdentifiers` from the template. Do NOT set singular `RuntimeIdentifier=linux-x64` — it would force restore under linux-x64 on every `dotnet build`/`restore` and break local dev on Windows. Production RID is supplied via CLI `-r linux-x64` at publish time (SPEC §5.1 Dockerfile).
- **Consequence:** `dotnet build` without `-r` restores for the host OS; `dotnet publish -r linux-x64 /p:PublishAot=true` works as SPEC §5.1 specifies. M7 Dockerfile unchanged.
- **Verify:** `.csproj` has `RuntimeIdentifiers` (plural), no `RuntimeIdentifier` (singular).

### ADR-002: Cross-OS AOT — local verify via `win-x64`, production via `linux-x64` in Docker
- **Status:** Active (M1, commit `23476ee`)
- **Context:** Native AOT does not support cross-OS compilation. `dotnet publish -r linux-x64 /p:PublishAot=true` run from Windows fails: `error : Cross-OS native compilation is not supported.`
- **Decision:** Local AOT-trim-safety verification on the Windows dev host uses `dotnet publish -c Release -r win-x64 /p:PublishAot=true` (trim-analysis warnings are RID-independent, so win-x64 is a valid proxy for AOT-safety). The `linux-x64` AOT build runs inside the Linux Docker build stage (M7, `mcr.microsoft.com/dotnet:10.0-sdk-aot`).
- **Consequence:** M1–M6 acceptance criteria that say "AOT publish without trim-warnings" are satisfied by a clean `win-x64` publish. M7 adds the `linux-x64` verification inside Docker. Do NOT attempt `dotnet publish -r linux-x64` on Windows — it will fail and waste time.
- **Verify:** `dotnet publish -c Release -r win-x64 /p:PublishAot=true` output contains no `warning IL####`.

### ADR-003: `InvariantGlobalization=true` retained
- **Status:** Active (M1, commit `23476ee`) — revisit if M4 URL-normalization needs culture-aware casing
- **Context:** The `mcpserver --aot` template sets `<InvariantGlobalization>true</InvariantGlobalization>` (disables ICU/globalization, shrinks the native binary). SPEC §5.1 Dockerfile installs `libicu-dev`, implying culture-aware globalization.
- **Decision:** Keep `InvariantGlobalization=true`. Rationale: the SearXNG `language` parameter is a string token (`ru`, `en`) passed through to the HTTP API; no culture-aware string comparisons are required for M2–M6. URL normalization uses `ToLowerInvariant()` which is culture-invariant and works under invariant globalization.
- **Consequence:** Smaller binary, no ICU dependency at runtime. If M4's fuzzy title matching or URL normalization turns out to need culture-aware casing, supersede this ADR and remove the flag (then M7 Dockerfile's `libicu-dev` becomes relevant).
- **Verify:** `.csproj` has `InvariantGlobalization=true`. M4 to re-evaluate.

### ADR-004: Binary name `McpWebSearchService` is fixed
- **Status:** Active (M1, commit `23476ee`)
- **Context:** SPEC §5.1 Dockerfile ENTRYPOINT is `["./McpWebSearchService"]`. The binary name is derived from the project name passed to `dotnet new -n`.
- **Decision:** The project name `McpWebSearchService` is fixed for the lifetime of the project. Do not rename the `.csproj`, the `.slnx`, or the assembly.
- **Consequence:** Any rename would break the M7 Dockerfile ENTRYPOINT and the `.mcp/server.json` manifest. If a rename is ever required, update SPEC §5.1, the Dockerfile, and `.mcp/server.json` atomically.
- **Verify:** `AssemblyName` derives from project name `McpWebSearchService`; `dotnet publish` produces `McpWebSearchService` (or `McpWebSearchService.exe` on Windows).

### ADR-005: Solution file format is `.slnx` (XML), not `.sln`
- **Status:** Active (M1, commit `23476ee`)
- **Context:** .NET 10 SDK `dotnet new sln` defaults to the new `.slnx` (XML-based) format, not the legacy `.sln`.
- **Decision:** Use `McpWebSearchService.slnx` as the solution file. Do not create a parallel `.sln`.
- **Consequence:** `dotnet build McpWebSearchService.slnx` works; IDEs that predate .NET 10 may not understand `.slnx` (acceptable — this project targets .NET 10 tooling). M8 test project is added via `dotnet sln McpWebSearchService.slnx add <test.csproj>`.
- **Verify:** Only `McpWebSearchService.slnx` exists at repo root; no `.sln`.

### ADR-006: `EnableConfigurationBindingGenerator=true` + Hosting ≥10.x for AOT-safe Options binding
- **Status:** Active (M2, architect probe on net10.0 — full native AOT binary produced)
- **Context:** M2 requires `IOptions<SearXNGSettings>` / `IOptions<McpServerSettings>` bound from `appsettings.json` via `OptionsBuilder<T>.Bind(IConfiguration)`. `Bind<T>` is annotated `[RequiresUnreferencedCode]` + `[RequiresDynamicCode]` — it uses reflection to set properties. Under Native AOT / trimming this emits `warning IL2026` + `warning IL3050` per `Bind<T>` call, violating the AOT-safety hard constraint (SPEC §4.1, AGENTS.md). M2 milestone text flags this as a risk: "Configuration binder + AOT".
- **Decision:** Two-part fix, BOTH required:
  1. Set `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` in `McpWebSearchService.csproj`. This enables the configuration-binding source generator, which emits a strongly-typed binder at compile time — no reflection.
  2. Upgrade `Microsoft.Extensions.Hosting` from the template's `8.0.1` to a `10.x` version (e.g. `10.0.9`). The template-pinned `8.0.1` pulls `Microsoft.Extensions.Configuration.Binder 8.0.2`, whose source generator does NOT intercept `Bind<T>()` under net10.0 trim analysis (verified: 4 warnings persist). Hosting 10.x pulls a compatible `Configuration.Binder 10.x` where the generator correctly intercepts `Bind<T>()`.
  - Usage stays standard: `AddOptions<T>().Bind(section).Validate(...).ValidateOnStart()`.
- **Evidence (architect probe, net10.0, full `dotnet publish -c Release -r win-x64 /p:PublishAot=true` — native binary 12.8 MB produced):**
  - Hosting 8.0.1 + generator=true: **4 × `IL####`** (`IL2026`+`IL3050` × 2). Generator not intercepting.
  - Hosting 10.0.9 + generator=true: **0 × `IL####`**. Native AOT binary produced cleanly.
  - `ValidateOnStart()` + `Validate(...)` lambda: AOT-clean, runtime-verified — invalid config throws `OptionsValidationException` at `RunAsync()`, host fails to start.
  - Note: `ConfigurationBinder.Get<T>()` is NOT an AOT-safe alternative — it is itself reflection-annotated and emits the same warnings under net10.0 when the generator does not intercept. The generator intercepts `Bind<T>()` (Options path), not raw `Get<T>()`.
- **Consequence:**
  - `Microsoft.Extensions.Hosting` MUST be upgraded to 10.x in M2 (deviation from template's 8.0.1). This is a csproj change @code must perform.
  - All settings classes MUST be `sealed class` with public `{ get; set; }` properties (no positional records, no `init`-only) — this is what the source generator binds. Applies to M2's `SearXNGSettings`/`McpServerSettings` and any future settings classes.
  - If a future milestone adds new `IOptions<T>`, ensure the type is source-generator-friendly (sealed, public setters) and that Hosting stays on a 10.x line.
- **Verify:** `.csproj` contains `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` AND `<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.9" />` (or later 10.x). `dotnet publish -c Release -r win-x64 /p:PublishAot=true` produces no `warning IL####` and a native binary.

### ADR-007: Manual Circuit Breaker (no Polly)
- **Status:** Active (M4 — complete, QA SUCCESS)
- **Context:** M4 requires a Circuit Breaker for the SearXNG HTTP client (SPEC §4.2). Polly v7 is NOT AOT-compatible (reflection on expression trees). Polly v8 (`Polly.Core`) IS AOT-compatible (verified: 0 × `IL####`), but adds a runtime dependency for a single-endpoint client.
- **Decision:** Implement the Circuit Breaker manually — `internal sealed class CircuitBreakerState` with `lock`-based thread safety. States: Closed (normal), Open (fail-fast after 3 failures), HalfOpen (single probe after 30s). Methods: `TryAcquirePermit()`, `RecordFailure()`, `RecordSuccess()`. No expression trees, no reflection, no dynamic codegen — fully AOT-safe.
- **Consequence:** Zero new runtime dependencies. ~50 LOC state machine. Singleton per endpoint. Testable via internal helpers (`Reset`, `SetLastFailureTime`, `SetState`) exposed through `InternalsVisibleTo`.
- **Verify:** `Services/CircuitBreakerState.cs` exists, `internal sealed class`, `lock`-based. `dotnet publish -c Release -r win-x64 /p:PublishAot=true` — 0 × `IL####`. M4 QA: 29/29 tests pass.

### ADR-008: `Microsoft.Extensions.Http` added as PackageReference
- **Status:** Active (M4 — complete, QA SUCCESS)
- **Context:** M4 requires `IHttpClientFactory` and `AddHttpClient<ISearXNGClient, SearXNGClient>()`. `Microsoft.Extensions.Hosting` 10.0.9 (ADR-006) does NOT transitively pull `Microsoft.Extensions.Http`.
- **Decision:** Add `<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.9" />` to `McpWebSearchService.csproj`. Version 10.0.9 matches Hosting (ADR-006) — AOT-safe (verified: 0 × `IL####`).
- **Consequence:** Single csproj change in M4. `dotnet build` + `dotnet publish -c Release -r win-x64 /p:PublishAot=true` both clean. Also added `<InternalsVisibleTo Include="McpWebSearchService.Tests" />` for CircuitBreakerState testability.
- **Verify:** `.csproj` contains `<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.9" />`. `dotnet publish -c Release -r win-x64 /p:PublishAot=true` — 0 × `IL####`.

### ADR-009: Fuzzy title similarity — Jaccard on tokens (not Levenshtein)
- **Status:** Active (M5)
- **Context:** SPEC §3.1.3 requires fuzzy title matching ≥90% (Levenshtein OR Jaccard). Milestone risk note: "Levenshtein on long titles is costly". Titles are typically 30–120 chars. Levenshtein is O(n·m) on chars; Jaccard is O(n+m) on tokens. AOT-safety requires no native deps.
- **Decision:** Use **Jaccard similarity on whitespace tokens** (after `ToLowerInvariant()` + `Split`). Threshold `0.90` (configurable constant `TitleSimilarityThreshold`). Titles capped to 500 chars before tokenization (`MaxTitleLengthForFuzzy`). Implemented in `Services/TitleSimilarity.cs` as `internal static class` with `Calculate(string,string): double` and `AreSimilar(string,string,double): bool`.
- **Consequence:** O(n+m) performance, pure arithmetic over `HashSet<string>`, fully AOT-safe. `ToLowerInvariant()` works under `InvariantGlobalization=true` (ADR-003 — no revision needed). Separate file for testability. Threshold is a constant (not config) — business logic, not deployment config.
- **Verify:** `Services/TitleSimilarity.cs` — `internal static class`, Jaccard formula `|A∩B|/|A∪B|`. `dotnet test` TitleSimilarityTests pass. `dotnet publish` — 0 × `IL####`.

### ADR-010: Engine priority — hardcoded array (not in config)
- **Status:** Active (M5)
- **Context:** SPEC §3.1.2: on exact-URL collision, keep the result from the priority engine ("Google > Bing > Yandex" as example). SearXNG aggregates google, bing, yandex, duckduckgo, brave, wikipedia, startpage, mojeek, etc.
- **Decision:** Hardcoded `private static readonly string[] EnginePriority = ["google", "bing", "duckduckgo", "brave", "yandex", "startpage", "mojeek", "wikipedia"];` in `SearchResultProcessor`. `GetEnginePriority(string? engine): int` returns array index (0 = highest) or `int.MaxValue` if null/unknown. On collision: replace if new has strictly higher priority (lower index); if equal priority, keep the one with the longer snippet; if both unknown, keep first.
- **Consequence:** Business logic in code (not config) — testable via `[Theory]`. Unknown engines get `int.MaxValue` (lowest priority) — don't break the pipeline. Extending the array is a code change (acceptable — engine set is stable).
- **Verify:** `SearchResultProcessor.EnginePriority` array present. Test: bing vs google collision → google wins. Test: unknown vs bing → bing wins.

### ADR-011: Tracking-param detection — prefix-check `utm_` + explicit set
- **Status:** Active (M5)
- **Context:** SPEC §3.1.1: strip tracking params (`utm_*`, `gclid`, `fbclid`, etc.). `utm_*` is a prefix wildcard (utm_source, utm_medium, utm_campaign, utm_content, utm_term, and future variants).
- **Decision:** Two-rule filter in `NormalizeUrl`: (1) param removed if `name.StartsWith("utm_", StringComparison.Ordinal)`; (2) param removed if lowercase name is in `HashSet<string> TrackingParams = ["gclid", "fbclid", "mc_eid", "mc_cid", "msclkid", "yclid", "dclid", "ref", "ref_src", "ref_url", "_hsenc", "_hsmi", "hsctagr", "igshid", "spm", "scm", "sr_share", "wt_mc"]`. `StringComparison.Ordinal` — culture-invariant (ADR-003). If no non-tracking params remain, `?` is omitted.
- **Consequence:** Prefix-check catches all `utm_*` variants without enumeration. Explicit set covers known click-IDs and referral params. O(1) HashSet lookup. Combines well with other NormalizeUrl rules.
- **Verify:** `SearchResultProcessor.TrackingParams` HashSet + prefix-check logic. `[Theory]` tests: `utm_source=x` removed, `gclid=abc` removed, `q=search` kept, `utm_custom=future` removed (prefix), `q=x&utm_source=y&fbclid=z` → `q=x`.

### ADR-012: StripHtml — `[GeneratedRegex]` + `WebUtility.HtmlDecode`
- **Status:** Active (M5)
- **Context:** SPEC §4.3: snippets must be plain text (strip HTML/scripts), truncated 200–300 chars. SearXNG `content` sometimes contains HTML tags. Need HTML-strip + entity-decode, AOT-safe.
- **Decision:** (1) `[GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]` source-generated regex strips `<script>`/`<style>` blocks with content. (2) `[GeneratedRegex(@"<[^>]+>")]` strips remaining tags. (3) `System.Net.WebUtility.HtmlDecode(string)` decodes entities (`&amp;`→`&`). (4) Collapse whitespace via regex `\s+` → single space + Trim. Order: script/style blocks → tags → decode → collapse. `[GeneratedRegex]` is a source generator (AOT-safe), NOT `Regex.Compile()` (which uses reflection IL-emit).
- **Consequence:** No external HTML-parsing dependency (HtmlAgilityPack/AngleSharp rejected — heavyweight, AOT-unsafe by default). BCL-only. `WebUtility.HtmlDecode` is a static BCL method (AOT-safe).
- **Verify:** `SearchResultProcessor.StripHtml` uses `[GeneratedRegex]` attributes (not `new Regex(..., Compiled)`). `[Theory]` tests: `<b>bold</b>`→`bold`, `<script>alert(1)</script>text`→`text`, `a &amp; b`→`a & b`. `dotnet publish` — 0 × `IL####`.

### ADR-013: Truncation — `MaxSnippetLength = 300` with word-boundary
- **Status:** Active (M5)
- **Context:** SPEC §4.3: "truncate long snippets (e.g. to 200-300 chars)". Need a concrete default.
- **Decision:** `private const int MaxSnippetLength = 300;` (upper bound of SPEC range). `TruncateSnippet(string)`: if ≤300 → return as-is; if >300 → take `text[..300]`, find last space in range [250,300], cut at it, append `"…"` (ellipsis). If no space in range → hard cut at 300 + `"…"`.
- **Consequence:** 300 chars gives maximum LLM context per SPEC. Word-boundary avoids mid-word splits. Ellipsis signals truncation to LLM. Constant (not config) — display concern, not deployment.
- **Verify:** `SearchResultProcessor.MaxSnippetLength == 300`. `[Theory]`: 100-char as-is, 400-char ≤303 with ellipsis, 300-exact as-is, word-boundary cut.

### ADR-014: Pipeline composition — DTO-mapping in the middle
- **Status:** Active (M5)
- **Context:** SPEC §3 pipeline order is `NormalizeUrl → Deduplicate → Filter`, but NormalizeUrl/Deduplicate operate on `SearXNGResult` (input DTO with Engine/Content) while Filter returns `SearchResultDto` (output DTO). Need exact stage boundaries and mapping location.
- **Decision:** Exact sequence in `Process`: (1) `NormalizeUrl` on `SearXNGResult.Url` → canonical key; (2) `Deduplicate` on `SearXNGResult` (exact by normalized URL + engine priority, fuzzy by Title across different domains) — needs Engine for priority, Content for longest-snippet; (3) `MapToDto` SearXNGResult→SearchResultDto (Url = ORIGINAL not normalized — LLM sees clickable original); (4) `StripHtml` on `SearchResultDto.Snippet`; (5) `Truncate` on Snippet; (6) `Filter` on `SearchResultDto` (blocklist by domain + snippet<20 post-strip + Take(MaxResults)). Filter operates on DTO (post-StripHtml) because SPEC §3.2 "content <20 chars" refers to plain-text length.
- **Consequence:** Dedup on input DTO (has Engine), filter on output DTO (has clean snippet). Url in DTO is original (not normalized) — normalized URL is dedup-key only. Pipeline-order test verifies via observable effects (HTML snippet with 25 plain chars passes, 15 plain chars filtered).
- **Verify:** `SearchResultProcessor.Process` calls stages in order: Normalize → Dedup → Map → StripHtml → Truncate → Filter → Take. Test: HTML snippet `<b>x</b>` (1 plain char) filtered; `<b>twenty+chars here!!</b>` (20+ plain chars) kept.

### ADR-015: Thread-safety — stateless singleton
- **Status:** Active (M5)
- **Context:** `SearchResultProcessor` is registered as a singleton (M6 DI). All data is per-call (input array, local collections).
- **Decision:** `SearchResultProcessor` is **fully stateless**. All mutable collections (`Dictionary`, `HashSet`, `List`) are created as locals inside `Process`. Only instance fields are injected dependencies (`SearXNGSettings _settings` readonly snapshot, `ILogger _logger`). `EnginePriority`, `TrackingParams`, `MaxSnippetLength`, regex patterns are `static readonly`/`const` (immutable, thread-safe).
- **Consequence:** Stateless singleton is thread-safe by construction — no `lock`/`ConcurrentDictionary` needed. No shared mutable state.
- **Verify:** `SearchResultProcessor` has exactly 2 instance fields (`_settings`, `_logger`), both `readonly`. No instance mutable fields. Code review confirms all `Dictionary`/`HashSet`/`List` are method locals.

### ADR-016: `TitleSimilarity` as separate `internal static class`
- **Status:** Active (M5)
- **Context:** Milestone Draft Code Graph has `Services/TitleSimilarity.cs` as a separate utility. Alternative: private nested class in `SearchResultProcessor`.
- **Decision:** Separate file `Services/TitleSimilarity.cs`, `internal static class TitleSimilarity`. Methods: `Calculate(string,string): double` (Jaccard), `AreSimilar(string,string,double): bool`. Accessible to tests via `InternalsVisibleTo` (already configured in M4 csproj).
- **Consequence:** Isolated testability (`TitleSimilarityTests.cs` tests pure function without pipeline dependencies). `static` — no DI needed. `internal` — not part of public API.
- **Verify:** `Services/TitleSimilarity.cs` exists as `internal static class`. `Tests_TitleSimilarityTests_cs` tests it in isolation.

### ADR-017: `CircuitBreakerState` DI registration — singleton via `AddSingleton<CircuitBreakerState>()`
- **Status:** Active (M6)
- **Context:** `SearXNGClient` (M4) constructor takes `CircuitBreakerState` as 4th parameter. `CircuitBreakerState` is `public sealed class` in the actual M4 code (ADR-007 text says "internal sealed" but the implementation made it `public` — likely for DI compatibility). M6 needs to wire this into DI so `AddHttpClient<ISearXNGClient, SearXNGClient>()` can resolve it.
- **Decision:** Register `services.AddSingleton<CircuitBreakerState>()` in `Program.cs`. The DI container resolves the singleton and injects it into `SearXNGClient`'s constructor. `AddHttpClient<T>` registers the typed client as transient (new per request), but `CircuitBreakerState` singleton is shared — correct behavior (breaker state persists across requests for a single endpoint).
- **Consequence:** One line in `Program.cs`: `builder.Services.AddSingleton<CircuitBreakerState>();` before `AddHttpClient<ISearXNGClient, SearXNGClient>()`. M4 unit tests (`SearXNGClientTests`) construct `new CircuitBreakerState()` directly — unaffected by DI.
- **Verify:** `SearXNGClient` constructor receives non-null `CircuitBreakerState`. `dotnet run` — host starts without DI resolution errors.

### ADR-018: `web_search` return type — pre-serialized JSON `string` (NOT `SearchResultDto[]`)
- **Status:** Active (M6)
- **Context:** SPEC §6.1 step 4 says "return `IEnumerable<SearchResultDto>`". But MCP SDK 1.2.0 `AIFunctionMcpServerTool.InvokeAsync` (source: GitHub v1.2.0 `AIFunctionMcpServerTool.cs`) handles return values via pattern-match. For "Other types" (not string, not CallToolResult, not AIContent), the SDK calls `JsonSerializer.Serialize(result, AIFunction.JsonSerializerOptions.GetTypeInfo(typeof(object)))` — this uses `typeof(object)` which may trigger reflection-based fallback under Native AOT, producing `IL2026`/`IL3050` warnings. Even passing `McpJsonContext.Default.Options` to `WithTools<T>` doesn't fully guarantee AOT-safety because `GetTypeInfo(typeof(object))` tries to resolve `object` type info (not registered in `McpJsonContext`).
- **Decision:** The `WebSearch` tool method returns `Task<string>` — a pre-serialized JSON string via `JsonSerializer.Serialize(results, McpJsonContext.Default.SearchResultDtoArray)`. The SDK's `string text` branch creates a `TextContentBlock { Text = text }` directly — zero serialization, zero reflection, guaranteed AOT-safe. The LLM receives a JSON string as text content (standard MCP pattern).
- **Consequence:** `WebSearch` signature: `public async Task<string> WebSearch(...)`. On `SearXNGUnavailableException` → returns `"Search service temporarily unavailable"` (string, not exception). On empty results → returns `"[]"`. `fetch_and_extract` also returns `string` (plain text) — symmetric. SPEC §2.1 "JSON array" satisfied by JSON string payload.
- **Verify:** `dotnet publish -c Release -r win-x64 /p:PublishAot=true` → 0 × `warning IL####`. Unit test: `WebSearch` returns non-null string containing JSON array. `SearXNGUnavailableException` → returns the exact error string.

### ADR-019: Tool name derivation — SDK `SnakeCaseLower` default (no explicit `[McpServerTool(Name=...)]`)
- **Status:** Active (M6)
- **Context:** SPEC §2 requires tool names `web_search` and `fetch_and_extract`. MCP SDK 1.2.0 `AIFunctionMcpServerTool.DeriveName` applies `JsonNamingPolicy.SnakeCaseLower.ConvertName(method.Name)` by default when `McpServerToolAttribute.Name` is not set. It also strips "Async" suffix from async method names.
- **Decision:** Methods `WebSearch` and `FetchAndExtract` — WITHOUT explicit `Name` in `[McpServerTool]`. SDK auto-derives: `WebSearch` → `web_search`, `FetchAndExtract` → `fetch_and_extract`. Methods are NOT async-suffixed (cleaner; SDK strips "Async" anyway).
- **Consequence:** `[McpServerTool]` without parameters on both methods. `[McpServerToolType]` on class. If SDK changes default policy in future, switch to explicit `[McpServerTool(Name = "web_search")]`.
- **Verify:** `dotnet run` → MCP `list_tools` returns `web_search` and `fetch_and_extract`.

### ADR-020: Parameter names — C# idiomatic `timeRange` (NOT `time_range`)
- **Status:** Active (M6)
- **Context:** SPEC §2.1 specifies snake_case params (`time_range`). MCP SDK binds parameters from `CallToolRequestParams.Arguments` dictionary by C# parameter name (key match). C# identifiers cannot use snake_case. The SDK generates JSON schema from C# parameter names + `[Description]` attributes — there is no `[JsonPropertyName]`-equivalent for tool parameters.
- **Decision:** Use C# idiomatic parameter names: `query`, `categories`, `timeRange`, `language`. The JSON schema (generated by SDK) will have `timeRange` key. LLM sees `timeRange` in schema and sends `timeRange`. `[Description]` on each parameter for LLM-facing description. SPEC §2.1 param names are a guide, not a strict wire-format contract (SearXNG API params are converted in `SearXNGClient.BuildUrl` where `time_range` SearXNG API param is used).
- **Consequence:** Method signature: `WebSearch([Description("...")] string query, [Description("...")] string categories = "general", [Description("...")] string? timeRange = null, [Description("...")] string? language = null)`.
- **Verify:** MCP `list_tools` → `web_search` inputSchema contains `query`, `categories`, `timeRange`, `language`.

### ADR-021: `language` default — `null` param → `_settings.DefaultLanguage` fallback
- **Status:** Active (M6)
- **Context:** SPEC §2.1: `language` optional, default from config (`SearXNGSettings.DefaultLanguage`). If LLM omits `language`, tool uses config default. If provided, uses the provided value.
- **Decision:** Parameter `string? language = null`. In method body: `var lang = string.IsNullOrWhiteSpace(language) ? _settings.DefaultLanguage : language;`. Build `SearchRequest` with `Language = lang`.
- **Consequence:** `SearchRequest.Language` always non-empty after tool method (from param or config). `SearXNGClient.BuildUrl` adds `language` param only if non-empty (M4 logic already handles this). `null` default → SDK generates optional param in JSON schema.
- **Verify:** Unit test: `WebSearch(query: "test")` → `SearchRequest.Language == _settings.DefaultLanguage`. `WebSearch(query: "test", language: "en")` → `SearchRequest.Language == "en"`.

### ADR-022: `fetch_and_extract` — separate `HttpClient` via `IHttpClientFactory` + shared `HtmlTextExtractor`
- **Status:** Active (M6)
- **Context:** SPEC §2.2: `fetch_and_extract` loads a URL and returns cleaned text (≤5000 chars). Needs an HTTP client for arbitrary URLs (not SearXNG). HTML-strip logic from M5 (`SearchResultProcessor.StripHtml`) is a private method — need a shared utility without modifying M5 code.
- **Decision:** (1) HTTP client: `WebSearchTools` receives `IHttpClientFactory` via DI. In `FetchAndExtract` — `var client = _httpClientFactory.CreateClient("FetchExtract");` with 15s timeout. Does NOT reuse `SearXNGClient`'s HttpClient (different endpoint, no Circuit Breaker for arbitrary URLs). (2) HTML-strip: Create `Services/HtmlTextExtractor.cs` — `internal static class HtmlTextExtractor` with `ExtractPlainText(string html, int maxLength = 5000): string`. Logic: `[GeneratedRegex]` strip script/style blocks + tags (ADR-012 pattern) + `WebUtility.HtmlDecode` + collapse whitespace + truncate to `maxLength` with word-boundary + ellipsis. `SearchResultProcessor.StripHtml` (M5) is NOT refactored — `HtmlTextExtractor` is a separate utility for `fetch_and_extract` (different truncation: 5000 vs 300). Acceptable ~10 lines of regex duplication (priority: don't modify M5, avoid regression). (3) Error handling: HTTP errors, invalid URL, non-HTML content → return clear message string to LLM (not throw).
- **Consequence:** New file: `Services/HtmlTextExtractor.cs` — `internal static class`. `WebSearchTools` constructor adds `IHttpClientFactory`. `Program.cs`: `builder.Services.AddHttpClient("FetchExtract").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));`. `FetchAndExtract` returns `Task<string>`.
- **Verify:** Unit test: `FetchAndExtract` with mock HttpClient → returns plain text ≤5000 chars. Invalid URL → error message string. HTTP 404 → error message string. `dotnet publish -c Release -r win-x64 /p:PublishAot=true` → 0 × `IL####`.

### ADR-023: Runtime dependencies — full runtime-deps set (NOT just `libicu-dev`)
- **Status:** Active (M7)
- **Context:** SPEC §5.1 conceptual Dockerfile installs `libicu-dev` in the runtime stage (`debian:bookworm-slim`). Three issues: (1) `libicu-dev` is the development package (headers + static libs) — not needed at runtime; the runtime package is `libicu72` (Debian 12/bookworm ships ICU 72). (2) With `InvariantGlobalization=true` (ADR-003), application code uses invariant culture — BUT `dotnet/runtime#100583` confirms Native AOT binaries on Linux still link against ICU native libs even under invariant globalization ("we still need to link ICU native libs and they don't get linked out"). So `libicu72` IS still required. (3) An AOT binary using `HttpClient` for HTTPS to SearXNG requires `libssl3` (TLS) + `ca-certificates` (root CA store). A native binary on Debian also needs `libc6`, `libgcc-s1`, `libstdc++6`. SPEC §5.1's Dockerfile only installs `libicu-dev` — missing these critical deps, which would cause runtime failures.
- **Decision:** Replace SPEC §5.1's `libicu-dev` with the full runtime-deps set, matching the official `dotnet/dotnet-docker` `runtime-deps/9.0/bookworm-slim/amd64/Dockerfile` pattern: `ca-certificates libc6 libgcc-s1 libicu72 libssl3 libstdc++6 tzdata`. Rationale per package: `ca-certificates` (HTTPS cert validation for HttpClient), `libc6` (GNU C Library base), `libgcc-s1` (GCC runtime), `libicu72` (ICU runtime — NOT `-dev`; AOT binary links against it even with InvariantGlobalization=true), `libssl3` (OpenSSL 3 TLS for HttpClient HTTPS), `libstdc++6` (C++ stdlib — coreclr native runtime), `tzdata` (parity with official image).
- **Consequence:** Dockerfile runtime-stage `RUN apt-get install` installs 7 packages (not just `libicu-dev`). SPEC §5.1 Dockerfile's `apt-get install` line is superseded by this ADR; all other SPEC §5.1 elements preserved. `tzdata` is optional but kept for parity.
- **Verify:** Dockerfile runtime-stage `RUN` installs `ca-certificates libc6 libgcc-s1 libicu72 libssl3 libstdc++6 tzdata`. Operator-gated `docker run` confirms binary starts without `DllNotFoundException` or TLS errors.

### ADR-024: Dockerfile — `debian:bookworm-slim` runtime base (SPEC §5.1 fidelity, NOT `runtime-deps` image)
- **Status:** Active (M7)
- **Context:** Two valid patterns for AOT runtime images: (a) `FROM debian:bookworm-slim` + manual `apt-get install` (SPEC §5.1 pattern), or (b) `FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-bookworm-slim` (Microsoft pre-built deps image). The official `dotnet/samples` AOT Dockerfile uses pattern (b) with `runtime-deps:10.0-noble-chiseled`.
- **Decision:** Follow SPEC §5.1 exactly — use `FROM debian:bookworm-slim AS final` with manual `apt-get install` (ADR-023 package set). Do NOT switch to `runtime-deps` image.
- **Consequence:** Dockerfile is slightly longer (manual `apt-get install`) but matches SPEC §5.1 structure and gives full transparency over installed packages. If a future milestone prioritizes image minimization, switch to `runtime-deps` and supersede this ADR.
- **Verify:** Dockerfile `final` stage `FROM debian:bookworm-slim`. NOT `mcr.microsoft.com/dotnet/runtime-deps:*`.

### ADR-025: `.dockerignore` — exclude `tests/`, `.opencode/`, build artifacts, IDE files
- **Status:** Active (M7)
- **Context:** Docker build context (`COPY . .`) sends the entire repo to the Docker daemon. Without `.dockerignore`, this includes `bin/`, `obj/` (large stale host-OS artifacts), `.git/` (full history), `.opencode/` (agent config), `tests/` (test project not needed for production build), IDE files, runtime state.
- **Decision:** `.dockerignore` excludes: `bin/`, `obj/`, `.git/`, `.opencode/`, `tests/`, `.test_counter.json`, `.vs/`, `.idea/`, `.vscode/`, `*.user`, `*.suo`, `*.userprefs`, `*.rsuser`, `**/*.log`, `**/*.tmp`, `**/*.cache`, `Dockerfile`, `.dockerignore`, `publish/`. NOT excluded: source files, csproj, slnx, appsettings.json, .mcp/, docs.
- **Consequence:** Faster Docker builds (smaller context), cleaner production image. `tests/` exclusion is safe — csproj already `<Compile Remove="tests\**" />`.
- **Verify:** `.dockerignore` file exists at repo root. `docker build` context size is small.

### ADR-026: E2E testing approach — DI-integration primary (AC1–AC4) + stdio-smoke secondary (AC5)
- **Status:** Active (M8)
- **Context:** M8 requires E2E/integration tests over the assembled system. Two viable approaches: (A) stdio-based — spawn `dotnet run` as child process, send JSON-RPC over stdin/stdout (maximum fidelity, but brittle process management + stdio parsing); (B) DI-integration — build full ServiceCollection replicating Program.cs DI wiring with mock HttpMessageHandler, resolve WebSearchTools from DI (fast, reliable, easy mock injection, tests real DI + real pipeline, but doesn't test stdio transport). Orchestrator confirmed stdio is feasible (initialize handshake + tools/list work).
- **Decision:** Do BOTH, weighted differently. (B) DI-integration tests are PRIMARY — cover AC1–AC4 (search success with pipeline, unavailability/CB, fetch_and_extract, timeout) reliably and fast. Build real DI container via `TestHostFactory` helper: `AddOptions<SearXNGSettings>().Bind(in-memory config)`, `AddSingleton<CircuitBreakerState>()`, `AddHttpClient<ISearXNGClient, SearXNGClient>().ConfigurePrimaryHttpMessageHandler(mockHandler)`, `AddHttpClient("FetchExtract").ConfigurePrimaryHttpMessageHandler(mockHandler)`, `AddSingleton<ISearchResultProcessor, SearchResultProcessor>()`, `AddSingleton<WebSearchTools>()`. Mock at HTTP boundary only (DelegatingHandler) — correct isolation point. (A) stdio-smoke tests are SECONDARY — 2 tests only: `ProcessStarts_InitializeHandshake` (process starts + JSON-RPC initialize) + `ToolsList_ReturnsTwoTools` (tools/list returns web_search + fetch_and_extract with descriptions). stdio smoke tests do NOT call `tools/call` (search is covered by DI-integration; stdio tests only verify transport + handshake + tool registration).
- **Consequence:** Two test files: `IntegrationTests.cs` (7 tests, AC1–AC4/AC6/AC8/AC10) + `StdioSmokeTests.cs` (2 tests, AC5). `TestHostFactory.cs` helper builds DI. `CapturingLogger.cs` (ADR-027) for LDD verification. Test project csproj needs NO new PackageReference (Microsoft.Extensions.Http transitive via ProjectReference, ADR-008). If `ConfigurePrimaryHttpMessageHandler` extension not found at compile time, add `<PackageReference Include="Microsoft.Extensions.Http" Version="10.0.9" />` as fallback.
- **Verify:** `dotnet test` runs both integration + stdio-smoke + existing 110 unit tests. All pass. Integration tests <5s total. Stdio smoke tests <15s total.

### ADR-027: Capturing logger — `CapturingLogger<T>` for LDD `[IMP:7-10]` verification
- **Status:** Active (M8)
- **Context:** M8 AC requires `[IMP:7-10]` LDD markers present in logs on all key paths (search, unavailability, timeout). Existing unit tests use `NullLogger`/`TestLogger` (no-op) — they verify OUTPUT but not LOG CONTENT. This is the GREEN TEST TRAP (mode-debug): 100% green tests without semantic verification. `@qa` must verify logs match contracts, not just green tests.
- **Decision:** Create `tests/McpWebSearchService.Tests/CapturingLogger.cs` — `internal sealed class CapturingLogger<T> : ILogger<T>`. Captures all log entries into a `List<LogEntry>` (thread-safe via `lock`). `LogEntry` record: `{ LogLevel Level, string Message, Exception? Exception }`. `IsEnabled` returns `true` for all levels. The `formatter` callback produces the formatted message string. Integration tests inject `CapturingLogger<WebSearchTools>` / `CapturingLogger<SearXNGClient>` / `CapturingLogger<SearchResultProcessor>` into the DI container (replacing default `ILogger<T>`) and assert on captured entries: `Assert.Contains(capturedLogs, e => e.Message.Contains("[IMP:9]"))`.
- **Consequence:** New file `CapturingLogger.cs` — `internal sealed class` + `internal record LogEntry`. Injected via `services.AddSingleton<ILogger<WebSearchTools>>(capturingLogger)`. Integration tests assert on log content. `csharp-conventions` apply. `lock`-based `List<LogEntry>` is sufficient — each test method creates its own `TestHostFactory` → own `CapturingLogger` instance (no shared state, no concurrent writes).
- **Verify:** Integration tests contain `Assert.Contains(capturedLogs, e => e.Message.Contains("[IMP:9]"))` style assertions. `@qa` verifies these assertions exist and pass (semantic trace verification).

### ADR-028: Tool descriptions — add `[Description]` to `WebSearch` and `FetchAndExtract` methods
- **Status:** Active (M8)
- **Context:** Orchestrator code review found `web_search` and `fetch_and_extract` tool descriptions are EMPTY (`"description":""` in tools/list response). SPEC §2.1/§2.2 define LLM-facing descriptions: web_search = "Searches the internet for current information. Use it when you need data not in your knowledge base, or when you need to check the latest news."; fetch_and_extract = "Loads the content of a web page by URL and returns cleaned text. Use it when the search snippet is insufficient." `[Description]` attributes exist on parameters but NOT on the methods. The MCP SDK may read the tool-level description from `[McpServerTool(Description = "...")]` OR from method-level `[Description]` — `@code` verifies which attribute produces the non-empty description.
- **Decision:** Add description attributes to both `WebSearch` and `FetchAndExtract` methods in `Services/WebSearchTools.cs`, with the SPEC §2.1/§2.2 descriptions. Use `[Description("...")]` (System.ComponentModel) first; if the SDK doesn't read it for the tool-level description, use `[McpServerTool(Description = "...")]` instead. `@code` verifies which works by checking the stdio-smoke test AC5b response. AOT-safety is preserved — `[Description]` is a plain attribute (no reflection at runtime; SDK reads it at tool-registration time via source-generated code).
- **Consequence:** `Services/WebSearchTools.cs` modified — 2 attribute additions, no logic change. `[CHANGES]` updated: `LAST_CHANGE: M8 — added [Description] attributes (ADR-028)`. `dotnet publish -c Release -r win-x64 /p:PublishAot=true` re-verified → 0 × `IL####`. Stdio-smoke test AC5b verifies non-empty descriptions in `tools/list`.
- **Verify:** `dotnet run` → `tools/list` → both tools have non-empty `description` matching SPEC §2.1/§2.2. `dotnet publish -c Release -r win-x64 /p:PublishAot=true` → 0 × `warning IL####`.

### ADR-029: Dockerfile fixes — correct MCR tag format + explicit .csproj in publish (closes DEFERRED AC7/AC9)
- **Status:** Active (M7 fix — verified by local Docker E2E)
- **Context:** M7 Dockerfile was never actually built — AC7 (Docker E2E) and AC9 (Docker AOT verification) were marked DEFERRED as operator-gated. First real `docker build` attempt revealed TWO bugs in the M7 Dockerfile:
  1. **Wrong image tag format:** `FROM mcr.microsoft.com/dotnet:10.0-sdk-aot` — this tag does NOT exist in MCR. `docker pull` fails with `manifest unknown`. The dotnet-docker documentation (supported-tags.md) uses the `repo:tag` format with a slash separator: `mcr.microsoft.com/dotnet/sdk:<version>-aot`, NOT the legacy `mcr.microsoft.com/dotnet:<version>-sdk-aot` format. Verified: `mcr.microsoft.com/dotnet/sdk:10.0-aot` pulls successfully (digest `sha256:47f56dc3...`), `mcr.microsoft.com/dotnet:10.0-sdk-aot` does not.
  2. **`dotnet publish` without explicit project:** The Dockerfile ran `dotnet publish -c Release -r linux-x64 ...` (no project/solution argument). The .NET SDK auto-discovers `McpWebSearchService.slnx` and tries to publish ALL projects in the solution — including `tests/McpWebSearchService.Tests/McpWebSearchService.Tests.csproj`. But `.dockerignore` (ADR-025) excludes `tests/` from the build context → `error MSB3202: The project file "/src/tests/McpWebSearchService.Tests/McpWebSearchService.Tests.csproj" was not found.`
- **Decision:**
  1. Fix the build-stage `FROM` line: `FROM mcr.microsoft.com/dotnet/sdk:10.0-aot AS build` (slash separator, correct repo/tag format). This matches the official dotnet-docker tagging policy (MCR `dotnet/sdk:<version>-aot` for the SDK+AOT-toolchain image).
  2. Fix the publish command: `RUN dotnet publish McpWebSearchService.csproj -c Release -r linux-x64 -o /app/publish /p:PublishAot=true` (explicit `.csproj` argument). Publishing the single main project explicitly avoids the solution-level enumeration of the test project. The `.slnx` is NOT used in the Docker build — it is a dev-time convenience only.
- **Evidence (local Docker E2E — closes DEFERRED AC7/AC9):**
  - `docker build -t mcp-web-search .` → SUCCESS. AOT compilation inside `mcr.microsoft.com/dotnet/sdk:10.0-aot` build-stage took ~138s. Runtime image `debian:bookworm-slim` + ADR-023 deps → final image 177 MB.
  - AOT warnings: 0 × `IL####`. 1 × `CS8601` (nullable, non-AOT, pre-existing in WebSearchTools.cs:98 — not a regression).
  - `docker run -i --rm --init --network searxng-net -e SearXNG__BaseUrl=http://searxng:8080 mcp-web-search` → MCP server starts, responds to JSON-RPC `initialize` (`protocolVersion: 2025-11-25`, `serverInfo: McpWebSearchService v1.0.0.0`) and `tools/list` (2 tools: `web_search`, `fetch_and_extract`).
  - Full E2E `tools/call web_search` with query "dotnet 10 native aot" → returned 5 results (16 raw → Take(5) pipeline applied). LDD markers `[IMP:1-10]` present in stderr logs.
- **Consequence:**
  - `Dockerfile` line 9: `mcr.microsoft.com/dotnet/sdk:10.0-aot` (was `mcr.microsoft.com/dotnet:10.0-sdk-aot`).
  - `Dockerfile` line 19: `dotnet publish McpWebSearchService.csproj ...` (was `dotnet publish ...`).
  - M7 AC7 (Docker E2E) and AC9 (Docker AOT verification) are now RESOLVED (no longer DEFERRED).
  - Future AOT SDK upgrades: use `mcr.microsoft.com/dotnet/sdk:<major.minor>-aot` (e.g. `sdk:11.0-aot` for .NET 11). Do NOT revert to the `dotnet:<version>-sdk-aot` format.
  - Future Dockerfile changes: always specify the project explicitly in `dotnet publish` — never rely on solution auto-discovery in a Docker build context where `.dockerignore` may exclude referenced projects.
- **Verify:** `Dockerfile` line 9 contains `mcr.microsoft.com/dotnet/sdk:10.0-aot` (slash, no `-sdk-aot` suffix). Line 19 contains `dotnet publish McpWebSearchService.csproj`. `docker build -t mcp-web-search .` succeeds with 0 × `IL####`.

### ADR-030: `web-search-mcp` as built-in research tool for GRACE phase agents
- **Status:** Active (post-M8 — meta-protocol enhancement)
- **Context:** The `web-search-mcp` MCP server (this project) is registered in `.opencode/opencode.json` and provides `web_search` + `fetch_and_extract` tools to opencode. The GRACE Protocol rules did not formally define when/how agents use web search. The Anti-Loop Protocol says "use search" at counter≥3 but doesn't specify which tool, and doesn't handle MCP unavailability. Five unavailability scenarios exist: S1 (Docker not running), S2 (image not built), S3 (SearXNG down → "Search service temporarily unavailable"), S4 (target URL unreachable), S5 (MCP call timeout).
- **Decision:** Formalize web search integration via `$START_WEB_SEARCH_PROTOCOL` in `rules.md` + agent prompt amendments (`debug.md`, `architect.md`, `orchestrator.md`). Key design principle: **search is OPTIONAL, never blocking**. The degradation ladder handles all 5 unavailability scenarios: each maps to a specific agent response (log `[WEB_SEARCH_UNAVAILABLE]` + proceed). Critical rule: **failed search does NOT increment the anti-loop counter** — only failed code fix attempts increment it. At counter≥3, search is best-effort; if unavailable, agents generate alternative hypotheses (broader grep, simplified hypothesis, minimal repro test). Source tagging convention: `[SOURCE: web_search, query="...", ts=ISO8601]` on all search-derived facts; untagged = unverified hypothesis. Context budget: max 3 search calls + 3 fetch calls per dispatch, ≤15,000 chars total. Language policy: `en` for technical queries, `null` for general.
- **Consequence:** GRACE Protocol is resilient to `web-search-mcp` unavailability. Agents degrade gracefully — they log `[WEB_SEARCH_UNAVAILABLE]` and continue. Anti-loop counter semantics are preserved (code-fix attempts, not tool availability). `@architect` marks unverifiable claims as `[UNVERIFIED]` in plans. `@qa` can audit `[SOURCE]`-tagged claims for credibility. The `dogfooding` pattern (project uses its own MCP server) creates a feedback loop: if the server has issues, the agents developing it experience them first-hand.
- **Verify:** `rules.md` contains `$START_WEB_SEARCH_PROTOCOL` with degradation ladder (5 scenarios). `debug.md` contains Step 2b with S1-S5 handling. `architect.md` contains External Knowledge Gap Detection + Version Verification (both with `[WEB_SEARCH_UNAVAILABLE]` fallback). `orchestrator.md` Anti-Loop Gate says "best-effort" at counter≥3/4. `AGENTS.md` contains this ADR-030.

### ADR-032: Semantic category selection via local LLM (M9)
- **Status:** Active (M9)
- **Context:** M8 diagnostic confirmed the fallback `categories=general` is unstable on the SearXNG instance (general engines blocked by CAPTCHA/access-denied/timeout → 0–1 results) while `it` stably returns 100+ (`unresponsive: none`). Operator requirement: do NOT hardcode `categories="it"`; instead select the category **semantically by the query's meaning** on the MCP side via a local LLM server, but ONLY when the agent omits an explicit category. M9 practical probe on `Qwen3.8-27B-Q8_0.gguf` (OpenAI-compatible `/v1/chat/completions`): the reasoning model, without disabling thinking, spends ~9.5–10s and never returns a single-word `content` (`finish_reason=length`, tokens consumed by `reasoning_content`) — breaking both the 5s timeout and single-word classification. With `chat_template_kwargs: {"enable_thinking": false}` + `temperature: 0` + `max_tokens=10` + strict "one-word" prompt, it answers a valid category in ~0.6–1.1s (`finish_reason=stop`).
- **Decision:**
  - Only when the agent omits `categories` (now default `null`, not `"general"`): infer the category via the local LLM. Explicit agent `categories` retain UNCONDITIONAL priority (zero LLM latency — the classifier short-circuits).
  - `ICategoryInferenceClient` (public interface, DI-registered via `AddHttpClient<ICategoryInferenceClient, LocalLlmCategoryInference>()`) → `LocalLlmCategoryInference` posts an OpenAI-compatible `/v1/chat/completions` request using a dedicated AOT-safe source-generated `Serialization/LocalLlmJsonContext` (no reflection, no SDK libs). Request: `model`, `messages` (system single-word prompt + user query), `temperature:0`, `max_tokens≈10`, and **`chat_template_kwargs: {"enable_thinking": false}`** (mandatory for reasoning models, per probe). Strict closed-set validation of `choices[0].message.content` against the CONFIGURABLE `CategoryInferenceSettings.Categories` (case-insensitive, single token); any mismatch → invalid. **Any failure/timeout/bad-JSON/invalid returns `null` (never throws).** Per-call timeout = `CategoryInferenceSettings.TimeoutSeconds` (default 5s) via a linked `CancellationTokenSource`.
  - `Services/SearchCategoryClassifier.cs` — `internal static class` (pattern of TitleSimilarity/HtmlTextExtractor): priority `explicitCategories` → LLM-inferred → `general` fallback. Fallback gated by `CategoryInferenceSettings.EnableFallback` (default true; false → empty string, SearXNG's own general default applies).
  - `Configuration/CategoryInferenceSettings.cs` — sealed class with public setters (ADR-006 AOT binder): `BaseUrl`, `Model`, `TimeoutSeconds=5`, `EnableFallback=true`, `Categories[]` (default `it, news, science, images, videos, music, books, weather, map, general`; NOT hardcoded — the full 32-category SearXNG list is configurable if desired). `appsettings.json`: `SearXNG.DefaultLanguage` → `"auto"`, plus a `CategoryInference{...}` section.
  - `WebSearchTools` gains `ICategoryInferenceClient` + `IOptions<CategoryInferenceSettings>` and computes `Categories` via `SearchCategoryClassifier.InferCategoryAsync` instead of `categories ?? "general"`.
- **Consequence:** New files `Configuration/CategoryInferenceSettings.cs`, `Services/ICategoryInferenceClient.cs`, `Services/LocalLlmCategoryInference.cs`, `Services/SearchCategoryClassifier.cs`, `Models/LocalLlmDto.cs`, `Serialization/LocalLlmJsonContext.cs` (internal context — the DTOs are internal). Modified: `Services/WebSearchTools.cs`, `Program.cs`, `appsettings.json`. DI-integration tests via `MockLlmHandler` in `TestHostFactory`. Non-IT/general queries still fall back to `general` (tested) — no regression. LLM unavailability never hangs search (deterministic `general` fallback; ≤ one LLM call per `web_search`, bounded by the 5s timeout).
- **Verify:** `dotnet test` — all M2–M8 + new M9 tests pass. `dotnet publish -c Release -r win-x64 /p:PublishAot=true` — 0 × `IL####`. Control MCP-stdio calls on the native binary: (a) IT query w/o category → category `it`, results; (b) neutral query → `general`; (c) explicit `categories="news"` → `news`; (d) LLM down → `general` fallback, no hang. `SearchCategoryClassifierTests` cover (a)/(b)/(c)/(d); `LocalLlmCategoryInferenceTests` cover success/HTTP-fail/timeout/bad-JSON/closed-set/invalid.

### ADR-033: HTTP transport (streamable HTTP via ModelContextProtocol.AspNetCore) + JIT — supersedes AOT hard-constraint wording
- **Status:** Active (M10)
- **Context:** The MCP server was designed as a per-session stdio process (Native AOT binary, `docker run -i --rm`). Operator requirement: the MCP server must be a long-lived service decoupled from client launches — a single `docker run -d` container serves any number of MCP clients over HTTP. The streamable HTTP transport requires ASP.NET Core (Kestrel), which Native AOT does not support. Therefore M10 simultaneously transitions the project from stdio → streamable HTTP AND from AOT → JIT.
  - The `ModelContextProtocol.AspNetCore 1.2.0` NuGet package provides `WithHttpTransport()` + `MapMcp()` — confirmed by reflection of the package API. Version 1.2.0 matches the core `ModelContextProtocol 1.2.0`.
  - MCP server.json schema (2025-10-17, fetched from `https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json`) defines `StreamableHttpTransport: { type: "streamable-http", url: string }` and a top-level `remotes[]` field for already-running HTTP endpoints.
  - `Microsoft.AspNetCore.Mvc.Testing 10.0.11` (latest stable, NuGet, 2026-08-11) provides `WebApplicationFactory<TEntryPoint>` for in-memory TestServer-based HTTP integration tests.
  - [SOURCE: webfetch, url="https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json", ts=2026-09-08T12:00:00Z]
  - [SOURCE: webfetch, url="https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing", ts=2026-09-08T12:00:00Z]
- **Decision:**
  1. **Transport:** `AddMcpServer().WithHttpTransport(o => o.ConnectionRequestTimeout = TimeSpan.FromSeconds(30)).WithTools<WebSearchTools>()` + `app.MapMcp(mcpPath)` where `mcpPath` comes from `McpServerSettings.Path` (default "/mcp"). Kestrel binds to `McpServerSettings.Port` (default 8080) via `app.Run($"http://0.0.0.0:{port}")`.
  2. **SDK + AOT:** `Microsoft.NET.Sdk.Web` + `PublishAot=false`. The AOT hard constraint (SPEC §5.1, AGENTS.md "Hard technical constraints") is **superseded** — JIT is the new mode. Source-gen `JsonSerializerContext` (McpJsonContext, LocalLlmJsonContext) is **retained** (good practice under JIT, avoids reflection, no churn).
  3. **Superseded ADRs (AOT-specific wording no longer hard constraints):**
     - **ADR-002** (Cross-OS AOT verification via win-x64 proxy) — **SUPERSEDED**: JIT `dotnet publish` is cross-OS (framework-dependent deployment works on any OS). No need for win-x64 proxy. The `win-x64` RID is no longer special. The AOT-specific `dotnet publish -c Release -r win-x64 /p:PublishAot=true` verification is NO LONGER REQUIRED. Under JIT, verification is `dotnet build` + `dotnet test` (JIT compiles at runtime, no trim warnings to check).
     - **ADR-006** (EnableConfigurationBindingGenerator + Hosting 10.x for AOT-safe binding) — **PARTIALLY SUPERSEDED**: the AOT-safety rationale is no longer a hard constraint, but `EnableConfigurationBindingGenerator=true` is **KEPT** (harmless, reflection-free binding, no churn). `Microsoft.Extensions.Hosting 10.0.9` is **KEPT** (required by `WebApplication.CreateBuilder`).
     - **ADR-007** (Manual Circuit Breaker, no Polly — AOT rationale) — **CONSCIOUS CARRY-FORWARD**: the AOT rationale weakens (Polly v8 is JIT-safe), but the manual CB is already implemented, tested (29+ tests), and adds zero dependencies. No benefit to switching to Polly. The manual CB stays.
     - **ADR-018** (web_search return type pre-serialized JSON string for AOT) — **CONSCIOUS CARRY-FORWARD**: the AOT rationale weakens, but the pre-serialized string approach is harmless and already implemented. Keep.
  4. **Still-active ADRs (unaffected by transport change):** ADR-001 (RuntimeIdentifiers plural — kept, harmless), ADR-003 (`InvariantGlobalization=true` — KEPT, harmless under JIT), ADR-004 (binary name McpWebSearchService — still used as DLL/assembly name), ADR-005 (.slnx), ADR-008–016 (business logic, pipeline, DTOs — unaffected), ADR-017 (CB DI registration), ADR-019–028 (tool registration, param names, descriptions — unaffected), ADR-029 (Dockerfile fixes — explicit-csproj lesson carries forward to M10 Dockerfile), ADR-030 (web-search-mcp as research tool — meta-protocol, unaffected), ADR-032 (category inference — unaffected).
  5. **Dockerfile:** JIT build (`mcr.microsoft.com/dotnet/sdk:10.0` — NOT `sdk:10.0-aot`; `dotnet publish McpWebSearchService.csproj -c Release -o /app/publish /p:SelfContained=false /p:PublishSingleFile=false` — NO `/p:PublishAot=true`, framework-dependent). Runtime `mcr.microsoft.com/dotnet/aspnet:10.0` (includes Kestrel + ICU + OpenSSL + ca-certificates — supersedes ADR-023 manual apt-get + ADR-024 `debian:bookworm-slim` base). `EXPOSE 8080` + `HEALTHCHECK` probing `/healthz` (dedicated lightweight GET endpoint added to Program.cs — avoids MCP protocol overhead in health probe). `curl` installed for HEALTHCHECK. `ENTRYPOINT ["dotnet", "McpWebSearchService.dll"]` (JIT — NOT native binary).
  6. **Manifest:** `.mcp/server.json` — `remotes[]` with `{ "type": "streamable-http", "url": "http://localhost:8080/mcp" }`. Supersedes `packages[].transport` stdio command. Per MCP server.json schema (2025-10-17): `remotes[]` is the top-level field for already-running HTTP endpoints (vs `packages[]` for installable stdio packages). `packages[]` may be removed or retained for Docker image metadata.
  7. **Tests:** `StdioSmokeTests.cs` REPLACED by `HttpSmokeTests.cs` — uses `WebApplicationFactory<Program>` (in-memory TestServer via `Microsoft.AspNetCore.Mvc.Testing 10.0.11`) to POST JSON-RPC to `/mcp` (initialize → tools/list). `IntegrationTests.HardConstraints_Verified` — doc-comment updated (remove AOT mention); assertions unchanged (AOT-independent: types/attributes/JsonSerializerContext/no-Newtonsoft/CB/pipeline/DTO). `Program.cs` gets `public partial class Program { }` for `WebApplicationFactory<Program>` accessibility.
  8. **`/healthz` endpoint:** `app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))` added to Program.cs — lightweight GET for Docker HEALTHCHECK, no MCP protocol overhead. Standard ASP.NET Core minimal API endpoint.
- **Consequence:**
  - `McpWebSearchService.csproj`: SDK `Microsoft.NET.Sdk.Web`, `PublishAot=false`, `ModelContextProtocol.AspNetCore 1.2.0` added. `InvariantGlobalization=true` + `EnableConfigurationBindingGenerator=true` KEPT. `SelfContained`/`PublishSingleFile` KEPT in csproj (overridden at Docker publish time via `/p:SelfContained=false /p:PublishSingleFile=false`).
  - `Program.cs`: `WebApplication.CreateBuilder(args)` + `WithHttpTransport` + `MapMcp` + `app.Run($"http://0.0.0.0:{port}")` + `public partial class Program { }` + `app.MapGet("/healthz", ...)`.
  - `Configuration/McpServerSettings.cs`: `Port` (default 8080) + `Path` (default "/mcp") with validation.
  - `appsettings.json`: `McpServer.Port` + `McpServer.Path` added.
  - `Dockerfile`: JIT build + aspnet:10.0 runtime + EXPOSE 8080 + HEALTHCHECK + curl + ENTRYPOINT dotnet.
  - `.mcp/server.json`: `remotes[]` streamable-http.
  - `tests/`: `StdioSmokeTests.cs` → `HttpSmokeTests.cs` (WebApplicationFactory<Program>). Test csproj adds `Microsoft.AspNetCore.Mvc.Testing 10.0.11`. `IntegrationTests.HardConstraints_Verified` doc-comment updated.
  - SPEC §5.1 AOT constraint is CANCELLED/REPLACED by JIT + HTTP transport. SPEC §5.1 Dockerfile structure is updated (aspnet:10.0 base, not debian:bookworm-slim + manual apt-get).
- **Verify:**
  - `.csproj`: `Sdk="Microsoft.NET.Sdk.Web"`, `PublishAot=false`, `ModelContextProtocol.AspNetCore 1.2.0`.
  - `Program.cs`: `WebApplication.CreateBuilder` + `WithHttpTransport` + `MapMcp` + `MapGet("/healthz")` + `public partial class Program {}`.
  - `McpServerSettings`: `Port` (int, default 8080, validation 0<Port≤65535) + `Path` (string, default "/mcp", validation non-empty).
  - `dotnet build` + `dotnet test` — PASS (M2–M9 regression + M10 HTTP-smoke tests).
  - `Dockerfile`: `sdk:10.0` build (NOT `sdk:10.0-aot`), `aspnet:10.0` runtime (NOT `debian:bookworm-slim`), `EXPOSE 8080`, `HEALTHCHECK`, `ENTRYPOINT ["dotnet", "McpWebSearchService.dll"]`.
  - `.mcp/server.json`: `remotes[].type == "streamable-http"`, `remotes[].url == "http://localhost:8080/mcp"`.
  - `HttpSmokeTests.cs`: `WebApplicationFactory<Program>` + POST JSON-RPC to `/mcp` → initialize handshake + tools/list (2 tools, non-empty descriptions).
  - (Operator-gated) `docker run -d -p 8080:8080 mcp-web-search` → HTTP service starts; `curl http://localhost:8080/healthz` → 200 OK; MCP `initialize` + `tools/list` via HTTP POST → valid JSON-RPC responses.

### ADR-034: Main project relocated to `src/McpWebSearchService/`
- **Status:** Active (M12 — restructure)
- **Context:** The main service project was created at the repo root by the `mcpserver --aot` template (M1, ADR-001/004/005) and kept accumulating folders (`Configuration/`, `Models/`, `Serialization/`, `Services/`, `Program.cs`, `WazuhAudit.cs`, `appsettings.json`, `packages.lock.json`) alongside repo-level and test artifacts — the root became a mix of source and non-source files. The test project already lived cleanly under `tests/`. `.gitlab-ci.yml` build artifacts already assumed a `src/**` layout. The user requested the standard `src/<Project>/` split.
- **Decision:** Move the main project files into `src/McpWebSearchService/` (via `git mv`, preserving history). C# namespaces are explicit (`McpWebSearchService.*`) and folder-independent, so **no `.cs` source changes** were needed. Repo-level files (`McpWebSearchService.slnx`, `Dockerfile`, `.dockerignore`, `.mcp/server.json`, `README.md`, `AppGraph.xml`, `SPEC.md`, `milestones/`, `.gitlab-ci.yml`) stay at the repo root. `milestones/*.md` are frozen historical specs and are intentionally NOT rewritten (their paths reflect the state at that milestone).
  - `McpWebSearchService.slnx`: main project path → `src/McpWebSearchService/McpWebSearchService.csproj`; `<Folder Name="/src/">` added.
  - `tests/.../McpWebSearchService.Tests.csproj`: `ProjectReference` → `..\..\src\McpWebSearchService\McpWebSearchService.csproj`.
  - `src/.../McpWebSearchService.csproj`: `..\`-prefixed includes for repo-root files (`.mcp\server.json`, `README.md`); the former root-level `Compile/Content Remove` glob exclusions were dropped (now out of scope) — only `<Content Remove="McpWebSearchService.csproj" />` kept.
  - `Dockerfile`: `dotnet publish src/McpWebSearchService/McpWebSearchService.csproj …`.
  - `.dockerignore`: `bin/`/`obj/` → `**/bin/`/`**/obj/` (recursive; old patterns matched only the repo root).
  - `tests/McpWebSearchService.Tests/`: flat files regrouped into domain folders (`Configuration/`, `Models/`, `Services/`, `Integration/`, `Smoke/`, `Infrastructure/`) with matching namespaces `McpWebSearchService.Tests.<Folder>` (except `McpWebSearchService.Tests.Services` — the `Services.WebSearchTools` qualifier was dropped to `WebSearchTools` since `Tests.Services` would shadow the main `Services` namespace).
- **Consequence:** `dotnet build McpWebSearchService.slnx` / `dotnet test McpWebSearchService.slnx` resolve main under `src/` and tests under `tests/`. All 138 tests pass (M12 verification). Any future `dotnet` command, `DevelopmentPlan.md` Draft Code Graph, `AppGraph.xml` node, or documentation that targets main-project files MUST use the `src/McpWebSearchService/` prefix. `AppGraph.xml` FILE= paths for main-project files and `DevelopmentPlan.md` M10 graph paths were updated accordingly. `.gitlab-ci.yml` needed no change (`src/**/obj/**` artifacts already matched).
- **Verify:** `McpWebSearchService.slnx` points to `src/McpWebSearchService/McpWebSearchService.csproj`. `Dockerfile` publishes `src/McpWebSearchService/McpWebSearchService.csproj`. Test `ProjectReference` uses `..\..\src\McpWebSearchService\McpWebSearchService.csproj`. `dotnet build` + `dotnet test McpWebSearchService.slnx` — 0 errors, 138/138 tests pass. No main-project `.cs` file remains at the repo root.

### How to add a new ADR
When `@architect` makes a decision that constrains future milestones (technology choice, pattern selection, deviation from SPEC or milestone text), append a new `ADR-NNN` subsection here with: Status, Context, Decision, Consequence, Verify. Do not edit past ADRs in-place — supersede them with a new ADR that references the old one.
