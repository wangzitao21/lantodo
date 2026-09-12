using LanTodo.Core;

namespace LanTodo.Android;

public partial class MainActivity
{
    private readonly HashSet<string> pendingChanges = new();

    private void SaveTodo(TodoView todo, TodoData data, Action<Revision>? committed = null)
    {
        var actor = app.Identity.Id; var name = app.Identity.Name;
        WriteTodo(todo, () => app.Store.Save(actor, name, data, todo.Id, todo.VersionIds), committed);
    }
    private async void WriteTodo(TodoView todo, Func<Revision> write, Action<Revision>? committed)
    {
        if (!pendingChanges.Add(todo.Id)) return;
        feedAdapter?.NotifyDataSetChanged();
        try
        {
            var revision = await app.LocalWrites.Enqueue(write);
            if (!IsDestroyed) committed?.Invoke(revision);
        }
        catch (Exception ex) { if (!IsDestroyed) Error(ex.Message); }
        finally
        {
            pendingChanges.Remove(todo.Id);
            if (!IsDestroyed) StoreChanged();
        }
    }
    private void PurgeTodo(TodoView todo)
    {
        if (todo.Conflict || todo.Data.Purged) { Error("请先处理冲突，或刷新已删除的记录。"); return; }
        SaveTodo(todo, todo.Data with { Deleted = true, Purged = true, Attachments = null, Starred = false, Color = null });
    }
}
