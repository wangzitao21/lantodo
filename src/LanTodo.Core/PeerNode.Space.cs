using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace LanTodo.Core;

public sealed record RouteHint(string DeviceId, string[] Addresses);
internal sealed record RouteBook(string Root, RouteHint[] Routes);

public sealed partial class PeerNode
{
    private readonly object routesGate = new();
    private RouteBook? routes;
    internal IProfileDatabase? ConnectionProfile { get; init; }
    private IProfileDatabase? Profile => ConnectionProfile ?? (store as TodoStore)?.Database;
    public string AdvertisedAddress => Profile?.ReadMetadata("space-public-address") is { } bytes ? Json.Read<string>(bytes) : "";
    public void SetAdvertisedAddress(string address)
    {
        address = address.Trim();
        if (address.Length > 300) throw new InvalidDataException("地址过长。");
        if (address.Length != 0) _ = ReplicaAddress.Parse(address);
        Profile?.WriteMetadata("space-public-address", JsonSerializer.SerializeToUtf8Bytes(address, Json.Options));
        RequestSync();
    }
    private string[] OwnAddresses()
    {
        IEnumerable<string> local;
        try { local = LocalAddresses().Select(a => a.Address + ":" + Port).Distinct().Take(4).ToArray(); }
        catch (System.Net.NetworkInformation.NetworkInformationException) { local = []; }
        return (AdvertisedAddress.Length > 0 ? new[] { AdvertisedAddress }.Concat(local) : local).Distinct().Take(8).ToArray();
    }
    public string CreateInvite() => identity.CreateSpaceInvite(OwnAddresses());
    private RouteBook RouteState()
    {
        var root = identity.Space?.Root ?? "";
        lock (routesGate)
        {
            if (routes?.Root == root) return routes;
            routes = Profile?.ReadMetadata("space-routes-v2.json") is { } bytes ? Json.Read<RouteBook>(bytes) : null;
            if (routes?.Root != root) routes = new(root, []);
            return routes;
        }
    }
    private RouteHint[] ShareRoutes()
    {
        var own = new RouteHint(identity.Id, OwnAddresses());
        return RouteState().Routes.Where(r => r.DeviceId != identity.Id && identity.Space?.Contains(r.DeviceId) == true).Append(own).Take(64).ToArray();
    }
    private void LearnRoutes(RouteHint[]? incoming)
    {
        if (incoming is null) return;
        if (incoming.Length > 64 || incoming.Any(r => r is null || !Json.IsHash(r.DeviceId) || r.Addresses is null || r.Addresses.Length > 8 || r.Addresses.Any(a => a is null || a.Length > 300))) throw new InvalidDataException("连接地址提示无效。");
        foreach (var r in incoming) foreach (var address in r.Addresses) _ = ReplicaAddress.Parse(address);
        lock (routesGate)
        {
            var current = RouteState();
            var next = current.Routes.ToDictionary(r => r.DeviceId, r => r.Addresses);
            foreach (var hint in incoming.Where(r => r.DeviceId != identity.Id && identity.Space?.Contains(r.DeviceId) == true))
                next[hint.DeviceId] = hint.Addresses.Concat(next.GetValueOrDefault(hint.DeviceId) ?? []).Distinct().Take(8).ToArray();
            var updated = new RouteBook(current.Root, next.OrderBy(p => p.Key).Select(p => new RouteHint(p.Key, p.Value)).Take(64).ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, Json.Options);
            if (bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(current, Json.Options))) return;
            Profile?.WriteMetadata("space-routes-v2.json", bytes); routes = updated;
        }
    }
    private string[] AddressesFor(string id) => Nearby.Where(p => p.Id == id).Select(p => p.Endpoint.ToString())
        .Concat(Replicas?.Current.Endpoints?.Where(e => e.DeviceId == id).Select(e => e.Address) ?? [])
        .Concat(RouteState().Routes.FirstOrDefault(r => r.DeviceId == id)?.Addresses ?? []).Distinct().Take(10).ToArray();

    public async Task JoinSpaceAsync(string code, string? address = null, CancellationToken token = default)
    {
        var invitation = DeviceIdentity.ParseSpaceInvite(code);
        if (invitation.DeviceId == identity.Id) throw new InvalidDataException("请在另一台设备输入本机授权码。");
        if (identity.Space?.Root != invitation.Root && !identity.CanJoinSpace) throw new InvalidOperationException("请先退出当前空间，再加入另一个空间。");
        if (!string.IsNullOrWhiteSpace(address)) _ = ReplicaAddress.Parse(address);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(75));
        var addresses = (string.IsNullOrWhiteSpace(address) ? Array.Empty<string>() : [address.Trim()])
            .Concat(Nearby.Where(p => p.Id == invitation.DeviceId).Select(p => p.Endpoint.ToString())).Concat(invitation.Addresses).Distinct().ToList();
        if (addresses.Count == 0)
        {
            SetStatus("正在寻找邀请设备，请保持双方在线");
            for (int i = 0; i < 60 && addresses.Count == 0; i++)
            { await Task.Delay(500, timeout.Token); addresses.AddRange(Nearby.Where(p => p.Id == invitation.DeviceId).Select(p => p.Endpoint.ToString())); }
        }
        Exception? last = null;
        foreach (var route in addresses)
        {
            try
            {
                await AtAddress(route, async endpoint =>
                {
                    using var tcp = new TcpClient();
                    using var tls = await Connect(tcp, endpoint, invitation.DeviceId, timeout.Token);
                    await Wire.Write(tls, new("space-join", Name: identity.Name, Secret: invitation.Secret, SpaceRoot: invitation.Root), timeout.Token);
                    var reply = await Wire.Read(tls, timeout.Token);
                    if (reply.Kind != "space-state" || reply.Space is null) throw new InvalidDataException("对方未确认加入空间。");
                    // Adoption validates every signature and the invitation's root before changing local trust.
                    identity.AdoptSpace(reply.Space, invitation.Root);
                    identity.MergeLabels(reply.Labels);
                    LearnRoutes(reply.Routes);
                    LearnRoutes([new(invitation.DeviceId, [route])]);
                    return true;
                }, timeout.Token);
                SetStatus("已加入空间 · 正在自动同步"); RequestSync(); return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); last = ex; }
        }
        throw new IOException("无法连接邀请设备。请保持对方在线，并连接同一网络；跨网络可在高级设置填写可达地址。", last);
    }
    private async Task SpaceHello(SslStream tls, string peerId, CancellationToken token)
    {
        var space = identity.Space ?? throw new UnauthorizedAccessException("空间已改变。");
        await Wire.Write(tls, new("space-hello", Space: space.Snapshot, Routes: ShareRoutes(), Port: Port), token);
        var reply = await Wire.Read(tls, token);
        if (identity.Space?.Root != space.Root || reply.Kind != "space-state" || reply.Space is null) throw new UnauthorizedAccessException("空间握手失败。");
        space.Merge(reply.Space);
        if (!identity.IsTrusted(peerId)) throw new UnauthorizedAccessException("设备已移出空间，本机数据仍保留。");
        LearnRoutes(reply.Routes);
        PeerSeen(peerId);
    }
    private async Task MeshLoop(CancellationToken token)
    {
        var workers = new Dictionary<(string Root, string Id), (CancellationTokenSource Stop, Task Task)>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var configVersion=Interlocked.Read(ref localVersion);
                var space = identity.Space;
                var desired = space is null ? [] : space.Snapshot.Events.Where(e => e.Body.Kind != "remove" && e.Body.Subject != identity.Id).Select(e => (space.Root, Id: e.Body.Subject)).ToHashSet();
                foreach (var key in workers.Keys.Except(desired).ToArray())
                { var worker = workers[key]; worker.Stop.Cancel(); await worker.Task; worker.Stop.Dispose(); workers.Remove(key); meshStates.TryRemove(key.Id, out _); }
                foreach (var key in desired.Except(workers.Keys))
                { var stop = CancellationTokenSource.CreateLinkedTokenSource(token); workers[key] = (stop, Task.Run(() => MeshPeerLoop(key.Id, key.Root, stop.Token))); }
                await WaitForLocalChange(configVersion, TimeSpan.FromMinutes(5), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            foreach (var w in workers.Values) w.Stop.Cancel();
            await Task.WhenAll(workers.Values.Select(w => w.Task));
            foreach (var w in workers.Values) w.Stop.Dispose(); meshStates.Clear();
        }
    }
    private readonly ConcurrentDictionary<string, (ReplicaStatus Status, long Version)> meshStates = new();
    public ReplicaStatus[] SpaceStatuses => identity.Devices.Select(d =>
    {
        if (lifetime is null) return new ReplicaStatus(d.Id, "", "网络已停止");
        if (!meshStates.TryGetValue(d.Id, out var s)) return new ReplicaStatus(d.Id, "", "等待连接");
        return s.Version != Interlocked.Read(ref localVersion) && s.Status.State == "已同步" ? s.Status with { State = "等待此设备确认更新" } : s.Status;
    }).ToArray();
    private async Task MeshPeerLoop(string id, string root, CancellationToken token)
    {
        int failures = 0; DateTimeOffset? success = null;
        try
        {
            while (!token.IsCancellationRequested && identity.Space?.Root == root)
            {
                long version = Interlocked.Read(ref localVersion); string? used = null; string? remoteGeneration = null; Exception? error = null;
                long routesVersion = Interlocked.Read(ref connectionVersion);
                try
                {
                    meshStates[id] = (new(id, "", "正在连接", success), -1); Changed?.Invoke();
                    using var connection = await ConnectPeer(id, token);
                    meshStates[id] = (new(id, connection.Address, "正在同步", success), -1);
                    remoteGeneration = await SyncConnectionAsync(connection.Stream, id, token);
                    used = connection.Address;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && !token.IsCancellationRequested) { error = ex; }
                if (used is null)
                {
                    if (!meshStates.TryGetValue(id, out var recent) || recent.Status.LastSuccess is not { } received || DateTimeOffset.Now - received > TimeSpan.FromSeconds(30))
                        meshStates[id] = (new(id, "", "等待连接", success, error?.Message), -1);
                    Changed?.Invoke();
                    failures = Math.Min(failures + 1, 6);
                    await WaitForLocalChange(version, TimeSpan.FromSeconds(identity.IsTrusted(id) ? Math.Min(120, 3 * Math.Pow(2, failures - 1)) : 300), token, routesVersion); continue;
                }
                failures = 0; success = DateTimeOffset.Now;
                meshStates[id] = (new(id, used, "已同步", success), version); Changed?.Invoke();
                LearnRoutes([new(id, [used])]);
                while (version == Interlocked.Read(ref localVersion) && identity.IsTrusted(id) && DateTimeOffset.Now - success < ReconcileInterval)
                {
                    if (remoteGeneration is null) break;
                    try
                    {
                        if (await WaitForPeerChange(used, id, remoteGeneration, version, token) != remoteGeneration) break;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && !token.IsCancellationRequested) { break; }
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
    }
}
