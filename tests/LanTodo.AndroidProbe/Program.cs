using System.Net;
using LanTodo.Core;

// Explicit Android integration fixture, never uses the user's normal data directory.
// Pair: pair <isolated directory> <forwarded localhost port> <invite file>
// Later launches: sync <same directory> <forwarded localhost port>
if (args.Length < 3 || args[0] is not ("pair" or "sync" or "send")) throw new ArgumentException("Use pair|sync|send <isolated directory> <forwarded localhost port> [invite file or test title]");
using var store = new TodoStore(Path.GetFullPath(args[1]));
using var identity = new DeviceIdentity(store.Database, "Windows 集成测试");
await using var node = new PeerNode(store, identity, 0);
var endpoint = new IPEndPoint(IPAddress.Loopback, int.Parse(args[2]));
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
if (args[0] == "pair")
{
    store.Save(identity.Id, identity.Name, new TodoData("Windows 到 Android 同步测试", "这是一条自动测试数据"));
    await node.PairAtAsync(endpoint, DeviceIdentity.ParseInvite(File.ReadAllText(args[3])), timeout.Token);
}
else
{
    if (args[0] == "send") store.Save(identity.Id, identity.Name, new TodoData(args[3]));
    await node.SyncAsync(endpoint, identity.Devices.Single().Id, timeout.Token);
}
Console.WriteLine("SYNC_OK");
foreach (var view in store.List()) Console.WriteLine($"{view.Data.Title} | completed={view.Data.Completed} | conflict={view.Conflict}");
