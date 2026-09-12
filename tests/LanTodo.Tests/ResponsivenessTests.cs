using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LanTodo.Core;

static class ResponsivenessTests
{
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("Responsiveness: queued edits are ordered, survive prior failure and drain before shutdown", Writes));
        tests.Add(("Responsiveness: sending drains older drafts and keeps later input", Drafts));
        tests.Add(("Startup: shared local identities retain private keys and independent certificate lifetimes", SharedIdentity));
        tests.Add(("Startup: a different identity in an existing space is never replaced by the primary", DifferentIdentity));
        tests.Add(("Responsiveness: cached display names refresh after local and remote renames", Names));
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Writes()
    {
        var queue = new LocalWriteQueue();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var order = new List<int>();
        var first = queue.Enqueue(() => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); order.Add(1); });
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "Queued work did not start");
        var failed = queue.Enqueue(() => { order.Add(2); throw new IOException("Injected failure"); });
        var last = queue.Enqueue(() => { order.Add(3); return 42; });
        var stopped = queue.DisposeAsync().AsTask();
        Check(!stopped.IsCompleted && !last.IsCompleted, "Shutdown did not wait for accepted work");
        release.Set(); await first;
        try { await failed; throw new Exception("Failure was hidden from caller"); } catch (IOException) { }
        Check(await last == 42, "Prior failure prevented a later edit"); await stopped;
        Check(order.SequenceEqual(new[] { 1, 2, 3 }), "Queued work ran out of order");
        try { _ = queue.Enqueue(() => { }); throw new Exception("Disposed queue accepted work"); } catch (ObjectDisposedException) { }
    }
    static async Task Drafts()
    {
        using var temp = new Sandbox(); using var store = new TodoStore(temp.Path("drafts"));
        await using var queue = new LocalWriteQueue();
        const string actor = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        _ = queue.Enqueue(() => store.SaveDraft("compose", new(new("old text"), [])));
        await queue.DrainAsync();
        var sent = store.Save(actor, "PC", new("old text"), draftKey: "compose");
        _ = queue.Enqueue(() => store.SaveDraft("compose", new(new("next text"), [])));
        await queue.DrainAsync();
        Check(store.List().Single().Id == sent.Body.TodoId && store.Drafts.Get("compose")?.Data.Title == "next text", "Sending erased later input or restored an older draft");
    }
    static async Task SharedIdentity()
    {
        using var temp = new Sandbox(); await using var app = new AppRuntime(temp.Path("shared"), "PC", 0);
        var first = app.Identity; var id = first.Id;
        await app.CreateSpaceAsync("Second network");
        Check(app.Identity.Id == id && app.Identity.Certificate.HasPrivateKey, "Additional space lost the local private key");
        first.Dispose();
        using var privateKey = app.Identity.Certificate.GetRSAPrivateKey()!;
        using var publicKey = app.Identity.Certificate.GetRSAPublicKey()!;
        byte[] data = [1, 2, 3];
        var signature = privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Check(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "Disposing one certificate invalidated another space");
    }
    static async Task DifferentIdentity()
    {
        using var temp = new Sandbox(); var path = temp.Path("different"); string primary;
        await using (var app = new AppRuntime(path, "PC", 0)) primary = app.Identity.Id;
        string key = Guid.NewGuid().ToString("N"), other;
        using (var store = new TodoStore(Path.Combine(path, "spaces", key)))
        using (var identity = new DeviceIdentity(store.Database, "Separate")) { identity.EnsureSpace(); other = identity.Id; }
        using (var profile = new SqliteProfile(path)) profile.WriteMetadata("spaces-catalog.json", JsonSerializer.SerializeToUtf8Bytes(new { active = key, keys = new[] { key } }, Json.Options));
        await using var reopened = new AppRuntime(path, "PC", 0);
        Check(reopened.Identity.Id == other && other != primary, "Opening the catalog replaced a distinct persisted identity");
    }
    static async Task Names()
    {
        using var temp = new Sandbox(); await using var a = new AppRuntime(temp.Path("names"), "PC", 0);
        var id = a.Identity.Id;
        Check(a.DisplayName(id, "fallback") == "fallback", "Missing override did not use its supplied fallback");
        a.RenameSelf("First"); Check(a.DisplayName(id, "fallback") == "First", "Local rename did not invalidate cache");
        await a.CreateSpaceAsync("Extra");
        a.RenameSelf("Second"); Check(a.DisplayName(id, "fallback") == "Second", "Multi-space rename left stale cache");
        using var store = new TodoStore(temp.Path("remote")); using var remote = new DeviceIdentity(store.Database, "Remote");
        remote.Rename(remote.Id, "Remote name");
        Check(a.DisplayName(remote.Id, "Before") == "Before", "Unexpected remote override");
        a.Identity.MergeLabels(remote.Labels);
        Check(a.DisplayName(remote.Id, "Before") == "Remote name", "Remote labels left cached fallback");
    }
}
