# M7 — Dockerization and AOT publish

**Stage:** M7
**Predecessors:** M6
**Successors:** M8
**Requirement source:** `SPEC.md` §4.1, §5.1, §6.1 (step 7)

## Goal

Create a multi-stage `Dockerfile` for Native AOT publishing and verify that the resulting image runs
without the .NET runtime (a native AOT binary), with a minimal runtime image and the required system
dependencies (`libicu`, etc.).

## Context and constraints

- Multi-stage build (SPEC §5.1): build stage (`mcr.microsoft.com/dotnet:10.0-sdk-aot`) → runtime stage (`debian:bookworm-slim`).
- Publish: `dotnet publish -c Release -r linux-x64 -o /app/publish /p:PublishAot=true`.
- The runtime stage does NOT require the .NET Runtime (native AOT binary); it requires only `libicu-dev` (and other AOT dependencies).
- `ENTRYPOINT ["./McpWebSearchService"]` — the binary name must match the M1 project name (AssemblyName). If it differs, synchronize with M1.
- `.dockerignore` — exclude `bin/`, `obj/`, `.opencode/`, `.git/`, and temporary artifacts.
- Bash permissions: `docker` is NOT auto-allowed in `opencode.json` (requires operator confirmation) — this stage runs with operator involvement; `dotnet publish` is auto-allowed.

## Draft Code Graph

```xml
<DraftCodeGraph>
  <Dockerfile FILE="Dockerfile" TYPE="INFRA">
    <annotation>Multi-stage: sdk-aot build → debian:bookworm-slim runtime, libicu.</annotation>
    <Dockerfile_BuildStage STAGE="build" TYPE="DOCKER_STAGE"
        FROM="mcr.microsoft.com/dotnet:10.0-sdk-aot">
      <Dockerfile_PublishStep STEP="publish" TYPE="DOCKER_STEP"
          COMMAND="dotnet publish -c Release -r linux-x64 -o /app/publish /p:PublishAot=true" />
    </Dockerfile_BuildStage>
    <Dockerfile_RuntimeStage STAGE="final" TYPE="DOCKER_STAGE"
        FROM="debian:bookworm-slim">
      <Dockerfile_AptStep STEP="apt" TYPE="DOCKER_STEP"
          COMMAND="apt-get install libicu-dev" />
      <Dockerfile_EntrypointStep STEP="entrypoint" TYPE="DOCKER_STEP"
          COMMAND="./McpWebSearchService" />
    </Dockerfile_RuntimeStage>
  </Dockerfile>

  <dockerignore FILE=".dockerignore" TYPE="INFRA">
    <annotation>Excludes bin/, obj/, .opencode/, .git/, intermediate artifacts.</annotation>
  </dockerignore>
</DraftCodeGraph>
```

## Step-by-step Data Flow

1. Create the `Dockerfile` per SPEC §5.1 (multi-stage):
   - Stage `build`: `FROM mcr.microsoft.com/dotnet:10.0-sdk-aot`, `WORKDIR /src`, `COPY . .`, `RUN dotnet publish -c Release -r linux-x64 -o /app/publish /p:PublishAot=true`.
   - Stage `final`: `FROM debian:bookworm-slim`, `WORKDIR /app`, `RUN apt-get update && apt-get install -y --no-install-recommends libicu-dev && rm -rf /var/lib/apt/lists/*`, `COPY --from=build /app/publish .`, `ENTRYPOINT ["./McpWebSearchService"]`.
2. Create `.dockerignore` (bin/obj/.opencode/.git/artifacts).
3. Cross-check the binary name (`McpWebSearchService`) against the `AssemblyName`/`OutputName` from the `.csproj` (M1) — on mismatch, update the `ENTRYPOINT` or the project name.
4. AOT publish locally (verification without Docker): `dotnet publish -c Release -r linux-x64 -o /app/publish /p:PublishAot=true` — without trim-analysis warnings.
5. (With operator involvement) Docker build + run: `docker build -t mcp-web-search .` → `docker run --rm mcp-web-search` — the binary starts and answers an MCP request (stdio interaction, as in M8).

