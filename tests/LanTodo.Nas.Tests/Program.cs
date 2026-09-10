using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanTodo.Core;

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "LanTodo.sln"))) root = root.Parent;
if (root is null) throw new Exception("Repository root not found");
var nasDll = Path.Combine(root.FullName, "src/LanTodo.Nas/bin/Release/net10.0/LanTodo.Nas.dll");
var host = OperatingSystem.IsWindows() && File.Exists(Path.Combine(root.FullName, ".tools/dotnet/dotnet.exe"))
    ? Path.Combine(root.FullName, ".tools/dotnet/dotnet.exe") : "dotnet";
var temporary = Path.Combine(Path.GetTempPath(), "lantodo-nas-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    await using var home = MakeServer("home");
    await home.Start();
    var initial = JsonDocument.Parse(await home.Command("status"));
    var identity = initial.RootElement.GetProperty("deviceId").GetString();
    using (var duplicate = home.LaunchAtPort(42858, "serve"))
    {
        await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (duplicate.ExitCode == 0) throw new Exception("Second server opened the same data directory");
    }
    var firstInvite = await home.Command("invite");
    await home.Command("cancel-invite");
    using (var store = new TodoStore(Path.Combine(temporary, "phone")))
    using (var phoneIdentity = new DeviceIdentity(store.Database, "phone"))
    {
        var settings = new ReplicaSettings(store.Database);
        await using var phone = new PeerNode(store, phoneIdentity, 0) { Replicas = settings };
        phone.Start(false);
        try { await phone.PairAddressAsync(home.Address, firstInvite); throw new Exception("Canceled invitation accepted"); }
        catch (IOException) { }
        await phone.PairAddressAsync(home.Address, await home.Command("invite"));
        store.Save(phoneIdentity.Id, "phone", new TodoData("进程级持久化测试"));
        await Until(async () => JsonDocument.Parse(await home.Command("status")).RootElement.GetProperty("revisionCount").GetInt32() == 1);
    }
    var backup = await home.Command("backup");
    if (!File.Exists(backup)) throw new Exception("Manual backup missing");
    using (var restored = new TodoStore(Path.Combine(temporary, "restore")))
    {
        restored.Restore(backup);
        if (restored.List().Single().Data.Title != "进程级持久化测试") throw new Exception("Backup cannot restore history");
    }
    await home.Crash(); await home.Start();
    var restarted = JsonDocument.Parse(await home.Command("status"));
    if (restarted.RootElement.GetProperty("deviceId").GetString() != identity || restarted.RootElement.GetProperty("revisionCount").GetInt32() != 1)
        throw new Exception("Server restart lost identity/history");
    await Until(() => Task.FromResult(Directory.GetFiles(Path.Combine(temporary, "home", "snapshots"), "daily-*.lantodo.zip").Length > 0));
    await using var office = MakeServer("office"); await office.Start();
    await office.CommandWithInput(await home.Command("invite"), "pair", home.Address);
    await Until(async () => JsonDocument.Parse(await office.Command("status")).RootElement.GetProperty("revisionCount").GetInt32() == 1);
    using (var pc = new TodoStore(Path.Combine(temporary, "pc")))
    using (var pcIdentity = new DeviceIdentity(pc.Database, "pc"))
    {
        await using var node = new PeerNode(pc, pcIdentity, 0) { Replicas = new(pc.Database) };
        node.Start(false);
        await node.PairAddressAsync(office.Address, await office.Command("invite"));
        await Until(() => Task.FromResult(pc.Export().Length == 1));
    }
    await office.Command("remove-address", identity!);
    await office.Command("revoke", identity!);
    Console.WriteLine("PASS NAS process: administrative pipe, canceled pairing, profile exclusion, upload then exit, backup restore, crash/restart identity, dual NAS relay and later client download.");
    return 0;
}
finally
{
    // Windows can retain process-owned file handles briefly after termination is signaled.
    for (int attempt = 0; ; attempt++)
    {
        try { Directory.Delete(temporary, true); break; }
        catch (IOException) when (attempt < 20) { await Task.Delay(250); }
    }
}

async Task Until(Func<Task<bool>> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    while (!await condition()) await Task.Delay(200, timeout.Token);
}

// Process helper uses only synthetic profiles; killed processes are exactly those created by this test.
// Values passed from top-level through the constructor factory below are explicit and never shell-evaluated.
Server MakeServer(string name) => new(host, nasDll, Path.Combine(temporary, name));

sealed class Server : IAsyncDisposable
{
    readonly string host, dll, directory;
    readonly int port;
    Process? process;
    public Server(string host, string dll, string directory)
    {
        this.host = host; this.dll = dll; this.directory = directory;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    }
    public string Address => "127.0.0.1:" + port;
    public Process Launch(params string[] arguments) => LaunchAtPort(port, arguments);
    public Process LaunchAtPort(int listenPort, params string[] arguments)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(dll); foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment["LANTODO_DATA"] = directory; start.Environment["LANTODO_BACKUPS"] = Path.Combine(directory, "snapshots");
        start.Environment["LANTODO_PORT"] = listenPort.ToString(); start.Environment["LANTODO_NAME"] = Path.GetFileName(directory);
        return Process.Start(start)!;
    }
    public async Task Start()
    {
        process = Launch("serve");
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (line?.Contains("listening") != true) throw new Exception("NAS failed to start: " + await process.StandardError.ReadToEndAsync());
    }
    public Task<string> Command(params string[] args) => CommandWithInput(null, args);
    public async Task<string> CommandWithInput(string? input, params string[] args)
    {
        using var child = Launch(args);
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        if (input is not null) await child.StandardInput.WriteLineAsync(input);
        child.StandardInput.Close();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(65)); }
        catch { child.Kill(); throw; }
        if (child.ExitCode != 0) throw new Exception(await error);
        return (await output).Trim();
    }
    public async Task Crash()
    {
        if (process is null) return;
        if (!process.HasExited) process.Kill();
        await process.WaitForExitAsync(); process.Dispose(); process = null;
    }
    public async ValueTask DisposeAsync() => await Crash();
}
