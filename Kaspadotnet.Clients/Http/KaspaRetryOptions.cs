namespace Kaspadotnet.Clients.Http;

/// <summary>
/// Retry policy used by <see cref="KaspaRetryHandler"/>. Defaults are conservative
/// because api.kaspa.org rate-limits some routes: three retries, exponential
/// backoff from 500 ms capped at 10 s, and <c>Retry-After</c> honoured (also capped).
/// </summary>
public sealed class KaspaRetryOptions
{
    /// <summary>Default policy: 3 retries, 500 ms base delay, 10 s cap, 30 s per attempt.</summary>
    public static KaspaRetryOptions Default => new();

    /// <summary>Policy that never retries. The 30 s per-attempt timeout still applies.</summary>
    public static KaspaRetryOptions None => new() { MaxRetries = 0 };

    /// <summary>Retries after the first attempt. Zero disables retrying.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Delay before the first retry. Doubles on every further retry.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for any single delay, including a server supplied <c>Retry-After</c>.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout for a single attempt. <see cref="Timeout.InfiniteTimeSpan"/> disables it.</summary>
    public TimeSpan PerAttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>When true, each computed backoff is scaled by a random factor in [0.5, 1.0].</summary>
    public bool Jitter { get; init; } = true;

    /// <summary>
    /// Waits between attempts. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>;
    /// tests replace it so no real time passes.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    internal void Validate()
    {
        if (MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), "MaxRetries must not be negative.");
        }

        if (BaseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseDelay), "BaseDelay must not be negative.");
        }

        if (MaxDelay < BaseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), "MaxDelay must be at least BaseDelay.");
        }

        if (PerAttemptTimeout != Timeout.InfiniteTimeSpan && PerAttemptTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PerAttemptTimeout), "PerAttemptTimeout must be positive or infinite.");
        }

        ArgumentNullException.ThrowIfNull(DelayAsync);
    }
}
