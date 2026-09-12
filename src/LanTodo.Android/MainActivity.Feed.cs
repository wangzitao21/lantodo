using Android.Graphics;
using Android.Views;
using Android.Widget;
using LanTodo.Core;
using Color = Android.Graphics.Color;

namespace LanTodo.Android;

public partial class MainActivity
{
    private sealed class FeedAdapter(MainActivity owner) : BaseAdapter<TodoView>
    {
        private TodoView[] items = [];
        private readonly Dictionary<string, long> ids = new();
        private long nextId;
        public override int Count => items.Length;
        public override TodoView this[int position] => items[position];
        public override bool HasStableIds => true;
        public override long GetItemId(int position) => ids[items[position].Id];
        public void Update(TodoView[] next)
        {
            foreach (var item in next) if (!ids.ContainsKey(item.Id)) ids[item.Id] = ++nextId;
            items = next;
            NotifyDataSetChanged();
        }
        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var card = convertView as MessageCard ?? new MessageCard(owner);
            card.Bind(items[position]);
            return card;
        }
    }

    // Handlers refer to the current binding, never to the record that originally
    // created this recycled view. Unchanged attachments keep their image views.
    private sealed class MessageCard : LinearLayout
    {
        private readonly MainActivity owner;
        private readonly LinearLayout row, attachmentArea;
        private readonly TextView check, title, when, notes, source, star;
        private readonly SwipeMessage swipe;
        private TodoView current = null!;
        private string? stamp, attachmentStamp;

        public MessageCard(MainActivity owner) : base(owner)
        {
            this.owner = owner; Orientation = Orientation.Vertical;
            row = new LinearLayout(owner) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetPadding(owner.Dp(8), owner.Dp(11), owner.Dp(14), owner.Dp(11));
            swipe = new SwipeMessage(owner, row, () => owner.DeleteTodo(current));
            var layout = new LinearLayout.LayoutParams(-1, -2); layout.SetMargins(0, 0, 0, owner.Dp(8));
            AddView(swipe, layout);
            row.LongClick += (_, args) => { args.Handled = true; Actions(); };
            check = owner.Text("", 26); check.Gravity = GravityFlags.Center;
            check.Click += (_, _) => owner.SaveTodo(current, current.Data with { Completed = !current.Data.Completed });
            check.LongClick += (_, args) => { args.Handled = true; Actions(); };
            row.AddView(check, new LinearLayout.LayoutParams(owner.Dp(44), owner.Dp(48)));
            var content = new LinearLayout(owner) { Orientation = Orientation.Vertical };
            title = owner.Text("", 16, true); content.AddView(title);
            when = owner.Text("", 12); when.SetTextColor(Green); content.AddView(when);
            notes = owner.Text("", 12); notes.SetTextColor(Muted); notes.SetMaxLines(1); content.AddView(notes);
            attachmentArea = new LinearLayout(owner) { Orientation = Orientation.Vertical }; content.AddView(attachmentArea);
            source = owner.Text("", 10); source.SetTextColor(Muted); source.SetSingleLine(true);
            source.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
            source.LongClick += (_, args) => { args.Handled = true; Actions(); }; content.AddView(source);
            content.LongClick += (_, args) => { args.Handled = true; Actions(); };
            content.Click += (_, _) => { if (current.Data.Deleted) Actions(); else owner.Navigate(() => owner.Editor(current)); };
            row.AddView(content, new LinearLayout.LayoutParams(0, -2, 1));
            star = owner.Text("", 26); star.Gravity = GravityFlags.Center;
            star.Click += (_, _) => owner.SaveTodo(current, current.Data with { Starred = !current.Data.Starred });
            row.AddView(star, new LinearLayout.LayoutParams(owner.Dp(44), owner.Dp(48)));
        }
        private void Actions() => owner.MessageActions(current);
        public void Bind(TodoView todo)
        {
            bool trash = owner.filter == "trash", busy = owner.pendingChanges.Contains(todo.Id);
            var name = owner.app.ShowDeviceSource ? owner.app.DisplayName(todo.Heads[0].Body.Actor, todo.Heads[0].Body.DeviceName) : "";
            var attachments = string.Join("\n", (todo.Data.Attachments ?? []).Select(a => a.Key + "|" + a.Name + "|" + a.Kind + "|" + owner.app.Store.Attachments.Availability(a)));
            var nextStamp = todo.Id + "|" + string.Join(";", todo.VersionIds) + "|" + trash + "|" + busy + "|" + owner.app.ShowDeviceSource + "|" + name + "|" + attachments;
            current = todo;
            if (nextStamp == stamp) return;
            stamp = nextStamp;
            swipe.Reset(); swipe.CanSwipe = !trash && !busy;
            row.Background = owner.Surface(Color.ParseColor(MessageStyle.Background(todo.Data.Color)), 14, true);
            check.Visibility = star.Visibility = trash ? ViewStates.Gone : ViewStates.Visible;
            check.Enabled = star.Enabled = !busy;
            check.Text = todo.Data.Completed ? "✓" : "○";
            check.SetTextColor(todo.Data.Completed ? Green : Color.Rgb(182, 196, 205));
            check.ContentDescription = todo.Data.Completed ? "标为未完成" : "完成待办";
            title.Text = todo.Data.Title; title.SetTextColor(todo.Data.Completed ? Muted : Ink);
            title.PaintFlags = todo.Data.Completed ? title.PaintFlags | PaintFlags.StrikeThruText : title.PaintFlags & ~PaintFlags.StrikeThruText;
            when.Visibility = todo.Data.Date is null ? ViewStates.Gone : ViewStates.Visible;
            when.Text = todo.Data.Date + "  " + todo.Data.Time;
            notes.Visibility = todo.Data.Notes.Length == 0 ? ViewStates.Gone : ViewStates.Visible;
            notes.Text = todo.Data.Notes;
            notes.PaintFlags = todo.Data.Completed ? notes.PaintFlags | PaintFlags.StrikeThruText : notes.PaintFlags & ~PaintFlags.StrikeThruText;
            source.Visibility = owner.app.ShowDeviceSource ? ViewStates.Visible : ViewStates.Gone;
            source.Text = "来自 " + name;
            star.Text = todo.Data.Starred ? "★" : "☆";
            star.SetTextColor(todo.Data.Starred ? Color.Rgb(216, 164, 35) : Muted);
            star.ContentDescription = todo.Data.Starred ? "取消置顶" : "星标置顶";
            if (attachments != attachmentStamp)
            {
                attachmentStamp = attachments; attachmentArea.RemoveAllViews();
                foreach (var attachment in todo.Data.Attachments ?? []) owner.AddAttachmentView(attachmentArea, attachment, Actions);
            }
        }
    }
}
