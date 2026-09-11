using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LanTodo.Core;

public sealed record ReplicaEndpoint(string DeviceId, string Address);
public sealed record ReplicaConfiguration(bool NasEnabled = false, bool LanEnabled = true, ReplicaEndpoint[]? Endpoints = null);
public sealed record ReplicaStatus(string DeviceId, string Address, string State, DateTimeOffset? LastSuccess = null, string? Error = null);

// Addresses are routing hints only; every connection still pins the paired certificate.
public static class ReplicaAddress
{
    public static Uri Parse(string address)
    {
        address = address.Trim();
        if (address.Length is 0 or > 512 || address.Contains('\\') ||
            !Uri.TryCreate("tcp://" + address, UriKind.Absolute, out var uri) ||
            uri.HostNameType == UriHostNameType.Unknown || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port is < 1 or > 65535)
            throw new InvalidDataException("请输入设备的 IP 或域名及同步端口，例如 device.home:42851。");
        return uri;
    }

    public static async Task<IPEndPoint[]> Resolve(string address, CancellationToken token)
    {
        var uri = Parse(address);
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, token);
        var endpoints = addresses.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Select(a => new IPEndPoint(a, uri.Port)).ToArray();
        if (endpoints.Length == 0) throw new IOException("设备地址没有可用的 IP。");
        return endpoints;
    }
}

public sealed class ReplicaSettings
{
    private const string Key = "replica-settings-v1.json";
    private readonly IProfileDatabase database;
    private readonly object gate = new();
    private ReplicaConfiguration configuration;
    public event Action? Changed;
    public ReplicaSettings(IProfileDatabase database)
    {
        this.database = database;
        configuration = database.ReadMetadata(Key) is { } bytes ? Json.Read<ReplicaConfiguration>(bytes) : new();
        Validate(configuration);
    }
    public ReplicaConfiguration Current { get { lock (gate) return configuration with { Endpoints = configuration.Endpoints?.ToArray() ?? [] }; } }
    private static void Validate(ReplicaConfiguration value)
    {
        var endpoints = value.Endpoints ?? [];
        if (endpoints.Length > 16 || endpoints.Select(e => e.DeviceId).Distinct().Count() != endpoints.Length)
            throw new InvalidDataException("最多配置 16 个不同的 NAS 节点。");
        foreach (var endpoint in endpoints)
        {
            if (!Json.IsHash(endpoint.DeviceId)) throw new InvalidDataException("NAS 身份无效。");
            _ = ReplicaAddress.Parse(endpoint.Address);
        }
    }
    private void Update(Func<ReplicaConfiguration, ReplicaConfiguration> change)
    {
        lock (gate)
        {
            var next = change(configuration);
            Validate(next);
            database.WriteMetadata(Key, JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
            configuration = next;
        }
        Changed?.Invoke();
    }
    public void SetMode(bool nasEnabled, bool lanEnabled) => Update(c => c with { NasEnabled = nasEnabled, LanEnabled = lanEnabled });
    public void SetEndpoint(string deviceId, string address) => Update(c => c with
    {
        NasEnabled = true,
        Endpoints = (c.Endpoints ?? []).Where(e => e.DeviceId != deviceId).Append(new(deviceId, address.Trim())).ToArray()
    });
    public void RemoveEndpoint(string deviceId) => Update(c => c with { Endpoints = (c.Endpoints ?? []).Where(e => e.DeviceId != deviceId).ToArray() });
}
