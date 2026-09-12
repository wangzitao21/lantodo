using System.IO.Compression;
using System.Text.Json;

namespace LanTodo.Core;

public sealed class TodoStore : ITodoStore, IDisposable
{
    private readonly object gate = new();
    public IProfileDatabase Database { get; }
    private readonly Dictionary<string, Revision> revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> heads = new(StringComparer.Ordinal);
    private readonly List<Revision> ordered = new();
    private readonly Dictionary<string, List<Revision>> history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> created = new(StringComparer.Ordinal);
    private readonly Dictionary<Attachment, int> attachmentReferences = new();
    private readonly Dictionary<string, int> hashReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> cleanup = new(StringComparer.Ordinal);
    private TodoView[]? viewCache;
    private Attachment[]? attachmentCache;
    public string? MaintenanceWarning { get; private set; }
    public event Action? Changed;
    public string Root => Path.GetDirectoryName(Database.FilePath)!;
    public AttachmentStore Attachments { get; }
    public LocalDraftStore Drafts { get; }
    public void SaveDraft(string key, LocalDraft? draft)
    {
        lock (gate)
        {
            var previous = Drafts.Get(key);
            Drafts.Save(key, draft);
            foreach (var item in previous?.Data.Attachments ?? [])
                if (!hashReferences.ContainsKey(item.Hash) && !Drafts.Protects(item.Hash)) cleanup[item.Hash] = DateTimeOffset.MinValue;
            CleanDeletedAttachments();
        }
    }
    public bool ReferencesAttachment(Attachment item) { lock (gate) return attachmentReferences.ContainsKey(item); }
    public void DetectMissingAttachments() { lock (gate) { CleanDeletedAttachments(); Attachments.DetectMissing(ActiveAttachments()); } }
    public void NotifyAttachmentsChanged() { lock (gate) CleanDeletedAttachments(); Changed?.Invoke(); }
    public void ReceiveAttachment(Attachment item, long offset, byte[] bytes)
    {
        lock (gate)
        {
            if (!attachmentReferences.ContainsKey(item)) throw new IOException("消息已删除或附件已移除。");
            Attachments.DetectMissing([item]);
            Attachments.Receive(item, offset, bytes);
        }
    }

    public TodoStore(string root)
    {
        Attachments = new(() => Root, () => Database!);
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        LegacyProfile.Upgrade(root);
        Database = new SqliteProfile(root);
        Drafts = new(Database);
        // ReadRevisions has already validated every payload/hash. Still validate the
        // complete parent graph, without hashing every historical record twice.
        try { foreach (var r in ValidateOrder(Database.ReadRevisions(), validateRecords: false)) AddMemory(r); CleanDeletedAttachments(); Attachments.MigrateOriginalNames(ActiveAttachments()); DetectMissingAttachments(); Attachments.Changed += AttachmentStateChanged; }
        catch { Database.Dispose(); throw; }
    }

