#region MODULE_CONTRACT [DOMAIN(DTO): MCP; CONCEPT(Request): Web search input; TECH(AOT): JsonSerializerContext]
/**
 * [GREP_SUMMARY]: SearchRequest, record, input, web_search, Query, Categories, TimeRange, Language, PascalCase
 * [STRUCTURE]: > LLM -> o MCP web_search tool -> + SearchRequest -> o SearXNGClient(M4) -> = SearXNG query params
 *
 * <summary>
 * [PURPOSE]: Input DTO for the web_search tool (SPEC §2.1). Describes the search query,
 * categories, optional time range, and language. PascalCase properties used for MCP
 * serialisation. Consumed by SearXNGClient in M4.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Query, Categories, Language are non-null (default string.Empty). TimeRange
 * is nullable — omitted from JSON when null via WhenWritingNull.
 * [RATIONALE]: Q: Why separate SearchRequest from SearXNG query params? A: MCP-facing DTO
 * is PascalCase, while SearXNG query parameters are snake_case. M4 mapping layer
 * (SearXNGClient) converts between them — no leak into MCP contract.
 * [CHANGES]: LAST_CHANGE: M3 — initial creation (DevelopmentPlan.md §2 step 2).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Models;

#region CLASS_SearchRequest [DOMAIN(DTO): MCP; CONCEPT(Request): Web search input]
/// <summary>
/// [PURPOSE]: MCP tool web_search input parameters. Serialised as PascalCase JSON.
/// </summary>
public record SearchRequest
{
    /// <summary>
    /// Search query string (required per SPEC §2.1).
    /// </summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>
    /// SearXNG search categories (e.g. "general", "news", "it"). Default "general".
    /// </summary>
    public string Categories { get; init; } = string.Empty;

    /// <summary>
    /// Optional time range filter ("day", "week", "month", "year"). Null omits from
    /// JSON via <see cref="JsonIgnoreCondition.WhenWritingNull"/>.
    /// </summary>
    public string? TimeRange { get; init; }

    /// <summary>
    /// Result language token (e.g. "ru", "en"). Default from SearXNGSettings.DefaultLanguage (M2).
    /// </summary>
    public string Language { get; init; } = string.Empty;
}
#endregion CLASS_SearchRequest