## Acceptance Criteria

- [x] `Dockerfile` is multi-stage, strictly per the SPEC §5.1 structure (build + final). [QA: PASS — 2 `FROM` lines: `mcr.microsoft.com/dotnet:10.0-sdk-aot AS build` + `debian:bookworm-slim AS final`; ADR-024]
- [x] The build stage uses `mcr.microsoft.com/dotnet:10.0-sdk-aot`; the publish command contains `PublishAot=true`, RID `linux-x64`. [QA: PASS — `RUN dotnet publish -c Release -r linux-x64 -o /app/publish /p:PublishAot=true`]
- [x] The runtime stage is `debian:bookworm-slim`, installs `libicu-dev` (and the required AOT dependencies), and does not contain the .NET Runtime. [QA: PASS — ADR-023: `libicu-dev` replaced by the full runtime-deps set `ca-certificates libc6 libgcc-s1 libicu72 libssl3 libstdc++6 tzdata`; `libicu-dev` is a dev package (headers) while runtime needs `libicu72`; `libssl3`+`ca-certificates` are required for HttpClient HTTPS]
- [x] The `ENTRYPOINT` points to the actual built binary name (cross-checked against the M1 `.csproj`). [QA: PASS — `ENTRYPOINT ["./McpWebSearchService"]`, ADR-004; the binary name is confirmed by `dotnet publish` output — `McpWebSearchService.exe` (win-x64) / `McpWebSearchService` (linux-x64)]
- [x] `.dockerignore` excludes `bin/`, `obj/`, `.opencode/`, `.git/`, and temporary files. [QA: PASS — ADR-025: bin/, obj/, .git/, .opencode/, tests/, .test_counter.json, IDE files, logs, temp]
- [x] The local AOT publish (`dotnet publish ... PublishAot=true`) passes without trim-analysis warnings. [QA: PASS — `dotnet publish -c Release -r win-x64 /p:PublishAot=true` (ADR-002 proxy): 0 × `warning IL####`, native binary 16.52 MB; cross-OS `linux-x64` is not possible on Windows — verified inside the Docker build stage]
- [x] (With the operator) `docker build` succeeds; `docker run` starts the MCP server. [RESOLVED via ADR-029 — local Docker E2E: `docker build -t mcp-web-search .` SUCCESS (image 177 MB, ~138s AOT); `docker run -i --network searxng-net` → MCP server starts, JSON-RPC `initialize` returns `protocolVersion: 2025-11-25`, `serverInfo: McpWebSearchService v1.0.0.0`; full E2E `tools/call web_search` returns 5 results (16 raw → Take(5))]
- [x] The runtime image size is minimal (no SDK, no .NET Runtime) — verify via `docker images`. [RESOLVED via ADR-029 — `docker images mcp-web-search` → 177 MB (debian:bookworm-slim + AOT binary + ADR-023 runtime deps; NO .NET SDK, NO .NET Runtime)]
- [x] Code/infra files follow the conventions (the Dockerfile is not `.cs` but requires per-stage comments). [QA: PASS — `# Stage 1: Build`, `# Stage 2: Runtime`, comments for each ADR-023 dependency; `.dockerignore` — comments per group]

## Risks

- The M1 template binary name may not match `McpWebSearchService` → verify before building; pin it in M1.
- AOT requires additional system libraries in the runtime image beyond `libicu` (e.g., `libssl`, the `libc` version) → on startup failure, add them as needed and document in the `Dockerfile`.
- Docker build requires network access and operator confirmation (bash `docker` is not auto-allowed) — plan for the time; the AOT publish can be verified locally without Docker.
- Windows development environment: the `linux-x64` AOT publish may require cross-compilation; on problems, publish inside the Docker build stage.
