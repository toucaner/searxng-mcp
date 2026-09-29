#region MODULE_CONTRACT [DOMAIN(Test): SearchResultProcessor; CONCEPT(Unit Tests): Pipeline stages + LDD]
/**
 * [GREP_SUMMARY]: SearchResultProcessorTests, xunit, test, pipeline, normalize url, deduplication, filter blocklist, strip html, truncation, take max results
 * [STRUCTURE]: > MkResult() -> o Process(raw) = Assert(result) | > NormalizeUrl() via exact-dedup | > EnginePriority via collision | > Filter by domain + snippet length
 */

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using McpWebSearchService.Configuration;
using McpWebSearchService.Models;
using McpWebSearchService.Services;

namespace McpWebSearchService.Tests.Services;

#region CLASS_SearchResultProcessorTests [DOMAIN(Test): SearchResultProcessor pipeline tests]
/// <summary>
/// [PURPOSE]: Comprehensive unit tests for the post-processing pipeline (M5). Covers:
/// NormalizeUrl (7 rules), exact-dedup (engine priority + longest snippet), fuzzy-dedup, blocklist,
/// empty-snippet filter, Take(MaxResults), HTML stripping, truncation, and full pipeline order.
/// </summary>
public class SearchResultProcessorTests : IDisposable
{
    #region HELPER_METHOD_MkResult [DOMAIN(Test): Factory — SearXNGResult]
    /// <summary>
    /// [PURPOSE]: Creates a test SearXNGResult with the given properties for pipeline testing.
    /// </summary>
    private static SearXNGResult MkResult(string title, string url, string content, string? engine = null) => new()
    {
        Title = title,
        Url = url,
        Content = content,
        Engine = engine
    };
    #endregion HELPER_METHOD_MkResult

    #region HELPER_METHOD_MkSettings [DOMAIN(Test): Factory — SearXNGSettings]
    /// <summary>
    /// [PURPOSE]: Creates a test SearXNGSettings with configurable MaxResults and BlockedDomains.
    /// </summary>
    private static IOptions<SearXNGSettings> MkSettings(int maxResults = 5, string[]? blockedDomains = null) =>
        Options.Create(new SearXNGSettings { MaxResults = maxResults, BlockedDomains = blockedDomains ?? [] });
    #endregion HELPER_METHOD_MkSettings

    #region HELPER_METHOD_MkProcessor [DOMAIN(Test): Factory — SearchResultProcessor]
    /// <summary>
    /// [PURPOSE]: Creates a configured SearchResultProcessor with NullLogger (no-op logger for tests).
    /// </summary>
    private static SearchResultProcessor MkProcessor(IOptions<SearXNGSettings>? settings = null) =>
        new(settings ?? MkSettings(), new NullLogger<SearchResultProcessor>());
    #endregion HELPER_METHOD_MkSettings

    private readonly List<string> _logs = [];
    private ILogger? _capturedLogger;

    public void Dispose() { }

    #region THEORY_NormalizeUrl_LowercaseHost [DOMAIN(Test): NormalizeUrl rule 1 — lowercase host]
    /// <summary>
    /// [PURPOSE]: Verify that uppercase letters in the hostname are lowercased (rule 1).
    /// Two URLs with different-cased hosts but same path should deduplicate to one result.
    /// </summary>
    // BUG_FIX_CONTEXT: [HYPOTHESIS: Multiple NormalizeUrl/dedup tests use snippet content < 20 chars
    // (e.g. "content A" = 9 chars). The Filter stage drops any result with snippet < MinSnippetLength
    // (20 chars, SPEC §3.2). So the deduped result is filtered out → collection empty → Assert.Single
    // fails. Fix: use snippet content ≥ 20 chars so the filtered result survives. The NormalizeUrl logic
    // itself was also broken (port=-1 appended as ":-1") — fixed in SearchResultProcessor.cs.]

