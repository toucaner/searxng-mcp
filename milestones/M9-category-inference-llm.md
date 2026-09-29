# M9 — Semantic search-category selection via a local LLM

**Stage:** M9
**Predecessors:** M2 (Configuration), M6 (MCP Tools Integration), M8 (E2E testing + acceptance)
**Successors:** —
**Requirement source:** `SPEC.md` §2.1 (the `categories` parameter); the diagnostic result (M8): the `general` category is unstable in SearXNG (engines are blocked by CAPTCHA/access-denied/timeout → 0–1 result), while the `it` category consistently returns 100+ results (`unresponsive: none`). Operator requirement: do not hardcode `categories="it"`, but select the category **semantically, based on the query meaning on the MCP side**, using a local AI server.

## Goal

Make `web_search` semantically correct: when the LLM agent calls the tool **without an explicit
category**, `McpWebSearchService` itself determines the category from the query meaning — via the local
LLM server, not a hardcoded `general` default. **The set of categories the LLM chooses from is
configurable** (see `CategoryInferenceSettings.Categories`), so it is not hardcoded and can be adapted to
a specific SearXNG instance. By default — a practical subset that is both sufficient and stable (e.g.
`it, news, science, images, videos, music, books, weather, map, general`), with a `general` fallback for
the unrecognized. This improves result completeness and stability, removing the unstable `general`,
without breaking non-IT queries (the `general` fallback remains for neutral ones).

At the same time:
- **an explicit `categories` passed by the agent has unconditional priority** (LLM classification only when `categories` is not set);
- when the LLM is unavailable — a **deterministic fallback to `general`**, so search never hangs;
- strict AOT safety and the project conventions are preserved (ADR, `#region`, XML, LDD `[IMP]`, xUnit tests, `InternalsVisibleTo`).

## Context and constraints

- M8 diagnostics (confirmed by facts via direct HTTP and MCP-stdio calls):
  - `categories=general&language=auto` for `q=test` → **1 result** (wikipedia); `unresponsive`: brave/duckduckgo/startpage/qwant/yahoo (CAPTCHA / access denied / timeout).
  - `categories=it&language=auto` for `q=test` → **130 raw / 106 via MCP before Take(10)**; `unresponsive: none`. IT engines (github, mdn, stackoverflow, hoogle, docker hub, askubuntu) **do not block bots**.
  - MCP currently sends `categories=general` (hardcoded default: `WebSearchTools.cs:76/96`, `SearXNGClient.cs:193`) and `language=ru-RU` (from `SearXNGSettings.DefaultLanguage`). The one that works per results is `language=auto`.
- AOT requirement: **no `Newtonsoft`, no reflection/dynamic codegen**. The LLM client is a typed `HttpClient` + source-gen `JsonSerializerContext`. Do not use SDK libraries (OpenAI/`Azure.AI.OpenAI`) if they pull reflection — check in the architecture phase.
- Local LLM server: the address/API format as of this record is **not pinned in the diagnostic environment** (typical local ports Ollama `11434` / LM Studio `1234` on localhost did not respond from the diagnostic host). The address and model are specified by the operator; the client is designed for the chosen format (Ollama `/api/generate` or OpenAI-compatible `/v1/chat/completions`).
- Bash permissions: `dotnet build*/test*` is auto-allowed; `dotnet publish` for the AOT check (ADR-002 `win-x64` proxy) is in the `@architect` context. External LLM server: verify over the network that the LLM is reachable from the MCP environment (if MCP runs in Docker — `host.docker.internal` or a network address).
- `@code` role: implement the `.cs` (client, classifier, configuration, integration) + tests + `tests/test_guide.md`. `@qa` role: independent acceptance (read-only, veto).

## Draft Code Graph

