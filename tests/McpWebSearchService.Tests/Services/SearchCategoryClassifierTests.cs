#region MODULE_CONTRACT [DOMAIN(Test): SearchCategoryClassifier; CONCEPT(Unit Tests): Category priority resolution (M9)]
/**
 * [GREP_SUMMARY]: SearchCategoryClassifierTests, xunit, explicit priority, LLM inference, general fallback, internal static classifier
 * [STRUCTURE]: > InferCategoryAsync(query, explicit, client, enableFallback) -> explicit non-empty -> IT (no LLM) | LLM -> IT | LLM null -> general | fallback disabled -> empty
 *
 * <summary>
 * [PURPOSE]: Unit tests for the <c>SearchCategoryClassifier</c> internal static decision helper (M9).
 * Verifies the strict priority contract: explicit agent category wins unconditionally; otherwise the
 * LLM-inferred category (already closed-set validated by the client) is used; otherwise the deterministic
 * general fallback (or empty when fallback is disabled). A stub ICategoryInferenceClient isolates the
 * classifier from the real LLM HTTP client.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Each test is self-contained — a fresh stub client per test. The explicit branch is verified
 * to short-circuit WITHOUT invoking the client (records call count). Null/invalid LLM results fall back to
 * GeneralCategory and never propagate as an exception.
 * [RATIONALE]: Q: Why a stub client rather than the real LocalLlmCategoryInference? A: Classifier tests target
 * the priority/fallback logic in isolation (per Draft Code Graph: "SearchCategoryClassifierTests"); the real
 * client is exercised in LocalLlmCategoryInferenceTests via a mock HTTP handler. Q: Why assert the stub was
 * NOT called on the explicit path? A: Milestone: "an explicit categories ... has unconditional priority" — an
 * explicit category must incur zero LLM latency.
 * [CHANGES]: LAST_CHANGE: M9 — initial creation (M9 CategoryInference milestone, ADR-032).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using McpWebSearchService.Services;

namespace McpWebSearchService.Tests.Services;

#region CLASS_SearchCategoryClassifierTests [DOMAIN(Test): SearchCategoryClassifier unit tests]
/// <summary>
/// [PURPOSE]: Unit tests for SearchCategoryClassifier category priority resolution (M9).
/// </summary>
public class SearchCategoryClassifierTests
{
    #region STUB_CLASS_StubCategoryClient [DOMAIN(Test): Configurable ICategoryInferenceClient stub]
    /// <summary>
    /// [PURPOSE]: A stub ICategoryInferenceClient that returns a configured result (or null) and records
    /// how many times it was invoked. Used to verify the classifier's priority and short-circuit behaviour.
    /// </summary>
    private sealed class StubCategoryClient : ICategoryInferenceClient
    {
        /// <summary>Result returned by InferCategoryAsync (null = LLM unavailable/invalid).</summary>
        public string? Result { get; set; }

        /// <summary>Number of times InferCategoryAsync was called.</summary>
        public int CallCount { get; private set; }

        public Task<string?> InferCategoryAsync(string query, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }
    #endregion STUB_CLASS_StubCategoryClient

    #region TEST_METHOD_ExplicitCategory_Wins_WithoutLlmCall [(a) — explicit category has unconditional priority]
    /// <summary>
    /// [PURPOSE]: Verifies AC (a): an explicit agent category is returned verbatim, even if the LLM would
    /// have returned something different, AND the LLM client is never invoked (zero latency on explicit path).
    /// </summary>
    [Fact]
    public async Task InferCategory_ExplicitCategory_Wins_WithoutLlmCall()
    {
        var stub = new StubCategoryClient { Result = "it" }; // would be chosen if misused.

        var result = await SearchCategoryClassifier.InferCategoryAsync(
            "how to write an xunit test", explicitCategories: "news", client: stub, enableFallback: true);

        Assert.Equal("news", result);
        Assert.Equal(0, stub.CallCount); // explicit short-circuits — LLM NOT called.
    }
    #endregion TEST_METHOD_ExplicitCategory_Wins_WithoutLlmCall

    #region TEST_METHOD_LlmInfers_It_UsesIt [(b) — LLM returns "it" → "it"]
    /// <summary>
    /// [PURPOSE]: Verifies AC (b): when no explicit category is given and the LLM infers "it", the classifier
    /// returns "it".
    /// </summary>
    [Fact]
    public async Task InferCategory_LlmReturnsIt_UsesIt()
    {
        var stub = new StubCategoryClient { Result = "it" };

        var result = await SearchCategoryClassifier.InferCategoryAsync(
            "dotnet native aot publish", explicitCategories: null, client: stub, enableFallback: true);

        Assert.Equal("it", result);
        Assert.Equal(1, stub.CallCount);
    }
    #endregion TEST_METHOD_LlmInfers_It_UsesIt

    #region TEST_METHOD_LlmUnavailable_FallsBackToGeneral [(c) — LLM unavailable → "general"]
    /// <summary>
    /// [PURPOSE]: Verifies AC (c): when the LLM is unavailable (client returns null), the classifier falls
    /// back to the deterministic "general" category when fallback is enabled — search never breaks.
    /// </summary>
    [Fact]
    public async Task InferCategory_LlmUnavailable_FallsBackToGeneral()
    {
        var stub = new StubCategoryClient { Result = null };

        var result = await SearchCategoryClassifier.InferCategoryAsync(
            "it is cold today", explicitCategories: null, client: stub, enableFallback: true);

        Assert.Equal(SearchCategoryClassifier.FallbackCategory, result);
        Assert.Equal(1, stub.CallCount);
    }
    #endregion TEST_METHOD_LlmUnavailable_FallsBackToGeneral

    #region TEST_METHOD_LlmInvalid_FallsBackToGeneral [(d) — invalid LLM response → "general"]
    /// <summary>
    /// [PURPOSE]: Verifies AC (d): an invalid LLM response surfaces as null from the client (closed-set
    /// validation is the client's responsibility) — the classifier treats it as unavailable and falls back
    /// to "general". Also covers a null client reference.
    /// </summary>
    [Fact]
    public async Task InferCategory_LlmInvalidOrNullClient_FallsBackToGeneral()
    {
        // Invalid → client returns null.
        var invalidStub = new StubCategoryClient { Result = null };
        var resultInvalid = await SearchCategoryClassifier.InferCategoryAsync(
            "ambiguous query", explicitCategories: null, client: invalidStub, enableFallback: true);
        Assert.Equal(SearchCategoryClassifier.FallbackCategory, resultInvalid);

        // Null client reference (defensive) → same fallback.
        var resultNullClient = await SearchCategoryClassifier.InferCategoryAsync(
            "ambiguous query", explicitCategories: null, client: null, enableFallback: true);
        Assert.Equal(SearchCategoryClassifier.FallbackCategory, resultNullClient);
    }
    #endregion TEST_METHOD_LlmInvalid_FallsBackToGeneral

    #region TEST_METHOD_FallbackDisabled_EmptyCategory [(—) — EnableFallback=false and LLM fails → empty]
    /// <summary>
    /// [PURPOSE]: Verifies that when EnableFallback is false and the LLM yields nothing usable, the
    /// classifier returns empty (SearXNG's own general default then applies at the client boundary).
    /// </summary>
    [Fact]
    public async Task InferCategory_FallbackDisabled_LlmFails_ReturnsEmpty()
    {
        var stub = new StubCategoryClient { Result = null };

        var result = await SearchCategoryClassifier.InferCategoryAsync(
            "query", explicitCategories: null, client: stub, enableFallback: false);

        Assert.Equal(string.Empty, result);
    }
    #endregion TEST_METHOD_FallbackDisabled_EmptyCategory
}
#endregion CLASS_SearchCategoryClassifierTests
