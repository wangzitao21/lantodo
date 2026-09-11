using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LanTodo.Core;

static class WorkspaceTests
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static async Task Until(Func<bool> condition){var end=DateTime.UtcNow.AddSeconds(25);while(!condition()){if(DateTime.UtcNow>end)throw new Exception("Timed out waiting for background sync");await Task.Delay(100);}}
    public static void Register(List<(string Name,Func<Task> Run)> tests)
    {
        tests.Add(("Workspaces: shared content relays across separate network groups without sharing membership",Spaces));
        tests.Add(("Workspaces: failed invitation retry reuses the prepared profile",Retry));
        tests.Add(("Workspaces: legacy isolated stores merge history, conflicts, originals and invalidations once",MergeLegacy));
        tests.Add(("Workspaces: interrupted legacy attachment migration preserves originals and can retry",RetryMigration));
        tests.Add(("Workspaces: complete profile relocation preserves all spaces and selection",Move));
        tests.Add(("Workspaces: deleting original, inactive and last networks preserves shared content",Delete));
        tests.Add(("Workspaces: deletion reaches offline members and a fresh invitation can rejoin",DeleteShared));
        if(OperatingSystem.IsWindows())tests.Add(("Workspaces: deleting network membership does not touch occupied attachments",DeleteOccupied));
        tests.Add(("Attachments: filesystem deletion wins before upload, relays offline, persists and explicit re-upload works",Missing));
        tests.Add(("Attachments: flat filename collisions and legacy folder migration preserve bytes",Flat));
        tests.Add(("Trash: restore, stale purge protection, clear and replay remain convergent",Trash));
    }
    static async Task Spaces()
    {
        using var temp=new Sandbox();string selected,phoneId;
        await using(var home=new AppRuntime(temp.Path("home"),"Home",0))
        await using(var work=new AppRuntime(temp.Path("work"),"Office",0))
        await using(var phone=new AppRuntime(temp.Path("phone"),"Phone",0))
        {
            home.RenameSpace("家里");work.RenameSpace("公司");
            home.Store.Save(home.Identity.Id,home.Identity.Name,new("家里清单"));work.Store.Save(work.Identity.Id,work.Identity.Name,new("公司清单"));
            phone.Store.Save(phone.Identity.Id,phone.Identity.Name,new("手机原有清单"));phoneId=phone.Identity.Id;
            var shared=phone.Store;
            await home.SetNetworkAsync(true);await work.SetNetworkAsync(true);await phone.SetNetworkAsync(true);
            await phone.JoinSpaceAsync(home.Node.CreateInvite(),"127.0.0.1:"+home.Node.Port);var homeKey=phone.ActiveSpaceKey;
            Check(phone.Identity.SpaceName=="家里","Invitation lost network name");
            await phone.JoinSpaceAsync(work.Node.CreateInvite(),"127.0.0.1:"+work.Node.Port);selected=phone.ActiveSpaceKey;
            Check(ReferenceEquals(shared,phone.Store),"Changing networks switched content store");
            await Until(()=>home.Store.List().Length==3 && work.Store.List().Length==3 && phone.Store.List().Length==3);
            Check(!home.Identity.Space!.Contains(work.Identity.Id) && !work.Identity.Space!.Contains(home.Identity.Id),"Content relay merged network membership");
            phone.Node.SetAdvertisedAddress("127.0.0.1:49900");phone.SelectSpace(homeKey);
            Check(phone.Node.AdvertisedAddress=="","Separate network routes share one metadata slot");phone.SelectSpace(selected);
            home.Store.Save(home.Identity.Id,home.Identity.Name,new("转发更新"));await Until(()=>work.Store.List().Length==4);
            using var bytes=new MemoryStream([1,4,9]);var file=home.Store.Attachments.Add(bytes,"bridge.txt");
            home.Store.Save(home.Identity.Id,home.Identity.Name,new("附件",Attachments:[file]));await Until(()=>work.Store.Attachments.Has(file));
            File.Delete(phone.Store.Attachments.PathFor(file.Hash));phone.Store.DetectMissingAttachments();
            await Until(()=>home.Store.Attachments.IsInvalid(file)&&work.Store.Attachments.IsInvalid(file));
            home.RenameSpace("家人");await Until(()=>phone.Spaces.Single(s=>s.Key==homeKey).Name=="家人");
            home.Identity.Revoke(phoneId);await Until(()=>!phone.Spaces.Single(s=>s.Key==homeKey).Member);
            Check(phone.Identity.Space!.Contains(phoneId),"Removal affected a different network");
            var view=phone.Store.List().Single(t=>t.Data.Title=="公司清单");phone.Store.Save(phoneId,phone.Identity.Name,view.Data with{Title="公司同步仍正常"},view.Id,view.VersionIds);
            await Until(()=>work.Store.List().Any(t=>t.Data.Title=="公司同步仍正常"));
            phone.SelectSpace("");Check(ReferenceEquals(shared,phone.Store)&&phone.Store.List().Length==5,"Network selection hid content");phone.SelectSpace(selected);
        }
        await using var reopened=new AppRuntime(temp.Path("phone"),"Ignored",0);
        Check(reopened.Spaces.Length==3 && reopened.ActiveSpaceKey==selected && reopened.Identity.Id==phoneId,"Restart lost network selection");
        Check(reopened.Store.List().Length==5 && reopened.Store.List().Any(t=>t.Data.Title=="公司同步仍正常"),"Restart lost shared content");
    }
    static async Task Retry()
    {
        using var temp=new Sandbox();await using var host=new AppRuntime(temp.Path("host"),"Host",0);await using var client=new AppRuntime(temp.Path("client"),"Client",0);
        await host.SetNetworkAsync(true);await client.SetNetworkAsync(true);
        var canceled=host.Node.CreateInvite();host.Identity.CancelInvite();
        try{await client.JoinSpaceAsync(canceled,"127.0.0.1:"+host.Node.Port);throw new Exception("Canceled invitation accepted");}catch(IOException){}
        Check(client.Spaces.Length==2 && client.ActiveSpaceKey=="","Failed join changed active space or lost prepared profile");
        await client.JoinSpaceAsync(host.Node.CreateInvite(),"127.0.0.1:"+host.Node.Port);
        Check(client.Spaces.Length==2 && client.Identity.Space!.Root==host.Identity.Space!.Root && host.Identity.IsTrusted(client.Identity.Id),"Retry skipped redemption or created duplicate space");
    }
    static async Task MergeLegacy()
    {
        using var temp=new Sandbox();var path=temp.Path("legacy");var key=Guid.NewGuid().ToString("N");var child=Path.Combine(path,"spaces",key);
        Attachment first,second;string id;
        using(var root=new TodoStore(path))
        using(var identity=new DeviceIdentity(root.Database,"PC"))
        using(var old=new TodoStore(child))
        {
            identity.EnsureSpace();id=identity.Id;
            old.Database.WriteMetadata("identity.pfx",root.Database.ReadMetadata("identity.pfx")!);
            using var childIdentity=new DeviceIdentity(old.Database,"PC");childIdentity.EnsureSpace();childIdentity.RenameSpace("公司");
            using var one=new MemoryStream([1,2,3]);using var two=new MemoryStream([9,8,7]);
            first=root.Attachments.Add(one,"same.jpg");second=old.Attachments.Add(two,"same.jpg");
            var initial=root.Save(id,"PC",new("共同起点",Attachments:[first]));old.Import(root.Export());
            root.Save(id,"PC",initial.Body.Data with{Title="根目录的修改"},initial.Body.TodoId,[initial.Id]);
            old.Save(id,"PC",initial.Body.Data with{Title="旧空间的修改",Attachments=[second]},initial.Body.TodoId,[initial.Id]);
            old.Save(id,"PC",new("旧回收站",Deleted:true,Attachments:[second]));
            File.Delete(root.Attachments.PathFor(first.Hash));root.DetectMissingAttachments();
            root.Database.WriteMetadata("spaces-catalog.json",JsonSerializer.SerializeToUtf8Bytes(new{active=key,keys=new[]{key}},Json.Options));
        }
        int versions;
        await using(var app=new AppRuntime(path,"PC",0))
        {
            versions=app.Store.Export().Length;
            Check(versions==4 && app.Store.List().Single().Conflict && app.Store.Trash().Length==1,"Migration lost history, conflict or trash");
            Check(app.Store.Attachments.IsInvalid(first) && app.Store.Attachments.Has(second),"Migration lost attachment availability state");
            Check(Path.GetDirectoryName(app.Store.Attachments.PathFor(second.Hash))==Path.Combine(path,"attachments"),"Migrated original not in shared attachment folder");
            Check(File.ReadAllBytes(app.Store.Attachments.PathFor(second.Hash)).SequenceEqual(new byte[]{9,8,7}),"Migration changed original bytes");
            app.SelectSpace("");Check(app.Store.Export().Length==versions,"Network selection changed migrated history");
            app.SelectSpace(key);Check(app.BackupSpaces(temp.Path("backup")).Length==1,"Multiple networks produced duplicate backups");
        }
        using(var old=new TodoStore(child))Check(old.Export().Length==0,"Migrated revisions still stored separately");
        await using var reopened=new AppRuntime(path,"PC",0);
        Check(reopened.Store.Export().Length==versions && reopened.Store.Attachments.Has(second),"Second startup duplicated or lost migrated content");
    }
    static async Task RetryMigration()
    {
        using var temp=new Sandbox();var path=temp.Path("retry-migration");var key=Guid.NewGuid().ToString("N");var child=Path.Combine(path,"spaces",key);string original;
        using(var root=new TodoStore(path))
        using(var identity=new DeviceIdentity(root.Database,"PC"))
        using(var old=new TodoStore(child))
        {
            identity.EnsureSpace();old.Database.WriteMetadata("identity.pfx",root.Database.ReadMetadata("identity.pfx")!);
            using var bytes=new MemoryStream([1,2,3]);var item=old.Attachments.Add(bytes,"retry.txt");original=old.Attachments.PathFor(item.Hash);
            old.Save(identity.Id,identity.Name,new("keep original",Attachments:[item]));
            File.WriteAllBytes(original,[4,5,6]);
            root.Database.WriteMetadata("spaces-catalog.json",JsonSerializer.SerializeToUtf8Bytes(new{active=key,keys=new[]{key}},Json.Options));
        }
        try { await using var failed=new AppRuntime(path,"PC",0);throw new Exception("Corrupt original migrated"); }catch(IOException){}
        using(var old=new TodoStore(child))Check(old.Export().Length==1 && File.Exists(original),"Failed migration erased source");
        File.WriteAllBytes(original,[1,2,3]);
        await using var app=new AppRuntime(path,"PC",0);
        Check(app.Store.List().Single().Data.Title=="keep original" && app.Store.Attachments.Has(app.Store.ActiveAttachments().Single()),"Migration retry failed");
    }
    static async Task Move()
    {
        using var temp=new Sandbox();var destination=temp.Path("moved");string key;
        await using(var app=new AppRuntime(temp.Path("old"),"PC",0))
        {
            app.Store.Save(app.Identity.Id,app.Identity.Name,new("one"));await app.CreateSpaceAsync("two");key=app.ActiveSpaceKey;
            using var bytes=new MemoryStream([1,2,3]);var item=app.Store.Attachments.Add(bytes,"note.txt");app.Store.Save(app.Identity.Id,app.Identity.Name,new("two",Attachments:[item]));
            await app.MoveToAsync(destination);Check(app.ProfileRoot==destination && app.Store.Attachments.Has(item),"Move lost active attachment");
        }
        await using var reopened=new AppRuntime(destination,"PC",0);Check(reopened.ActiveSpaceKey==key && reopened.Store.List().Any(t=>t.Data.Title=="two"),"Move lost space selection");
        reopened.SelectSpace("");Check(reopened.Store.List().Any(t=>t.Data.Title=="one"),"Move lost original space");
    }
    static async Task Missing()
    {
        using var temp=new Sandbox();using var pc=new TodoStore(temp.Path("pc"));using var phone=new TodoStore(temp.Path("phone"));using var offline=new TodoStore(temp.Path("offline"));
        using var ip=new DeviceIdentity(pc.Database,"PC");using var im=new DeviceIdentity(phone.Database,"Phone");
        await using var np=new PeerNode(pc,ip,0);await using var nm=new PeerNode(phone,im,0);np.Start(false);nm.Start(false);ip.Trust(im.Id,im.Name);im.Trust(ip.Id,ip.Name);
        var data=RandomNumberGenerator.GetBytes(AttachmentStore.ChunkSize+137);using var input=new MemoryStream(data);var item=phone.Attachments.Add(input,"image.jpg");
        phone.Save(im.Id,im.Name,new("照片",Attachments:[item]));await nm.SyncAsync(new(IPAddress.Loopback,np.Port),ip.Id);
        offline.Import(phone.Export());using(var original=new MemoryStream(data))offline.Attachments.Add(original,item.Name,item.Kind);
        offline.DetectMissingAttachments();File.Delete(pc.Attachments.PathFor(item.Hash));
        // No watcher delay: even an upload initiated immediately by the other peer must observe the deletion.
        await nm.SyncAsync(new(IPAddress.Loopback,np.Port),ip.Id);
        Check(!File.Exists(pc.Attachments.PathFor(item.Hash)) && pc.Attachments.IsInvalid(item) && phone.Attachments.IsInvalid(item),"Phone repaired deleted PC file");
        offline.Attachments.Invalidate(phone.Attachments.InvalidKeys);Check(!offline.Attachments.Has(item),"Offline peer failed to invalidate");
        var backup=temp.Path("invalid.zip");pc.Backup(backup);using var restored=new TodoStore(temp.Path("restored"));restored.Restore(backup);Check(restored.Attachments.IsInvalid(item),"Backup lost deletion state");
        using var again=new MemoryStream(data);var fresh=phone.Attachments.Add(again,"image.jpg");Check(fresh.Key!=item.Key && phone.Attachments.Has(fresh),"Explicit new upload cannot reuse original content");
        phone.Save(im.Id,im.Name,new("重新上传",Attachments:[fresh]));await nm.SyncAsync(new(IPAddress.Loopback,np.Port),ip.Id);
        Check(pc.Attachments.Has(fresh)&&pc.Attachments.IsInvalid(item),"New upload repaired old reference or remained invalid");
        pc.DetectMissingAttachments();await nm.SyncAsync(new(IPAddress.Loopback,np.Port),ip.Id);Check(pc.Attachments.Has(fresh),"New reference was mistaken for old deletion");
    }
    static async Task Delete()
    {
        using var temp=new Sandbox();var path=temp.Path("delete");string remaining;
        await using(var app=new AppRuntime(path,"PC",0))
        {
            var original=app.Store;using var bytes=new MemoryStream([8,3,1]);var item=original.Attachments.Add(bytes,"original.txt");
            original.Save(app.Identity.Id,app.Identity.Name,new("shared history",Attachments:[item]));
            await app.CreateSpaceAsync("公司");var company=app.ActiveSpaceKey;app.Store.Save(app.Identity.Id,app.Identity.Name,new("keep"));
            await app.SetNetworkAsync(true);await app.DeleteSpaceAsync("");
            Check(app.Spaces.Length==1 && app.ActiveSpaceKey==company && app.Store.List().Length==2,"Deleting original network erased content");
            Check(ReferenceEquals(original,app.Store)&&original.Attachments.Has(item),"Deleting network changed store or attachment");
            await app.CreateSpaceAsync("临时");await app.DeleteSpaceAsync(app.ActiveSpaceKey);
            Check(app.ActiveSpaceKey==company,"Deleting active network did not select surviving network");
            await app.DeleteSpaceAsync(company);remaining=app.ActiveSpaceKey;
            Check(app.Spaces.Length==1 && app.Store.Export().Length==2 && app.Store.Attachments.Has(item),"Last network deletion lost content");
            app.Store.Save(app.Identity.Id,app.Identity.Name,new("still editable"));
        }
        await using var reopened=new AppRuntime(path,"PC",0);
        Check(reopened.Spaces.Length==1 && reopened.ActiveSpaceKey==remaining && reopened.Store.Export().Length==3,"Restart lost content after network deletion");
        Check(reopened.BackupSpaces(temp.Path("backups")).Length==1,"Shared content created duplicate backups");
    }
    static async Task DeleteShared()
    {
        using var temp=new Sandbox();var localPath=temp.Path("departing");string id,remoteRoot;
        await using var remote=new AppRuntime(temp.Path("remote"),"Remote",0);await remote.SetNetworkAsync(true);
        await using(var local=new AppRuntime(localPath,"Local",0))
        {
            await local.SetNetworkAsync(true);id=local.Identity.Id;remoteRoot=local.Identity.Space!.Root;
            local.Store.Save(id,local.Identity.Name,new("shared original"));
            await remote.JoinSpaceAsync(local.Node.CreateInvite(),"127.0.0.1:"+local.Node.Port);
            await Until(()=>remote.Store.List().Length==1);
            await remote.SetNetworkAsync(false);
            await local.DeleteSpaceAsync("");Check(local.Spaces.All(s=>s.Root!=remoteRoot),"Deleted original remains selectable");
        }
        await using var reopened=new AppRuntime(localPath,"Local",0);await reopened.SetNetworkAsync(true);await remote.SetNetworkAsync(true);
        remote.Replicas.SetEndpoint(id,"127.0.0.1:"+reopened.Node.Port);remote.RequestSync();
        await Until(()=>!remote.Identity.Space!.Contains(id));
        Check(remote.Store.List().Single().Data.Title=="shared original","Local deletion erased another member's data");
        var invite=remote.Node.CreateInvite();await reopened.JoinSpaceAsync(invite,"127.0.0.1:"+remote.Node.Port);
        await Until(()=>reopened.Store.List().Length==1);
        Check(reopened.Identity.Space!.Root==remoteRoot && reopened.Spaces.Length==2 && remote.Identity.Space!.Contains(id),"Fresh invitation failed to reactivate deleted membership");
    }
    static async Task DeleteOccupied()
    {
        using var temp=new Sandbox();var path=temp.Path("occupied");
        await using(var app=new AppRuntime(path,"PC",0))
        {
            using var bytes=new MemoryStream([5,6]);var item=app.Store.Attachments.Add(bytes,"open.txt");
            app.Store.Save(app.Identity.Id,app.Identity.Name,new("keep",Attachments:[item]));
            using var locked=new FileStream(app.Store.Attachments.PathFor(item.Hash),FileMode.Open,FileAccess.Read,FileShare.Read);
            await app.DeleteSpaceAsync("");
            Check(app.Store.Export().Length==1 && app.Store.Attachments.Has(item),"Network deletion touched an open attachment");
        }
        await using var reopened=new AppRuntime(path,"PC",0);Check(reopened.Store.Export().Length==1,"Network deletion lost content on restart");
    }
    static Task Flat()
    {
        using var temp=new Sandbox();var path=temp.Path("flat");Attachment first,second;
        using(var store=new TodoStore(path))
        {
            using var a=new MemoryStream([1,2]);using var b=new MemoryStream([3,4]);first=store.Attachments.Add(a,"相片.jpg");second=store.Attachments.Add(b,"相片.jpg");
            Check(Path.GetDirectoryName(store.Attachments.PathFor(first.Hash))==store.Attachments.DirectoryPath && Path.GetDirectoryName(store.Attachments.PathFor(second.Hash))==store.Attachments.DirectoryPath,"Per-hash folders remain");
            Check(Path.GetFileName(store.Attachments.PathFor(first.Hash))=="相片.jpg" && Path.GetFileName(store.Attachments.PathFor(second.Hash))=="相片 (1).jpg","Collision overwrote original name");
            store.Save(new string('a',64),"PC",new("files",Attachments:[first,second]));
        }
        using(var reopened=new TodoStore(path))
        {
            Check(File.ReadAllBytes(reopened.Attachments.PathFor(first.Hash)).SequenceEqual(new byte[]{1,2}),"Flat index did not survive restart");
            File.Move(reopened.Attachments.PathFor(first.Hash),Path.Combine(path,".transfers",first.Hash+".ready"));
        }
        using(var recovered=new TodoStore(path)){Check(recovered.Attachments.Has(first)&&!recovered.Attachments.IsInvalid(first),"Interrupted atomic commit mistaken for deletion");File.Delete(recovered.Attachments.PathFor(first.Hash));}
        using(var deleted=new TodoStore(path))Check(deleted.Attachments.IsInvalid(first),"Offline filesystem deletion not detected on restart");
        var legacy=temp.Path("legacy");var bytes=new byte[]{8,9};var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();var old=new Attachment(hash,"旧照片.jpg",2,"image");
        using(var store=new TodoStore(legacy)){store.Save(new string('a',64),"PC",new("old",Attachments:[old]));Directory.CreateDirectory(Path.Combine(store.Attachments.DirectoryPath,hash));File.WriteAllBytes(Path.Combine(store.Attachments.DirectoryPath,hash,old.Name),bytes);}
        using(var upgraded=new TodoStore(legacy))Check(upgraded.Attachments.Has(old)&&!Directory.Exists(Path.Combine(upgraded.Attachments.DirectoryPath,hash))&&Path.GetFileName(upgraded.Attachments.PathFor(hash))==old.Name,"Legacy original not migrated");
        return Task.CompletedTask;
    }
    static Task Trash()
    {
        using var temp=new Sandbox();using var a=new TodoStore(temp.Path("a"));using var b=new TodoStore(temp.Path("b"));var actor=new string('a',64);
        var first=a.Save(actor,"PC",new("恢复测试"));a.Save(actor,"PC",first.Body.Data with{Deleted=true},first.Body.TodoId,[first.Id]);b.Import(a.Export());
        Check(a.List().Length==0 && b.Trash().Length==1,"Delete not in remote trash");var trashed=b.Trash().Single();b.Save(actor,"Phone",trashed.Data with{Deleted=false},trashed.Id,trashed.VersionIds);a.Import(b.Export());
        try{a.Purge(actor,"PC",trashed);throw new Exception("Stale purge erased restored task");}catch(StaleEditException){}
        var view=a.List().Single();a.Save(actor,"PC",view.Data with{Deleted=true},view.Id,view.VersionIds);a.Purge(actor,"PC",a.Trash().Single());b.Import(a.Export());a.Import(b.Export());
        Check(a.List().Length==0 && a.Trash().Length==0 && b.Trash().Length==0,"Purged task resurrected");return Task.CompletedTask;
    }
}
