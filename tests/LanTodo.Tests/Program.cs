using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LanTodo.Core;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
string State(TodoStore store) => string.Join(";",store.List().OrderBy(v=>v.Id).Select(v=>v.Id+":"+string.Join(",",v.VersionIds)));
void Exchange(TodoStore a, TodoStore b) { a.Import(b.Export()); b.Import(a.Export()); Check(State(a) == State(b),"States did not converge"); }
const string Pc = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const string Phone = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
Revision Add(TodoStore s, string title = "提交报告", string actor = Pc) => s.Save(actor, actor == Pc ? "我的电脑" : "我的手机",new TodoData(title));
Revision Edit(TodoStore s, Revision r, string title, string actor = Pc, bool delete = false)
{
    var v = s.List().Single(x => x.Id == r.Body.TodoId);
    return s.Save(actor, actor == Pc ? "我的电脑" : "我的手机",v.Data with { Title = title, Deleted = delete },v.Id,v.VersionIds);
}

Test("A: offline CRUD survives restart", () =>
{
    using var temp = new Sandbox(); string id;
    using (var s = new TodoStore(temp.Path("a")))
    {
        var a = Add(s); id = a.Body.TodoId;
        s.Save(Pc,"电脑",new TodoData("年度报告","附上 Excel","2026-09-12","17:30",true),id,new[]{a.Id});
    }
    using var reopened = new TodoStore(temp.Path("a"));
    Check(reopened.List().Single().Data == new TodoData("年度报告","附上 Excel","2026-09-12","17:30",true));
    Check(reopened.History(id).Length == 2);
});
Test("E/F: concurrent creations, one-sided edits, idempotency", () =>
{
    using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
    var r = Add(a,"周五提交报告"); Add(b,"购买打印纸",Phone); Exchange(a,b);
    Edit(a,r,"周四提交报告"); Exchange(a,b); Exchange(b,a);
    Check(b.List().Any(t=>t.Data.Title=="周四提交报告")); Check(a.Export().Length==3);
});
Test("G: concurrent edit preserves both and resolution propagates", () =>
{
    using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
    var r = Add(a); Exchange(a,b); var pc=Edit(a,r,"周四提交报告"); var phone=Edit(b,r,"下周一提交报告",Phone);
    Exchange(a,b); var conflict=a.List().Single(); Check(conflict.Conflict); Check(conflict.Heads.Select(h=>h.Id).ToHashSet().SetEquals(new[]{pc.Id,phone.Id}));
    a.Save(Pc,"电脑",new TodoData("周日提交报告"),conflict.Id,conflict.VersionIds); Exchange(a,b);
    Check(!b.List().Single().Conflict); Check(b.List().Single().Data.Title=="周日提交报告"); Check(b.Export().Length==4);
});
Test("H: delete propagates, stale replicas cannot resurrect", () =>
{
    using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
    var r=Add(a); Exchange(a,b); var stale=b.Export(); Edit(a,r,r.Body.Data.Title,delete:true); Exchange(a,b); a.Import(stale);
    Check(a.List().Length==0 && b.List().Length==0); Check(a.Export().Length==2);
});
Test("I: delete/edit conflict keeps both, explicit deletion resolves", () =>
{
    using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
    var r=Add(a); Exchange(a,b); Edit(a,r,r.Body.Data.Title,delete:true); Edit(b,r,"手机离线重要修改",Phone); Exchange(a,b);
    var c=a.List().Single(); Check(c.Conflict && c.Heads.Any(h=>h.Body.Data.Deleted) && c.Heads.Any(h=>h.Body.Data.Title=="手机离线重要修改"));
    a.Save(Pc,"电脑",c.Heads.Single(h=>h.Body.Data.Deleted).Body.Data,c.Id,c.VersionIds); Exchange(a,b); Check(a.List().Length==0); Check(a.Export().Length==4);
});
Test("Stale editor never overwrites new arrivals", () =>
{
    using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
    var r=Add(a); Exchange(a,b); var before=a.List().Single(); Edit(b,r,"新版本",Phone); a.Import(b.Export());
    Throws<StaleEditException>(()=>a.Save(Pc,"电脑",new TodoData("旧编辑器"),before.Id,before.VersionIds));
    Check(a.List().Single().Data.Title=="新版本");
});
Test("Concurrent resolutions themselves create a new conflict", () =>
{
    using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); using var b = new TodoStore(temp.Path("b"));
    var r=Add(a); Exchange(a,b); Edit(a,r,"A"); Edit(b,r,"B",Phone); Exchange(a,b);
    var c=a.List().Single(); a.Save(Pc,"电脑",new TodoData("决定一"),c.Id,c.VersionIds); b.Save(Phone,"手机",new TodoData("决定二"),c.Id,c.VersionIds); Exchange(a,b);
    Check(a.List().Single().Heads.Length==2); Check(a.Export().Length==5);
});
Test("Clock skew never decides a winner", () =>
{
    using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); var r=Add(a);
    var older=Revision.Create(r.Body with { Nonce=Guid.NewGuid().ToString("N"),CreatedUtc="2000-01-01T00:00:00Z",Parents=new[]{r.Id},Data=new TodoData("旧时钟的新编辑") });
    var newer=Revision.Create(r.Body with { Nonce=Guid.NewGuid().ToString("N"),CreatedUtc="2099-01-01T00:00:00Z",Parents=new[]{r.Id},Data=new TodoData("新时钟的编辑") });
    a.Import(new[]{newer,older}); Check(a.List().Single().Conflict);
});
Test("Invalid, missing-parent, cross-task data rejected before writes", () =>
{
    using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a")); var r=Add(a); var other=Add(a,"另一项"); int count=a.Export().Length;
    Throws<InvalidDataException>(()=>a.Import(new[]{r with { Body=r.Body with { Data=new TodoData("篡改") } }}));
    var missing=Revision.Create(r.Body with { Parents=new[]{new string('f',64)},Nonce=Guid.NewGuid().ToString("N") });
    Throws<InvalidDataException>(()=>a.Import(new[]{missing}));
    var cross=Revision.Create(r.Body with { Parents=new[]{other.Id},Nonce=Guid.NewGuid().ToString("N") });
    Throws<InvalidDataException>(()=>a.Import(new[]{cross})); Check(a.Export().Length==count);
    Throws<InvalidDataException>(()=>a.Save(Pc,"电脑",new TodoData("日期错误",Date:"2026-02-30")));
});
Test("Partial transfer and reversed replay recover without duplicates", () =>
{
    using var temp = new Sandbox(); using var a = new TodoStore(temp.Path("a"));
    var r=Add(a); for(int i=0;i<80;i++) r=Edit(a,r,"版本 "+i);
    using (var b = new TodoStore(temp.Path("b"))) b.Import(a.Export().Take(31));
    using var reopened = new TodoStore(temp.Path("b")); reopened.Import(a.Export().Reverse()); reopened.Import(a.Export());
    Check(State(a)==State(reopened)); Check(reopened.Export().Length==81);
});
Test("Torn temporary write ignored; corrupt committed record fails closed", () =>
{
    using var temp = new Sandbox(); string path=temp.Path("a"); string id;
    using(var a=new TodoStore(path)) id=Add(a).Id;
    File.WriteAllText(System.IO.Path.Combine(path,"unrelated.tmp"),"{torn");
    using(var reopened=new TodoStore(path)) Check(reopened.Export().Length==1);
    using(var connection=new SqliteConnection("Pooling=False;Data Source="+Path.Combine(path,SqliteProfile.FileName)))
    { connection.Open();using var cmd=connection.CreateCommand();cmd.CommandText="UPDATE revisions SET payload=x'7B'";cmd.ExecuteNonQuery(); }
    Throws<System.Text.Json.JsonException>(()=>new TodoStore(path));
});
Test("Single-process lock prevents concurrent local writers", () =>
{
    using var temp=new Sandbox(); using var a=new TodoStore(temp.Path("a")); Throws<IOException>(()=>new TodoStore(temp.Path("a")));
});
Test("Backup restores full history, conflicts and tombstones", () =>
{
    using var temp=new Sandbox(); using var a=new TodoStore(temp.Path("a")); using var b=new TodoStore(temp.Path("b")); using var c=new TodoStore(temp.Path("c"));
    var r=Add(a); var deleted=Add(a,"删除项"); Exchange(a,b); Edit(a,r,"A"); Edit(b,r,"B",Phone); Edit(a,deleted,"删除项",delete:true); Exchange(a,b);
    a.Database.WriteMetadata("trusted.json", System.Text.Encoding.UTF8.GetBytes("source pairing"));
    c.Database.WriteMetadata("trusted.json", System.Text.Encoding.UTF8.GetBytes("destination pairing"));
    string path=temp.Path("backup.zip"); a.Backup(path); Check(c.Restore(path)==a.Export().Length); Check(State(c)==State(a)); Check(c.Restore(path)==0);
    Check(System.Text.Encoding.UTF8.GetString(c.Database.ReadMetadata("trusted.json")!) == "destination pairing", "Restore replaced local pairing");
    using var archive = System.IO.Compression.ZipFile.OpenRead(path);
    Check(archive.Entries.Count == a.Export().Length + 1);
    Check(archive.Entries.All(e => e.FullName == "manifest.json" || e.FullName.StartsWith("revisions/", StringComparison.Ordinal)), "Backup included device metadata");
});
Test("K: 1200 randomized offline mutations across 3 replicas converge", () =>
{
    using var temp=new Sandbox(); var stores=new[]{new TodoStore(temp.Path("a")),new TodoStore(temp.Path("b")),new TodoStore(temp.Path("c"))};
    try
    {
        var random=new Random(90210);
        for(int i=0;i<1200;i++)
        {
            var s=stores[random.Next(3)]; var views=s.List();
            if(views.Length==0 || random.Next(4)==0) Add(s,"任务 "+i);
            else { var v=views[random.Next(views.Length)]; s.Save(Pc,"测试设备",v.Data with { Title="修改 "+i,Deleted=random.Next(8)==0,Completed=random.Next(3)==0 },v.Id,v.VersionIds); }
            if(i%17==0) Exchange(stores[random.Next(3)],stores[random.Next(3)]);
        }
        Exchange(stores[0],stores[1]); Exchange(stores[1],stores[2]); Exchange(stores[0],stores[2]); Exchange(stores[0],stores[1]);
        Check(State(stores[0])==State(stores[1]) && State(stores[1])==State(stores[2]));
        Check(stores[0].Export().Length==1200,"A historical revision was lost");
    }
    finally { foreach(var s in stores) s.Dispose(); }
});
Test("Pair invitation parsing, single recipient, renewal, revocation persistence", () =>
{
    using var temp=new Sandbox();
    using(var db=new TodoStore(temp.Path("a")))
    using(var a=new DeviceIdentity(db.Database,"电脑"))
    {
        var invite=DeviceIdentity.ParseInvite(a.CreateInvite());
        Check(!a.Redeem(new string('0',64),Phone,"手机")); Check(a.Redeem(invite.Secret,Phone,"手机"));
        Check(!a.Redeem(invite.Secret,Pc,"陌生电脑")); Check(a.Redeem(invite.Secret,Phone,"手机"));
        a.Revoke(Phone); Check(!a.Redeem(invite.Secret,Phone,"手机"));
        var next=DeviceIdentity.ParseInvite(a.CreateInvite()); Check(a.Redeem(next.Secret,Pc,"新电脑")); a.Revoke(Pc);
    }
    using var reopenedDb=new TodoStore(temp.Path("a")); using var reopened=new DeviceIdentity(reopenedDb.Database,"电脑"); Check(reopened.Devices.Length==0);
});
AsyncTest("TLS C/D/G/I: real encrypted pairing + two-way sync + revocation", async () =>
{
    using var temp=new Sandbox(); using var a=new TodoStore(temp.Path("a")); using var b=new TodoStore(temp.Path("b"));
    using var ia=new DeviceIdentity(a.Database,"我的电脑"); using var ib=new DeviceIdentity(b.Database,"我的手机");
    await using var na=new PeerNode(a,ia,0); await using var nb=new PeerNode(b,ib,0); na.Start(false); nb.Start(false);
    Check(na.Port>0 && nb.Port>0,"Listener failed to start");
    var ea=new IPEndPoint(IPAddress.Loopback,na.Port); var eb=new IPEndPoint(IPAddress.Loopback,nb.Port);
    var ra=Add(a,"电脑任务"); Add(b,"手机任务",Phone);
    await nb.PairAtAsync(ea,DeviceIdentity.ParseInvite(ia.CreateInvite()));
    Check(ia.IsTrusted(ib.Id) && ib.IsTrusted(ia.Id)); Check(State(a)==State(b));
    Edit(a,ra,"电脑修改"); Edit(b,ra,"手机修改",Phone); await na.SyncAsync(eb,ib.Id);
    Check(a.List().Single(v=>v.Id==ra.Body.TodoId).Conflict); Check(State(a)==State(b));
    ia.Revoke(ib.Id); await ThrowsAsync<IOException>(()=>nb.SyncAsync(ea,ia.Id));
    Check(a.Export().Length==4);
});
AsyncTest("TLS denies unpaired reader even with client certificate", async () =>
{
    using var temp=new Sandbox(); using var s=new TodoStore(temp.Path("a")); using var ia=new DeviceIdentity(s.Database,"电脑"); using var strangerDb=new TodoStore(temp.Path("x")); using var stranger=new DeviceIdentity(strangerDb.Database,"陌生设备");
    Add(s,"私人内容"); await using var node=new PeerNode(s,ia,0); node.Start(false);
    using var tcp=new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback,node.Port);
    using var tls=new SslStream(tcp.GetStream(),false,(_,cert,_,_)=>cert is not null && DeviceIdentity.Fingerprint(cert)==ia.Id);
    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost="LanTodo",ClientCertificates=new X509CertificateCollection{stranger.Certificate},LocalCertificateSelectionCallback=(_,_,_,_,_)=>stranger.Certificate,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13 });
    await Wire.Write(tls,new("inventory"),CancellationToken.None); await ThrowsAsync<IOException>(()=>Wire.Read(tls,CancellationToken.None));
});
AsyncTest("TLS pin rejects an impersonated discovery endpoint", async () =>
{
    using var temp=new Sandbox(); using var a=new TodoStore(temp.Path("a")); using var b=new TodoStore(temp.Path("b")); using var ia=new DeviceIdentity(a.Database,"A"); using var ib=new DeviceIdentity(b.Database,"B");
    await using var na=new PeerNode(a,ia,0); await using var nb=new PeerNode(b,ib,0); nb.Start(false);
    ia.Trust(Phone,"假冒发现信息"); await ThrowsAsync<AuthenticationException>(()=>na.SyncAsync(new(IPAddress.Loopback,nb.Port),Phone));
});
AsyncTest("J: canceled network sync retries to converge with 300 revisions", async () =>
{
    using var temp=new Sandbox(); using var a=new TodoStore(temp.Path("a")); using var b=new TodoStore(temp.Path("b")); using var ia=new DeviceIdentity(a.Database,"A"); using var ib=new DeviceIdentity(b.Database,"B");
    ia.Trust(ib.Id,ib.Name); ib.Trust(ia.Id,ia.Name); for(int i=0;i<300;i++) Add(a,"任务 "+i);
    await using var na=new PeerNode(a,ia,0); await using var nb=new PeerNode(b,ib,0); na.Start(false); nb.Start(false);
    var endpoint=new IPEndPoint(IPAddress.Loopback,nb.Port); using var cts=new CancellationTokenSource();
    int original=b.Export().Length; b.Changed+=()=> { if(b.Export().Length>=24) cts.Cancel(); };
    await ThrowsAsync<OperationCanceledException>(()=>na.SyncAsync(endpoint,ib.Id,cts.Token));
    Check(b.Export().Length>original && b.Export().Length<300,"Failure did not occur mid-transfer");
    await na.SyncAsync(endpoint,ib.Id); Check(State(a)==State(b)); Check(b.Export().Length==300);
});
AsyncTest("Protocol rejects oversized frames and truncated messages", async () =>
{
    using var oversized=new MemoryStream(new byte[]{0x7f,0xff,0xff,0xff}); await ThrowsAsync<InvalidDataException>(()=>Wire.Read(oversized,CancellationToken.None));
    using var truncated=new MemoryStream(new byte[]{0,0,0,10,1,2}); await ThrowsAsync<EndOfStreamException>(()=>Wire.Read(truncated,CancellationToken.None));
});

