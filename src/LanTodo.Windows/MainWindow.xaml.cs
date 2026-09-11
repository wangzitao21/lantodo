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
    private int refreshQueued, statusQueued;
    private string? renderedState;
    private readonly System.Windows.Threading.DispatcherTimer attachmentTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DevicesWindow? devicesWindow;
    private StorageWindow? storageWindow;
    public MainWindow(AppRuntime app)
    {
        this.app = app;
        InitializeComponent(); ready = true;
        SetupAttachments();
        app.CanSwitchSpace=()=>!attachmentBusy; app.ActiveSpaceChanged+=SpaceSwitched;
        app.DataChanged += RefreshSoon; app.StatusChanged += RefreshStatusSoon; app.MembershipChanged += RefreshSoon;
        Activated += (_, _) => RefreshSoon();
        attachmentTimer.Tick += (_, _) => { if (IsVisible && IsActive) RefreshSoon(); };
        attachmentTimer.Start();
        Closed += (_, _) => { attachmentTimer.Stop(); app.ActiveSpaceChanged-=SpaceSwitched; app.CanSwitchSpace=null; app.DataChanged -= RefreshSoon; app.StatusChanged -= RefreshStatusSoon; app.MembershipChanged -= RefreshSoon; };

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
    private void SpaceSwitched()
    {
        Dispatcher.Invoke(()=> { Title="LanTodo";RefreshStatus(); });
    }
    private void RefreshSoon() { if (!Dispatcher.HasShutdownStarted && System.Threading.Interlocked.Exchange(ref refreshQueued, 1) == 0) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { System.Threading.Interlocked.Exchange(ref refreshQueued, 0); Refresh(); }); }
    private void RefreshStatusSoon() { if (!Dispatcher.HasShutdownStarted && System.Threading.Interlocked.Exchange(ref statusQueued, 1) == 0) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { System.Threading.Interlocked.Exchange(ref statusQueued, 0); RefreshStatus(); }); }
    private void RefreshStatus()
    {
        StatusText.Text = "● " + app.Status;
        StatusText.ToolTip = app.Node.LastError ?? "数据保存在本机；同步只在已授权设备间进行。";
    }
    private void Refresh()
    {
        if (!ready) return;
        var all = filter == "trash" ? app.Store.Trash() : app.Store.List();
        var state = filter + DateFilter.SelectedDate + DateTime.Today + app.ShowDeviceSource + string.Join(";", all.SelectMany(t => t.Heads).Select(h => h.Id + app.DisplayName(h.Body.Actor, h.Body.DeviceName))) + string.Join(";", app.Store.ActiveAttachments().Select(a => a.Hash + app.Store.Attachments.Availability(a)));
        if (state == renderedState) { RefreshStatus(); return; }
        renderedState = state;
        int conflicts = all.Count(t => t.Conflict);
        ConflictsButton.Content = conflicts == 0 ? "◇     待确认" : $"◇     待确认  ·  {conflicts}";
        AllButton.Tag = filter == "all" ? "active" : ""; TrashButton.Tag = filter == "trash" ? "active" : "";
        DoneButton.Tag = filter == "done" ? "active" : ""; ConflictsButton.Tag = filter == "conflict" ? "active" : "";
        DeleteCompletedButton.Visibility = filter is "done" or "trash" ? Visibility.Visible : Visibility.Collapsed;
        NewButton.Visibility = filter is "done" or "trash" ? Visibility.Collapsed : Visibility.Visible;
        DateFilter.Placeholder = DateTime.Today.ToString("MM-dd dddd") + " · 全部";
        DeleteCompletedButton.Content = filter == "trash" ? "全部清除" : "全部删除";
        DeleteCompletedButton.IsEnabled = filter == "trash" ? all.Length > 0 : all.Any(t => !t.Conflict && t.Data.Completed);
        Composer.Visibility = filter == "trash" ? Visibility.Collapsed : Visibility.Visible;
        ListScroll.Background = Brushes.Transparent;
        TrashButton.Background = filter == "trash" ? new SolidColorBrush(Color.FromRgb(247,215,228)) : Brushes.Transparent;
        TrashButton.Foreground = filter == "trash" ? new SolidColorBrush(Color.FromRgb(140,61,96)) : new SolidColorBrush(Color.FromRgb(181,199,208));
        var date = DateFilter.SelectedDate?.ToString("yyyy-MM-dd");
        var todos = all.Where(t => filter switch
        {
            "conflict" => t.Conflict,
            "done" => !t.Conflict && t.Data.Completed,
            "trash" => true,
            _ => !t.Conflict && !t.Data.Completed
        }).Where(t => date is null || t.Heads.Any(h => h.Body.Data.Date == date)).ToArray();
        Title = "LanTodo · " + app.Identity.SpaceName;
        Heading.Text = filter switch { "trash" => "回收站", "done" => "已完成", "conflict" => "待确认", _ => "全部清单" };
        ItemCount.Text = $"{todos.Length} 项";
        Subtitle.Text = filter == "conflict" ? "选一张卡片保留，也可以编辑合并或删除。" : "";
        Subtitle.Visibility = filter == "conflict" ? Visibility.Visible : Visibility.Collapsed;
        ItemsPanel.Children.Clear();
        if (todos.Length == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(15,70,15,30), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new TextBlock { Text = "✓", FontSize = 48, Foreground = new SolidColorBrush(Color.FromRgb(22,125,141)), HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock { Text = filter == "conflict" ? "每一个版本，都已妥善安放" : "给想法一个落点", FontSize = 23, Margin = new Thickness(0,20,0,10), HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock { Text = filter == "trash" ? "回收站是空的。" : "在下方输入，按 Enter 就能记下。", Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Center });
            ItemsPanel.Children.Add(empty);
        }
        foreach (var todo in todos)
        {
            var card = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(215,225,233)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18,14,18,14), Margin = new Thickness(0,0,0,9) };
            var row = new DockPanel(); card.Child = row;
            if (app.ShowDeviceSource)
            {
                var source = new TextBlock { Text = string.Join(" / ", todo.Heads.Select(h => app.DisplayName(h.Body.Actor, h.Body.DeviceName)).Distinct()), FontSize = 11, Foreground = Brushes.Gray, MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12,0,0,0) };
                source.ToolTip = source.Text; DockPanel.SetDock(source, Dock.Right); row.Children.Add(source);
            }
            if (!todo.Conflict)
            {
                var check = new CheckBox { IsChecked = todo.Data.Completed, Visibility = filter == "trash" ? Visibility.Collapsed : Visibility.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,18,0) };
                check.Click += (_, _) => Run(() => app.Store.Save(app.Identity.Id, app.Identity.Name, todo.Data with { Completed = check.IsChecked == true }, todo.Id, todo.VersionIds));
                row.Children.Add(check);
            }
            var body = new StackPanel { Cursor = Cursors.Hand, Focusable = true }; row.Children.Add(body);
            card.MouseLeftButtonUp += (_, e) => { e.Handled = true; if(filter != "trash") Edit(todo); };
            body.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.OriginalSource == body) { e.Handled = true; if(filter != "trash") Edit(todo); } };
            body.Children.Add(new TextBlock { Text = todo.Data.Title, FontSize = 17, FontWeight = FontWeights.Medium,
                TextDecorations = !todo.Conflict && todo.Data.Completed ? TextDecorations.Strikethrough : null, Foreground = todo.Data.Completed ? Brushes.SlateGray : new SolidColorBrush(Color.FromRgb(31,41,55)) });
            if (todo.Conflict || todo.Data.Date is not null)
                body.Children.Add(new TextBlock { Text = todo.Conflict ? string.Join(" / ", todo.Heads.Select(h => app.DisplayName(h.Body.Actor, h.Body.DeviceName)).Distinct()) + "  ·  点击选择版本" : todo.Data.Date + "  " + todo.Data.Time, Foreground = Brushes.SlateGray, FontSize = 12, Margin = new Thickness(0,7,0,0) });
            if (!todo.Conflict && todo.Data.Notes.Length > 0) body.Children.Add(new TextBlock { Text = todo.Data.Notes, MaxHeight = 42, TextTrimming = TextTrimming.CharacterEllipsis, TextDecorations = todo.Data.Completed ? TextDecorations.Strikethrough : null, Foreground = Brushes.Gray, Margin = new Thickness(0,8,12,0) });
            if (!todo.Conflict)
            {
                var menu = new ContextMenu();
                foreach (var attachment in todo.Data.Attachments ?? []) AddAttachmentView(body, attachment);
                var complete = new MenuItem { Header = todo.Data.Deleted ? "恢复" : todo.Data.Completed ? "标为未完成" : "完成" };
                complete.Click += (_, _) => Run(() => app.Store.Save(app.Identity.Id,app.Identity.Name,todo.Data.Deleted ? todo.Data with { Deleted = false } : todo.Data with { Completed = !todo.Data.Completed },todo.Id,todo.VersionIds));
                var delete = new MenuItem { Header = todo.Data.Deleted ? "彻底清除" : "移到回收站" };
                delete.Click += (_, _) => Run(() => { if(todo.Data.Deleted) { if(ModernDialog.Show(this,"彻底清除此记录？此操作会同步到所有已连接设备。","彻底清除",MessageBoxButton.YesNo)==MessageBoxResult.Yes)app.Store.Purge(app.Identity.Id,app.Identity.Name,todo); } else app.Store.Save(app.Identity.Id,app.Identity.Name,todo.Data with { Deleted = true },todo.Id,todo.VersionIds); });
                menu.Items.Add(complete); menu.Items.Add(new Separator()); menu.Items.Add(delete); card.ContextMenu = menu;
            }
            if (todo.Conflict) AddConflictChoices(body, todo);
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
        if (attachmentBusy || (string.IsNullOrWhiteSpace(QuickInput.Text) && pendingAttachments.Count == 0)) return;
        Run(() => { app.Store.Save(app.Identity.Id, app.Identity.Name, new TodoData(string.IsNullOrWhiteSpace(QuickInput.Text) ? pendingAttachments[0].Name : QuickInput.Text.Trim(), Attachments: pendingAttachments.Count == 0 ? null : pendingAttachments.ToArray())); QuickInput.Clear(); pendingAttachments.Clear(); RenderPending(); filter = "all"; DateFilter.SelectedDate = null; Refresh(); QuickInput.Focus(); });
    }
    private void Trash_Click(object sender, RoutedEventArgs e) { filter = "trash"; DateFilter.SelectedDate = null; Refresh(); }
    private void All_Click(object sender, RoutedEventArgs e) { filter = "all"; Refresh(); }
    private void Done_Click(object sender, RoutedEventArgs e) { filter = "done"; Refresh(); }
    private void Conflicts_Click(object sender, RoutedEventArgs e) { filter = "conflict"; Refresh(); }
    private void DateFilter_Changed(object? sender, EventArgs e) => Refresh();
    private void ClearDate_Click(object sender, RoutedEventArgs e) { DateFilter.SelectedDate = null; Refresh(); }
    private void Devices_Click(object sender, RoutedEventArgs e) => (devicesWindow ??= Prepare(new DevicesWindow(app))).ShowDialog();
    private void Sync_Click(object sender, RoutedEventArgs e) { app.Node.RequestSync(); StatusText.Text = "已请求同步，正在寻找在线成员…"; }
    private async void DeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        if(filter == "trash")
        {
            var trash=app.Store.Trash();
            if(trash.Length==0 || ModernDialog.Show(this,$"彻底清除回收站中的 {trash.Length} 条记录？此操作会同步到所有已连接设备。","清空回收站",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            Run(()=>{foreach(var item in trash)app.Store.Purge(app.Identity.Id,app.Identity.Name,item);}); Refresh(); return;
        }
        var confirmed = app.Store.List().Where(t => !t.Conflict && t.Data.Completed).ToArray();
        if (confirmed.Length == 0 || ModernDialog.Show(this,$"删除全部 {confirmed.Length} 条已完成内容（包含其他日期）？内容将移入回收站，并同步到所有已连接设备。","删除已完成内容",MessageBoxButton.YesNo,MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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