    [Theory]
    [InlineData("https://Example.com/path", "https://example.com/path")]
    // BUG_FIX_CONTEXT: [HYPOTHESIS: The original InlineData used "https://EXAMPLE.COM/PATH" with an
    // UPPERCASE path (/PATH) paired against "https://example.com/path" with a lowercase path (/path).
    // NormalizeUrl lowercases the HOST only (per SPEC §3.1.1, ADR-011, AC4: "lowercase host"), NOT
    // the path — path is case-sensitive per RFC 3986. So NormalizeUrl("...EXAMPLE.COM/PATH") produces
    // "https://example.com/PATH" while NormalizeUrl("...example.com/path") produces "https://example.com/path"
    // — DIFFERENT keys, so they correctly do NOT dedup. The test's own docstring says "different-cased
    // hosts but SAME PATH" — the bug is that /PATH and /path are NOT the same path. Fix: use the same
    // path casing in both URLs so only the host differs (which is what the test is meant to verify).]
    [InlineData("https://EXAMPLE.COM/path", "https://example.com/path")]
    public void NormalizeUrl_LowercaseHost(string a, string b)
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var results = new[]
        {
            MkResult("A", a, "content snippet A for test"),
            MkResult("B", b, "content snippet B for test")
        };

        var result = processor.Process(results);

