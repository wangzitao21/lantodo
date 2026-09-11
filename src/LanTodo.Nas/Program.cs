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
        Console.WriteLine("LanTodo NAS: serve | status | invite | cancel-invite | pair [HOST:PORT] (invite on stdin, merges local data) | address DEVICE_ID HOST:PORT | remove-address DEVICE_ID | revoke DEVICE_ID | upgrade-space | leave-space | delete-space | new-space | public-address HOST:PORT | backup\nEnvironment: LANTODO_DATA, LANTODO_BACKUPS, LANTODO_NAME, LANTODO_PORT (42851)");
        return 0;
    }
    if (command != "serve")
    {
        var request = command switch
        {
            "status" or "invite" or "cancel-invite" or "backup" or "upgrade-space" or "leave-space" or "delete-space" or "new-space" when args.Length == 1 => new Packet(command),
            "public-address" when args.Length == 2 => new Packet(command, Name: args[1]),
            "pair" when args.Length == 1 => new Packet(command, Secret: (await Console.In.ReadLineAsync())?.Trim()),
            "pair" when args.Length == 2 => new Packet(command, Name: args[1], Secret: (await Console.In.ReadLineAsync())?.Trim()),
            "address" when args.Length == 3 => new Packet(command, Name: args[2], Ids: [args[1]]),
            "remove-address" or "revoke" or "delete-device" when args.Length == 2 => new Packet(command, Ids: [args[1]]),
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
    var port = int.Parse(Environment.GetEnvironmentVariable("LANTODO_PORT") ?? "42851");
    if(port is <1024 or >65535)throw new ArgumentException("同步端口无效。");
    await using var app=new AppRuntime(dataPath,Environment.GetEnvironmentVariable("LANTODO_NAME")??"LanTodo NAS",port);
    await app.SetNetworkAsync(true);
    if(app.Node.Port==0)throw new IOException("同步监听未启动："+app.Node.LastError);
    Directory.CreateDirectory(backupPath);
    string? backupError = null;
    Console.WriteLine($"LanTodo NAS listening on TCP {app.Node.Port}; device {app.Identity.Id}.");

    string Backup(bool automatic)
    {
        if(automatic){app.BackupSpaces(backupPath,true);backupError=null;return backupPath;}
        var store=app.Store;var path=Path.Combine(backupPath,$"manual-{app.Identity.Space?.Root[..12]}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.lantodo.zip");
        store.Backup(path);backupError=null;return path;
    }

    using var commandGate=new SemaphoreSlim(1);
    async Task<Packet> Execute(Packet request,CancellationToken token)
    {
        await commandGate.WaitAsync(token);
        try{return await ExecuteCore(request,token);}
        finally{commandGate.Release();}
    }
    async Task<Packet> ExecuteCore(Packet request, CancellationToken token)
    {
        var store=app.Store;var identity=app.Identity;var node=app.Node;var replicas=app.Replicas;
        if(request.SpaceRoot is not null && request.SpaceRoot != identity.Space?.Root && request.Kind != "select-space")throw new InvalidOperationException("当前连接空间已改变，请刷新后重试。");
        switch (request.Kind)
        {
            case "invite":
                if (!string.IsNullOrWhiteSpace(request.Name)) node.SetAdvertisedAddress(request.Name);
                return new("ok", Name: node.CreateInvite());
            case "upgrade-space": identity.EnsureSpace(true); break;
            case "leave-space": identity.LeaveSpace(); break;
            case "delete-space": await app.DeleteSpaceAsync(app.ActiveSpaceKey); break;
            case "new-space": await app.CreateSpaceAsync(request.Name ?? "新空间"); break;
            case "select-space": app.SelectSpace(request.Name ?? ""); break;
            case "rename-space": app.RenameSpace(request.Name ?? ""); break;
            case "public-address": node.SetAdvertisedAddress(request.Name ?? ""); break;
            case "cancel-invite": identity.CancelInvite(); break;
            case "delete-invite": identity.DeleteInviteRecord(request.Ids?.Single() ?? ""); break;
            case "delete-device": identity.DeleteRevokedDevice(request.Ids?.Single() ?? ""); break;
            case "rename": if((request.Ids?.Single()??identity.Id)==identity.Id)app.RenameSelf(request.Name??"");else identity.Rename(request.Ids!.Single(),request.Name??""); break;
            case "resolve":
                var choice = request.Ids ?? [];
                if (choice.Length < 3) throw new InvalidDataException("请选择版本。");
                var todo = store.List().FirstOrDefault(t => t.Id == choice[0] && t.Conflict) ?? throw new StaleEditException();
                var selected = todo.Heads.FirstOrDefault(h => h.Id == choice[1]) ?? throw new StaleEditException();
                store.Save(identity.Id, identity.Name, request.Name == "delete" ? selected.Body.Data with { Deleted = true } : selected.Body.Data, todo.Id, choice.Skip(2).ToArray());
                break;
            case "sync": app.RequestSync(); break;
            case "status": return new("ok", Name: JsonSerializer.Serialize(new
            {
                spaces=app.Spaces, spaceName=identity.SpaceName,
                name = identity.Name, deviceId = identity.Id, port = node.Port, revisionCount = store.Export().Length,
                space = identity.Space is { } space ? new { id = space.Root, active = space.Contains(identity.Id), members = space.Members.Length } : null,
                needsUpgrade = identity.NeedsSpaceUpgrade, advertisedAddress = node.AdvertisedAddress,
                devices = identity.Devices.Select(d => new { d.Id, d.Name, d.PairedUtc, state = node.DeviceState(d.Id) }), revokedDevices = identity.RevokedDevices, invites = identity.Invites,
                conflicts = store.List().Where(t => t.Conflict).Select(t => new { t.Id, versions = t.VersionIds, heads = t.Heads.Select(h => new { h.Id, name = app.DisplayName(h.Body.Actor, h.Body.DeviceName), h.Body.CreatedUtc, h.Body.Data }) }),
                nodes = node.ReplicaStatuses, backupError, lastSync = node.LastSync, syncStatus = app.Status,
                unbound = node.Nearby.Where(p => !identity.IsTrusted(p.Id)).Select(p => new { p.Id, address = p.Endpoint.ToString() }), version = "1.0.1"
            }, Json.Options));
            case "pair": await app.JoinSpaceAsync(request.Secret ?? "", request.Name, token); break;
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
            // CLI management stays local to this OS user. The optional HTTP UI uses separate bearer authentication.
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
            try { Backup(true); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { backupError = ex.GetType().Name; Console.Error.WriteLine("Automatic backup failed: " + backupError); }
            await Task.Delay(TimeSpan.FromMinutes(1), stop.Token);
        }
    }
    await using var web = await AdminWeb.Start(dataPath, Execute, stop.Token);
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
