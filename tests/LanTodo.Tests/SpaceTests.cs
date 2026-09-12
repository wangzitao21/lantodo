using System.Net;
using LanTodo.Core;

static class SpaceTests
{
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("Space: signed transitive membership, tampering, removal replay and explicit readmission", Membership));
        tests.Add(("Space: concurrent removal denies offline member invitations and their descendants", ConcurrentRemoval));
        tests.Add(("Space: three peers auto-connect, origin may go offline, removal reaches remaining peers, rejoin", Mesh));
        tests.Add(("Space: legacy trust needs explicit migration and existing data survives", Migration));
        tests.Add(("Space: QR roundtrip carries root, pinned identity, addresses and expiry", Qr));
        tests.Add(("Space: membership observers can read concurrently without deadlocking", Observers));
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { return; } throw new Exception("Unauthorized operation accepted"); }
    static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)); while (!condition()) await Task.Delay(100, timeout.Token); }
    static Task Membership()
    {
        using var temp = new Sandbox(); using var a = new Peer(temp.Path("a")); using var b = new Peer(temp.Path("b")); using var c = new Peer(temp.Path("c"));
        a.Id.EnsureSpace(); a.Id.Space!.Add(b.Id.Certificate, b.Id.Name);
        b.Id.AdoptSpace(a.Id.Space.Snapshot, a.Id.Space.Root); b.Id.Space!.Add(c.Id.Certificate, c.Id.Name);
        a.Id.Space.Merge(b.Id.Space.Snapshot);
        Check(a.Id.IsTrusted(c.Id.Id), "Invitation by a member did not propagate");
        var stale = a.Id.Space.Snapshot;
        var corrupt = stale with { Events = stale.Events.Select((e,i) => i == 1 ? e with { Body = e.Body with { Name = "tampered" } } : e).ToArray() };
        Reject(() => a.Id.Space.Merge(corrupt));
        a.Id.Revoke(c.Id.Id); b.Id.Space.Merge(a.Id.Space.Snapshot); b.Id.Space.Merge(stale);
        Check(!b.Id.IsTrusted(c.Id.Id) && b.Id.Devices.All(d => d.Id != c.Id.Id), "Stale log resurrected membership");
        b.Id.Space.Add(c.Id.Certificate, c.Id.Name); a.Id.Space.Merge(b.Id.Space.Snapshot);
        Check(a.Id.IsTrusted(c.Id.Id), "Explicit invitation did not readmit removed identity");
        return Task.CompletedTask;
    }
    static Task ConcurrentRemoval()
    {
        using var temp = new Sandbox(); using var a = new Peer(temp.Path("a")); using var b = new Peer(temp.Path("b")); using var c = new Peer(temp.Path("c")); using var d = new Peer(temp.Path("d"));
        a.Id.EnsureSpace(); a.Id.Space!.Add(b.Id.Certificate, b.Id.Name); b.Id.AdoptSpace(a.Id.Space.Snapshot, a.Id.Space.Root);
        a.Id.Space.Remove(b.Id.Id);
        b.Id.Space!.Add(c.Id.Certificate, c.Id.Name); c.Id.AdoptSpace(b.Id.Space.Snapshot, b.Id.Space.Root); c.Id.Space!.Add(d.Id.Certificate, d.Id.Name);
        c.Id.Space.Remove(a.Id.Id);
        a.Id.Space.Merge(c.Id.Space.Snapshot); c.Id.Space.Merge(a.Id.Space.Snapshot);
        Check(!a.Id.IsTrusted(b.Id.Id) && !a.Id.IsTrusted(c.Id.Id) && !a.Id.IsTrusted(d.Id.Id), "Offline removal branch retained authority");
        Check(!c.Id.Space.Contains(c.Id.Id), "Revocation did not reach invitation descendant");
        Check(a.Id.Space.Contains(a.Id.Id), "Invalid invitation descendant removed an existing member");
        return Task.CompletedTask;
    }
    static Task Migration()
    {
        using var temp = new Sandbox(); using var a = new Peer(temp.Path("a")); using var b = new Peer(temp.Path("b"));
        a.Id.Trust(b.Id.Id, b.Id.Name); a.Add("保留原有清单"); Reject(() => a.Id.EnsureSpace());
        a.Id.EnsureSpace(true);
        Check(a.Store.List().Length == 1 && !a.Id.IsTrusted(b.Id.Id) && a.Id.Space!.Members.Length == 1, "Migration silently expanded old trust or lost data");
        return Task.CompletedTask;
    }
    static Task Qr()
    {
        using var temp = new Sandbox(); using var a = new Peer(temp.Path("qr")); a.Id.EnsureSpace();
        var code = a.Id.CreateSpaceInvite(["192.168.1.20:42851", "home.example:42851"]);
        using var matrix = QRCoder.QRCodeGenerator.GenerateQrCode(code, QRCoder.QRCodeGenerator.ECCLevel.M);
        int size = matrix.ModuleMatrix.Count * 4; var rgb = new byte[size * size * 3];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) for (int k = 0; k < 3; k++) rgb[(y * size + x) * 3 + k] = matrix.ModuleMatrix[y/4][x/4] ? (byte)0 : (byte)255;
        var decoded = InviteQr.Decode(new ZXing.RGBLuminanceSource(rgb, size, size, ZXing.RGBLuminanceSource.BitmapFormat.RGB24));
        Check(decoded == code, "QR cannot be decoded");
        var invite = DeviceIdentity.ParseSpaceInvite(decoded!); Check(invite.Root == a.Id.Space!.Root && invite.DeviceId == a.Id.Id && invite.Addresses.Length == 2, "QR invitation mismatch");
        Check(InviteQr.Png(code).Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}), "Invalid PNG"); return Task.CompletedTask;
    }
    static Task Observers()
    {
        using var temp = new Sandbox(); using var a = new Peer(temp.Path("a")); using var b = new Peer(temp.Path("b")); a.Id.EnsureSpace();
        a.Id.Space!.Changed += () => Check(Task.Run(() => a.Id.Space.Members).Wait(TimeSpan.FromSeconds(3)), "Notification held the membership lock while an observer read state");
        a.Id.Space.Add(b.Id.Certificate, b.Id.Name); a.Id.Space.Remove(b.Id.Id); return Task.CompletedTask;
    }
    static async Task Mesh()
    {
        using var temp = new Sandbox(); using var a = new Peer(temp.Path("a")); using var b = new Peer(temp.Path("b")); using var c = new Peer(temp.Path("c"));
        try
        {
            foreach (var p in new[] { a,b,c }) { p.Id.EnsureSpace(); p.Node.Start(false); p.Node.SetAdvertisedAddress("127.0.0.1:" + p.Node.Port); }
            await b.Node.JoinSpaceAsync(a.Node.CreateInvite());
            await c.Node.JoinSpaceAsync(b.Node.CreateInvite());
            await Until(() => a.Id.IsTrusted(c.Id.Id) && c.Id.IsTrusted(a.Id.Id));
            var first = a.Add("任何成员发起更新"); await Until(() => b.Has(first.Id) && c.Has(first.Id));
            await b.Node.DisposeAsync(); // The inviting peer is no longer a central dependency.
            var next = c.Add("邀请设备离线仍互通"); await Until(() => a.Has(next.Id));
            b.Node.Start(false);
            await Until(() => b.Has(next.Id));
            a.Id.Revoke(c.Id.Id);
            await Until(() => !b.Id.IsTrusted(c.Id.Id) && !c.Id.Space!.Contains(c.Id.Id));
            Check(!c.Node.Status.StartsWith("已同步") && !c.Node.IsOnline, "Removed device still reports an active sync");
            var denied = c.Add("被移除后的本机修改"); await Task.Delay(1500);
            Check(!a.Has(denied.Id) && !b.Has(denied.Id), "Removed device uploaded data");
            await c.Node.JoinSpaceAsync(a.Node.CreateInvite()); await Until(() => b.Has(denied.Id));
            Check(c.Store.List().Length > 0, "Removal wiped local data");
        }
        finally { foreach (var p in new[] {a,b,c}) await p.Node.DisposeAsync(); }
    }
    sealed class Peer : IDisposable
    {
        public TodoStore Store; public DeviceIdentity Id; public PeerNode Node;
        public Peer(string path) { Store = new(path); Id = new(Store.Database, System.IO.Path.GetFileName(path)); Node = new(Store, Id, 0) { Replicas = new(Store.Database) }; }
        public Revision Add(string title) => Store.Save(Id.Id, Id.Name, new TodoData(title));
        public bool Has(string id) => Store.Export().Any(r => r.Id == id);
        public void Dispose() { Id.Dispose(); Store.Dispose(); }
    }
}
