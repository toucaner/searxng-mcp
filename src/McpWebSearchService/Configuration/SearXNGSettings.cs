#region MODULE_CONTRACT [DOMAIN(Config): SearXNG; CONCEPT(Settings): Backend connection; TECH(Options): IOptions<T>]
/**
 * [GREP_SUMMARY]: SearXNGSettings, options, configuration, searxng, BaseUrl, MaxResults, BlockedDomains, DefaultLanguage, AOT-safe, EnableConfigurationBindingGenerator
 * [STRUCTURE]: > Section["SearXNG"] -> o Bind -> + SearXNGSettings { BaseUrl, MaxResults, BlockedDomains[], DefaultLanguage } -> = IOptions<SearXNGSettings>.Value
 *
 * <summary>
 * [PURPOSE]: Strongly-typed options for the SearXNG backend instance (SPEC §5.2). Binds the "SearXNG"
 * configuration section to a sealed class with public setters, enabling AOT-safe configuration binding
 * via the EnableConfigurationBindingGenerator source generator (ADR-006) — no reflection.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Class is sealed (helps trim analyzer — no derived types). All properties have public
 * setters and non-null defaults (string.Empty / 0 / []) so validation can detect unpopulated config
 * without NullReferenceException. Nullable enable context is satisfied by non-null defaults.
 * [RATIONALE]: Q: Why sealed class with { get; set; } instead of a positional record? A: The
 * configuration-binding source generator emits a strongly-typed binder for public settable properties
 * on sealed classes; positional/init-only records complicate the binder and can provoke generator
 * warnings. Verified by the architect AOT probe on net10.0 (0 × IL####).
 * [CHANGES]: LAST_CHANGE: M2 — initial creation (DevelopmentPlan.md §2 step 1).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Configuration;

#region CLASS_SearXNGSettings [DOMAIN(Config): SearXNG; CONCEPT(Settings): Backend connection]
/// <summary>
/// [PURPOSE]: Holds connection and behaviour options for the SearXNG backend consumed by the
/// HTTP client (M3) and the post-processing pipeline (M4). Bound from the "SearXNG" config section.
/// </summary>
/// <remarks>
/// [INVARIANTS]: Sealed class; public setters; non-null defaults. Bound via
/// <c>AddOptions&lt;SearXNGSettings&gt;().Bind(section)</c> in <c>Program.cs</c>.
/// [RATIONALE]: AOT-safe — the EnableConfigurationBindingGenerator source generator intercepts
/// <c>Bind&lt;T&gt;()</c> and emits a compile-time binder (ADR-006), avoiding reflection-based
/// binding that would emit IL2026/IL3050 under Native AOT trimming.
/// </remarks>
public sealed class SearXNGSettings
{
    /// <summary>
    /// [PURPOSE]: Base URL of the SearXNG instance (e.g. <c>http://searxng-instance:8080</c>).
    /// Must be a non-empty absolute URI — validated on host startup.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// [PURPOSE]: Maximum number of results returned to the LLM after the post-processing pipeline
    /// applies <c>Take(N)</c>. Must be &gt; 0 — validated on host startup.
    /// </summary>
    public int MaxResults { get; set; } = 0;

    /// <summary>
    /// [PURPOSE]: Domain blocklist used by the post-processing filter (M4). Domains in this array
    /// are excluded from search results. Default is empty (no blocking).
    /// </summary>
    public string[] BlockedDomains { get; set; } = [];

    /// <summary>
    /// [PURPOSE]: Default language token passed to the SearXNG HTTP API (e.g. <c>"ru"</c>, <c>"en"</c>).
    /// Must be non-empty — validated on host startup.
    /// </summary>
    public string DefaultLanguage { get; set; } = string.Empty;
}
#endregion CLASS_SearXNGSettings
