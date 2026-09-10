using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanTodo.Core;

var dataPath = Path.GetFullPath(Environment.GetEnvironmentVariable("LANTODO_DATA") ?? "/data");
var backupPath = Path.GetFullPath(Environment.GetEnvironmentVariable("LANTODO_BACKUPS") ?? Path.Combine(dataPath, "backups"));
var pipePath = OperatingSystem.IsWindows() ? dataPath.ToUpperInvariant() : dataPath;
var pipeName = "lantodo-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pipePath)))[..24];
var command = args.FirstOrDefault() ?? "serve";
try
{
    if (command is "help" or "--help")
    {
        Console.WriteLine("LanTodo NAS: serve | status | invite | cancel-invite | pair HOST:PORT (invite on stdin) | address DEVICE_ID HOST:PORT | remove-address DEVICE_ID | revoke DEVICE_ID | backup\nEnvironment: LANTODO_DATA, LANTODO_BACKUPS, LANTODO_NAME, LANTODO_PORT (42851)");
        return 0;
    }
    if (command != "serve")
    {
        var request = command switch
        {
            "status" or "invite" or "cancel-invite" or "backup" when args.Length == 1 => new Packet(command),
            "pair" when args.Length == 2 => new Packet(command, Name: args[1], Secret: (await Console.In.ReadLineAsync())?.Trim()),
            "address" when args.Length == 3 => new Packet(command, Name: args[2], Ids: [args[1]]),
            "remove-address" or "revoke" when args.Length == 2 => new Packet(command, Ids: [args[1]]),
            _ => throw new ArgumentException("命令参数无效，请运行 help。")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, timeout.Token);
        await Wire.Write(pipe, request, timeout.Token);
        // Pairing/export can take longer than one frame deadline; the control server acknowledges first.
        var reply = await Wire.Read(pipe, timeout.Token);
        if (reply.Kind != "accepted") throw new IOException("管理通道响应无效。");
        while ((reply = await Wire.Read(pipe, timeout.Token)).Kind == "working") { }
        Console.WriteLine(reply.Name ?? "完成");
        return 0;
    }

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
    using var store = new TodoStore(dataPath);
    using var identity = new DeviceIdentity(store.Database, Environment.GetEnvironmentVariable("LANTODO_NAME") ?? "LanTodo NAS");
    var replicas = new ReplicaSettings(store.Database);
    var port = int.Parse(Environment.GetEnvironmentVariable("LANTODO_PORT") ?? "42851");
    if (port is < 1024 or > 65535) throw new ArgumentException("LANTODO_PORT 必须为 1024–65535。");
    await using var node = new PeerNode(store, identity, port) { Replicas = replicas };
    node.Start(enableDiscovery: false);
    if (node.Port == 0) throw new IOException("同步监听未启动：" + node.LastError);
    Directory.CreateDirectory(backupPath);
    string? backupError = null;
    Console.WriteLine($"LanTodo NAS listening on TCP {node.Port}; device {identity.Id}. Use 'invite' to pair.");

    string Backup(bool automatic)
    {
        var name = automatic ? $"daily-{DateTime.UtcNow:yyyyMMdd}.lantodo.zip" : $"manual-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.lantodo.zip";
        var path = Path.Combine(backupPath, name);
        if (!automatic || !File.Exists(path)) store.Backup(path);
        backupError = null;
        return path;
    }
    async Task<Packet> Execute(Packet request, CancellationToken token)
    {
        switch (request.Kind)
        {
            case "invite": return new("ok", Name: identity.CreateInvite());
            case "cancel-invite": identity.CancelInvite(); break;
            case "status": return new("ok", Name: JsonSerializer.Serialize(new
            {
                name = identity.Name, deviceId = identity.Id, port = node.Port, revisionCount = store.Export().Length,
                devices = identity.Devices, nodes = node.ReplicaStatuses, backupError
            }, Json.Options));
            case "pair": await node.PairAddressAsync(request.Name ?? "", request.Secret ?? "", token); break;
            case "address":
                var id = request.Ids?.Single() ?? "";
                if (!identity.IsTrusted(id)) throw new UnauthorizedAccessException("请先配对此设备。");
                replicas.SetEndpoint(id, request.Name ?? ""); break;
            case "remove-address": replicas.RemoveEndpoint(request.Ids?.Single() ?? ""); break;
            case "revoke":
                var revoked = request.Ids?.Single() ?? "";
                identity.Revoke(revoked); replicas.RemoveEndpoint(revoked); break;
            case "backup": return new("ok", Name: Backup(false));
            default: throw new InvalidDataException("未知管理命令。");
        }
        return new("ok", Name: "配置已保存；可通过 status 查看同步状态。");
    }
    async Task ControlLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            // Same OS user only; never expose administrative commands on a TCP port.
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(stop.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(55));
            try
            {
                var request = await Wire.Read(pipe, deadline.Token);
                await Wire.Write(pipe, new("accepted"), deadline.Token);
                var work = Task.Run(() => Execute(request, deadline.Token));
                try
                {
                    while (!work.IsCompleted)
                    {
                        await Task.WhenAny(work, Task.Delay(5000, deadline.Token));
                        deadline.Token.ThrowIfCancellationRequested();
                        if (!work.IsCompleted) await Wire.Write(pipe, new("working"), deadline.Token);
                    }
                    await Wire.Write(pipe, await work, deadline.Token);
                }
                finally { deadline.Cancel(); try { await work; } catch { } }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                try { await Wire.Write(pipe, new("error", Error: ex.Message), stop.Token); } catch { }
            }
        }
    }
    async Task BackupLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try { if (store.Export().Length > 0) Backup(true); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { backupError = ex.GetType().Name; Console.Error.WriteLine("Automatic backup failed: " + backupError); }
            await Task.Delay(TimeSpan.FromMinutes(1), stop.Token);
        }
    }
    var controls = ControlLoop(); var backups = BackupLoop();
    try { await Task.WhenAny(controls, backups); }
    finally { stop.Cancel(); }
    try { await Task.WhenAll(controls, backups); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("LanTodo NAS: " + ex.Message);
    return 1;
}
