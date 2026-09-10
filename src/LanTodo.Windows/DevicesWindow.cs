using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using LanTodo.Core;

namespace LanTodo.Windows;

public sealed class DevicesWindow : Window
{
    public DevicesWindow(AppRuntime app)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Title = "我的设备"; Width = 650; Height = 660; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "设备与同步", FontSize = 26, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = app.Identity.Name + " · 本机", Foreground = Brushes.SlateGray, Margin = new Thickness(0,6,0,22) });
        Border Card(UIElement content) => new() { Child = content, Background = Brushes.White, CornerRadius = new CornerRadius(14), BorderBrush = new SolidColorBrush(Color.FromRgb(222,230,237)), BorderThickness = new Thickness(1), Padding = new Thickness(20), Margin = new Thickness(0,0,0,14) };
        var sync = new StackPanel(); panel.Children.Add(Card(sync));
        sync.Children.Add(new TextBlock { Text = "自动同步", FontSize = 18, FontWeight = FontWeights.SemiBold });
        sync.Children.Add(new TextBlock { Text = "有修改时立即同步，空闲时定期核对。", Foreground = Brushes.SlateGray, Margin = new Thickness(0,8,0,12) });
        var intervals = new StackPanel { Orientation = Orientation.Horizontal }; sync.Children.Add(intervals);
        var one = new Button { Content = "每 1 小时核对" }; var two = new Button { Content = "每 2 小时核对" };
        intervals.Children.Add(one); intervals.Children.Add(two);
        void PaintInterval()
        {
            foreach (var (button, hours) in new[] { (one,1), (two,2) })
            {
                var selected = app.ReconcileHours == hours;
                button.Background = new SolidColorBrush(selected ? Color.FromRgb(22,125,141) : Color.FromRgb(239,244,247));
                button.Foreground = selected ? Brushes.White : Brushes.SlateGray;
            }
        }
        void SetInterval(int hours) { try { app.SetReconcileHours(hours); PaintInterval(); } catch (Exception ex) { ModernDialog.Show(this,ex.Message,"设置未保存"); } }
        one.Click += (_, _) => SetInterval(1); two.Click += (_, _) => SetInterval(2); PaintInterval();
        var syncNow = new Button { Content = "立即同步", HorizontalAlignment = HorizontalAlignment.Left }; syncNow.Click += (_, _) => app.Node.RequestSync(); sync.Children.Add(syncNow);
        var devices = new StackPanel(); panel.Children.Add(Card(devices));
        var setup = new StackPanel();
        var addDevice = new Button { Content = "＋ 连接新设备", HorizontalContentAlignment = HorizontalAlignment.Left, FontWeight = FontWeights.SemiBold };
        panel.Children.Add(addDevice);
        var setupCard = Card(setup); setupCard.Visibility = app.Identity.Devices.Length == 0 ? Visibility.Visible : Visibility.Collapsed; panel.Children.Add(setupCard);
        addDevice.Click += (_, _) => setupCard.Visibility = setupCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        setup.Children.Add(new TextBlock { Text = "首次连接一次，以后自动同步", FontSize = 18, FontWeight = FontWeights.SemiBold });
        setup.Children.Add(new TextBlock { Text = "两台设备连接同一局域网。一台生成配对码，另一台粘贴确认。配对码 5 分钟有效；已建立的连接会长期保存。", Foreground = Brushes.SlateGray, Margin = new Thickness(0,10,0,14) });
        var code = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var generate = new Button { Content = "第 1 步：生成首次配对码（给另一台设备）" }; setup.Children.Add(generate); setup.Children.Add(code);
        generate.Click += (_, _) => { code.Text = app.Identity.CreateInvite(); };
        var copy = new Button { Content = "复制这次配对码" }; setup.Children.Add(copy);
        copy.Click += (_, _) => { if (code.Text.Length > 0) Clipboard.SetText(code.Text); };
        var input = new TextBox { TextWrapping = TextWrapping.Wrap, MaxLength = 4096 }; setup.Children.Add(new TextBlock { Text = "如果另一台已经生成配对码，请粘贴到这里" }); setup.Children.Add(input);
        var pair = new Button { Content = "第 2 步：确认连接，以后自动同步" }; setup.Children.Add(pair);
        var result = new TextBlock { Margin = new Thickness(0,8,0,14) }; setup.Children.Add(result);
        void Reload()
        {
            devices.Children.Clear();
            devices.Children.Add(new TextBlock { Text = "已记住的设备 · 无需重复配对", FontSize = 18, Margin = new Thickness(0,14,0,8) });
            foreach (var device in app.Identity.Devices)
            {
                var row = new DockPanel { Margin = new Thickness(0,10,0,4) };
                var revoke = new Button { Content = "断开", Padding = new Thickness(12,8,12,8), Foreground = new SolidColorBrush(Color.FromRgb(175,67,78)), Background = new SolidColorBrush(Color.FromRgb(252,240,241)) };
                DockPanel.SetDock(revoke,Dock.Right); row.Children.Add(revoke);
                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = device.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
                label.Children.Add(new TextBlock { Text = "● 已记住 · 相遇时自动同步", FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0,4,0,0) });
                row.Children.Add(label); devices.Children.Add(row);
                revoke.Click += (_, _) => { if (ModernDialog.Show(this, $"取消对 {device.Name} 的授权？对方已经收到的历史数据仍保留在对方设备。", "取消授权", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { app.Identity.Revoke(device.Id); Reload(); } };
            }
            if (app.Identity.Devices.Length == 0) devices.Children.Add(new TextBlock { Text = "还没有连接过其他设备，请在下方完成第一次连接。" });
            else { setupCard.Visibility = Visibility.Collapsed; code.Clear(); }
        }
        usingToken = new CancellationTokenSource();
        pair.Click += async (_, _) =>
        {
            pair.IsEnabled = false; result.Text = "正在寻找设备并配对…";
            try { await app.Node.PairAsync(input.Text, usingToken.Token); result.Text = "配对成功。之后相遇时会自动同步。"; input.Clear(); Reload(); }
            catch (Exception ex) { result.Text = ex is OperationCanceledException ? "未找到设备或连接超时。请保持双方打开，检查 Wi-Fi、防火墙及配对码有效期。" : ex.Message; }
            finally { pair.IsEnabled = true; }
        };
        void TrustChanged() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Reload); }
        app.Identity.TrustChanged += TrustChanged;
        Closed += (_, _) => { usingToken.Cancel(); app.Identity.CancelInvite(); app.Identity.TrustChanged -= TrustChanged; };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { usingToken.Dispose(); usingToken = new CancellationTokenSource(); Reload(); }
            else { usingToken.Cancel(); app.Identity.CancelInvite(); code.Clear(); input.Clear(); result.Text = ""; }
        };
        Reload();
    }
    private CancellationTokenSource usingToken;
}