    private void AttachmentStateChanged() => Changed?.Invoke();
    internal bool SpaceDeleted => Database.ReadMetadata("space-deleted") is {Length:>0} value && value[0]==1;
    internal void ClearLocalContent()
    {
        lock(gate)
        {
            Attachments.ClearLocalFiles();
            ((SqliteProfile)Database).ClearRevisions();
            ClearMemory();
            Database.WriteMetadata("space-cleared",[1]);
        }
    }
    internal void ForgetMergedContent()
    {
        lock(gate)
        {
            // Delete only tracked originals; unrelated files in an old folder are never swept up.
            foreach(var hash in ordered.SelectMany(r=>r.Body.Data.Attachments??[]).Select(a=>a.Hash).Distinct())Attachments.Delete(hash);
            ((SqliteProfile)Database).ClearRevisions();ClearMemory();
        }
    }
    public TodoView[] Trash() { lock(gate) return AllViews().Where(v => !v.Conflict && v.Data.Deleted && !v.Data.Purged).ToArray(); }
    private IEnumerable<TodoView> AllViews() => heads.Select(p => new TodoView(p.Key,p.Value.OrderBy(id=>id,StringComparer.Ordinal).Select(id=>revisions[id]).ToArray()));
    public void Purge(string actor, string name, TodoView view)
    {
        if(view.Conflict || view.Data.Purged) throw new InvalidOperationException("请先处理冲突，或刷新已删除的记录。");
        Save(actor,name,view.Data with { Deleted=true, Purged=true, Attachments=null, Starred=false, Color=null },view.Id,view.VersionIds);
    }
    public (int Deleted, int Skipped) PurgeTrash(string actor, string name, IReadOnlyList<TodoView> confirmed)
    {
        int skipped = 0;
        var pending = new List<Revision>();
        lock (gate)
        {
            if (SpaceDeleted) throw new InvalidOperationException("这个空间已从本机删除。");
            foreach (var view in confirmed.DistinctBy(v => v.Id))
            {
                var actual = heads.TryGetValue(view.Id, out var current) ? current.Order(StringComparer.Ordinal).ToArray() : [];
                if (view.Conflict || !view.Data.Deleted || view.Data.Purged || !actual.SequenceEqual(view.VersionIds.Order(StringComparer.Ordinal)))
                { skipped++; continue; }
                var data = view.Data with { Purged=true, Attachments=null, Starred=false, Color=null };
                var revision = Revision.Create(new(1,view.Id,actor,name,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToString("O"),actual,data));
                revision.Validate(); pending.Add(revision);
            }
            Database.Append(pending);
            foreach (var revision in pending) AddMemory(revision);
            CleanDeletedAttachments();
        }
        if (pending.Count > 0) Changed?.Invoke();
        return (pending.Count, skipped);
    }
    public TodoView[] List()
    {
        lock (gate) return (viewCache ??= AllViews().Where(v => v.Conflict || !v.Data.Deleted)
            .OrderByDescending(v => v.Data.Starred).ThenBy(v => v.Data.Date ?? "9999").ThenBy(v => v.Data.Time ?? "99")
            .ThenByDescending(v => created[v.Id]).ThenBy(v => v.Id, StringComparer.Ordinal).ToArray()).ToArray();
    }
    public Revision[] Export() { lock (gate) return ordered.ToArray(); }
    public int RevisionCount { get { lock (gate) return ordered.Count; } }
    public Attachment[] ActiveAttachments() { lock (gate) return (attachmentCache ??= attachmentReferences.Keys.ToArray()).ToArray(); }
    private static Attachment[] SnapshotAttachments(IEnumerable<Revision> snapshot)
    {
        var all = snapshot.ToArray();
        var parents = all.SelectMany(r => r.Body.Parents).ToHashSet();
        return all.Where(r => !parents.Contains(r.Id) && !r.Body.Data.Purged).SelectMany(r => r.Body.Data.Attachments ?? []).Distinct().ToArray();
    }
    private void CleanDeletedAttachments()
    {
        foreach (var (hash, retry) in cleanup.ToArray())
        {
            if (hashReferences.ContainsKey(hash)) { cleanup.Remove(hash); continue; }
            if (Drafts.Protects(hash)) continue;
            if (retry > DateTimeOffset.UtcNow) continue;
            try { Attachments.Delete(hash); cleanup.Remove(hash); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { cleanup[hash] = DateTimeOffset.UtcNow.AddSeconds(30); }
        }
        int pending = cleanup.Count(p => p.Value != DateTimeOffset.MinValue);
        MaintenanceWarning = pending == 0 ? null : $"内容已保存，{pending} 个原件待清理；文件释放后将自动重试。";
    }
    public Revision[] History(string todoId) { lock (gate) return history.TryGetValue(todoId, out var items) ? items.ToArray() : []; }
    private void ClearMemory()
    {
        revisions.Clear(); heads.Clear(); ordered.Clear(); history.Clear(); created.Clear();
        attachmentReferences.Clear(); hashReferences.Clear(); cleanup.Clear(); viewCache = null; attachmentCache = null;
    }

    Revision ITodoStore.Save(string actor, string deviceName, TodoData data, string? todoId, string[]? expectedHeads) => Save(actor, deviceName, data, todoId, expectedHeads);
    public Revision Save(string actor, string deviceName, TodoData data, string? todoId = null, string[]? expectedHeads = null, string? draftKey = null)
    {
        Revision r;
        lock (gate)
        {
            todoId ??= Guid.NewGuid().ToString("N");
            if(SpaceDeleted)throw new InvalidOperationException("这个空间已从本机删除。");
            var actual = heads.TryGetValue(todoId, out var h) ? h.OrderBy(id => id, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            if (!actual.SequenceEqual((expectedHeads ?? Array.Empty<string>()).OrderBy(id => id, StringComparer.Ordinal))) throw new StaleEditException();
            r = Revision.Create(new RevisionBody(1, todoId, actor, deviceName, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToString("O"), actual, data));
            r.Validate();
            if (draftKey is null) Persist(r);
            else
            {
                var previous = Drafts.Get(draftKey);
                Drafts.CommitRemoval(draftKey, bytes => Database.AppendWithMetadata([r], LocalDraftStore.Key, bytes));
                AddMemory(r);
                foreach (var item in previous?.Data.Attachments ?? [])
                    if (!hashReferences.ContainsKey(item.Hash) && !Drafts.Protects(item.Hash)) cleanup[item.Hash] = DateTimeOffset.MinValue;
            }
            CleanDeletedAttachments();
        }
        Changed?.Invoke();
        return r;
    }

    public int Import(IEnumerable<Revision> incoming)
    {
        int count;
        lock (gate)
        {
            var sorted = ValidateOrder(incoming.ToArray());
            if(SpaceDeleted)throw new InvalidOperationException("这个空间已从本机删除。");
            count = sorted.Count;
            Database.Append(sorted);
            foreach (var r in sorted) AddMemory(r);
            CleanDeletedAttachments();
        }
        if (count > 0) Changed?.Invoke();
        return count;
    }

    // Delete only the completed versions the user actually confirmed. Concurrent edits are skipped.
    public (int Deleted, int Skipped) DeleteCompleted(string actor, string deviceName, IReadOnlyList<TodoView> confirmed)
    {
        int skipped = 0;
        var pending = new List<Revision>();
        lock (gate)
        {
            foreach (var view in confirmed.DistinctBy(v => v.Id))
            {
                var actual = heads.TryGetValue(view.Id, out var current) ? current.OrderBy(id => id, StringComparer.Ordinal).ToArray() : [];
                if (view.Conflict || !view.Data.Completed || view.Data.Deleted || !actual.SequenceEqual(view.VersionIds.OrderBy(id => id, StringComparer.Ordinal)))
                { skipped++; continue; }
                var revision = Revision.Create(new RevisionBody(1,view.Id,actor,deviceName,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.ToString("O"),actual,view.Data with { Deleted = true }));
                revision.Validate(); pending.Add(revision);
            }
            Database.Append(pending);
            foreach (var revision in pending) AddMemory(revision);
            CleanDeletedAttachments();
        }
        if (pending.Count > 0) Changed?.Invoke();
        return (pending.Count, skipped);
    }

    private List<Revision> ValidateOrder(Revision[] incoming, bool validateRecords = true)
    {
        var pending = new Dictionary<string, Revision>(StringComparer.Ordinal);
        foreach (var r in incoming)
        {
            if (validateRecords) r.Validate();
            if (!revisions.ContainsKey(r.Id)) pending.TryAdd(r.Id, r);
        }
        // Kahn ordering: a restored or received child can never hide a missing parent.
        var indegree = new Dictionary<string, int>();
        var children = new Dictionary<string, List<string>>();
        foreach (var r in pending.Values)
        {
            int degree = 0;
            foreach (var p in r.Body.Parents)
            {
                Revision? parent = revisions.GetValueOrDefault(p) ?? pending.GetValueOrDefault(p);
                if (parent is null || parent.Body.TodoId != r.Body.TodoId) throw new InvalidDataException("缺失父版本或跨待办引用，已拒绝导入。");
                if (pending.ContainsKey(p))
                {
                    degree++;
                    if (!children.TryGetValue(p, out var list)) children[p] = list = new();
                    list.Add(r.Id);
                }
            }
            indegree[r.Id] = degree;
        }
        var queue = new Queue<string>(indegree.Where(p => p.Value == 0).Select(p => p.Key));
        var result = new List<Revision>();
        while (queue.TryDequeue(out var id))
        {
            result.Add(pending[id]);
            if (children.TryGetValue(id, out var cs)) foreach (var child in cs) if (--indegree[child] == 0) queue.Enqueue(child);
        }
        if (result.Count != pending.Count) throw new InvalidDataException("版本图含循环。");
        return result;
    }

    private void Persist(Revision r)
    {
        Database.Append([r]);
        AddMemory(r);
    }
    private void AddMemory(Revision r)
    {
        revisions.Add(r.Id, r);
        ordered.Add(r);
        if (!history.TryGetValue(r.Body.TodoId, out var items)) history[r.Body.TodoId] = items = new();
        items.Add(r);
        if (r.Body.Parents.Length == 0)
        {
            var time = DateTimeOffset.Parse(r.Body.CreatedUtc, System.Globalization.CultureInfo.InvariantCulture);
            if (!created.TryGetValue(r.Body.TodoId, out var previous) || time < previous) created[r.Body.TodoId] = time;
        }
        if (!heads.TryGetValue(r.Body.TodoId, out var current)) heads[r.Body.TodoId] = current = new();
        foreach (var p in r.Body.Parents) if (current.Remove(p)) UpdateReferences(revisions[p], -1);
        current.Add(r.Id);
        UpdateReferences(r, 1); viewCache = null;
    }
    private void UpdateReferences(Revision revision, int delta)
    {
        if (revision.Body.Data.Purged) return;
        foreach (var item in revision.Body.Data.Attachments ?? [])
        {
            var count = attachmentReferences.GetValueOrDefault(item) + delta;
            if (count == 0) attachmentReferences.Remove(item); else attachmentReferences[item] = count;
            var hashes = hashReferences.GetValueOrDefault(item.Hash) + delta;
            if (hashes == 0) { hashReferences.Remove(item.Hash); cleanup[item.Hash] = DateTimeOffset.MinValue; }
            else { hashReferences[item.Hash] = hashes; cleanup.Remove(item.Hash); }
            attachmentCache = null;
        }
    }

    public void Backup(string path)
    {
        lock (gate) BackupSnapshot(path);
    }
    private void BackupSnapshot(string path)
    {
        DetectMissingAttachments();
        var snapshot = Export();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    foreach (var r in snapshot)
                    {
                        using var entry = zip.CreateEntry("revisions/" + r.Id + ".json").Open();
                        JsonSerializer.Serialize(entry, r, Json.Options);
                    }
                    foreach (var attachment in SnapshotAttachments(snapshot).Where(a => !Attachments.IsInvalid(a)).DistinctBy(a => a.Hash))
                    {
                        if (Attachments.IsInvalid(attachment)) continue;
                        if (!Attachments.Has(attachment)) throw new IOException("附件尚未同步完成，暂不能生成完整备份。");
                        Attachments.Verify(attachment);
                        zip.CreateEntryFromFile(Attachments.PathFor(attachment.Hash), "attachments/" + attachment.Hash, CompressionLevel.NoCompression);
                    }
                    using var manifest = zip.CreateEntry("manifest.json").Open();
                    JsonSerializer.Serialize(manifest, new { schema = 1, invalidAttachments = Attachments.InvalidKeys, count = snapshot.Length, ids = snapshot.Select(r => r.Id).ToArray() }, Json.Options);
                }
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public int Restore(string path)
    {
        int count;
        lock (gate) count = RestoreSnapshot(path);
        Changed?.Invoke();
        return count;
    }
    private int RestoreSnapshot(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("备份缺失清单。");
        if (entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("备份清单过大。");
        using var stream = entry.Open();
        using var manifest = JsonDocument.Parse(stream);
        if (manifest.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("不支持的备份版本。");
        var ids = manifest.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (ids.Length != manifest.RootElement.GetProperty("count").GetInt32() || ids.Distinct().Count() != ids.Length) throw new InvalidDataException("备份清单不一致。");
        var restored = new List<Revision>();
        foreach (var id in ids)
        {
            if (!Json.IsHash(id)) throw new InvalidDataException("备份版本号无效。");
            var item = zip.GetEntry("revisions/" + id + ".json") ?? throw new InvalidDataException("备份缺失版本：" + id);
            if (item.Length > Json.MaxRevisionBytes) throw new InvalidDataException("备份记录过大。");
            using var input = item.Open();
            var r = JsonSerializer.Deserialize<Revision>(input, Json.Options) ?? throw new InvalidDataException();
            if (r.Id != id) throw new InvalidDataException("备份记录与清单不符。");
            restored.Add(r);
        }
        var sorted = ValidateOrder(restored.ToArray());
        if (SpaceDeleted) throw new InvalidOperationException("这个空间已从本机删除。");
        var invalid = manifest.RootElement.TryGetProperty("invalidAttachments", out var invalidKeys)
            ? invalidKeys.EnumerateArray().Select(e => e.GetString()!).ToHashSet() : new HashSet<string>();
        foreach (var key in invalid) AttachmentStore.ValidateKey(key);
        // Validate every blob promised by this backup, independently of the local pending downloads.
        // Nothing in the live history or invalidation set changes during this phase.
        var staging = Path.Combine(Root, ".transfers", "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var originals = SnapshotAttachments(restored).Where(a => !invalid.Contains(a.Key)).DistinctBy(a => a.Hash).ToArray();
            foreach (var attachment in originals)
            {
                attachment.Validate();
                var blob = zip.GetEntry("attachments/" + attachment.Hash) ?? throw new InvalidDataException("备份缺失附件：" + attachment.Name);
                if (blob.Length != attachment.Size) throw new InvalidDataException("备份附件大小不符。");
                var staged = Path.Combine(staging, attachment.Hash);
                using (var input = blob.Open())
                using (var output = File.Create(staged)) input.CopyTo(output);
                using var verified = File.OpenRead(staged);
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(verified)).ToLowerInvariant() != attachment.Hash)
                    throw new InvalidDataException("备份附件校验失败：" + attachment.Name);
            }
            // Install verified originals before publishing references. A failed SQL commit can leave
            // unreferenced originals, but cannot erase or invalidate previously usable content.
            foreach (var attachment in originals)
            {
                if (Attachments.IsInvalid(attachment)) continue;
                if (Attachments.Has(attachment))
                {
                    try { Attachments.Verify(attachment); continue; }
                    catch (InvalidDataException) { /* A verified backup can repair a damaged local copy. */ }
                }
                using var input = File.OpenRead(Path.Combine(staging, attachment.Hash));
                Attachments.Add(input, attachment.Name, attachment.Kind);
            }
            // Record the restored instances, including multiple references to one shared blob.
            // A deletion immediately after restore must not look like a never-received attachment.
            Attachments.RememberAvailable(SnapshotAttachments(restored).Where(a => !invalid.Contains(a.Key)));
            Attachments.CommitInvalidations(invalid, bytes => Database.AppendWithMetadata(sorted, AttachmentStore.InvalidMetadata, bytes));
            foreach (var revision in sorted) AddMemory(revision);
            CleanDeletedAttachments();
            return sorted.Count;
        }
        finally { try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public void Dispose() { Attachments.Changed -= AttachmentStateChanged; Database.Dispose(); }
}
