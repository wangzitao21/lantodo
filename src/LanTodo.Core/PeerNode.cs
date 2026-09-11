using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;

namespace LanTodo.Core;

public sealed record Beacon(int Protocol, string DeviceId, int Port, string? Generation = null);
public sealed record FoundPeer(string Id, IPEndPoint Endpoint, DateTimeOffset Seen, string? Generation = null);

public sealed partial class PeerNode : IAsyncDisposable
{
    public const int DefaultPort = 42851;
    public const int DiscoveryPort = 42852;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.42.51");
    private readonly ITodoStore store;
    private readonly DeviceIdentity identity;
    private readonly int requestedPort;
    private CancellationTokenSource? lifetime;
    private TcpListener? listener;
    private UdpClient? discovery;
    private Task[] loops = Array.Empty<Task>();
    private readonly ConcurrentDictionary<string, FoundPeer> found = new();
    private readonly ConcurrentDictionary<string, byte> syncing = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> peerActivity = new();
    public string DeviceState(string id) => !identity.IsTrusted(id) ? "已取消绑定" :
        ReplicaStatuses.FirstOrDefault(s => s.DeviceId == id)?.State ??
        (peerActivity.TryGetValue(id, out var seen) && DateTimeOffset.UtcNow - seen < TimeSpan.FromSeconds(45) ? "最近已连接" : "已绑定 · 等待连接");
    private void ReadPeerState(Packet packet, string peerId)
    {
        if (identity.Space is { } space)
        {
            if (packet.Space is not null) space.Merge(packet.Space);
            if (!identity.IsTrusted(peerId)) throw new UnauthorizedAccessException("设备已移出空间，本机数据仍保留。");
            if (packet.Kind is "binding-revoked" or "binding-deleted") throw new UnauthorizedAccessException("对方使用旧连接规则，请升级后重新加入。");
        }
        if (packet.Kind is "binding-revoked" or "binding-deleted")
        {
            if (identity.IsTrusted(peerId)) identity.Revoke(peerId);
            if (packet.Kind == "binding-deleted") { identity.DeleteRevokedDevice(peerId); Replicas?.RemoveEndpoint(peerId); found.TryRemove(peerId, out _); }
            peerActivity.TryRemove(peerId, out _);
            throw new UnauthorizedAccessException(packet.Kind == "binding-deleted" ? "对方已删除绑定，本机记录已移除。" : "对方已取消绑定，请使用新配对码重新连接。");
        }
        if (identity.IsTrusted(peerId)) { identity.MergeLabels(packet.Labels); peerActivity[peerId] = DateTimeOffset.UtcNow; }
    }
    private async Task<bool> ReplyIfUnbound(SslStream tls, string peerId, CancellationToken token)
    {
        if (identity.IsTrusted(peerId)) return false;
        if (identity.Space is { } space)
        {
            if (!space.Knows(peerId)) throw new UnauthorizedAccessException("设备未授权。");
            await Wire.Write(tls, new("space-state", Space: space.Snapshot), token); return true;
        }
        if (!identity.WasDeleted(peerId) && !identity.RevokedDevices.Any(d => d.Id == peerId)) throw new UnauthorizedAccessException("设备未授权。");
        await Wire.Write(tls, new(identity.WasDeleted(peerId) ? "binding-deleted" : "binding-revoked"), token);
        return true;
    }
    private readonly ConcurrentDictionary<int, Task> handlers = new();
    private readonly SemaphoreSlim incomingSlots = new(32);
    private int handlerId;
    private readonly object statusGate = new();
    private readonly Channel<bool> announceSignals = Channel.CreateBounded<bool>(1);
    private readonly Channel<bool> syncSignals = Channel.CreateBounded<bool>(1);
    private readonly Dictionary<string, SyncSchedule> schedules = new();
    private long localVersion;
    private int requestedSync;
    private volatile string generation = Guid.NewGuid().ToString("N");
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromSeconds(30);
    private int successfulSyncs;
    public int SuccessfulSyncs => Volatile.Read(ref successfulSyncs);
    private string legacyStatus = "当前仅本机使用";
    public string Status
    {
        get
        {
            if (identity.Space is not { } space) return legacyStatus;
            if (!space.Contains(identity.Id)) return "已退出空间 · 本机数据已保留";
            if (lifetime is null) return "已保存到本机 · 网络已停止";
            var members = SpaceStatuses;
            if (members.Length == 0) return "已保存到本机 · 当前仅本机使用";
            int confirmed = members.Count(s => s.State == "已同步");
            var summary = $"已保存本机 · {confirmed} 台已确认" + (confirmed < members.Length ? $"，{members.Length - confirmed} 台等待确认" : "");
            return store.List().Any(t => t.Conflict) ? "存在待确认内容 · " + summary : summary;
        }
    }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSync { get; private set; }
    public event Action? Changed;
    internal PeerNode? Gateway { get; set; }
    internal Func<string?, PeerNode?>? ResolveSpace { get; set; }
    public int Port => Gateway?.Port ?? ((IPEndPoint?)listener?.LocalEndpoint)?.Port ?? 0;
    public FoundPeer[] Nearby => Gateway?.Nearby ?? found.Values.Where(p => DateTimeOffset.UtcNow - p.Seen < TimeSpan.FromSeconds(100)).ToArray();

