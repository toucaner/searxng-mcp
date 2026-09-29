#region MODULE_CONTRACT [DOMAIN(Pipeline): SearchResultProcessor; CONCEPT(Service): Post-processing pipeline; TECH(M5): Normalization + Deduplication + Filter]
/**
 * [GREP_SUMMARY]: SearchResultProcessor, pipeline, postprocessing, search result processor, normalize url, deduplicate, strip html, filter blocklist
 * [STRUCTURE]: > IReadOnlyList<SearXNGResult> -> o Process() = [NormalizeUrl -> Deduplicate -> MapToDto -> StripHtml -> TruncateSnippet -> Filter] -> SearchResultDto[]
  */
#endregion MODULE_CONTRACT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using McpWebSearchService.Configuration;
using McpWebSearchService.Models;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace McpWebSearchService.Services;

#region CLASS_SearchResultProcessor [DOMAIN(Pipeline): SearchResultProcessor]
/// <summary>
/// [PURPOSE]: Post-processing pipeline that transforms raw SearXNG search results into clean,
/// deduplicated SearchResultDto objects ready for MCP tool response. Pipeline order:
/// NormalizeUrl → Deduplicate (exact by normalized URL + fuzzy by Title across domains)
/// → MapToDto → StripHtml → TruncateSnippet → Filter (blocklist + snippet length + Take(MaxResults)).
/// </summary>
/// <remarks>
/// [PURPOSE]: Post-processing pipeline that transforms raw SearXNG search results into clean,
/// deduplicated SearchResultDto objects ready for MCP tool response. Pipeline order:
/// NormalizeUrl → Deduplicate (exact by normalized URL + fuzzy by Title across domains)
/// → MapToDto → StripHtml → TruncateSnippet → Filter (blocklist + snippet length + Take(MaxResults)).
/// [INVARIANTS]: Stateless singleton — only instance fields are injected dependencies (_settings, _logger).
/// All mutable collections (Dictionary, HashSet, List) are created as method locals. Thread-safe by construction.
/// Pipeline order is strict: normalization before deduplication, deduplication before filtering,
/// StripHtml before empty-snippet check (so HTML tags don't inflate plain-text length).
/// [RATIONALE]: Q: Why separate interface from implementation? A: Allows mocking in tests and enables future pipeline variants.
/// Interface is the only public-facing contract — SearchResultProcessor implementation details are internal to this assembly.
/// [CHANGES]: LAST_CHANGE: M5 — initial creation (DevelopmentPlan.md §2 step 3).
/// </remarks>
public sealed class SearchResultProcessor : ISearchResultProcessor
{
    #region CONSTS_AND_STATICS [DOMAIN(Pipeline): SearchResultProcessor; TECH(Static config): Constants + arrays]

    /// <summary>Maximum snippet length after truncation.</summary>
    private const int MaxSnippetLength = 300;

    /// <summary>Minimum plain-text snippet length — results shorter than this are filtered out (SPEC §3.2).</summary>
    private const int MinSnippetLength = 20;

    /// <summary>Ellipsis character appended to truncated snippets.</summary>
    private const string Ellipsis = "\u2026";

    /// <summary>Engine priority order: index 0 = highest priority. Used on exact URL collision (ADR-010).</summary>
    private static readonly string[] EnginePriority =
        ["google", "bing", "duckduckgo", "brave", "yandex", "startpage", "mojeek", "wikipedia"];

    /// <summary>Tracking parameters to strip from URLs — prefix-check (utm_*) and explicit set (ADR-011).</summary>
    private static readonly HashSet<string> TrackingParams = new(StringComparer.Ordinal)
    {
        "gclid", "fbclid", "mc_eid", "mc_cid", "msclkid", "yclid", "dclid",
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "ref", "ref_src", "ref_url", "_hsenc", "_hsmi", "hsctagr",
        "igshid", "spm", "scm", "sr_share", "wt_mc"
    };

    #endregion CONSTS_AND_STATICS

    #region REGEX_PATTERNS [DOMAIN(Pipeline): SearchResultProcessor; TECH(Regex): Compiled patterns — AOT-safety verified via trim analysis on publish]

