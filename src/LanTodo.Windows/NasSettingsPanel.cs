using System.Windows;
using System.Windows.Controls;
using LanTodo.Core;

namespace LanTodo.Windows;

// Retained class name for saved UI integration; all peers now use the same connection rules.
public sealed class NasSettingsPanel : StackPanel
{
    public NasSettingsPanel(AppRuntime app)
    {
        Children.Add(new TextBlock { Text = "同一网络自动发现。跨网络可使用已有 VPN 或可达的 IP / 域名与同步端口。此设置适用于所有设备。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) });
        Children.Add(new TextBlock { Text = "本机对外地址（可选，会放入邀请与成员信息）" });
        var address = new TextBox { Text = app.Node.AdvertisedAddress, MaxLength = 300 }; Children.Add(address);
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap }; var save = new Button { Content = "保存本机地址" }; Children.Add(save); Children.Add(result);
        save.Click += (_, _) => { try { app.Node.SetAdvertisedAddress(address.Text); result.Text = "已保存。请重新生成邀请，让新设备使用此地址。"; } catch (Exception ex) { result.Text = ex.Message; } };
        var hours = new ComboBox { ItemsSource = new[] { "每 1 小时核对完整历史", "每 2 小时核对完整历史" }, SelectedIndex = app.ReconcileHours - 1, Margin = new Thickness(0, 12, 0, 8) }; Children.Add(hours);
        hours.SelectionChanged += (_, _) => { try { app.SetReconcileHours(hours.SelectedIndex + 1); } catch (Exception ex) { result.Text = ex.Message; } };
        Children.Add(new TextBlock { Text = "修改仍会立即触发同步，定期核对用于补漏。", TextWrapping = TextWrapping.Wrap });
        var routes = new StackPanel(); Children.Add(new Expander { Header = "成员备用地址", Content = routes });
        Loaded += (_, _) =>
        {
            routes.Children.Clear();
            foreach (var device in app.Identity.Devices)
            {
                routes.Children.Add(new TextBlock { Text = device.Name, Margin = new Thickness(0,12,0,4) });
                var endpoint = new TextBox { Text = app.Replicas.Current.Endpoints?.FirstOrDefault(e => e.DeviceId == device.Id)?.Address ?? "", MaxLength = 300 }; routes.Children.Add(endpoint);
                var update = new Button { Content = "保存备用地址（留空移除）" }; routes.Children.Add(update);
                update.Click += (_, _) => { try { if (string.IsNullOrWhiteSpace(endpoint.Text)) app.Replicas.RemoveEndpoint(device.Id); else app.Replicas.SetEndpoint(device.Id, endpoint.Text); result.Text = "备用地址已更新。"; } catch (Exception ex) { result.Text = ex.Message; } };
            }
        };
    }
}
