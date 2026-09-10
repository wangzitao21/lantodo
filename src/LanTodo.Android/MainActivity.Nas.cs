using Android.Widget;

namespace LanTodo.Android;

public partial class MainActivity
{
    private void NasSettings()
    {
        Screen("NAS 辅助同步");
        body.AddView(Text("NAS 保存修改，其他设备稍后上线也能收到。手机被系统停止后，下次打开自动补同步。"));
        var enabled = new Switch(this) { Text = "启用 NAS 自动同步", Checked = app.Replicas.Current.NasEnabled };
        var lan = new Switch(this) { Text = "同时保留局域网设备直连", Checked = app.Replicas.Current.LanEnabled };
        body.AddView(enabled); body.AddView(lan);
        void SaveMode()
        {
            try { app.Replicas.SetMode(enabled.Checked, !enabled.Checked || lan.Checked); }
            catch (Exception ex) { Error(ex.Message); }
        }
        enabled.CheckedChange += (_, _) => { if (!enabled.Checked) lan.Checked = true; SaveMode(); };
        lan.CheckedChange += (_, _) => SaveMode();
        foreach (var endpoint in app.Replicas.Current.Endpoints!)
        {
            var name = app.Identity.Devices.FirstOrDefault(d => d.Id == endpoint.DeviceId)?.Name ?? "未授权节点";
            body.AddView(Text(name, 18, true));
            var address = Input("IP 或域名:端口", endpoint.Address);
            var state = app.Node.ReplicaStatuses.First(s => s.DeviceId == endpoint.DeviceId);
            body.AddView(Text(state.State + (state.LastSuccess is { } time ? " · 最近确认 " + time.ToString("MM-dd HH:mm:ss") : "") + (state.Error is { } error ? "\n" + error : ""), 12));
            body.AddView(Button("更新地址", () => { try { app.Replicas.SetEndpoint(endpoint.DeviceId, address.Text ?? ""); NasSettings(); } catch (Exception ex) { Error(ex.Message); } }));
            body.AddView(Button("移除 NAS 地址（保留配对）", () => { try { app.Replicas.RemoveEndpoint(endpoint.DeviceId); NasSettings(); } catch (Exception ex) { Error(ex.Message); } }));
        }
        body.AddView(Button("刷新节点状态", NasSettings));
        body.AddView(Text("添加 NAS", 20, true));
        var host = Input("例如 192.168.1.10:42851 或 nas.home:42851");
        var code = Input("粘贴 NAS 生成的配对码", multiline: true);
        var result = Text("");
        var pair = new Button(this) { Text = "连接 NAS" }; body.AddView(pair); body.AddView(result);
        pair.Click += async (_, _) =>
        {
            pair.Enabled = false; result.Text = "正在连接 NAS…";
            try
            {
                await app.Node.PairAddressAsync(host.Text ?? "", code.Text ?? "", activityToken.Token);
                code.Text = "";
                if (!IsDestroyed) { enabled.Checked = true; result.Text = "配对已保存，正在自动同步。点击刷新查看节点状态。"; }
            }
            catch (Exception ex) { if (!IsDestroyed) result.Text = ex is OperationCanceledException ? "连接已取消或超时" : ex.Message; }
            finally { if (!IsDestroyed) pair.Enabled = true; }
        };
        body.AddView(Text("NAS 地址需网络可达。家中与办公室之间可使用已有 VPN；每台 NAS 分别配对。", 12));
    }
}
