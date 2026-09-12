using Android.Content;
using Android.OS;
using LanTodo.Core;

namespace LanTodo.Android;

public partial class MainActivity
{
    private readonly Handler draftHandler = new(Looper.MainLooper!);
    private int composeVersion, editorVersion;
    private Action<bool>? editorDraftSave;
    private readonly Dictionary<string, int> draftWrites = new();
    private void ScheduleComposeDraft()
    {
        int version = ++composeVersion;
        draftHandler.PostDelayed(() => { if (version == composeVersion && !IsDestroyed) SaveComposeDraft(background: true); }, 500);
    }
    private void SaveComposeDraft(bool background = false)
    {
        GetSharedPreferences("preferences", FileCreationMode.Private)!.Edit()!.PutString("quickDraft", quickDraft)!.Apply();
        if (app is null) return;
        try
        {
            PersistDraft("compose", quickDraft.Length == 0 && pendingAttachments.Count == 0 ? null :
                new LocalDraft(new(quickDraft, Attachments: pendingAttachments.ToArray()), []), background);
            // Keep a small text mirror so the composer can be restored before SQLite opens.
        }
        catch (Exception ex) { if (status is not null) status.Text = "草稿暂未保存 · " + ex.Message; }
    }
    private void ScheduleEditorDraft()
    {
        int version = ++editorVersion;
        draftHandler.PostDelayed(() => { if (version == editorVersion && !IsDestroyed) SaveEditorDraft(background: true); }, 500);
    }
    private void SaveEditorDraft(bool background = false)
    {
        try { editorDraftSave?.Invoke(background); }
        catch (Exception ex) { if (status is not null) status.Text = "草稿暂未保存 · " + ex.Message; }
    }
    private void PersistDraft(string key, LocalDraft? snapshot, bool background)
    {
        int version = draftWrites.GetValueOrDefault(key) + 1; draftWrites[key] = version;
        if (background) { _ = PersistDraftAsync(key, snapshot, version); return; }
        // Lifecycle/send boundaries still flush before returning, so an older
        // background draft cannot overwrite a sent or newly restored draft.
        app.LocalWrites.DrainAsync().GetAwaiter().GetResult();
        app.Store.SaveDraft(key, snapshot);
    }
    private async Task PersistDraftAsync(string key, LocalDraft? snapshot, int version)
    {
        try { await app.LocalWrites.Enqueue(() => app.Store.SaveDraft(key, snapshot)); }
        catch (Exception ex)
        {
            if (!IsDestroyed && draftWrites.GetValueOrDefault(key) == version && status is not null)
                status.Text = "草稿暂未保存 · " + ex.Message;
        }
    }
}
