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
        var attachments=todo?.Data.Attachments;
        Screen(todo?.Conflict == true ? "处理冲突" : todo is null ? "新建待办" : "编辑待办");BeginCard();
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
                var versionText = Text($"{app.DisplayName(head.Body.Actor,head.Body.DeviceName)} · {head.Body.CreatedUtc}\n{(data.Deleted ? "[已删除] " : "")}{data.Title}\n{data.Date} {data.Time} · {(data.Completed ? "已完成" : "未完成")}\n{data.Notes}");
                versionText.SetTextIsSelectable(true); versions.AddView(versionText);
                versions.AddView(Button("以此版本为基础", () => { attachments=data.Attachments; title.Text = data.Title; notes.Text = data.Notes; date.Text = data.Date; time.Text = data.Time; completed.Checked = data.Completed; deleted.Checked = data.Deleted; }));
            }
        body.AddView(Button(todo?.Conflict == true ? "确认最终版本并保存" : "保存到本机", () =>
        {
            static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            app.Store.Save(app.Identity.Id,app.Identity.Name,new TodoData(title.Text?.Trim() ?? "", notes.Text ?? "",Empty(date.Text),Empty(time.Text),completed.Checked,deleted.Checked,attachments),todo?.Id,todo?.VersionIds);
            Home();
        }));
        if (todo is not null) body.AddView(Button("查看完整修改历史", () => Navigate(() =>
        {
            Screen("修改历史");BeginCard();
            foreach (var r in app.Store.History(todo.Id).Reverse()) body.AddView(Text($"{app.DisplayName(r.Body.Actor,r.Body.DeviceName)} · {r.Body.CreatedUtc}\n{(r.Body.Data.Deleted ? "[删除] " : "")}{r.Body.Data.Title}\n{r.Body.Data.Date} {r.Body.Data.Time} · {(r.Body.Data.Completed ? "已完成" : "未完成")}\n{r.Body.Data.Notes}\n────────"));
        })));
    }

    private void Backups()
    {
        Screen("备份与恢复");BeginCard();
        body.AddView(Text("ZIP 备份仅包含全部清单历史、回收站和可用附件，并保留附件失效记录；附件未下载完整时请先同步。不含设备身份、空间成员和设置。备份未加密，请妥善保存。"));
        body.AddView(Button("导出清单与全部历史", () =>
        {
            var intent = new Intent(Intent.ActionCreateDocument); intent.AddCategory(Intent.CategoryOpenable); intent.SetType("application/zip"); intent.PutExtra(Intent.ExtraTitle,$"LanTodo-{DateTime.Now:yyyyMMdd-HHmmss}.lantodo.zip"); StartActivityForResult(intent,41);
        }));
        body.AddView(Button("从备份合并恢复", () => Confirm("合并恢复", "备份历史会合并到本机清单，可能产生需要处理的冲突。", "选择备份", () =>
        {
            var intent = new Intent(Intent.ActionOpenDocument); intent.AddCategory(Intent.CategoryOpenable); intent.SetType("application/zip"); StartActivityForResult(intent,42);
        })));
    }
    protected override async void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode,resultCode,data);
        if (requestCode == 72) { if (resultCode == Result.Ok && spaceCodeInput is not null) spaceCodeInput.Text = data?.GetStringExtra("code") ?? ""; return; }
        if (requestCode == 71) { if (resultCode == Result.Ok && data?.Data is not null) await ReadSpaceQr(data); return; }
        if(requestCode is 51 or 53){if(resultCode==Result.Ok && data is not null)_=HandleAttachmentResult(requestCode,data);return;}
        if (resultCode != Result.Ok || data?.Data is null || requestCode is not (41 or 42)) return;
        var temp = Path.Combine(CacheDir!.AbsolutePath,Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            Toast.MakeText(this,requestCode==41?"正在导出原件与历史…":"正在校验并恢复…",ToastLength.Short)?.Show();
            if (requestCode == 41)
            {
                await Task.Run(() =>
                {
                    app.Store.Backup(temp);
                    using var output = ContentResolver!.OpenOutputStream(data.Data,"wt") ?? throw new IOException("无法打开目标文件。");
                    using var file = File.OpenRead(temp); file.CopyTo(output); output.Flush();
                });
                Error("备份已导出。");
            }
            else
            {
                int count = await Task.Run(() =>
                {
                    using (var input = ContentResolver!.OpenInputStream(data.Data) ?? throw new IOException("无法读取备份。"))
                    using (var output = File.Create(temp)) input.CopyTo(output);
                    return app.Store.Restore(temp);
                });
                Error($"已恢复 {count} 个版本。");
            }
        }
        catch (Exception ex) { Error(ex.Message); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
