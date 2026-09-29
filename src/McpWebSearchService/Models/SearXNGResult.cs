#region MODULE_CONTRACT [DOMAIN(DTO): SearXNG; CONCEPT(Response): Result item; TECH(Serialization): JsonPropertyName]
/**
 * [GREP_SUMMARY]: SearXNGResult, record, searxng, result, title, url, content, engine, snake_case, JsonPropertyName
 * [STRUCTURE]: > SearXNG HTTP JSON -> o [JsonPropertyName("title")] -> + SearXNGResult -> [] SearXNGResponse.Results -> o M4/M5 Pipeline
 *
 * <summary>
 * [PURPOSE]: Maps a single result item from the SearXNG API response (format=json).
 * SearXNG returns lowercase fields (title, url, content, engine). [JsonPropertyName]
 * per property maps these to PascalCase properties on this record.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Title, Url, Content are non-null (default string.Empty). Engine is
 * nullable — aggregated results may omit the engine field.
 * [RATIONALE]: Q: Why explicit [JsonPropertyName] instead of global SnakeCaseLower policy?
 * A: SnakeCaseLower policy would apply to ALL DTOs in McpJsonContext, including MCP-facing
 * SearchResultDto (which needs PascalCase). Explicit [JsonPropertyName] on SearXNG-facing
 * DTOs gives precise control (per plan §0.2).
 * [CHANGES]: LAST_CHANGE: M3 — initial creation (DevelopmentPlan.md §2 step 3).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Text.Json.Serialization;

namespace McpWebSearchService.Models;

#region CLASS_SearXNGResult [DOMAIN(DTO): SearXNG; CONCEPT(Response): Result item]
/// <summary>
/// [PURPOSE]: Single SearXNG search result. Mapped from snake_case JSON fields via
/// [JsonPropertyName] attributes.
/// </summary>
public record SearXNGResult
{
    /// <summary>
    /// Result title. Maps from SearXNG "title" field.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Result URL. Maps from SearXNG "url" field.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Result snippet / content text. Maps from SearXNG "content" field.
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Source search engine identifier (e.g. "google", "bing"). Nullable — aggregated
    /// results may omit the engine field. Maps from SearXNG "engine" field.
    /// </summary>
    [JsonPropertyName("engine")]
    public string? Engine { get; init; }
}
#endregion CLASS_SearXNGResult