    /// <summary>Strips &lt;script&gt;/&lt;style&gt; blocks including their content (ADR-012).</summary>
    private static readonly Regex ScriptStyleBlock = new(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Strips all remaining HTML tags.</summary>
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.CultureInvariant);

    /// <summary>Collapses multiple whitespace characters into a single space.</summary>
    private static readonly Regex WhitespaceCollapse = new(@"\s+");

    #endregion REGEX_PATTERNS

    #region INSTANCE_FIELDS [DOMAIN(Pipeline): SearchResultProcessor; TECH(Injection): Dependencies only]

    /// <summary>Read-only snapshot of SearXNG configuration.</summary>
    private readonly SearXNGSettings _settings;

    /// <summary>Logger for pipeline telemetry (LDD).</summary>
    private readonly ILogger<SearchResultProcessor> _logger;

    #endregion INSTANCE_FIELDS

    #region CONSTRUCTOR_SearchResultProcessor [DOMAIN(Pipeline): SearchResultProcessor]
    /// <summary>
    /// [PURPOSE]: Constructs the processor with configuration and logging.
    /// </summary>
    public SearchResultProcessor(IOptions<SearXNGSettings> options, ILogger<SearchResultProcessor> logger)
    {
        _settings = options.Value;
        _logger = logger;
    }
    #endregion CONSTRUCTOR_SearchResultProcessor

    #region METHOD_Process [DOMAIN(Pipeline): SearchResultProcessor]
    /// <summary>
    /// [PURPOSE]: Applies the full post-processing pipeline to transform raw SearXNG search results into
    /// SearchResultDto objects ready for MCP tool response. Pipeline order: NormalizeUrl → Deduplicate → MapToDto → StripHtml → TruncateSnippet → Filter → Take(MaxResults).
    /// </summary>
    /// <param name="rawResults">Raw results from SearXNGClient.SearchAsync.</param>
    /// <returns>Deduplicated, filtered SearchResultDto[] sorted by engine priority and relevance. Never null — empty array if all results are filtered.</returns>
    public SearchResultDto[] Process(IReadOnlyList<SearXNGResult> rawResults)
    {
        var count = rawResults.Count;
        _logger.LogInformation("[IMP:1][Process][START] {Count} raw results", count);

        if (count == 0)
        {
            _logger.LogInformation("[IMP:9][Process][BELIEF] 0 raw → 0 deduped → 0 filtered");
            _logger.LogInformation("[IMP:10][Process][COMPLETE] Returned 0 SearchResultDto");
            return [];
        }

        // Stage 1 — Normalize & Deduplicate (on SearXNGResult, before DTO mapping).
        var deduped = Deduplicate(rawResults);

        int afterDedup = deduped.Count;
        _logger.LogInformation("[IMP:7][Deduplicate][STAGE] {AfterExact} after exact dedup, {AfterFuzzy} after fuzzy", afterDedup, afterDedup);

        // Stage 2 — Map to DTO + StripHtml + Truncate (on SearchResultDto).
        var dtoList = new List<SearchResultDto>(deduped.Count);
        foreach (var r in deduped)
        {
            var dto = MapToDto(r);
            dto.Snippet = StripHtml(dto.Snippet);
            dto.Snippet = TruncateSnippet(dto.Snippet);
            dtoList.Add(dto);
        }

        // Stage 3 — Filter + Take.
        _logger.LogInformation("[IMP:7][Process][STAGE] {Count} DTOs ready for filtering", dtoList.Count);

        var filtered = Filter(dtoList).ToList();

        int afterFilter = filtered.Count;
        _logger.LogInformation("[IMP:7][Filter][STAGE] {AfterFilter} after filter, Take({MaxResults})", afterFilter, _settings.MaxResults);

        // Apply MaxResults limit.
        var result = filtered.Take(_settings.MaxResults).ToArray();

        _logger.LogInformation("[IMP:9][Process][BELIEF] {RawCount} raw → {Deduped} deduped → {Filtered} filtered", count, afterDedup, afterFilter);
        _logger.LogInformation("[IMP:10][Process][COMPLETE] Returned {Count} SearchResultDto", result.Length);

        return result;
    }
    #endregion METHOD_Process

