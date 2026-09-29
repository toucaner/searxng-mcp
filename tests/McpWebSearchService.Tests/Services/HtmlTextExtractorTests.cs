#region MODULE_CONTRACT [DOMAIN(Test): HtmlTextExtractor; CONCEPT(Unit Tests): Pure function tests with [Theory] tabular cases]
/**
 * [GREP_SUMMARY]: HtmlTextExtractorTests, xunit, HTML stripping, script/style removal, entity decode, whitespace collapse, truncation
 * [STRUCTURE]: > [Theory] cases -> o ExtractPlainText(html, maxLength) -> = Assert(expected output) | Script/Style blocks removed first
 *
 * <summary>
 * [PURPOSE]: Unit tests for Services.HtmlTextExtractor (M6). Covers HTML tag stripping, script/style block removal,
 * entity decoding, whitespace collapsing, truncation with word-boundary, and edge cases (empty input, short HTML).
 * Uses [Theory] + InlineData for tabular test case specification.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Each test is independent — ExtractPlainText is a pure function of its inputs.
 * No shared mutable state between tests. All cases are explicit [InlineData]/[Theory].
 * [RATIONALE]: Q: Why no Moq? A: HtmlTextExtractor has zero dependencies (static class, BCL-only) —
 * direct calls with string input/output assertions suffice. No need for DI or mocking infrastructure.
 * [CHANGES]: LAST_CHANGE: M6 — initial creation (DevelopmentPlan.md §2 step 5).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Net;
using McpWebSearchService.Services;

namespace McpWebSearchService.Tests.Services;

#region CLASS_HtmlTextExtractorTests [DOMAIN(Test): HtmlTextExtractor unit tests]
/// <summary>
/// [PURPOSE]: Unit tests for the static HtmlTextExtractor utility class.
/// </summary>
public class HtmlTextExtractorTests
{
    #region TEST_METHOD_TagStripping [IMP:7-8][HtmlTextExtractorTests][ExtractPlainText] HTML tag stripping
    /// <summary>
    /// Verifies that basic HTML tags are stripped, preserving only the text content.
    /// </summary>
    [Theory]
    [InlineData("<b>bold</b>", "bold")]
    [InlineData("<i>italic</i>", "italic")]
    [InlineData("<p>nested <strong>text</strong></p>", "nested text")]
    [InlineData("plain text", "plain text")] // no-op case
    [InlineData("<h1>Title</h1>", "Title")]
    public void ExtractPlainText_StripsTags(string html, string expected)
    {
        var result = HtmlTextExtractor.ExtractPlainText(html);

        Assert.Equal(expected, result);
    }
    #endregion TEST_METHOD_TagStripping

    #region TEST_METHOD_ScriptRemoval [IMP:7-8][HtmlTextExtractorTests][ExtractPlainText] Script block removal
    /// <summary>
    /// Verifies that &lt;script&gt; blocks (including their content) are removed before tag stripping.
    /// </summary>
    [Fact]
    public void ExtractPlainText_RemovesScriptBlocks()
    {
        var html = "<p>before</p><script>alert('xss');</script><p>after</p>";
        var result = HtmlTextExtractor.ExtractPlainText(html);

        Assert.DoesNotContain("alert", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before", result);
        Assert.Contains("after", result);
    }
    #endregion TEST_METHOD_ScriptRemoval

    #region TEST_METHOD_StyleBlockRemoval [IMP:7-8][HtmlTextExtractorTests][ExtractPlainText] Style block removal
    /// <summary>
    /// Verifies that &lt;style&gt; blocks (including their content) are removed before tag stripping.
    /// </summary>
    [Fact]
    public void ExtractPlainText_RemovesStyleBlocks()
    {
        var html = "<p>content</p><style>.class{display:none}</style>";
        var result = HtmlTextExtractor.ExtractPlainText(html);

        Assert.DoesNotContain("display", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content", result);
    }
    #endregion TEST_METHOD_StyleBlockRemoval

    #region TEST_METHOD_EntityDecoding [IMP:7-8][HtmlTextExtractorTests][ExtractPlainText] Entity decoding
    /// <summary>
    /// Verifies that HTML entities are decoded to their character equivalents.
    /// </summary>
    [Theory]
    [InlineData("a &amp; b", "a & b")]
    [InlineData("&lt;tag&gt;", "<tag>")]
    [InlineData("&quot;quoted&quot;", "\"quoted\"")]
    [InlineData("5 &gt; 3", "5 > 3")]
    public void ExtractPlainText_DecodesEntities(string html, string expected)
    {
        var result = HtmlTextExtractor.ExtractPlainText(html);

        Assert.Equal(expected, result);
    }
    #endregion TEST_METHOD_EntityDecoding

    #region TEST_METHOD_WhitespaceCollapse [IMP:7-8][HtmlTextExtractorTests][ExtractPlainText] Whitespace collapsing
    /// <summary>
    /// Verifies that multiple consecutive whitespace characters are collapsed to a single space.
    /// </summary>
    [Theory]
    [InlineData("<p>line1</p><p>line2</p>", "line1 line2")]
    [InlineData("  lots   of    spaces  ", "lots of spaces")]
    [InlineData("<div>\n\t\n</div>", "")] // tags only, collapsed whitespace = empty
    public void ExtractPlainText_CollapsesWhitespace(string html, string expected)
    {
        var result = HtmlTextExtractor.ExtractPlainText(html);

        Assert.Equal(expected, result);
    }
    #endregion TEST_METHOD_WhitespaceCollapse

    #region TEST_METHOD_Truncation [IMP:7-8][HtmlTextExtractorTests][ExtractPlainText] Truncation with word-boundary and maxLength
    /// <summary>
    /// Verifies truncation behavior: output is at most maxLength + 4 chars (for ellipsis), cuts at last space.
    /// </summary>
    [Fact]
    public void ExtractPlainText_TruncatesToMaxLength()
    {
        const int maxLen = 100;
        var words = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"word{i}"));
        var html = $"<p>{words}</p>";

        var result = HtmlTextExtractor.ExtractPlainText(html, maxLen);

        Assert.True(result.Length <= maxLen + 1, $"Truncated output ({result.Length}) exceeds maxLength+ellipsis");
    }

    [Fact]
    public void ExtractPlainText_TruncatesWithWordBoundary()
    {
        const int maxLen = 40;
        // After tag stripping: "short word longlonglonglonglongword at end" (43 chars) > 40.
        var html = "<p>short word longlonglonglonglongword at end</p>";

        var result = HtmlTextExtractor.ExtractPlainText(html, maxLen);

        Assert.Contains("short", result);
        Assert.EndsWith("…", result);
    }
    #endregion TEST_METHOD_Truncation

    #region TEST_METHOD_EmptyInput [IMP:7-8][HtmlTextExtractorTests][ExtractPlainText] Empty and minimal input handling
    /// <summary>
    /// Verifies empty/whitespace-only inputs return expected values without throwing.
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")] // whitespace-only → collapsed+trimmed = empty
    [InlineData("<b></b>", "")] // tags only, no text content
    public void ExtractPlainText_HandlesEmptyInput(string html, string expected)
    {
        var result = HtmlTextExtractor.ExtractPlainText(html);

        Assert.Equal(expected, result);
    }
    #endregion TEST_METHOD_EmptyInput

    #region TEST_METHOD_NonHtmlInput [IMP:7-8][HtmlTextExtractorTests][ExtractPlainText] Plain text passthrough
    /// <summary>
    /// Verifies that plain text (no HTML) is returned unchanged except for entity decoding.
    /// </summary>
    [Theory]
    [InlineData("just a sentence", "just a sentence")]
    [InlineData("with &amp; entities only", "with & entities only")]
    public void ExtractPlainText_PassThroughPlainHtml(string html, string expected)
    {
        var result = HtmlTextExtractor.ExtractPlainText(html);

        Assert.Equal(expected, result);
    }
    #endregion TEST_METHOD_NonHtmlInput
}
#endregion CLASS_HtmlTextExtractorTests
