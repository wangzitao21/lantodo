namespace LanTodo.Core;

public sealed class AppRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim networkGate = new(1);
    public TodoStore Store { get; }
    public DeviceIdentity Identity { get; }
    public PeerNode Node { get; }
    public ReplicaSettings Replicas { get; }
    public int ReconcileHours { get; private set; } = 1;
    public Task StartupBackup { get; }
    public AppRuntime(string path, string deviceName)
    {
        Store = new(path);
        try
        {
            Identity = new(Store.Database, deviceName);
            Replicas = new(Store.Database);
            Node = new(Store, Identity) { Replicas = Replicas };
            var settings = Store.Database.ReadMetadata("sync-settings.json");
            if (settings is not null) ReconcileHours = Math.Clamp(Json.Read<int>(settings), 1, 2);
            Node.ReconcileInterval = TimeSpan.FromHours(ReconcileHours);
            StartupBackup = Task.CompletedTask;
        }
        catch { Identity?.Dispose(); Store.Dispose(); networkGate.Dispose(); throw; }
    }
    public void SetReconcileHours(int hours)
    {
        if (hours is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(hours));
        Store.Database.WriteMetadata("sync-settings.json", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(hours));
        ReconcileHours = hours; Node.ReconcileInterval = TimeSpan.FromHours(hours);
    }
    public async Task SetNetworkAsync(bool enabled)
    {
        await networkGate.WaitAsync();
        try { if (enabled) Node.Start(); else await Node.DisposeAsync(); }
        finally { networkGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await SetNetworkAsync(false);
        await StartupBackup;
        Identity.Dispose(); Store.Dispose(); networkGate.Dispose();
    }
}
