# M1 — Project scaffolding

**Stage:** M1
**Predecessors:** —
**Successors:** M2, M3
**Requirement source:** `SPEC.md` §1, §4.1, §6.1 (step 1)

## Goal

Create the .NET 10 project skeleton from the MCP template, verify the AOT compatibility of the base
build, and establish the directory structure for subsequent stages.

## Context and constraints

- Target framework: **.NET 10**, `net10.0`.
- Template: `Microsoft.McpServer.ProjectTemplates` (includes the MCP SDK `ModelContextProtocol`).
- Native AOT enabled in `.csproj`: `<PublishAot>true</PublishAot>`, RID `linux-x64`.
- AOT safety is non-negotiable from day one (SPEC §4.1): no `Newtonsoft.Json`, no reflection-based
  serialization, no dynamic code generation.
- Bash permissions (`opencode.json`): `dotnet *` auto-allowed for `@architect`; `dotnet build*` /
  `dotnet test*` for `@code`.

## Draft Code Graph

```xml
<DraftCodeGraph>
  <Project_csproj FILE="McpWebSearchService.csproj" TYPE="PROJECT">
    <annotation>.NET 10, PublishAot=true, RID linux-x64, MCP SDK.</annotation>
    <Project_TargetFramework PROPERTY="TargetFramework" TYPE="PROPERTY" VALUE="net10.0" />
    <Project_PublishAot PROPERTY="PublishAot" TYPE="PROPERTY" VALUE="true" />
    <Project_Rid PROPERTY="RuntimeIdentifier" TYPE="PROPERTY" VALUE="linux-x64" />
    <Project_ImplicitUsings PROPERTY="ImplicitUsings" TYPE="PROPERTY" VALUE="enable" />
    <Project_Nullable PROPERTY="Nullable" TYPE="PROPERTY" VALUE="enable" />
  </Project_csproj>

  <Program_cs FILE="Program.cs" TYPE="ENTRYPOINT">
    <annotation>MCP server host from the template; minimal bootstrap, extended in M6.</annotation>
  </Program_cs>
</DraftCodeGraph>
```

## Step-by-step Data Flow

1. Install the `Microsoft.McpServer.ProjectTemplates` template (`dotnet new install`).
2. Create the project from the template (remember the solution/project name — it is used by the M7 ENTRYPOINT).
3. Enable `PublishAot=true` and `RuntimeIdentifier=linux-x64` in `.csproj`.
4. Run a trial AOT publish to verify: `dotnet publish -c Release -r linux-x64 /p:PublishAot=true`
   (AOT trim-analysis warnings are blocking).
5. Establish the directory structure for subsequent stages: `Models/`, `Services/`, `Tools/`,
   `Configuration/` (empty/`_Placeholder` directories are unnecessary — directories are created as
   files appear in M2–M6).

## Acceptance Criteria

- [x] Project builds: `dotnet build` without errors. *(verified M1, commit `23476ee` — 0 warnings, 0 errors)*
- [x] AOT publish passes without trim-analysis warnings: `dotnet publish -c Release -r linux-x64 /p:PublishAot=true`.
  - **Deviation per ADR-002:** `linux-x64` AOT verification is not possible on a Windows host
    (cross-OS native compilation not supported). Verified locally via `win-x64` (0 `IL####` warnings;
    trim analysis is RID-independent). The `linux-x64` AOT build is deferred to the M7 Docker build stage.
- [x] `dotnet --version` confirms the .NET 10 SDK. *(verified — 10.0.300)*
- [x] In `.csproj`: `TargetFramework=net10.0`, `PublishAot=true`, `RuntimeIdentifier=linux-x64`,
  `Nullable=enable`, `ImplicitUsings=enable`.
  - **Deviation per ADR-001:** instead of the singular `RuntimeIdentifier=linux-x64`, the template's
    plural `RuntimeIdentifiers=...;linux-x64;...` is used. The singular form would break local
    `dotnet build`/`restore` on Windows. The production RID `linux-x64` is passed via the CLI `-r` at
    publish time.
- [x] The template's MCP server host starts (`dotnet run`) and does not crash at startup.
  *(verified — host alive ≥3s, stdio transport reading messages, clean exit on stdin close)*
- [x] The solution contains no `Newtonsoft.Json` and no non-AOT-compatible packages.
  *(verified — `project.assets.json` scan: 40 packages, all AOT-safe, `HasNewtonsoftJson=False`)*

## Risks

- The MCP template may pull in dependencies with AOT warnings → inspect the full `dotnet publish` output
  and fix before moving on to M2.
- The template's binary name must match the Dockerfile ENTRYPOINT (M7) — fix it and do not rename
  without updating M7.
