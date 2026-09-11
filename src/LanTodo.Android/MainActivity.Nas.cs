using Android.Widget;
namespace LanTodo.Android;
public partial class MainActivity
{
    private void NasSettings()
    {
        Screen("高级连接设置");BeginCard();
        body.AddView(Text("同一网络自动发现。跨网络可使用已有 VPN 或可达 IP / 域名与同步端口，所有成员使用同一规则。"));
        var address = Input("本机对外地址（可选）", app.Node.AdvertisedAddress);
        body.AddView(Button("保存本机地址", () => { try { app.Node.SetAdvertisedAddress(address.Text ?? ""); Toast.MakeText(this, "已保存，重新生成邀请即可带上此地址", ToastLength.Long)?.Show(); } catch (Exception ex) { Error(ex.Message); } }));
        body.AddView(Text("填写后，此地址会自动提供给空间成员。通常留空即可；本机同步端口为 " + app.Node.Port + "。", 12));
        BeginCard("当前连接状态");
        foreach (var state in app.Node.SpaceStatuses)
        {
            var name = app.Identity.Devices.FirstOrDefault(d => d.Id == state.DeviceId)?.Name ?? "设备";
            body.AddView(Text(name + " · " + state.State + (state.Address.Length > 0 ? "\n" + state.Address : "") + (state.Error is null ? "" : "\n" + state.Error), 12));
            var fallback = Input("备用地址（可选）", app.Replicas.Current.Endpoints?.FirstOrDefault(e => e.DeviceId == state.DeviceId)?.Address ?? "");
            body.AddView(Button("保存备用地址（留空移除）", () => { try { if (string.IsNullOrWhiteSpace(fallback.Text)) app.Replicas.RemoveEndpoint(state.DeviceId); else app.Replicas.SetEndpoint(state.DeviceId, fallback.Text); Toast.MakeText(this,"已保存",ToastLength.Short)?.Show(); } catch (Exception ex) { Error(ex.Message); } }));
        }
    }
}
