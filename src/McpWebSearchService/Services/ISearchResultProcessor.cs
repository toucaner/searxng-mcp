#region MODULE_CONTRACT [DOMAIN(Pipeline): SearchResultProcessor; CONCEPT(Interface): Pipeline contract; TECH(M5): Interface]
/**
 * [GREP_SUMMARY]: ISearchResultProcessor, pipeline, post-processing, search result processor, process, SearXNGResult, SearchResultDto
 * [STRUCTURE]: > IReadOnlyList<SearXNGResult> -> o Process() = SearchResultDto[] -> o McpJsonContext -> + JSON to LLM
 *
 * <summary>
 * [PURPOSE]: Contract for the post-processing pipeline that transforms raw SearXNG search results into
 * clean, deduplicated SearchResultDto objects ready for MCP tool response. One method: Process.
 * Consumed by M6 WebSearchTools (not wired in DI until M5).
 * </summary>
 * <remarks>
 * [INVARIANTS]: Single method Process() only — no stateful methods outside of it. Input is IReadOnlyList
 * to allow callers to pass arrays/lists without boxing. Output is SearchResultDto[] (array, not IEnumerable)
 * for efficient serialization via McpJsonContext.Default.SearchResultDtoArray.
 * [RATIONALE]: Q: Why separate interface from implementation? A: Allows mocking in tests and enables future
 * pipeline variants (e.g., cached results, different dedup strategies). Interface is the only public-facing
 * contract — SearchResultProcessor implementation details are internal to this assembly.
 * [CHANGES]: LAST_CHANGE: M5 — initial creation (DevelopmentPlan.md §2 step 1).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Services;

using System.Collections.Generic;
using McpWebSearchService.Models;

#region INTERFACE_ISearchResultProcessor [DOMAIN(Pipeline): SearchResultProcessor; CONCEPT(Interface): Pipeline contract]
/// <summary>
/// [PURPOSE]: Post-processing pipeline contract. Transforms raw SearXNG results into clean, deduplicated DTOs.
/// </summary>
public interface ISearchResultProcessor
{
    /// <summary>
    /// [PURPOSE]: Applies the full post-processing pipeline to transform raw SearXNG search results into
    /// SearchResultDto objects ready for MCP tool response. Pipeline order: NormalizeUrl → Deduplicate
    /// (exact by normalized URL + fuzzy by Title across domains) → MapToDto → StripHtml → TruncateSnippet
    /// → Filter (blocklist + snippet length + Take(MaxResults)).
    /// </summary>
    /// <param name="rawResults">Raw results from SearXNGClient.SearchAsync. Must not be null.</param>
    /// <returns>Deduplicated, filtered SearchResultDto[] sorted by engine priority and relevance. Never null — empty array if all results are filtered.</returns>
    SearchResultDto[] Process(IReadOnlyList<SearXNGResult> rawResults);
}
#endregion INTERFACE_ISearchResultProcessor
