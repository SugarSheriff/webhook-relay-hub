namespace RelayHub.Workers;

/// <summary>
/// Exponential backoff: wait 2s, then 4s, 8s, 16s... so a struggling target gets room to recover.
/// </summary>
public static class RetryPolicy
{
    public static TimeSpan DelayAfter(int failedAttempts, double baseSeconds, double maxSeconds)
    {
        var seconds = baseSeconds * Math.Pow(2, Math.Max(0, failedAttempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, maxSeconds));
    }
}
