#region MODULE_CONTRACT [DOMAIN(Util): SearchCategoryClassifier; CONCEPT(Classifier): Category priority resolution; TECH(M9): internal static helper]
/**
 * [GREP_SUMMARY]: SearchCategoryClassifier, InferCategoryAsync, explicit priority, LLM, general fallback, internal static class
 * [STRUCTURE]: > query + explicitCategories -> if explicit non-empty -> it (PRIORITY) | else -> ICategoryInferenceClient.InferCategoryAsync -> non-empty? -> it | else EnableFallback -> "general" | else ""
 *
 * <summary>
 * [PURPOSE]: Pure decision helper (M9) that resolves the effective SearXNG category given an optional
 * explicit agent-supplied category and a category-inference client. Mirrors the proven
 * <c>internal static class</c> pattern of TitleSimilarity/HtmlTextExtractor — no DI, testable in isolation.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Returns non-null string on every path. Priority is strict: explicitCategories (non-blank)
 * wins unconditionally; otherwise the LLM's inferred category (non-null, already closed-set-validated by the
 * client) is used; otherwise enableFallback ? "general" : string.Empty. Async only because the LLM path is async.
 * [RATIONALE]: Q: Why internal static rather than a DI-registered service? A: The milestone specifies
 * <c>internal static class</c> (per Draft Code Graph), matching TitleSimilarity/HtmlTextExtractor. It carries
 * no state and only needs the injected client + settings as arguments, so WebSearchTools passes them in.
 * Q: Why does the explicit branch short-circuit without touching the LLM? A: Milestone: "an explicit categories ...
 * has unconditional priority" — no LLM latency is incurred when the agent already specified a category.
 * [CHANGES]: LAST_CHANGE: M9 — initial creation (M9 CategoryInference milestone, ADR-032).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Services;

#region CLASS_SearchCategoryClassifier [DOMAIN(Util): SearchCategoryClassifier; CONCEPT(Classifier): Priority resolution]
/// <summary>
/// [PURPOSE]: Resolves the effective search category: explicit → LLM-inferred → fallback. internal static.
/// </summary>
internal static class SearchCategoryClassifier
{
    #region CONSTS [DOMAIN(Util): Fallback]
    /// <summary>Default fallback category when the LLM is unavailable/invalid and fallback is enabled.</summary>
    public const string FallbackCategory = "general";
    #endregion CONSTS

    #region METHOD_InferCategoryAsync [DOMAIN(Util): Main decision logic]
    /// <summary>
    /// [PURPOSE]: Computes the effective category with strict priority: explicitCategories (if non-blank)
    /// → LLM-inferred → fallback (general when enabled, empty otherwise). Never throws.
    /// </summary>
    /// <param name="query">The search query string.</param>
    /// <param name="explicitCategories">Agent-provided category (may be null/whitespace → LLM path).</param>
    /// <param name="client">Category-inference client (may be null in pure-classifier tests for the explicit branch).</param>
    /// <param name="enableFallback">Whether to fall back to general when the LLM returns nothing usable.</param>
    /// <param name="ct">Cancellation token forwarded to the LLM client.</param>
    /// <returns>The effective category string (non-null; empty only when fallback is disabled and LLM fails).</returns>
    public static async Task<string> InferCategoryAsync(
        string query,
        string? explicitCategories,
        ICategoryInferenceClient? client,
        bool enableFallback,
        CancellationToken ct = default)
    {
        // Priority 1 — explicit agent category wins unconditionally (no LLM latency).
        if (!string.IsNullOrWhiteSpace(explicitCategories))
            return explicitCategories;

        // Priority 2 — LLM inference; the client already validated against the closed set (null = unusable).
        if (client != null)
        {
            var inferred = await client.InferCategoryAsync(query, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(inferred))
                return inferred;
        }

        // Priority 3 — deterministic fallback so search never hangs.
        return enableFallback ? FallbackCategory : string.Empty;
    }
    #endregion METHOD_InferCategoryAsync
}
#endregion CLASS_SearchCategoryClassifier
