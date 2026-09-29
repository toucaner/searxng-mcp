#region MODULE_CONTRACT [DOMAIN(HTTP): SearXNG; CONCEPT(Client): Typed HTTP client; TECH(AOT): JsonSerializerContext]
/**
 * [GREP_SUMMARY]: ISearXNGClient, interface, contract, SearchAsync, SearXNGResult, CancellationToken, IReadOnlyList
 * [STRUCTURE]: > LLM -> o MCP tool -> + SearchRequest -> = ISearXNGClient.SearchAsync() -> o HttpClient -> = SearXNGResult[]
 *
 * <summary>
 * [PURPOSE]: Contract for a typed HTTP client that sends search requests to a SearXNG backend
 * instance. Single method: SearchAsync — builds URL, dispatches via HttpClient, returns parsed results.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Return is IReadOnlyList&lt;SearXNGResult&gt; (never null). Exceptions are wrapped in
 * SearXNGUnavailableException for LLM consumption (M6 maps to text response).
 * [RATIONALE]: Q: Why interface instead of sealed class? A: Enables testing with mock HttpClient via
 * DelegatingHandler — the entire search pipeline is unit-testable without a real SearXNG instance.
 * [CHANGES]: LAST_CHANGE: M4 — initial creation (DevelopmentPlan.md §2 step 1).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using McpWebSearchService.Models;

namespace McpWebSearchService.Services;

#region INTERFACE_ISearXNGClient [DOMAIN(HTTP): SearXNG; CONCEPT(Client): Typed HTTP client]
/// <summary>
/// [PURPOSE]: Contract for the typed SearXNG HTTP client (M4).
/// </summary>
public interface ISearXNGClient
{
    /// <summary>
    /// [PURPOSE]: Sends a search request to SearXNG and returns parsed results.
    /// Circuit Breaker gate: throws SearXNGUnavailableException when breaker is OPEN.
    /// Timeout (15s): handled by HttpClient.Timeout — caller receives TaskCanceledException.
    /// </summary>
    /// <param name="request">Search request with query, categories, language, time_range.</param>
    /// <param name="ct">Cancellation token passed to HttpClient.SendAsync.</param>
    /// <returns>Non-null list of search results. Empty if SearXNG returns no results.</returns>
    Task<IReadOnlyList<SearXNGResult>> SearchAsync(SearchRequest request, CancellationToken ct = default);
}
#endregion INTERFACE_ISearXNGClient
