using System.Windows;
using LanTodo.Core;

namespace LanTodo.Windows;

public partial class MainWindow
{
    private int undoVersion;
    private Action? undoDelete;
    private void MoveToTrash(TodoView todo)
    {
        var deleted = app.Store.Save(app.Identity.Id, app.Identity.Name, todo.Data with { Deleted=true }, todo.Id, todo.VersionIds);
        undoDelete = () => app.Store.Save(app.Identity.Id, app.Identity.Name, todo.Data, todo.Id, [deleted.Id]);
        UndoButton.Visibility = Visibility.Visible;
        HideUndoLater(++undoVersion);
    }
    private async void HideUndoLater(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (version == undoVersion) { UndoButton.Visibility = Visibility.Collapsed; undoDelete = null; }
    }
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        Run(() => { undoDelete?.Invoke(); undoDelete = null; UndoButton.Visibility = Visibility.Collapsed; });
    }
}