    public PeerNode(ITodoStore store, DeviceIdentity identity, int port = DefaultPort)
    { this.store = store; this.identity = identity; requestedPort = port; }

    public void Start(bool enableDiscovery = true)
    {
        if (lifetime is not null) return;
        lifetime = new();
        var token = lifetime.Token;
        store.Changed += DataChanged;
        identity.TrustChanged += RequestSync;
        if (Replicas is not null) Replicas.Changed += RequestSync;
        try
        {
            if(Gateway is null)
            {
            listener = new TcpListener(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, requestedPort);
            if (Socket.OSSupportsIPv6) listener.Server.DualMode = true;
            listener.Start(8);
            }
            // Network tasks must never capture the WPF/Android UI synchronization context.
            var jobs = new List<Task> { Task.Run(() => FixedPeersLoop(token)), Task.Run(() => MeshLoop(token)), Task.Run(() => AttachmentScanLoop(token)) };
            if(Gateway is null) jobs.Add(Task.Run(()=>AcceptLoop(token)));
            string? discoveryError = null;
            if (enableDiscovery && Gateway is null)
            {
                try
                {
                    discovery = new UdpClient(AddressFamily.InterNetwork);
                    discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    discovery.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                    discovery.EnableBroadcast = true;
                    discovery.MulticastLoopback = true;
                    discovery.Ttl = 1;
                    JoinInterfaces();
                    jobs.Add(Task.Run(() => DiscoverLoop(token)));
                    jobs.Add(Task.Run(() => AnnounceLoop(token)));
                    jobs.Add(Task.Run(() => AutoSyncLoop(token)));
                }
                catch (Exception ex) when (ex is SocketException or NetworkInformationException)
                {
                    discovery?.Dispose(); discovery = null; discoveryError = ex.Message;
                }
            }
            loops = jobs.ToArray();
            SetStatus(discoveryError is null ? "当前仅本机使用" : "局域网发现不可用 · NAS 固定地址连接仍可用", discoveryError);
        }
        catch (Exception ex)
        {
            lifetime.Cancel(); listener?.Stop(); discovery?.Dispose(); lifetime.Dispose(); lifetime = null;
            store.Changed -= DataChanged; identity.TrustChanged -= RequestSync;
            if (Replicas is not null) Replicas.Changed -= RequestSync;
            SetStatus("局域网暂不可用；本机可正常使用", ex.Message);
        }
    }

