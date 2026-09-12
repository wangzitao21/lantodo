using System.Text.Json;

namespace LanTodo.Core;

public sealed record QuickCapture(string TodoId, string Text);

// Accept text without opening the main database. Stable todo IDs make replay safe
// if the process stops between the SQLite commit and removing an inbox entry.
public sealed class QuickCaptureInbox(string path)
{
    private readonly object gate = new();
    private QuickCapture[]? pending;
    private QuickCapture[] State => pending ??= Read();
    private QuickCapture[] Read()
    {
        var items = File.Exists(path) ? Json.Read<QuickCapture[]>(File.ReadAllBytes(path)) : [];
        if (items.Any(i => i is null || !Guid.TryParseExact(i.TodoId, "N", out _) || string.IsNullOrWhiteSpace(i.Text)))
            throw new InvalidDataException("待保存的想法无效，请保留应用数据。");
        foreach (var item in items) new TodoData(item.Text).Validate();
        return items;
    }
    public QuickCapture[] Pending { get { lock (gate) return State.ToArray(); } }
    public void Enqueue(string text)
    {
        text = text.Trim(); new TodoData(text).Validate();
        lock (gate) Persist([.. State, new(Guid.NewGuid().ToString("N"), text)]);
    }
    public int CommitTo(TodoStore store, string actor, string name)
    {
        lock (gate)
        {
            int added = 0;
            foreach (var item in State.ToArray())
            {
                if (store.History(item.TodoId).Length == 0)
                {
                    var draft = store.Drafts.Get("compose");
                    bool consumesDraft = draft?.Data.Title == item.Text && draft.Data.Attachments is not { Length: > 0 };
                    store.Save(actor, name, new(item.Text), item.TodoId, draftKey: consumesDraft ? "compose" : null);
                    added++;
                }
                Persist(State.Where(i => i.TodoId != item.TodoId).ToArray());
            }
            return added;
        }
    }
    private void Persist(QuickCapture[] next)
    {
        if (next.Length == 0) File.Delete(path);
        else AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(next, Json.Options), true);
        pending = next;
    }
}
