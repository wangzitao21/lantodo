namespace LanTodo.Core;

public sealed partial class AppRuntime
{
    private void MergeLegacyContent()
    {
        var sources=sessions.Where(s=>s!=primary).Select(s=>s.Store).ToArray();
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

    public string DisplayName(string id,string fallback)
    {
        lock(gate)return sessions.SelectMany(s=>s.Identity.Labels).Where(l=>l.Id==id)
            .OrderByDescending(l=>l.Updated).ThenByDescending(l=>l.Author,StringComparer.Ordinal).FirstOrDefault()?.Name??fallback;
    }
    public string Status
    {
        get
        {
            lock(gate)
            {
                if(!networkEnabled)return "已保存到本机 · 网络已停止";
                var peers=sessions.Where(s=>!s.Deleted && s.Identity.Space?.Contains(s.Identity.Id)==true)
                    .SelectMany(s=>s.Node.SpaceStatuses).GroupBy(s=>s.DeviceId).ToArray();
                if(peers.Length==0)return "已保存到本机 · 当前仅本机使用";
                int confirmed=peers.Count(g=>g.Any(s=>s.State=="已同步"));
                return $"已保存到本机 · {confirmed} 台已确认"+(confirmed<peers.Length?$"，{peers.Length-confirmed} 台等待确认":"");
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
