using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanTodo.Core;
using Microsoft.Data.Sqlite;

static class StartupTests
{
    const string Actor = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("Startup: captures survive restart, failed SQL and replay after commit without duplicates or resurrection", Captures));
        tests.Add(("Startup: reachable route wins while an older TLS address stalls", Routes));
        tests.Add(("Startup: foreground reconnect interrupts an in-flight stale TLS handshake", Reconnect));
        tests.Add(("Startup: local text reaches the peer before its history is downloaded", UploadFirst));
        tests.Add(("Startup: a capture during a sync round is sent before unfinished attachments", TextBeforeAttachments));
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static async Task Until(Func<bool> condition, int seconds = 5)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    static Task Captures()
    {
        using var temp = new Sandbox(); var path = temp.Path("inbox.json");
        var inbox = new QuickCaptureInbox(path); inbox.Enqueue("first fleeting thought"); inbox.Enqueue("second thought");
        var replay = File.ReadAllBytes(path);
        using var store = new TodoStore(temp.Path("profile"));
        using (var sql = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Database.FilePath, Pooling = false }.ToString()))
        {
            sql.Open(); using var cmd = sql.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER refuse_capture BEFORE INSERT ON revisions BEGIN SELECT RAISE(ABORT,'test'); END"; cmd.ExecuteNonQuery();
            try { inbox.CommitTo(store, Actor, "Phone"); throw new Exception("Failed write was accepted"); } catch (SqliteException) { }
            Check(new QuickCaptureInbox(path).Pending.Length == 2 && store.List().Length == 0, "Failed SQL consumed captures");
            cmd.CommandText = "DROP TRIGGER refuse_capture"; cmd.ExecuteNonQuery();
        }
        Check(new QuickCaptureInbox(path).CommitTo(store, Actor, "Phone") == 2 && store.List().Length == 2, "Restart did not replay captures");
        var first = store.List().Single(t => t.Data.Title == "first fleeting thought"); store.Purge(Actor, "Phone", first);
        int versions = store.Export().Length;
        File.WriteAllBytes(path, replay); // Simulate a stop after SQLite committed but before inbox removal.
        Check(new QuickCaptureInbox(path).CommitTo(store, Actor, "Phone") == 0 && store.Export().Length == versions && store.List().Length == 1,
            "Replay duplicated or resurrected a committed capture");
        Check(!File.Exists(path), "An empty inbox remained on the startup path");
        store.Drafts.Preserve("compose-recovery", new(new("older draft"), []));
        store.Drafts.Preserve("compose-recovery", new(new("another old draft"), []));
        store.Drafts.Preserve("compose-recovery", new(new("older draft"), []));
        Check(store.Drafts.WithPrefix("compose-recovery").Length == 2, "Recovering drafts overwrote an older capture or duplicated it");
        var recovery = store.Drafts.WithPrefix("compose-recovery")[0];
        store.SaveDraft("compose", new(new("new thought"), []));
        store.Drafts.Swap("compose", recovery.Key);
        Check(store.Drafts.Get("compose")!.Data.Title == recovery.Value.Data.Title && store.Drafts.Get(recovery.Key)!.Data.Title == "new thought", "Restoring the old draft lost the new input");
        store.Drafts.Preserve("compose-recovery", recovery.Value);
        store.Drafts.Preserve("compose-recovery", recovery.Value);
        Check(store.Drafts.WithPrefix("compose-recovery").Length == 3 && store.Drafts.Get(recovery.Key)!.Data.Title == "new thought", "Preserving a restored draft overwrote the newer input or duplicated it");
        return Task.CompletedTask;
    }
    static async Task Routes() => await RouteScenario(false);
    static async Task Reconnect() => await RouteScenario(true);
    static async Task RouteScenario(bool reconnect)
    {
        using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
        using var ia = new DeviceIdentity(a.Database, "Phone"); using var ib = new DeviceIdentity(b.Database, "PC");
        ia.EnsureSpace(); ia.Space!.Add(ib.Certificate, ib.Name); ib.AdoptSpace(ia.Space.Snapshot, ia.Space.Root);
        var settings = new ReplicaSettings(a.Database);
        await using var na = new PeerNode(a, ia, 0) { Replicas = settings }; await using var nb = new PeerNode(b, ib, 0);
        using var stalled = new TcpListener(IPAddress.Loopback, 0); stalled.Start();
        string bad = "127.0.0.1:" + ((IPEndPoint)stalled.LocalEndpoint).Port;
        nb.Start(false); string good = "127.0.0.1:" + nb.Port;
        if (reconnect) settings.SetEndpoint(ib.Id, bad);
        else a.Database.WriteMetadata("space-routes-v2.json", JsonSerializer.SerializeToUtf8Bytes(new { Root = ia.Space.Root, Routes = new[] { new RouteHint(ib.Id, [bad, good]) } }, Json.Options));
        a.Save(ia.Id, ia.Name, new("quick message"));
        var accepted = stalled.AcceptTcpClientAsync(); var clock = Stopwatch.StartNew(); na.Start(false);
        using var hanging = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        if (reconnect) { settings.SetEndpoint(ib.Id, good); clock.Restart(); na.RequestReconnect(); }
        await Until(() => b.List().Any(t => t.Data.Title == "quick message"));
        Check(clock.Elapsed < TimeSpan.FromSeconds(5), "Reachable peer waited for the 12-second stale handshake");
        Console.WriteLine($"      {(reconnect ? "foreground reconnect" : "stale route race")}: {clock.ElapsedMilliseconds} ms");
    }
    static async Task UploadFirst()
    {
        using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
        using var ia = new DeviceIdentity(a.Database, "Phone"); using var ib = new DeviceIdentity(b.Database, "PC");
        ia.Trust(ib.Id, ib.Name); ib.Trust(ia.Id, ia.Name);
        var message = a.Save(ia.Id, ia.Name, new("send before downloading history"));
        b.Import(Enumerable.Range(0, 80).Select(i => Revision.Create(new(1, Guid.NewGuid().ToString("N"), ib.Id, ib.Name, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToString("O"), [], new("history " + i)))));
        bool downloaded = false;
        a.Changed += () => { downloaded = true; Check(b.History(message.Body.TodoId).Length == 1, "History download delayed the outgoing message"); };
        await using var na = new PeerNode(a, ia, 0); await using var nb = new PeerNode(b, ib, 0); nb.Start(false);
        await na.SyncAsync(new(IPAddress.Loopback, nb.Port), ib.Id);
        Check(downloaded && a.List().Length == 81 && b.List().Length == 81, "The upload-first round failed to converge");
    }
    static async Task TextBeforeAttachments()
    {
        using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
        using var ia = new DeviceIdentity(a.Database, "Phone"); using var ib = new DeviceIdentity(b.Database, "PC");
        ia.Trust(ib.Id, ib.Name); ib.Trust(ia.Id, ia.Name);
        var settings = new ReplicaSettings(a.Database);
        await using var na = new PeerNode(a, ia, 0) { Replicas = settings }; await using var nb = new PeerNode(b, ib, 0);
        using var content = new MemoryStream(new byte[AttachmentStore.ChunkSize * 4 + 7]);
        var file = a.Attachments.Add(content, "large.bin");
        a.Save(ia.Id, ia.Name, new("older attachment", Attachments: [file]));
        int injected = 0; bool sawBlob = false, textArrivedFirst = false;
        b.Changed += () =>
        {
            if (Interlocked.Exchange(ref injected, 1) == 0) a.Save(ia.Id, ia.Name, new("fleeting thought during sync"));
            if (!sawBlob && b.Attachments.Has(file))
            {
                textArrivedFirst = b.List().Any(t => t.Data.Title == "fleeting thought during sync");
                sawBlob = true;
            }
        };
        nb.Start(false); settings.SetEndpoint(ib.Id, "127.0.0.1:" + nb.Port); na.Start(false);
        await Until(() => sawBlob && b.List().Length == 2);
        Check(textArrivedFirst, "An attachment delayed the text captured during the previous round");
    }
}
