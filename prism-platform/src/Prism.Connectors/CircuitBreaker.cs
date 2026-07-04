// ============================================================
//  CircuitBreaker.cs
//  Minimal consecutive-failure breaker for connector sweep loops
//  that CONTINUE after individual failures (per-scope, per-title,
//  per-$batch). The shared HTTP clients already retry each request
//  with backoff; when even those retried requests fail N times IN
//  A ROW, something systemic is wrong (auth, outage, revoked
//  permission) and continuing would just hammer the service and
//  burn the run's time. The breaker then aborts the CONNECTOR with
//  a clear error - the job host still runs the other connectors,
//  and a REPLACE-mode sink keeps the previous good snapshot because
//  the aborted connector never reaches its WriteAsync.
//  Any success resets the count. Not thread-safe by design: each
//  instance guards one connector's own sequential loop.
// ============================================================
namespace Prism.Connectors;

public sealed class ConsecutiveFailureBreaker
{
    private readonly int _limit;
    private readonly string _what;
    private int _consecutive;

    /// <param name="limit">Consecutive failures tolerated before tripping (min 1).</param>
    /// <param name="what">Loop description for the error message, e.g. "azure.cost scope sweep".</param>
    public ConsecutiveFailureBreaker(int limit, string what)
    {
        _limit = Math.Max(1, limit);
        _what = what;
    }

    public void RecordSuccess() => _consecutive = 0;

    /// <summary>
    /// Count one failure; throws once the limit is reached with no success in between,
    /// aborting the connector cleanly instead of hammering on.
    /// </summary>
    public void RecordFailure(string detail)
    {
        if (++_consecutive < _limit) return;
        throw new InvalidOperationException(
            $"{_what}: {_consecutive} consecutive failures (last: {detail}) - circuit breaker tripped; " +
            "aborting this connector instead of hammering the service. " +
            "Raise Prism__CircuitBreakerFailures to tolerate more.");
    }
}