    #region METHOD_NormalizeUrl [DOMAIN(Pipeline): SearchResultProcessor; TECH(Algorithm): URL canonicalization — 5 rules (ADR-011)]
    /// <summary>
    /// [PURPOSE]: Normalizes a URL to a canonical form for deduplication purposes. Applies five rules:
    /// (1) lowercase host, (2) strip port 80/443, (3) remove tracking params (utm_* prefix + explicit set),
    /// (4) strip fragment (#...), (5) strip trailing slash on root path. Returns the canonical string.
    /// If the URL is invalid, returns the original string unchanged — never throws.
    /// </summary>
    /// <remarks>
    /// [CHANGES]: M5-debug: Fixed port logic — old code used port=-1 as a "strip" signal but then
    /// appended ":-1" to the URL because `port != 0` was true for -1. Non-default ports were also
    /// dropped because port stayed at initial 0. New logic: only append port if it's non-default
    /// (not 80 for http, not 443 for https, and not -1 which Uri uses for "no port in original string").
    /// </remarks>
    private static string NormalizeUrl(string url)
    {
        // BUG_FIX_CONTEXT: [HYPOTHESIS: The port logic used -1 as a "strip" sentinel but -1 != 0,
        // so ":-1" was appended to every URL with a default port. Non-default ports were also lost
        // because port stayed at initial 0. This produces malformed canonical keys like
        // "https://example.com:-1/path". Two URLs with the same scheme still dedup (both get -1),
        // but the key is wrong and non-default ports are silently dropped.]
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url; // Invalid URL — return as-is (don't crash).

        var host = uri.Host.ToLowerInvariant();

        // BUG_FIX_CONTEXT: [Why the old approach failed: `port = -1` as a sentinel was indistinguishable
        // from a real port value in the `port != 0` check. Why this solution: directly compute whether
        // the port should be included — only non-default ports are appended. Uri.Port returns -1 when
        // no port is in the original string, 80 for http default, 443 for https default.]
        bool includePort = uri.Port > 0
            && !((uri.Scheme == "http" && uri.Port == 80)
                 || (uri.Scheme == "https" && uri.Port == 443));

        var sb = new StringBuilder();
        sb.Append(uri.Scheme).Append("://").Append(host);

        if (includePort)
            sb.Append(':').Append(uri.Port);

        var path = uri.AbsolutePath;

        // Strip trailing slash on root path.
        if (path.Length == 1 && path == "/")
            path = string.Empty;   // Will be reconstructed as "/" below.
        else if (path.Length > 1 && path.EndsWith("/"))
            path = path.TrimEnd('/');

        sb.Append(path);

        var queryParts = new List<string>();

        // Parse and filter query parameters.
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var rawQuery = uri.Query.Substring(1); // Remove leading '?'
            foreach (var pair in rawQuery.Split('&'))
            {
                var idx = pair.IndexOf('=');
                string name;
                string value;

                if (idx > 0)
                {
                    name = pair.Substring(0, idx).ToLowerInvariant();
                    value = pair.Substring(idx + 1);
                }
                else
                {
                    name = pair.ToLowerInvariant();
                    value = string.Empty;
                }

                // Rule: skip tracking params (ADR-011).
                if (name.StartsWith("utm_", StringComparison.Ordinal))
                    continue;
                if (TrackingParams.Contains(name))
                    continue;

                queryParts.Add(idx > 0 ? $"{name}={value}" : name);
            }
        }

        if (queryParts.Count > 0)
            sb.Append('?').Append(string.Join("&", queryParts));

