#region MODULE_CONTRACT [DOMAIN(Tools): MCP web search; CONCEPT(WebSearchTools): SearXNG proxy + fetch_and_extract]
/**
 * [GREP_SUMMARY]: WebSearchTools, MCP tool, web_search, fetch_and_extract, SearchResultDto, ADR-018, ADR-022, pre-serialized JSON, HtmlTextExtractor
 * [STRUCTURE]: > DI[ISearXNGClient, ISearchResultProcessor, IOptions<SearXNGSettings>, ILogger<WebSearchTools>, IHttpClientFactory] -> o WebSearchTools {
 *               web_search(query, categories?, timeRange?, language?) -> string (JSON SearchResultDto[]) |
 *               fetch_and_extract(url) -> string (plain text <=5000 chars) } -> + McpJsonContext.Default.SearchResultDtoArray
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Services;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using ModelContextProtocol.Server;
using System.Text.Json;
using System.Web;
using McpWebSearchService.Configuration;
using McpWebSearchService.Models;
using McpWebSearchService.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#region CLASS_WebSearchTools [DOMAIN(Tools): MCP tool for web_search + fetch_and_extract]
/// <summary>
/// [PURPOSE]: Implements the two MCP tools defined in SPEC §2 — <c>web_search</c> (proxies queries to SearXNG with
/// post-processing) and <c>fetch_and_extract</c> (loads a URL and returns cleaned plain text).
/// </summary>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
/// <remarks>
/// [PURPOSE]: See above.
/// [INVARIANTS]: 
///   - <c>web_search</c> always returns valid JSON array string (possibly empty "<code>[]</code>") or error message.
///   - <c>fetch_and_extract</c> never throws — all errors are returned as user-friendly strings.
///   - Snippets in search results are ≤300 chars, plain text (HTML stripped), truncated with ellipsis.
/// [RATIONALE]: 
///   ADR-018: Return <c>Task&lt;string&gt;</c> — pre-serialized JSON via <c>McpJsonContext.Default.SearchResultDtoArray</c>.
///   This avoids reflection-based serialization at the MCP boundary, which would emit IL2026/IL3050 under Native AOT.
///   ADR-019: Tool names derived by SDK SnakeCaseLower from method names (<c>WebSearch → web_search</c>).
///   ADR-020: Parameter names use C# idiomatic casing (camelCase, not snake_case).
///   ADR-021: <c>language == null</c> falls back to <c>_settings.DefaultLanguage</c>.
    ///   ADR-022: Named HttpClient "FetchExtract" for fetch_and_extract — separate from SearXNG client.
    ///   ADR-028: [Description] attributes on tool methods so MCP tools/list returns non-empty descriptions.
    ///   ADR-032 (M9): when the agent omits "categories", the category is inferred semantically via the local LLM
    ///   (SearchCategoryClassifier → ICategoryInferenceClient), falling back to "general" on LLM unavailability.
    /// [CHANGES]: LAST_CHANGE: M9 — semantic category inference (ADR-032); categories default → null.
/// </remarks>
[McpServerToolType]
public sealed class WebSearchTools(
    ISearXNGClient client,
    ISearchResultProcessor processor,
    IOptions<SearXNGSettings> options,
    ILogger<WebSearchTools> logger,
    IHttpClientFactory httpClientFactory,
    ICategoryInferenceClient categoryInferenceClient,
    IOptions<CategoryInferenceSettings> categoryOptions)
{
private readonly SearXNGSettings _settings = options.Value!; // DI guarantees non-null (ADR-006).
private readonly ICategoryInferenceClient _categoryInferenceClient = categoryInferenceClient; // M9: semantic category inference (ADR-032).
private readonly CategoryInferenceSettings _categorySettings = categoryOptions.Value!; // DI guarantees non-null (ADR-006).

    #region METHOD_WebSearch [PURPOSE: MCP "web_search" tool — proxy query to SearXNG + post-process]
    /// <summary>
    /// [PURPOSE]: Executes a web search by forwarding the request to the configured SearXNG instance,
    /// applies post-processing (URL normalization → deduplication → HTML stripping → truncation → filtering),
    /// and returns the results as a pre-serialized JSON array string.
    /// </summary>
    /// <param name="query">The search query text. Required — validated on entry.</param>
    /// <param name="categories">Explicit search category filter (e.g., "news", "images", "it"). When omitted (null),
    /// the category is inferred semantically from the query via the local LLM (M9, ADR-032), falling back to "general".</param>
    /// <param name="timeRange">Time range filter ("day", "week", "month", "year") or null for no filter. Falls back to config default.</param>
    /// <param name="language">Language token (e.g., "ru", "en") or null — falls back to <c>SearXNGSettings.DefaultLanguage</c>.</param>
    /// <returns>A JSON array string of <c>SearchResultDto</c>, empty array on no results, or an error message.</returns>
    /// <remarks>
    /// [PURPOSE]: See summary.
    /// [INVARIANTS]: Returns non-null string in all paths (empty "[]" for zero hits, error for service down).
   /// [RATIONALE]: ADR-018: pre-serialized via McpJsonContext.Default.SearchResultDtoArray to stay AOT-safe.
        /// </remarks>
        [Description("Searches the web for current information using SearXNG. Use this when you need data not in your knowledge base or need to check the latest news. Supports filtering by category, time range, and language.")]
        [McpServerTool]
        public async Task<string> WebSearch(
        string query,
        [Description("Search category filter (e.g., \"news\", \"images\", \"it\"). When omitted, the category is inferred semantically from the query via a local LLM (falls back to \"general\").")] string? categories = null,
        [Description("Time range filter: \"day\", \"week\", \"month\", or \"year\". Null = no filter (uses SearXNG default).")] string? timeRange = null,
        [Description("Language token for the search query (e.g., \"ru\", \"en\"). Null falls back to the configured default language.")] string? language = null)
    {
        // Validate required parameter — MCP SDK guarantees non-null, but be defensive.
        if (string.IsNullOrWhiteSpace(query))
        {
            logger.LogWarning("[IMP:2] WebSearch[EmptyQuery]: query is empty or whitespace");
            return JsonSerializer.Serialize("Error: the 'query' parameter is required and cannot be empty.", McpJsonContext.Default.String);
        }

        logger.LogInformation("[IMP:3] WebSearch[Start]: query=\"{Query}\", explicitCategories={Categories}, timeRange={TimeRange}", query, categories, timeRange);

        // ADR-021: language fallback to config default when null or whitespace.
        var lang = string.IsNullOrWhiteSpace(language) ? _settings.DefaultLanguage : language;
        logger.LogInformation("[IMP:4] WebSearch[Lang]: effective_lang=\"{Lang}\"", lang);

        // M9 (ADR-032): resolve the effective category. Explicit agent category wins unconditionally;
        // otherwise the category is inferred semantically via the local LLM, falling back to "general".
        // An explicit category short-circuits without any LLM latency.
        var category = await SearchCategoryClassifier.InferCategoryAsync(
            query, categories, _categoryInferenceClient, _categorySettings.EnableFallback, CancellationToken.None);
        logger.LogInformation("[IMP:4][5] WebSearch[Category]: effective_category=\"{Category}\" (explicit={IsExplicit})",
            category, !string.IsNullOrWhiteSpace(categories));

        var request = new SearchRequest
        {
            Query = query,
            Categories = category,
            TimeRange = timeRange,
            Language = string.IsNullOrWhiteSpace(lang) ? null : lang
        };

        logger.LogInformation("[IMP:5] WebSearch[Request]: sending to SearXNG");

        IReadOnlyList<SearXNGResult> rawResults;
        try
        {
            // AOT-safe exception type (no reflection on the exception itself — it's a regular sealed class).
#pragma warning disable IL3050
            rawResults = await client.SearchAsync(request);
#pragma warning restore IL3050

            logger.LogInformation("[IMP:6] WebSearch[Response]: got {Count} results from SearXNG", rawResults.Count);
        }
        catch (SearXNGUnavailableException ex)
        {
            // ADR-018: return the error message as a JSON string — not an exception.
            logger.LogWarning(ex, "[IMP:7] WebSearch[SearXNGUnavailable]: circuit breaker open or request failed");
            var error = "Search service temporarily unavailable";
            return JsonSerializer.Serialize(error, McpJsonContext.Default.String);
        }

        // Post-processing pipeline (M4/M5): NormalizeUrl → Deduplicate → MapToDto → StripHtml → Truncate → Filter.
        SearchResultDto[] processedResults = processor.Process(rawResults.ToArray());
        logger.LogInformation("[IMP:8] WebSearch[Processed]: {Count} results after post-processing", processedResults.Length);

        if (processedResults.Length == 0)
        {
            logger.LogInformation("[IMP:9] WebSearch[Empty]: no results after post-processing");
            return "[]";
        }

        // ADR-018: pre-serialize via JsonSerializerContext to stay AOT-safe.
        var json = JsonSerializer.Serialize(processedResults, McpJsonContext.Default.SearchResultDtoArray);
        logger.LogInformation("[IMP:9][10] WebSearch[DONE]: serialized {Count} SearchResultDto entries", processedResults.Length);

        return json;
    }
    #endregion METHOD_WebSearch

    #region METHOD_FetchAndExtract [PURPOSE: MCP "fetch_and_extract" tool — load URL, strip HTML, return text ≤5000]
    /// <summary>
    /// [PURPOSE]: Fetches an arbitrary HTTP URL and returns its content as cleaned plain text (≤5000 chars).
    /// Strips HTML tags, script/style blocks, decodes entities, collapses whitespace.
    /// </summary>
    /// <param name="url">The absolute URI to fetch.</param>
    /// <returns>Cleaned plain text ≤5000 characters, or an error message string on failure (never throws).</returns>
    /// <remarks>
    /// [PURPOSE]: See summary.
    /// [INVARIANTS]: Never throws — all errors returned as user-friendly strings. Output ≤5000 chars with word-boundary truncation + ellipsis.
  /// [RATIONALE]: ADR-022: Uses named HttpClient "FetchExtract" (not SearXNG client) so it has 15s timeout and no Circuit Breaker for arbitrary URLs.
    /// [CHANGES]: M8 — [Description] per ADR-028.
        /// </remarks>
        [Description("Fetches a web page by URL and returns cleaned plain text (removes HTML tags, scripts, entities). Use this when the search snippet is insufficient to fully understand the page content. Returns up to 5000 characters.")]
        [McpServerTool]
        public async Task<string> FetchAndExtract(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Error: the 'url' parameter is required and cannot be empty.";
        }

        // Validate that the string is a valid absolute URI — prevents HttpClient from throwing.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http", StringComparison.Ordinal))
        {
            logger.LogWarning("[IMP:3] FetchAndExtract[InvalidUrl]: url=\"{Url}\" not a valid HTTP(S) URI", url);
            return $"Error: invalid URL — \"{url}\".";
        }

        // ADR-022: named HttpClient "FetchExtract" with 15s timeout.
        var client = httpClientFactory.CreateClient("FetchExtract");
        logger.LogInformation("[IMP:4] FetchAndExtract[Start]: url=\"{Url}\"", uri);

        try
        {
            // AOT-safe: HttpResponseMessage + ReadAsByteArrayAsync are both reflection-free (verified in M4).
#pragma warning disable IL3050
            var response = await client.GetAsync(uri, CancellationToken.None);
#pragma warning restore IL3050

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("[IMP:6] FetchAndExtract[HttpError]: status={StatusCode}", response.StatusCode);
                return $"HTTP error {response.StatusCode} while fetching \"{url}\".";
            }

            var content = await response.Content.ReadAsStringAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            // Only process HTML-like responses — text/plain is fine to return as-is (trimmed).
            if (!string.IsNullOrEmpty(contentType) && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("[IMP:7] FetchAndExtract[NonText]: content-type=\"{ContentType}\"", contentType);
                return $"Only HTML and text content are supported (received \"{contentType}\").";
            }

            var text = HtmlTextExtractor.ExtractPlainText(content, maxLength: 5000);
            logger.LogInformation("[IMP:8][9] FetchAndExtract[DONE]: {Chars} chars extracted from \"{Url}\"", text.Length, uri);

            return text;
        }
        catch (OperationCanceledException) when (CancellationToken.None.IsCancellationRequested)
        {
            // CancellationToken.None should never fire — this is unreachable.
            logger.LogError("[IMP:6] FetchAndExtract[Cancelled]: cancellation received unexpectedly");
            return "Error: the request was cancelled.";
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "[IMP:6] FetchAndExtract[HttpError]: {Message}", ex.Message);
            return $"HTTP error while fetching \"{url}\" — {ex.Message}.";
        }
        catch (TaskCanceledException ex) when (!CancellationToken.None.IsCancellationRequested)
        {
            logger.LogWarning(ex, "[IMP:6] FetchAndExtract[Timeout]: url=\"{Url}\"", uri);
            return $"Error: fetching \"{url}\" did not complete within 15 seconds.";
        }
    }
    #endregion METHOD_FetchAndExtract
}
#endregion CLASS_WebSearchTools