AsyncTest("B: UDP discovery and automatic sync without endpoint configuration", async () =>
{
    using var temp=new Sandbox(); using var a=new TodoStore(temp.Path("a")); using var b=new TodoStore(temp.Path("b")); using var ia=new DeviceIdentity(a.Database,"A"); using var ib=new DeviceIdentity(b.Database,"B");
    ia.Trust(ib.Id,ib.Name); ib.Trust(ia.Id,ia.Name); Add(a,"自动发现电脑任务"); Add(b,"自动发现手机任务",Phone);
    await using var na=new PeerNode(a,ia,0); await using var nb=new PeerNode(b,ib,0); na.Start(); nb.Start();
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
    while(a.List().Length!=2 || b.List().Length!=2) await Task.Delay(200,deadline.Token);
    Check(State(a)==State(b)); Check(na.Nearby.Any(p=>p.Id==ib.Id) && nb.Nearby.Any(p=>p.Id==ia.Id));
});

AsyncTest("Pair once: persisted identity reconnects after both apps restart without any invite", async () =>
{
    using var temp = new Sandbox();
    string idA, idB;
    using (var a = new TodoStore(temp.Path("a")))
    using (var b = new TodoStore(temp.Path("b")))
    using (var ia = new DeviceIdentity(a.Database, "电脑"))
    using (var ib = new DeviceIdentity(b.Database, "手机"))
    {
        idA = ia.Id; idB = ib.Id;
        await using var na = new PeerNode(a, ia, 0);
        await using var nb = new PeerNode(b, ib, 0);
        na.Start(false);
        await nb.PairAtAsync(new(IPAddress.Loopback, na.Port), DeviceIdentity.ParseInvite(ia.CreateInvite()));
        ia.CancelInvite(); ib.CancelInvite();
    }
    using var reopenedA = new TodoStore(temp.Path("a"));
    using var reopenedB = new TodoStore(temp.Path("b"));
    using var identityA = new DeviceIdentity(reopenedA.Database, "电脑");
    using var identityB = new DeviceIdentity(reopenedB.Database, "手机");
    Check(identityA.Id == idA && identityB.Id == idB);
    Check(identityA.IsTrusted(idB) && identityB.IsTrusted(idA));
    Add(reopenedA, "重启后电脑新增"); Add(reopenedB, "重启后手机新增", Phone);
    await using var nodeA = new PeerNode(reopenedA, identityA, 0);
    await using var nodeB = new PeerNode(reopenedB, identityB, 0);
    nodeA.Start(); nodeB.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    while (reopenedA.List().Length != 2 || reopenedB.List().Length != 2) await Task.Delay(200, timeout.Token);
    Check(State(reopenedA) == State(reopenedB));
});

