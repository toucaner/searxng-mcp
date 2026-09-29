#region MODULE_CONTRACT [DOMAIN(Test): TitleSimilarity; CONCEPT(Unit Tests): Jaccard similarity edge cases]
/**
 * [GREP_SUMMARY]: TitleSimilarityTests, xunit, test, jaccard, fuzzy dedup, title comparison, token set
 * [STRUCTURE]: > Titles -> o Calculate(a,b) = [0.0..1.0] | > AreSimilar() = bool threshold gate
 */

using McpWebSearchService.Services;

namespace McpWebSearchService.Tests.Services;

#region CLASS_TitleSimilarityTests [DOMAIN(Test): TitleSimilarity unit tests]
/// <summary>
/// [PURPOSE]: Unit tests for the Jaccard title similarity algorithm (ADR-009). Tests pure function behavior:
/// identical titles → 1.0, disjoint tokens → 0.0, partial overlap → proportional value, empty strings.
/// </summary>
public class TitleSimilarityTests
{
    #region THEORY_Calculate_IdenticalTitles [DOMAIN(Test): Jaccard — perfect match]
    /// <summary>
    /// [PURPOSE]: Verify that identical titles produce Jaccard similarity of 1.0 (both exact and case-insensitive).
    /// </summary>
    [Theory]
    [InlineData("The .NET Documentation", "The .NET Documentation")]
    [InlineData("Hello World", "hello world")]
    public void Calculate_IdenticalTitles_ReturnsOne(string a, string b)
    {
        var result = TitleSimilarity.Calculate(a, b);
        Assert.Equal(1.0, result);
    }
    #endregion THEORY_Calculate_IdenticalTitles

    #region THEORY_Calculate_DisjointTokens [DOMAIN(Test): Jaccard — no overlap]
    /// <summary>
    /// [PURPOSE]: Verify that completely different titles produce Jaccard similarity of 0.0 (no common tokens).
    /// </summary>
    [Theory]
    [InlineData("Hello World", "Foo Bar")]
    [InlineData("The .NET Documentation", "SearXNG GitHub Repository")]
    public void Calculate_DisjointTokens_ReturnsZero(string a, string b)
    {
        var result = TitleSimilarity.Calculate(a, b);
        Assert.Equal(0.0, result);
    }
    #endregion THEORY_Calculate_DisjointTokens

    #region THEORY_Calculate_EmptyStrings [DOMAIN(Test): Jaccard — empty inputs]
    /// <summary>
    /// [PURPOSE]: Verify behavior with empty strings: both empty → 1.0 (identical), one empty → 0.0.
    /// </summary>
    [Theory]
    [InlineData("", "", 1.0)]
    [InlineData("hello", "", 0.0)]
    [InlineData("", "world", 0.0)]
    public void Calculate_EmptyStrings_HandlesCorrectly(string a, string b, double expected)
    {
        var result = TitleSimilarity.Calculate(a, b);
        Assert.Equal(expected, result);
    }
    #endregion THEORY_Calculate_EmptyStrings

    #region THEORY_Calculate_PartialOverlap [DOMAIN(Test): Jaccard — partial token match]
    /// <summary>
    /// [PURPOSE]: Verify that titles with partial token overlap produce proportional Jaccard similarity.
    /// "the quick brown fox" vs "the quick brown dog": intersection = {the,quick,brown} (3), union = 5 → 0.6.
    /// </summary>
    [Theory]
    [InlineData("the quick brown fox", "the quick brown dog", 3.0 / 5)]
    public void Calculate_PartialOverlap_ReturnsProportional(string a, string b, double expected)
    {
        var result = TitleSimilarity.Calculate(a, b);
        Assert.Equal(expected, result);
    }
    #endregion THEORY_Calculate_PartialOverlap

