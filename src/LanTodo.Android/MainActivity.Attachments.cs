using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using LanTodo.Core;

namespace LanTodo.Android;

public partial class MainActivity
{
    private readonly List<Attachment> pendingAttachments = new();
    private LinearLayout? pendingPanel;
    private ScrollView? pendingScroll;
    private Action? updateSend;
    private bool attachmentBusy;
    private Attachment? exportingAttachment;
    protected override async void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent); Intent=intent;
        if(app is not null)await HandleSharedIntent(intent);
    }
    private async Task HandleSharedIntent(Intent? intent)
    {
        if(intent?.Action is not (Intent.ActionSend or Intent.ActionSendMultiple))return;
        if(attachmentBusy){Error("附件仍在读取，请稍后重新分享。");return;}
        var shared=new Intent();var uris=new List<global::Android.Net.Uri>();
#pragma warning disable CA1422
        if(intent.Action==Intent.ActionSend && intent.GetParcelableExtra(Intent.ExtraStream) is global::Android.Net.Uri single)uris.Add(single);
        if(intent.Action==Intent.ActionSendMultiple && intent.GetParcelableArrayListExtra(Intent.ExtraStream) is {} multiple)
            foreach(var entry in multiple)if(entry is global::Android.Net.Uri uri)uris.Add(uri);
#pragma warning restore CA1422
        // EXTRA_STREAM is the sender's attachment list. ClipData often contains a
        // second, exported copy of the same photo; it is a fallback, not another list.
        if(uris.Count == 0 && intent.ClipData is {} clip)
            for(int i=0;i<clip.ItemCount;i++)if(clip.GetItemAt(i)?.Uri is {} uri)uris.Add(uri);
        var text=intent.GetStringExtra(Intent.ExtraText);
        intent.SetAction(Intent.ActionMain);intent.RemoveExtra(Intent.ExtraStream);intent.RemoveExtra(Intent.ExtraText);intent.ClipData=null;
        if(!string.IsNullOrWhiteSpace(text))quickDraft=(quickDraft.Length==0?text:quickDraft+"\n"+text);
        if (showingHome && quickInput is not null) quickInput.Text = quickDraft; else Home();
        var unique=uris.DistinctBy(uri=>uri.ToString()).ToArray();
        if(unique.Length==0)return;
        var data=ClipData.NewRawUri("分享的附件",unique[0])!;foreach(var uri in unique.Skip(1))data.AddItem(new ClipData.Item(uri));
        shared.ClipData=data;await HandleAttachmentResult(51,shared);
    }
    private void PickAttachment()
    {
        new AlertDialog.Builder(this).SetTitle("添加附件 · 保留原件")!
            .SetItems(new[]{"图片", "文件"}, (_,args)=>
            {
                OpenAttachmentPicker(args.Which==0?"image/*":"*/*");
            })!.Show();
    }
    private void OpenAttachmentPicker(string mime)
    {
        if(attachmentBusy)return;
        if(attachmentTray is not null)attachmentTray.Visibility=ViewStates.Gone;
        var picker=new Intent(Intent.ActionOpenDocument);picker.AddCategory(Intent.CategoryOpenable);
        picker.SetType(mime);picker.PutExtra(Intent.ExtraAllowMultiple,true);StartActivityForResult(picker,51);
    }
    private async Task HandleAttachmentResult(int code,Intent result)
    {
        if(attachmentBusy)return;
        attachmentBusy=true;updateSend?.Invoke();
        try
        {
            if(code==53)
            {
                if(exportingAttachment is not {} item || result.Data is null)return;
                await Task.Run(()=>{using var output=ContentResolver!.OpenOutputStream(result.Data,"wt")??throw new IOException("无法保存文件。");using var input=File.OpenRead(app.Store.Attachments.PathFor(item.Hash));input.CopyTo(output);});
                Toast.MakeText(this,"原件已保存",ToastLength.Short)?.Show();return;
            }
            var uris=new List<global::Android.Net.Uri>();
            if(result.ClipData is {} clip){for(int i=0;i<clip.ItemCount;i++)if(clip.GetItemAt(i)?.Uri is {} uri)uris.Add(uri);}
            else if(result.Data is {} uri)uris.Add(uri);
            uris = uris.DistinctBy(uri => uri.ToString()).ToList();
            if(pendingAttachments.Count+uris.Count>32)throw new IOException("每条消息最多 32 个附件。");
            Toast.MakeText(this,"正在读取附件原件…",ToastLength.Short)?.Show();
            foreach(var uri in uris)
            {
                var item=await Task.Run(()=>
                {
                    string name="附件";
                    using var cursor=ContentResolver!.Query(uri,new[]{global::Android.Provider.IOpenableColumns.DisplayName},null,null,null);
                    if(cursor?.MoveToFirst()==true)name=cursor.GetString(0)??name;
                    using var input=ContentResolver.OpenInputStream(uri)??throw new IOException("无法读取文件。");
                    return app.Store.Attachments.Add(input,name,ContentResolver.GetType(uri)?.StartsWith("image/")==true?"image":null);
                });
                if (!pendingAttachments.Any(a => a.Hash == item.Hash)) pendingAttachments.Add(item);
                if (!IsDestroyed) RenderPendingAttachments();
            }
        }
        catch(Exception ex){if(!IsDestroyed)Error(ex.Message);}
        finally{attachmentBusy=false;if(!IsDestroyed)RenderPendingAttachments();}
    }
    private void RenderPendingAttachments()
    {
        if(pendingScroll is not null)pendingScroll.Visibility=pendingAttachments.Count==0?ViewStates.Gone:ViewStates.Visible;
        pendingPanel?.RemoveAllViews();
        if(pendingPanel is not null)
        {
            foreach(var item in pendingAttachments.ToArray())
            {
                var chip=Button(item.Name+"  ×",()=>{pendingAttachments.Remove(item);RenderPendingAttachments();});chip.TextSize=12;pendingPanel.AddView(chip);
            }
            if(pendingAttachments.Count>0)pendingPanel.AddView(Button("＋ 继续添加",PickAttachment));
        }
        updateSend?.Invoke();
    }
    private void AddAttachmentView(LinearLayout container,Attachment item,Action actions)
    {
        var available=app.Store.Attachments.Has(item);
        if(available && item.Kind=="image")
        {
            var image=new ImageView(this);image.SetScaleType(ImageView.ScaleType.FitCenter);image.ContentDescription=item.Name;
            image.Click+=(_,_)=>Navigate(()=>PreviewImage(item));image.LongClick+=(_,args)=>{args.Handled=true;actions();};
            container.AddView(image,new LinearLayout.LayoutParams(-1,Dp(150)));_ = LoadThumbnail(image,item);
        }
        var file=Button(item.Description+(available?" · 保存原件":" · "+app.Store.Attachments.Availability(item)),()=>SaveAttachment(item));file.TextSize=12;file.Enabled=available;file.LongClick+=(_,args)=>{args.Handled=true;actions();};container.AddView(file);
    }
    private readonly Dictionary<string, Bitmap> thumbnailCache = new();
    private readonly SemaphoreSlim thumbnailSlots = new(2);
    private async Task LoadThumbnail(ImageView image, Attachment item)
    {
        if (thumbnailCache.TryGetValue(item.Hash, out var cached)) { image.SetImageBitmap(cached); return; }
        await thumbnailSlots.WaitAsync();
        try
        {
            if (IsDestroyed) return;
            if (!thumbnailCache.TryGetValue(item.Hash, out cached))
            {
                var path = app.Store.Attachments.PathFor(item.Hash);
                cached = await Task.Run(() =>
                {
                    using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true }; BitmapFactory.DecodeFile(path,bounds);
                    int sample=1;while(Math.Max(bounds.OutWidth,bounds.OutHeight)/sample>480)sample*=2;
                    using var options = new BitmapFactory.Options { InSampleSize = sample };
                    return BitmapFactory.DecodeFile(path,options);
                });
                if (cached is null) return;
                if (IsDestroyed) { cached.Dispose(); return; }
                if (thumbnailCache.Count >= 64) { foreach(var old in thumbnailCache.Values) old.Dispose(); thumbnailCache.Clear(); }
                thumbnailCache[item.Hash] = cached;
            }
            image.SetImageBitmap(cached);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { thumbnailSlots.Release(); }
    }
    private void PreviewImage(Attachment item)
    {
        Screen(item.Name);
        try
        {
            var path=app.Store.Attachments.PathFor(item.Hash);
            using var bounds=new BitmapFactory.Options{InJustDecodeBounds=true};BitmapFactory.DecodeFile(path,bounds);
            int sample=1;while(Math.Max(bounds.OutWidth,bounds.OutHeight)/sample>2048)sample*=2;
            using var options=new BitmapFactory.Options{InSampleSize=sample};using var bitmap=BitmapFactory.DecodeFile(path,options);
            if(bitmap is not null){var image=new ImageView(this);image.SetImageBitmap(bitmap);image.SetAdjustViewBounds(true);body.AddView(image,new LinearLayout.LayoutParams(-1,-2));}
            body.AddView(Button("保存原件",()=>SaveAttachment(item)));
        }
        catch(Exception ex){Error(ex.Message);}
    }
    private void SaveAttachment(Attachment item)
    {
        exportingAttachment=item;
        var picker=new Intent(Intent.ActionCreateDocument);picker.AddCategory(Intent.CategoryOpenable);picker.SetType(item.Kind=="folder"?"application/zip":"application/octet-stream");picker.PutExtra(Intent.ExtraTitle,item.Name);StartActivityForResult(picker,53);
    }
    private void DeleteTodo(TodoView todo)
    {
        try{app.Store.Save(app.Identity.Id,app.Identity.Name,todo.Data with{Deleted=true},todo.Id,todo.VersionIds);}
        catch(Exception ex){Error(ex.Message);}
    }
    private void MessageActions(TodoView todo)
    {
        if(todo.Data.Deleted)
        {
            new AlertDialog.Builder(this).SetTitle(todo.Data.Title)!.SetItems(new[]{"恢复","彻底清除"},(_,args)=>
            {
                try { if(args.Which==0)app.Store.Save(app.Identity.Id,app.Identity.Name,todo.Data with{Deleted=false},todo.Id,todo.VersionIds);
                else Confirm("彻底清除","此操作会同步到所有已连接设备。","清除",()=>app.Store.Purge(app.Identity.Id,app.Identity.Name,todo)); }
                catch(Exception ex){Error(ex.Message);}
            })!.Show(); return;
        }
        new AlertDialog.Builder(this).SetTitle(todo.Data.Title)!.SetItems(new[]{"查看 / 编辑",todo.Data.Completed?"标为未完成":"完成","复制文字","删除"},(_,args)=>
        {
            try
            {
                switch(args.Which)
                {
                    case 0:Navigate(()=>Editor(todo));break;
                    case 1:app.Store.Save(app.Identity.Id,app.Identity.Name,todo.Data with{Completed=!todo.Data.Completed},todo.Id,todo.VersionIds);break;
                    case 2:((ClipboardManager)GetSystemService(ClipboardService)!).PrimaryClip=ClipData.NewPlainText("LanTodo",todo.Data.Title+(todo.Data.Notes.Length>0?"\n"+todo.Data.Notes:""));break;
                    case 3:DeleteTodo(todo);break;
                }
            }
            catch(Exception ex){Error(ex.Message);}
        })!.Show();
    }
    private sealed class SwipeMessage : FrameLayout
    {
        private readonly View card;
        private readonly int reveal,slop;
        private readonly Action delete;
        private readonly ImageButton trash;
        private float startX,startY,initial;
        private bool dragging,vertical;
        public SwipeMessage(MainActivity owner,View card,Action delete):base(owner)
        {
            this.card=card;this.delete=delete;reveal=owner.Dp(76);slop=ViewConfiguration.Get(owner)!.ScaledTouchSlop;
            SetClipChildren(true);
            Background=owner.Surface(Color.Rgb(213,62,75),16);
            Background.Alpha=0;
            trash=new ImageButton(owner){ContentDescription="删除消息",Background=null,Visibility=ViewStates.Invisible};trash.SetImageResource(Resource.Drawable.ic_delete);trash.SetPadding(owner.Dp(22),owner.Dp(16),owner.Dp(22),owner.Dp(16));trash.Click+=(_,_)=>delete();
            AddView(trash,new FrameLayout.LayoutParams(reveal,-1,GravityFlags.Right));AddView(card,new FrameLayout.LayoutParams(-1,-2));
        }
        public override bool OnInterceptTouchEvent(MotionEvent? e)
        {
            if(e is null)return false;
            switch(e.ActionMasked)
            {
                case MotionEventActions.Down:card.Animate()?.Cancel();startX=e.GetX();startY=e.GetY();initial=card.TranslationX;dragging=false;vertical=false;break;
                case MotionEventActions.Move:
                    float dx=e.GetX()-startX,dy=e.GetY()-startY;
                    if(!vertical && Math.Abs(dy)>slop && Math.Abs(dy)>Math.Abs(dx))vertical=true;
                    if(!vertical && Math.Abs(dx)>slop && Math.Abs(dx)>Math.Abs(dy)*1.4 && (dx<0 || initial<0)){dragging=true;Parent?.RequestDisallowInterceptTouchEvent(true);return true;}break;
            }
            return false;
        }
        public override bool OnTouchEvent(MotionEvent? e)
        {
            if(e is null || !dragging)return base.OnTouchEvent(e);
            if(e.ActionMasked==MotionEventActions.Move)
            {
                card.TranslationX=Math.Clamp(initial+e.GetX()-startX,-Width,0);
                Background!.Alpha=card.TranslationX<0?255:0;trash.Visibility=card.TranslationX<0?ViewStates.Visible:ViewStates.Invisible;
            }
            if(e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
            {
                var remove=e.ActionMasked==MotionEventActions.Up && -card.TranslationX>=Width*.5f;
                var target=remove?-Width:0;
                var animation=card.Animate()!.TranslationX(target)!.SetDuration(160)!;
                animation.WithEndAction(new Java.Lang.Runnable(()=>
                {
                    if(remove){delete();card.TranslationX=0;}
                    if(card.TranslationX==0){Background!.Alpha=0;trash.Visibility=ViewStates.Invisible;}
                }));
                animation.Start();dragging=false;Parent?.RequestDisallowInterceptTouchEvent(false);
            }
            return true;
        }
    }
}
