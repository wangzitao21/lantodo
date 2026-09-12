namespace LanTodo.Core;

public sealed partial class AppRuntime
{
    private void MergeLegacyContent()
    {
        var sources=sessions.Where(s=>s!=primary).Select(s=>s.Store).ToArray();
        if(sources.Length==0)return;
        // The revision graph provides idempotent merging and preserves genuine edit conflicts.
        // Finish every import/copy before removing any migrated source data, so interruption is retryable.
        foreach(var source in sources)Store.Import(source.Export());
        Store.Attachments.Invalidate(sources.SelectMany(s=>s.Attachments.InvalidKeys));
        var needed=Store.ActiveAttachments();
        foreach(var source in sources)
        {
            foreach(var item in source.ActiveAttachments().Where(a=>needed.Contains(a)))
            {
                if(Store.Attachments.IsInvalid(item) || !source.Attachments.Has(item))continue;
                if(Store.Attachments.Has(item))
                {
                    using var existing=File.OpenRead(Store.Attachments.PathFor(item.Hash));
                    if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(existing)).ToLowerInvariant()!=item.Hash)
                        throw new IOException("合并目标中的附件校验失败，旧原件已保留，请修复后重试。");
                    continue;
                }
                using var input=File.OpenRead(source.Attachments.PathFor(item.Hash));
                if(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant()!=item.Hash)
                    throw new IOException("旧空间附件校验失败，原件已保留，请修复原件后重新启动。");
                input.Position=0;
                if(Store.Attachments.Add(input,item.Name,item.Kind).Hash!=item.Hash)throw new IOException("旧空间附件合并校验失败，原件已保留。");
            }
        }
        Store.DetectMissingAttachments();
        foreach(var source in sources)
        {
            try { source.ForgetMergedContent(); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string,string>? displayNames;
    public string DisplayName(string id,string fallback)
    {
        lock(gate)
        {
            displayNames ??= sessions.SelectMany(s=>s.Identity.Labels).GroupBy(l=>l.Id)
                .ToDictionary(g=>g.Key,g=>g.OrderByDescending(l=>l.Updated).ThenByDescending(l=>l.Author,StringComparer.Ordinal).First().Name);
            return displayNames.GetValueOrDefault(id,fallback);
        }
    }
    public bool IsOnline { get { lock(gate) return networkEnabled && sessions.Any(s => !s.Deleted && s.Node.IsOnline); } }
    public bool HasPeers { get { lock (gate) return sessions.Any(s => !s.Deleted && s.Identity.Devices.Length > 0); } }
    public string SyncDetails
    {
        get
        {
            lock (gate) return string.Join("\n", sessions.Where(s => !s.Deleted).SelectMany(s => s.Node.SpaceStatuses.Select(p =>
                s.Identity.DisplayName(p.DeviceId, "设备") + " · " + (s.Node.IsDeviceOnline(p.DeviceId) ? p.State : "待上线") +
                (p.LastSuccess is { } time ? " · 最近确认 " + time.ToString("MM-dd HH:mm") : "") +
                (p.Error is null ? "" : "\n" + p.Error))).Append(Store.MaintenanceWarning).Where(s => !string.IsNullOrEmpty(s)));
        }
    }
    public string Status
    {
        get
        {
            lock(gate)
            {
                var nodes=sessions.Where(s=>!s.Deleted).Select(s=>s.Node).ToArray();
                var peers = nodes.SelectMany(n => n.SpaceStatuses.Select(s => new { s.DeviceId, Online = n.IsDeviceOnline(s.DeviceId), Synced = s.State == "已同步" }))
                    .GroupBy(s => s.DeviceId).ToArray();
                if (peers.Length == 0) return "仅本机使用 · 已保存";
                int online = peers.Count(g => g.Any(p => p.Online));
                int confirmed = peers.Count(g => g.Any(p => p.Online && p.Synced));
                if (!networkEnabled) return "已保存到本机 · 等待设备上线";
                if (online == 0) return nodes.SelectMany(n => n.SpaceStatuses).Any(s => s.State is "正在连接" or "正在同步")
                    ? "已保存到本机 · 正在连接设备" : "已保存到本机 · 等待设备上线";
                if (confirmed < online) return "已保存到本机 · 正在同步";
                return online < peers.Length ? $"已同步 {confirmed} 台 · {peers.Length - online} 台待上线" : $"已同步至 {confirmed} 台设备";
            }
        }
    }
    public void RenameSelf(string name)
    {
        primary.Identity.Rename(primary.Identity.Id,name);
        var label=primary.Identity.Labels.Single(l=>l.Id==primary.Identity.Id);
        lock(gate)foreach(var session in sessions.Where(s=>s!=primary))session.Identity.MergeLabels([label]);
    }
}