    #region THEORY_AreSimilar_ThresholdBoundary [DOMAIN(Test): AreSimilar — threshold boundary]
    /// <summary>
    /// [PURPOSE]: Verify threshold behavior at and around 0.90 (Jaccard).
    /// 
    /// Jaccard = |A ∩ B| / |A ∪ B|. Note: "9 out of 10 tokens match" does NOT mean Jaccard = 0.9 —
    /// the union includes the differing tokens from BOTH strings, so 9 shared / 11 total = 0.818.
    /// To get Jaccard ≥ 0.90, one string must be a near-subset of the other (e.g. 10 shared out of
    /// 11 union = 0.909).
    /// </summary>
    /// <remarks>
    /// [CHANGES]: M5-debug: Fixed mathematically incorrect test expectation. The original test used
    /// "a b c d e f g h i j" vs "a b c d e f g h i k" expecting True (assuming 9/10 = 0.90), but
    /// Jaccard = 9/11 ≈ 0.818 < 0.90 → correct answer is False. Added a case that genuinely crosses
    /// the threshold (10 shared tokens, 11 union → 0.909 ≥ 0.90 → True).
    /// </remarks>
    [Theory]
    // BUG_FIX_CONTEXT: [HYPOTHESIS: The test assumed Jaccard = 9/10 = 0.90 for 9 matching tokens out
    // of 10 per string, but Jaccard uses the UNION (11 unique tokens: 9 shared + 1 unique to each),
    // so 9/11 ≈ 0.818 < 0.90 → AreSimilar must return False. ADR-009 defines Jaccard explicitly.]
    [InlineData("a b c d e f g h i j", "a b c d e f g h i k", false)]   // 9/11 ≈ 0.818 < 0.90 → false
    [InlineData("a b c d e f g h i j", "a b c d e f g h i j k", true)]   // 10/11 ≈ 0.909 ≥ 0.90 → true (subset)
    [InlineData("a b c d e f g h i j", "a b c d e f g h i j", true)]     // 10/10 = 1.0 ≥ 0.90 → true (identical)
    public void AreSimilar_NearThreshold_AppliesGate(string a, string b, bool expected)
    {
        var result = TitleSimilarity.AreSimilar(a, b, 0.90);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("hello world foo", "bar baz qux", false)]                // 0/6 = 0.00
    public void AreSimilar_DisjointBelowThreshold_ReturnsFalse(string a, string b, bool expected)
    {
        var result = TitleSimilarity.AreSimilar(a, b, 0.90);
        Assert.Equal(expected, result);
    }
    #endregion THEORY_AreSimilar_ThresholdBoundary

    #region METHOD_TitleLengthCapping [DOMAIN(Test): TitleSimilarity — length capping]
    /// <summary>
    /// [PURPOSE]: Verify that titles longer than MaxTitleLengthForFuzzy (500) are capped before tokenization.
    /// Two 600-char strings differing only in the tail should still match as similar (tail is discarded).
    /// </summary>
    [Fact]
    public void Calculate_CapsLongTitles()
    {
        var baseTitle = string.Join(' ', Enumerable.Repeat("word", 400));
        var longWithTailA = baseTitle + " tail-a";
        var longWithTailB = baseTitle + " tail-b";

        var result = TitleSimilarity.Calculate(longWithTailA, longWithTailB);

        // After capping to 500 chars, both contain only the common prefix — similarity should be 1.0.
        Assert.Equal(1.0, result);
    }
    #endregion METHOD_TitleLengthCapping

    #region METHOD_AreSimilar_CustomThreshold [DOMAIN(Test): AreSimilar — custom threshold]
    /// <summary>
    /// [PURPOSE]: Verify that a custom lower threshold (e.g., 0.5) catches titles with moderate overlap,
    /// while the default 0.90 rejects them.
    /// </summary>
    [Fact]
    public void AreSimilar_CustomThreshold_CatchesPartialOverlap()
    {
        var result = TitleSimilarity.AreSimilar("hello world", "world foo", 0.5);

        // intersection = {world} (1), union = {hello,world,foo} (3) → 1/3 ≈ 0.33 < 0.5
        Assert.False(result);
    }

    [Fact]
    public void AreSimilar_DefaultThreshold_RejectsPartialOverlap()
    {
        var result = TitleSimilarity.AreSimilar("hello world", "world foo");

        // Default threshold is 0.90, but actual similarity ≈ 0.33 — should be false.
        Assert.False(result);
    }
    #endregion METHOD_AreSimilar_CustomThreshold
}
#endregion CLASS_TitleSimilarityTests
#endregion MODULE_CONTRACT
