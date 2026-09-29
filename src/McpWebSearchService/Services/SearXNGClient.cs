#region MODULE_CONTRACT [DOMAIN(HTTP): SearXNG Client; CONCEPT(Typed Client): IHttpClientFactory + CB + 15s timeout]
/**
 * [GREP_SUMMARY]: SearXNGClient, typed HTTP client, HttpClientFactory, CircuitBreakerState, SearchAsync, BuildUrl, McpJsonContext, AOT-safe
 * [STRUCTURE]: > SearchRequest -> o BuildUrl() -> + GET {BaseUrl}/search?q=...&format=json -> = ReadFromJson<SearXNGResponse> -> [] SearXNGResult -> return IReadOnlyList | > Failures >= 3 -> O CB.OPEN -> throw SearXNGUnavailableException
 *
 * <summary>
 * [PURPOSE]: Typed HTTP client for the SearXNG backend. Injected by IHttpClientFactory,
 * configured with a 15-second timeout (SPEC §4.2). Manual Circuit Breaker (3 failures →
 * 30s OPEN) prevents hammering an unavailable backend. All JSON deserialization uses
 * McpJsonContext source-generated serializers — zero reflection (AOT-safe per ADR-007).
 * </summary>
 * <remarks>
 * [INVARIANTS]: SearchAsync never returns null (returns Array.Empty on empty response).
 * Exceptions are always SearXNGUnavailableException — raw HttpRequestException / TaskCanceledException
 * do not escape to the caller. LDD logging covers every execution path with [IMP:1-10] markers.
 * [RATIONALE]: Q: Why manual Circuit Breaker instead of Polly? A: See ADR-007 — zero runtime
 * dependencies, fully AOT-safe, trivially simple state machine (~50 LOC). The 3-failure threshold
 * matches SPEC §4.2 requirements.
 * [CHANGES]: LAST_CHANGE: M4 — initial creation (DevelopmentPlan.md §2 step 4 + ADR-007).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using McpWebSearchService.Configuration;
using McpWebSearchService.Models;
using McpWebSearchService.Serialization;

namespace McpWebSearchService.Services;

#region CLASS_SearXNGClient [DOMAIN(HTTP): SearXNG Client; CONCEPT(Typed Client): HTTP GET to SearXNG]
/// <summary>
/// [PURPOSE]: Typed HTTP client for the SearXNG backend (M4). Configured via IHttpClientFactory
/// with a 15s timeout and manual Circuit Breaker gating.
/// </summary>
public sealed class SearXNGClient : ISearXNGClient
{
    #region FIELDS [DOMAIN(HTTP): Injected dependencies]
    private readonly HttpClient _httpClient;
    private readonly SearXNGSettings _settings;
    private readonly ILogger<SearXNGClient> _logger;
    private readonly CircuitBreakerState _breaker;
    #endregion FIELDS

    #region CTOR_SearXNGClient [DOMAIN(HTTP): Constructor]
    /// <summary>
    /// [PURPOSE]: Constructs the typed SearXNG client with injected dependencies.
    /// Timeout is set on HttpClient (not via CancellationTokenSource).
    /// </summary>
    /// <param name="httpClient">HttpClient from IHttpClientFactory — already configured with timeout.</param>
    /// <param name="options">SearXNG settings bound from configuration section.</param>
    /// <param name="logger">Structured logger for LDD trace logging [IMP:1-10].</param>
    /// <param name="breaker">Circuit Breaker state (shared singleton per endpoint).</param>
    public SearXNGClient(
        HttpClient httpClient,
        IOptions<SearXNGSettings> options,
        ILogger<SearXNGClient> logger,
        CircuitBreakerState breaker)
    {
        _httpClient = httpClient;
        _settings = options.Value;
        _logger = logger;
        _breaker = breaker;

        // ADR-008: 15-second timeout — single source of truth via HttpClient.Timeout.
        httpClient.Timeout = TimeSpan.FromSeconds(15);
    }
    #endregion CTOR_SearXNGClient

    #region METHOD_SearchAsync [DOMAIN(HTTP): Main entry point]
    /// <summary>
    /// [PURPOSE]: Executes a search against the SearXNG backend.
    /// Flow: Circuit Breaker gate → URL construction → HTTP GET → JSON deserialize → return results
    /// or throw SearXNGUnavailableException on any failure mode.
    /// </summary>
    /// <param name="request">Search query parameters (query, categories, language, time_range).</param>
    /// <param name="ct">Cancellation token — passed to HttpClient.SendAsync for early termination.</param>
    /// <returns>Non-null list of parsed search results from SearXNG.</returns>
    public async Task<IReadOnlyList<SearXNGResult>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        #region STEP_CircuitBreakerGate [DOMAIN(HTTP): Circuit Breaker check]
        // IMP:1 — Entry point log.
        _logger.LogDebug("[IMP:1][SearchAsync][START] Begin search for query='{Query}'", request.Query);

        if (!_breaker.TryAcquirePermit())
        {
            // IMP:9 — CB OPEN: fail-fast, no HTTP request sent.
            _logger.LogWarning("[IMP:9][CircuitBreaker][OPEN] Request rejected by Circuit Breaker.");
            throw new SearXNGUnavailableException(
                "Search service temporarily unavailable. Circuit Breaker OPEN.",
                null);
        }

        #region STEP_BeliefState [DOMAIN(HTTP): Belief State at start]
        // IMP:10 — Early belief state based on CB gate result.
        _logger.LogDebug("[IMP:10][SearchAsync][BELIEF] Circuit Breaker state was {State}",
            GetBreakerStateString(_breaker));
        #endregion STEP_BeliefState
        #endregion STEP_CircuitBreakerGate

        #region STEP_BuildUrl [DOMAIN(HTTP): URL construction]
        var url = BuildUrl(request);
        _logger.LogDebug("[IMP:7][SearchAsync][URL] Built URL: {Url}", url);
        #endregion STEP_BuildUrl

        try
        {
            var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                // IMP:8 — Success path.
                _breaker.RecordSuccess();
                _logger.LogDebug("[IMP:8][CircuitBreaker][CLOSED] SearXNG responded successfully.");

                var result = await response.Content.ReadFromJsonAsync<SearXNGResponse>(
                    McpJsonContext.Default.SearXNGResponse, ct).ConfigureAwait(false);

                // IMP:8 — Results returned.
                int count = result?.Results.Length ?? 0;
                _logger.LogDebug("[IMP:8][SearchAsync][SUCCESS] {Count} results from SearXNG.", count);
                return (IReadOnlyList<SearXNGResult>)(result?.Results ?? []);
            }

            // IMP:9 — HTTP error.
            var statusCode = (int)response.StatusCode;
            _logger.LogWarning("[IMP:9][SearchAsync][UNAVAILABLE] HTTP status {(int)response.StatusCode} for query='{Query}'",
                response.StatusCode, request.Query);
            _breaker.RecordFailure();

            throw new SearXNGUnavailableException(
                $"SearXNG returned HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                new HttpRequestException($"HTTP error {(int)response.StatusCode}"));
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // IMP:9 — HttpClient.Timeout expired.
            _logger.LogWarning("[IMP:9][SearchAsync][UNAVAILABLE] Request timed out for query='{Query}': {Message}",
                request.Query, ex.Message);
            _breaker.RecordFailure();

            throw new SearXNGUnavailableException(
                "Search service temporarily unavailable", ex);
        }
        catch (TaskCanceledException ex)
        {
            // IMP:9 — Cancellation by caller or other timeout.
            _logger.LogWarning("[IMP:9][SearchAsync][UNAVAILABLE] Request cancelled for query='{Query}': {Message}",
                request.Query, ex.Message);
            _breaker.RecordFailure();

            throw new SearXNGUnavailableException(
                "Search service temporarily unavailable", ex);
        }
        catch (HttpRequestException ex)
        {
            // IMP:9 — Network error or non-success HTTP status without explicit Timeout.
            _logger.LogWarning("[IMP:9][SearchAsync][UNAVAILABLE] HTTP request failed for query='{Query}': {Message}",
                request.Query, ex.Message);
            _breaker.RecordFailure();

            throw new SearXNGUnavailableException(
                "Search service temporarily unavailable", ex);
        }
        catch (JsonException ex)
        {
            // IMP:9 — Failed to deserialize SearXNG response.
            _logger.LogWarning("[IMP:9][SearchAsync][UNAVAILABLE] JSON deserialization failed for query='{Query}': {Message}",
                request.Query, ex.Message);
            _breaker.RecordFailure();

            throw new SearXNGUnavailableException(
                "Search service temporarily unavailable", ex);
        }
    }
    #endregion METHOD_SearchAsync

    #region PRIVATE_METHOD_BuildUrl [DOMAIN(HTTP): URL builder helper]
    /// <summary>
    /// [PURPOSE]: Constructs the SearXNG search API URL from a SearchRequest.
    /// Base: {BaseUrl}/search?format=json (required).
    /// Required params: q=Uri.EscapeDataString(Query), categories=default "general".
    /// Optional params: language, time_range — added only if non-empty/non-null.
    /// </summary>
    /// <param name="request">Search request parameters.</param>
    /// <returns>Fully-qualified URL for the SearXNG HTTP GET request.</returns>
    private string BuildUrl(SearchRequest request)
    {
        var baseUrl = _settings.BaseUrl.TrimEnd('/');
        var categories = string.IsNullOrWhiteSpace(request.Categories) ? "general" : request.Categories;

        // Manual query-string building using Uri.EscapeDataString for safety (no external dependency).
        // NOTE: No explicit engines= filter here. SearXNG selects the engines per category from its
        // settings.yml (bing is enabled for general/web; it uses stackoverflow/github/mdn, etc.).
        // Hard-coding engines=bing caused categories=general&engines=bing to return 0 results.
        var parts = new[]
        {
            $"q={Uri.EscapeDataString(request.Query)}",
            $"categories={Uri.EscapeDataString(categories)}",
            "format=json",
        };

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            parts = parts.Concat(new[] { $"language={Uri.EscapeDataString(request.Language)}" }).ToArray();
        }

        if (request.TimeRange != null)
        {
            parts = parts.Concat(new[] { $"time_range={Uri.EscapeDataString(request.TimeRange!)}" }).ToArray();
        }

        return $"{baseUrl}/search?{string.Join("&", parts)}";
    }
    #endregion PRIVATE_METHOD_BuildUrl

    #region HELPER_GetBreakerStateString [DOMAIN(HTTP): Diagnostic helper]
    /// <summary>
    /// [PURPOSE]: Returns a string representation of the current circuit breaker state for logging.
    /// </summary>
    private static string GetBreakerStateString(CircuitBreakerState breaker)
        => breaker.IsOpen ? "OPEN" : "CLOSED/HALF_OPEN";
    #endregion HELPER_GetBreakerStateString
}
#endregion CLASS_SearXNGClient
