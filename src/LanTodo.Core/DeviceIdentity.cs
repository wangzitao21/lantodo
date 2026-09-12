using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LanTodo.Core;

public sealed record TrustedDevice(string Id, string Name, string PairedUtc);
public sealed record DeviceLabel(string Id, string Name, long Updated, string Author);
public sealed record PairingInvite(int Protocol, string DeviceId, string Name, string Secret);
public sealed record InviteRecord(string Id, DateTimeOffset Created, DateTimeOffset Expires, string State, string? DeviceId = null);

public sealed partial class DeviceIdentity : IDisposable
{
    private readonly object gate = new();
    private readonly IProfileDatabase database;
    private Dictionary<string, TrustedDevice> trusted;
    private PairingInvite? invite;
    private DateTimeOffset inviteExpiry;
    public X509Certificate2 Certificate { get; }
    public string Id { get; }
    public string Name { get; private set; }
    private Dictionary<string, DeviceLabel> labels = new();
    private HashSet<string> deletedDevices = new();
    public DeviceLabel[] Labels { get { lock (gate) return labels.Values.ToArray(); } }
    public bool WasDeleted(string id) { if (Space is { } s) return s.Knows(id) && !s.Contains(id); lock (gate) return deletedDevices.Contains(id); }
    public string DisplayName(string id, string fallback) { lock (gate) return labels.GetValueOrDefault(id)?.Name ?? fallback; }
    public void Rename(string id, string name)
    {
        name = name.Trim();
        if (id != Id && !Devices.Concat(RevokedDevices).Any(d => d.Id == id)) throw new InvalidDataException("设备不存在。");
        MergeLabels([new(id, name, Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Labels.Select(l => l.Updated).DefaultIfEmpty().Max() + 1), Id)]);
    }
    public void MergeLabels(DeviceLabel[]? incoming)
    {
        if (incoming is null || incoming.Length == 0) return;
        if (incoming.Length > 256 || incoming.Any(l => !Json.IsHash(l.Id) || !Json.IsHash(l.Author) || string.IsNullOrWhiteSpace(l.Name) || l.Name.Length > 100 || l.Name.Any(char.IsControl) || l.Updated < 0 || l.Updated > DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds()))
            throw new InvalidDataException("设备昵称无效（1–100 字）。");
        bool changed = false;
        lock (gate)
        {
            var next = new Dictionary<string, DeviceLabel>(labels);
            foreach (var label in incoming)
                if (!next.TryGetValue(label.Id, out var old) || label.Updated > old.Updated || (label.Updated == old.Updated && string.CompareOrdinal(label.Author, old.Author) > 0)) { next[label.Id] = label; changed = true; }
            if (!changed) return;
            if (next.Count > 256) throw new InvalidDataException("设备昵称记录过多。");
            database.WriteMetadata("device-labels.json", JsonSerializer.SerializeToUtf8Bytes(next, Json.Options)); labels = next;
            if (labels.TryGetValue(Id, out var own)) { database.WriteMetadata("name.json", JsonSerializer.SerializeToUtf8Bytes(own.Name, Json.Options)); Name = own.Name; }
        }
        TrustChanged?.Invoke();
    }
    public event Action? TrustChanged;
    private List<InviteRecord> inviteRecords = new();
    private string? inviteRecordId;
    private Dictionary<string, TrustedDevice> revokedDevices = new();
    public TrustedDevice[] RevokedDevices { get { if (Space is not null) return []; lock (gate) return revokedDevices.Values.Select(d => d with { Name = DisplayName(d.Id, d.Name) }).ToArray(); } }
    public InviteRecord[] Invites { get { lock (gate) return inviteRecords.Select(r => r.State == "待绑定" && r.Expires <= DateTimeOffset.UtcNow ? r with { State = "已过期" } : r).ToArray(); } }
    private void PersistInvites() => database.WriteMetadata("invite-history.json", JsonSerializer.SerializeToUtf8Bytes(inviteRecords, Json.Options));
    private void RetireInvite(string state)
    {
        if (inviteRecordId is not null)
        {
            inviteRecords = Invites.Select(r => r.Id == inviteRecordId && r.State == "待绑定" ? r with { State = state } : r).ToList();
            PersistInvites();
        }
        invite = null; inviteRecordId = null;
        spaceInvite = null;
    }
    public void DeleteInviteRecord(string id)
    {
        lock (gate)
        {
            var record = Invites.FirstOrDefault(r => r.Id == id) ?? throw new InvalidDataException("配对码记录不存在。");
            if (record.State == "待绑定") throw new InvalidOperationException("请先作废有效配对码。");
            if (inviteRecordId == id) { invite = null; inviteRecordId = null; }
            inviteRecords.RemoveAll(r => r.Id == id); PersistInvites();
        }
    }

    public DeviceIdentity(IProfileDatabase database, string defaultName) : this(database, defaultName, null) { }
    internal DeviceIdentity(IProfileDatabase database, string defaultName, DeviceIdentity? sharedIdentity)
    {
        this.database = database;
        var key = database.ReadMetadata("identity.pfx");
        if (key is null)
        {
            // Refuse identity replacement when a trust store exists: missing key is a recovery event.
            if (database.ReadMetadata("trusted.json") is not null || database.ReadMetadata("space-v2.json") is not null) throw new InvalidDataException("设备密钥丢失，请保留数据目录并恢复备份。");
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
        // Additional local spaces normally contain the same identity. Duplicate the
        // certificate handle instead of repeating PKCS#12 key derivation/import.
        // Compare persisted bytes first: a genuinely different identity must remain independent.
        Certificate = sharedIdentity is not null && sharedIdentity.database.ReadMetadata("identity.pfx") is { } sharedKey && key.AsSpan().SequenceEqual(sharedKey)
            ? new X509Certificate2(sharedIdentity.Certificate)
            : X509CertificateLoader.LoadPkcs12(key, null, flags);
        if (!Certificate.HasPrivateKey) throw new InvalidDataException("设备密钥无效。");
        Id = Fingerprint(Certificate);
        var name = database.ReadMetadata("name.json");
        if (name is null) { name = JsonSerializer.SerializeToUtf8Bytes(defaultName[..Math.Min(defaultName.Length, 100)], Json.Options); database.WriteMetadata("name.json", name); }
        Name = Json.Read<string>(name);
        if (database.ReadMetadata("device-labels.json") is { } labelBytes) labels = Json.Read<Dictionary<string, DeviceLabel>>(labelBytes);
        if (labels.TryGetValue(Id, out var ownLabel)) Name = ownLabel.Name;
        if (database.ReadMetadata("deleted-devices.json") is { } deletedBytes) deletedDevices = Json.Read<HashSet<string>>(deletedBytes);
        var trust = database.ReadMetadata("trusted.json");
        trusted = trust is null ? new() : Json.Read<Dictionary<string, TrustedDevice>>(trust);
        if (database.ReadMetadata("revoked-devices.json") is { } revoked) revokedDevices = Json.Read<Dictionary<string, TrustedDevice>>(revoked);
        if (database.ReadMetadata("invite-history.json") is { } history)
        {
            inviteRecords = Json.Read<List<InviteRecord>>(history).Select(r => r.State == "待绑定" ? r with { State = "已作废" } : r).ToList();
            PersistInvites();
        }
        if (trusted.Any(p => p.Key != p.Value.Id || !Json.IsHash(p.Key))) throw new InvalidDataException("可信设备列表损坏。");
        AttachSpace(SyncSpace.Load(database, Certificate));
    }

    public static string Fingerprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();
    public bool IsTrusted(string id) { if (Space is { } s) return s.Contains(Id) && s.Contains(id) && id != Id; lock (gate) return trusted.ContainsKey(id); }
    public TrustedDevice[] Devices { get { if (Space is { } s) return (s.Contains(Id) ? s.Members : []).Where(d => d.Id != Id).Select(d => d with { Name = DisplayName(d.Id, d.Name) }).OrderBy(d => d.Name).ToArray(); lock (gate) return trusted.Values.Select(d => d with { Name = DisplayName(d.Id, d.Name) }).OrderBy(d => d.Name).ToArray(); } }
    public void Trust(string id, string name)
    {
        if (Space is not null) throw new InvalidOperationException("请使用空间授权码加入，旧版逐台配对已停用。");
        if (!Json.IsHash(id) || id == Id || string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new InvalidDataException("配对设备信息无效。");
        lock (gate)
        {
            var next = new Dictionary<string, TrustedDevice>(trusted) { [id] = new(id, name, DateTimeOffset.UtcNow.ToString("O")) };
            database.WriteMetadata("trusted.json", JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
            trusted = next;
            deletedDevices.Remove(id);
            database.WriteMetadata("deleted-devices.json", JsonSerializer.SerializeToUtf8Bytes(deletedDevices, Json.Options));
            revokedDevices.Remove(id);
            database.WriteMetadata("revoked-devices.json", JsonSerializer.SerializeToUtf8Bytes(revokedDevices, Json.Options));
        }
        TrustChanged?.Invoke();
    }
    public void Revoke(string id)
    {
        if (Space is { } s) { s.Remove(id); CancelInvite(); return; }
        lock (gate)
        {
            if (trusted.TryGetValue(id, out var device)) revokedDevices[id] = device;
            database.WriteMetadata("revoked-devices.json", JsonSerializer.SerializeToUtf8Bytes(revokedDevices, Json.Options));
            var next = new Dictionary<string, TrustedDevice>(trusted);
            next.Remove(id);
            database.WriteMetadata("trusted.json", JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
            trusted = next;
            RetireInvite("已作废");
        }
        TrustChanged?.Invoke();
    }
    public string CreateInvite()
    {
        if (Space is not null) return CreateSpaceInvite([]);
        lock (gate)
        {
            RetireInvite("已作废");
            invite = new(1, Id, Name, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
            redeemedBy = null;
            inviteExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
            inviteRecordId = Guid.NewGuid().ToString("N");
            inviteRecords.Add(new(inviteRecordId, DateTimeOffset.UtcNow, inviteExpiry, "待绑定"));
            if (inviteRecords.Count > 200) inviteRecords.RemoveAt(0);
            PersistInvites();
            return "lantodo1:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invite, Json.Options));
        }
    }
    public void DeleteRevokedDevice(string id)
    {
        if (Space is { } s) { s.Remove(id); return; }
        lock (gate)
        {
            if (trusted.ContainsKey(id)) throw new InvalidOperationException("请先取消设备绑定。");
            if (!Json.IsHash(id)) throw new InvalidDataException("设备标识无效。");
            deletedDevices.Add(id);
            database.WriteMetadata("deleted-devices.json", JsonSerializer.SerializeToUtf8Bytes(deletedDevices, Json.Options));
            inviteRecords.RemoveAll(r => r.DeviceId == id); PersistInvites();
            var next = new Dictionary<string, TrustedDevice>(revokedDevices);
            next.Remove(id);
            database.WriteMetadata("revoked-devices.json", JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
            revokedDevices = next;
        }
        TrustChanged?.Invoke();
    }
    public void CancelInvite() { lock (gate) RetireInvite("已作废"); }
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
            inviteRecords = inviteRecords.Select(r => r.Id == inviteRecordId ? r with { State = "已使用", DeviceId = remoteId } : r).ToList();
            PersistInvites();
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
