using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LanTodo.Core;

namespace LanTodo.Windows;

public partial class MainWindow
{
    private void AddConflictChoices(StackPanel panel, TodoView todo)
    {
        foreach (var head in todo.Heads)
        {
            var data = head.Body.Data;
            var choice = new StackPanel();
            choice.Children.Add(new TextBlock { Text = app.DisplayName(head.Body.Actor, head.Body.DeviceName) + " · " + DateTimeOffset.Parse(head.Body.CreatedUtc).ToLocalTime().ToString("MM-dd HH:mm"), FontSize = 12, Foreground = Brushes.SlateGray });
            choice.Children.Add(new TextBlock { Text = (data.Deleted ? "已删除 · " : data.Completed ? "已完成 · " : "待办 · ") + data.Title, FontWeight = FontWeights.SemiBold });
            if (data.Date is not null) choice.Children.Add(new TextBlock { Text = data.Date + " " + data.Time, FontSize = 12 });
            if (data.Notes.Length > 0) choice.Children.Add(new TextBlock { Text = data.Notes, MaxHeight = 64, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = data.Notes });
            if (data.Attachments is { Length: > 0 }) choice.Children.Add(new TextBlock { Text = string.Join(" · ", data.Attachments.Select(a => a.Name)), FontSize = 12 });
            var use = new Button { Content = data.Deleted ? "采用删除" : "保留这个版本", HorizontalAlignment = HorizontalAlignment.Left };
            use.Click += (_, e) => { e.Handled = true; Run(() => app.Store.Save(app.Identity.Id, app.Identity.Name, data, todo.Id, todo.VersionIds)); };
            choice.Children.Add(use);
            panel.Children.Add(new Border { Child = choice, Background = new SolidColorBrush(Color.FromRgb(245,249,250)), Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Margin = new Thickness(0,8,0,0) });
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var edit = new Button { Content = "编辑合并…" }; edit.Click += (_, e) => { e.Handled = true; Edit(todo); };
        var delete = new Button { Content = "删除此条", Foreground = Brushes.Firebrick };
        delete.Click += (_, e) => { e.Handled = true; Run(() => app.Store.Save(app.Identity.Id, app.Identity.Name, todo.Data with { Deleted = true }, todo.Id, todo.VersionIds)); };
        actions.Children.Add(edit); actions.Children.Add(delete); panel.Children.Add(actions);
    }
}
