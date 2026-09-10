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
    public event Action? Changed;
    public string Root => Path.GetDirectoryName(Database.FilePath)!;

    public TodoStore(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        LegacyProfile.Upgrade(root);
        Database = new SqliteProfile(root);
        try { foreach (var r in ValidateOrder(Database.ReadRevisions())) AddMemory(r); }
        catch { Database.Dispose(); throw; }
    }

    public TodoView[] List()
    {
        lock (gate) return heads.Select(p => new TodoView(p.Key, p.Value.OrderBy(id => id, StringComparer.Ordinal).Select(id => revisions[id]).ToArray()))
            .Where(v => v.Conflict || !v.Data.Deleted).OrderBy(v => v.Data.Date ?? "9999").ThenBy(v => v.Data.Time ?? "99").ThenBy(v => v.Id).ToArray();
    }
    public Revision[] Export() { lock (gate) return ordered.ToArray(); }
    public Revision[] History(string todoId) { lock (gate) return ordered.Where(r => r.Body.TodoId == todoId).ToArray(); }

    public Revision Save(string actor, string deviceName, TodoData data, string? todoId = null, string[]? expectedHeads = null)
    {
        Revision r;
        lock (gate)
        {
            todoId ??= Guid.NewGuid().ToString("N");
            var actual = heads.TryGetValue(todoId, out var h) ? h.OrderBy(id => id, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            if (!actual.SequenceEqual((expectedHeads ?? Array.Empty<string>()).OrderBy(id => id, StringComparer.Ordinal))) throw new StaleEditException();
            r = Revision.Create(new RevisionBody(1, todoId, actor, deviceName, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToString("O"), actual, data));
            r.Validate();
            Persist(r);
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
            count = sorted.Count;
            Database.Append(sorted);
            foreach (var r in sorted) AddMemory(r);
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
        }
        if (pending.Count > 0) Changed?.Invoke();
        return (pending.Count, skipped);
    }

    private List<Revision> ValidateOrder(Revision[] incoming)
    {
        var pending = new Dictionary<string, Revision>(StringComparer.Ordinal);
        foreach (var r in incoming)
        {
            r.Validate();
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
        if (!heads.TryGetValue(r.Body.TodoId, out var current)) heads[r.Body.TodoId] = current = new();
        foreach (var p in r.Body.Parents) current.Remove(p);
        current.Add(r.Id);
    }

    public void Backup(string path)
    {
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
                    using var manifest = zip.CreateEntry("manifest.json").Open();
                    JsonSerializer.Serialize(manifest, new { schema = 1, count = snapshot.Length, ids = snapshot.Select(r => r.Id).ToArray() }, Json.Options);
                }
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public int Restore(string path)
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
            if (item.Length > 256 * 1024) throw new InvalidDataException("备份记录过大。");
            using var input = item.Open();
            var r = JsonSerializer.Deserialize<Revision>(input, Json.Options) ?? throw new InvalidDataException();
            if (r.Id != id) throw new InvalidDataException("备份记录与清单不符。");
            restored.Add(r);
        }
        return Import(restored);
    }
    public void Dispose() => Database.Dispose();
}
