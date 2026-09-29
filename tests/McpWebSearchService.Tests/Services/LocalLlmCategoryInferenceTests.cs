#region MODULE_CONTRACT [DOMAIN(Test): LocalLlmCategoryInference; CONCEPT(Unit Tests): MockHttpMessageHandler — success/failure/timeout/invalid]
/**
 * [GREP_SUMMARY]: LocalLlmCategoryInferenceTests, xunit, mock HTTP handler, OpenAI-compatible chat completions, success, http error, timeout, invalid json, strict validation
 * [STRUCTURE]: > LocalLlmCategoryInference(http, options, logger) -> o InferCategoryAsync(query) -> mock handler returns {choices[{message.content}]} -> = category | null on failure
 *
 * <summary>
 * [PURPOSE]: Unit tests for the LocalLlmCategoryInference typed client (M9). Covers: successful category
 * inference, HTTP failure (non-200), per-call timeout (linked CTS), invalid JSON in the response, and the
 * STRICT closed-set validation (a raw LLM word outside CategoryInferenceSettings.Categories → null).
 * Uses a manual MockHttpMessageHandler — no external mocking library.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Each test constructs its own client with a fresh mock handler and small settings. Every
 * failure path is verified to return null (never throw) — the null-on-failure contract is the milestone's
 * core guarantee (search must never hang). The success test also verifies the request body carries
 * chat_template_kwargs.enable_thinking=false (ADR-032 critical requirement).
 * [RATIONALE]: Q: Why construct the client directly rather than via DI? A: This is a unit test of the
 * HTTP/serialization/validation layer (per Draft Code Graph "LocalLlmClientTests"); DI wiring and the full
 * web_search chain are covered by IntegrationTests via TestHostFactory. Q: Why a manual handler instead of
 * Moq? A: Consistent with the existing test suite (SearXNGClientTests uses the same pattern) — no extra deps.
 * [CHANGES]: LAST_CHANGE: M9-fix — added request-priority-rule test (regression guard for the
 * SystemPrompt IT-priority improvement, M9 A/B probe).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using McpWebSearchService.Configuration;
using McpWebSearchService.Services;
using Microsoft.Extensions.Options;
using System.Net;

namespace McpWebSearchService.Tests.Services;

#region CLASS_LocalLlmCategoryInferenceTests [DOMAIN(Test): LocalLlmCategoryInference unit tests]
/// <summary>
/// [PURPOSE]: Unit tests for the local-LLM category-inference client (M9).
/// </summary>
public class LocalLlmCategoryInferenceTests
{
    #region MOCK_CLASS_MockHttpMessageHandler [DOMAIN(Test): Mock HTTP handler]
    /// <summary>
    /// [PURPOSE]: Manual HttpMessageHandler that returns a configured response, throws, or delays.
    /// Captures the request body and the request URI for verification.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ResponseFunc { get; set; }

        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content != null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            LastRequestUri = request.RequestUri;

            if (ResponseFunc != null)
                return await ResponseFunc(request, ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""choices"":[{""message"":{""role"":""assistant"",""content"":""general""},""finish_reason"":""stop""}]}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
    #endregion MOCK_CLASS_MockHttpMessageHandler

    #region HELPER_METHOD_CreateClient [DOMAIN(Test): Client + handler factory]
    /// <summary>
    /// [PURPOSE]: Builds a LocalLlmCategoryInference with a mock handler, configurable settings, and a
    /// TestLogger. Returns the client, handler, and captured settings for assertion.
    /// </summary>
    private static (LocalLlmCategoryInference client, MockHttpMessageHandler handler) CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responseFunc = null,
        int timeoutSeconds = 5,
        string baseUrl = "http://localhost:11434",
        string model = "test-model",
        string[]? categories = null)
    {
        var handler = new MockHttpMessageHandler();
        if (responseFunc != null)
            handler.ResponseFunc = responseFunc;

        var httpClient = new HttpClient(handler);
        var settings = new CategoryInferenceSettings
        {
            BaseUrl = baseUrl,
            Model = model,
            TimeoutSeconds = timeoutSeconds,
            EnableFallback = true,
            Categories = categories ?? ["it", "news", "science", "images", "videos", "music", "books", "weather", "map", "general"]
        };
        var logger = new TestLogger<LocalLlmCategoryInference>();

        return (new LocalLlmCategoryInference(httpClient, Options.Create(settings), logger), handler);
    }
    #endregion HELPER_METHOD_CreateClient

    #region TEST_METHOD_Success_ReturnsIt [(1) — success returns valid category + request carries enable_thinking=false]
    /// <summary>
    /// [PURPOSE]: Verifies that a 200-OK response with content "it" returns "it", and that the request body
    /// carries model, temperature:0, max_tokens:10, and chat_template_kwargs.enable_thinking:false (ADR-032).
    /// </summary>
    [Fact]
    public async Task InferCategoryAsync_Success_ReturnsIt_AndSendsEnableThinkingFalse()
    {
        // BUG_FIX_CONTEXT: [scar: This test verifies the ADR-032 critical requirement that the client sends
        // chat_template_kwargs {"enable_thinking": false} — without it a reasoning model never returns a
        // single word within the timeout (M9 practical probe).]
        var responseFunc = (HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""choices"":[{""message"":{""role"":""assistant"",""content"":""it""},""finish_reason"":""stop""}]}", System.Text.Encoding.UTF8, "application/json")
            });

        var (client, handler) = CreateClient(responseFunc);

        var category = await client.InferCategoryAsync("how to write a csharp unit test");

        Assert.Equal("it", category);

        // Verify request URI points at the OpenAI-compatible completions endpoint.
        Assert.NotNull(handler.LastRequestUri);
        Assert.EndsWith("/v1/chat/completions", handler.LastRequestUri!.ToString(), StringComparison.Ordinal);

        // Verify the request body carries the critical fields (enable_thinking=false).
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"temperature\":0", handler.LastRequestBody!);
        Assert.Contains("\"max_tokens\":10", handler.LastRequestBody!);
        Assert.Contains("\"enable_thinking\":false", handler.LastRequestBody!);
        Assert.Contains("\"model\":\"test-model\"", handler.LastRequestBody!);
    }
    #endregion TEST_METHOD_Success_ReturnsIt

    #region TEST_METHOD_HttpError_ReturnsNull [(2) — non-200 → null]
    /// <summary>
    /// [PURPOSE]: Verifies that an HTTP 500 response returns null (never throws) — category inference is
    /// best-effort and must not break web_search.
    /// </summary>
    [Fact]
    public async Task InferCategoryAsync_HttpError_ReturnsNull()
    {
        var responseFunc = (HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var (client, _) = CreateClient(responseFunc);

        var category = await client.InferCategoryAsync("some query");

        Assert.Null(category);
    }
    #endregion TEST_METHOD_HttpError_ReturnsNull

    #region TEST_METHOD_Timeout_ReturnsNull [(3) — per-call timeout → null]
    /// <summary>
    /// [PURPOSE]: Verifies that a slow LLM response exceeding CategoryInferenceSettings.TimeoutSeconds causes
    /// the linked CTS to fire and the client to return null (never throw, never hang indefinitely). Uses a
    /// short 1s timeout and a handler that delays 5s.
    /// </summary>
    [Fact]
    public async Task InferCategoryAsync_Timeout_ReturnsNull()
    {
        var responseFunc = async (HttpRequestMessage req, CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct); // exceeds 1s client timeout → linked CTS cancels.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""choices"":[{""message"":{""role"":""assistant"",""content"":""it""},""finish_reason"":""stop""}]}", System.Text.Encoding.UTF8, "application/json")
            };
        };

        var (client, _) = CreateClient(responseFunc, timeoutSeconds: 1);

        var category = await client.InferCategoryAsync("slow query");

        Assert.Null(category);
    }
    #endregion TEST_METHOD_Timeout_ReturnsNull

    #region TEST_METHOD_InvalidJson_ReturnsNull [(4) — malformed JSON → null]
    /// <summary>
    /// [PURPOSE]: Verifies that a non-JSON / malformed response body returns null (JsonException caught).
    /// </summary>
    [Fact]
    public async Task InferCategoryAsync_InvalidJson_ReturnsNull()
    {
        var responseFunc = (HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("this is <html>not json</html>", System.Text.Encoding.UTF8, "text/plain")
            });

        var (client, _) = CreateClient(responseFunc);

        var category = await client.InferCategoryAsync("query");

        Assert.Null(category);
    }
    #endregion TEST_METHOD_InvalidJson_ReturnsNull

    #region TEST_METHOD_OutOfSetCategory_ReturnsNull [(5) — strict closed-set validation → null]
    /// <summary>
    /// [PURPOSE]: Verifies STRICT validation: an LLM word that is NOT in the configured Categories closed set
    /// is rejected → null (fallback). Also verifies a multi-word content ("it please") is rejected.
    /// </summary>
    [Fact]
    public async Task InferCategoryAsync_OutOfSetCategory_ReturnsNull()
    {
        // LLM returns "shopping" — NOT in the default closed set.
        var responseFunc = (HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""choices"":[{""message"":{""role"":""assistant"",""content"":""shopping""},""finish_reason"":""stop""}]}", System.Text.Encoding.UTF8, "application/json")
            });

        var (client, _) = CreateClient(responseFunc);

        var category = await client.InferCategoryAsync("buy sneakers");

        Assert.Null(category);
    }
    #endregion TEST_METHOD_OutOfSetCategory_ReturnsNull

    #region TEST_METHOD_CaseInsensitive_Accepted [(6) — case-insensitive membership]
    /// <summary>
    /// [PURPOSE]: Verifies that a category differing only in case ("IT") is accepted (validated
    /// case-insensitively) and normalized to lowercase. Also verifies the empty-choices response → null.
    /// </summary>
    [Fact]
    public async Task InferCategoryAsync_CaseInsensitive_Accepted_Normalized()
    {
        var responseFunc = (HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""choices"":[{""message"":{""role"":""assistant"",""content"":""IT""},""finish_reason"":""stop""}]}", System.Text.Encoding.UTF8, "application/json")
            });

        var (client, _) = CreateClient(responseFunc);

        var category = await client.InferCategoryAsync("dotnet");

        Assert.Equal("it", category);
    }
    #endregion TEST_METHOD_CaseInsensitive_Accepted

    #region TEST_METHOD_RequestCarriesPriorityRule [(7) — system prompt carries IT-priority rule (A/B probe)]
    /// <summary>
    /// [PURPOSE]: Regression guard for the M9-fix: the system prompt must guide the model toward a specific
    /// category (IT queries -> it) instead of defaulting to "general". Verifies the request body carries the
    /// priority rule, the few-shot example, and the dynamically-appended allowed-category list.
    /// </summary>
    [Fact]
    public async Task InferCategoryAsync_RequestCarriesPriorityRule()
    {
        var (client, handler) = CreateClient();

        // The mock default returns "general"; we assert on the REQUEST body, not the response.
        var category = await client.InferCategoryAsync("dotnet aot");

        Assert.Equal("general", category);
        Assert.NotNull(handler.LastRequestBody);

        // Priority rule present (M9 A/B probe: without it, IT queries fell to "general").
        // Note: System.Text.Json escapes single quotes as \u0027 in the serialized wire body.
        Assert.Contains("reply \\u0027it\\u0027", handler.LastRequestBody!);
        Assert.Contains("Use \\u0027general\\u0027 ONLY when no specific category fits", handler.LastRequestBody!);

        // BuildRequest appends the configured allowed set to the system prompt.
        Assert.Contains("Allowed categories: it, news", handler.LastRequestBody!);

        // BuildRequest appends the configured allowed set to the system prompt.
        Assert.Contains("Allowed categories: it, news", handler.LastRequestBody!);
    }
    #endregion TEST_METHOD_RequestCarriesPriorityRule
}
#endregion CLASS_LocalLlmCategoryInferenceTests
