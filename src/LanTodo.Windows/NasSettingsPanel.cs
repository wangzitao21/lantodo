using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LanTodo.Core;

namespace LanTodo.Windows;

public sealed class NasSettingsPanel : StackPanel
{
    public NasSettingsPanel(AppRuntime app)
    {
        Children.Add(new TextBlock { Text = "NAS 辅助同步", FontSize = 18, FontWeight = FontWeights.SemiBold });
        Children.Add(new TextBlock { Text = "NAS 保存修改，其他设备稍后上线也能收到。可添加家中、办公室等多个节点。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0,8,0,12) });
        var enabled = new CheckBox { Content = "启用 NAS 自动同步", IsChecked = app.Replicas.Current.NasEnabled, Margin = new Thickness(0,4,0,8) };
        var lan = new CheckBox { Content = "同时保留局域网设备直连", IsChecked = app.Replicas.Current.LanEnabled, Margin = new Thickness(0,4,0,12) };
        Children.Add(enabled); Children.Add(lan);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
        var rows = new StackPanel(); Children.Add(rows);
        var labels = new Dictionary<string, TextBlock>();
        void SaveMode()
        {
            try { app.Replicas.SetMode(enabled.IsChecked == true, enabled.IsChecked != true || lan.IsChecked == true); }
            catch (Exception ex) { message.Text = ex.Message; }
        }
        enabled.Click += (_, _) => { if (enabled.IsChecked != true) lan.IsChecked = true; SaveMode(); };
        lan.Click += (_, _) => SaveMode();
        void RefreshStatus()
        {
            foreach (var state in app.Node.ReplicaStatuses)
                if (labels.TryGetValue(state.DeviceId, out var label))
                    label.Text = state.State + (state.LastSuccess is { } time ? " · 最近确认 " + time.ToString("MM-dd HH:mm:ss") : "") + (state.Error is { } error ? "\n" + error : "");
        }
        void Reload()
        {
            rows.Children.Clear(); labels.Clear(); enabled.IsChecked = app.Replicas.Current.NasEnabled;
            foreach (var endpoint in app.Replicas.Current.Endpoints!)
            {
                var name = app.Identity.Devices.FirstOrDefault(d => d.Id == endpoint.DeviceId)?.Name ?? "未授权节点";
                rows.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,10,0,4) });
                var address = new TextBox { Text = endpoint.Address, MaxLength = 512 }; rows.Children.Add(address);
                var state = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.SlateGray }; rows.Children.Add(state); labels[endpoint.DeviceId] = state;
                var actions = new StackPanel { Orientation = Orientation.Horizontal }; rows.Children.Add(actions);
                var save = new Button { Content = "更新地址" }; actions.Children.Add(save);
                save.Click += (_, _) => { try { app.Replicas.SetEndpoint(endpoint.DeviceId, address.Text); message.Text = "地址已保存"; Reload(); } catch (Exception ex) { message.Text = ex.Message; } };
                var remove = new Button { Content = "移除 NAS 地址" }; actions.Children.Add(remove);
                remove.Click += (_, _) => { try { app.Replicas.RemoveEndpoint(endpoint.DeviceId); Reload(); } catch (Exception ex) { message.Text = ex.Message; } };
            }
            RefreshStatus();
        }
        Children.Add(new TextBlock { Text = "添加 NAS：地址（IP 或域名:端口）", Margin = new Thickness(0,14,0,4) });
        var host = new TextBox { MaxLength = 512, ToolTip = "例如 192.168.1.10:42851 或 nas.home:42851" }; Children.Add(host);
        Children.Add(new TextBlock { Text = "NAS 生成的配对码" });
        var code = new TextBox { TextWrapping = TextWrapping.Wrap, MaxLength = 4096, MaxHeight = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Children.Add(code);
        var pair = new Button { Content = "连接 NAS", HorizontalAlignment = HorizontalAlignment.Left }; Children.Add(pair); Children.Add(message);
        CancellationTokenSource? pairing = null;
        pair.Click += async (_, _) =>
        {
            pairing = new(); pair.IsEnabled = false; message.Text = "正在连接 NAS…";
            try { await app.Node.PairAddressAsync(host.Text, code.Text, pairing.Token); code.Clear(); message.Text = "配对已保存，正在自动同步。"; Reload(); }
            catch (Exception ex) { message.Text = ex is OperationCanceledException ? "连接已取消或超时" : ex.Message; }
            finally { pairing.Dispose(); pairing = null; pair.IsEnabled = true; }
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshStatus();
        Loaded += (_, _) => { Reload(); timer.Start(); };
        Unloaded += (_, _) => { timer.Stop(); pairing?.Cancel(); code.Clear(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) { Reload(); timer.Start(); } else { timer.Stop(); pairing?.Cancel(); code.Clear(); } };
        Reload();
    }
}
