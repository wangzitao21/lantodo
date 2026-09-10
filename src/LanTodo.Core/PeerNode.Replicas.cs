using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LanTodo.Core;

public sealed partial class PeerNode
{
    public ReplicaSettings? Replicas { get; init; }
    private readonly ConcurrentDictionary<string, (ReplicaStatus Status, long Version)> replicaStates = new();
    private readonly object replicaWakeGate = new();
    private TaskCompletionSource replicaWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void WakeReplicas()
    {
        lock (replicaWakeGate)
        {
            var previous = replicaWake;
            replicaWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }
    private bool HasFixedPeers => Replicas?.Current is { NasEnabled: true, Endpoints.Length: > 0 };
    private bool IsFixedPeer(string id) => Replicas?.Current is { NasEnabled: true } c && c.Endpoints!.Any(e => e.DeviceId == id);
    public ReplicaStatus[] ReplicaStatuses => (Replicas?.Current.Endpoints ?? []).Select(e =>
    {
        if (!identity.IsTrusted(e.DeviceId)) return new ReplicaStatus(e.DeviceId, e.Address, "未授权，请重新配对");
        if (Replicas?.Current.NasEnabled != true) return new ReplicaStatus(e.DeviceId, e.Address, "NAS 同步已暂停");
        if (lifetime is null) return new ReplicaStatus(e.DeviceId, e.Address, "网络已停止");
        if (!replicaStates.TryGetValue(e.DeviceId, out var state) || state.Status.Address != e.Address)
            return new ReplicaStatus(e.DeviceId, e.Address, "等待连接");
        return state.Version != Interlocked.Read(ref localVersion) && state.Status.State == "已同步"
            ? state.Status with { State = "已保存本机 · 等待此节点确认" } : state.Status;
    }).ToArray();

    public async Task PairAddressAsync(string address, string code, CancellationToken token = default)
    {
        if (Replicas is null) throw new InvalidOperationException("未配置 NAS 设置存储。");
        var invite = DeviceIdentity.ParseInvite(code);
        if (invite.DeviceId == identity.Id) throw new InvalidDataException("不能与本机配对。");
        _ = ReplicaAddress.Parse(address);
        if (Replicas.Current.Endpoints!.Length >= 16 && !Replicas.Current.Endpoints.Any(e => e.DeviceId == invite.DeviceId))
            throw new InvalidDataException("最多配置 16 个 NAS 节点。");
        await AtAddress(address, async endpoint =>
        {
            using var tcp = new TcpClient();
            using var tls = await Connect(tcp, endpoint, invite.DeviceId, token);
            await Wire.Write(tls, new("pair", Name: identity.Name, Secret: invite.Secret), token);
            if ((await Wire.Read(tls, token)).Kind != "paired") throw new IOException("配对失败。");
            identity.Trust(invite.DeviceId, invite.Name);
            return true;
        }, token);
        // Persist immediately after authorization; a failed first transfer must not lose the route.
        Replicas.SetEndpoint(invite.DeviceId, address);
        RequestSync();
    }

    private static async Task<T> AtAddress<T>(string address, Func<IPEndPoint, Task<T>> action, CancellationToken token)
    {
        using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        dnsTimeout.CancelAfter(TimeSpan.FromSeconds(12));
        var endpoints = await ReplicaAddress.Resolve(address, dnsTimeout.Token);
        Exception? last = null;
        foreach (var endpoint in endpoints)
        {
            token.ThrowIfCancellationRequested();
            try { return await action(endpoint); }
            catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); last = ex; }
        }
        throw new IOException("无法连接已配对节点，请检查地址、网络及证书身份。", last);
    }

    private async Task FixedPeersLoop(CancellationToken token)
    {
        var workers = new Dictionary<ReplicaEndpoint, (CancellationTokenSource Stop, Task Task)>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var config = Replicas?.Current;
                var desired = (config?.NasEnabled == true ? config.Endpoints! : [])
                    .Where(e => identity.IsTrusted(e.DeviceId)).ToHashSet();
                foreach (var obsolete in workers.Keys.Where(e => !desired.Contains(e)).ToArray())
                {
                    var worker = workers[obsolete]; worker.Stop.Cancel();
                    await worker.Task; worker.Stop.Dispose(); workers.Remove(obsolete);
                    replicaStates.TryRemove(obsolete.DeviceId, out _);
                }
                foreach (var endpoint in desired.Where(e => !workers.ContainsKey(e)))
                {
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                    workers.Add(endpoint, (stop, Task.Run(() => FixedPeerLoop(endpoint, stop.Token))));
                }
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            foreach (var worker in workers.Values) worker.Stop.Cancel();
            await Task.WhenAll(workers.Values.Select(w => w.Task));
            foreach (var worker in workers.Values) worker.Stop.Dispose();
            replicaStates.Clear();
        }
    }

    private async Task FixedPeerLoop(ReplicaEndpoint peer, CancellationToken token)
    {
        var failures = 0;
        DateTimeOffset? lastSuccess = null;
        void Status(string state, long version = -1, string? error = null)
        {
            replicaStates[peer.DeviceId] = (new(peer.DeviceId, peer.Address, state, lastSuccess, error), version);
            Changed?.Invoke();
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                var version = Interlocked.Read(ref localVersion);
                try
                {
                    Status("正在同步");
                    var remoteGeneration = await AtAddress(peer.Address, endpoint => SyncCoreAsync(endpoint, peer.DeviceId, token), token);
                    lastSuccess = DateTimeOffset.Now; failures = 0;
                    Status("已同步", version);
                    while (version == Interlocked.Read(ref localVersion) && DateTimeOffset.Now - lastSuccess.Value < ReconcileInterval)
                    {
                        // Older peers have no watch capability; preserve compatibility with bounded polling.
                        if (remoteGeneration is null) { await WaitForLocalChange(version, TimeSpan.FromSeconds(30), token); break; }
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var local = WaitForLocalChange(version, ReconcileInterval, wait.Token);
                        var remote = AtAddress(peer.Address, async endpoint =>
                        {
                            using var tcp = new TcpClient();
                            using var tls = await Connect(tcp, endpoint, peer.DeviceId, wait.Token);
                            await Wire.Write(tls, new("watch", Generation: remoteGeneration), wait.Token);
                            var reply = await Wire.Read(tls, wait.Token);
                            if (!identity.IsTrusted(peer.DeviceId)) throw new UnauthorizedAccessException("节点授权已取消。");
                            if (reply.Kind != "changed" || reply.Generation is null || reply.Generation.Length > 64)
                                throw new InvalidDataException("节点变化通知无效。");
                            return reply.Generation;
                        }, wait.Token);
                        var completed = await Task.WhenAny(local, remote);
                        wait.Cancel();
                        try { await local; } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                        string? next = null;
                        try { next = await remote; } catch (OperationCanceledException) when (wait.IsCancellationRequested && completed == local) { }
                        if (completed == local || next != remoteGeneration) break;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && !token.IsCancellationRequested)
                {
                    Status("等待重连", error: ex.Message);
                    failures = Math.Min(6, failures + 1);
                    await WaitForLocalChange(Interlocked.Read(ref localVersion), TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, failures - 1))), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (token.IsCancellationRequested) { }
    }

    private async Task WaitForLocalChange(long version, TimeSpan delay, CancellationToken token)
    {
        Task signal;
        lock (replicaWakeGate) signal = replicaWake.Task;
        if (Interlocked.Read(ref localVersion) != version) return;
        try { await signal.WaitAsync(delay, token); }
        catch (TimeoutException) { }
    }
}
