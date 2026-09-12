using LanTodo.Core;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LanTodo.Windows;

public partial class MainWindow
{
    private readonly DispatcherTimer draftTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private void SetupDraft()
    {
        if (app.Store.Drafts.Get("compose") is { } draft)
        { QuickInput.Text = draft.Data.Title; pendingAttachments.AddRange(draft.Data.Attachments ?? []); RenderPending(); }
        draftTimer.Tick += (_, _) => { draftTimer.Stop(); SaveComposeDraft(); };
        QuickInput.TextChanged += (_, _) => ScheduleDraft();
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); Refresh(); };
        Closing += (_, _) => SaveComposeDraft();
        Closed += (_, _) => { draftTimer.Stop(); searchTimer.Stop(); };
    }
    private void ScheduleDraft() { draftTimer.Stop(); draftTimer.Start(); }
    private void SaveComposeDraft()
    {
        try
        {
            app.Store.SaveDraft("compose", QuickInput.Text.Length == 0 && pendingAttachments.Count == 0 ? null :
                new LocalDraft(new(QuickInput.Text, Attachments: pendingAttachments.ToArray()), []));
        }
        catch (Exception ex) { StatusText.Text = "草稿暂未保存 · " + ex.Message; }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    { if (!ready) return; searchTimer.Stop(); searchTimer.Start(); }
}
