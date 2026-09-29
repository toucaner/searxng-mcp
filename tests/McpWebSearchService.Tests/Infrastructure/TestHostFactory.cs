#region MODULE_CONTRACT [DOMAIN(Test): TestHostFactory; CONCEPT(DI builder): Real DI container for integration tests]
/**
 * [GREP_SUMMARY]: TestHostFactory, real DI, mock HttpMessageHandler, DelegatingHandler, SearXNGClient, WebSearchTools, CircuitBreakerState
 * [STRUCTURE]: > CreateMockHandler() = MockSearXngHandler -> o SendAsync() -> return configured response | throw configured exception
 *              -> BuildServiceCollection(settings) = AddOptions + AddSingleton<CB> + AddHttpClient<SearXNGClient>(handler) + AddHttpClient("FetchExtract")(mock handler)
 *              -> CreateWebSearchTools() = cachedProvider.GetRequiredService<>() -> WebSearchTools instance (SAME provider → SAME CB singleton)
 *
 * <summary>
 * [PURPOSE]: Builds a real DI container (mirroring Program.cs wiring from ADR-017) for integration tests.
 * Injects mock HTTP handlers at the HttpClient boundary via DelegatingHandler — correct isolation point
 * per ADR-026: test the pipeline end-to-end while mocking only the network layer. The ServiceProvider is
 * built ONCE in the constructor and reused for all CreateWebSearchTools() calls — this ensures the
 * CircuitBreakerState singleton persists across calls so failures accumulate (Bug #6 fix).
 * </summary>
 * <remarks>
 * [INVARIANTS]: BuildServiceProvider is called exactly once per TestHostFactory instance. The SearXNG client
 * handler is shared across all HttpClient instances that target the SearXNG endpoint (only one). Mock responses
 * are set via properties on the DelegatingHandler — tests configure response before calling WebSearch or FetchAndExtract.
 * [RATIONALE]: Q: Why not mock ISearXNGClient like unit tests do? A: Unit tests mock at the interface boundary,
 * which bypasses real DI wiring and HttpClient pipeline. Integration tests must exercise the REAL SearXNGClient
 * (with its Circuit Breaker, timeout handling, JSON deserialization) — hence the DelegatingHandler approach.
 * [CHANGES]: LAST_CHANGE: M8-debug — fixed Bug #5 (Bind type mismatch), Bug #4 (FetchHandler ResponseToReturn), Bug #6 (CB singleton isolation).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using McpWebSearchService.Configuration;
using McpWebSearchService.Models;
using McpWebSearchService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;

namespace McpWebSearchService.Tests.Infrastructure;

#region CLASS_TestHostFactory [DOMAIN(Test): Real DI container builder for integration tests]
/// <summary>
/// [PURPOSE]: Factory that builds a real DI container mirroring Program.cs wiring, with mock HTTP handlers.
/// The ServiceProvider is built once in the constructor and reused so CircuitBreakerState singleton persists.
/// </summary>
internal sealed class TestHostFactory : IDisposable
{
    #region FIELDS [DOMAIN(Test): Service provider + mock handler references]
    private ServiceProvider? _provider;

    /// <summary>The SearXNG HTTP handler injected into the SearXNG client — tests configure response via this.</summary>
    public MockSearXngHandler SearXngHandler { get; } = new();

    /// <summary>The FetchAndExtract HTTP handler injected into the named "FetchExtract" HttpClient.</summary>
    public MockFetchHandler FetchHandler { get; } = new();

    /// <summary>The local-LLM HTTP handler injected into the category-inference client (M9, ADR-032).</summary>
    public MockLlmHandler LlmHandler { get; } = new();

    private readonly SearXNGSettings _settings;

    private readonly CategoryInferenceSettings _categorySettings;

    private readonly CapturingLogger<WebSearchTools>? _webSearchLogger;
    private readonly CapturingLogger<SearXNGClient>? _clientLogger;
    private readonly CapturingLogger<SearchResultProcessor>? _processorLogger;
    private readonly CapturingLogger<LocalLlmCategoryInference>? _llmLogger;
    #endregion FIELDS

    /// <summary>All log entries captured from WebSearchTools.</summary>
    public IReadOnlyList<LogEntry> WebSearchLogs => _webSearchLogger?.Entries ?? [];

    /// <summary>All log entries captured from SearXNGClient.</summary>
    public IReadOnlyList<LogEntry> ClientLogs => _clientLogger?.Entries ?? [];

    /// <summary>All log entries captured from SearchResultProcessor.</summary>
    public IReadOnlyList<LogEntry> ProcessorLogs => _processorLogger?.Entries ?? [];

    /// <summary>All log entries captured from LocalLlmCategoryInference (M9).</summary>
    public IReadOnlyList<LogEntry> LlmLogs => _llmLogger?.Entries ?? [];

    #region CTOR_TestHostFactory [DOMAIN(Test): Configuration constructor]
    /// <summary>
    /// [PURPOSE]: Constructs the factory with SearXNG settings and optional capturing loggers.
    /// Builds the DI container ONCE — all subsequent CreateWebSearchTools() calls resolve from the same provider.
    /// </summary>
    public TestHostFactory(SearXNGSettings? settings = null,
        CapturingLogger<WebSearchTools>? webSearchLogger = null,
        CapturingLogger<SearXNGClient>? clientLogger = null,
        CapturingLogger<SearchResultProcessor>? processorLogger = null,
        CategoryInferenceSettings? categorySettings = null,
        CapturingLogger<LocalLlmCategoryInference>? llmLogger = null)
    {
        _settings = settings ?? new SearXNGSettings { DefaultLanguage = "ru", MaxResults = 10, BlockedDomains = [] };
        _categorySettings = categorySettings ??
            new CategoryInferenceSettings
            {
                BaseUrl = "http://localhost:11434",
                Model = "test-model",
                TimeoutSeconds = 5,
                EnableFallback = true,
                Categories = ["it", "news", "science", "images", "videos", "music", "books", "weather", "map", "general"]
            };
        _webSearchLogger = webSearchLogger;
        _clientLogger = clientLogger;
        _processorLogger = processorLogger;
        _llmLogger = llmLogger;

        // BUG_FIX_CONTEXT: [HYPOTHESIS: Bug #6 — each CreateWebSearchTools() call built a NEW ServiceProvider, so the CircuitBreakerState singleton was never shared. 3 failures never accumulated on the same CB instance, so it never tripped to OPEN. Fix: build the provider ONCE in the constructor, reuse for all calls.]
        // Build DI container on construction — cached for the lifetime of this factory.
        _provider = BuildServiceProvider();
    }
    #endregion CTOR_TestHostFactory

    /// <summary>
    /// Resolves WebSearchTools from the CACHED ServiceProvider — all calls share the same
    /// CircuitBreakerState singleton, so failures accumulate correctly (Bug #6 fix).
    /// </summary>
    public WebSearchTools CreateWebSearchTools()
    {
        // BUG_FIX_CONTEXT: [scar: Previously this method called BuildServiceProvider() each time, creating a new ServiceProvider + new CB singleton per call. Now resolves from the cached _provider so CB state persists. This is critical for the CircuitBreaker trip test which needs 3 failures on the SAME CB instance.]
        return _provider!.GetRequiredService<WebSearchTools>();
    }

    #region METHOD_BuildServiceProvider [DOMAIN(Test): Real DI builder]
    /// <summary>
    /// [PURPOSE]: Builds a fresh ServiceCollection mirroring Program.cs: AddOptions + CB singleton + SearXNG typed client (with mock handler)
    /// + FetchExtract named client + SearchResultProcessor + WebSearchTools. Returns ServiceProvider.
    /// </summary>
    private ServiceProvider BuildServiceProvider()
    {
        var sc = new ServiceCollection();

        // BUG_FIX_CONTEXT: [HYPOTHESIS: Bug #5 — OptionsBuilder<T>.Bind() expects an IConfiguration parameter, not IOptions<T>. Passing Options.Create(_settings) is a type mismatch (CS1503). Fix: register the pre-configured settings object directly as IOptions<T> singleton.]
        // 1. Configuration — register the pre-configured settings object directly (Bug #5 fix).
        sc.AddSingleton<IOptions<SearXNGSettings>>(Options.Create(_settings));
        // BUG_FIX_CONTEXT: [scar: @code used AddOptions<T>().Bind(Options.Create(_settings)) which is a type error — Bind() takes IConfiguration, not IOptions<T>. Registering IOptions<T> directly via Options.Create() is the correct pattern for pre-configured settings in test scenarios.]

        // 2. Circuit Breaker singleton (ADR-017) — shared across all WebSearchTools resolved from this provider.
        sc.AddSingleton<CircuitBreakerState>();

        // 3. SearXNG typed client with mock handler — real SearXNGClient, not mocked interface.
        sc.AddHttpClient<ISearXNGClient, SearXNGClient>()
            .ConfigurePrimaryHttpMessageHandler(() => SearXngHandler)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));

        // 4. FetchAndExtract named client with mock handler.
        sc.AddHttpClient("FetchExtract")
            .ConfigurePrimaryHttpMessageHandler(() => FetchHandler)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(15));

        // 4.5 M9 (ADR-032): CategoryInference options + typed LLM client with mock handler.
        // Registered BEFORE WebSearchTools so its constructor dependency can be resolved.
        sc.AddSingleton<IOptions<CategoryInferenceSettings>>(Options.Create(_categorySettings));
        sc.AddHttpClient<ICategoryInferenceClient, LocalLlmCategoryInference>()
            .ConfigurePrimaryHttpMessageHandler(() => LlmHandler);

        // 5. Processor — uses real settings + NullLogger by default.
        sc.AddSingleton<ISearchResultProcessor, SearchResultProcessor>();

        // 6. WebSearchTools with optional capturing loggers (for LDD verification).
        if (_webSearchLogger != null)
            sc.AddSingleton<ILogger<WebSearchTools>>(_webSearchLogger);
        else
            sc.AddSingleton<ILogger<WebSearchTools>>(NullLogger<WebSearchTools>.Instance);

        if (_clientLogger != null)
            sc.AddSingleton<ILogger<SearXNGClient>>(_clientLogger);
        else
            sc.AddSingleton<ILogger<SearXNGClient>>(NullLogger<SearXNGClient>.Instance);

        if (_processorLogger != null)
            sc.AddSingleton<ILogger<SearchResultProcessor>>(_processorLogger);
        else
            sc.AddSingleton<ILogger<SearchResultProcessor>>(NullLogger<SearchResultProcessor>.Instance);

        if (_llmLogger != null)
            sc.AddSingleton<ILogger<LocalLlmCategoryInference>>(_llmLogger);
        else
            sc.AddSingleton<ILogger<LocalLlmCategoryInference>>(NullLogger<LocalLlmCategoryInference>.Instance);

        // 7. WebSearchTools — resolved via DI (gets all dependencies from container).
        sc.AddSingleton<WebSearchTools>();

        return sc.BuildServiceProvider();
    }
    #endregion METHOD_BuildServiceProvider

    /// <summary>Disposes the underlying ServiceProvider.</summary>
    public void Dispose() => _provider?.Dispose();
}
#endregion CLASS_TestHostFactory

#region MOCK_CLASS_MockSearXngHandler [DOMAIN(Test): DelegatingHandler for SearXNG endpoint]
/// <summary>
/// [PURPOSE]: A DelegatingHandler that acts as a mock HTTP server for the SearXNG client.
/// Tests configure ResponseToReturn (response to send) or ExceptionToThrow (exception to raise).
/// Captures the last HttpRequestMessage sent via HttpClient, enabling request verification.
/// </summary>
internal sealed class MockSearXngHandler : DelegatingHandler
{
    /// <summary>The response this handler will return.</summary>
    public HttpResponseMessage? ResponseToReturn { get; set; }

    /// <summary>If non-null, the handler throws this exception instead of returning a response.</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>Captures each HttpRequestMessage sent through this handler — for request verification.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request; // capture the actual request URL.

        if (ExceptionToThrow != null)
            throw ExceptionToThrow;

        return ResponseToReturn!;
    }
}
#endregion MOCK_CLASS_MockSearXngHandler

#region MOCK_CLASS_MockFetchHandler [DOMAIN(Test): DelegatingHandler for fetch_and_extract endpoint]
/// <summary>
/// [PURPOSE]: A DelegatingHandler that acts as a mock HTTP server for the fetch_and_extract tool.
/// Tests configure ResponseToReturn (response to send) or ExceptionToThrow (exception to raise).
/// This replaces the old NoOpHttpMessageHandler which had no ResponseToReturn property (Bug #4 fix).
/// </summary>
// BUG_FIX_CONTEXT: [HYPOTHESIS: Bug #4 — NoOpHttpMessageHandler had no ResponseToReturn property, so tests couldn't configure mock responses for FetchAndExtract. Fix: replace with a mock handler that supports ResponseToReturn + ExceptionToThrow, same pattern as MockSearXngHandler but a SEPARATE instance for independent configuration.]
internal sealed class MockFetchHandler : DelegatingHandler
{
    /// <summary>The response this handler will return. If null, throws InvalidOperationException.</summary>
    public HttpResponseMessage? ResponseToReturn { get; set; }

    /// <summary>If non-null, the handler throws this exception instead of returning a response.</summary>
    public Exception? ExceptionToThrow { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (ExceptionToThrow != null)
            throw ExceptionToThrow;

        return ResponseToReturn != null
            ? Task.FromResult(ResponseToReturn)
            : throw new InvalidOperationException("MockFetchHandler: configure ResponseToReturn before calling WebSearchTools.FetchAndExtract.");
    }
}
// BUG_FIX_CONTEXT: [scar: @code created NoOpHttpMessageHandler which only threw InvalidOperationException — it had no way to return a mock response. FetchAndExtract tests need to set ResponseToReturn. Replaced with MockFetchHandler (DelegatingHandler with ResponseToReturn + ExceptionToReturn). Using DelegatingHandler (not HttpMessageHandler) so it can be registered via ConfigurePrimaryHttpMessageHandler and composed in the HttpClient pipeline.]
#endregion MOCK_CLASS_MockFetchHandler

#region MOCK_CLASS_MockLlmHandler [DOMAIN(Test): DelegatingHandler for local-LLM category-inference endpoint]
/// <summary>
/// [PURPOSE]: A DelegatingHandler that acts as a mock HTTP server for the local-LLM category-inference client
/// (M9, ADR-032). By default returns a successful OpenAI-compatible chat completion whose content is the
/// configured <see cref="CategoryToReturn"/> (default "general") so that DI-integration tests that omit an
/// explicit category continue to resolve to "general" — preserving prior M8 behaviour. A FRESH
/// HttpResponseMessage is built on every SendAsync (category string persisted, not a reused content stream),
/// so multiple web_search calls in one test do not hit a disposed stream. Tests may set
/// <see cref="CategoryToReturn"/> to return e.g. "it" or <see cref="ExceptionToThrow"/> to simulate LLM
/// unavailability.
/// </summary>
internal sealed class MockLlmHandler : DelegatingHandler
{
    /// <summary>Category word returned by default when no custom category is configured.</summary>
    public const string DefaultCategory = "general";

    /// <summary>The category word returned in the mocked chat completion (fresh response each call).</summary>
    public string CategoryToReturn { get; set; } = DefaultCategory;

    /// <summary>If non-null, the handler throws this exception (simulates LLM unavailable).</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>Captures the request body JSON for verification.</summary>
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content != null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);

        if (ExceptionToThrow != null)
            throw ExceptionToThrow;

        // Build a FRESH response each call — the StringContent stream of a reused response is disposed after
        // the first ReadFromJsonAsync, which would throw ObjectDisposedException on subsequent calls.
        return BuildCompletionResponse(CategoryToReturn);
    }

    /// <summary>Builds an OpenAI-compatible success response whose choices[0].message.content equals category.</summary>
    public static HttpResponseMessage BuildCompletionResponse(string category)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":\"{category}\"}},\"finish_reason\":\"stop\"}}]}}",
                System.Text.Encoding.UTF8,
                "application/json")
        };
}
#endregion MOCK_CLASS_MockLlmHandler
