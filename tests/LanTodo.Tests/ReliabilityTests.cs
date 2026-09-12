using System.IO.Compression;
using System.Text;
using LanTodo.Core;
using Microsoft.Data.Sqlite;

static class ReliabilityTests
{
    const string Actor = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static Attachment Add(TodoStore store, string name) => store.Attachments.Add(new MemoryStream(Encoding.UTF8.GetBytes(name)), name + ".txt");
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("Reliability: invalid restore and SQL failure leave live invalidations unchanged", RestoreFailure));
        tests.Add(("Reliability: restore ignores unrelated local pending blobs", RestoreIndependent));
        tests.Add(("Reliability: locked original cannot turn committed purge into failure", Cleanup));
        tests.Add(("Reliability: unrelated saves do not rewrite deleted attachment indexes", QuietWrites));
        tests.Add(("Reliability: backup rejects same-size corrupted originals", Integrity));
        tests.Add(("Experience: search covers notes/files and drafts survive restart", Drafts));
        tests.Add(("Reliability: batch trash purge is atomic and preserves changed records", PurgeBatch));
        tests.Add(("Experience: sending atomically clears its draft and orphan draft files are reclaimed", DraftCommit));
        tests.Add(("Power: idle watch reuses TLS and edits still converge", Watch));
    }
    static Task RestoreFailure()
    {
        using var temp = new Sandbox(); using var target = new TodoStore(temp.Path("target")); using var source = new TodoStore(temp.Path("source"));
        var kept = Add(target, "kept"); var r = target.Save(Actor, "PC", new("keep", Attachments: [kept]));
        source.Import([r]); source.Attachments.Invalidate([kept.Key]);
        var added = Add(source, "additional"); source.Save(Actor, "PC", new("additional", Attachments: [added]));
        var path = temp.Path("backup.zip"); source.Backup(path);
        using (var db = new SqliteConnection("Pooling=False;Data Source=" + target.Database.FilePath))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER refuse_restore BEFORE INSERT ON revisions BEGIN SELECT RAISE(ABORT,'test'); END"; command.ExecuteNonQuery();
            try { target.Restore(path); throw new Exception("Failed SQL restore was accepted"); } catch (SqliteException) { }
            Check(target.Export().Length == 1 && !target.Attachments.IsInvalid(kept), "SQL failure changed live data");
            command.CommandText = "DROP TRIGGER refuse_restore"; command.ExecuteNonQuery();
        }
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.GetEntry("attachments/" + added.Hash)!.Delete();
        try { target.Restore(path); throw new Exception("Incomplete backup was accepted"); } catch (InvalidDataException) { }
        Check(target.Export().Length == 1 && !target.Attachments.IsInvalid(kept), "Preflight failure invalidated a live original");
        source.Backup(path); target.Restore(path);
        Check(target.Attachments.IsInvalid(kept) && target.Export().Length == 2, "Committed restore did not advance history and invalidations together");
        return Task.CompletedTask;
    }
    static Task RestoreIndependent()
    {
        using var temp = new Sandbox(); using var target = new TodoStore(temp.Path("target")); using var source = new TodoStore(temp.Path("source"));
        target.Save(Actor, "PC", new("pending", Attachments: [new(new string('b', 64), "pending.txt", 4)]));
        source.Save(Actor, "PC", new("backup text")); var path = temp.Path("backup.zip"); source.Backup(path);
        Check(target.Restore(path) == 1 && target.List().Length == 2, "Unrelated pending original blocked restore");
        var one=Add(source,"shared");var two=Add(source,"shared");
        source.Save(Actor,"PC",new("shared original",Attachments:[one,two]));source.Backup(path);target.Restore(path);
        File.Delete(target.Attachments.PathFor(one.Hash));target.DetectMissingAttachments();
        Check(target.Attachments.IsInvalid(one)&&target.Attachments.IsInvalid(two),"Immediate deletion after restore lost a received instance");
        return Task.CompletedTask;
    }
    static Task Cleanup()
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        using var temp = new Sandbox(); var path = temp.Path("target"); Attachment item;
        using (var store = new TodoStore(path))
        {
            item = Add(store, "locked"); store.Save(Actor, "PC", new("purge", Attachments: [item]));
            int notifications = 0; store.Changed += () => notifications++;
            using var held = new FileStream(store.Attachments.PathFor(item.Hash), FileMode.Open, FileAccess.Read, FileShare.None);
            store.Purge(Actor, "PC", store.List().Single());
            store.Save(Actor, "PC", new("unrelated"));
            Check(notifications == 2 && store.List().Single().Data.Title == "unrelated", "Committed operation failed or missed notification");
            Check(store.MaintenanceWarning is not null, "Deferred cleanup has no diagnostic");
        }
        using var reopened = new TodoStore(path);
        Check(!reopened.Attachments.Has(item), "Restart did not retry deferred cleanup");
        return Task.CompletedTask;
    }
    static Task QuietWrites()
    {
        using var temp = new Sandbox(); using var store = new TodoStore(temp.Path("target")); var item = Add(store, "removed");
        store.Save(Actor, "PC", new("purge", Attachments: [item])); store.Purge(Actor, "PC", store.List().Single());
        var path = Path.Combine(store.Root, "attachment-index.json"); File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1));
        store.Save(Actor, "PC", new("text")); store.DetectMissingAttachments();
        Check(File.GetLastWriteTimeUtc(path).Year == 2000, "Text-only work rewrote the attachment index");
        return Task.CompletedTask;
    }
    static Task Integrity()
    {
        using var temp = new Sandbox(); using var store = new TodoStore(temp.Path("target")); var item = Add(store, "original");
        store.Save(Actor, "PC", new("file", Attachments: [item]));
        var backup = temp.Path("good.zip"); store.Backup(backup);
        File.WriteAllBytes(store.Attachments.PathFor(item.Hash), Enumerable.Repeat((byte)42, (int)item.Size).ToArray());
        store.DetectMissingAttachments(); Check(!store.Attachments.IsInvalid(item), "Corruption was propagated as a user deletion");
        try { store.Backup(temp.Path("bad.zip")); throw new Exception("Corrupt original was exported as verified backup"); } catch (InvalidDataException) { }
        store.Restore(backup); store.Attachments.Verify(item);
        Check(!store.Attachments.IsInvalid(item), "Repair invalidated the restored attachment");
        return Task.CompletedTask;
    }
    static Task PurgeBatch()
    {
        using var temp = new Sandbox(); using var store = new TodoStore(temp.Path("target"));
        for(int i=0;i<4;i++)store.Save(Actor,"PC",new("trash "+i,Deleted:true));
        var confirmed=store.Trash();
        store.Save(Actor,"PC",confirmed[0].Data with {Deleted=false},confirmed[0].Id,confirmed[0].VersionIds);
        using(var db=new SqliteConnection("Pooling=False;Data Source="+store.Database.FilePath))
        {
            db.Open();using var command=db.CreateCommand();
            command.CommandText="CREATE TRIGGER refuse_purge BEFORE INSERT ON revisions BEGIN SELECT RAISE(ABORT,'test'); END";command.ExecuteNonQuery();
            try{store.PurgeTrash(Actor,"PC",confirmed);throw new Exception("Failed batch commit was accepted");}catch(SqliteException){}
            Check(store.Trash().Length==3 && store.Export().Length==5,"Batch failure partially purged data");
            command.CommandText="DROP TRIGGER refuse_purge";command.ExecuteNonQuery();
        }
        int notifications=0;store.Changed+=()=>notifications++;
        var result=store.PurgeTrash(Actor,"PC",confirmed);
        Check(result==(3,1)&&notifications==1&&store.List().Length==1&&store.Trash().Length==0,"Batch purge lost a changed record or sent partial notifications");
        return Task.CompletedTask;
    }
    static Task Drafts()
    {
        using var temp = new Sandbox(); var path = temp.Path("target"); Attachment item;
        using (var store = new TodoStore(path))
        {
            item = Add(store, "attachment"); store.Drafts.Save("compose", new(new("unfinished", Attachments: [item]), []));
            store.Save(Actor, "PC", new("record", "important notes", Attachments: [item]));
            Check(MessageQuery.Matches(store.List().Single(), "IMPORTANT attachment"), "Search ignored notes/files");
            store.Purge(Actor, "PC", store.List().Single()); Check(store.Attachments.Has(item), "Purge destroyed a draft original");
        }
        using var reopened = new TodoStore(path);
        Check(reopened.Drafts.Get("compose")?.Data.Title == "unfinished" && reopened.Attachments.Has(item), "Draft lost on restart");
        reopened.Drafts.Save("compose", null); reopened.DetectMissingAttachments();
        Check(!reopened.Attachments.Has(item), "Unprotected historical original was not cleaned");
        return Task.CompletedTask;
    }
    static Task DraftCommit()
    {
        using var temp=new Sandbox();using var store=new TodoStore(temp.Path("target"));
        var item=Add(store,"draft only");store.SaveDraft("compose",new(new("draft",Attachments:[item]),[]));
        using(var db=new SqliteConnection("Pooling=False;Data Source="+store.Database.FilePath))
        {
            db.Open();using var command=db.CreateCommand();
            command.CommandText="CREATE TRIGGER refuse_send BEFORE INSERT ON revisions BEGIN SELECT RAISE(ABORT,'test'); END";command.ExecuteNonQuery();
            try{store.Save(Actor,"PC",new("sent",Attachments:[item]),draftKey:"compose");throw new Exception("Failed send was accepted");}catch(SqliteException){}
            Check(store.List().Length==0&&store.Drafts.Get("compose") is not null&&store.Attachments.Has(item),"Failed send consumed draft or original");
            command.CommandText="DROP TRIGGER refuse_send";command.ExecuteNonQuery();
        }
        store.Save(Actor,"PC",new("sent",Attachments:[item]),draftKey:"compose");
        Check(store.Drafts.Get("compose") is null&&store.List().Length==1&&store.Attachments.Has(item),"Committed send retained draft or removed referenced original");
        var orphan=Add(store,"never sent");store.SaveDraft("compose",new(new("",Attachments:[orphan]),[]));store.SaveDraft("compose",null);
        Check(!store.Attachments.Has(orphan)&&store.Attachments.Has(item),"Removing pending attachment left orphan or erased sent original");
        return Task.CompletedTask;
    }
    static async Task Watch()
    {
        using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
        using var ia = new DeviceIdentity(a.Database, "a"); using var ib = new DeviceIdentity(b.Database, "b");
        var settings = new ReplicaSettings(a.Database);
        await using var na = new PeerNode(a, ia, 0) { Replicas = settings }; await using var nb = new PeerNode(b, ib, 0);
        ia.Trust(ib.Id, ib.Name); ib.Trust(ia.Id, ia.Name); nb.Start(false);
        settings.SetEndpoint(ib.Id, "127.0.0.1:" + nb.Port); na.Start(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (na.SuccessfulSyncs == 0) await Task.Delay(50, deadline.Token);
        await Task.Delay(1000, deadline.Token); int connections = na.ConnectionsOpened;
        await Task.Delay(17000, deadline.Token);
        Check(na.ConnectionsOpened == connections, "Idle watch opened another TLS session");
        b.Save(ib.Id, ib.Name, new("remote change"));
        while (a.List().Length != 1) await Task.Delay(50, deadline.Token);
    }
}
