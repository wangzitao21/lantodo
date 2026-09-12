using System.Text.Json;
using LanTodo.Core;

static class InteractionTests
{
    public static void Register(List<(string Name,Func<Task> Run)> tests)
    {
        tests.Add(("Messages: star/color survive edits, replication and backups; direct purge is atomic and rejects stale edits",Messages));
        tests.Add(("Presence: no peers, online with offline member, stopped network and reconnection",Presence));
    }
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static Task Messages()
    {
        using var temp=new Sandbox();using var a=new TodoStore(temp.Path("a"));using var b=new TodoStore(temp.Path("b"));
        const string actor="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        a.Save(actor,"PC",new("有日期的普通消息",Date:"2026-01-01"));
        using var bytes=new MemoryStream([1,2,3,4]);var audio=a.Attachments.Add(bytes,"语音-5秒.m4a","file");
        Check(audio.IsAudio,"Voice attachment was not recognized");
        Check(!JsonSerializer.Serialize(audio,Json.Options).Contains("isAudio"),"Computed audio flag changed revision hashes");
        var revision=a.Save(actor,"PC",new("语音 · 5秒",Attachments:[audio],Starred:true,Color:"mint"));
        Check(a.List()[0].Id==revision.Body.TodoId,"Starred message did not sort first");
        b.Import(a.Export());Check(b.List()[0].Data.Starred && b.List()[0].Data.Color=="mint","Style did not sync");
        var original=a.List()[0];a.Save(actor,"PC",original.Data with{Title="修改标题"},original.Id,original.VersionIds);
        try{a.Purge(actor,"PC",original);throw new Exception("Stale direct purge succeeded");}catch(StaleEditException){}
        var current=a.List()[0];Check(current.Data.Starred && current.Data.Color=="mint","Edit erased style");
        var backup=temp.Path("messages.zip");a.Backup(backup);using var restored=new TodoStore(temp.Path("restored"));restored.Restore(backup);
        Check(restored.List()[0].Data.Color=="mint" && restored.Attachments.Has(audio),"Backup lost style or audio");
        int before=a.Export().Length;a.Purge(actor,"PC",current);
        Check(a.Export().Length==before+1 && a.Trash().Length==0 && !a.List().Any(t=>t.Id==current.Id),"Direct deletion passed through trash");
        b.Import(a.Export());a.Import(restored.Export());
        Check(!a.List().Any(t=>t.Id==current.Id) && !b.List().Any(t=>t.Id==current.Id),"Stale replica resurrected direct deletion");
        Check(!a.Attachments.Has(audio),"Unused audio remained after purge");
        try{new TodoData("bad",Color:"#bad").Validate();throw new Exception("Invalid color accepted");}catch(System.IO.InvalidDataException){}
        return Task.CompletedTask;
    }
    static async Task Until(Func<bool> condition)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));while(!condition())await Task.Delay(100,timeout.Token);}
    static async Task Presence()
    {
        using var temp=new Sandbox();await using var a=new AppRuntime(temp.Path("a"),"PC",0);await using var b=new AppRuntime(temp.Path("b"),"Phone",0);
        using var offlineStore=new TodoStore(temp.Path("offline"));using var offline=new DeviceIdentity(offlineStore.Database,"Offline");
        Check(a.Status=="仅本机使用 · 已保存" && !a.IsOnline,"Offline status incorrect");
        await a.SetNetworkAsync(true);await b.SetNetworkAsync(true);
        Check(!a.IsOnline && a.Status=="仅本机使用 · 已保存","A listener alone claimed sync");
        await b.JoinSpaceAsync(a.Node.CreateInvite(),"127.0.0.1:"+a.Node.Port);
        a.Identity.Space!.Add(offline.Certificate,offline.Name);
        a.Store.Save(a.Identity.Id,a.Identity.Name,new("同步状态测试",Starred:true,Color:"sky"));
        await Until(()=>b.Store.List().Length==1 && a.Status.StartsWith("已同步") && b.Status.StartsWith("已同步"));
        Check(a.Node.DeviceState(b.Identity.Id)=="在线" && a.Node.DeviceState(offline.Id)=="当前不在线","Member presence included an offline member");
        await b.SetNetworkAsync(false);
        Check(!b.IsOnline && b.Status=="已保存到本机 · 等待设备上线","Stopped network retained green status");
        await Until(()=>!a.IsOnline && a.Status is "已保存到本机 · 等待设备上线" or "已保存到本机 · 正在连接设备");
        a.Store.Save(a.Identity.Id,a.Identity.Name,new("离线新增"));
        await b.SetNetworkAsync(true);a.RequestSync();b.RequestSync();
        await Until(()=>b.Store.List().Length==2 && a.Status.StartsWith("已同步") && b.Status.StartsWith("已同步"));
    }
}
