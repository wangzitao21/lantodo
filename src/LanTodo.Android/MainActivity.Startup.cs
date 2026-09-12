using Android.Content;
using Android.Views;
using LanTodo.Core;

namespace LanTodo.Android;

public partial class MainActivity
{
    private readonly List<View> startupControls = new();
    private bool composeTouched, sentDuringStartup, restoringCompose;
    private string? startupDraftSnapshot;
    private void EnableWhenReady(View view)
    {
        view.Enabled = app is not null;
        if (app is null) startupControls.Add(view);
    }
    private void ReadQuickDraft()
    {
        var preferences = GetSharedPreferences("preferences", FileCreationMode.Private)!;
        if (!preferences.Contains("shared-draft-v1"))
        {
            var drafts = (preferences.All ?? new Dictionary<string, object>()).Where(p => p.Key.StartsWith("quickDraft:")).Select(p => p.Value?.ToString())
                .Append(preferences.GetString("quickDraft", "")).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct();
            var text = string.Join("\n\n", drafts);
            if (text.Length > 0) preferences.Edit()!.PutString("quickDraft", text)!.Apply();
            preferences.Edit()!.PutBoolean("shared-draft-v1", true)!.Apply();
        }
        startupDraftSnapshot = preferences.GetString("quickDraft", null);
        quickDraft = startupDraftSnapshot ?? "";
    }
    private async Task InitializeRuntimeAsync(System.Diagnostics.Stopwatch clock)
    {
        try
        {
            var runtime = await AndroidSession.GetAsync(this);
            await runtime.LocalWrites.DrainAsync();
            if (IsDestroyed || IsFinishing) return;
            app = runtime;
            var saved = app.Store.Drafts.Get("compose");
            bool recover = saved is not null && (sentDuringStartup || composeTouched && startupDraftSnapshot is null || startupDraftSnapshot == "" && saved.Data.Title.Length > 0);
            if (recover) app.Store.Drafts.Preserve("compose-recovery", saved!);
            else if (saved is not null)
            {
                if (!composeTouched && startupDraftSnapshot is null) quickDraft = saved.Data.Title;
                pendingAttachments.AddRange(saved.Data.Attachments ?? []);
            }
            app.DataChanged += StoreChanged; app.StatusChanged += StatusChanged; app.MembershipChanged += TrustChanged;
            app.CanSwitchSpace = () => !attachmentBusy; app.ActiveSpaceChanged += SpaceSwitched;
            // Bind the existing page: never replace text/focus with a second Home().
            if (quickInput is not null && quickInput.Text != quickDraft)
            { restoringCompose = true; quickInput.Text = quickDraft; restoringCompose = false; }
            foreach (var control in startupControls) control.Enabled = true;
            startupControls.Clear();
            SaveComposeDraft();
            RenderPendingAttachments(saveDraft: false); StoreChanged(); StatusChanged();
            global::Android.Util.Log.Info("LanTodo", "DataReadyMs=" + clock.ElapsedMilliseconds);
            await HandleSharedIntent(Intent);
            await FlushStartupCapturesAsync();
        }
        catch (Exception ex)
        {
            if (!IsDestroyed) Error("本地清单暂时无法载入，新输入仍可保存。请保留应用数据。\n" + ex.Message);
        }
    }
    private void RenderStartupCaptures()
    {
        homeList.RemoveAllViews();
        try
        {
            var pending = AndroidSession.Captures(this).Pending;
            countLabel.Text = pending.Length == 0 ? "" : $"已记下 {pending.Length} 条";
            foreach (var capture in pending.Reverse()) homeList.AddView(Text(capture.Text, 16, true));
            if (pending.Length > 0) status.Text = "已保存到本机 · 正在载入清单";
        }
        catch (Exception ex) { status.Text = "待保存的想法暂时无法读取 · " + ex.Message; }
    }
    private async Task FlushStartupCapturesAsync()
    {
        try { await AndroidSession.FlushCapturesAsync(this); if (!IsDestroyed && app is not null) StoreChanged(); }
        catch (Exception ex) { if (!IsDestroyed) Error("想法已保留在本机，暂未写入清单。\n" + ex.Message); }
    }
    private void AddRecoveredDraft()
    {
        var entry = app.Store.Drafts.WithPrefix("compose-recovery").FirstOrDefault();
        if (entry.Value is not { } recovered) return;
        homeList.AddView(Button("恢复上次的草稿", () =>
        {
            app.LocalWrites.DrainAsync().GetAwaiter().GetResult();
            var current = quickDraft.Length == 0 && pendingAttachments.Count == 0 ? null : new LocalDraft(new(quickDraft, Attachments: pendingAttachments.ToArray()), []);
            app.Store.SaveDraft("compose", current);
            app.Store.Drafts.Swap("compose", entry.Key);
            quickDraft = recovered.Data.Title; pendingAttachments.Clear(); pendingAttachments.AddRange(recovered.Data.Attachments ?? []);
            restoringCompose = true; quickInput!.Text = quickDraft; restoringCompose = false;
            SaveComposeDraft(); RenderPendingAttachments(); renderedState = null; RenderTodos();
        }));
    }
    private void AddPendingCaptures()
    {
        foreach (var capture in AndroidSession.Captures(this).Pending.Reverse())
            homeList.AddView(Text(capture.Text + "\n已记下 · 等待写入清单", 14));
    }
}
