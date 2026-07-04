// ============================================================
//  HttpRetry.cs
//  Shared retry timing for every HTTP client (Graph, Defender for
//  Endpoint, Defender for Cloud Apps, ARM). Two rules the whole
//  subsystem follows:
//   * Computed backoff is exponential AND jittered (0-1s random) so
//     parallel connectors / multiple replicas that fail together
//     don't retry together (thundering herd).
//   * A service-provided Retry-After (on 429, and on 503 when the
//     service sends one) is honored in both wire forms -
//     delta-seconds and HTTP-date - clamped to a ceiling so a
//     pathological header can't park the run, plus a small jitter
//     so identical hints don't re-synchronize the callers.
// ============================================================
using System.Net;

namespace Prism.Connectors;

internal static class HttpRetry
{
    /// <summary>Exponential backoff for transient failures: 2^attempt seconds (capped at 60s) + 0-1s jitter.</summary>
    public static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)))
        + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));

    /// <summary>
    /// The service's Retry-After - delta-seconds or HTTP-date - else a 5s default; never
    /// negative, clamped to <paramref name="capSeconds"/>, + 0-1s jitter (see file header).
    /// </summary>
    public static TimeSpan RetryAfterDelay(HttpResponseMessage resp, int capSeconds)
    {
        TimeSpan d;
        if (resp.Headers.RetryAfter?.Delta is { } delta) d = delta;
        else if (resp.Headers.RetryAfter?.Date is { } at) d = at - DateTimeOffset.UtcNow;
        else d = TimeSpan.FromSeconds(5);
        if (d < TimeSpan.Zero) d = TimeSpan.FromSeconds(5);       // clock-skewed HTTP-date
        TimeSpan cap = TimeSpan.FromSeconds(Math.Max(1, capSeconds));
        if (d > cap) d = cap;
        return d + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
    }

    /// <summary>
    /// True when the response carries the service's own wait hint and the delay should come
    /// from <see cref="RetryAfterDelay"/> instead of computed backoff: any 429 (Graph always
    /// sends Retry-After; the 5s default covers the rest), or a 503 that includes Retry-After
    /// (Graph/ARM emit 503 with Retry-After under load-shedding - waiting the hinted time
    /// beats blind exponential backoff).
    /// </summary>
    public static bool HasServiceWaitHint(HttpResponseMessage resp) =>
        resp.StatusCode == HttpStatusCode.TooManyRequests
        || (resp.StatusCode == HttpStatusCode.ServiceUnavailable && resp.Headers.RetryAfter is not null);
}
