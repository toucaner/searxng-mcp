#region MODULE_CONTRACT [DOMAIN(DTO): MCP; CONCEPT(Response): Search result; TECH(AOT): JsonSerializerContext]
/**
 * [GREP_SUMMARY]: SearchResultDto, record, MCP response, Title, Url, Snippet, SourceEngine, PascalCase
 * [STRUCTURE]: > SearXNG JSON -> o M4/M5 Pipeline -> o MCP Tool -> = SearchResultDto -> o McpJsonContext -> + PascalCase JSON -> [] LLM
 *
 * <summary>
 * [PURPOSE]: MCP-facing search result DTO returned to the LLM from the web_search tool.
 * Carries Title, Url, Snippet, and optional SourceEngine. PascalCase properties (no
 * [JsonPropertyName]) — serialised via McpJsonContext for AOT safety.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Title, Url, Snippet are non-null (default string.Empty). SourceEngine is
 * nullable — omitted from JSON output when null via WhenWritingNull (context-level option).
 * Value-equality via record semantics (used in round-trip tests).
 * [RATIONALE]: Q: Why record with init-setters instead of positional record?
 * A: Explicit property body allows default values and avoids positional constructor
 * conflicts with JSON deserialization. record provides value-equality for testing.
 * [CHANGES]: LAST_CHANGE: M3 — initial creation (DevelopmentPlan.md §2 step 1).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Models;

#region CLASS_SearchResultDto [DOMAIN(DTO): MCP; CONCEPT(Response): Search result]
/// <summary>
/// [PURPOSE]: MCP tool web_search return type. Serialised as PascalCase JSON to the LLM.
/// </summary>
public record SearchResultDto
{
    /// <summary>
    /// Result title (e.g. ".NET 10 Documentation").
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Result URL (e.g. "https://learn.microsoft.com/dotnet").
    /// </summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Snippet / excerpt text (plain text, truncated to ~200–300 chars by M5 pipeline).
    /// </summary>
    public string Snippet { get; set; } = string.Empty;

    /// <summary>
    /// Source search engine identifier (e.g. "google", "bing"). Null if unknown or
    /// aggregated — omitted from JSON via <see cref="JsonIgnoreCondition.WhenWritingNull"/>.
    /// </summary>
    public string? SourceEngine { get; init; }
}
#endregion CLASS_SearchResultDto
