#region MODULE_CONTRACT [DOMAIN(Exception): SearXNG; CONCEPT(Domain Exception): Unavailable backend; TECH(SPEC): plain-text error message]
/**
 * [GREP_SUMMARY]: SearXNGUnavailableException, domain exception, unavailable, error message, HTTP error, timeout, circuit breaker
 * [STRUCTURE]: > Timeout/HTTP error/CB open -> + SearXNGUnavailableException -> o M6 MCP tool -> = text response to LLM
 *
 * <summary>
 * [PURPOSE]: Domain-level exception indicating the SearXNG backend is temporarily unavailable.
 * Wraps transport exceptions (HttpRequestException, TaskCanceledException) so that the MCP
 * tool layer in M6 can map this to a user-visible message per SPEC §4.2.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Default message is exactly "Search service temporarily unavailable" — matches
 * SPEC §4.2 verbatim. InnerException carries the original transport exception for logging.
 * [RATIONALE]: Q: Why not return raw HttpRequestException? A: MCP tools (M6) must not throw
 * transport exceptions to LLM — they map this domain exception to a plain-text response string.
 * Preserving inner exception allows structured logging ([IMP:9]) while presenting clean UX.
 * [CHANGES]: LAST_CHANGE: M4 — initial creation (DevelopmentPlan.md §2 step 2).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Services;

#region CLASS_SearXNGUnavailableException [DOMAIN(Exception): SearXNG; CONCEPT(Domain Exception): Unavailable backend]
/// <summary>
/// [PURPOSE]: Thrown when the SearXNG HTTP client detects temporary unavailability (timeout,
/// HTTP error, or Circuit Breaker OPEN).
/// </summary>
public sealed class SearXNGUnavailableException : Exception
{
    #region FIELD_DEFAULT_MESSAGE [DOMAIN(Exception): Default message constant]
    /// <summary>
    /// Default message — SPEC §4.2 exact string.
    /// </summary>
    public const string DefaultMessage = "Search service temporarily unavailable";
    #endregion FIELD_DEFAULT_MESSAGE

    #region CTOR_SearXNGUnavailableException [DOMAIN(Exception): Constructors]
    /// <summary>
    /// [PURPOSE]: Constructs an exception with the default message (SPEC §4.2).
    /// </summary>
    public SearXNGUnavailableException()
        : base(DefaultMessage) { }

    /// <summary>
    /// [PURPOSE]: Constructs an exception with a custom message and optional inner exception.
    /// Preserves the original transport exception for structured logging ([IMP:9]).
    /// </summary>
    /// <param name="message">Custom error message.</param>
    /// <param name="inner">The original transport exception (HttpRequestException, TaskCanceledException, etc.).</param>
    public SearXNGUnavailableException(string message, Exception? inner)
        : base(message, inner) { }
    #endregion CTOR_SearXNGUnavailableException
}
#endregion CLASS_SearXNGUnavailableException
