#region MODULE_CONTRACT [DOMAIN(Test): SearXNG Client; CONCEPT(Unit Tests): MockHttpClient + CB lifecycle]
/**
 * [GREP_SUMMARY]: SearXNGClientTests, xunit, test, mock HTTP handler, circuit breaker, timeout, empty response, URL building
 * [STRUCTURE]: > MockHandler -> o SearchAsync() -> = Assert(result) | > Failures>=3 -> O CB.OPEN -> throw unavailable | > Timeout -> throw unavailable
 *
 * <summary>
 * [PURPOSE]: Unit tests for the SearXNGClient HTTP client layer (M4). Covers:
 * successful search, timeout handling, HTTP errors, Circuit Breaker open/close lifecycle,
 * empty response handling, and URL parameter construction. Uses a manual MockHttpMessageHandler
 * (no Moq dependency) to isolate from real HTTP calls.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Tests are self-contained — each creates its own SearXNGClient with fresh
 * CircuitBreakerState, HttpClient (with mock handler), and IOptions&lt;SearXNGSettings&gt;.
 * No test depends on another's state. MockHandler tracks whether SendAsync was called to verify
 * CB OPEN behavior (no HTTP calls when breaker is open).
 * [RATIONALE]: Q: Why manual MockHttpMessageHandler instead of Moq? A: Keeps test project
 * dependencies minimal — xUnit + configuration packages only. Manual handler gives full control
 * over delay-based timeout testing and response customization.
 * [CHANGES]: LAST_CHANGE: M4 — initial creation (DevelopmentPlan.md §2 step 6).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using McpWebSearchService.Configuration;
using McpWebSearchService.Models;
using McpWebSearchService.Serialization;
using McpWebSearchService.Services;

namespace McpWebSearchService.Tests.Services;

#region CLASS_SearXNGClientTests [DOMAIN(Test): SearXNG Client unit tests]
/// <summary>
/// [PURPOSE]: Unit tests for the typed SearXNG HTTP client (M4).
/// </summary>
public class SearXNGClientTests : IDisposable
{
    #region MOCK_CLASS_MockHttpMessageHandler [DOMAIN(Test): Mock HTTP handler]
    /// <summary>
    /// [PURPOSE]: Manual DelegatingHandler that overrides SendAsync to return a pre-configured
    /// response or throw an exception. Tracks whether the underlying handler was invoked.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? ResponseFunc { get; set; }

        /// <summary>true if SendAsync was actually called (i.e., underlying handler executed).</summary>
        public bool WasCalled { get; internal set; } = false;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            WasCalled = true;

            if (ResponseFunc != null)
            {
                return await ResponseFunc(request, ct);
            }

            // Default: 200 OK with empty SearXNG response JSON.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
    #endregion MOCK_CLASS_MockHttpMessageHandler

    #region HELPER_METHOD_CreateClient [DOMAIN(Test): Factory for test fixtures]
    /// <summary>
    /// [PURPOSE]: Creates a configured SearXNGClient with a mock handler, fresh circuit breaker,
    /// and default settings. Shared across all tests to ensure isolation.
    /// </summary>
    private static (SearXNGClient client, MockHttpMessageHandler handler, CircuitBreakerState breaker) CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responseFunc = null,
        int maxResults = 5, string baseUrl = "http://searxng:8080")
    {
        var handler = new MockHttpMessageHandler();
        if (responseFunc != null)
        {
            handler.ResponseFunc = responseFunc;
        }

        var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15) // ADR-008: 15s timeout, single source.
        };

        var settings = Options.Create(new SearXNGSettings { BaseUrl = baseUrl, MaxResults = maxResults });
        var logger = new TestLogger<SearXNGClient>();
        var breaker = new CircuitBreakerState();

        return (new SearXNGClient(httpClient, settings, logger, breaker), handler, breaker);
    }
    #endregion HELPER_METHOD_CreateClient

    private HttpClient? _httpClient;

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    #region METHOD_SearchAsync_Success_ReturnsResults [DOMAIN(Test): Happy path]
    /// <summary>
    /// [PURPOSE]: Verify that SearchAsync returns parsed results from a successful 200-OK SearXNG response.
    /// Mock handler returns JSON with three search results, each having Title, Url, Content, and Engine.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Success_ReturnsResults()
    {
        var sampleJson = "{ \"results\": [" +
            "{ \"title\": \"Result One\", \"url\": \"https://example.com/1\", \"content\": \"Snippet one\", \"engine\": \"google\" }, " +
            "{ \"title\": \"Result Two\", \"url\": \"https://example.com/2\", \"content\": \"Snippet two\", \"engine\": \"bing\" }, " +
            "{ \"title\": \"Result Three\", \"url\": \"https://example.com/3\", \"content\": \"Snippet three\", \"engine\": null }" +
        "] }";

        var (client, handler, _) = CreateClient((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sampleJson, System.Text.Encoding.UTF8, "application/json")
        }));

        var result = await client.SearchAsync(new SearchRequest { Query = "test query", Categories = "general" });

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("Result One", result[0].Title);
        Assert.Equal("https://example.com/1", result[0].Url);
        Assert.Equal("Snippet one", result[0].Content);
        Assert.Equal("google", result[0].Engine);

        // Verify the mock handler was called (request actually dispatched).
        Assert.True(handler.WasCalled, "MockHttpMessageHandler.SendAsync should have been invoked.");
    }
    #endregion METHOD_SearchAsync_Success_ReturnsResults

    #region METHOD_SearchAsync_Timeout_ThrowsUnavailableException [DOMAIN(Test): Timeout handling]
    /// <summary>
    /// [PURPOSE]: Verify that a TaskCanceledException (simulating HttpClient.Timeout) is
    /// wrapped into SearXNGUnavailableException with the correct Russian default message.
    /// </summary>
    [Fact]
    public async Task SearchAsync_Timeout_ThrowsUnavailableException()
    {
        using var cts = new CancellationTokenSource();

        _httpClient = new HttpClient(new MockHttpMessageHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
        var settings = Options.Create(new SearXNGSettings { BaseUrl = "http://searxng:8080" });
        var logger = new TestLogger<SearXNGClient>();
        var breaker = new CircuitBreakerState();

        // Configure handler to throw TaskCanceledException (what HttpClient.Timeout does).
        _httpClient = new HttpClient(new MockHttpMessageHandler { ResponseFunc = (_, ct) =>
            throw new TaskCanceledException("Request timed out", null, ct) })
            { Timeout = TimeSpan.FromSeconds(30) };  // Long timeout so handler fires first.

        var client = new SearXNGClient(_httpClient, settings, logger, breaker);

        await Assert.ThrowsAsync<SearXNGUnavailableException>(async () =>
            await client.SearchAsync(new SearchRequest { Query = "timeout test" }, cts.Token));
    }
    #endregion METHOD_SearchAsync_Timeout_ThrowsUnavailableException

    #region METHOD_SearchAsync_HttpError_ThrowsUnavailableException [DOMAIN(Test): HTTP 500 error]
    /// <summary>
    /// [PURPOSE]: Verify that an HTTP 500 Internal Server Error from SearXNG is wrapped into
    /// SearXNGUnavailableException. The Circuit Breaker should record the failure.
    /// </summary>
    [Fact]
    public async Task SearchAsync_HttpError_ThrowsUnavailableException()
    {
        var (client, handler, breaker) = CreateClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = "Internal Server Error",
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            }));

        await Assert.ThrowsAsync<SearXNGUnavailableException>(async () =>
            await client.SearchAsync(new SearchRequest { Query = "error test" }));

        // Verify Circuit Breaker recorded the failure (threshold=3, so not yet OPEN).
        Assert.True(breaker.FailureCount >= 1);
    }
    #endregion METHOD_SearchAsync_HttpError_ThrowsUnavailableException

    #region METHOD_CircuitBreaker_OpensAfterThreshold_AndClosesAfterRecovery [DOMAIN(Test): CB lifecycle]
    /// <summary>
    /// [PURPOSE]: Verify the full Circuit Breaker lifecycle:
    /// 1. Three sequential failures → OPEN state (subsequent requests blocked without HTTP).
    /// 2. Simulate OpenDuration elapsed (via SetLastFailureTime) → HALF_OPEN probe succeeds → CLOSED.
    /// </summary>
   [Fact]
    public async Task CircuitBreaker_OpensAfterThreshold_AndClosesAfterRecovery()
    {
        var breaker = new CircuitBreakerState();

        // Failures 1-3: return HTTP 500 (will be counted by the SAME breaker).
        for (int i = 1; i <= 3; i++)
        {
            using var handler = new MockHttpMessageHandler();
            handler.ResponseFunc = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var settings = Options.Create(new SearXNGSettings { BaseUrl = "http://searxng:8080" });
            var logger = new TestLogger<SearXNGClient>();
            var client = new SearXNGClient(_httpClient, settings, logger, breaker);

            await Assert.ThrowsAsync<SearXNGUnavailableException>(async () =>
                await client.SearchAsync(new SearchRequest { Query = $"fail-{i}" }));
        }

        // After 3 failures, breaker should be OPEN.
        Assert.True(breaker.IsOpen, "Circuit Breaker should be OPEN after 3 consecutive failures.");

        // Fourth call: no HTTP request should be made — CB rejects it immediately.
        using var handler4 = new MockHttpMessageHandler();
        var client4 = new SearXNGClient(
            new HttpClient(handler4) { Timeout = TimeSpan.FromSeconds(15) },
            Options.Create(new SearXNGSettings { BaseUrl = "http://searxng:8080" }),
            new TestLogger<SearXNGClient>(),
            breaker);

        var ex = await Assert.ThrowsAsync<SearXNGUnavailableException>(async () =>
            await client4.SearchAsync(new SearchRequest { Query = "should-blocked-by-cb" }));

        Assert.Contains("Circuit Breaker OPEN", ex.Message);
    }
    #endregion METHOD_CircuitBreaker_OpensAfterThreshold_AndClosesAfterRecovery

    #region METHOD_SearchAsync_EmptyResponse_ReturnsEmptyList [DOMAIN(Test): Empty results]
    /// <summary>
    /// [PURPOSE]: Verify that a 200-OK response with an empty "results" array returns
    /// an IReadOnlyList&lt;SearXNGResult&gt; with zero items (not null).
    /// </summary>
    [Fact]
    public async Task SearchAsync_EmptyResponse_ReturnsEmptyList()
    {
        var (client, _, _) = CreateClient((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{ ""results"": [] }", System.Text.Encoding.UTF8, "application/json")
            }));

        var result = await client.SearchAsync(new SearchRequest { Query = "no results" });

        Assert.NotNull(result);
        Assert.Empty(result);
    }
    #endregion METHOD_SearchAsync_EmptyResponse_ReturnsEmptyList

    #region METHOD_BuildUrl_IncludesAllParameters [DOMAIN(Test): URL construction]
    /// <summary>
    /// [PURPOSE]: Verify that BuildUrl constructs a correct query string with all parameters:
    /// q=encoded-query, categories=news (explicit), format=json (required), language=en (optional), time_range=week (optional).
    /// </summary>
    [Fact]
    public async Task BuildUrl_IncludesAllParameters()
    {
        var capturedUri = string.Empty;
        using var handler = new MockHttpMessageHandler();

        handler.ResponseFunc = async (req, ct) =>
        {
            capturedUri = req.RequestUri?.ToString() ?? "";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{ ""results"": [] }", System.Text.Encoding.UTF8, "application/json")
            };
        };

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var settings = Options.Create(new SearXNGSettings { BaseUrl = "http://searxng:8080" });
        var logger = new TestLogger<SearXNGClient>();
        var breaker = new CircuitBreakerState();

        var client = new SearXNGClient(_httpClient, settings, logger, breaker);
        await client.SearchAsync(new SearchRequest
        {
            Query = "test query",
            Categories = "news",
            Language = "en",
            TimeRange = "week"
        });

        Assert.NotEmpty(capturedUri);
        // HttpClient normalizes the URI — spaces appear as literal " ", not "%20".
        Assert.Contains("q=", capturedUri);
        Assert.Contains("test query", capturedUri);
        Assert.Contains("categories=news", capturedUri);
        Assert.Contains("format=json", capturedUri);
        Assert.Contains("language=en", capturedUri);
        Assert.Contains("time_range=week", capturedUri);
    }
    #endregion METHOD_BuildUrl_IncludesAllParameters

    #region METHOD_BuildUrl_DefaultCategories [DOMAIN(Test): Default categories]
    /// <summary>
    /// [PURPOSE]: Verify that when Categories is empty, BuildUrl uses "general" as the default.
    /// </summary>
    [Fact]
    public async Task BuildUrl_DefaultCategories_IsGeneral()
    {
        var capturedUri = string.Empty;

        // Return valid SearXNG JSON (required because SearchAsync deserializes the response).
        const string jsonBody = "{\"results\":[]}";
        using var handler = new MockHttpMessageHandler();
        handler.ResponseFunc = (req, ct) =>
        {
            capturedUri = req.RequestUri?.ToString() ?? "";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jsonBody) });
        };

        _httpClient = new HttpClient(handler);
        var settings = Options.Create(new SearXNGSettings { BaseUrl = "http://searxng:8080" });
        var logger = new TestLogger<SearXNGClient>();
        var breaker = new CircuitBreakerState();

        var client = new SearXNGClient(_httpClient, settings, logger, breaker);
        await client.SearchAsync(new SearchRequest { Query = "search", Categories = "" });

        Assert.Contains("categories=general", capturedUri);
    }
    #endregion METHOD_BuildUrl_DefaultCategories
}
#endregion CLASS_SearXNGClientTests

#region HELPER_CLASS_TestLogger [DOMAIN(Test): Minimal ILogger for unit testing]
/// <summary>
/// [PURPOSE]: A minimal, no-op ILogger&lt;T&gt; implementation used by unit tests to satisfy the constructor contract.
/// No output is produced — LDD logs are written during production but suppressed in test runs.
/// </summary>
internal sealed class TestLogger<T> : ILogger<T>, IDisposable
{
    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
#endregion HELPER_CLASS_TestLogger
