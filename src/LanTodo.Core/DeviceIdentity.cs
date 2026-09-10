using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LanTodo.Core;

public sealed record TrustedDevice(string Id, string Name, string PairedUtc);
public sealed record PairingInvite(int Protocol, string DeviceId, string Name, string Secret);

public sealed class DeviceIdentity : IDisposable
{
    private readonly object gate = new();
    private readonly IProfileDatabase database;
    private Dictionary<string, TrustedDevice> trusted;
    private PairingInvite? invite;
    private DateTimeOffset inviteExpiry;
    public X509Certificate2 Certificate { get; }
    public string Id { get; }
    public string Name { get; }
    public event Action? TrustChanged;

    public DeviceIdentity(IProfileDatabase database, string defaultName)
    {
        this.database = database;
        var key = database.ReadMetadata("identity.pfx");
        if (key is null)
        {
            // Refuse identity replacement when a trust store exists: missing key is a recovery event.
            if (database.ReadMetadata("trusted.json") is not null) throw new InvalidDataException("设备密钥丢失，请保留数据目录并恢复备份。");
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=LanTodo", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
            key = cert.Export(X509ContentType.Pfx);
            database.WriteMetadata("identity.pfx", key);
        }
        // Windows Schannel needs a CNG key container; ephemeral keys fail during TLS credential acquisition.
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        Certificate = X509CertificateLoader.LoadPkcs12(key, null, flags);
        if (!Certificate.HasPrivateKey) throw new InvalidDataException("设备密钥无效。");
        Id = Fingerprint(Certificate);
        var name = database.ReadMetadata("name.json");
        if (name is null) { name = JsonSerializer.SerializeToUtf8Bytes(defaultName[..Math.Min(defaultName.Length, 100)], Json.Options); database.WriteMetadata("name.json", name); }
        Name = Json.Read<string>(name);
        var trust = database.ReadMetadata("trusted.json");
        trusted = trust is null ? new() : Json.Read<Dictionary<string, TrustedDevice>>(trust);
        if (trusted.Any(p => p.Key != p.Value.Id || !Json.IsHash(p.Key))) throw new InvalidDataException("可信设备列表损坏。");
    }

    public static string Fingerprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();
    public bool IsTrusted(string id) { lock (gate) return trusted.ContainsKey(id); }
    public TrustedDevice[] Devices { get { lock (gate) return trusted.Values.OrderBy(d => d.Name).ToArray(); } }
    public void Trust(string id, string name)
    {
        if (!Json.IsHash(id) || id == Id || string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new InvalidDataException("配对设备信息无效。");
        lock (gate)
        {
            var next = new Dictionary<string, TrustedDevice>(trusted) { [id] = new(id, name, DateTimeOffset.UtcNow.ToString("O")) };
            database.WriteMetadata("trusted.json", JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
            trusted = next;
        }
        TrustChanged?.Invoke();
    }
    public void Revoke(string id)
    {
        lock (gate)
        {
            var next = new Dictionary<string, TrustedDevice>(trusted);
            next.Remove(id);
            database.WriteMetadata("trusted.json", JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
            trusted = next;
            invite = null;
        }
        TrustChanged?.Invoke();
    }
    public string CreateInvite()
    {
        lock (gate)
        {
            invite = new(1, Id, Name, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            redeemedBy = null;
            inviteExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
            return "lantodo1:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invite, Json.Options));
        }
    }
    public void CancelInvite() { lock (gate) invite = null; }
    public static PairingInvite ParseInvite(string code)
    {
        if (code.Length > 4096 || !code.Trim().StartsWith("lantodo1:")) throw new InvalidDataException("请粘贴完整配对码。");
        var parsed = Json.Read<PairingInvite>(Convert.FromBase64String(code.Trim()[9..]));
        if (parsed.Protocol != 1 || !Json.IsHash(parsed.DeviceId) || !Json.IsHash(parsed.Secret) || string.IsNullOrWhiteSpace(parsed.Name) || parsed.Name.Length > 100)
            throw new InvalidDataException("配对码无效。");
        return parsed;
    }
    private bool AcceptInvite(string secret, string remoteId, string remoteName)
    {
        lock (gate)
        {
            if (invite is null || DateTimeOffset.UtcNow >= inviteExpiry || !Json.IsHash(secret) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(invite.Secret), Convert.FromHexString(secret))) return false;
            Trust(remoteId, remoteName);
            // A retry by this same certificate after an interrupted acknowledgement is safe.
            // Bind the invitation to one device until expiry; no other certificate may redeem it.
            redeemedBy = remoteId;
            return true;
        }
    }
    private string? redeemedBy;
    public bool Redeem(string secret, string remoteId, string remoteName)
    {
        lock (gate)
        {
            if (redeemedBy is not null && redeemedBy != remoteId) return false;
            return AcceptInvite(secret, remoteId, remoteName);
        }
    }
    public void Dispose() => Certificate.Dispose();
}
