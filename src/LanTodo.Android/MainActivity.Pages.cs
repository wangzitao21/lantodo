using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using LanTodo.Core;

namespace LanTodo.Android;

public partial class MainActivity
{
    private void Editor(TodoView? todo)
    {
        Screen(todo?.Conflict == true ? "处理冲突" : todo is null ? "新建待办" : "编辑待办");
        LinearLayout? versions = null;
        if (todo?.Conflict == true)
        {
            body.AddView(Text("每个版本都已保留。选一个作为基础，或编辑出最终内容，再确认保存。"));
            versions = new LinearLayout(this) { Orientation = Orientation.Vertical }; body.AddView(versions);
        }
        var title = Input("标题", todo?.Data.Title ?? "");
        var notes = Input("备注", todo?.Data.Notes ?? "", true);
        var date = Input("截止日期 yyyy-MM-dd（可留空）", todo?.Data.Date ?? "");
        var time = Input("截止时间 HH:mm（可留空）", todo?.Data.Time ?? "");
        var completed = new CheckBox(this) { Text = "已完成", Checked = todo?.Data.Completed == true }; body.AddView(completed);
        var deleted = new CheckBox(this) { Text = "删除这条待办（保留历史）", Checked = todo?.Data.Deleted == true };
        if (todo is not null) body.AddView(deleted);
        if (versions is not null)
            foreach (var head in todo!.Heads)
            {
                var data = head.Body.Data;
                var versionText = Text($"{head.Body.DeviceName} · {head.Body.CreatedUtc}\n{(data.Deleted ? "[已删除] " : "")}{data.Title}\n{data.Date} {data.Time} · {(data.Completed ? "已完成" : "未完成")}\n{data.Notes}");
                versionText.SetTextIsSelectable(true); versions.AddView(versionText);
                versions.AddView(Button("以此版本为基础", () => { title.Text = data.Title; notes.Text = data.Notes; date.Text = data.Date; time.Text = data.Time; completed.Checked = data.Completed; deleted.Checked = data.Deleted; }));
            }
        body.AddView(Button(todo?.Conflict == true ? "确认最终版本并保存" : "保存到本机", () =>
        {
            static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            app.Store.Save(app.Identity.Id,app.Identity.Name,new TodoData(title.Text?.Trim() ?? "", notes.Text ?? "",Empty(date.Text),Empty(time.Text),completed.Checked,deleted.Checked),todo?.Id,todo?.VersionIds);
            Home();
        }));
        if (todo is not null) body.AddView(Button("查看完整修改历史", () => Navigate(() =>
        {
            Screen("修改历史");
            foreach (var r in app.Store.History(todo.Id).Reverse()) body.AddView(Text($"{r.Body.DeviceName} · {r.Body.CreatedUtc}\n{(r.Body.Data.Deleted ? "[删除] " : "")}{r.Body.Data.Title}\n{r.Body.Data.Date} {r.Body.Data.Time} · {(r.Body.Data.Completed ? "已完成" : "未完成")}\n{r.Body.Data.Notes}\n────────"));
        })));
    }

