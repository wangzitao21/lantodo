using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LanTodo.Core;

public sealed record MembershipBody(string Kind, string Actor, string Subject, string Name, string? Certificate, string[] Parents, string Nonce);
public sealed record MembershipEvent(string Id, MembershipBody Body, string Signature);
public sealed record SpaceSnapshot(string Root, MembershipEvent[] Events);

// A root-pinned, signed causal log. Invitations grant membership, never a shared private key.
// Concurrent removal wins; a later invitation may explicitly readmit the same certificate.
public sealed class SyncSpace
{
    private readonly object gate = new();
    private readonly IProfileDatabase database;
    private readonly X509Certificate2 signer;
    private SpaceSnapshot snapshot;
    private Dictionary<string, MembershipEvent> events = new();
    private Dictionary<string, HashSet<string>> ancestors = new();
    private HashSet<string> effectiveGrants = new();
    private HashSet<string> effectiveRemovals = new();
    public event Action? Changed;
    public string Root { get { lock (gate) return snapshot.Root; } }
    public SpaceSnapshot Snapshot { get { lock (gate) return Json.Read<SpaceSnapshot>(JsonSerializer.SerializeToUtf8Bytes(snapshot, Json.Options)); } }
    public bool Knows(string id) { lock (gate) return events.Values.Any(e => e.Body.Subject == id && e.Body.Kind != "remove"); }
    public bool Contains(string id) { lock (gate) return Active(id, events.Keys.Where(k => events[k].Body.Kind != "remove" || effectiveRemovals.Contains(k)).ToHashSet(), effectiveGrants, events, ancestors); }
    public TrustedDevice[] Members { get { lock (gate) return events.Values.Where(e => e.Body.Kind != "remove").Select(e => e.Body.Subject).Distinct().Where(Contains).Select(id => new TrustedDevice(id, events.Values.First(e => e.Body.Subject == id && e.Body.Kind != "remove").Body.Name, "")).ToArray(); } }

