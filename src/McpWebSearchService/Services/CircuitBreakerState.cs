#region MODULE_CONTRACT [DOMAIN(CircuitBreaker): SearXNG; CONCEPT(State Machine): Manual CB without Polly; TECH(AOT): Thread-safe]
/**
 * [GREP_SUMMARY]: CircuitBreakerState, manual circuit breaker, Closed, Open, HalfOpen, thread-safe, lock, failure threshold, recovery probe
 * [STRUCTURE]: > Failures -> o RecordFailure() -> + (count >= 3 ? OPEN) = Breaker.OPEN -> halt requests until OpenDuration -> HalfOpen (probe) -> RecordSuccess()/RecordFailure() = Closed/Open
 *
 * <summary>
 * [PURPOSE]: Manual Circuit Breaker state machine for the SearXNG HTTP client. Three states:
 * CLOSED (normal operation), OPEN (fail-fast, reject all requests), HALF_OPEN (allow one probe
 * after OpenDuration has elapsed). No external dependencies — fully AOT-safe (ADR-007).
 * </summary>
 * <remarks>
 * [INVARIANTS]: State transitions are atomic (protected by lock). Failure counter is reset on success.
 * Only ONE HalfOpen probe is allowed per recovery window; subsequent probes after the first return false.
 * [RATIONALE]: Q: Why manual instead of Polly? A: Polly v7 uses reflection/expression trees (not AOT-safe).
 * Polly v8 is AOT-compatible but adds a dependency for ~50 lines of trivial logic. Manual implementation
 * gives zero runtime dependencies and full control over the state machine semantics (per plan §0.1).
 * [CHANGES]: LAST_CHANGE: M4 — initial creation (DevelopmentPlan.md §2 step 3 + ADR-007).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Services;

#region CLASS_CircuitBreakerStateEnum [DOMAIN(CircuitBreaker): Enum of possible states]
/// <summary>
/// [PURPOSE]: Possible states of the Circuit Breaker state machine.
/// </summary>
public enum CircuitBreakerStateEnum
{
    /// <summary>Closed — requests flow normally.</summary>
    Closed,

    /// <summary>Open — fail-fast: reject all requests until OpenDuration elapses.</summary>
    Open,

    /// <summary>HalfOpen — one probe request is allowed. Result determines next state.</summary>
    HalfOpen,
}
#endregion CLASS_CircuitBreakerStateEnum

#region CLASS_CircuitBreakerState [DOMAIN(CircuitBreaker): Thread-safe state machine]
/// <summary>
/// [PURPOSE]: Thread-safe Circuit Breaker implementation (manual, no external library).
/// Threshold: 3 consecutive failures → OPEN. Recovery timeout: 30 seconds.
/// </summary>
public sealed class CircuitBreakerState
{
    #region FIELDS_CONFIGURATION [DOMAIN(CircuitBreaker): Configuration constants]
    /// <summary>Number of consecutive failures before the breaker trips to Open.</summary>
    private const int FailureThreshold = 3;

    /// <summary>Duration the breaker stays in Open state before allowing a HalfOpen probe.</summary>
    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(30);
    #endregion FIELDS_CONFIGURATION

    #region FIELDS_STATE [DOMAIN(CircuitBreaker): Mutable state fields]
    /// <summary>Current circuit breaker state. Protected by _lock for thread safety.</summary>
    private CircuitBreakerStateEnum _state = CircuitBreakerStateEnum.Closed;

    /// <summary>Count of consecutive failures. Reset to 0 on success.</summary>
    private int _failureCount;

    /// <summary>Timestamp of the last failure. Used to determine recovery timeout.</summary>
    private DateTime? _lastFailureTime;

    /// <summary>Synchronization lock for thread-safe state transitions.</summary>
    private readonly object _lock = new();
    #endregion FIELDS_STATE

    #region PROPERTY_IsOpen [DOMAIN(CircuitBreaker): Read-only state probe]
    /// <summary>
    /// [PURPOSE]: Returns true if the circuit breaker is currently OPEN (rejecting requests).
    /// Exposed for test verification and diagnostic logging.
    /// </summary>
    public bool IsOpen => _state == CircuitBreakerStateEnum.Open;
    #endregion PROPERTY_IsOpen

    #region METHOD_TryAcquirePermit [DOMAIN(CircuitBreaker): Request gating]
    /// <summary>
    /// [PURPOSE]: Determines whether the current request is allowed through.
    /// CLOSED → true (normal). OPEN → false unless OpenDuration elapsed (→ HALF_OPEN probe).
    /// HALF_OPEN → false for all but the first probe after recovery — only one probe per window.
    /// </summary>
    /// <returns>true if the request is permitted; false to fail-fast.</returns>
    public bool TryAcquirePermit()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case CircuitBreakerStateEnum.Closed:
                    return true;

                case CircuitBreakerStateEnum.Open when _lastFailureTime.HasValue
                    && (DateTime.UtcNow - _lastFailureTime.Value >= OpenDuration):
                    // Recovery window elapsed — transition to HalfOpen and allow ONE probe.
                    _state = CircuitBreakerStateEnum.HalfOpen;
                    return true;

                case CircuitBreakerStateEnum.Open:
                    return false;

                case CircuitBreakerStateEnum.HalfOpen when _failureCount > 0:
                    // A HalfOpen probe is already in-flight — reject subsequent probes.
                    return false;

                default:
                    return false;
            }
        }
    }
    #endregion METHOD_TryAcquirePermit

    #region METHOD_RecordFailure [DOMAIN(CircuitBreaker): Failure reporting]
    /// <summary>
    /// [PURPOSE]: Records a failure (HTTP error, timeout, or CB-rejected request).
    /// Increments the failure counter and sets _lastFailureTime. If threshold reached,
    /// transitions to OPEN state immediately.
    /// </summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailureTime = DateTime.UtcNow;

            if (_failureCount >= FailureThreshold)
            {
                _state = CircuitBreakerStateEnum.Open;
            }
        }
    }
    #endregion METHOD_RecordFailure

    #region METHOD_RecordSuccess [DOMAIN(CircuitBreaker): Success reporting]
    /// <summary>
    /// [PURPOSE]: Records a successful request. Resets the failure counter to zero and
    /// transitions back to CLOSED (normal operation).
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitBreakerStateEnum.Closed;
        }
    }
    #endregion METHOD_RecordSuccess

    #region PROPERTY_FailureCount [DOMAIN(Test): Expose failure count for test verification]
    /// <summary>
    /// [PURPOSE]: Current consecutive failure count (internal — test-only via InternalsVisibleTo).
    /// </summary>
    internal int FailureCount => _failureCount;
    #endregion PROPERTY_FailureCount

    #region TEST_METHOD_Reset [DOMAIN(Test): Test-only state reset]
    /// <summary>
    /// [PURPOSE]: Resets all circuit breaker state to initial values for test scenarios.
    /// Internal — only accessible via InternalsVisibleTo in the test project csproj.
    /// </summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _state = CircuitBreakerStateEnum.Closed;
            _failureCount = 0;
            _lastFailureTime = null;
        }
    }
    #endregion TEST_METHOD_Reset

    #region TEST_METHOD_SetLastFailureTime [DOMAIN(Test): Test-only timestamp manipulation]
    /// <summary>
    /// [PURPOSE]: Sets the internal failure timestamp for testing recovery scenarios.
    /// Allows simulating time passage without waiting 30 seconds.
    /// </summary>
    /// <param name="time">The timestamp to set.</param>
    internal void SetLastFailureTime(DateTime? time)
    {
        lock (_lock)
        {
            _lastFailureTime = time;
        }
    }
    #endregion TEST_METHOD_SetLastFailureTime

    #region TEST_METHOD_SetState [DOMAIN(Test): Test-only state override]
    /// <summary>
    /// [PURPOSE]: Sets the internal state for testing specific scenarios.
    /// </summary>
    /// <param name="state">The CircuitBreakerStateEnum to set.</param>
    internal void SetState(CircuitBreakerStateEnum state)
    {
        lock (_lock)
        {
            _state = state;
        }
    }
    #endregion TEST_METHOD_SetState
}
#endregion CLASS_CircuitBreakerState
