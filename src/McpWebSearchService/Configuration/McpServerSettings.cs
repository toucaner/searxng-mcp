#region MODULE_CONTRACT [DOMAIN(Config): McpServer; CONCEPT(Settings): Server identity + HTTP endpoint; TECH(Options): IOptions<T>]
/**
 * [GREP_SUMMARY]: McpServerSettings, options, configuration, mcpserver, Name, Version, Port, Path, AOT-safe, EnableConfigurationBindingGenerator
 * [STRUCTURE]: > Section["McpServer"] -> o Bind -> + McpServerSettings { Name, Version, Port, Path } -> = IOptions<McpServerSettings>.Value
 *
 * <summary>
 * [PURPOSE]: Strongly-typed options identifying the MCP server (SPEC §5.2) and, since M10 (ADR-033),
 * the HTTP endpoint it listens on (Port + Path). Binds the "McpServer" configuration section to a sealed
 * class with public setters, enabling reflection-free configuration binding via the
 * EnableConfigurationBindingGenerator source generator (ADR-006).
 * </summary>
 * <remarks>
 * [INVARIANTS]: Class is sealed (helps trim analyzer — no derived types). All properties have public
 * setters and non-null defaults (string.Empty) so validation can detect unpopulated config without
 * NullReferenceException. Nullable enable context is satisfied by non-null defaults.
 * [RATIONALE]: Q: Why sealed class with { get; set; } instead of a positional record? A: The
 * configuration-binding source generator emits a strongly-typed binder for public settable properties
 * on sealed classes; positional/init-only records complicate the binder and can provoke generator
 * warnings. M10 (ADR-033) adds Port/Path for Kestrel — the HTTP transport now hosts the server.
 * [CHANGES]: LAST_CHANGE: M10 — added Port + Path (ADR-033 HTTP transport); removed AOT framing from purpose.
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Configuration;

#region CLASS_McpServerSettings [DOMAIN(Config): McpServer; CONCEPT(Settings): Server identity + HTTP endpoint]
/// <summary>
/// [PURPOSE]: Holds the MCP server name/version identifiers (handshake) plus the HTTP endpoint
/// (Port + Path) that the streamable HTTP transport listens on since M10 (ADR-033). Bound from the
/// "McpServer" config section.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Sealed class; public setters; non-null defaults. Bound via
/// <c>AddOptions&lt;McpServerSettings&gt;().Bind(section)</c> in <c>Program.cs</c>.
/// [RATIONALE]: Keep the config-binder source generator active (ADR-006 device) — reflection-free
/// binding regardless of AOT/JIT mode.
/// </remarks>
public sealed class McpServerSettings
{
    /// <summary>
    /// [PURPOSE]: MCP server name identifier (e.g. <c>"web-search-mcp"</c>).
    /// Must be non-empty — validated on host startup.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// [PURPOSE]: MCP server version string (e.g. <c>"1.0.0"</c>).
    /// Must be non-empty — validated on host startup.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// [PURPOSE]: TCP port the Kestrel host binds (M10, ADR-033). Range-validated (0, 65535].
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// [PURPOSE]: URL path the MCP streamable-HTTP endpoint is mapped at (M10, ADR-033).
    /// Default <c>"/mcp"</c> — clients connect to <c>http://host:&lt;Port&gt;/mcp</c>.
    /// </summary>
    public string Path { get; set; } = "/mcp";
}
#endregion CLASS_McpServerSettings