    private SyncSpace(IProfileDatabase database, X509Certificate2 signer, SpaceSnapshot snapshot)
    {
        this.database = database; this.signer = signer; this.snapshot = snapshot;
        (events, ancestors, effectiveGrants, effectiveRemovals) = Validate(snapshot);
    }
    public static SyncSpace? Load(IProfileDatabase database, X509Certificate2 signer) => database.ReadMetadata("space-v2.json") is { } bytes ? new(database, signer, Json.Read<SpaceSnapshot>(bytes)) : null;
    public static SyncSpace Create(IProfileDatabase database, X509Certificate2 signer, string name)
    {
        var id = DeviceIdentity.Fingerprint(signer);
        var root = Sign(new("create", id, id, name, Convert.ToBase64String(signer.RawData), [], Guid.NewGuid().ToString("N")), signer);
        return Adopt(database, signer, new(root.Id, [root]), root.Id);
    }
    public static SyncSpace Adopt(IProfileDatabase database, X509Certificate2 signer, SpaceSnapshot snapshot, string expectedRoot)
    {
        if (snapshot.Root != expectedRoot) throw new UnauthorizedAccessException("邀请所属空间不匹配。");
        var space = new SyncSpace(database, signer, snapshot);
        if (!space.Contains(DeviceIdentity.Fingerprint(signer))) throw new UnauthorizedAccessException("尚未获得空间成员资格。");
        space.Persist(); return space;
    }
    public void Merge(SpaceSnapshot incoming)
    {
        bool changed;
        lock (gate) changed = MergeLocked(incoming);
        if (changed) Changed?.Invoke();
    }
    private bool MergeLocked(SpaceSnapshot incoming)
    {
        if (incoming.Root != snapshot.Root || incoming.Events is null || incoming.Events.Length > 512) throw new UnauthorizedAccessException("空间不匹配。");
        var merged = events.Values.Concat(incoming.Events).GroupBy(e => e.Id).Select(g =>
        {
            var first = g.First();
            if (g.Any(e => JsonSerializer.Serialize(e, Json.Options) != JsonSerializer.Serialize(first, Json.Options))) throw new InvalidDataException("成员记录不一致。");
            return first;
        }).ToArray();
        if (merged.Length == events.Count) return false;
        var next = new SpaceSnapshot(snapshot.Root, merged);
        var validated = Validate(next);
        database.WriteMetadata("space-v2.json", JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
        snapshot = next; (events, ancestors, effectiveGrants, effectiveRemovals) = validated;
        return true;
    }
    public void Add(X509Certificate2 certificate, string name)
    {
        lock (gate)
        {
            var id = DeviceIdentity.Fingerprint(certificate);
            if (Contains(id)) return;
            Append("add", id, name, Convert.ToBase64String(certificate.RawData));
        }
        Changed?.Invoke();
    }
    public void Remove(string id)
    {
        lock (gate)
        {
            if (!Contains(id)) return;
            Append("remove", id, "", null);
        }
        Changed?.Invoke();
    }
    private void Append(string kind, string subject, string name, string? certificate)
    {
        string actor = DeviceIdentity.Fingerprint(signer);
        if (!Contains(actor)) throw new UnauthorizedAccessException("本机已离开空间，请使用新授权码加入。");
        var parents = events.Keys.Except(events.Values.SelectMany(e => e.Body.Parents)).Order().ToArray();
        var entry = Sign(new(kind, actor, subject, name, certificate, parents, Guid.NewGuid().ToString("N")), signer);
        MergeLocked(new(snapshot.Root, [entry]));
    }
    private void Persist() => database.WriteMetadata("space-v2.json", JsonSerializer.SerializeToUtf8Bytes(snapshot, Json.Options));
    private static MembershipEvent Sign(MembershipBody body, X509Certificate2 signer)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json.Options);
        using var key = signer.GetRSAPrivateKey()!;
        return new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), body, Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }
    private static bool Active(string id, HashSet<string> scope, HashSet<string> grants, Dictionary<string, MembershipEvent> all, Dictionary<string, HashSet<string>> past)
    {
        var removes = scope.Where(k => all[k].Body.Kind == "remove" && all[k].Body.Subject == id).ToArray();
        return scope.Any(k => grants.Contains(k) && all[k].Body.Subject == id && removes.All(r => past[k].Contains(r)));
    }
    private static (Dictionary<string, MembershipEvent>, Dictionary<string, HashSet<string>>, HashSet<string>, HashSet<string>) Validate(SpaceSnapshot state)
    {
        if (!Json.IsHash(state.Root) || state.Events is null || state.Events.Length is < 1 or > 512) throw new InvalidDataException("成员记录容量无效。");
        var all = new Dictionary<string, MembershipEvent>();
        foreach (var e in state.Events)
        {
            if (e?.Body is not { } b || !Json.IsHash(e.Id) || !Json.IsHash(b.Actor) || !Json.IsHash(b.Subject) || b.Name is null || b.Name.Length > 100 || b.Name.Any(char.IsControl) || b.Nonce is null || b.Nonce.Length > 64 || b.Parents is null || b.Parents.Length > 64 || b.Parents.Any(p => !Json.IsHash(p)) || b.Certificate?.Length > 8192 || e.Signature is null || e.Signature.Length > 2048 || !all.TryAdd(e.Id, e)) throw new InvalidDataException("成员记录无效。");
            if (Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(b, Json.Options))).ToLowerInvariant() != e.Id) throw new InvalidDataException("成员记录校验失败。");
        }
        var past = new Dictionary<string, HashSet<string>>();
        var grants = new HashSet<string>();
        var certificates = new Dictionary<string, byte[]>();
        while (past.Count < all.Count)
        {
            var ready = all.Values.Where(e => !past.ContainsKey(e.Id) && e.Body.Parents.All(past.ContainsKey)).ToArray();
            if (ready.Length == 0) throw new InvalidDataException("成员记录缺少历史或存在循环。");
            foreach (var e in ready)
            {
                var b = e.Body;
                var history = b.Parents.SelectMany(p => past[p].Append(p)).ToHashSet();
                if (b.Kind == "create")
                {
                    if (e.Id != state.Root || b.Actor != b.Subject || b.Parents.Length != 0) throw new UnauthorizedAccessException("空间根记录无效。");
                }
                else if (b.Kind is not ("add" or "remove") || !history.Contains(state.Root) || !Active(b.Actor, history, grants, all, past)) throw new UnauthorizedAccessException("成员操作未经授权。");
                if (b.Kind != "remove")
                {
                    if (string.IsNullOrWhiteSpace(b.Name) || b.Certificate is null) throw new InvalidDataException("成员身份无效。");
                    var raw = Convert.FromBase64String(b.Certificate);
                    using var certificate = X509CertificateLoader.LoadCertificate(raw);
                    if (DeviceIdentity.Fingerprint(certificate) != b.Subject) throw new UnauthorizedAccessException("成员证书不匹配。");
                    certificates[b.Subject] = raw;
                }
                if (!certificates.TryGetValue(b.Actor, out var actor)) throw new UnauthorizedAccessException("缺少操作人证书。");
                using var cert = X509CertificateLoader.LoadCertificate(actor);
                using var rsa = cert.GetRSAPublicKey();
                if (rsa is null || !rsa.VerifyData(JsonSerializer.SerializeToUtf8Bytes(b, Json.Options), Convert.FromBase64String(e.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new UnauthorizedAccessException("成员操作签名无效。");
                past[e.Id] = history;
                if (b.Kind != "remove") grants.Add(e.Id);
            }
        }
        // A removed member cannot create a new branch of members while disconnected.
        var removals = all.Values.Where(e => e.Body.Kind == "remove").Select(e => e.Id).ToHashSet();
        bool changed;
        do
        {
            var invalidGrants = new List<string>(); var invalidRemovals = new List<string>();
            HashSet<string> EffectiveHistory(string id) => past[id].Where(k => all[k].Body.Kind != "remove" || removals.Contains(k)).ToHashSet();
            foreach (var id in grants.ToArray())
            {
                var e = all[id]; if (e.Body.Kind == "create") continue;
                bool concurrentRemoval = removals.Any(r => all[r].Body.Subject == e.Body.Actor && !past[id].Contains(r) && !past[r].Contains(id));
                if (concurrentRemoval || !Active(e.Body.Actor, EffectiveHistory(id), grants, all, past)) invalidGrants.Add(id);
            }
            // Invalid invitation descendants have no authority to remove existing members either.
            foreach (var id in removals) if (!Active(all[id].Body.Actor, EffectiveHistory(id), grants, all, past)) invalidRemovals.Add(id);
            changed = invalidGrants.Count + invalidRemovals.Count > 0;
            grants.ExceptWith(invalidGrants); removals.ExceptWith(invalidRemovals);
        } while (changed);
        if (certificates.Count > 64) throw new InvalidDataException("此空间最多容纳 64 个设备身份。");
        return (all, past, grants, removals);
    }
}
