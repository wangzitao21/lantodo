using System.Text.Json;

namespace LanTodo.Core;

public sealed record LocalDraft(TodoData Data, string[] Heads);

// Local drafts never participate in revision exchange or data backups.
public sealed class LocalDraftStore(IProfileDatabase database)
{
    internal const string Key = "local-drafts-v1";
    private readonly object gate = new();
    private Dictionary<string, LocalDraft>? drafts;
    private Dictionary<string, LocalDraft> State => drafts ??= database.ReadMetadata(Key) is { } bytes
        ? Json.Read<Dictionary<string, LocalDraft>>(bytes) : new();
    public LocalDraft? Get(string key) { lock (gate) return State.GetValueOrDefault(key); }
    public KeyValuePair<string, LocalDraft>[] WithPrefix(string prefix)
    { lock (gate) return State.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray(); }
    public void Preserve(string prefix, LocalDraft draft)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft, Json.Options);
        lock (gate)
        {
            if (WithPrefix(prefix).Any(p => JsonSerializer.SerializeToUtf8Bytes(p.Value, Json.Options).SequenceEqual(bytes))) return;
            // A restored slot may now hold the user's newer input. Its key must
            // not be derived from the old content and reused on the next launch.
            Save(prefix + ":" + Guid.NewGuid().ToString("N"), draft);
        }
    }
    public void Swap(string first, string second)
    {
        lock (gate)
        {
            var next = new Dictionary<string, LocalDraft>(State);
            var a = next.GetValueOrDefault(first); var b = next.GetValueOrDefault(second);
            if (b is null) next.Remove(first); else next[first] = b;
            if (a is null) next.Remove(second); else next[second] = a;
            database.WriteMetadata(Key, JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
            drafts = next;
        }
    }
    public bool Protects(string hash) { lock (gate) return State.Values.Any(d => d.Data.Attachments?.Any(a => a.Hash == hash) == true); }
    public void Save(string key, LocalDraft? draft)
    {
        lock (gate)
        {
            var next = new Dictionary<string, LocalDraft>(State);
            if (draft is null) next.Remove(key); else next[key] = draft;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json.Options);
            if (bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(State, Json.Options))) return;
            database.WriteMetadata(Key, bytes); drafts = next;
        }
    }
    internal void CommitRemoval(string key, Action<byte[]> commit)
    {
        lock (gate)
        {
            var next = new Dictionary<string, LocalDraft>(State); next.Remove(key);
            commit(JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
            drafts = next;
        }
    }
}

public static class MessageQuery
{
    public static bool Matches(TodoView view, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return terms.All(term => view.Heads.Any(h =>
            h.Body.Data.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            h.Body.Data.Notes.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (h.Body.Data.Attachments?.Any(a => a.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ?? false)));
    }
}
