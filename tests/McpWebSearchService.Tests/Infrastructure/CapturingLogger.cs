#region MODULE_CONTRACT [DOMAIN(Test): CapturingLogger; CONCEPT(ILogger): Thread-safe log capture for LDD verification]
/**
 * [GREP_SUMMARY]: CapturingLogger, ILogger<T>, LogEntry record, thread-safe, formatter callback, LDD markers
 * [STRUCTURE]: > capturedLogs List<LogEntry> -> o IsEnabled() = true | + Log(LogLevel, state, ex, fmt) -> lock { entry } = _entries.Add -> return 0
 *              -> TestHostFactory injects CapturingLogger<T> into DI to verify [IMP:7-10] markers
 *
 * <summary>
 * [PURPOSE]: Thread-safe ILogger&lt;T&gt; implementation that captures every log entry into a thread-safe list.
 * Used exclusively in integration tests (M8) to verify LDD telemetry — specifically the presence of
 * [IMP:7-10] markers on key execution paths (search, unavailability, timeout). Without this logger,
 * tests verify OUTPUT but not LOG CONTENT — the GREEN TEST TRAP. @qa relies on these captures for
 * semantic trace verification per ADR-027 and mode-debug docs.
 * </summary>
 * <remarks>
 * [INVARIANTS]: IsEnabled always returns true (capture everything). LogEntries is thread-safe via lock.
 * Each test creates its own CapturingLogger instance — no shared mutable state between tests.
 * Formatter callback is invoked inside the lock to guarantee consistent state ordering.
 * [RATIONALE]: Q: Why not use TestLogger from xUnit? A: xUnit's TestLogger writes to console output,
 * which requires parsing and is fragile under parallel test execution. CapturingLogger gives direct
 * access to structured log entries for programmatic assertions on LDD markers.
 * [CHANGES]: LAST_CHANGE: M8 — initial creation (ADR-027).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using Microsoft.Extensions.Logging;
using McpWebSearchService.Models;

namespace McpWebSearchService.Tests.Infrastructure;

#region CLASS_CapturingLoggerEnum [DOMAIN(Test): LogEntry record for captured entries]
/// <summary>
/// [PURPOSE]: Immutable snapshot of a single log entry as it was emitted by the logger.
/// </summary>
public sealed class LogEntry
{
    /// <summary>Log level at which the entry was recorded.</summary>
    public LogLevel Level { get; }

    /// <summary>The formatted message string (result of calling formatter with state).</summary>
    public string Message { get; }

    /// <summary>Exception associated with this log, or null if none.</summary>
    public Exception? Exception { get; }

    /// <summary>Constructs a new LogEntry from the parameters passed to ILogger.Log().</summary>
    public LogEntry(LogLevel level, string message, Exception? exception)
    {
        Level = level;
        Message = message ?? string.Empty;
        Exception = exception;
    }
}
#endregion CLASS_CapturingLoggerEnum

#region CLASS_CapturingLogger [DOMAIN(Test): Captures all log entries for test verification]
/// <summary>
/// [PURPOSE]: Thread-safe ILogger&lt;T&gt; that captures every log entry into a list.
/// Used by integration tests to verify LDD telemetry ([IMP:7-10] markers) on key paths.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    #region FIELDS [DOMAIN(Test): Capture storage + formatter callback]
    private readonly List<LogEntry> _entries = new();
    private readonly Func<string, IReadOnlyList<object?>?, Exception?, string> _formatter;
    private readonly object _lock = new();
    #endregion FIELDS

    /// <summary>All captured log entries in chronological order.</summary>
    public IReadOnlyList<LogEntry> Entries => _entries;

    /// <summary>Constructs a CapturingLogger with the default ILogger formatter (equivalent to logging framework formatting).</summary>
    public CapturingLogger()
        : this((message, state, exception) => message!) { }

    /// <summary>Constructs a CapturingLogger with a custom formatter callback.</summary>
    public CapturingLogger(Func<string, IReadOnlyList<object?>?, Exception?, string> formatter)
    {
        _formatter = formatter ?? ((msg, _, _) => msg);
    }

    #region METHOD_BeginScope [DOMAIN(Test): No-op scope — tests don't need scopes]
    /// <summary>
    /// [PURPOSE]: Returns a no-op scope. Tests don't use scopes so this is safe to return null.
    /// </summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
    #endregion METHOD_BeginScope

    #region METHOD_IsEnabled [DOMAIN(Test): Always enabled — capture everything]
    /// <summary>
    /// [PURPOSE]: Returns true for all log levels to ensure nothing is filtered.
    /// </summary>
    public bool IsEnabled(LogLevel logLevel) => true;
    #endregion METHOD_IsEnabled

    #region METHOD_Log [DOMAIN(Test): Thread-safe capture]
    /// <summary>
    /// [PURPOSE]: Formats the message using the callback and appends to thread-safe list.
    /// All access is protected by _lock — no concurrent writes from multiple threads.
    /// </summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) where TState : notnull
    {
        var formattedMessage = formatter(state, exception);

        lock (_lock)
        {
            _entries.Add(new LogEntry(logLevel, formattedMessage ?? string.Empty, exception));
        }
    }
    #endregion METHOD_Log

    #region HELPER_ConvenientCapture [DOMAIN(Test): Helper for convenient test assertions]
    /// <summary>
    /// [PURPOSE]: Returns a snapshot copy of captured entries (thread-safe — lock-protected).
    /// Use in tests to verify log content without holding the lock across assert calls.
    /// </summary>
    public IReadOnlyList<LogEntry> TakeSnapshot()
    {
        lock (_lock)
            return _entries.ToList();
    }
    #endregion HELPER_ConvenientCapture
}
#endregion CLASS_CapturingLogger