```xml
<DraftCodeGraph>
  <CategoryInferenceSettings_cs FILE="Configuration/CategoryInferenceSettings.cs" TYPE="CONFIG">
    <annotation>LLM server parameters: BaseUrl, Model, TimeoutSeconds (default 5), EnableFallback, Categories[] (configurable category list for the LLM). Sealed class with public setters (AOT binder, ADR-006).</annotation>
  </CategoryInferenceSettings_cs>

  <ICategoryInferenceClient_cs FILE="Services/ICategoryInferenceClient.cs" TYPE="INTERFACE">
    <annotation>Contract: Task&lt;string?&gt; InferCategoryAsync(string query, CancellationToken). null = unavailable/invalid (fallback).</annotation>
  </ICategoryInferenceClient_cs>

  <LocalLlmCategoryInference_cs FILE="Services/LocalLlmCategoryInference.cs" TYPE="SERVICE">
    <annotation>Typed client for the local LLM: JSON-REST (Ollama /v1/chat/completions), source-gen JsonSerializerContext, timeout; any failure → null (never throws).</annotation>
    <LocalLlmCategoryInference_CLASS NAME="LocalLlmCategoryInference" TYPE="SERVICE" />
  </LocalLlmCategoryInference_cs>

  <SearchCategoryClassifier_cs FILE="Services/SearchCategoryClassifier.cs" TYPE="SERVICE">
    <annotation>internal static class. Input: query + explicitCategories. If explicit is non-empty → use it (priority). Otherwise → LLM. LLM unavailable/invalid → general (fallback).</annotation>
    <SearchCategoryClassifier_CLASS NAME="SearchCategoryClassifier" TYPE="SERVICE" />
  </SearchCategoryClassifier_cs>

  <WebSearchTools_cs MODIFY="true" FILE="Services/WebSearchTools.cs" TYPE="SERVICE">
    <annotation>Replace `categories ?? "general"` with `SearchCategoryClassifier.InferCategory(query, categories)`.</annotation>
  </WebSearchTools_cs>

  <CategoryInferenceTests_cs FILE="tests/McpWebSearchService.Tests/SearchCategoryClassifierTests.cs" TYPE="TEST_CLASS">
    <annotation>Classifier tests: explicit-category priority, LLM→it, LLM unavailable→general, invalid response→general.</annotation>
  </CategoryInferenceTests_cs>

  <LocalLlmClientTests_cs FILE="tests/McpWebSearchService.Tests/LocalLlmCategoryInferenceTests.cs" TYPE="TEST_CLASS">
    <annotation>Client tests via a mock HttpMessageHandler: success, failure, timeout, invalid JSON.</annotation>
  </LocalLlmClientTests_cs>
</DraftCodeGraph>
```

## Step-by-step Data Flow

1. **Configuration:** `appsettings.json` — `SearXNG.DefaultLanguage: "auto"`; add a `CategoryInference { BaseUrl, Model, TimeoutSeconds, EnableFallback, Categories[] }` section + `Configuration/CategoryInferenceSettings.cs` (sealed, public setters — for the AOT binder, ADR-006). `Categories` is a configurable list from which the LLM selects a category (a practical subset by default; the full SearXNG category list is in the reference below).
2. **DI:** `Program.cs` — `builder.Services.Configure<CategoryInferenceSettings>(...)` + `AddHttpClient<ICategoryInferenceClient, LocalLlmCategoryInference>()` with a timeout (following `ISearXNGClient` / `AddHttpClient("FetchExtract")`).
3. **LLM client (`LocalLlmCategoryInference`):** an OpenAI-compatible POST `/v1/chat/completions` to `BaseUrl`; body: `model`, `messages` (system + user), `temperature: 0`, `max_tokens ≈ 10–16`, and **`chat_template_kwargs: {"enable_thinking": false}`** — critical for reasoning models (Qwen3); source-gen `JsonSerializerContext` for the request/response DTOs; **strict validation** of `choices[0].message.content` = a single word from the **configurable closed list** `CategoryInference.Categories` (default `it, news, science, images, videos, music, books, weather, map, general`); **any failure/timeout/invalid → `null` (never throw)**; timeout = `CategoryInferenceSettings.TimeoutSeconds` (default **5 s**, sufficient per the probe); LDD `[IMP:5-9]`.
4. **Classifier (`SearchCategoryClassifier`):** the priority logic (explicit → LLM → general). An `internal static class` following `TitleSimilarity`/`HtmlTextExtractor`.
5. **Integration (`WebSearchTools.cs`):** `Categories = SearchCategoryClassifier.InferCategory(query, categories)` instead of `categories ?? "general"`. XML docs + `#region` + LDD per the conventions.
6. **Tests + guide:** `SearchCategoryClassifierTests.cs` + `LocalLlmCategoryInferenceTests.cs` (mock `HttpMessageHandler` via the `TestHostFactory` pattern); update `tests/test_guide.md`.
7. **Documentation:** ADR-032 in `AGENTS.md`; if needed — `DevelopmentPlan.md`/`AppGraph.xml`.
8. **Verification:** `dotnet build`, `dotnet test`, `dotnet publish -c Release -r win-x64 /p:PublishAot=true` (0×`IL####`), control MCP-stdio calls with the native binary.

