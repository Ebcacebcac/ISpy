namespace ISpy.Core.Media;

/// <summary>
/// Decides how long to wait before retrying a dropped stream.
/// </summary>
/// <remarks>
/// Exponential backoff with a cap and jitter. The jitter matters more here than in most retry
/// loops: when a recorder reboots, sixteen tiles all fail within the same second, and without
/// jitter they would reconnect in lockstep and hammer it with a synchronised burst every time.
/// </remarks>
public sealed class ReconnectPolicy(
    TimeSpan? initialDelay = null,
    TimeSpan? maxDelay = null,
    double multiplier = 2.0,
    double jitterFraction = 0.2)
{
    private readonly TimeSpan _initial = initialDelay ?? TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _max = maxDelay ?? TimeSpan.FromSeconds(15);

    public int Attempt { get; private set; }

    /// <summary>Delay before the next attempt, then advances the attempt counter.</summary>
    public TimeSpan NextDelay(Func<double>? random = null)
    {
        var raw = _initial.TotalMilliseconds * Math.Pow(multiplier, Attempt);
        var capped = Math.Min(raw, _max.TotalMilliseconds);

        Attempt++;

        // Jitter is symmetric around the capped delay and never pushes it below the initial delay.
        var roll = random?.Invoke() ?? Random.Shared.NextDouble();
        var jitter = capped * jitterFraction * (roll * 2 - 1);

        return TimeSpan.FromMilliseconds(Math.Max(_initial.TotalMilliseconds, capped + jitter));
    }

    /// <summary>Called after a stream has been healthy, so the next outage starts from scratch.</summary>
    public void Reset() => Attempt = 0;
}
