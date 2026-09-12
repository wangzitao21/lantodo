using Android.Widget;
using Android.Views;
using LanTodo.Core;

namespace LanTodo.Android;

public partial class MainActivity
{
    private View? undoBar;
    private void ShowUndo(Action restore)
    {
        if (undoBar?.Parent is ViewGroup old) old.RemoveView(undoBar);
        var bar = Button("已移到回收站 · 撤销", () =>
        {
            restore();
            if (undoBar?.Parent is ViewGroup parent) parent.RemoveView(undoBar);
        });
        undoBar = bar; root.AddView(bar);
        bar.PostDelayed(() => { if (bar.Parent is ViewGroup parent) parent.RemoveView(bar); }, 12000);
    }
    private async Task PurgeTrashAsync(TodoView[] confirmed)
    {
        try
        {
            var result = await Task.Run(() => app.Store.PurgeTrash(app.Identity.Id, app.Identity.Name, confirmed));
            if (IsDestroyed) return;
            if (showingHome) RenderTodos();
            Toast.MakeText(this, result.Skipped > 0 ? $"已清除 {result.Deleted} 条，{result.Skipped} 条已有修改，已保留" : $"已清除 {result.Deleted} 条", ToastLength.Short)?.Show();
        }
        catch (Exception ex) { Error(ex.Message); }
    }
}
