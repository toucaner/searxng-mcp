# M2 — Configuration layer

**Stage:** M2
**Predecessors:** M1
**Successors:** M4, M5
**Can run in parallel with:** M3
**Requirement source:** `SPEC.md` §5.2, §6.1 (step 2)

## Goal

Implement a strongly-typed configuration layer through the Options pattern: `SearXNGSettings` and
`McpServerSettings`, bound to `appsettings.json` via `IOptions<T>`, with validation of critical values.

## Context and constraints

- Options pattern: `services.Configure<SearXNGSettings>(config.GetSection("SearXNG"))`.
- `SearXNG` section: `BaseUrl` (string), `MaxResults` (int), `BlockedDomains` (string[]), `DefaultLanguage` (string).
- `McpServer` section: `Name` (string), `Version` (string).
- AOT safety: settings classes are plain records/classes and do not require `JsonSerializerContext`
  (configuration-binder binding is AOT-compatible when static properties are used).
- `appsettings.json` must be copied to the output directory (`CopyToOutputDirectory`).

## Draft Code Graph

```xml
<DraftCodeGraph>
  <Configuration_SearXNGSettings_cs FILE="Configuration/SearXNGSettings.cs" TYPE="SETTINGS">
    <annotation>SearXNG options: BaseUrl, MaxResults, BlockedDomains[], DefaultLanguage.</annotation>
    <SearXNGSettings_CLASS NAME="SearXNGSettings" TYPE="CLASS">
      <SearXNGSettings_BaseUrl PROPERTY="BaseUrl" TYPE="PROPERTY" />
      <SearXNGSettings_MaxResults PROPERTY="MaxResults" TYPE="PROPERTY" />
      <SearXNGSettings_BlockedDomains PROPERTY="BlockedDomains" TYPE="PROPERTY" />
      <SearXNGSettings_DefaultLanguage PROPERTY="DefaultLanguage" TYPE="PROPERTY" />
    </SearXNGSettings_CLASS>
  </Configuration_SearXNGSettings_cs>

  <Configuration_McpServerSettings_cs FILE="Configuration/McpServerSettings.cs" TYPE="SETTINGS">
    <annotation>MCP server identity options: Name, Version.</annotation>
    <McpServerSettings_CLASS NAME="McpServerSettings" TYPE="CLASS">
      <McpServerSettings_Name PROPERTY="Name" TYPE="PROPERTY" />
      <McpServerSettings_Version PROPERTY="Version" TYPE="PROPERTY" />
    </McpServerSettings_CLASS>
  </Configuration_McpServerSettings_cs>

  <appsettings_json FILE="appsettings.json" TYPE="CONFIG">
    <annotation>Configuration per SPEC §5.2.</annotation>
  </appsettings_json>
</DraftCodeGraph>
```

## Step-by-step Data Flow

1. Create `SearXNGSettings` (record/class with `BaseUrl:string`, `MaxResults:int`, `BlockedDomains:string[]`, `DefaultLanguage:string`).
2. Create `McpServerSettings` (`Name:string`, `Version:string`).
3. Create `appsettings.json` with the SPEC §5.2 schema (sections `SearXNG` and `McpServer`).
4. In `Program.cs`, register `Configure<SearXNGSettings>` and `Configure<McpServerSettings>` against the respective sections.
5. Enable validation: `BaseUrl` — non-empty valid URI; `MaxResults` > 0; `DefaultLanguage` — non-empty.
   On invalid configuration — a deterministic startup failure with a clear message (via
   `OptionsBuilder.Validate`/`PostConfigure` or an explicit bootstrap check).

## Acceptance Criteria

- [x] `SearXNGSettings` contains all 4 fields from SPEC §5.2 with correct types. *(verified M2 — `Configuration/SearXNGSettings.cs`: `public sealed class`, `BaseUrl:string=string.Empty`, `MaxResults:int=0`, `BlockedDomains:string[]=[]`, `DefaultLanguage:string=string.Empty`)*
- [x] `McpServerSettings` contains `Name` and `Version`. *(verified M2 — `Configuration/McpServerSettings.cs`: `public sealed class`, `Name:string=string.Empty`, `Version:string=string.Empty`)*
- [x] `appsettings.json` matches the SPEC §5.2 schema and is copied to the output directory. *(verified — `<None Update="appsettings.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>`; the file is present in `bin/Debug`, `bin/Release`, `publish/`)*
- [x] Registration via `IOptions<T>` works: resolving `IOptions<SearXNGSettings>` through DI returns the values from `appsettings.json`. *(verified — unit test `Build_ReturnsBoundValues_ForAllFourFields` via `ConfigurationBuilder` + in-memory config)*
- [x] Validation: starting with an empty `BaseUrl` or `MaxResults<=0` produces a clear startup error. *(verified at runtime — `OptionsValidationException` in `RunAsync()`, the host does not start; covered by 7 negative `[Theory]` cases for SearXNG + 4 for McpServer)*
- [x] Unit tests (xUnit) cover: value binding, validation (positive + negative scenarios), and defaults. *(verified — 17 tests: 4 binding, 2 defaults, 2 positive-validation, 9 negative-validation; 100% PASS)*
- [x] AOT publish still passes without warnings. *(verified — `dotnet publish -c Release -r win-x64 /p:PublishAot=true`: 0 × `IL####`, native binary 12.25 MB; see the ADR-006 deviation below)*
  - **Deviation per ADR-006:** `EnableConfigurationBindingGenerator=true` alone is not enough on net10.0. The source generator in `Configuration.Binder 8.0.2` (transitive from the template-default `Microsoft.Extensions.Hosting 8.0.1`) does NOT intercept `Bind<T>()` — 4 × `IL####` remain (`IL2026`+`IL3050` × 2). Resolved with a two-part fix: (1) `EnableConfigurationBindingGenerator=true` + (2) upgrade `Microsoft.Extensions.Hosting 8.0.1 → 10.0.9` (pulls a compatible `Configuration.Binder 10.x`). Confirmed by an @architect probe on net10.0 (full AOT publish, native binary 12.8 MB).
- [x] Code follows `csharp-conventions` (`#region`, XML docs, LDD `[IMP:1-10]`). *(verified — `SearXNGSettings.cs`/`McpServerSettings.cs`/both test files: `#region MODULE_CONTRACT`+`GREP_SUMMARY`+`STRUCTURE`, `#region CLASS_*`, XML `<summary>`/`<remarks>` with `[PURPOSE]`/`[INVARIANTS]`/`[RATIONALE]`. `Program.cs` is an exception: Options registration only; `#region`/XML deferred to M6.)*

## Risks

- Configuration binder + AOT: use `Configure<T>` with static property access; avoid `ConfigurationBinder.Bind` with reflection where possible. Verify with an AOT publish.
  - **IMPLEMENTED AND CLOSED (ADR-006):** the @architect probe on net10.0 found that `Bind<T>()` produces `IL2026`+`IL3050` even with `EnableConfigurationBindingGenerator=true` if `Microsoft.Extensions.Hosting` stays at the template-default `8.0.1` (pulls `Configuration.Binder 8.0.2`, where the generator does not intercept `Bind<T>()` under net10.0). Fix: generator=true + Hosting 10.0.9. Verified: 0 × `IL####`, native binary. See `AGENTS.md` ADR-006.
