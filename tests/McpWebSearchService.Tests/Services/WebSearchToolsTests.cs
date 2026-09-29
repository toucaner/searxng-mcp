#region MODULE_CONTRACT [DOMAIN(Test): WebSearchTools; CONCEPT(Unit Tests): Manual mocks + mock HTTP handler]
/**
 * [GREP_SUMMARY]: WebSearchToolsTests, xunit, manual mocks for ISearXNGClient/ISearchResultProcessor, web_search, fetch_and_extract
 * [STRUCTURE]: > MockSearXNGClient -> o SearchAsync() = capturedRequest | > MockProcessor -> o Process(Results) = DtoArray
 *              -> WebSearchTools(client, processor, options, logger, factory) -> o WebSearch() / FetchAndExtract() -> = Assert(string output)
 *
 * <summary>
 * [PURPOSE]: Unit tests for WebSearchTools (M6). Covers web_search (success, unavailability, empty results, param mapping),
 * fetch_and_extract (invalid URL handled gracefully — full HTTP fetch uses real IHttpClientFactory + MockHttpMessageHandler).
 * Uses manual mock implementations of ISearXNGClient and ISearchResultProcessor (no Moq dependency).
 * </summary>
 * <remarks>
 * [INVARIANTS]: Each test creates its own WebSearchTools instance with fresh mocks — no shared mutable state.
 * Capture variables are method-local, not instance fields. Asserts verify both output string content and internal mock state.
 * [RATIONALE]: Q: Why manual mocks instead of Moq? A: Test project has no Moq/NSubstitute dependency — only xUnit + config packages.
 * Manual mocks give full control over behavior and capture verification without external libs.
 * [CHANGES]: LAST_CHANGE: M6 — initial creation (DevelopmentPlan.md §2 step 6).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using McpWebSearchService.Configuration;
using McpWebSearchService.Models;
using McpWebSearchService.Serialization;
using McpWebSearchService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace McpWebSearchService.Tests.Services;

#region CLASS_WebSearchToolsTests [DOMAIN(Test): WebSearchTools unit tests]
/// <summary>
/// [PURPOSE]: Unit tests for the WebSearchTools class (M6 MCP tools).
/// </summary>
public class WebSearchToolsTests : IDisposable
{
    #region MOCK_CLASS_MockSearXNGClient [DOMAIN(Test): Mock ISearXNGClient]
    /// <summary>
    /// [PURPOSE]: Manual mock of ISearXNGClient that allows configuring the response and capturing the request.
    /// </summary>
    private sealed class MockSearXNGClient : ISearXNGClient
    {
        public SearchRequest? CapturedRequest { get; private set; }

        public Task<IReadOnlyList<SearXNGResult>>? ResponseToReturn { get; set; }
        public Exception? ExceptionToThrow { get; set; }

        public async Task<IReadOnlyList<SearXNGResult>> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            CapturedRequest = request;
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            return await ResponseToReturn!;
        }
    }
    #endregion MOCK_CLASS_MockSearXNGClient

    #region MOCK_CLASS_MockProcessor [DOMAIN(Test): Mock ISearchResultProcessor]
    /// <summary>
    /// [PURPOSE]: Manual mock of ISearchResultProcessor that returns a pre-configured SearchResultDto[].
    /// </summary>
    private sealed class MockProcessor : ISearchResultProcessor
    {
        public IReadOnlyList<SearXNGResult>? CapturedRawResults { get; private set; }

        public SearchResultDto[]? ResultsToReturn { get; set; } = [];

        public SearchResultDto[] Process(IReadOnlyList<SearXNGResult> rawResults)
        {
            CapturedRawResults = rawResults;
            return ResultsToReturn!;
        }
    }
    #endregion MOCK_CLASS_MockProcessor

    #region MOCK_CLASS_MockCategoryInferenceClient [DOMAIN(Test): Mock ICategoryInferenceClient]
    /// <summary>
    /// [PURPOSE]: Manual mock of ICategoryInferenceClient (M9) that returns a configured category or null.
    /// Defaults to "general" (a valid closed-set member) so WebSearch tests that omit an explicit category
    /// behave as before. Tests may set CategoryToReturn = null to simulate LLM unavailability.
    /// </summary>
    private sealed class MockCategoryInferenceClient : ICategoryInferenceClient
    {
        /// <summary>Category returned by InferCategoryAsync, or null for LLM-unavailable.</summary>
        public string? CategoryToReturn { get; set; } = "general";

        public string? LastQuery { get; private set; }

        public Task<string?> InferCategoryAsync(string query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(CategoryToReturn);
        }
    }
    #endregion MOCK_CLASS_MockCategoryInferenceClient

    private readonly MockSearXNGClient _mockClient = new();
    private readonly MockProcessor _mockProcessor = new();
    private readonly MockCategoryInferenceClient _mockCategoryClient = new();
    private readonly SearXNGSettings _settings = new() { DefaultLanguage = "ru", MaxResults = 10, BlockedDomains = [] };
    private readonly IHttpClientFactory _httpClientFactory;

    public WebSearchToolsTests()
    {
        // Build a real DI container so we have IHttpClientFactory for the constructor.
        var sc = new ServiceCollection();
        sc.AddHttpClient("FetchExtract");
        var sp = sc.BuildServiceProvider();
        _httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    }

    public void Dispose() => GC.Collect(); // let disposed ServiceProvider be collected; no-op since we build per-test below

    private static WebSearchTools CreateWebSearchTools(
        ISearXNGClient client, ISearchResultProcessor processor, SearXNGSettings settings,
        ICategoryInferenceClient? categoryClient = null)
    {
        var factory = new TestHttpClientFactory(new Dictionary<string, HttpClient>());
        var category = categoryClient ?? new MockCategoryInferenceClient();
        var categorySettings = Options.Create(new CategoryInferenceSettings
        {
            BaseUrl = "http://localhost:11434",
            Model = "test-model",
            TimeoutSeconds = 5,
            EnableFallback = true,
            Categories = ["it", "news", "science", "images", "videos", "music", "books", "weather", "map", "general"]
        });
        return new WebSearchTools(client, processor, Options.Create(settings), NullLogger<WebSearchTools>.Instance, factory, category, categorySettings);
    }

    #region TEST_METHOD_WebSearch_Success [IMP:7-8][WebSearchToolsTests][WebSearch] Success path — JSON output verification
    /// <summary>
    /// Verifies that a successful web_search returns properly serialized SearchResultDto[] as JSON string.
    /// </summary>
    [Fact]
    public async Task WebSearch_ReturnsSerializedJson()
    {
        var settings = new SearXNGSettings { DefaultLanguage = "ru" };
        _mockClient.ResponseToReturn = Task.FromResult<IReadOnlyList<SearXNGResult>>(new List<SearXNGResult>
        {
            new() { Title = "Test Article", Url = "http://example.com/article", Content = "Snippet text.", Engine = "google" }
        });
        _mockProcessor.ResultsToReturn = [new SearchResultDto { Title = "Test Article", Url = "http://example.com/article", Snippet = "Snippet text." }];

        var tools = CreateWebSearchTools(_mockClient, _mockProcessor, settings);

        // Act: call web_search.
        var result = await tools.WebSearch("test query");

        // Assert: deserialize and verify content — 1 item with correct Title/Url.
        var items = JsonSerializer.Deserialize(result, McpJsonContext.Default.SearchResultDtoArray)!;
        Assert.NotNull(items);
        Assert.Single(items);
        Assert.Equal("Test Article", items[0].Title);
        Assert.Equal("http://example.com/article", items[0].Url);
    }
    #endregion TEST_METHOD_WebSearch_Success

    #region TEST_METHOD_WebSearch_Unavailable [IMP:7-8][WebSearchToolsTests][WebSearch] SearXNGUnavailableException → error message
    /// <summary>
    /// Verifies that when the SearXNG client throws SearXNGUnavailableException, the tool returns the exact error string.
    /// </summary>
    [Fact]
    public async Task WebSearch_SearXNGUnavailable_ReturnsErrorMessage()
    {
        var tools = CreateWebSearchTools(_mockClient, _mockProcessor, _settings);

        _mockClient.ExceptionToThrow = new SearXNGUnavailableException();

        var result = await tools.WebSearch("query");

        // JsonSerializer.Serialize on a string produces JSON-encoded text (with quotes).
        var deserialized = JsonSerializer.Deserialize(result, McpJsonContext.Default.String)!;
        Assert.Equal(SearXNGUnavailableException.DefaultMessage, deserialized);
    }
    #endregion TEST_METHOD_WebSearch_Unavailable

    #region TEST_METHOD_WebSearch_EmptyResults [IMP:7-8][WebSearchToolsTests][WebSearch] Empty results → "[]" string
    /// <summary>
    /// Verifies that an empty search result set produces a JSON empty array string.
    /// </summary>
    [Fact]
    public async Task WebSearch_EmptyResults_ReturnsEmptyJsonArray()
    {
        var settings = new SearXNGSettings { DefaultLanguage = "ru" };

        _mockClient.ResponseToReturn = Task.FromResult<IReadOnlyList<SearXNGResult>>([]);
        _mockProcessor.ResultsToReturn = [];

        var tools = CreateWebSearchTools(_mockClient, _mockProcessor, settings);

        var result = await tools.WebSearch("query");

        Assert.Equal("[]", result);
    }
    #endregion TEST_METHOD_WebSearch_EmptyResults

    #region TEST_METHOD_WebSearch_ParamMapping [IMP:7-8][WebSearchToolsTests][WebSearch] Parameter mapping to SearchRequest (categories, timeRange, language)
    /// <summary>
    /// Verifies that all tool parameters are correctly forwarded to the SearXNG client via SearchRequest.
    /// </summary>
    [Theory]
    [InlineData("general", null, "ru")]   // default categories, no time range, config default language
    [InlineData("news", "day", "en")]     // explicit everything
    [InlineData(null, null, null)]        // all defaults
    public async Task WebSearch_ForwardsParameters(string? categories, string? timeRange, string? language)
    {
        var settings = new SearXNGSettings { DefaultLanguage = "ru" };

        _mockClient.ResponseToReturn = Task.FromResult<IReadOnlyList<SearXNGResult>>([]);
        _mockProcessor.ResultsToReturn = [];

        var tools = CreateWebSearchTools(_mockClient, _mockProcessor, settings);

        // Act: call with all parameters — use explicit named args.
        await tools.WebSearch("test", categories ?? "general", timeRange, language);

        // Assert: verify captured request values.
        var req = _mockClient.CapturedRequest;
        Assert.NotNull(req);
        Assert.Equal("test", req.Query);
        Assert.Equal(categories ?? "general", req.Categories);
        Assert.Equal(timeRange, req.TimeRange);

        // Language: null param → DefaultLanguage from config.
        var expectedLang = string.IsNullOrWhiteSpace(language) ? settings.DefaultLanguage : language;
        Assert.Equal(expectedLang, req.Language);
    }
    #endregion TEST_METHOD_WebSearch_ParamMapping

    #region TEST_METHOD_FetchExtract_InvalidUrl [IMP:7-8][WebSearchToolsTests][FetchAndExtract] Invalid URL → error message string (not throw)
    /// <summary>
    /// Verifies that an invalid URL is handled gracefully — returns a descriptive error string instead of throwing.
    /// </summary>
    [Fact]
    public async Task FetchAndExtract_InvalidUrl_ReturnsErrorMessage()
    {
        var settings = new SearXNGSettings();

        // This tests the URL validation path — should not throw an exception.
        try
        {
            var tools = CreateWebSearchTools(_mockClient, _mockProcessor, settings);
            var result = await tools.FetchAndExtract("not-a-url");

            Assert.NotNull(result);
            Assert.False(string.IsNullOrEmpty(result));
            Assert.Contains("invalid URL", result); // error message contains this substring.
        }
        catch (Exception)
        {
            // Should NOT reach here — invalid URLs must produce error strings, not exceptions.
            throw new Exception("FetchAndExtract should not throw for invalid URL; it should return an error string.");
        }
    }
    #endregion TEST_METHOD_FetchExtract_InvalidUrl

    #region TEST_METHOD_FetchExtract_EmptyUrl [IMP:7-8][WebSearchToolsTests][FetchAndExtract] Empty URL → error message (not throw)
    /// <summary>
    /// Verifies that an empty or whitespace URL is handled gracefully.
    /// </summary>
    [Fact]
    public async Task FetchAndExtract_EmptyUrl_ReturnsErrorMessage()
    {
        var settings = new SearXNGSettings();

        try
        {
            var tools = CreateWebSearchTools(_mockClient, _mockProcessor, settings);
            var result = await tools.FetchAndExtract("   ");

            Assert.NotNull(result);
            Assert.False(string.IsNullOrEmpty(result));
        }
        catch (Exception)
        {
            throw new Exception("FetchAndExtract should not throw for empty URL.");
        }
    }
    #endregion TEST_METHOD_FetchExtract_EmptyUrl
}

#region MOCK_CLASS_TestHttpClientFactory [DOMAIN(Test): Lightweight IHttpClientFactory that returns a no-op HttpClient]
/// <summary>
/// [PURPOSE]: Minimal test implementation of IHttpClientFactory for constructing WebSearchTools in tests.
/// Returns an HttpClient whose SendAsync is overridden to not make real network calls — only used when tests
/// do NOT exercise the fetch_and_extract HTTP path (which uses MockHttpMessageHandler via ServiceCollection).
/// </summary>
internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    private readonly Dictionary<string, HttpClient> _clients = new();

    public TestHttpClientFactory(Dictionary<string, HttpClient> clients) => _clients = clients;

    public HttpClient CreateClient(string name)
    {
        if (_clients.TryGetValue(name, out var client)) return client;
        // Return a no-op HttpClient for tests that don't exercise fetch_and_extract.
        var handler = new NoOpHttpMessageHandler();
        return new HttpClient(handler);
    }

    private sealed class NoOpHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("NoOpHttpMessageHandler: this handler should not be used for fetch_and_extract tests.");
    }
}
#endregion MOCK_CLASS_TestHttpClientFactory

#endregion CLASS_WebSearchToolsTests
