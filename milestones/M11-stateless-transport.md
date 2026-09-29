# M11 — Stateless MCP transport + SearXNG blocked_domains

**Stage:** M11
**Predecessors:** M10 (HTTP transport)
**Successors:** —
**Requirement source:** diagnostics after M10 — intermittent `Bad Request: A new session can only be created by an initialize request` errors when calling `web_search`/`fetch_and_extract`. opencode uses a stateless approach for remote MCP (each request is a new session without a Session-Id), while the MCP server requires session initialization. `o.Stateless = true` is required in `HttpServerTransportOptions`.

## Goal

1. Enable the MCP transport's stateless mode — opencode can then call tools without a prior `initialize` session.
2. (Optional, SearXNG configuration) Add `blocked_domains` to `settings.yml` to filter noisy domains (MDN, Docker Hub) at the SearXNG level.

## Changes

### 1. Stateless MCP transport

| File | Line | Before | After |
|---|---|---|---|
| `Program.cs` | 119 | `.WithHttpTransport(o => o.IdleTimeout = TimeSpan.FromSeconds(30))` | `.WithHttpTransport(o => { o.IdleTimeout = TimeSpan.FromSeconds(30); o.Stateless = true; })` |
| `HttpSmokeTests.cs` | 65-163 | Removed Mcp-Session-Id checks; tools/list is called without initialize | Tests adapted to the stateless mode |

### 2. SearXNG settings.yml — blocked_domains + engine disabling

```yaml
search:
  blocked_domains:
    - developer.mozilla.org
    - hub.docker.com
    - docker.com

engines:
  - name: mdn
    disabled: true
  - name: docker hub
    disabled: true
```

## Verification

1. `dotnet build McpWebSearchService.slnx` — successful build
2. `dotnet test McpWebSearchService.slnx` — all tests PASS (138/138)
3. After deployment: 5 consecutive `web_search` calls via MCP — no session errors

## Acceptance Criteria

- `o.Stateless = true` added in `Program.cs`
- All tests pass (138/138)
- After deployment, the MCP server does not return `Bad Request: A new session can only be created by an initialize request`