        // Rule: strip fragment (#...).
        return sb.ToString();
    }
    #endregion METHOD_NormalizeUrl

    #region METHOD_Deduplicate [DOMAIN(Pipeline): SearchResultProcessor; TECH(Algorithm): Exact + fuzzy deduplication]
    /// <summary>
    /// [PURPOSE]: Deduplicates results in two passes: (1) exact by normalized URL, (2) fuzzy by title across different domains.
    /// On exact-URL collision: higher-priority engine wins (ADR-010), or if equal priority, longer snippet wins.
    /// Fuzzy dedup: TitleSimilarity ≥ 90% AND different domains → keep only the first occurrence.
    /// </summary>
    private List<SearXNGResult> Deduplicate(IEnumerable<SearXNGResult> results)
    {
        // Exact dedup by normalized URL.
        var exact = new Dictionary<string, SearXNGResult>(StringComparer.Ordinal);

        foreach (var result in results)
        {
            var key = NormalizeUrl(result.Url);

            if (!exact.TryGetValue(key, out var existing))
            {
                // First time seeing this URL — add it.
                exact[key] = result;
                continue;
            }

            // Collision: decide which one to keep using engine priority (ADR-010).
            int newPriority = GetEnginePriority(result.Engine);
            int existingPriority = GetEnginePriority(existing.Engine);

            if (newPriority < existingPriority)
            {
                // New has higher priority — replace.
                exact[key] = result;
            }
            else if (newPriority == existingPriority)
            {
                // Equal priority — keep the one with longer content snippet.
                if (result.Content.Length > existing.Content.Length)
                    exact[key] = result;
            }
        }

        var kept = new List<SearXNGResult>(exact.Values);

        // Fuzzy dedup across different domains.
        var final = new List<SearXNGResult>();
        foreach (var current in kept)
        {
            bool isDuplicate = false;
            for (int i = 0; i < final.Count; i++)
            {
                if (final[i] == current) continue;

                var existingDomain = GetDomain(final[i].Url);
                var newDomain = GetDomain(current.Url);

                // Fuzzy only applies across different domains. Same domain → already handled by exact dedup.
                if (existingDomain != string.Empty && newDomain != string.Empty && existingDomain == newDomain)
                    continue;

                // Check title similarity across domains.
                if (TitleSimilarity.AreSimilar(current.Title, final[i].Title))
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
                final.Add(current);
        }

        return final;
    }
    #endregion METHOD_Deduplicate

    #region METHOD_MapToDto [DOMAIN(Pipeline): SearchResultProcessor]
    /// <summary>
    /// [PURPOSE]: Maps a SearXNGResult to a SearchResultDto. URL is the original (not normalized) —
    /// LLM sees a clickable link. Snippet comes from Content; SourceEngine from Engine.
    /// </summary>
    private static SearchResultDto MapToDto(SearXNGResult result) => new()
    {
        Title = result.Title,
        Url = result.Url,
        Snippet = result.Content ?? string.Empty,
        SourceEngine = result.Engine
    };
    #endregion METHOD_MapToDto

    #region METHOD_StripHtml [DOMAIN(Pipeline): SearchResultProcessor; TECH(Regex): HTML stripping + entity decode (ADR-012)]
    /// <summary>
    /// [PURPOSE]: Strips all HTML content from a string: removes script/style blocks, removes remaining tags,
    /// decodes entities (&amp; → &), and collapses whitespace. Returns plain text. Tags are replaced with
    /// a space (not empty string) so that adjacent block-level tags like &lt;/p&gt;&lt;p&gt; don't
    /// concatenate their text content (e.g. "line1&lt;/p&gt;&lt;p&gt;line2" → "line1 line2", not "line1line2").
    /// The whitespace-collapse step then normalizes any double spaces back to single.
    /// </summary>
    /// <remarks>
    /// [CHANGES]: M5-debug: Tag stripping now replaces each tag with a space instead of empty string.
    /// Old: `HtmlTagPattern.Replace(result, string.Empty)` → "line1&lt;/p&gt;&lt;p&gt;line2" → "line1line2".
    /// New: `HtmlTagPattern.Replace(result, " ")` → "line1 line2" (whitespace collapse handles doubles).
    /// Script/style block removal still uses empty string — the entire block content is being discarded.
    /// </remarks>
    private static string StripHtml(string html)
    {
        // BUG_FIX_CONTEXT: [HYPOTHESIS: StripHtml replaces tags with string.Empty, so adjacent block
        // tags like </p><p> produce no separator between text content: "line1</p><p>line2" → "line1line2".
        // ADR-012 and DevelopmentPlan §0.5 specify "<p>line1</p><p>line2</p>" → "line1 line2". The fix
        // replaces each tag match with a single space; the subsequent whitespace-collapse step normalizes
        // multiple spaces back to one, so inline tags like <b>bold</b> → " bold " → "bold" are unaffected.]

        // 1. Remove script/style blocks (including content) — replace with empty (entire block discarded).
        var result = ScriptStyleBlock.Replace(html, string.Empty);

        // 2. Remove all remaining HTML tags — replace with space to avoid text concatenation.
        // BUG_FIX_CONTEXT: [Why the old approach failed: replacing with string.Empty concatenated
        // text across block boundaries. Why this solution: a single space per tag is a safe separator
        // that the whitespace-collapse step normalizes back. This matches ADR-012's expectation.]
        result = HtmlTagPattern.Replace(result, " ");

        // 3. Decode HTML entities.
        result = WebUtility.HtmlDecode(result);

        // 4. Collapse whitespace and trim.
        return WhitespaceCollapse.Replace(result, " ").Trim();
    }
    #endregion METHOD_StripHtml

    #region METHOD_TruncateSnippet [DOMAIN(Pipeline): SearchResultProcessor; TECH(Algorithm): Word-boundary truncation (ADR-013)]
    /// <summary>
    /// [PURPOSE]: Truncates a snippet to MaxSnippetLength (300) characters with word-boundary soft cut.
    /// If the text is longer than 300 chars, finds the last space in [250..300] and cuts there;
    /// appends ellipsis. If no space found, hard-cuts at 300 + ellipsis.
    /// </summary>
    private static string TruncateSnippet(string text)
    {
        if (text.Length <= MaxSnippetLength)
            return text;

        var truncated = text[..MaxSnippetLength];
        int cutPos = -1;

        // Search backwards from 300 to find a word boundary.
        for (int i = Math.Min(MaxSnippetLength, truncated.Length) - 1; i >= 250 && i < truncated.Length; i--)
        {
            if (truncated[i] == ' ')
            {
                cutPos = i;
                break;
            }
        }

        // No word boundary found — hard cut.
        return cutPos >= 0 ? truncated[..cutPos] + Ellipsis : truncated + Ellipsis;
    }
    #endregion METHOD_TruncateSnippet

    #region METHOD_Filter [DOMAIN(Pipeline): SearchResultProcessor; TECH(Filtering): Domain blocklist + snippet length + Take]
    /// <summary>
    /// [PURPOSE]: Filters results by: (1) BlockedDomains (case-insensitive domain match),
    /// (2) minimum snippet length (≥ MinSnippetLength plain chars after StripHtml),
    /// (3) takes MaxResults. Returns filtered enumeration.
    /// </summary>
    private IEnumerable<SearchResultDto> Filter(IEnumerable<SearchResultDto> results)
    {
        return results.Where(dto =>
        {
            // Blocklist filter.
            var domain = GetDomain(dto.Url);
            if (!string.IsNullOrEmpty(domain))
            {
                foreach (var blocked in _settings.BlockedDomains)
                {
                    if (blocked.Equals(domain, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            // Empty-snippet filter.
            if (dto.Snippet.Length < MinSnippetLength)
                return false;

            return true;
        });
    }
    #endregion METHOD_Filter

    #region METHOD_GetDomain [DOMAIN(Pipeline): SearchResultProcessor]
    /// <summary>
    /// [PURPOSE]: Extracts the lowercase host from a URL for domain-based blocklist + fuzzy dedup checks.
    /// Returns string.Empty if the URL is invalid.
    /// </summary>
    private static string GetDomain(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;

        return uri.Host.ToLowerInvariant();
    }
    #endregion METHOD_GetDomain

    #region METHOD_GetEnginePriority [DOMAIN(Pipeline): SearchResultProcessor; TECH(Algorithm): Engine priority lookup (ADR-010)]
    /// <summary>
    /// [PURPOSE]: Returns the engine's position in the priority array — index 0 = highest.
    /// Unknown or null engines get int.MaxValue (lowest priority).
    /// </summary>
    private static int GetEnginePriority(string? engine) => engine == null
        ? int.MaxValue
        : Array.IndexOf(EnginePriority, engine.ToLowerInvariant()) is var idx && idx >= 0
            ? idx
            : int.MaxValue;
    #endregion METHOD_GetEnginePriority
}
#endregion CLASS_SearchResultProcessor