## Acceptance Criteria

- [ ] `appsettings.json`: `SearXNG.DefaultLanguage == "auto"`; the `CategoryInference` section is present and valid (a `ValidateOnStart`-like check per ADR-006).
- [ ] `Configuration/CategoryInferenceSettings.cs` — a sealed class with public setters (AOT binder).
- [ ] `Services/LocalLlmCategoryInference.cs` — a typed client; the response format is strictly validated against the closed category list; **on any failure/timeout it returns `null`, never throws**; LDD `[IMP:5-9]`.
- [ ] `Services/SearchCategoryClassifier.cs` — an `internal static class`; priority `explicitCategories` → LLM → `general`; fallback to `general` when the LLM is unavailable.
- [ ] `WebSearchTools.cs` — `Categories` is computed via the classifier (not a hardcoded `?? "general"`); an explicit `categories` from the agent keeps priority.
- [ ] Tests `SearchCategoryClassifierTests.cs`: (a) explicit category → that category; (b) LLM answers `it` → `it`; (c) LLM unavailable → `general`; (d) invalid LLM response → `general`.
- [ ] Tests `LocalLlmCategoryInferenceTests.cs`: success / HTTP failure / timeout / invalid JSON.
- [ ] Integration case in `IntegrationTests.cs` (optional): an IT query without a category + mock LLM → 10 results via real DI.
- [ ] `tests/test_guide.md` reflects M9 (AC → test mapping, expected LDD).
- [ ] `AGENTS.md`: ADR-032 recorded (Status/Context/Decision/Consequence/Verify).
- [ ] `dotnet test` — 100% PASS (M2–M8 + the new M9 tests).
- [ ] AOT: `dotnet publish -c Release -r win-x64 /p:PublishAot=true` → **0×`IL####`** (ADR-002).
- [ ] Control MCP-stdio calls: (a) an IT query without a category → category `it`, ≥10 results; (b) a neutral query → `general` (not broken); (c) an explicit `categories="news"` → `news` is used; (d) LLM unavailable → `general` fallback, the service does not hang.
- [ ] `@qa` semantic verification: the logs match the contracts (not just green tests) — `SUCCESS`/`BLOCK`.

## Practical probe on the local LLM

Verified in the M9 environment (real HTTP requests):

- **Server:** `<url>`, OpenAI-compatible `/v1/chat/completions`.
- **Model:** `Qwen3.8-27B-Q8_0.gguf` (Qwen3-27B, Q8).
- **Behavior without disabling thinking (works poorly):**
  - with `max_tokens=20` → `finish_reason=length`, `content` empty, everything goes into `reasoning_content`; ~0.9–2.8 s.
  - with `max_tokens=200` → verbose text instead of a single word (`finish_reason=length`); ~9.5–10 s per request.
  - Conclusion: reasoning consumes tokens and breaks single-word classification; ~10 s exceeds the 5 s timeout and is close to the SearXNG timeout.