Test("Hourly reconciliation, immediate changes, retry backoff", () =>
{
    var policy = new SyncSchedule(); var now = DateTimeOffset.UtcNow;
    Check(policy.Due(1,"remote-1",now,TimeSpan.FromHours(1)));
    policy.Succeeded(1,"remote-1",now);
    Check(!policy.Due(1,"remote-1",now.AddMinutes(59),TimeSpan.FromHours(1)));
    Check(policy.Due(1,"remote-1",now.AddHours(1),TimeSpan.FromHours(1)));
    Check(!policy.Due(1,"remote-1",now.AddHours(1),TimeSpan.FromHours(2)));
    Check(policy.Due(2,"remote-1",now,TimeSpan.FromHours(2)));
    Check(policy.Due(1,"remote-2",now,TimeSpan.FromHours(2)));
    policy.Failed(now); Check(!policy.Due(2,"remote-2",now.AddSeconds(4),TimeSpan.FromHours(1)));
    Check(policy.Due(2,"remote-2",now.AddSeconds(5),TimeSpan.FromHours(1)));
});
AsyncTest("Idle peers stop syncing; edits wake both directions immediately", async () =>
{
    using var temp=new Sandbox(); using var a=new TodoStore(temp.Path("a")); using var b=new TodoStore(temp.Path("b"));
    using var ia=new DeviceIdentity(a.Database,"A");using var ib=new DeviceIdentity(b.Database,"B");ia.Trust(ib.Id,"B");ib.Trust(ia.Id,"A");
    await using var na=new PeerNode(a,ia,0);await using var nb=new PeerNode(b,ib,0);na.Start();nb.Start();
    Add(a,"首次变化");
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
    while(b.List().Length!=1)await Task.Delay(100,timeout.Token);
    await Task.Delay(2500,timeout.Token);
    int before=na.SuccessfulSyncs+nb.SuccessfulSyncs;
    await Task.Delay(6500,timeout.Token);
    Check(before==na.SuccessfulSyncs+nb.SuccessfulSyncs,"Idle replicas kept exchanging data");
    Add(b,"手机修改立即发出",Phone);
    using var immediate=new CancellationTokenSource(TimeSpan.FromSeconds(4));
    while(a.List().Length!=2)await Task.Delay(100,immediate.Token);
    Check(State(a)==State(b));
    await Task.Delay(1000,timeout.Token);
    int manualBefore=na.SuccessfulSyncs;
    na.RequestSync();
    using var manualTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));
    while(na.SuccessfulSyncs==manualBefore)await Task.Delay(100,manualTimeout.Token);
});
AsyncTest("Shutdown does not need a UI dispatcher to drain network continuations", async () =>
{
    using var temp=new Sandbox();using var s=new TodoStore(temp.Path("a"));using var identity=new DeviceIdentity(s.Database,"A");
    var node=new PeerNode(s,identity,0);var original=SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
    try { node.Start(); } finally { SynchronizationContext.SetSynchronizationContext(original); }
    await Task.Delay(200);
    await Task.Run(async()=>await node.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(3));
    // Network resources are actually released and the same node can restart.
    node.Start(); Check(node.Port>0);await node.DisposeAsync();
});

