using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LanTodo.Core;

public sealed record SpaceInvite(int Protocol, string Root, string DeviceId, string Name, string Secret, string[] Addresses, long Expires);

public sealed partial class DeviceIdentity
{
    public SyncSpace? Space { get; private set; }
    public string SpaceName => Space is { } space ? DisplayName(space.Root, "默认网络") : "原有清单";
    public void RenameSpace(string name)
    {
        if(Space is not { } space || !space.Contains(Id))throw new InvalidOperationException("请先加入空间。");
        MergeLabels([new(space.Root,name.Trim(),Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Labels.Select(l=>l.Updated).DefaultIfEmpty().Max()+1),Id)]);
    }

    private SpaceInvite? spaceInvite;
    public bool CanJoinSpace => Space is null || !Space.Contains(Id) || Space.Members.Length == 1;
    public bool NeedsSpaceUpgrade => Space is null && trusted.Count > 0;
    public const string JoinNotice = "本机清单、附件和历史将与整个空间合并共享。空间内设备均可添加或移除成员。旧版逐台配对将被替换，本机数据会保留。";
    private void AttachSpace(SyncSpace? value)
    {
        if (Space is not null) Space.Changed -= OnSpaceChanged;
        Space = value;
        if (Space is not null) Space.Changed += OnSpaceChanged;
    }
    private void OnSpaceChanged()
    {
        if (Space is { } space && !space.Contains(Id)) CancelInvite();
        TrustChanged?.Invoke();
    }
    public void EnsureSpace(bool upgradeConfirmed = false)
    {
        if (Space is not null) return;
        if (NeedsSpaceUpgrade && !upgradeConfirmed) throw new InvalidOperationException("请先确认升级到统一空间，再邀请其他设备重新加入。");
        AttachSpace(SyncSpace.Create(database, Certificate, Name));
        CancelInvite(); OnSpaceChanged();
    }
    public void AdoptSpace(SpaceSnapshot snapshot, string root)
    {
        if (Space?.Root != root && !CanJoinSpace) throw new InvalidOperationException("请先退出当前空间，再加入另一个空间。");
        AttachSpace(SyncSpace.Adopt(database, Certificate, snapshot, root));
        CancelInvite(); OnSpaceChanged();
    }
    public string CreateSpaceInvite(string[] addresses)
    {
        EnsureSpace();
        if (!Space!.Contains(Id)) throw new InvalidOperationException("本机已退出空间，请先加入或创建新空间。");
        if (addresses.Length > 8) throw new InvalidDataException("地址过多。");
        foreach (var address in addresses) _ = ReplicaAddress.Parse(address);
        lock (gate)
        {
            RetireInvite("已作废"); redeemedBy = null;
            inviteExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
            spaceInvite = new(2, Space.Root, Id, Name, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), addresses, inviteExpiry.ToUnixTimeSeconds());
            inviteRecordId = Guid.NewGuid().ToString("N");
            inviteRecords.Add(new(inviteRecordId, DateTimeOffset.UtcNow, inviteExpiry, "待绑定"));
            if (inviteRecords.Count > 200) inviteRecords.RemoveAt(0);
            PersistInvites();
            return "lantodo2:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(spaceInvite, Json.Options));
        }
    }
    public static SpaceInvite ParseSpaceInvite(string code)
    {
        if (code.Length > 8192 || !code.Trim().StartsWith("lantodo2:")) throw new InvalidDataException("请粘贴完整空间授权码。");
        var parsed = Json.Read<SpaceInvite>(Convert.FromBase64String(code.Trim()[9..]));
        if (parsed.Protocol != 2 || !Json.IsHash(parsed.Root) || !Json.IsHash(parsed.DeviceId) || !Json.IsHash(parsed.Secret) || string.IsNullOrWhiteSpace(parsed.Name) || parsed.Name.Length > 100 || parsed.Addresses is null || parsed.Addresses.Length > 8 || parsed.Addresses.Any(a => a is null || a.Length > 300)) throw new InvalidDataException("空间授权码无效。");
        foreach (var address in parsed.Addresses) _ = ReplicaAddress.Parse(address);
        if (parsed.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new InvalidDataException("授权码已过期，请在对方设备重新生成。");
        return parsed;
    }
    public SpaceSnapshot RedeemSpace(string secret, X509Certificate2 certificate, string name)
    {
        lock (gate)
        {
            var remoteId = Fingerprint(certificate);
            if (Space is null || spaceInvite is null || spaceInvite.Root != Space.Root || !Space.Contains(Id) || DateTimeOffset.UtcNow >= inviteExpiry || !Json.IsHash(secret) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(spaceInvite.Secret), Convert.FromHexString(secret)) || (redeemedBy is not null && redeemedBy != remoteId)) throw new UnauthorizedAccessException("授权码无效、已使用或已过期。");
            Space.Add(certificate, name);
            redeemedBy = remoteId;
            inviteRecords = inviteRecords.Select(r => r.Id == inviteRecordId ? r with { State = "已使用", DeviceId = remoteId } : r).ToList();
            PersistInvites(); return Space.Snapshot;
        }
    }
    public void LeaveSpace() { Space?.Remove(Id); CancelInvite(); }
    public void CreateNewSpace()
    {
        if (Space is { } s && s.Contains(Id)) throw new InvalidOperationException("请先退出当前空间。");
        AttachSpace(SyncSpace.Create(database, Certificate, Name)); CancelInvite(); OnSpaceChanged();
    }
}
