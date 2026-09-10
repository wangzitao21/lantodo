using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace LanTodo.Windows;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();
    private readonly string pipeName;
    public bool IsPrimary { get; }
    public SingleInstance(string dataPath)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataPath)).ToUpperInvariant())))[..24];
        pipeName = "LanTodo-" + key;
        mutex = new Mutex(true, "Local\\" + pipeName, out bool created);
        IsPrimary = created;
    }
    public async Task SignalAsync(byte command = 1)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(timeout.Token);
        await client.WriteAsync(new[] { command }, timeout.Token);
    }
    public void Listen(Action<byte> command)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    var bytes = new byte[1];
                    if (await server.ReadAsync(bytes, deadline.Token) == 1) command(bytes[0]);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        });
    }
    public void Dispose()
    {
        stop.Cancel();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
