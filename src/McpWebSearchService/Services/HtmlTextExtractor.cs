#region MODULE_CONTRACT [DOMAIN(Utility): HtmlTextExtractor; CONCEPT(Service): Plain-text extraction from HTML; TECH(M5/M6): AOT-safe regex-based stripping]
/**
 * [GREP_SUMMARY]: HtmlTextExtractor, plain text extractor, HTML strip, script style block, WebUtility HtmlDecode, GeneratedRegex, AOT-safe
 * [STRUCTURE]: > raw HTML string -> o ExtractPlainText() = [StripScriptStyle -> StripTags -> HtmlDecode -> CollapseWhitespace] -> plain text (<=5000 chars) -> return string
 *
 * <summary>
 * [PURPOSE]: Utility class that extracts plain text from arbitrary HTML content. Used by the
 * fetch_and_extract MCP tool to convert fetched web pages into LLM-friendly text snippets.
 * AOT-safe: uses BCL Regex constructor with compiled options, no reflection, no native deps.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Returns non-null string (empty input -> empty output). MaxLength parameter defaults to 5000.
 * All operations are stateless -- static class with only static methods. Safe for concurrent calls from any thread.
 * [RATIONALE]: Q: Why separate utility instead of reusing SearchResultProcessor.StripHtml? A: Different truncation
 * limits (300 chars for search snippets vs 5000 chars for fetched pages) and different use contexts. Modifying M5
 * code is forbidden -- this avoids regression risk while keeping the regex pattern logic identical.
 * [CHANGES]: LAST_CHANGE: M6 -- initial creation (DevelopmentPlan.md section 2 step 1).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Net;
using System.Text.RegularExpressions;

namespace McpWebSearchService.Services;

#region CLASS_HtmlTextExtractorEnum [DOMAIN(Utility): Static utility class]
/// <summary>
/// [PURPOSE]: AOT-safe HTML-to-plain-text extraction utility for the fetch_and_extract tool.
/// Uses Regex constructor with CultureInvariant -- no reflection, no native code generation.
/// </summary>
internal static class HtmlTextExtractor
{
    #region CONSTS [DOMAIN(Utility): Configuration constants]
    /// <summary>Default maximum text length returned by ExtractPlainText.</summary>
    private const int DefaultMaxLength = 5000;

    /// <summary>Suffix appended to truncated text (Unicode ellipsis).</summary>
    private static readonly string Ellipsis = "\u2026";
    #endregion CONSTS

    #region REGEX_PATTERNS [DOMAIN(Utility): AOT-safe regex patterns]
    /// <summary>Matches script/style blocks including their content (single-line mode for multi-block content).</summary>
    private static readonly Regex ScriptStyleBlock = new(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Matches all remaining HTML tags.</summary>
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.CultureInvariant);

    /// <summary>Collapses multiple whitespace characters into a single space.</summary>
    private static readonly Regex WhitespaceCollapse = new(@"\s+");
    #endregion REGEX_PATTERNS

    #region METHOD_ExtractPlainText [DOMAIN(Utility): Main entry point]
    /// <summary>
    /// Extracts plain text from HTML content by stripping all markup and decoding entities.
    /// Pipeline: script/style blocks -> remaining tags -> entity decode -> whitespace collapse -> truncate with word-boundary.
    /// </summary>
    /// <param name="html">Raw HTML string to extract text from.</param>
    /// <param name="maxLength">Maximum length of the returned plain text (default 5000).</param>
    /// <returns>Plain-text content with all tags stripped, entities decoded, and whitespace normalized. Never null.</returns>
    public static string ExtractPlainText(string html, int maxLength = DefaultMaxLength)
    {
        #region STEP_STRIP_SCRIPT_STYLE [DOMAIN(Utility): Strip script/style blocks]
        // Remove script/style blocks including their content -- entire block is discarded.
        var result = ScriptStyleBlock.Replace(html, string.Empty);
        #endregion STEP_STRIP_SCRIPT_STYLE

        #region STEP_STRIP_TAGS [DOMAIN(Utility): Strip all remaining HTML tags]
        // Replace each tag with a single space to avoid text concatenation across block boundaries.
        // E.g., '</p><p>' -> ' ' so adjacent text stays separated.
        result = HtmlTagPattern.Replace(result, " ");
        #endregion STEP_STRIP_TAGS

        #region STEP_DECODE_ENTITIES [DOMAIN(Utility): Decode HTML entities]
        // Convert HTML entities to their character equivalents: &amp; -> &, &lt; -> <, etc.
        result = WebUtility.HtmlDecode(result);
        #endregion STEP_DECODE_ENTITIES

        #region STEP_COLLAPSE_WHITESPACE [DOMAIN(Utility): Normalize whitespace]
        // Collapse runs of spaces/tabs/newlines into single spaces and trim leading/trailing whitespace.
        result = WhitespaceCollapse.Replace(result, " ").Trim();
        #endregion STEP_COLLAPSE_WHITESPACE

        #region STEP_TRUNCATE [DOMAIN(Utility): Word-boundary truncation]
        return Truncate(result, maxLength);
        #endregion STEP_TRUNCATE
    }
    #endregion METHOD_ExtractPlainText

    #region METHOD_Truncate [DOMAIN(Utility): Truncation with word boundary]
    /// <summary>
    /// Truncates text to the given maximum length with a soft word-boundary cut.
    /// Searches backwards from maxLength (down to maxLength - 50) for a space character; if found,
    /// cuts there and appends ellipsis. If no space is found, hard-cuts at maxLength + ellipsis.
    /// </summary>
    private static string Truncate(string text, int maxLength)
    {
        #region STEP_CHECK_LENGTH [DOMAIN(Utility): Early return if within limit]
        // No truncation needed -- return as-is.
        if (text.Length <= maxLength)
            return text;
        #endregion STEP_CHECK_LENGTH

        #region STEP_FIND_BOUNDARY [DOMAIN(Utility): Word-boundary search in trailing range]
        var truncated = text[..maxLength];
        int cutPos = -1;

        // Search backwards from the end to find a word boundary.
        for (int i = Math.Min(maxLength, truncated.Length) - 1; i >= maxLength - 50 && i >= 0; i--)
        {
            if (truncated[i] == ' ')
            {
                cutPos = i;
                break;
            }
        }

        // Return with ellipsis -- either at the found word boundary or hard-cut.
        return cutPos >= 0 ? truncated[..cutPos] + Ellipsis : truncated + Ellipsis;
        #endregion STEP_FIND_BOUNDARY
    }
    #endregion METHOD_Truncate
}
#endregion CLASS_HtmlTextExtractor