- **Solution (works well):** `chat_template_kwargs: {"enable_thinking": false}` + `temperature: 0` + `max_tokens=10` + a strict "one word" prompt:
  | Query | Category | finish | time |
  |---|---|---|---|
  | how to write an xunit test in c# | `it` | `stop` | ~1.09 s |
  | latest weather news | `general` | `stop` | ~0.6 s |
  | study on the effect of caffeine on sleep | `general` | `stop` | ~0.6 s |
  | buy sneakers | `general` | `stop` | ~0.59 s |
  - All categories are valid (from the closed list), `finish_reason=stop`, time **~0.6–1.1 s** → a 5 s timeout is sufficient.
  - Note: the model produces a correct `it` for IT queries; "news"/"science" are recognized less well (→ `general`) — improve the prompt if needed (e.g., add few-shot/markers for `news`/`science`), but the basic mechanism works.
- **Implementation requirement:** the `LocalLlmCategoryInference` client MUST send `chat_template_kwargs: {"enable_thinking": false}` (otherwise a reasoning model will not produce a single word within an acceptable time). This is pinned in Data Flow step 3 and in ADR-032.

### Full SearXNG category list (reference, from `/config` of the instance `<ip>:<port>`)

The actual list of categories supported by SearXNG (all 32, from `config.categories`):
```
general, videos, images, social media, music, packages, it, files, books, news,
apps, software wikis, science, scientific publications, web, repos, other,
currency, icons, weather, map, dictionaries, shopping, lyrics, cargo, movies,
translate, radio, q&a, wikimedia, define
```
Important: the original M9 project assumed a narrow set `[it|news|science|images|videos|general]` — **this is not the full SearXNG list**. Therefore:
- **the list for LLM classification is configurable** (`CategoryInference.Categories`), defaulting to a practical subset (`it, news, science, images, videos, music, books, weather, map, general`);
- the full list (32) can be supplied via configuration if needed, but some categories (`lyrics`, `cargo`, `q&a`, `wikimedia`, `define`, etc.) have almost no engines in the results — classifying "into the void" is ineffective;
- `general` remains the fallback for the unrecognized.

## Risks

- **LLM client AOT compatibility** — the main risk. Solution: a typed `HttpClient` + source-gen `JsonSerializerContext`, no SDK libraries with reflection. Check `AddHttpClient<T>` + `ReadFromJsonAsync<T>(JsonTypeInfo)`; if in doubt — an `@architect` probe on net10.0 (like ADR-006/ADR-008).
- **LLM latency/reliability** — the practical probe (see "Practical probe on the local LLM"): the model `Qwen3.8-27B-Q8_0.gguf` is a reasoning model; **without disabling thinking it reasons for ~9.5–10 s and does not return a single-word `content`** (`finish_reason=length`), which breaks both speed and single-word output. **Solution (confirmed by the probe):** send `chat_template_kwargs: {"enable_thinking": false}` (+ `temperature: 0`, `max_tokens ≈ 10–16`, a strict "one word" prompt) → the model answers with a valid category in **~0.6–1.1 s** (`finish_reason=stop`). The default **5 s** timeout is then sufficient. A mandatory `general` fallback on unavailability/invalid input — search is not blocked (no longer than the timeout, no more than one call per `web_search`).
- **LLM server API format/address not pinned** — the client is designed for the chosen format; the address/model are specified by the operator. If the LLM is unreachable from the MCP environment (Docker) — either `host.docker.internal` or a network address.
- **Regression of non-IT queries** — the `general` fallback preserves the previous behavior for neutral queries; test case (b) guards this.
- **GREEN TEST TRAP** (from `mode-debug`): `@qa` verifies that the logs semantically match the contracts (LDD `[IMP:5-9]` on the classification/fallback path), not just that tests are green.
- **Change in default-category behavior** — if the LLM misclassifies, it may degrade results relative to `general`; protection — the `general` fallback on an invalid response and the priority of an explicit category.
