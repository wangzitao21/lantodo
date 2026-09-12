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
    await using var setup = new Server(host, nasDll, Path.Combine(temporary, "setup"), false);
    await setup.Start(); await setup.CheckSetup();
    await using var home = MakeServer("home");
    await home.Start();
    await home.CheckWeb();
    await using var choices = MakeServer("choices"); await choices.Start(); await choices.CheckChoices();
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
    await office.Command("delete-device", identity!);
    if(JsonDocument.Parse(await office.Command("status")).RootElement.GetProperty("revokedDevices").GetArrayLength()!=0)throw new Exception("Revoked device record retained");
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
    readonly int port, webPort;
    const string AdminToken = "synthetic-test-token-not-a-real-password";
    Process? process;
    readonly bool configured;
    public Server(string host, string dll, string directory, bool configured = true)
    {
        this.host = host; this.dll = dll; this.directory = directory; this.configured = configured;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); webPort = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    }
    public string Address => "127.0.0.1:" + port;
    public Process Launch(params string[] arguments) => LaunchAtPort(port, arguments);
    public Process LaunchAtPort(int listenPort, params string[] arguments)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(dll); foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment["LANTODO_DATA"] = directory; start.Environment["LANTODO_BACKUPS"] = Path.Combine(directory, "snapshots");
        start.Environment["LANTODO_PORT"] = listenPort.ToString(); start.Environment["LANTODO_NAME"] = Path.GetFileName(directory);
        start.Environment["LANTODO_WEB_PORT"] = webPort.ToString(); start.Environment["LANTODO_ADMIN_TOKEN"] = configured ? AdminToken : "";
        return Process.Start(start)!;
    }
    public async Task Start()
    {
        process = Launch("serve");
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (line?.Contains("listening") != true) throw new Exception("NAS failed to start: " + await process.StandardError.ReadToEndAsync());
    }
    public Task<string> Command(params string[] args) => CommandWithInput(null, args);
    public async Task CheckWeb()
    {
        await Command("status");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{webPort}") };
        var html = await http.GetStringAsync("/");
        if (!html.Contains("你的空间，随处同步") || html.Contains("连接另一台 NAS") || !html.Contains("mergeConsent")) throw new Exception("Unified dashboard missing");
        if ((await http.GetAsync("/api/status")).StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Anonymous status exposed");
        if ((await http.PostAsync("/api/command",new StringContent("{\"kind\":\"invite\"}",System.Text.Encoding.UTF8,"application/json"))).StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Anonymous mutation accepted");
        http.DefaultRequestHeaders.Authorization = new("Bearer",AdminToken);
        async Task<JsonDocument> Post(string json)
        {
            using var response=await http.PostAsync("/api/command",new StringContent(json,System.Text.Encoding.UTF8,"application/json"));response.EnsureSuccessStatusCode();return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        using var invitation = await Post("{\"kind\":\"invite\"}");
        var invite = DeviceIdentity.ParseSpaceInvite(invitation.RootElement.GetProperty("result").GetString()!);
        if (!Convert.FromBase64String(invitation.RootElement.GetProperty("qr").GetString()!).Take(4).SequenceEqual(new byte[] {137,80,78,71})) throw new Exception("Invite QR missing");
        var state=JsonDocument.Parse(await http.GetStringAsync("/api/status"));var id=state.RootElement.GetProperty("invites")[0].GetProperty("id").GetString();
        using var activeDelete=await http.PostAsync("/api/command",new StringContent(JsonSerializer.Serialize(new{kind="delete-invite",ids=new[]{id}}),System.Text.Encoding.UTF8,"application/json"));
        if(activeDelete.IsSuccessStatusCode)throw new Exception("Active invitation deleted without revocation");
        await Post("{\"kind\":\"cancel-invite\"}");await Post(JsonSerializer.Serialize(new{kind="delete-invite",ids=new[]{id}}));
        state=JsonDocument.Parse(await http.GetStringAsync("/api/status"));if(state.RootElement.GetProperty("invites").GetArrayLength()!=0)throw new Exception("Canceled code history not deleted");
        await Post("{\"kind\":\"new-space\",\"name\":\"测试公司\"}");
        var multiple=JsonDocument.Parse(await http.GetStringAsync("/api/status"));
        if(multiple.RootElement.GetProperty("spaces").GetArrayLength()!=2 || multiple.RootElement.GetProperty("spaceName").GetString()!="测试公司")throw new Exception("Web did not create isolated named space");
        await Post("{\"kind\":\"rename-space\",\"name\":\"新名称\"}");
        await Post("{\"kind\":\"select-space\",\"name\":\"\"}");
        if(JsonDocument.Parse(await http.GetStringAsync("/api/status")).RootElement.GetProperty("spaces").GetArrayLength()!=2)throw new Exception("Web switching lost space");
        var childRoot=multiple.RootElement.GetProperty("space").GetProperty("id").GetString();
        using var staleDelete=await http.PostAsync("/api/command",new StringContent(JsonSerializer.Serialize(new{kind="delete-space",spaceRoot=childRoot}),System.Text.Encoding.UTF8,"application/json"));
        if(staleDelete.IsSuccessStatusCode)throw new Exception("Stale web confirmation deleted a different space");
        var childKey=multiple.RootElement.GetProperty("spaces").EnumerateArray().Single(s=>s.GetProperty("selected").GetBoolean()).GetProperty("key").GetString();
        await Post(JsonSerializer.Serialize(new{kind="select-space",name=childKey}));
        await Post(JsonSerializer.Serialize(new{kind="delete-space",spaceRoot=childRoot}));
        var afterDelete=JsonDocument.Parse(await http.GetStringAsync("/api/status"));
        if(afterDelete.RootElement.GetProperty("spaces").GetArrayLength()!=1 || afterDelete.RootElement.GetProperty("space").GetProperty("id").GetString()==childRoot)throw new Exception("Web space deletion did not remove and switch space");
        if(!html.Contains("id=\"deleteSpace\""))throw new Exception("Delete space UI missing");
        var js=await http.GetStringAsync("/app.js");if(!js.Contains("textContent"))throw new Exception("Dashboard script missing");
        Console.WriteLine("PASS NAS web: embedded assets, authenticated status, anonymous mutation denied, active/canceled invite lifecycle.");
    }
    public async Task CheckChoices()
    {
        static async Task Until(Func<Task<bool>> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!await condition()) await Task.Delay(100, timeout.Token);
        }
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{webPort}") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", AdminToken);
        async Task<HttpResponseMessage> Post(Packet packet) => await http.PostAsync("/api/command", new StringContent(JsonSerializer.Serialize(packet, Json.Options), System.Text.Encoding.UTF8,"application/json"));
        using var store = new TodoStore(directory + "-client"); using var identity = new DeviceIdentity(store.Database,"测试手机");
        await using var node = new PeerNode(store,identity,0) { Replicas = new(store.Database) }; node.Start(false);
        await node.PairAddressAsync(Address,await Command("invite"));
        using (var renamed = await Post(new("rename", Name:"相册手机", Ids:[identity.Id]))) renamed.EnsureSuccessStatusCode();
        await Until(() => Task.FromResult(identity.Name == "相册手机"));
        var original = store.Save(identity.Id,identity.Name,new("待确认"));
        var left = Revision.Create(original.Body with { Nonce=Guid.NewGuid().ToString("N"),Parents=[original.Id],Data=new("左侧修改") });
        var right = Revision.Create(original.Body with { Nonce=Guid.NewGuid().ToString("N"),Parents=[original.Id],Data=new("右侧修改") });
        store.Import([left,right]);
        await Until(async () => JsonDocument.Parse(await http.GetStringAsync("/api/status")).RootElement.GetProperty("conflicts").GetArrayLength() == 1);
        using (var stale = await Post(new("resolve",Ids:[original.Body.TodoId,left.Id,left.Id])))
            if (stale.IsSuccessStatusCode) throw new Exception("Stale conflict choice overwritten new heads");
        using (var resolved = await Post(new("resolve",Name:"delete",Ids:[original.Body.TodoId,left.Id,left.Id,right.Id]))) resolved.EnsureSuccessStatusCode();
        await Until(() => Task.FromResult(store.List().Length == 0));
        if (store.History(original.Body.TodoId).Length != 4) throw new Exception("Conflict resolution lost history");
        Console.WriteLine("PASS NAS choices: web rename reaches terminal; stale selection rejected; conflict deletion converges with full history.");
    }
    public async Task CheckSetup()
    {
        await Command("status");
        using var http=new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer() }) { BaseAddress=new Uri($"http://127.0.0.1:{webPort}") };
        using var anonymous=new HttpClient { BaseAddress=http.BaseAddress };
        async Task<HttpResponseMessage> Post(HttpClient client,string path,string body="{}") => await client.PostAsync(path,new StringContent(body,System.Text.Encoding.UTF8,"application/json"));
        var first=await http.GetAsync("/api/status");
        if(first.StatusCode!=HttpStatusCode.Unauthorized)throw new Exception("First launch allowed anonymous administration");
        var initialToken=File.ReadAllText(Path.Combine(directory,"admin-token")).Trim();
        http.DefaultRequestHeaders.Authorization=new("Bearer",initialToken);
        (await Post(http,"/api/login")).EnsureSuccessStatusCode();http.DefaultRequestHeaders.Authorization=null;
        var html=await http.GetStringAsync("/");if(html.Contains("revisionCount")||html.Contains("订阅"))throw new Exception("Removed dashboard sections remain");
        using var origin=new HttpRequestMessage(HttpMethod.Post,"/api/security") { Content=new StringContent(JsonSerializer.Serialize(new { secret=AdminToken }),System.Text.Encoding.UTF8,"application/json") };
        origin.Headers.Add("Origin","https://unrelated.example");
        if((await http.SendAsync(origin)).StatusCode!=HttpStatusCode.Forbidden)throw new Exception("Cross-origin setup allowed");
        (await Post(http,"/api/security",JsonSerializer.Serialize(new { secret=AdminToken }))).EnsureSuccessStatusCode();
        if((await anonymous.GetAsync("/api/status")).StatusCode!=HttpStatusCode.Unauthorized)throw new Exception("Token setup did not protect console");
        (await http.GetAsync("/api/status")).EnsureSuccessStatusCode();
        await Crash();await Start();await Command("status");
        (await http.GetAsync("/api/status")).EnsureSuccessStatusCode();
        (await Post(http,"/api/logout")).EnsureSuccessStatusCode();
        if((await http.GetAsync("/api/status")).StatusCode!=HttpStatusCode.Unauthorized)throw new Exception("Logout failed");
        http.DefaultRequestHeaders.Authorization=new("Bearer",AdminToken);
        (await Post(http,"/api/login")).EnsureSuccessStatusCode();http.DefaultRequestHeaders.Authorization=null;
        (await http.GetAsync("/api/status")).EnsureSuccessStatusCode();
        (await Post(http,"/api/security",JsonSerializer.Serialize(new { secret=AdminToken+"-changed" }))).EnsureSuccessStatusCode();
        anonymous.DefaultRequestHeaders.Authorization=new("Bearer",AdminToken);
        if((await anonymous.GetAsync("/api/status")).StatusCode!=HttpStatusCode.Unauthorized)throw new Exception("Old token remains valid");
        Console.WriteLine("PASS NAS setup: protected first use, same-origin setup, token enforcement, persistent session across restart, logout/login and token rotation.");
    }
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
