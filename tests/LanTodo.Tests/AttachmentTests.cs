using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LanTodo.Core;

static class AttachmentTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("Attachments: resume, tampering, backup, folder structure, migration and legacy JSON", Storage));
        tests.Add(("Attachments: actual TLS upload, resumed download, NAS relay, empty files and source attribution", Relay));
        tests.Add(("Attachments: deletion, shared originals, partial cleanup, backup and offline conflict", Deletion));
        tests.Add(("Invites: retire/delete history, revoke, restart and source preference", Invites));
    }
    static Task Storage()
    {
        using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
        Check(JsonSerializer.Serialize(new TodoData("old"),Json.Options)=="{\"title\":\"old\",\"notes\":\"\",\"date\":null,\"time\":null,\"completed\":false,\"deleted\":false}","Legacy revision serialization changed");
        var bytes = RandomNumberGenerator.GetBytes(AttachmentStore.ChunkSize + 789);
        using var input = new MemoryStream(bytes); var item = a.Attachments.Add(input,"原始文件.bin");
        b.Attachments.Receive(item,0,bytes[..AttachmentStore.ChunkSize]);
        Check(!b.Attachments.Has(item) && b.Attachments.Received(item)==AttachmentStore.ChunkSize,"Partial file exposed");
        b.Attachments.Receive(item,AttachmentStore.ChunkSize,bytes[AttachmentStore.ChunkSize..]);
        Check(File.ReadAllBytes(b.Attachments.PathFor(item.Hash)).SequenceEqual(bytes),"Original bytes changed");
        File.Delete(b.Attachments.PathFor(item.Hash));
        Check(!b.Attachments.Has(item) && b.Attachments.Availability(item).StartsWith("已失效"), "External deletion not distinguished from pending sync");
        using (var duplicate = new MemoryStream(bytes))
        {
            var renamed = a.Attachments.Add(duplicate, "同内容另一个名字.bin");
            Check(renamed.Hash == item.Hash && Directory.GetFiles(Path.GetDirectoryName(a.Attachments.PathFor(item.Hash))!).Length == 1, "Duplicate import wrote a second original");
        }
        var bad = item with { Hash = new string('a',64), Size=3 };
        try { b.Attachments.Receive(bad,0,[1,2,3]); throw new Exception("Accepted wrong hash"); } catch (InvalidDataException) { }
        Check(!b.Attachments.Has(bad) && b.Attachments.Received(bad)==0,"Corrupt partial not reset");
        try { (item with { Name="../escape" }).Validate(); throw new Exception("Traversal accepted"); } catch(InvalidDataException) { }
        var folder = temp.Path("folder"); Directory.CreateDirectory(Path.Combine(folder,"empty")); File.WriteAllBytes(Path.Combine(folder,"data.bin"),bytes);
        var packed=a.Attachments.AddPath(folder);
        using(var zip=ZipFile.OpenRead(a.Attachments.PathFor(packed.Hash)))
        { Check(zip.GetEntry("empty/") is not null,"Empty directory lost");var entry=zip.GetEntry("data.bin")!;Check(entry.Length==entry.CompressedLength,"Folder was compressed"); }
        a.Save(new string('b',64),"PC",new("附件",Attachments:[item,packed]));
        var longest = new TodoData(new string('中',500),new string('文',32000),Attachments:Enumerable.Range(0,32).Select(_=>item with { Name=new string('图',255) }).ToArray());
        longest.Validate();
        for(int i=0;i<4;i++) a.Save(new string('b',64),"PC",longest);
        var frame=JsonSerializer.SerializeToUtf8Bytes(new Packet("put",Revisions:a.Export().TakeLast(4).ToArray()),Json.Options);
        Check(frame.Length<=Wire.MaxFrame,"Valid maximum metadata exceeds protocol frame");
        var backup=temp.Path("backup.zip");a.Backup(backup);b.Restore(backup);
        Check(b.Attachments.Has(packed),"Backup lost attachments");
        using var c = new TodoStore(temp.Path("c")); c.Import(a.Export());
        try { c.Backup(temp.Path("incomplete.zip")); throw new Exception("Incomplete backup succeeded"); } catch(IOException) { }
        ProfileMigration.MoveTo(a,temp.Path("moved"));Check(a.Attachments.Has(item),"Move lost attachment path");
        return Task.CompletedTask;
    }
    static async Task Relay()
    {
        using var temp = new Sandbox();using var a=new TodoStore(temp.Path("phone"));using var b=new TodoStore(temp.Path("nas"));using var c=new TodoStore(temp.Path("pc"));
        using var ia=new DeviceIdentity(a.Database,"Phone");using var ib=new DeviceIdentity(b.Database,"NAS");using var ic=new DeviceIdentity(c.Database,"PC");
        await using var na=new PeerNode(a,ia,0);await using var nb=new PeerNode(b,ib,0);await using var nc=new PeerNode(c,ic,0);
        na.Start(false);nb.Start(false);nc.Start(false);ia.Trust(ib.Id,ib.Name);ib.Trust(ia.Id,ia.Name);ic.Trust(ib.Id,ib.Name);ib.Trust(ic.Id,ic.Name);
        var bytes=RandomNumberGenerator.GetBytes(2*1024*1024+371);using var stream=new MemoryStream(bytes);var item=a.Attachments.Add(stream,"original.png");
        using var empty=new MemoryStream();var zero=a.Attachments.Add(empty,"empty.txt");
        a.Save(ia.Id,ia.Name,new("发送原件",Attachments:[item,zero]));
        await na.SyncAsync(new(IPAddress.Loopback,nb.Port),ib.Id);
        Check(b.Attachments.Has(item),"Upload missing");await na.DisposeAsync();
        c.Attachments.Receive(item,0,bytes[..AttachmentStore.ChunkSize]);
        await nc.SyncAsync(new(IPAddress.Loopback,nb.Port),ib.Id);
        Check(File.ReadAllBytes(c.Attachments.PathFor(item.Hash)).SequenceEqual(bytes)&&c.Attachments.Has(zero),"Relay changed bytes or lost empty file");
        var view=c.List().Single();Check(view.Heads[0].Body.DeviceName=="Phone","NAS replaced source");
        c.Save(ic.Id,ic.Name,view.Data with { Title="电脑修改" },view.Id,view.VersionIds);
        await nc.SyncAsync(new(IPAddress.Loopback,nb.Port),ib.Id);
        Check(b.List().Single().Heads[0].Body.DeviceName=="PC"&&b.List().Single().Data.Attachments!.Length==2,"Edit lost attachment/source");
        view=c.List().Single();c.Save(ic.Id,ic.Name,view.Data with { Deleted=true },view.Id,view.VersionIds);
        await nc.SyncAsync(new(IPAddress.Loopback,nb.Port),ib.Id);
        await nc.SyncAsync(new(IPAddress.Loopback,nb.Port),ib.Id);
        Check(b.Attachments.Has(item)&&c.Attachments.Has(item)&&b.Trash().Length==1,"Trash must retain restorable originals");
        c.Purge(ic.Id,ic.Name,c.Trash().Single());
        await nc.SyncAsync(new(IPAddress.Loopback,nb.Port),ib.Id);
        Check(!b.Attachments.Has(item)&&!c.Attachments.Has(item)&&b.Trash().Length==0,"Purge must remove unused originals");
    }
    static Task Deletion()
    {
        using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
        var actor = new string('a',64); var bytes = new byte[] { 1, 2, 3, 4 };
        using var input = new MemoryStream(bytes); var item = a.Attachments.Add(input,"原件.png");
        Check(Path.GetFileName(a.Attachments.PathFor(item.Hash)) == item.Name,"Original filename missing");
        var first=a.Save(actor,"PC",new("first",Attachments:[item]));
        var second=a.Save(actor,"PC",new("second",Attachments:[item]));
        b.Import(a.Export()); b.Attachments.Receive(item,0,bytes);
        a.Save(actor,"PC",first.Body.Data with { Deleted=true },first.Body.TodoId,[first.Id]);
        Check(a.Attachments.Has(item),"Shared original deleted while another message uses it");
        a.Save(actor,"PC",second.Body.Data with { Deleted=true },second.Body.TodoId,[second.Id]);
        Check(a.Attachments.Has(item)&&a.Trash().Length==2,"Trash lost originals");
        foreach(var trash in a.Trash())a.Purge(actor,"PC",trash);
        Check(!a.Attachments.Has(item),"Purged original retained");
        b.Import(a.Export());Check(!b.Attachments.Has(item),"Remote deletion retained original");
        var zip=temp.Path("deleted.zip");a.Backup(zip);
        using(var archive=ZipFile.OpenRead(zip))Check(!archive.Entries.Any(e=>e.FullName.StartsWith("attachments/")),"Deleted bytes in new backup");
        using var restored=new TodoStore(temp.Path("restored"));restored.Restore(zip);Check(restored.List().Length==0,"Backup revived deleted messages");
        // An offline edit remains a live conflict head and must keep its original.
        var offline=Revision.Create(new RevisionBody(1,first.Body.TodoId,actor,"Phone",Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToString("O"),[first.Id],first.Body.Data with { Title="offline" }));
        a.Import([offline]);a.Attachments.Receive(item,0,bytes);
        Check(a.List().Single().Conflict && a.Attachments.Has(item),"Offline conflict lost live original");
        var view=a.List().Single();a.Save(actor,"PC",view.Heads.Single(h=>!h.Body.Data.Purged).Body.Data with { Deleted=true },view.Id,view.VersionIds);
        Check(a.Attachments.Has(item),"Trash conflict resolution lost original");
        a.Purge(actor,"PC",a.Trash().Single());
        Check(!a.Attachments.Has(item),"Purged conflict retained original");
        return Task.CompletedTask;
    }
    static async Task Invites()
    {
        using var temp=new Sandbox();var path=temp.Path("app"); using var phoneStore = new TodoStore(temp.Path("phone")); using var phone = new DeviceIdentity(phoneStore.Database,"Phone");
        await using(var app=new AppRuntime(path,"PC"))
        {
            Check(app.ShowDeviceSource,"Source default missing");app.SetShowDeviceSource(false);
            var code=DeviceIdentity.ParseSpaceInvite(app.Identity.CreateInvite());var first=app.Identity.Invites.Single();app.Identity.CreateInvite();
            Check(app.Identity.Invites.First().State=="已作废","Old code not retired");app.Identity.DeleteInviteRecord(first.Id);
            try { app.Identity.RedeemSpace(code.Secret,phone.Certificate,phone.Name); throw new Exception("Deleted code accepted"); } catch (UnauthorizedAccessException) { }
            var next=DeviceIdentity.ParseSpaceInvite(app.Identity.CreateInvite());app.Identity.RedeemSpace(next.Secret,phone.Certificate,phone.Name);
            var used=app.Identity.Invites.Single(r=>r.State=="已使用");app.Identity.DeleteInviteRecord(used.Id);
            Check(app.Identity.IsTrusted(phone.Id),"Deleting record revoked device");app.Identity.Revoke(phone.Id);
            Check(app.Identity.Devices.Length==0 && app.Identity.WasDeleted(phone.Id),"Removed member still visible");app.Identity.CreateInvite();
        }
        await using var reopened=new AppRuntime(path,"PC");Check(!reopened.ShowDeviceSource,"Preference lost");Check(reopened.Identity.Invites.All(i=>i.State!="待绑定"),"Restart revived secret");
    }
}