    private void Devices()
    {
        Screen("我的设备"); showingDevices = true; body.AddView(Text(app.Identity.Name,20));
        body.AddView(Text("只需首次连接一次，以后自动同步",20));
        body.AddView(Text("配对会长期保存在本机。重启或更换 IP 都无需重新配对；双方在同一可互通的局域网运行时，会自动连接。开启后台同步后不必保持界面打开。"));
        foreach (var device in app.Identity.Devices)
        {
            body.AddView(Text(device.Name + " · 已建立长期配对\n无需再输入配对码。",16));
            body.AddView(Button("取消与这台设备的配对", () => Confirm("取消配对？", "取消后才需要重新配对。对方已经收到的数据仍保留在对方设备。", "取消配对", () => { app.Identity.Revoke(device.Id); Devices(); })));
        }
        var pairing = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = app.Identity.Devices.Length == 0 ? ViewStates.Visible : ViewStates.Gone };
        body.AddView(Button("首次连接一台新设备", () => pairing.Visibility = pairing.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible));
        body.AddView(pairing);
        var outer = body; body = pairing;
        body.AddView(Text("仅添加新设备时使用：一台生成配对码，另一台粘贴并确认。5 分钟是一次性配对码的有效期，已经建立的配对不会过期。"));
        var code = new EditText(this) { TextSize = 12 }; code.SetTextIsSelectable(true); code.KeyListener = null; body.AddView(code);
        body.AddView(Button("生成首次配对码（给另一台设备）", () =>
        {
            code.Text = app.Identity.CreateInvite();
            var clipboard = (ClipboardManager)GetSystemService(ClipboardService)!;
            clipboard.PrimaryClip = ClipData.NewPlainText("LanTodo 配对码", code.Text);
            Toast.MakeText(this,"配对码已复制",ToastLength.Short)?.Show();
        }));
        body.AddView(Button("作废本机配对码", () => { app.Identity.CancelInvite(); code.Text = ""; }));
        var input = Input("粘贴另一台设备的完整配对码", multiline: true);
        var result = Text("");
        var pair = new Button(this) { Text = "确认连接，以后自动同步" }; body.AddView(pair); body.AddView(result);
        pair.Click += async (_, _) =>
        {
            pair.Enabled = false; result.Text = "正在寻找配对设备…";
            try { await app.Node.PairAsync(input.Text ?? "",activityToken.Token); if (!IsDestroyed) Devices(); }
            catch (Exception ex) { if (!IsDestroyed) result.Text = ex is System.OperationCanceledException ? "连接超时。请保持双方打开，检查 Wi-Fi、防火墙和配对码有效期。" : ex.Message; }
            finally { if (!IsDestroyed) pair.Enabled = true; }
        };
        body = outer;
        body.AddView(Text("开启后台同步后，锁屏和离开界面仍可同步。系统电池限制可能影响连接，可在设置中查看锁屏联网选项。",12));
        if (app.Node.LastError is not null) body.AddView(Text("连接诊断：" + app.Node.LastError,12));
    }

    private void Backups()
    {
        Screen("备份与恢复");
        body.AddView(Text("ZIP 备份包含 JSON 格式的全部清单历史（含删除和冲突），用于跨设备合并恢复。它不是数据库镜像，不含设备身份、配对和设置。文件未加密，请保存到独立存储位置。"));
        body.AddView(Button("导出清单与全部历史", () =>
        {
            var intent = new Intent(Intent.ActionCreateDocument); intent.AddCategory(Intent.CategoryOpenable); intent.SetType("application/zip"); intent.PutExtra(Intent.ExtraTitle,$"LanTodo-{DateTime.Now:yyyyMMdd-HHmmss}.lantodo.zip"); StartActivityForResult(intent,41);
        }));
        body.AddView(Button("从备份合并恢复", () => Confirm("合并恢复", "备份历史会合并到本机，可能产生需要处理的冲突。", "选择备份", () =>
        {
            var intent = new Intent(Intent.ActionOpenDocument); intent.AddCategory(Intent.CategoryOpenable); intent.SetType("application/zip"); StartActivityForResult(intent,42);
        })));
    }
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode,resultCode,data);
        if (resultCode != Result.Ok || data?.Data is null || requestCode is not (41 or 42)) return;
        var temp = Path.Combine(CacheDir!.AbsolutePath,Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            if (requestCode == 41)
            {
                app.Store.Backup(temp);
                using var output = ContentResolver!.OpenOutputStream(data.Data,"wt") ?? throw new IOException("无法打开目标文件。");
                using var file = File.OpenRead(temp); file.CopyTo(output); output.Flush();
                Error("备份已导出。");
            }
            else
            {
                using (var input = ContentResolver!.OpenInputStream(data.Data) ?? throw new IOException("无法读取备份。"))
                using (var output = File.Create(temp)) input.CopyTo(output);
                int count = app.Store.Restore(temp); Error($"已恢复 {count} 个版本。");
            }
        }
        catch (Exception ex) { Error(ex.Message); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
