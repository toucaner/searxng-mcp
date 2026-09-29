#region MODULE_CONTRACT [DOMAIN(Pipeline): TitleSimilarity; CONCEPT(Algorithm): Jaccard similarity on whitespace tokens; TECH(AOT): Pure arithmetic]
/**
 * [GREP_SUMMARY]: TitleSimilarity, Jaccard, similarity, fuzzy dedup, title comparison, token set, AOT-safe
 * [STRUCTURE]: > two strings -> o ToLowerInvariant + Split(whitespace) -> o HashSet<string> x2 = intersection U union -> = |A∩B| / |A∪B| -> [0.0..1.0]
 *
 * <summary>
 * [PURPOSE]: Jaccard similarity computation on whitespace-tokenized strings, used for fuzzy title deduplication
 * in the search result pipeline (ADR-009). Returns 0.0–1.0 where 1.0 = identical token sets, 0.0 = no overlap.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Pure function — no side effects, no mutable state. Same inputs always produce same output.
 * Empty string pair → 1.0 (both empty = identical). One-empty-pair → 0.0. Both-empty → 0.0 is wrong; both empty returns 1.0.
 * [RATIONALE]: Q: Why Jaccard over Levenshtein? A: O(n+m) on tokens vs O(n·m) on characters. Titles are
 * typically 30–120 chars with ~5–20 words, so token sets are small but much smaller than char matrices.
 * Plus whitespace-tokenized Jaccard naturally handles word reordering (mirrored sites). AOT-safe because it only
 * uses HashSet<string>, string operations, and integer arithmetic — zero reflection, zero IL emission.
 * [CHANGES]: LAST_CHANGE: M5 — initial creation (DevelopmentPlan.md §2 step 2).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Services;

#region CLASS_TitleSimilarity [DOMAIN(Pipeline): TitleSimilarity; CONCEPT(Algorithm): Jaccard]
/// <summary>
/// [PURPOSE]: Calculates Jaccard similarity between two strings using whitespace-tokenized sets.
/// Used by SearchResultProcessor for fuzzy title deduplication (ADR-009).
/// </summary>
/// <remarks>
/// [INVARIANTS]: static class — no instances ever created, no state to manage. Thread-safe because all methods
/// are pure functions operating only on parameters and locals.
/// [RATIONALE]: Q: Why separate from SearchResultProcessor? A: Isolated testability — TitleSimilarityTests.cs
/// can test the pure function without pipeline dependencies. internal static so it's not part of public API
/// but accessible to tests via InternalsVisibleTo (configured in M4 csproj).
/// [CHANGES]: LAST_CHANGE: M5 — initial creation (DevelopmentPlan.md §2 step 2).
/// </remarks>
internal static class TitleSimilarity
{
    /// <summary>
    /// Similarity threshold for fuzzy deduplication. Titles with Jaccard similarity ≥ this value are considered duplicates.
    /// Set to 0.90 (9 out of 10 words must match) — high enough to avoid false positives from different articles,
    /// low enough to catch mirrored sites and near-duplicates.
    /// </summary>
    internal const double TitleSimilarityThreshold = 0.90;

    /// <summary>
    /// Maximum title length before tokenization for fuzzy matching. Long pathological titles are capped to prevent
    /// excessive memory allocation in the HashSet. Default: 500 characters.
    /// </summary>
    internal const int MaxTitleLengthForFuzzy = 500;

    #region METHOD_Calculate [DOMAIN(Pipeline): TitleSimilarity; TECH(Algorithm): Jaccard on token sets]
    /// <summary>
    /// [PURPOSE]: Computes the Jaccard similarity coefficient between two strings using whitespace-tokenized sets.
    /// Returns a value in [0.0, 1.0] where 1.0 means identical token sets (case-insensitive) and 0.0 means no overlap.
    /// </summary>
    /// <param name="a">First string to compare.</param>
    /// <param name="b">Second string to compare.</param>
    /// <returns>Jaccard similarity coefficient: |A ∩ B| / |A ∪ B|, in range [0.0, 1.0]. Both empty → 1.0; one empty → 0.0.</returns>
    public static double Calculate(string a, string b)
    {
        // _logger.LogDebug("[IMP:4][TitleSimilarity.Calculate][INIT] Comparing '{A}' vs '{B}'", a, b);

        // Both strings are effectively identical (empty).
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
            return 1.0;

        // One is empty while the other isn't — no overlap possible.
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0.0;

        // Tokenize: lowercase, cap length, split on whitespace → hash set for O(1) lookups.
        var tokensA = GetTokens(a);
        var tokensB = GetTokens(b);

        int intersectionCount = 0;
        foreach (var token in tokensA)
            if (tokensB.Contains(token))
                intersectionCount++;

        // Union size = |A| + |B| - |intersection|. Avoids creating a third HashSet.
        var unionSize = tokensA.Count + tokensB.Count - intersectionCount;

        return unionSize == 0 ? 0.0 : (double)intersectionCount / unionSize;
    }
    #endregion METHOD_Calculate

    #region METHOD_AreSimilar [DOMAIN(Pipeline): TitleSimilarity; TECH(Algorithm): Threshold gate on Jaccard]
    /// <summary>
    /// [PURPOSE]: Convenience wrapper that returns true if two titles are similar enough to be considered duplicates.
    /// </summary>
    /// <param name="a">First title.</param>
    /// <param name="b">Second title.</param>
    /// <param name="threshold">Similarity threshold (default: 0.90). Titles with Jaccard ≥ threshold are considered similar.</param>
    /// <returns>true if Calculate(a, b) ≥ threshold; false otherwise.</returns>
    public static bool AreSimilar(string a, string b, double threshold = TitleSimilarityThreshold)
        => Calculate(a, b) >= threshold;
    #endregion METHOD_AreSimilar

    #region METHOD_GetTokens [DOMAIN(Pipeline): TitleSimilarity; TECH(Algorithm): Tokenization helper]
    /// <summary>
    /// [PURPOSE]: Tokenizes a string by lowercasing it (culture-invariant), capping to MaxTitleLengthForFuzzy, and splitting on whitespace characters.
    /// Returns a HashSet for O(1) lookup during Jaccard intersection computation.
    /// </summary>
    private static HashSet<string> GetTokens(string input)
    {
        // Cap length — pathological titles (>500 chars) would allocate large hash sets.
        var capped = input.Length > MaxTitleLengthForFuzzy ? input.AsSpan(0, MaxTitleLengthForFuzzy).ToString() : input;

        // Culture-invariant lowercase (ADR-003: InvariantGlobalization=true).
        var lower = capped.ToLowerInvariant();

        var set = new HashSet<string>(lower.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        return set;
    }
    #endregion METHOD_GetTokens
}
#endregion CLASS_TitleSimilarity
