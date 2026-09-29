# Milestones — execution graph

Breakdown of the MCP service implementation into stages per `SPEC.md`. Each stage is an independent
deliverable with verifiable acceptance criteria. Spec files: `M{N}-{description}.md`.

## Dependency graph (DAG)

```
M1 Scaffolding
  |
  +---> M2 Configuration
  |       |
  |       +---------------------------+
  |                                   |
  +---> M3 DTOs + JsonSerializerContext
          |                           |
          |                           |
          v                           v
     M4 SearXNG HTTP Client      M5 Post-processing Pipeline
     (IHttpClientFactory,        (NormalizeUrl → Deduplicate
      15s timeout,                → Filter, HashSet O(1) dedup)
      Circuit Breaker)                 |           |
          |                             |           |
          +-------------+---------------+           |
                        |                             |
                        v                             |
                   M6 MCP Tools Integration <--------+
                   ([McpServerToolType], web_search,
                    fetch_and_extract, wiring)
                        |
          +-------------+-------------+
          |                           |
          v                           v
     M7 Docker + AOT Publish     M8 E2E Integration Testing
     (multi-stage Dockerfile,    (MCP over stdio, timeout
      PublishAot=true)            handling, Docker run)
          |                           |
          +-------------+-------------+
                        |
                        v
                   M9 Category Inference LLM
                        |
                        v
                  M10 HTTP Transport Service
                        |
                        v
                  M11 Stateless MCP Transport
```

## Stages and dependencies

| Stage | Description | Predecessors | Can run in parallel with | Status |
|------|----------|-----------------|---------------------|--------|
| M1 | Project scaffolding (.NET 10, MCP template, AOT verification) | — | — | ✓ complete (commit `23476ee`) |
| M2 | Configuration layer (`SearXNGSettings`, `IOptions`, `appsettings.json`) | M1 | M3 | ✓ complete |
| M3 | DTOs and AOT-safe serialization (`JsonSerializerContext`) | M1 | M2 | pending |
| M4 | SearXNG HTTP client (`IHttpClientFactory`, typed client, 15s timeout, Circuit Breaker) | M2, M3 | M5 | pending |
| M5 | Post-processing pipeline (`SearchResultProcessor`: Normalize → Dedup → Filter) | M2, M3 | M4 | pending |
| M6 | MCP tools integration (`[McpServerToolType]`, `web_search`, `fetch_and_extract`) | M4, M5 | — | pending |
| M7 | Dockerization and AOT publish (multi-stage Dockerfile, `PublishAot=true`) | M6 | — | pending |
| M8 | E2E testing and acceptance (MCP over stdio, timeout handling, Docker run) | M6, M7 | — | pending |
| M9 | Semantic search-category selection via a local LLM | M2, M6, M8 | M10 | ✓ complete |
| M10 | MCP server as a standalone HTTP service (moving away from AOT) | M1–M9 | M11 | ✓ complete |
| M11 | Stateless MCP transport + SearXNG blocked_domains | M10 | — | ✓ complete |

## Critical path

`M1 → M2 → M4 → M6 → M7 → M8` (or `M1 → M3 → M5 → M6 → ...` — equivalent).

M4 and M5 can run in parallel — both depend on M2+M3 but not on each other.

## Stage conventions

- Each stage is executed by the `@code` agent after `DevelopmentPlan.md` is approved by `@architect`.
- Unit tests (xUnit) are written **inside** each stage (M1–M7) per the `@code` workflow. M8 adds
  integration/E2E tests on top of the finished system.
- All hard constraints from `AGENTS.md` (AOT safety, `JsonSerializerContext`, no Newtonsoft, LDD logging
  `[IMP:1-10]`, `#region`, XML documentation) apply to every stage.
- AOT safety is verified by compiling `dotnet publish -c Release -r linux-x64 /p:PublishAot=true` —
  desirable at M1, mandatory at M7.
- Each stage's acceptance criteria are a checklist for `@qa`.
