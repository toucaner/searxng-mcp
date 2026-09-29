# Dockerfile — McpWebSearchService (JIT + ASP.NET Core HTTP transport)
# M10 (ADR-033): supersedes the AOT Dockerfile (sdk:10.0-aot + debian:bookworm-slim + manual apt-get,
# ADR-023/024). The MCP server is now a long-lived streamable-HTTP service on Kestrel — one container
# (`docker run -d -p 8080:8080 mcp-web-search`) serves any number of MCP clients over HTTP.
# Binary/assembly name: McpWebSearchService (ADR-004 — fixed, matches ENTRYPOINT).

# ============================================================
# Stage 1: Build — .NET 10 SDK (JIT, NOT AOT)
# Framework-dependent publish: the runtime is provided by aspnet:10.0 in stage 2.
# NO /p:PublishAot=true (Kestrel requires JIT). Publish the .csproj explicitly
# (ADR-029 lesson — .dockerignore excludes tests/ from the build context, so a
# solution-level publish would fail on the missing test project).
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy source (filtered by .dockerignore — excludes bin/, obj/, tests/, .opencode/, .git/)
COPY . .

# JIT publish (framework-dependent — runtime provided by aspnet:10.0).
# /p:SelfContained=false + /p:PublishSingleFile=false override the csproj defaults
# (csproj carries SelfContained=true/PublishSingleFile=true from the template era;
# the Docker build must stay framework-dependent for a small app layer).
RUN dotnet publish src/McpWebSearchService/McpWebSearchService.csproj -c Release -o /app/publish /p:SelfContained=false /p:PublishSingleFile=false

# ============================================================
# Stage 2: Runtime — ASP.NET Core 10 (Kestrel + ICU + OpenSSL + ca-certificates)
# Supersedes ADR-023 (manual apt-get dep set) + ADR-024 (debian:bookworm-slim base):
# aspnet:10.0 already includes the .NET 10 runtime, the ASP.NET Core shared framework
# (Kestrel), and all native deps (ICU, OpenSSL, ca-certificates).
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

WORKDIR /app

# curl is needed for the HEALTHCHECK probe below (not in the base image).
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Copy published app (McpWebSearchService.dll + deps + appsettings.json) from build stage.
COPY --from=build /app/publish .

# MCP streamable-HTTP port (McpServerSettings.Port default; override via McpServer__Port env var).
EXPOSE 8080

# Lightweight GET probe on the dedicated /healthz endpoint (the MCP /mcp endpoint is POST-only —
# a GET there would return 405 and false-positive the health check).
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD curl -f http://localhost:8080/healthz || exit 1

# Non-root user (security best practice — aspnet:10.0 base image provides $APP_UID=1654)
USER $APP_UID

# ADR-004: assembly name McpWebSearchService is fixed. JIT entrypoint (NOT a native binary).
ENTRYPOINT ["dotnet", "McpWebSearchService.dll"]
