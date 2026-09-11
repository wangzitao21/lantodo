using System.Text.Json;

namespace LanTodo.Core;

public sealed record SpaceInfo(string Key, string Name, string? Root, bool Selected, bool Member, int Devices, string Status);
public sealed partial class AppRuntime : IAsyncDisposable
{
    private sealed record Session(string Key, TodoStore Store, DeviceIdentity Identity, PeerNode Node, ReplicaSettings Replicas)
    {
        public bool Deleted => Store.Database.ReadMetadata("network-space-deleted") is [1];
    }
    private sealed record Catalog(string Active, string[] Keys);
    private readonly SemaphoreSlim networkGate = new(1);
    private readonly object gate = new();
    private readonly List<Session> sessions = new();
    private readonly Session primary;
    private volatile Session active;
    private bool networkEnabled;
    public event Action? DataChanged;
    public event Action? StatusChanged;
    public event Action? MembershipChanged;
    public event Action? ActiveSpaceChanged;
    public Func<bool>? CanSwitchSpace { get; set; }
    public TodoStore Store => primary.Store;
    public DeviceIdentity Identity => active.Identity;
    public PeerNode Node => active.Node;
    public ReplicaSettings Replicas => active.Replicas;
    public string ActiveSpaceKey => active.Key;
    public string ProfileRoot => primary.Store.Root;
    public int ReconcileHours { get; private set; } = 1;
    public Task StartupBackup => Task.CompletedTask;
    public bool ShowDeviceSource { get; private set; }
    public SpaceInfo[] Spaces { get { lock(gate) return sessions.Where(s=>!s.Deleted).Select(s=>new SpaceInfo(s.Key,s.Identity.SpaceName,s.Identity.Space?.Root,s==active,s.Identity.Space?.Contains(s.Identity.Id)==true,s.Identity.Devices.Length+1,s.Node.Status)).ToArray(); } }
    public void SetShowDeviceSource(bool value) { primary.Store.Database.WriteMetadata("show-device-source", [value ? (byte)1 : (byte)0]); ShowDeviceSource=value; }
    public AppRuntime(string path, string deviceName, int port=PeerNode.DefaultPort)
    {
        primary=Open("",path,deviceName,port); active=primary;
        sessions.Add(primary);
        try
        {
            ShowDeviceSource=primary.Store.Database.ReadMetadata("show-device-source") is not {Length:>0} bytes || bytes[0]!=0;
            if(primary.Store.Database.ReadMetadata("sync-settings.json") is {} settings)ReconcileHours=Math.Clamp(Json.Read<int>(settings),1,2);
            if(primary.Store.Database.ReadMetadata("spaces-catalog.json") is {} catalogBytes)
            {
                var catalog=Json.Read<Catalog>(catalogBytes);
                if(catalog.Keys.Length>4096 || catalog.Keys.Any(k=>!Guid.TryParseExact(k,"N",out _)) || catalog.Keys.Distinct().Count()!=catalog.Keys.Length)throw new InvalidDataException("空间目录无效。");
                foreach(var key in catalog.Keys)
                {
                    var directory=Path.Combine(path,"spaces",key);
                    if(!File.Exists(Path.Combine(directory,SqliteProfile.FileName)))throw new IOException("空间资料丢失，请恢复完整的数据目录。");
                    sessions.Add(Open(key,directory,deviceName,port,Store));
                }
                active=sessions.Single(s=>s.Key==catalog.Active);
            }
            primary.Node.ResolveSpace=root=>{lock(gate)return root is null ? primary.Node : sessions.FirstOrDefault(s=>s.Identity.Space?.Root==root)?.Node;};
            MergeLegacyContent();
            foreach(var session in sessions)Attach(session);
            if(active.Deleted)
            {
                active=sessions.FirstOrDefault(s=>!s.Deleted)??AddSession();
                PersistCatalog();
            }
        }
        catch { foreach(var session in sessions){session.Identity.Dispose();session.Store.Dispose();}networkGate.Dispose();throw; }
    }
    private static Session Open(string key,string path,string name,int port,TodoStore? content=null)
    {
        var store=new TodoStore(path); DeviceIdentity? identity=null;
        try
        {
            identity=new(store.Database,name); if(!identity.NeedsSpaceUpgrade)identity.EnsureSpace();
            if(store.SpaceDeleted)
            {
                // Older builds used the same flag to erase content. It now retires only the network membership.
                store.Database.WriteMetadata("network-space-deleted",[1]);
                store.Database.WriteMetadata("space-deleted",[0]);
            }
            var replicas=new ReplicaSettings(store.Database);
            return new(key,store,identity,new(content??store,identity,port){Replicas=replicas,ConnectionProfile=store.Database},replicas);
        }
        catch { identity?.Dispose();store.Dispose();throw; }
    }
    private void Attach(Session session)
    {
        if(session!=primary)session.Node.Gateway=primary.Node;
        session.Node.ReconcileInterval=TimeSpan.FromHours(ReconcileHours);
        if(session==primary)session.Store.Changed+=()=>DataChanged?.Invoke();
        session.Node.Changed+=()=>StatusChanged?.Invoke();
        session.Identity.TrustChanged+=()=>MembershipChanged?.Invoke();
    }
    private void PersistCatalog() => primary.Store.Database.WriteMetadata("spaces-catalog.json",JsonSerializer.SerializeToUtf8Bytes(new Catalog(active.Key,sessions.Where(s=>s!=primary).Select(s=>s.Key).ToArray()),Json.Options));
    public void SelectSpace(string key)
    {
        lock(gate)
        {
            var next=sessions.Single(s=>s.Key==key && !s.Deleted); if(next==active)return;
            active.Identity.CancelInvite(); var previous=active; active=next;
            try { PersistCatalog(); } catch { active=previous;throw; }
        }
        ActiveSpaceChanged?.Invoke();
    }
    private Session AddSession()
    {
        lock(gate)
        {
            if(sessions.Count(s=>!s.Deleted)>=32 || sessions.Count>=4096)throw new InvalidOperationException("每台设备最多保留 32 个空间。");
            var key=Guid.NewGuid().ToString("N"); var path=Path.Combine(ProfileRoot,"spaces",key);
            using(var database=new SqliteProfile(path))
            {
                database.WriteMetadata("identity.pfx",primary.Store.Database.ReadMetadata("identity.pfx")!);
                database.WriteMetadata("name.json",JsonSerializer.SerializeToUtf8Bytes(Identity.Name,Json.Options));
            }
            var session=Open(key,path,Identity.Name,PeerNode.DefaultPort,Store);sessions.Add(session);Attach(session);PersistCatalog();
            if(networkEnabled)session.Node.Start(false);
            return session;
        }
    }
    public async Task CreateSpaceAsync(string name)
    {
        ValidateSpaceName(name);
        await networkGate.WaitAsync();
        try { var session=AddSession();session.Identity.RenameSpace(name);SelectSpace(session.Key); }
        finally { networkGate.Release(); }
    }
    public const string JoinNotice="空间只划分设备连接，所有空间共用本机的清单、历史与附件。加入后，本机全部内容会与成员同步；同时加入多个空间的设备会在它们之间接力同步。";
    public async Task JoinSpaceAsync(string code,string? address=null,CancellationToken token=default)
    {
        var invitation=DeviceIdentity.ParseSpaceInvite(code);
        await networkGate.WaitAsync(token);
        try
        {
            Session? session; lock(gate)session=sessions.FirstOrDefault(s=>s.Identity.Space?.Root==invitation.Root || s.Store.Database.ReadMetadata("joining-root") is {} joining && System.Text.Encoding.UTF8.GetString(joining)==invitation.Root);
            if(session is not null && !session.Deleted && session.Identity.Space?.Root==invitation.Root && session.Identity.Space.Contains(session.Identity.Id)) { SelectSpace(session.Key);return; }
            session ??= AddSession();
            session.Store.Database.WriteMetadata("joining-root",System.Text.Encoding.UTF8.GetBytes(invitation.Root));
            // The prepared empty profile is persisted before joining. Interruption never loses an accepted membership.
            await session.Node.JoinSpaceAsync(code,address,token);
            session.Store.Database.WriteMetadata("network-space-deleted",[0]);
            session.Node.RequestSync();
            SelectSpace(session.Key);
        }
        finally { networkGate.Release(); }
    }
    public static void ValidateSpaceName(string name) { if(string.IsNullOrWhiteSpace(name) || name.Trim().Length>100 || name.Any(char.IsControl))throw new InvalidDataException("空间名称应为 1–100 字。"); }
    public void RenameSpace(string name) { ValidateSpaceName(name);Identity.RenameSpace(name); }
    public const string DeleteSpaceNotice="退出并从本机移除这组网络连接。清单、历史、附件和草稿均保留，其他连接空间继续同步。重新加入需要新的授权码。";
    public async Task DeleteSpaceAsync(string key)
    {
        await networkGate.WaitAsync();
        try
        {
            Session removed;lock(gate)removed=sessions.Single(s=>s.Key==key && !s.Deleted);
            removed.Identity.LeaveSpace();
            if(removed.Identity.Space is null)foreach(var device in removed.Identity.Devices)removed.Identity.Revoke(device.Id);
            removed.Store.Database.WriteMetadata("network-space-deleted",[1]);
            lock(gate)
            {
                if(active==removed)active=sessions.FirstOrDefault(s=>!s.Deleted)??AddSession();
                PersistCatalog();
            }
            ActiveSpaceChanged?.Invoke();MembershipChanged?.Invoke();
            // Keep signed departure/routing metadata to notify offline peers; shared content is untouched.
        }
        finally { networkGate.Release(); }
    }
    public void RequestSync() { lock(gate)foreach(var session in sessions)session.Node.RequestSync(); }
    public void SetReconcileHours(int hours)
    {
        if(hours is not (1 or 2))throw new ArgumentOutOfRangeException(nameof(hours));
        primary.Store.Database.WriteMetadata("sync-settings.json",JsonSerializer.SerializeToUtf8Bytes(hours));ReconcileHours=hours;
        lock(gate)foreach(var session in sessions)session.Node.ReconcileInterval=TimeSpan.FromHours(hours);
    }
    public async Task SetNetworkAsync(bool enabled)
    {
        await networkGate.WaitAsync();
        try { await SetNetworkCore(enabled); }
        finally { networkGate.Release(); }
    }
    private async Task SetNetworkCore(bool enabled)
    {
            Session[] snapshot;lock(gate)snapshot=sessions.ToArray();
            if(enabled) { primary.Node.Start();foreach(var session in snapshot.Skip(1))session.Node.Start(false); }
            else { await primary.Node.DisposeAsync();foreach(var session in snapshot.Skip(1))await session.Node.DisposeAsync(); }
            networkEnabled=enabled;
    }
    public async Task MoveToAsync(string destination,Action? activate=null)
    {
        await networkGate.WaitAsync();
        try
        {
            if(networkEnabled)throw new InvalidOperationException("请先暂停网络连接。");
            var target=Path.GetFullPath(destination); var origin=Path.GetFullPath(ProfileRoot);
            if(target.Equals(origin,StringComparison.OrdinalIgnoreCase) || target.StartsWith(Path.TrimEndingDirectorySeparator(origin)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || origin.StartsWith(Path.TrimEndingDirectorySeparator(target)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("请选择与原数据目录分开的文件夹。");
            var moved=new List<(Session Session,string Origin)>();
            try
            {
                foreach(var session in sessions.Where(s=>s!=primary))
                {
                    var previous=session.Store.Root;
                    await Task.Run(()=>ProfileMigration.MoveTo(session.Store,Path.Combine(target,"spaces",session.Key)));
                    moved.Add((session,previous));
                }
                await Task.Run(()=>ProfileMigration.MoveTo(primary.Store,target,activate));
            }
            catch
            {
                foreach(var item in moved.AsEnumerable().Reverse())ProfileMigration.MoveTo(item.Session.Store,item.Origin);
                throw;
            }
        }
        finally { networkGate.Release(); }
    }
    public string[] BackupSpaces(string directory,bool daily=false)
    {
        Directory.CreateDirectory(directory);
        if(daily && Store.Export().Length==0)return [];
        var name=(daily?"daily-":"manual-")+primary.Identity.Id[..12]+"-"+DateTime.UtcNow.ToString(daily?"yyyyMMdd":"yyyyMMdd-HHmmss-fffffff")+".lantodo.zip";
        var path=Path.Combine(directory,name);if(!daily || !File.Exists(path))Store.Backup(path);return [path];
    }
    public async ValueTask DisposeAsync()
    {
        await SetNetworkAsync(false);
        foreach(var session in sessions){session.Identity.Dispose();session.Store.Dispose();}
        networkGate.Dispose();
    }
}
