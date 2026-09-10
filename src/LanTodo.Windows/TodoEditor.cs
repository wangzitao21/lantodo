using System.Windows;
using System.Windows.Controls;
using LanTodo.Core;

namespace LanTodo.Windows;

public sealed class TodoEditor : Window
{
    private readonly TextBox title = new(), notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DateSelector date = new() { Placeholder = "不设截止日期", Margin = new Thickness(0,4,0,12) };
    private readonly TextBox time = new();
    private readonly CheckBox completed = new() { Content = "已完成", Margin = new Thickness(0,5,0,12) };
    private readonly CheckBox deleted = new() { Content = "最终决定：删除这条待办（历史仍保留）", Margin = new Thickness(0,5,0,12) };
    public TodoEditor(AppRuntime app, TodoView? todo)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = todo?.Conflict == true ? "保留的版本 · 处理冲突" : todo is null ? "新建待办" : "编辑待办";
        Width = 640; Height = 730; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (todo?.Conflict == true)
        {
            panel.Children.Add(new TextBlock { Text = "以下版本均已完整保存。可选取一个版本，再编辑最终内容；保存后会同步你的决定。", Margin = new Thickness(0,0,0,14) });
            foreach (var head in todo.Heads)
            {
                var data = head.Body.Data;
                panel.Children.Add(new TextBlock { Text = $"{head.Body.DeviceName} · {head.Body.CreatedUtc}\n{(data.Deleted ? "[已删除] " : "")}{data.Title}\n{data.Date} {data.Time} · {(data.Completed ? "已完成" : "未完成")}\n{data.Notes}", Margin = new Thickness(0,8,0,4) });
                var use = new Button { Content = "以这个版本为基础" }; use.Click += (_, _) => Fill(data); panel.Children.Add(use);
            }
        }
        foreach (var (label, field) in new (string, FrameworkElement)[] { ("标题", title), ("备注", notes), ("截止日期", date), ("截止时间（HH:mm，可留空）", time) })
        { panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(field); }
        panel.Children.Add(completed);
        if (todo is not null) panel.Children.Add(deleted);
        Fill(todo?.Data ?? new TodoData(""));
        var save = new Button { Content = todo?.Conflict == true ? "确认最终版本并保存" : "保存到本机", Background = System.Windows.Media.Brushes.DarkSlateGray, Foreground = System.Windows.Media.Brushes.White };
        panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            try
            {
                app.Store.Save(app.Identity.Id, app.Identity.Name, new TodoData(title.Text.Trim(), notes.Text, date.SelectedDate?.ToString("yyyy-MM-dd"), Empty(time.Text), completed.IsChecked == true, deleted.IsChecked == true), todo?.Id, todo?.VersionIds);
                DialogResult = true;
            }
            catch (Exception ex) { ModernDialog.Show(this, ex.Message, "内容尚未保存"); }
        };
        if (todo is not null)
        {
            var history = new Button { Content = "查看全部修改历史" }; panel.Children.Add(history);
            history.Click += (_, _) =>
            {
                var text = string.Join("\n\n────────\n\n", app.Store.History(todo.Id).Reverse().Select(r => $"{r.Body.DeviceName} · {r.Body.CreatedUtc}\n{(r.Body.Data.Deleted ? "[删除] " : "")}{r.Body.Data.Title}\n{r.Body.Data.Date} {r.Body.Data.Time} · {(r.Body.Data.Completed ? "已完成" : "未完成")}\n{r.Body.Data.Notes}"));
                new Window { Title = "不可变的修改历史", Width = 600, Height = 650, Owner = this, Content = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.ShowDialog();
            };
        }
    }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void Fill(TodoData data) { title.Text = data.Title; notes.Text = data.Notes; date.SelectedDate = data.Date is null ? null : DateTime.ParseExact(data.Date,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture); time.Text = data.Time ?? ""; completed.IsChecked = data.Completed; deleted.IsChecked = data.Deleted; }
}
