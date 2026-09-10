using System.Net;
using System.Net.Sockets;
using LanTodo.Core;

static class ReplicaTests
{
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("NAS settings validate addresses and survive restart", Settings));
        tests.Add(("NAS relay: non-overlapping clients, two NAS, offline conflict/delete, pause and restart", Relay));
        tests.Add(("NAS watch wakes without inventory polling; unreachable peer cannot block healthy peer; revoke denies writes", Watch));
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(100, timeout.Token);
    }
    static Task Settings()
    {
        using var temp = new Sandbox();
        var id = new string('a', 64);
        using (var store = new TodoStore(temp.Path("settings")))
        {
            var settings = new ReplicaSettings(store.Database);
            Check(!settings.Current.NasEnabled && settings.Current.LanEnabled, "Existing users must keep LAN defaults");
            foreach (var bad in new[] { "", "host", "https://host:443", "user:pass@host:80", "host:42851/path", "host:0", "host:65536", "host:123#fragment", "host:123?query" })
            {
                try { settings.SetEndpoint(id, bad); throw new Exception("Accepted address: " + bad); }
                catch (InvalidDataException) { }
            }
            settings.SetEndpoint(id, "nas.home:42851");
            settings.SetMode(true, false);
            var copy = settings.Current.Endpoints!; copy[0] = new(id, "other:1234");
            Check(settings.Current.Endpoints![0].Address == "nas.home:42851", "Configuration escaped by reference");
            _ = ReplicaAddress.Parse("[::1]:42851");
        }
        using (var reopened = new TodoStore(temp.Path("settings")))
        {
            var settings = new ReplicaSettings(reopened.Database);
            Check(settings.Current.NasEnabled && !settings.Current.LanEnabled && settings.Current.Endpoints!.Single().Address == "nas.home:42851", "Route lost on restart");
            settings.SetMode(false, true);
            Check(settings.Current.Endpoints!.Length == 1, "Pausing discarded route");
            settings.RemoveEndpoint(id);
            Check(settings.Current.Endpoints!.Length == 0, "Route removal failed");
        }
        return Task.CompletedTask;
    }
    static async Task Relay()
    {
        using var temp = new Sandbox();
        await using var home = new Peer(temp.Path("home"));
        await using var office = new Peer(temp.Path("office"));
        await using var phone = new Peer(temp.Path("phone"));
        await using var pc = new Peer(temp.Path("pc"));
        home.Node.Start(false); office.Node.Start(false); phone.Node.Start(false);
        await office.Pair(home);
        await phone.Pair(home);
        var first = phone.Add("手机先修改，电脑稍后上线");
        await Until(() => office.Has(first.Id));
        await phone.Node.DisposeAsync();
        pc.Node.Start(false); await pc.Pair(office);
        await Until(() => pc.Has(first.Id));
        Check(!pc.Identity.IsTrusted(phone.Identity.Id), "Relay should not require all-to-all trust");
        await pc.Node.DisposeAsync();
        var original = phone.Store.List().Single();
        var deletion = phone.Store.Save(phone.Identity.Id, "phone", original.Data with { Deleted = true }, original.Id, original.VersionIds);
        var edit = pc.Store.Save(pc.Identity.Id, "pc", original.Data with { Title = "离线修改" }, original.Id, original.VersionIds);
        phone.Node.Start(false); pc.Node.Start(false);
        await Until(() => phone.Has(edit.Id) && pc.Has(deletion.Id) && home.Has(edit.Id) && office.Has(deletion.Id));
        Check(pc.Store.List().Single().Conflict && phone.Store.List().Single().Conflict, "Delete/edit conflict was lost in transit");
        var conflict = pc.Store.List().Single();
        var resolution = pc.Store.Save(pc.Identity.Id, "pc", new TodoData("用户解决冲突"), conflict.Id, conflict.VersionIds);
        await Until(() => phone.Has(resolution.Id) && home.Has(resolution.Id) && office.Has(resolution.Id));
        Check(phone.Store.List().Single().VersionIds.Single() == resolution.Id, "Conflict resolution failed to converge");
        pc.Settings.SetMode(false, true);
        await Task.Delay(800);
        var paused = phone.Add("暂停后保留本地修改");
        await Until(() => office.Has(paused.Id));
        await Task.Delay(1000);
        Check(!pc.Has(paused.Id), "NAS pause did not stop fixed connection");
        pc.Settings.SetMode(true, true);
        await Until(() => pc.Has(paused.Id));
        await office.Node.DisposeAsync();
        office.Node.Start(false);
        pc.Settings.SetEndpoint(office.Identity.Id, office.Address);
        var afterRestart = pc.Add("NAS 重启后继续同步");
        await Until(() => phone.Has(afterRestart.Id));
        foreach (var peer in new[] { home, office, phone, pc })
            Check(peer.Store.Export().Length == 6, "Duplicate or missing revisions after relay");
    }
    static async Task Watch()
    {
        using var temp = new Sandbox();
        await using var nas = new Peer(temp.Path("nas"));
        await using var client = new Peer(temp.Path("client"));
        nas.Node.Start(false); client.Node.Start(false);
        // Configure a dead NAS before the healthy one: retries must be independent.
        var deadId = new string('d', 64); client.Identity.Trust(deadId, "离线 NAS");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var deadPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        client.Settings.SetEndpoint(deadId, "127.0.0.1:" + deadPort);
        await client.Pair(nas);
        await Until(() => client.Node.ReplicaStatuses.Any(s => s.DeviceId == nas.Identity.Id && s.LastSuccess is not null));
        await Task.Delay(1000);
        var before = client.Node.SuccessfulSyncs;
        await Task.Delay(12000); // Cross one watch heartbeat; there must be no full inventory round.
        Check(client.Node.SuccessfulSyncs == before, "Idle watch exchanged full inventories");
        // This represents a revision imported by NAS from another client.
        var pushed = nas.Add("服务器变化唤醒客户端");
        await Until(() => client.Has(pushed.Id));
        var outgoing = client.Add("本机修改打断等待");
        await Until(() => nas.Has(outgoing.Id));
        await Until(() => client.Node.ReplicaStatuses.Any(s => s.DeviceId == deadId && s.State == "等待重连"));
        nas.Identity.Revoke(client.Identity.Id);
        var rejected = client.Add("撤销后不得上传");
        await Task.Delay(1500);
        Check(!nas.Has(rejected.Id), "Revoked client wrote a revision");
        Check(client.Has(rejected.Id), "Failed upload lost local data");
    }
    sealed class Peer : IAsyncDisposable
    {
        public TodoStore Store { get; }
        public DeviceIdentity Identity { get; }
        public ReplicaSettings Settings { get; }
        public PeerNode Node { get; }
        public string Address => "localhost:" + Node.Port;
        public Peer(string path)
        {
            Store = new(path);
            try { Identity = new(Store.Database, Path.GetFileName(path)); }
            catch { Store.Dispose(); throw; }
            Settings = new(Store.Database);
            Node = new(Store, Identity, 0) { Replicas = Settings };
        }
        public Task Pair(Peer other) => Node.PairAddressAsync(other.Address, other.Identity.CreateInvite());
        public Revision Add(string title) => Store.Save(Identity.Id, Identity.Name, new TodoData(title));
        public bool Has(string id) => Store.Export().Any(r => r.Id == id);
        public async ValueTask DisposeAsync() { await Node.DisposeAsync(); Identity.Dispose(); Store.Dispose(); }
    }
}