Test("Bulk completed deletion keeps history, skips new edits, and converges", () =>
{
    using var temp=new Sandbox(); using var a=new TodoStore(temp.Path("a")); using var b=new TodoStore(temp.Path("b"));
    var done=a.Save(Pc,"电脑",new TodoData("可清理",Completed:true));
    var edited=a.Save(Pc,"电脑",new TodoData("确认后被修改",Completed:true));
    Add(a,"未完成仍保留"); Exchange(a,b);
    var confirmed=a.List().Where(t=>t.Data.Completed).ToArray();
    Edit(b,edited,"手机刚补充的新内容",Phone); a.Import(b.Export());
    int signals=0; a.Changed+=()=>signals++;
    var result=a.DeleteCompleted(Pc,"电脑",confirmed);
    Check(result.Deleted==1 && result.Skipped==1); Check(signals==1);
    Check(a.History(done.Body.TodoId).Length==2 && a.History(done.Body.TodoId).Last().Body.Data.Deleted);
    Check(a.List().Any(t=>t.Data.Title=="手机刚补充的新内容")); Check(a.List().Any(t=>!t.Data.Completed));
    Exchange(a,b); Check(b.List().All(t=>t.Id!=done.Body.TodoId));
    var again=a.DeleteCompleted(Pc,"电脑",confirmed); Check(again.Deleted==0);
});
AsyncTest("Profile move preserves history, settings, identity and pairing and removes source", async () =>
{
    using var temp=new Sandbox();
    var source=temp.Path("source");var destination=temp.Path("destination");string identity;string before;
    await using(var runtime=new AppRuntime(source,"电脑"))
    {
        runtime.Identity.Trust(Phone,"手机"); runtime.SetReconcileHours(2);
        var todo=Add(runtime.Store,"迁移前内容");Edit(runtime.Store,todo,"保留完整历史");
        before=State(runtime.Store);identity=runtime.Identity.Id;
        ProfileMigration.MoveTo(runtime.Store,destination);
        Check(runtime.Store.Root==destination && State(runtime.Store)==before);
        Check(!File.Exists(Path.Combine(source,SqliteProfile.FileName)));
        Check(Directory.GetFiles(destination).Select(Path.GetFileName).SequenceEqual(new[]{SqliteProfile.FileName}));
        runtime.SetReconcileHours(1);runtime.SetReconcileHours(2);
    }
    await using var reopened=new AppRuntime(destination,"不应覆盖");
    Check(State(reopened.Store)==before && reopened.Identity.Id==identity && reopened.Identity.IsTrusted(Phone) && reopened.ReconcileHours==2);
});
Test("Profile migration refuses existing profiles and nested destinations", () =>
{
    using var temp=new Sandbox();using var source=new TodoStore(temp.Path("source"));Add(source,"不能丢失");
    Directory.CreateDirectory(temp.Path("occupied"));var sentinel=temp.Path("occupied/LanTodo.sqlite");File.WriteAllText(sentinel,"keep");
    Throws<IOException>(()=>ProfileMigration.MoveTo(source,temp.Path("occupied")));
    Throws<IOException>(()=>ProfileMigration.MoveTo(source,temp.Path("source/nested")));
    Throws<IOException>(()=>ProfileMigration.MoveTo(source,temp.Path("source")));
    Check(File.ReadAllText(sentinel)=="keep" && source.List().Length==1);
});
Test("Custom storage selection persists and missing storage never opens an empty replacement", () =>
{
    using var temp=new Sandbox();var config=temp.Path("location.json");var selected=temp.Path("custom");var fallback=temp.Path("default");
    Check(LanTodo.Windows.DataLocation.Read(config,fallback)==fallback);
    Directory.CreateDirectory(selected);File.WriteAllText(Path.Combine(selected,SqliteProfile.FileName),"test database placeholder");
    LanTodo.Windows.DataLocation.Save(selected,config);Check(LanTodo.Windows.DataLocation.Read(config,fallback)==selected);
    File.Delete(Path.Combine(selected,SqliteProfile.FileName));Throws<IOException>(()=>LanTodo.Windows.DataLocation.Read(config,fallback));
    Check(!Directory.Exists(fallback));
});
void Sql(string file, string sql)
{
    using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString());c.Open();
    using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();
}
Test("SQLite bulk import rolls back SQL and memory together on mid-batch failure", () =>
{
    using var temp=new Sandbox();using var source=new TodoStore(temp.Path("source"));using var target=new TodoStore(temp.Path("target"));
    Add(source,"first");Add(source,"second");int events=0;target.Changed+=()=>events++;
    Sql(target.Database.FilePath,"CREATE TRIGGER fail_second BEFORE INSERT ON revisions WHEN (SELECT count(*) FROM revisions)=1 BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
    Throws<SqliteException>(()=>target.Import(source.Export()));
    Check(target.Export().Length==0 && target.Database.ReadRevisions().Length==0 && events==0);
    Sql(target.Database.FilePath,"DROP TRIGGER fail_second");Check(target.Import(source.Export())==2 && events==1);
});
Test("SQLite bulk deletion is atomic under write failure", () =>
{
    using var temp=new Sandbox();using var s=new TodoStore(temp.Path("a"));
    s.Save(Pc,"电脑",new TodoData("one",Completed:true));s.Save(Pc,"电脑",new TodoData("two",Completed:true));
    Sql(s.Database.FilePath,"CREATE TRIGGER fail_delete BEFORE INSERT ON revisions WHEN (SELECT count(*) FROM revisions)=3 BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
    Throws<SqliteException>(()=>s.DeleteCompleted(Pc,"电脑",s.List()));
    Check(s.List().Length==2 && s.Export().Length==2 && s.Database.ReadRevisions().Length==2);
});
Test("A failed location activation keeps source writable and removes incomplete destination", () =>
{
    using var temp=new Sandbox();using var s=new TodoStore(temp.Path("source"));Add(s,"保留");var original=s.Database.FilePath;
    Throws<IOException>(()=>ProfileMigration.MoveTo(s,temp.Path("target"),()=>throw new IOException("locator cannot be written")));
    Check(File.Exists(original) && s.Database.FilePath==original && !File.Exists(Path.Combine(temp.Path("target"),SqliteProfile.FileName)));
    Add(s,"仍可写入");Check(s.List().Length==2);
});
Test("Future schema fails closed without rewriting database", () =>
{
    using var temp=new Sandbox();var path=temp.Path("a");string file;
    using(var s=new TodoStore(path)){Add(s);file=s.Database.FilePath;}
    Sql(file,"PRAGMA user_version=999");var before=File.ReadAllBytes(file);
    Throws<InvalidDataException>(()=>new TodoStore(path));Check(before.SequenceEqual(File.ReadAllBytes(file)));
});
Test("Legacy conversion retains history, pairing, settings and archived backups in one SQLite", () =>
{
    using var temp=new Sandbox();var legacy=temp.Path("legacy");Directory.CreateDirectory(Path.Combine(legacy,"revisions"));string id;Revision[] records;
    using(var original=new TodoStore(temp.Path("fixture")))
    using(var identity=new DeviceIdentity(original.Database,"旧电脑"))
    {
        identity.Trust(Phone,"旧手机");id=identity.Id;var first=Add(original);Edit(original,first,"旧版修改");records=original.Export();
        foreach(var r in records) File.WriteAllBytes(Path.Combine(legacy,"revisions",r.Id+".json"),JsonSerializer.SerializeToUtf8Bytes(r,Json.Options));
        foreach(var key in new[]{"identity.pfx","name.json","trusted.json"})File.WriteAllBytes(Path.Combine(legacy,key),original.Database.ReadMetadata(key)!);
    }
    File.WriteAllText(Path.Combine(legacy,"sync-settings.json"),"2");Directory.CreateDirectory(Path.Combine(legacy,"backups"));File.WriteAllBytes(Path.Combine(legacy,"backups","old.lantodo.zip"),new byte[]{1,2,3});
    using var converted=new TodoStore(legacy);using var reopenedIdentity=new DeviceIdentity(converted.Database,"不能覆盖");
    Check(converted.Export().Length==records.Length && converted.List().Single().Data.Title=="旧版修改");
    Check(reopenedIdentity.Id==id && reopenedIdentity.IsTrusted(Phone));Check(Json.Read<int>(converted.Database.ReadMetadata("sync-settings.json")!)==2);
    Check(converted.Database.ReadMetadata("legacy-backup/old.lantodo.zip")!.SequenceEqual(new byte[]{1,2,3}));
    Check(Directory.GetFileSystemEntries(legacy).Select(Path.GetFileName).SequenceEqual(new[]{SqliteProfile.FileName}));
});
Test("Invalid legacy history never publishes an empty database or removes originals", () =>
{
    using var temp=new Sandbox();var legacy=temp.Path("legacy");Directory.CreateDirectory(Path.Combine(legacy,"revisions"));var file=Path.Combine(legacy,"revisions","bad.json");File.WriteAllText(file,"{broken");
    Throws<JsonException>(()=>new TodoStore(legacy));Check(File.ReadAllText(file)=="{broken");Check(!File.Exists(Path.Combine(legacy,SqliteProfile.FileName)));
});
Test("SQLite contains native header and no persistent journal or identity files", () =>
{
    using var temp=new Sandbox();using var s=new TodoStore(temp.Path("a"));using var identity=new DeviceIdentity(s.Database,"电脑");Add(s);
    using(var headerFile=new FileStream(s.Database.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
    { byte[] header=new byte[16];headerFile.ReadExactly(header);Check(System.Text.Encoding.ASCII.GetString(header)=="SQLite format 3\0"); }
    Check(Directory.GetFiles(s.Root).Select(Path.GetFileName).SequenceEqual(new[]{SqliteProfile.FileName}));
    using var c=new SqliteConnection("Pooling=False;Data Source="+s.Database.FilePath);c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT sqlite_version()";
    Console.WriteLine("SQLite engine: "+cmd.ExecuteScalar());
});
Test("Move back beside EXE preserves unrelated files and removes custom locator", () =>
{
    using var temp=new Sandbox();var portable=temp.Path("portable");Directory.CreateDirectory(portable);
    var executable=Path.Combine(portable,"LanTodo.exe");File.WriteAllText(executable,"fixture");
    var config=Path.Combine(portable,"LanTodo.location");var custom=temp.Path("custom");
    using var source=new TodoStore(custom);Add(source,"移动测试");
    LanTodo.Windows.DataLocation.Save(custom,config);
    ProfileMigration.MoveTo(source,portable,()=>LanTodo.Windows.DataLocation.Save(portable,config));
    Check(!File.Exists(config) && source.Root==portable && File.ReadAllText(executable)=="fixture");
    Check(LanTodo.Windows.DataLocation.Read(config,portable)==portable);
});
Test("An unversioned foreign SQLite database is rejected without creating application tables", () =>
{
    using var temp=new Sandbox();var root=temp.Path("foreign");Directory.CreateDirectory(root);var file=Path.Combine(root,SqliteProfile.FileName);
    Sql(file,"CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('keep');");var before=File.ReadAllBytes(file);
    Throws<InvalidDataException>(()=>new TodoStore(root));Check(before.SequenceEqual(File.ReadAllBytes(file)));
});
int failed=0;
foreach(var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS  "+test.Name); }
    catch(Exception ex) { failed++; Console.WriteLine("FAIL  "+test.Name+"\n"+ex); }
}
Console.WriteLine($"\n{tests.Count-failed}/{tests.Count} tests passed.");
return failed==0 ? 0 : 1;

sealed class Sandbox : IDisposable
{
    private readonly string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"lantodo-tests-"+Guid.NewGuid().ToString("N"));
    public Sandbox() => Directory.CreateDirectory(root);
    public string Path(string name)=>System.IO.Path.Combine(root,name);
    public void Dispose() { if(root.StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"lantodo-tests-"),StringComparison.OrdinalIgnoreCase)) Directory.Delete(root,true); }
}

sealed class NonPumpingContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) { }
}