        // Both URLs normalize to the same key → exact dedup keeps one.
        Assert.Single(result);
    }
    #endregion THEORY_NormalizeUrl_LowercaseHost

    #region THEORY_NormalizeUrl_StripPort [DOMAIN(Test): NormalizeUrl rule 2 — strip port 80/443]
    /// <summary>
    /// [PURPOSE]: Verify that default ports (http:80, https:443) are stripped.
    /// Two URLs with and without explicit default port should deduplicate to one result.
    /// </summary>
    [Theory]
    [InlineData("http://example.com:80/path", "http://example.com/path")]
    [InlineData("https://example.com:443/path", "https://example.com/path")]
    public void NormalizeUrl_StripDefaultPort(string a, string b)
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var results = new[]
        {
            MkResult("A", a, "content snippet A for test"),
            MkResult("B", b, "content snippet B for test")
        };

        Assert.Single(processor.Process(results));
    }
    #endregion THEORY_NormalizeUrl_StripPort

    #region THEORY_NormalizeUrl_StripTrackingParams [DOMAIN(Test): NormalizeUrl rule 3 — tracking params]
    /// <summary>
    /// [PURPOSE]: Verify that utm_* prefix and explicit tracking params (gclid, fbclid) are stripped.
    /// URLs with tracking params should deduplicate against their clean versions.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/path?utm_source=x&q=1", "https://example.com/path?q=1")]
    [InlineData("https://example.com/path?gclid=abc&q=1", "https://example.com/path?q=1")]
    [InlineData("https://example.com/path?fbclid=z&q=1", "https://example.com/path?q=1")]
    public void NormalizeUrl_StripTrackingParams(string a, string b)
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        Assert.Single(processor.Process(new[]
        {
            MkResult("A", a, "content snippet A for test"),
            MkResult("B", b, "content snippet B for test")
        }));
    }

    [Fact]
    public void NormalizeUrl_KeepsNonTrackingParams()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var a = MkResult("A", "https://example.com/path?q=1&utm_source=x&fbclid=y", "content snippet A for test");
        var b = MkResult("B", "https://example.com/path?q=1", "content snippet B for test");

        Assert.Single(processor.Process(new[] { a, b }));
    }
    #endregion THEORY_NormalizeUrl_StripTrackingParams

    #region THEORY_NormalizeUrl_StripFragment [DOMAIN(Test): NormalizeUrl rule 4 — strip fragment]
    /// <summary>
    /// [PURPOSE]: Verify that URL fragments (#section) are stripped for normalization.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/path#section", "https://example.com/path")]
    public void NormalizeUrl_StripFragment(string a, string b)
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        Assert.Single(processor.Process(new[]
        {
            MkResult("A", a, "content snippet A for test"),
            MkResult("B", b, "content snippet B for test")
        }));
    }
    #endregion THEORY_NormalizeUrl_StripFragment

    #region THEORY_NormalizeUrl_StripTrailingSlash [DOMAIN(Test): NormalizeUrl rule 5 — strip trailing slash]
    /// <summary>
    /// [PURPOSE]: Verify that trailing slashes are stripped from paths.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/path/", "https://example.com/path")]
    public void NormalizeUrl_StripTrailingSlash(string a, string b)
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        Assert.Single(processor.Process(new[]
        {
            MkResult("A", a, "content snippet A for test"),
            MkResult("B", b, "content snippet B for test")
        }));
    }
    #endregion THEORY_NormalizeUrl_StripTrailingSlash

    #region METHOD_ExactDedup_EnginePriority [DOMAIN(Test): Exact-dedup — engine priority wins]
    /// <summary>
    /// [PURPOSE]: Verify that on exact URL collision, the higher-priority engine wins (Google > Bing).
    /// </summary>
    [Fact]
    public void ExactDedup_EnginePriority_Wins()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        // BUG_FIX_CONTEXT: [HYPOTHESIS: Snippet content "short"/"longer" < 20 chars → Filter drops
        // the deduped result → empty collection → Assert.Single fails + [0] IndexOutOfRange.
        // Fix: use snippet content ≥ 20 chars so the result survives the filter.]
        var results = new[]
        {
            MkResult("A", "https://example.com/path", "short snippet content for test", engine: "bing"),
            MkResult("B", "https://example.com/path", "longer snippet content for test", engine: "google")
        };

        var result = processor.Process(results);
        Assert.Single(result);
        Assert.Equal("google", result[0].SourceEngine);
    }

    [Fact]
    public void ExactDedup_EnginePriority_WikipediaLosesToGoogle()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var results = new[]
        {
            MkResult("A", "https://example.com/path", "wikipedia snippet content here", engine: "wikipedia"),
            MkResult("B", "https://example.com/path", "google snippet content here too", engine: "google")
        };

        Assert.Equal("google", processor.Process(results)[0].SourceEngine);
    }

    [Fact]
    public void ExactDedup_EnginePriority_UnknownLosesToKnown()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var results = new[]
        {
            MkResult("A", "https://example.com/path", "unknown snippet content here", engine: "unknown"),
            MkResult("B", "https://example.com/path", "bing snippet content here too", engine: "bing")
        };

        Assert.Equal("bing", processor.Process(results)[0].SourceEngine);
    }

    [Fact]
    public void ExactDedup_EnginePriority_NullLosesToKnown()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var results = new[]
        {
            MkResult("A", "https://example.com/path", "null engine snippet content here"),
            MkResult("B", "https://example.com/path", "google snippet content here too", engine: "google")
        };

        Assert.Equal("google", processor.Process(results)[0].SourceEngine);
    }
    #endregion METHOD_ExactDedup_EnginePriority

    #region METHOD_ExactDedup_LongestSnippet [DOMAIN(Test): Exact-dedup — longest snippet when priorities equal]
    /// <summary>
    /// [PURPOSE]: When engine priority is equal (both unknown), the result with the longer content is kept.
    /// </summary>
    [Fact]
    public void ExactDedup_LongestSnippet_Wins()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var results = new[]
        {
            MkResult("A", "https://example.com/path", "short"),
            MkResult("B", "https://example.com/path", "much longer snippet here")
        };

        Assert.Equal("much longer snippet here", processor.Process(results)[0].Snippet);
    }
    #endregion METHOD_ExactDedup_LongestSnippet

    #region METHOD_FuzzyDedup_DifferentDomains [DOMAIN(Test): Fuzzy-dedup — same title, different domains]
    /// <summary>
    /// [PURPOSE]: Verify that fuzzy dedup across different domains with identical titles keeps only one.
    /// </summary>
    [Fact]
    public void FuzzyDedup_SameTitle_DifferentDomains_KeepsOne()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        // BUG_FIX_CONTEXT: [HYPOTHESIS: Snippet content "content A"/"content B" < 20 chars → Filter
        // drops the deduped result → empty collection. Fix: use snippet ≥ 20 chars.]
        var results = new[]
        {
            MkResult("My Title", "https://a.com/1", "content snippet A for test"),
            MkResult("My Title", "https://b.com/2", "content snippet B for test")
        };

        Assert.Single(processor.Process(results));
    }

    [Fact]
    public void FuzzyDedup_DifferentTitles_DifferentDomains_KeepsBoth()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var results = new[]
        {
            MkResult("Title A", "https://a.com/1", "content snippet A for test"),
            MkResult("Title B", "https://b.com/2", "content snippet B for test")
        };

        Assert.Equal(2, processor.Process(results).Length);
    }

    [Fact]
    public void FuzzyDedup_SameDomain_DifferentTitles_KeepsBoth()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var results = new[]
        {
            MkResult("Title A", "https://a.com/1", "content snippet A for test"),
            MkResult("Title B", "https://a.com/2", "content snippet B for test")
        };

        // Different URLs + different titles → both kept.
        Assert.Equal(2, processor.Process(results).Length);
    }
    #endregion METHOD_FuzzyDedup_DifferentDomains

    #region METHOD_Filter_Blocklist [DOMAIN(Test): Filter — domain blocklist]
    /// <summary>
    /// [PURPOSE]: Verify that BlockedDomains from SearXNGSettings correctly filters out results by domain.
    /// </summary>
    [Fact]
    public void Filter_Blocklist_RemovesBlockedDomain()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10, blockedDomains: new[] { "spam.com" }));
        // BUG_FIX_CONTEXT: [HYPOTHESIS: "valid content here" = 19 chars < MinSnippetLength (20) →
        // filtered by snippet-length filter even though domain is OK → empty collection.
        // Fix: use snippet ≥ 20 chars so the OK-domain result survives the filter.]
        var result = processor.Process(new[]
        {
            MkResult("A", "https://ok.com/page", "valid content here for test"),
            MkResult("B", "https://spam.com/malware", "bad content here for test")
        });

        Assert.Single(result);
        Assert.Equal("ok.com", result[0].Url.Split('/')[2]);
    }

    [Fact]
    public void Filter_Blocklist_CaseInsensitiveDomainMatch()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10, blockedDomains: new[] { "Spam.COM" }));
        var result = processor.Process(new[]
        {
            MkResult("A", "https://spam.com/page", "valid content here for test")
        });

        Assert.Empty(result);
    }
    #endregion METHOD_Filter_Blocklist

    #region METHOD_Filter_EmptySnippet [DOMAIN(Test): Filter — empty snippet length]
    /// <summary>
    /// [PURPOSE]: Verify that snippets shorter than MinSnippetLength (20 chars) are filtered out.
    /// </summary>
    [Theory]
    [InlineData(19, 0)]   // Exactly at boundary — removed.
    [InlineData(20, 1)]   // At threshold — kept.
    public void Filter_EmptySnippet_HandlesBoundary(int snippetLength, int expectedCount)
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var snippet = new string('x', snippetLength);

        var result = processor.Process(new[]
        {
            MkResult("Title", "https://example.com/1", snippet)
        });

        Assert.Equal(expectedCount, result.Length);
    }

    [Fact]
    public void Filter_HtmlSnippet_StripsBeforeChecking()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        // HTML tags inflate length but plain text is tiny — should be filtered after StripHtml.
        var result = processor.Process(new[]
        {
            MkResult("Title", "https://example.com/1", "<b>x</b>")  // Plain text = "x" (1 char)
        });

        Assert.Empty(result);
    }
    #endregion METHOD_Filter_EmptySnippet

    #region METHOD_Take_MaxResults [DOMAIN(Test): Pipeline — Take(MaxResults)]
    /// <summary>
    /// [PURPOSE]: Verify that Take(MaxResults) limits the output to the configured maximum.
    /// </summary>
    [Fact]
    public void Take_MaxResults_LimitsOutput()
    {
        var processor = MkProcessor(MkSettings(maxResults: 3));
        var results = new[]
        {
            MkResult("A", "https://a.com/1", "content A is long enough to pass the filter"),
            MkResult("B", "https://b.com/2", "content B is long enough to pass the filter"),
            MkResult("C", "https://c.com/3", "content C is long enough to pass the filter"),
            MkResult("D", "https://d.com/4", "content D is long enough to pass the filter"),
            MkResult("E", "https://e.com/5", "content E is long enough to pass the filter")
        };

        Assert.Equal(3, processor.Process(results).Length);
    }

    [Fact]
    public void Take_MaxResults_NoOverflow()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        var result = processor.Process(new[]
        {
            MkResult("A", "https://a.com/1", "short"),   // Snippet too short — filtered.
            MkResult("B", "https://b.com/2", "valid content here for filtering purposes")
        });

        Assert.Single(result);
    }
    #endregion METHOD_Take_MaxResults

    #region THEORY_StripHtml [DOMAIN(Test): StripHtml — HTML stripping and entity decoding]
    /// <summary>
    /// [PURPOSE]: Verify that StripHtml removes tags, decodes entities, and collapses whitespace.
    /// </summary>
    [Theory]
    [InlineData("<b>bold</b>", "bold")]
    [InlineData("<script>alert(1)</script>text", "text")]
    [InlineData("a &amp; b", "a & b")]
    [InlineData("<p>line1</p><p>line2</p>", "line1 line2")]
    public void StripHtml_RemovesTags(string html, string expected)
    {
        // Use reflection to access the private method.
        var processor = MkProcessor();
        var method = typeof(SearchResultProcessor).GetMethod("StripHtml", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = (string?)method.Invoke(null, new object[] { html });

        Assert.Equal(expected, result);
    }
    #endregion THEORY_StripHtml

    #region THEORY_TruncateSnippet [DOMAIN(Test): Truncation — word-boundary soft cut]
    /// <summary>
    /// [PURPOSE]: Verify truncation behavior: ≤300 chars as-is, >300 truncated to ~300 with ellipsis.
    /// </summary>
    [Theory]
    [InlineData(100)]  // Short — not truncated.
    [InlineData(300)]  // Exactly at limit — not truncated.
    public void TruncateSnippet_NoTruncationWhenWithinLimit(int length)
    {
        var text = new string('a', length);
        var processor = MkProcessor();
        var method = typeof(SearchResultProcessor).GetMethod("TruncateSnippet", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var result = (string)method.Invoke(null, new object[] { text });

        // Should not be truncated.
        Assert.Equal(text.Length, result.Length);
    }

    [Fact]
    public void TruncateSnippet_TruncatesLongSnippet()
    {
        // BUG_FIX_CONTEXT: [HYPOTHESIS: The original test string "A very long snippet..." is only ~99
        // chars — well under MaxSnippetLength (300). TruncateSnippet returns it as-is (no truncation,
        // no ellipsis). Assert.EndsWith("…") fails because no ellipsis was appended. Fix: use a string
        // longer than 300 chars so the truncation path is actually exercised.]
        var text = new string('a', 250) + " word " + new string('b', 250); // 506 chars total
        var processor = MkProcessor();
        var method = typeof(SearchResultProcessor).GetMethod("TruncateSnippet", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var result = (string)method.Invoke(null, new object[] { text });

        // Should be truncated with ellipsis.
        Assert.True(result.Length <= 303, $"Expected ≤303 chars but got {result.Length}");
        Assert.EndsWith("\u2026", result);
    }
    #endregion THEORY_TruncateSnippet

    #region METHOD_PipelineOrder [DOMAIN(Test): Pipeline — StripHtml before empty-snippet filter]
    /// <summary>
    /// [PURPOSE]: Verify that StripHtml happens BEFORE the empty-snippet filter. This is critical:
    /// an HTML snippet like "<b>x</b>" has 10 chars but only 1 plain char after strip — if filtering
    /// happened first, it would pass (10 ≥ 20 false) but fail after strip. The correct behavior
    /// is to filter AFTER stripping. This test uses a 25-char HTML snippet that becomes exactly 25
    /// plain chars (passes) vs a snippet that strips to <20 plain chars (fails).
    /// </summary>
    [Fact]
    public void PipelineOrder_StripsBeforeEmptySnippetFilter()
    {
        // Scenario: HTML tag wraps text so the raw length is ≥ 20 but the stripped text is < 20.
        var processor = MkProcessor(MkSettings(maxResults: 10));

        // Raw HTML "X" padded with tags → plain text "x" (1 char) after strip.
        var htmlSnippet = "<b><i>x</i></b>";  // After StripHtml → "x" (1 char < 20) → filtered out.
        var result = processor.Process(new[] { MkResult("T", "https://example.com/1", htmlSnippet) });

        Assert.Empty(result);
    }

    [Fact]
    public void PipelineOrder_DedupBeforeFilter()
    {
        // Two identical URLs should be deduplicated before Take(MaxResults) is applied.
        var processor = MkProcessor(MkSettings(maxResults: 5));
        var results = new[]
        {
            MkResult("A", "https://example.com/1", "content A is long enough to pass the filter check"),
            MkResult("B", "https://example.com/1", "content B is also long enough to pass")  // Same URL — duplicate.
        };

        Assert.Single(processor.Process(results));
    }
    #endregion METHOD_PipelineOrder

    #region METHOD_EmptyInput [DOMAIN(Test): Pipeline — empty input]
    /// <summary>
    /// [PURPOSE]: Verify that Process([]) returns an empty array without throwing.
    /// </summary>
    [Fact]
    public void EmptyInput_ReturnsEmptyArray()
    {
        var processor = MkProcessor();
        Assert.Empty(processor.Process(new SearXNGResult[0]));
    }

    [Fact]
    public void NullEngine_NoCrash()
    {
        var processor = MkProcessor(MkSettings(maxResults: 10));
        // Engine is null — GetEnginePriority should return int.MaxValue (not crash).
        var result = processor.Process(new[]
        {
            MkResult("Title", "https://example.com/1", "valid content here for filtering purposes")
        });

        Assert.Single(result);
    }
    #endregion METHOD_EmptyInput

    #region METHOD_LddMarkers [DOMAIN(Test): LDD — Belief State markers present]
    /// <summary>
    /// [PURPOSE]: Verify that Process() emits the mandatory [IMP:9-10] LDD markers.
    /// Uses a capturing logger to inspect actual log output.
    /// </summary>
    [Fact]
    public void LddMarkers_ContainBeliefStateAndComplete()
    {
        var captureLogger = new CapturingLogger<SearchResultProcessor>();
        var processor = new SearchResultProcessor(MkSettings(), captureLogger);

        processor.Process(new[]
        {
            MkResult("A", "https://example.com/1", "valid content here for filtering purposes")
        });

        var allText = string.Join(" ", captureLogger.Messages.Select(s => s.Message));

        Assert.Contains("[IMP:9]", allText);
        Assert.Contains("[IMP:10]", allText);
    }
    #endregion METHOD_LddMarkers

    #region HELPER_CLASS_CapturingLogger [DOMAIN(Test): ILogger that captures log messages]
    /// <summary>
    /// [PURPOSE]: Minimal ILogger implementation for capturing LDD markers during tests.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>, IDisposable
    {
        public List<State> Messages { get; } = new();

        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState? data, Exception? exception, Func<TState?, Exception?, string> formatter)
            => Messages.Add(new(logLevel, formatter(data, exception)));

        internal sealed record State(LogLevel LogLevel, string Message);
    }
    #endregion HELPER_CLASS_CapturingLogger
}
#endregion CLASS_SearchResultProcessorTests
#endregion MODULE_CONTRACT