    private void DataChanged()
    {
        Interlocked.Increment(ref localVersion);
        WakeReplicas();
        generation = Guid.NewGuid().ToString("N");
        announceSignals.Writer.TryWrite(true);
        syncSignals.Writer.TryWrite(true);
        if (HasFixedPeers) SetStatus("已保存本机 · 等待 NAS 确认");
    }
    public void RequestSync()
    {
        generation = Guid.NewGuid().ToString("N");
        Interlocked.Increment(ref localVersion);
        WakeReplicas();
        Interlocked.Exchange(ref requestedSync,1);
        announceSignals.Writer.TryWrite(true); syncSignals.Writer.TryWrite(true);
    }
    private static async Task WaitSignal(Channel<bool> channel, TimeSpan delay, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(delay);
        try { await channel.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }

    private void SetStatus(string value, string? error = null)
    {
        lock (statusGate)
        {
            legacyStatus = store.List().Any(t => t.Conflict) ? "存在冲突，请选择最终版本 · " + value : value;
            LastError = error;
        }
        Changed?.Invoke();
    }

    private void JoinInterfaces()
    {
        foreach (var ip in LocalAddresses())
            try { discovery!.JoinMulticastGroup(Group, ip.Address); } catch (SocketException) { }
    }
    private static IEnumerable<UnicastIPAddressInformation> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

    private async Task AnnounceLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (identity.Space is null && Replicas?.Current.LanEnabled == false)
                {
                    await WaitSignal(announceSignals, TimeSpan.FromSeconds(5), token);
                    continue;
                }
                JoinInterfaces(); // New Wi-Fi / DHCP interfaces are enrolled without restarting the app.
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Beacon(1, identity.Id, Port, generation), Json.Options);
                var endpoints = new HashSet<IPAddress> { Group, IPAddress.Broadcast };
                foreach (var ip in LocalAddresses())
                {
                    var addr = ip.Address.GetAddressBytes(); var mask = ip.IPv4Mask.GetAddressBytes();
                    endpoints.Add(new IPAddress(addr.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray()));
                }
                foreach (var address in endpoints)
                    try { await discovery!.SendAsync(bytes, new IPEndPoint(address, DiscoveryPort), token); } catch (SocketException) { }
                await WaitSignal(announceSignals, DiscoveryInterval, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { SetStatus("等待局域网连接", ex.Message); await DelayRetry(token); }
        }
    }

    private async Task DiscoverLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var received = await discovery!.ReceiveAsync(token);
                if (identity.Space is null && Replicas?.Current.LanEnabled == false) continue;
                if (received.Buffer.Length > 1024) continue;
                var beacon = Json.Read<Beacon>(received.Buffer);
                if (beacon.Protocol != 1 || !Json.IsHash(beacon.DeviceId) || beacon.DeviceId == identity.Id || beacon.Port is < 1024 or > 65535) continue;
                // Discovery is an untrusted hint; identity is established only by pinned TLS.
                if (found.Count >= 128 && !found.ContainsKey(beacon.DeviceId)) continue;
                if (beacon.Generation is { Length: > 64 }) continue;
                var previous = found.GetValueOrDefault(beacon.DeviceId);
                found[beacon.DeviceId] = new(beacon.DeviceId, new IPEndPoint(received.RemoteEndPoint.Address, beacon.Port), DateTimeOffset.UtcNow, beacon.Generation);
                if (previous is null || previous.Generation != beacon.Generation || previous.Endpoint.Address.ToString() != received.RemoteEndPoint.Address.ToString())
                {
                    if (identity.Space is not null && previous is null) RequestSync();
                    syncSignals.Writer.TryWrite(true);
                    if (previous is null) announceSignals.Writer.TryWrite(true); // Reply promptly to a newly arriving device.
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or SocketException) { }
        }
    }

    private async Task AutoSyncLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // Explicit manual sync and network recovery also bypass an earlier failed connection's backoff.
            if (identity.Space is not null) { try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; } continue; }
            if (Interlocked.Exchange(ref requestedSync,0) == 1) schedules.Clear();
            foreach (var peer in Nearby.Where(p => (identity.IsTrusted(p.Id) || identity.RevokedDevices.Any(d => d.Id == p.Id)) && (Replicas?.Current.LanEnabled ?? true) && !IsFixedPeer(p.Id)))
            {
                if (!schedules.TryGetValue(peer.Id, out var schedule)) schedules[peer.Id] = schedule = new();
                var version = Interlocked.Read(ref localVersion);
                // One device can announce through several network adapters. Address changes alone
                // must not trigger repeated data exchanges; the current endpoint is used on the next edit/retry.
                if (!schedule.Due(version, peer.Generation, DateTimeOffset.UtcNow, ReconcileInterval)) continue;
                if (!syncing.TryAdd(peer.Id, 0)) continue;
                try
                {
                    await SyncAsync(peer.Endpoint, peer.Id, token);
                    schedule.Succeeded(version, peer.Generation, DateTimeOffset.UtcNow);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    schedule.Failed(DateTimeOffset.UtcNow);
                    if (!token.IsCancellationRequested) SetStatus("等待重新连接", ex.Message);
                }
                finally { syncing.TryRemove(peer.Id, out _); }
            }
            foreach (var peer in found.Values.Where(p => DateTimeOffset.UtcNow - p.Seen > TimeSpan.FromSeconds(100))) { found.TryRemove(peer.Id, out _); schedules.Remove(peer.Id); }
            if (Nearby.All(p => !identity.IsTrusted(p.Id)) && !HasFixedPeers) SetStatus("已保存到本机 · 等待设备连接");
            try { await WaitSignal(syncSignals, TimeSpan.FromSeconds(5), token); } catch (OperationCanceledException) { break; }
        }
    }
    private static async Task DelayRetry(CancellationToken token) { try { await Task.Delay(4000, token); } catch (OperationCanceledException) { } }

    public async Task PairAsync(string code, CancellationToken token = default)
    {
        if (code.Trim().StartsWith("lantodo2:")) { await JoinSpaceAsync(code, token: token); return; }
        if (identity.Space is not null) throw new InvalidDataException("这是旧版配对码，请在对方升级后生成空间授权码。");
        var invite = DeviceIdentity.ParseInvite(code);
        if (invite.DeviceId == identity.Id) throw new InvalidDataException("这是本机配对码，请在另一台设备输入。");
        SetStatus("正在寻找配对设备");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        FoundPeer? peer;
        while ((peer = Nearby.FirstOrDefault(p => p.Id == invite.DeviceId)) is null) await Task.Delay(300, timeout.Token);
        await PairAtAsync(peer.Endpoint, invite, timeout.Token);
    }

    public async Task PairAtAsync(IPEndPoint endpoint, PairingInvite invite, CancellationToken token = default)
    {
        if (identity.Space is not null) throw new InvalidOperationException("请使用空间授权码。");
        using var tcp = new TcpClient();
        using var tls = await Connect(tcp, endpoint, invite.DeviceId, token);
        await Wire.Write(tls, new("pair", Name: identity.Name, Secret: invite.Secret), token);
        var reply = await Wire.Read(tls, token);
        if (reply.Kind != "paired") throw new InvalidDataException("配对失败。");
        identity.Trust(invite.DeviceId, invite.Name);
        SetStatus("已配对，正在同步");
        await SyncAsync(endpoint, invite.DeviceId, token);
    }

    private async Task<SslStream> Connect(TcpClient tcp, IPEndPoint endpoint, string peerId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        await tcp.ConnectAsync(endpoint.Address, endpoint.Port, timeout.Token);
        tcp.NoDelay = true;
        var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert is not null && DeviceIdentity.Fingerprint(cert) == peerId);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "LanTodo", ClientCertificates = new X509CertificateCollection { identity.Certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                LocalCertificateSelectionCallback = (_, _, _, _, _) => identity.Certificate
            }, timeout.Token);
            return tls;
        }
        catch { tls.Dispose(); throw; }
    }

    public async Task SyncAsync(IPEndPoint endpoint, string peerId, CancellationToken token = default) =>
        _ = await SyncCoreAsync(endpoint, peerId, token);

    private async Task<string?> SyncCoreAsync(IPEndPoint endpoint, string peerId, CancellationToken token)
    {
        if (!identity.IsTrusted(peerId) && identity.Space?.Knows(peerId) != true && !identity.RevokedDevices.Any(d => d.Id == peerId)) throw new UnauthorizedAccessException("设备未授权。");
        SetStatus("正在同步");
        using var tcp = new TcpClient();
        using var tls = await Connect(tcp, endpoint, peerId, token);
        var connectionRoot = identity.Space?.Root;
        if (connectionRoot is not null) await SpaceHello(tls, peerId, token);
        async Task<Packet> Exchange(Packet packet)
        {
            if (identity.Space?.Root != connectionRoot) throw new UnauthorizedAccessException("空间已改变，请重新连接。");
            if (!identity.IsTrusted(peerId) && packet.Kind != "inventory") throw new UnauthorizedAccessException("设备授权已取消。");
            await Wire.Write(tls, packet, token);
            var result = await Wire.Read(tls, token);
            ReadPeerState(result, peerId);
            if (!identity.IsTrusted(peerId)) throw new UnauthorizedAccessException("设备授权已取消。");
            return result;
        }
        if (store is TodoStore localFiles) localFiles.DetectMissingAttachments();
        var remoteIds = new List<string>();
        bool remoteBlobs = false;
        for (int offset = 0; ; offset += 256)
        {
            var page = await Exchange(new("inventory", Offset: offset, Labels: offset == 0 && identity.IsTrusted(peerId) ? identity.Labels : null));
            remoteBlobs |= page.Blobs;
            if (page.Kind != "inventory" || page.Ids is null || page.Ids.Length > 256 || page.Ids.Any(id => !Json.IsHash(id)) || (!page.Done && page.Ids.Length != 256))
                throw new InvalidDataException("版本清单无效。");
            remoteIds.AddRange(page.Ids);
            if (remoteIds.Count > 1_000_000) throw new InvalidDataException("版本数超出当前客户端容量。");
            if (page.Done) break;
        }
        var localIds = store.Export().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var batch in remoteIds.Where(id => !localIds.Contains(id)).Chunk(4))
        {
            var reply = await Exchange(new("get", Ids: batch));
            if (reply.Kind != "revisions" || reply.Revisions is null || !reply.Revisions.Select(r => r.Id).SequenceEqual(batch)) throw new InvalidDataException("收到的版本与请求不符。");
            store.Import(reply.Revisions);
        }
        var remoteSet = remoteIds.ToHashSet(StringComparer.Ordinal);
        if (!remoteBlobs && store.Export().Any(r => r.Body.Data.Attachments is { Length: > 0 }))
            throw new IOException("对方不支持附件，请将所有设备与 NAS 升级至 v1.0.1。");
        foreach (var batch in store.Export().Where(r => !remoteSet.Contains(r.Id)).Chunk(4))
        {
            var reply = await Exchange(new("put", Revisions: batch));
            if (reply.Kind != "saved") throw new InvalidDataException("对方尚未确认保存。");
        }
        if (remoteBlobs && store is TodoStore files)
            await SyncAttachments(files, Exchange, token);
        var done = await Exchange(new("done"));
        if (done.Kind != "done") throw new InvalidDataException("同步确认失败。");
        LastSync = DateTimeOffset.Now;
        Interlocked.Increment(ref successfulSyncs);
        SetStatus("已与 " + (identity.Devices.FirstOrDefault(d => d.Id == peerId)?.Name ?? "设备") + " 同步 · " + LastSync.Value.ToString("HH:mm:ss"));
        return done.Generation;
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var tcp = await listener!.AcceptTcpClientAsync(token);
                if (!incomingSlots.Wait(0)) { tcp.Dispose(); continue; }
                int id = Interlocked.Increment(ref handlerId);
                var task = Handle(tcp, token);
                handlers[id] = task;
                _ = task.ContinueWith(_ => { handlers.TryRemove(id, out var ignored); incomingSlots.Release(); }, TaskScheduler.Default);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { if (token.IsCancellationRequested) break; await DelayRetry(token); }
        }
    }

    private async Task Handle(TcpClient tcp, CancellationToken token)
    {
        using (tcp)
        using (var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert is not null))
        {
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = identity.Certificate, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, handshake.Token);
                if (tls.RemoteCertificate is null) return;
                var first = await Wire.Read(tls,token);
                var root = first.Space?.Root ?? first.SpaceRoot;
                var target = ResolveSpace?.Invoke(root) ?? (root is null || root == identity.Space?.Root ? this : null);
                if(target is null)throw new UnauthorizedAccessException("本机未加入此空间。");
                await target.HandleSession(tcp,tls,first,token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Diagnostics never include task content, secrets, or the untrusted request payload.
                if (tls.IsAuthenticated && !token.IsCancellationRequested)
                    try { await Wire.Write(tls, new("error", Error: ex is UnauthorizedAccessException ? "设备未获授权或配对码失效。" : "同步中断，请重试。"), token); } catch { }
            }
        }
    }

    private async Task HandleSession(TcpClient tcp, SslStream tls, Packet? first, CancellationToken token)
    {
                string peerId = DeviceIdentity.Fingerprint(tls.RemoteCertificate!);
                string? connectionRoot = identity.Space?.Root;
                bool spaceAuthenticated = false;
                Revision[]? snapshot = null;
                Dictionary<string, Revision>? index = null;
                HashSet<Attachment>? attachmentIndex = null;
                string? snapshotGeneration = null;
                long snapshotVersion = -1;
                while (!token.IsCancellationRequested)
                {
                    var request = first ?? await Wire.Read(tls, token); first = null;
                    if (identity.Space?.Root != connectionRoot) throw new UnauthorizedAccessException("空间已改变。");
                    if (request.Kind == "space-join")
                    {
                        if (request.Secret is null || request.Name is null) throw new UnauthorizedAccessException();
                        using var certificate = X509CertificateLoader.LoadCertificate(tls.RemoteCertificate!.GetRawCertData());
                        var membership = identity.RedeemSpace(request.Secret, certificate, request.Name);
                        await Wire.Write(tls, new("space-state", Space: membership, Routes: ShareRoutes(), Labels: identity.Labels), token); return;
                    }
                    if (request.Kind == "space-hello")
                    {
                        var space = identity.Space ?? throw new UnauthorizedAccessException("未加入空间。");
                        if (request.Space is null) throw new UnauthorizedAccessException();
                        space.Merge(request.Space);
                        if (!space.Knows(peerId)) throw new UnauthorizedAccessException();
                        if (await ReplyIfUnbound(tls, peerId, token)) return;
                        LearnRoutes(request.Routes);
                        if (request.Port is > 0 and <= 65535 && tcp.Client.RemoteEndPoint is IPEndPoint remote)
                            LearnRoutes([new(peerId, [new IPEndPoint(remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address, request.Port).ToString()])]);
                        spaceAuthenticated = true;
                        await Wire.Write(tls, new("space-state", Space: space.Snapshot, Routes: ShareRoutes()), token); continue;
                    }
                    if (request.Kind == "pair")
                    {
                        if (identity.Space is not null) throw new UnauthorizedAccessException("旧版配对已停用。");
                        if (request.Secret is null || request.Name is null || !identity.Redeem(request.Secret, peerId, request.Name))
                            throw new UnauthorizedAccessException("配对码无效、已使用或已过期，请在对方设备重新生成。");
                        await Wire.Write(tls, new("paired"), token);
                        return;
                    }
                    if (identity.Space is not null && !spaceAuthenticated) throw new UnauthorizedAccessException("请先完成空间握手。");
                    if (await ReplyIfUnbound(tls, peerId, token)) return;
                    identity.MergeLabels(request.Labels); peerActivity[peerId] = DateTimeOffset.UtcNow;
                    Packet reply;
                    switch (request.Kind)
                    {
                        case "inventory":
                            if (snapshot is null) snapshotVersion = Interlocked.Read(ref localVersion);
                            snapshotGeneration ??= generation; // Capture before snapshot: edits during exchange must wake the next round.
                            snapshot ??= store.Export();
                            index ??= snapshot.ToDictionary(r => r.Id);
                            if (request.Offset < 0 || request.Offset > snapshot.Length) throw new InvalidDataException("分页参数无效。");
                            reply = new("inventory", Ids: snapshot.Skip(request.Offset).Take(256).Select(r => r.Id).ToArray(), Done: request.Offset + 256 >= snapshot.Length, Blobs: store is TodoStore, Labels: request.Offset == 0 ? identity.Labels : null);
                            break;
                        case "get":
                            if (index is null || request.Ids is null || request.Ids.Length > 8 || request.Ids.Any(id => !index.ContainsKey(id))) throw new InvalidDataException("版本请求无效。");
                            reply = new("revisions", Revisions: request.Ids.Select(id => index[id]).ToArray());
                            break;
                        case "put":
                            if (request.Revisions is null || request.Revisions.Length > 8) throw new InvalidDataException("版本批次无效。");
                            store.Import(request.Revisions);
                            attachmentIndex = null;
                            reply = new("saved");
                            break;
                        case "done":
                            await Wire.Write(tls, new("done", Generation: snapshotGeneration), token);
                            LastSync = DateTimeOffset.Now;
                            if (identity.Space is not null)
                            {
                                meshStates[peerId] = (new(peerId, AddressesFor(peerId).FirstOrDefault() ?? "", "已同步", LastSync), snapshotVersion);
                            }
                            SetStatus("已与 " + (identity.Devices.FirstOrDefault(d => d.Id == peerId)?.Name ?? "设备") + " 同步 · " + LastSync.Value.ToString("HH:mm:ss"));
                            return;
                        case "blob-invalidations":
                            if(store is not TodoStore attachmentStore || request.Offset < 0) throw new InvalidDataException();
                            attachmentStore.DetectMissingAttachments();
                            if(request.Ids is { Length: > 256 }) throw new InvalidDataException();
                            attachmentStore.Attachments.Invalidate(request.Ids ?? []);
                            var invalidKeys = attachmentStore.Attachments.InvalidKeys;
                            reply = new("blob-invalidations",Ids:invalidKeys.Skip(request.Offset).Take(256).ToArray(),Done:request.Offset+256>=invalidKeys.Length);
                            break;
                        case "blob-status":
                        case "blob-get":
                        case "blob-put":
                            attachmentIndex ??= store.Export().SelectMany(r => r.Body.Data.Attachments ?? []).ToHashSet();
                            reply = HandleAttachment(request, attachmentIndex);
                            break;
                        case "watch":
                            if (request.Generation is null || request.Generation.Length > 64) throw new InvalidDataException("变化标记无效。");
                            // Bounded long poll fits the existing 20-second frame deadline. No history is sent while idle.
                            for (int i = 0; i < 40 && request.Generation == generation; i++)
                            {
                                if (await ReplyIfUnbound(tls, peerId, token)) return;
                                await Task.Delay(250, token);
                            }
                            if (await ReplyIfUnbound(tls, peerId, token)) return;
                            await Wire.Write(tls, new("changed", Generation: generation, Labels: identity.Labels), token);
                            return;
                        default: throw new InvalidDataException("未知同步命令。");
                    }
                    if (await ReplyIfUnbound(tls, peerId, token)) return;
                    await Wire.Write(tls, reply, token);
                }
    }

    public async ValueTask DisposeAsync()
    {
        if (lifetime is null) return;
        store.Changed -= DataChanged; identity.TrustChanged -= RequestSync;
        if (Replicas is not null) Replicas.Changed -= RequestSync;
        lifetime.Cancel(); listener?.Stop(); discovery?.Dispose();
        try { await Task.WhenAll(loops.Concat(handlers.Values)).ConfigureAwait(false); }
        catch (OperationCanceledException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
        finally { lifetime.Dispose(); lifetime = null; found.Clear(); schedules.Clear(); }
        SetStatus("当前仅本机使用");
    }
}
