using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LanTodo.Core;
using Microsoft.Win32;
using System.Windows.Input;

namespace LanTodo.Windows;

public partial class MainWindow : Window
{
    private readonly AppRuntime app;
    private string filter = "all";
    private bool ready;
    private DevicesWindow? devicesWindow;
    private StorageWindow? storageWindow;
    public MainWindow(AppRuntime app)
    {
        this.app = app;
        InitializeComponent(); ready = true;
        app.Store.Changed += RefreshSoon; app.Node.Changed += RefreshStatusSoon;
        Closed += (_, _) => { app.Store.Changed -= RefreshSoon; app.Node.Changed -= RefreshStatusSoon; };
        Refresh();
        ContentRendered += (_, _) =>
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => devicesWindow ??= Prepare(new DevicesWindow(app)));
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => storageWindow ??= Prepare(new StorageWindow(app)));
        };
    }
    private T Prepare<T>(T window) where T : Window
    {
        window.Owner = this;
        window.Closing += (_, args) => { if (((App)Application.Current).IsExiting) return; args.Cancel = true; window.Hide(); };
        if (window.Content is FrameworkElement content) { content.Measure(new Size(window.Width,window.Height)); content.Arrange(new Rect(0,0,window.Width,window.Height)); }
        return window;
    }
    private void RefreshSoon() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh); }
    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e) { if (SidebarHint is not null) SidebarHint.Visibility = e.NewSize.Height < 650 ? Visibility.Collapsed : Visibility.Visible; }
    private void RefreshStatusSoon() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshStatus); }
    private void RefreshStatus()
    {
        StatusText.Text = "● " + app.Node.Status;
        StatusText.ToolTip = app.Node.LastError ?? "数据保存在本机；同步只在已授权设备间进行。";
    }
    private void Refresh()
    {
        if (!ready) return;
        var all = app.Store.List();
        int conflicts = all.Count(t => t.Conflict);
        ConflictsButton.Content = conflicts == 0 ? "◇     待确认" : $"◇     待确认  ·  {conflicts}";
        AllButton.Tag = filter == "all" ? "active" : ""; TodayButton.Tag = filter == "today" ? "active" : "";
        DoneButton.Tag = filter == "done" ? "active" : ""; ConflictsButton.Tag = filter == "conflict" ? "active" : "";
        DeleteCompletedButton.Visibility = filter == "done" ? Visibility.Visible : Visibility.Collapsed;
        DeleteCompletedButton.IsEnabled = all.Any(t => !t.Conflict && t.Data.Completed);
        var date = DateFilter.SelectedDate?.ToString("yyyy-MM-dd");
        var todos = all.Where(t => filter switch
        {
            "conflict" => t.Conflict,
            "done" => !t.Conflict && t.Data.Completed,
            "today" => !t.Conflict && !t.Data.Completed && t.Data.Date == DateTime.Today.ToString("yyyy-MM-dd"),
            _ => !t.Conflict && !t.Data.Completed
        }).Where(t => date is null || t.Heads.Any(h => h.Body.Data.Date == date)).ToArray();
        Heading.Text = filter switch { "today" => "今日安排", "done" => "已完成", "conflict" => "待确认的内容", _ => "全部清单" };
        Subtitle.Text = filter == "conflict" ? "双方的修改都已保留。查看版本，决定最终内容。" : $"{DateTime.Now:MM 月 dd 日 · dddd}    /    {todos.Length} 项";
        ItemsPanel.Children.Clear();
        if (todos.Length == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(15,70,15,30), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new TextBlock { Text = "✓", FontSize = 48, Foreground = new SolidColorBrush(Color.FromRgb(22,125,141)), HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock { Text = filter == "conflict" ? "每一个版本，都已妥善安放" : "给想法一个落点", FontSize = 23, Margin = new Thickness(0,20,0,10), HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock { Text = "在下方输入，按 Enter 就能记下。", Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Center });
            ItemsPanel.Children.Add(empty);
        }
        foreach (var todo in todos)
        {
            var card = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(215,225,233)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18,14,18,14), Margin = new Thickness(0,0,0,9) };
            var row = new DockPanel(); card.Child = row;
            if (!todo.Conflict)
            {
                var check = new CheckBox { IsChecked = todo.Data.Completed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,18,0) };
                check.Click += (_, _) => Run(() => app.Store.Save(app.Identity.Id, app.Identity.Name, todo.Data with { Completed = check.IsChecked == true }, todo.Id, todo.VersionIds));
                row.Children.Add(check);
            }
            var body = new StackPanel { Cursor = Cursors.Hand, Focusable = true }; row.Children.Add(body);
            card.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; Edit(todo); } };
            body.KeyDown += (_, e) => { if (e.Key == Key.Enter) Edit(todo); };
            body.Children.Add(new TextBlock { Text = todo.Conflict ? $"{todo.Heads.Length} 个待决定的版本" : todo.Data.Title, FontSize = 17, FontWeight = FontWeights.Medium,
                TextDecorations = !todo.Conflict && todo.Data.Completed ? TextDecorations.Strikethrough : null, Foreground = todo.Data.Completed ? Brushes.SlateGray : new SolidColorBrush(Color.FromRgb(31,41,55)) });
            if (todo.Conflict || todo.Data.Date is not null)
                body.Children.Add(new TextBlock { Text = todo.Conflict ? string.Join(" / ", todo.Heads.Select(h => h.Body.DeviceName).Distinct()) + "  ·  点击选择版本" : todo.Data.Date + "  " + todo.Data.Time, Foreground = Brushes.SlateGray, FontSize = 12, Margin = new Thickness(0,7,0,0) });
            if (!todo.Conflict && todo.Data.Notes.Length > 0) body.Children.Add(new TextBlock { Text = todo.Data.Notes, MaxHeight = 42, TextTrimming = TextTrimming.CharacterEllipsis, TextDecorations = todo.Data.Completed ? TextDecorations.Strikethrough : null, Foreground = Brushes.Gray, Margin = new Thickness(0,8,12,0) });
            if (!todo.Conflict)
            {
                var menu = new ContextMenu();
                var complete = new MenuItem { Header = todo.Data.Completed ? "标为未完成" : "完成" };
                complete.Click += (_, _) => Run(() => app.Store.Save(app.Identity.Id,app.Identity.Name,todo.Data with { Completed = !todo.Data.Completed },todo.Id,todo.VersionIds));
                var delete = new MenuItem { Header = "删除" };
                delete.Click += (_, _) => Run(() => app.Store.Save(app.Identity.Id,app.Identity.Name,todo.Data with { Deleted = true },todo.Id,todo.VersionIds));
                menu.Items.Add(complete); menu.Items.Add(new Separator()); menu.Items.Add(delete); card.ContextMenu = menu;
            }
            ItemsPanel.Children.Add(card);
        }
        RefreshStatus();
    }
    private void Run(Action action) { try { action(); } catch (Exception ex) { ModernDialog.Show(this, ex.Message, "未完成操作"); Refresh(); } }
    private void Edit(TodoView? todo) { new TodoEditor(app, todo) { Owner = this }.ShowDialog(); Refresh(); }
    private void New_Click(object sender, RoutedEventArgs e) => Edit(null);
    private void QuickInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { QuickAdd(); e.Handled = true; } }
    private void QuickAdd_Click(object sender, RoutedEventArgs e) => QuickAdd();
    private void QuickAdd()
    {
        if (string.IsNullOrWhiteSpace(QuickInput.Text)) return;
        Run(() => { app.Store.Save(app.Identity.Id, app.Identity.Name, new TodoData(QuickInput.Text.Trim())); QuickInput.Clear(); filter = "all"; DateFilter.SelectedDate = null; Refresh(); QuickInput.Focus(); });
    }
    private void Today_Click(object sender, RoutedEventArgs e) { filter = "today"; DateFilter.SelectedDate = null; Refresh(); }
    private void All_Click(object sender, RoutedEventArgs e) { filter = "all"; Refresh(); }
    private void Done_Click(object sender, RoutedEventArgs e) { filter = "done"; Refresh(); }
    private void Conflicts_Click(object sender, RoutedEventArgs e) { filter = "conflict"; Refresh(); }
    private void DateFilter_Changed(object? sender, EventArgs e) => Refresh();
    private void ClearDate_Click(object sender, RoutedEventArgs e) { DateFilter.SelectedDate = null; Refresh(); }
    private void Devices_Click(object sender, RoutedEventArgs e) => (devicesWindow ??= Prepare(new DevicesWindow(app))).ShowDialog();
    private void Sync_Click(object sender, RoutedEventArgs e) { app.Node.RequestSync(); StatusText.Text = "已请求同步，正在连接已配对设备…"; }
    private async void DeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = app.Store.List().Where(t => !t.Conflict && t.Data.Completed).ToArray();
        if (confirmed.Length == 0 || ModernDialog.Show(this,$"删除全部 {confirmed.Length} 条已完成内容（包含其他日期）？删除会同步到其他设备，修改历史仍保留。","删除已完成内容",MessageBoxButton.YesNo,MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsEnabled = false;
        try
        {
            var result = await Task.Run(() => app.Store.DeleteCompleted(app.Identity.Id,app.Identity.Name,confirmed));
            if (result.Skipped > 0) ModernDialog.Show(this,$"已删除 {result.Deleted} 条；另有 {result.Skipped} 条在确认后发生修改，已保留。","清理完成");
        }
        catch (Exception ex) { ModernDialog.Show(this,"清理未全部完成，已保留未处理内容。\n"+ex.Message,"清理结果"); }
        finally { IsEnabled = true; Refresh(); }
    }
    private void Backup_Click(object sender, RoutedEventArgs e) => (storageWindow ??= Prepare(new StorageWindow(app))).ShowDialog();
}
