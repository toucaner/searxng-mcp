#region MODULE_CONTRACT [DOMAIN(HTTP): CategoryInference Client; CONCEPT(Interface): Local LLM client contract; TECH(M9): Interface]
/**
 * [GREP_SUMMARY]: ICategoryInferenceClient, interface, contract, InferCategoryAsync, query, CancellationToken, nullable result
 * [STRUCTURE]: > SearchCategoryClassifier -> + query -> = ICategoryInferenceClient.InferCategoryAsync() -> o LocalLlmCategoryInference -> = string? category (null = unavailable/invalid)
 *
 * <summary>
 * [PURPOSE]: Contract for a client that infers a SearXNG search category from the semantic content of a
 * query using a local LLM (M9). Single method: InferCategoryAsync — returns a category, or null to signal
 * the LLM is unavailable or returned an invalid result (caller falls back to general).
 * </summary>
 * <remarks>
 * [INVARIANTS]: Return is nullable string. A null return means category inference is NOT reliable — the
 * caller MUST fall back (never throws, never blocks beyond the client timeout). Implementations must never
 * throw for network/timeout/invalid-response conditions; they return null instead.
 * [RATIONALE]: Q: Why a nullable string rather than a result object or exception? A: The milestone contract
 * is "null = unavailable/invalid (fallback)". An exception-based contract would force the classifier to
 * try/catch on every inference; null is the clean "no reliable answer" signal. Q: Why interface instead of
 * a sealed class? A: Enables DI registration (AddHttpClient&lt;ICategoryInferenceClient, ...&gt;) and test
 * mocking with a stub — identical to ISearXNGClient's rationale.
 * [CHANGES]: LAST_CHANGE: M9 — initial creation (M9 CategoryInference milestone, ADR-032).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Services;

#region INTERFACE_ICategoryInferenceClient [DOMAIN(HTTP): CategoryInference Client; CONCEPT(Interface): Local LLM client contract]
/// <summary>
/// [PURPOSE]: Contract for the local-LLM category-inference client (M9).
/// </summary>
public interface ICategoryInferenceClient
{
    /// <summary>
    /// [PURPOSE]: Infers a SearXNG search category for the query via the local LLM.
    /// </summary>
    /// <param name="query">The search query text whose category is to be inferred.</param>
    /// <param name="ct">Cancellation token; combined with the configured per-call timeout.</param>
    /// <returns>The inferred category (a member of CategoryInferenceSettings.Categories), or null when the
    /// LLM is unavailable or its response is invalid/not in the closed set. Never throws.</returns>
    Task<string?> InferCategoryAsync(string query, CancellationToken ct = default);
}
#endregion INTERFACE_ICategoryInferenceClient
