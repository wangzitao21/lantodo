namespace LanTodo.Core;

// Scheduling metadata only. Never used to decide which data version wins.
public sealed class SyncSchedule
{
    public DateTimeOffset LastSuccess { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset RetryAfter { get; private set; } = DateTimeOffset.MinValue;
    public long LocalVersion { get; private set; } = -1;
    public string? RemoteGeneration { get; private set; }
    private int failures;
    public bool Due(long localVersion, string? remoteGeneration, DateTimeOffset now, TimeSpan interval) =>
        now >= RetryAfter && (localVersion != LocalVersion || remoteGeneration != RemoteGeneration || now - LastSuccess >= interval);
    public void Succeeded(long localVersion, string? remoteGeneration, DateTimeOffset now)
    {
        LocalVersion = localVersion; RemoteGeneration = remoteGeneration; LastSuccess = now;
        RetryAfter = DateTimeOffset.MinValue; failures = 0;
    }
    public void Failed(DateTimeOffset now)
    {
        failures = Math.Min(failures + 1, 6);
        RetryAfter = now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, failures - 1)));
    }
}
