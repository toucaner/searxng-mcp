#region MODULE_CONTRACT [DOMAIN(DTO): SearXNG; CONCEPT(Response): Envelope; TECH(Serialization): JsonPropertyName]
/**
 * [GREP_SUMMARY]: SearXNGResponse, record, searxng, response, results, envelope, JsonPropertyName
 * [STRUCTURE]: > SearXNG HTTP JSON -> o [JsonPropertyName("results")] -> + SearXNGResponse -> o M4 SearXNGClient -> [] SearXNGResult -> o M5 Pipeline
 *
 * <summary>
 * [PURPOSE]: Response envelope from the SearXNG API (format=json). Contains a
 * "results" array of <see cref="SearXNGResult"/> items. [JsonPropertyName("results")]
 * maps the SearXNG snake_case field to the PascalCase Results property.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Results is non-null — defaults to empty array ([]). Deserialised by
 * SearXNGClient in M4 via McpJsonContext.Default.SearXNGResponse.
 * [RATIONALE]: Q: Why not use JsonSerializerOptions.PropertyNameCaseInsensitive alone?
 * A: Both approaches are used. CaseInsensitive handles casing variance (Title/title),
 * while [JsonPropertyName("results")] provides exact mapping for the SearXNG envelope
 * field name — belt-and-suspenders for the critical wire-format boundary.
 * [CHANGES]: LAST_CHANGE: M3 — initial creation (DevelopmentPlan.md §2 step 4).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Text.Json.Serialization;

namespace McpWebSearchService.Models;

#region CLASS_SearXNGResponse [DOMAIN(DTO): SearXNG; CONCEPT(Response): Envelope]
/// <summary>
/// [PURPOSE]: SearXNG API JSON response envelope (format=json). Contains results array.
/// </summary>
public record SearXNGResponse
{
    /// <summary>
    /// Array of search result items. Maps from SearXNG "results" field.
    /// Defaults to empty array (non-null).
    /// </summary>
    [JsonPropertyName("results")]
    public SearXNGResult[] Results { get; init; } = [];
}
#endregion CLASS_SearXNGResponse
