using System.Buffers.Binary;
using System.Text.Json;

namespace LanTodo.Core;

public sealed record Packet(string Kind, string? Name = null, string? Secret = null, string[]? Ids = null,
    Revision[]? Revisions = null, int Offset = 0, bool Done = false, string? Error = null, int Protocol = 1);

public static class Wire
{
    public const int MaxFrame = 2 * 1024 * 1024;
    public static async Task Write(Stream stream, Packet packet, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, Json.Options);
        if (bytes.Length > MaxFrame) throw new InvalidDataException("同步消息过大。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, timeout.Token);
        await stream.WriteAsync(bytes, timeout.Token);
        await stream.FlushAsync(timeout.Token);
    }
    public static async Task<Packet> Read(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var header = new byte[4];
        await ReadExact(stream, header, timeout.Token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxFrame) throw new InvalidDataException("同步消息长度无效。");
        var bytes = new byte[length];
        await ReadExact(stream, bytes, timeout.Token);
        var packet = Json.Read<Packet>(bytes);
        if (packet.Protocol != 1 || packet.Kind is null) throw new InvalidDataException("同步协议版本不兼容。");
        if (packet.Error is not null) throw new IOException(packet.Error);
        return packet;
    }
    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (n == 0) throw new EndOfStreamException("连接中断，已保存的数据不受影响。");
            offset += n;
        }
    }
}
