using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;

namespace LanTodo.Core;

public sealed partial class PeerNode
{
    private readonly ConcurrentDictionary<Guid, (string Peer, CancellationTokenSource Stop)> connecting = new();
    private long connectionVersion;
    private sealed class PeerConnection(TcpClient tcp, SslStream stream, string address) : IDisposable
    {
        public SslStream Stream { get; } = stream;
        public string Address { get; } = address;
        public void Dispose() { Stream.Dispose(); tcp.Dispose(); }
    }
    public void RequestReconnect()
    {
        Interlocked.Increment(ref connectionVersion);
        CancelConnecting();
        RequestSync();
    }
    private void CancelConnecting(string? peer = null)
    {
        foreach (var attempt in connecting.Values.Where(a => peer is null || a.Peer == peer))
            try { attempt.Stop.Cancel(); } catch (ObjectDisposedException) { }
    }
    private async Task<PeerConnection> ConnectPeer(string id, CancellationToken token, string? fixedAddress = null)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            var key = Guid.NewGuid(); connecting[key] = (id, attempt);
            try { return await RaceRoutes(fixedAddress is null ? AddressesFor(id) : [fixedAddress], id, attempt.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt.IsCancellationRequested) { }
            finally { connecting.TryRemove(key, out _); }
        }
    }
    private async Task<PeerConnection> RaceRoutes(string[] addresses, string id, CancellationToken token)
    {
        if (addresses.Length == 0) throw new IOException("等待发现设备地址。");
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var winner = new TaskCompletionSource<PeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        int next = -1; Exception? last = null; PeerConnection? selected = null;
        async Task Dial(int lane)
        {
            try
            {
                // The usual route gets a head start. At most three TLS handshakes
                // run at once, and only the authenticated winner exchanges data.
                if (lane != 0) await Task.Delay(lane * 150, race.Token);
                while (!race.IsCancellationRequested)
                {
                    int index = Interlocked.Increment(ref next);
                    if (index >= addresses.Length) return;
                    try
                    {
                        var address = addresses[index];
                        var connection = await AtAddress(address, async endpoint =>
                        {
                            var tcp = new TcpClient();
                            try { return new PeerConnection(tcp, await Connect(tcp, endpoint, id, race.Token), address); }
                            catch { tcp.Dispose(); throw; }
                        }, race.Token);
                        if (!winner.TrySetResult(connection)) connection.Dispose();
                        return;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && !race.IsCancellationRequested) { last = ex; }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && race.IsCancellationRequested) { }
        }
        var all = Task.WhenAll(Enumerable.Range(0, Math.Min(3, addresses.Length)).Select(Dial));
        try
        {
            await Task.WhenAny(winner.Task, all);
            token.ThrowIfCancellationRequested();
            if (winner.Task.IsCompletedSuccessfully) return selected = await winner.Task;
            await all;
            throw new IOException("无法连接已配对设备，请检查网络及地址。", last);
        }
        finally
        {
            race.Cancel();
            try { await all; } catch (OperationCanceledException) when (race.IsCancellationRequested) { }
            if (winner.Task.IsCompletedSuccessfully && !ReferenceEquals(winner.Task.Result, selected)) winner.Task.Result.Dispose();
        }
    }
}
