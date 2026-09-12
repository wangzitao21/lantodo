using Android.App;
using Android.Content;
using Android.OS;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Android.Window;
using LanTodo.Core;
using Color = Android.Graphics.Color;

namespace LanTodo.Android;

[Activity(Label = "LanTodo", MainLauncher = true, Exported = true, LaunchMode = global::Android.Content.PM.LaunchMode.SingleTop,
    Theme = "@style/LanTodoLaunchTheme",
    WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation | global::Android.Content.PM.ConfigChanges.ScreenSize)]
[IntentFilter(new[] { Intent.ActionSend, Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
public partial class MainActivity : Activity
{
    private AppRuntime app = null!;
    private LinearLayout body = null!, root = null!, homeList = null!;
    private TextView status = null!, countLabel = null!;
    private EditText? quickInput;
    private LinearLayout? attachmentTray;
    private string quickDraft = "", filter = "all";
    private string? selectedDate;
    private bool showingHome, showingDevices;
    private string? renderedState;
    private ScrollView pageScroll = null!;
    private readonly Dictionary<string,(int Position,int Top)> listOffsets = new();
    private ListView? homeFeed;
    private FeedAdapter? feedAdapter;
    private string searchQuery = "";
    private void RememberPosition() { if(homeFeed is not null) listOffsets[filter]=(homeFeed.FirstVisiblePosition,homeFeed.GetChildAt(0)?.Top ?? 0); }
    private readonly Stack<PageState> pages = new();
    private readonly CancellationTokenSource activityToken = new();
    private BackHandler? backHandler;
    private SplashExitHandler? splashExit;
    private static readonly Color Ink = Color.Rgb(31,41,55), Green = Color.Rgb(22,125,141), Paper = Color.Rgb(245,247,250), Muted = Color.Rgb(123,135,149);
    private sealed record PageState(LinearLayout Root, LinearLayout Body, TextView Status, bool Home, bool Devices, ScrollView Scroll, LinearLayout? PageBody, int Offset, Action<bool>? DraftSave);

    protected override void OnCreate(Bundle? state)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        SetTheme(Resource.Style.LanTodoTheme);
        base.OnCreate(state);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            splashExit = new SplashExitHandler(view => { if (OperatingSystem.IsAndroidVersionAtLeast(31)) view.Remove(); });
            SplashScreen!.SetOnExitAnimationListener(splashExit);
        }
        ReadQuickDraft();
        Home();
        root.Post(() => { if (!IsDestroyed) { ReportFullyDrawn(); global::Android.Util.Log.Info("LanTodo", "ComposerReadyMs=" + clock.ElapsedMilliseconds); } });
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            backHandler = new BackHandler(()=>GoBack(true));
            OnBackInvokedDispatcher!.RegisterOnBackInvokedCallback(0, backHandler);
        }
        _ = InitializeRuntimeAsync(clock);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private sealed class SplashExitHandler(Action<SplashScreenView> action) : Java.Lang.Object, ISplashScreenOnExitAnimationListener
    { public void OnSplashScreenExit(SplashScreenView view) => action(view); }
    protected override async void OnStart()
    {
        base.OnStart();
        AndroidSession.ActivityVisible = true;
        if (app is not null && showingHome) StoreChanged();
        await StartNetworkAsync();
    }
    private async Task StartNetworkAsync()
    {
        // A notification/service error must not prevent foreground networking.
        try { if (AndroidSession.BackgroundEnabled(this)) AndroidSession.StartBackground(this); }
        catch (Exception ex) { global::Android.Util.Log.Warn("LanTodo", "Background service: " + ex.GetType().Name); }
        if (app is null) return;
        try
        {
            await AndroidSession.RefreshNetworkAsync();
            app.RequestReconnect();
        }
        catch (Exception ex) { Error("后台连接未启动，本机仍可使用。\n" + ex.Message); }
    }
    protected override async void OnStop()
    {
        CancelRecording();StopAudio();
        base.OnStop();
        AndroidSession.ActivityVisible = false;
        SaveComposeDraft(); SaveEditorDraft();
        GetSharedPreferences("preferences",FileCreationMode.Private)!.Edit()!.PutString("quickDraft",quickDraft)!.Commit();
        if (app is null) return;
        try { await AndroidSession.RefreshNetworkAsync(); }
        catch (Exception ex) { global::Android.Util.Log.Warn("LanTodo", ex.GetType().Name); }
    }
    protected override void OnDestroy()
    {
        CancelRecording();StopAudio();
        draftHandler.RemoveCallbacksAndMessages(null);
        activityToken.Cancel();
        foreach(var bitmap in thumbnailCache.Values) bitmap.Dispose(); thumbnailCache.Clear();
        if (app is not null) { app.CanSwitchSpace=null;app.ActiveSpaceChanged-=SpaceSwitched;app.DataChanged -= StoreChanged; app.StatusChanged -= StatusChanged; app.MembershipChanged -= TrustChanged; app.Identity.CancelInvite(); }
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && backHandler is not null) OnBackInvokedDispatcher!.UnregisterOnBackInvokedCallback(backHandler);
        base.OnDestroy();
    }
#pragma warning disable CS0672, CA1422
    public override void OnBackPressed() => GoBack(true);
#pragma warning restore CS0672, CA1422
    private sealed class BackHandler(Action action) : Java.Lang.Object, IOnBackInvokedCallback
    { public void OnBackInvoked() => action(); }
    private void Navigate(Action render)
    { SaveEditorDraft(); pages.Push(new(root,body,status,showingHome,showingDevices,pageScroll,pageBody,pageScroll.ScrollY,editorDraftSave)); StopAudio(); render(); }
    private void GoBack(bool toHome = false)
    {
        SaveEditorDraft();
        StopAudio();
        if (app is null) { MoveTaskToBack(true); return; }
        if (showingHome && attachmentTray?.Visibility == ViewStates.Visible) { attachmentTray.Visibility = ViewStates.Gone; return; }
        if(toHome) while(pages.Count>0 && !pages.Peek().Home) pages.Pop();
        if (pages.TryPop(out var page))
        {
            root=page.Root; body=page.Body; status=page.Status; showingHome=page.Home; showingDevices=page.Devices;
            pageScroll=page.Scroll;pageBody=page.PageBody;editorDraftSave=page.DraftSave;
            SetContentView(root);root.RequestApplyInsets();
            if(showingHome)
            {
                if(toHome && filter!="all") { RememberPosition();filter="all";selectedDate=null;Home();return; }
                RenderTodos();RenderPendingAttachments();
            }
            if(showingDevices)RefreshDevices();
            if(!showingHome) { var restoredScroll=pageScroll;restoredScroll.Post(()=>restoredScroll.ScrollTo(0,page.Offset)); }
            StatusChanged();
        }
        else if (!showingHome || filter!="all") { if(showingHome)RememberPosition();filter="all";selectedDate=null;Home(); }
        else MoveTaskToBack(true);
    }
    private void SpaceSwitched() => RunOnUiThread(()=>
    {
        if(!IsDestroyed) { StatusChanged();if(showingDevices)RefreshDevices(); }
    });
    private int renderQueued;
    private long contentVersion;
    private void StoreChanged()
    {
        Interlocked.Increment(ref contentVersion);
        if (Interlocked.Exchange(ref renderQueued, 1) != 0) return;
        RunOnUiThread(() => root.PostDelayed(() => { Interlocked.Exchange(ref renderQueued, 0); if (showingHome && !IsDestroyed) RenderTodos(); }, 16));
    }
    private void SetPresence(TextView label,bool online,string text)
    {
        var value=new global::Android.Text.SpannableString("●  "+text);
        var color=online?Color.Rgb(46,155,99):Muted;
        value.SetSpan(new global::Android.Text.Style.ForegroundColorSpan(color),0,1,global::Android.Text.SpanTypes.ExclusiveExclusive);
        label.TextFormatted=value;
    }
    private void StatusChanged() => RunOnUiThread(() =>
    {
        if (IsDestroyed || status is null || app is null) return;
        SetPresence(status,app.IsOnline,app.Status);
        if(showingDevices)foreach(var pair in spaceStatusLabels)
        {
            bool online=pair.Key==app.Identity.Id?app.IsOnline:app.Node.IsDeviceOnline(pair.Key);
            SetPresence(pair.Value,online,online?"在线":"当前不在线");
            pair.Value.SetShadowLayer(online?Dp(3):0,0,0,Color.Argb(70,46,155,99));
        }
    });
    private void TrustChanged() => RunOnUiThread(() => { if (!IsDestroyed) { if (showingDevices) RefreshDevices(); else if (showingHome) StoreChanged(); } });
    private int Dp(int n) => (int)(n * Resources!.DisplayMetrics!.Density);
    private GradientDrawable Surface(Color color, int radius = 14, bool border = false)
    {
        var drawable = new GradientDrawable(); drawable.SetColor(color); drawable.SetCornerRadius(Dp(radius));
        if (border) drawable.SetStroke(Dp(1),Color.Rgb(225,231,237));
        return drawable;
    }
    private void Screen(string title, bool home = false)
    {
        showingHome = home; showingDevices = false; editorDraftSave = null;
        root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Paper); root.SetPadding(Dp(20),Dp(24),Dp(20),Dp(20));
        root.SetOnApplyWindowInsetsListener(new Insets(this));
        var heading = new LinearLayout(this) { Orientation = Orientation.Horizontal, BaselineAligned = false }; heading.SetGravity(GravityFlags.CenterVertical);
        if (!home)
        {
            var back=Button("‹",()=>GoBack()); back.TextSize=30;back.SetPadding(0,0,0,0);back.SetMinWidth(0);back.SetMinimumWidth(0);back.SetIncludeFontPadding(false);back.Gravity=GravityFlags.Center; back.ContentDescription="返回上一级";
            var backParams=new LinearLayout.LayoutParams(Dp(40),Dp(44));backParams.SetMargins(0,0,Dp(12),0);heading.AddView(back,backParams);
        }
        var titleLabel=Text(title,home?26:24,true);
        if(home){titleLabel.SetSingleLine(true);titleLabel.SetAutoSizeTextTypeUniformWithConfiguration(18,26,1,(int)global::Android.Util.ComplexUnitType.Sp);}
        heading.AddView(titleLabel,new LinearLayout.LayoutParams(0,-2,1));
        if (home)
        {
            var dateButton=Button(selectedDate is null?$"{DateTime.Today:MM-dd}":DateTime.Parse(selectedDate).ToString("MM-dd"),SelectDate);
            dateButton.ContentDescription=selectedDate is null?"筛选日期，当前显示全部日期":"筛选日期："+selectedDate;
            dateButton.SetPadding(0,0,0,0);dateButton.SetMinimumWidth(0);dateButton.SetMinWidth(0);dateButton.SetSingleLine(true);dateButton.TextSize=12;
            var dateParams=new LinearLayout.LayoutParams(Dp(62),Dp(40));dateParams.SetMargins(0,0,Dp(6),0);heading.AddView(dateButton,dateParams);
            var sync=Button("同步",ManualSync);sync.ContentDescription="手动同步";sync.SetPadding(0,0,0,0);sync.SetMinimumWidth(0);sync.SetMinWidth(0);sync.SetSingleLine(true);sync.TextSize=13;
            var syncParams=new LinearLayout.LayoutParams(Dp(52),Dp(40));syncParams.SetMargins(0,0,Dp(8),0);heading.AddView(sync,syncParams);
            var settings=Button("设置",()=>Navigate(SettingsPage));EnableWhenReady(settings);settings.ContentDescription="设置";settings.SetPadding(0,0,0,0);settings.SetMinimumWidth(0);settings.SetMinWidth(0);settings.SetSingleLine(true);settings.TextSize=13;
            heading.AddView(settings,new LinearLayout.LayoutParams(Dp(52),Dp(40)));
        }
        root.AddView(heading);
        var statusRow=new LinearLayout(this){Orientation=Orientation.Horizontal};statusRow.SetGravity(GravityFlags.CenterVertical);
        status=Text("● " + (app?.Status ?? "可以先记下想法 · 清单载入中"),11); status.SetTextColor(Muted);status.SetSingleLine(true);status.Ellipsize=global::Android.Text.TextUtils.TruncateAt.End; status.SetPadding(0,Dp(5),0,Dp(14));statusRow.AddView(status,new LinearLayout.LayoutParams(0,-2,1));
        if(home){countLabel=Text("",11);countLabel.SetTextColor(Muted);countLabel.Gravity=GravityFlags.Right;countLabel.SetPadding(Dp(8),Dp(5),0,Dp(14));statusRow.AddView(countLabel,new LinearLayout.LayoutParams(-2,-2));}
        status.Click+=(_,_)=>{ if(app is not null) Error(app.SyncDetails); };
        root.AddView(statusRow);
        var scroll = pageScroll = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled=false, HorizontalScrollBarEnabled=false };
        body=new LinearLayout(this) { Orientation=Orientation.Vertical };pageBody=body;
        scroll.AddView(body); root.AddView(scroll,new LinearLayout.LayoutParams(-1,0,1)); SetContentView(root);StatusChanged();
    }
    private sealed class Insets(MainActivity owner) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View? view, WindowInsets? insets)
        {
            if (view is not null && insets is not null)
            {
                int top, bottom;
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                { var bars=insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.Ime()); top=bars.Top; bottom=bars.Bottom; }
                else
                {
#pragma warning disable CA1422
                    top=insets.SystemWindowInsetTop; bottom=insets.SystemWindowInsetBottom;
#pragma warning restore CA1422
                }
                view.SetPadding(owner.Dp(20),top+owner.Dp(16),owner.Dp(20),bottom+owner.Dp(12));
            }
            return insets!;
        }
    }
    private TextView Text(string value,int size=15,bool strong=false)
    {
        var view=new TextView(this) { Text=value, TextSize=size };
        view.SetTextColor(Ink); view.Typeface=strong
            ? mediumFace ??= Typeface.Create("sans-serif-medium",TypefaceStyle.Normal)
            : regularFace ??= Typeface.Create("sans-serif",TypefaceStyle.Normal);
        view.SetPadding(0,Dp(6),0,Dp(6)); return view;
    }
    private Typeface? regularFace, mediumFace;
    private LinearLayout? pageBody;
    private void BeginCard(string? title=null)
    {
        var card=new LinearLayout(this){Orientation=Orientation.Vertical};
        card.Background=Surface(Color.White,14,true);card.SetPadding(Dp(18),Dp(14),Dp(18),Dp(14));
        var layout=new LinearLayout.LayoutParams(-1,-2);layout.SetMargins(0,0,0,Dp(14));
        (pageBody??body).AddView(card,layout);body=card;
        if(title is not null)body.AddView(Text(title,18,true));
    }
    private Button Button(string label,Action? action=null)
    {
        var button=new Button(this) { Text=label, TextSize=14, StateListAnimator=null };
        button.SetAllCaps(false); button.SetMinHeight(Dp(44)); button.SetMinimumHeight(Dp(44));
        button.SetTextColor(Green); button.Background=Surface(Color.Rgb(232,241,243),12);
        button.SetPadding(Dp(12),Dp(8),Dp(12),Dp(8));
        var p=new LinearLayout.LayoutParams(-1,-2); p.SetMargins(0,Dp(5),0,Dp(5)); button.LayoutParameters=p;
        if(action is not null)button.Click+=(_,_)=> { try { action(); } catch(Exception ex){Error(ex.Message);} }; return button;
    }
    private EditText Input(string hint,string value="",bool multiline=false)
    {
        var input=Field(hint,value,multiline);
        var p=new LinearLayout.LayoutParams(-1,-2);p.SetMargins(0,Dp(5),0,Dp(10));body.AddView(input,p);return input;
    }
    private EditText Field(string hint,string value="",bool multiline=false)
    {
        var input=new EditText(this) { Hint=hint,Text=value,TextSize=15 };
        input.SetTextColor(Ink); input.SetHintTextColor(Muted); input.SetSingleLine(!multiline); input.Background=Surface(Color.White,12,true);
        input.SetPadding(Dp(14),Dp(12),Dp(14),Dp(12));
        if(multiline){input.SetMinLines(3);input.Gravity=GravityFlags.Top;} return input;
    }
    private void Error(string message)
    {
        if (IsDestroyed || IsFinishing) return;
        var d=new AlertDialog.Builder(this); d.SetTitle("LanTodo"); d.SetMessage(message);d.SetPositiveButton("知道了",(_,_)=>{});d.Show();
    }
    private void Confirm(string title,string message,string positive,Action action)
    {
        var d=new AlertDialog.Builder(this);d.SetTitle(title);d.SetMessage(message);d.SetNegativeButton("返回",(_,_)=>{});
        d.SetPositiveButton(positive,(_,_)=>{try{action();}catch(Exception ex){Error(ex.Message);}});d.Show();
    }
    private void Home()
    {
        startupControls.Clear();
        renderedState = null;
        pages.Clear(); Screen(filter == "trash" ? "回收站" : filter == "done" ? "已完成" : "全部清单",true);
        var tabs=new LinearLayout(this){Orientation=Orientation.Horizontal};
        foreach(var (key,label) in new[]{("all","清单"),("done","已完成"),("trash","回收站")})
        {
            var chip=Button(label,()=>{RememberPosition();filter=key;Home();});if(key=="trash"){chip.Background=Surface(Color.Rgb(255,230,240),12);chip.SetTextColor(Color.Rgb(167,60,99));}if(filter==key){chip.Background=Surface(key=="trash"?Color.Rgb(167,60,99):Green,12);chip.SetTextColor(Color.White);}
            var lp=new LinearLayout.LayoutParams(0,Dp(42),1);lp.SetMargins(0,Dp(8),Dp(6),Dp(16));tabs.AddView(chip,lp);
        }
        root.RemoveView(pageScroll);
        root.AddView(tabs,2);
        var search=Field("搜索标题、备注与文件名",searchQuery);search.TextSize=14;search.SetPadding(Dp(12),Dp(8),Dp(12),Dp(8));
        search.ContentDescription="搜索想法";search.ImeOptions=ImeAction.Search;
        root.AddView(search,3,new LinearLayout.LayoutParams(-1,Dp(42)));
        int searchVersion=0;
        search.TextChanged+=(_,_)=>{searchQuery=search.Text??"";int version=++searchVersion;search.PostDelayed(()=>{if(version==searchVersion && showingHome && !IsDestroyed)RenderTodos();},180);};
        homeFeed=new ListView(this){Divider=null,DividerHeight=0,VerticalScrollBarEnabled=false,CacheColorHint=Color.Transparent};
        homeFeed.SetSelector(global::Android.Resource.Color.Transparent);
        homeList=new LinearLayout(this){Orientation=Orientation.Vertical};homeFeed.AddHeaderView(homeList,null,false);
        feedAdapter=new FeedAdapter(this);homeFeed.Adapter=feedAdapter;
        var feedParams=new LinearLayout.LayoutParams(-1,0,1);feedParams.TopMargin=Dp(10);root.AddView(homeFeed,4,feedParams);
        var composer=new LinearLayout(this){Orientation=Orientation.Horizontal,BaselineAligned=false};composer.SetGravity(GravityFlags.Bottom);
        quickInput=new EditText(this){Hint="记下一个想法…",TextSize=16};
        quickInput.SetTextColor(Ink);quickInput.SetHintTextColor(Muted);quickInput.Background=Surface(Color.White,12,true);
        quickInput.InputType=global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextFlagMultiLine | global::Android.Text.InputTypes.TextFlagCapSentences;
        quickInput.SetSingleLine(false);quickInput.SetMinLines(1);quickInput.SetMaxLines(4);quickInput.SetMinimumHeight(Dp(48));
        quickInput.Gravity=GravityFlags.CenterVertical | GravityFlags.Left;quickInput.SetIncludeFontPadding(false);quickInput.VerticalScrollBarEnabled=false;quickInput.HorizontalScrollBarEnabled=false;quickInput.ImeOptions=ImeAction.None | (ImeAction)ImeFlags.NoExtractUi;quickInput.Text=quickDraft;
        quickInput.SetPadding(Dp(14),Dp(12),Dp(14),Dp(12));quickInput.SetFilters(new global::Android.Text.IInputFilter[]{new global::Android.Text.InputFilterLengthFilter(500)});
        quickInput.TextChanged+=(_,_)=>{quickDraft=quickInput.Text??"";if(!restoringCompose)composeTouched=true;ScheduleComposeDraft();};
        quickInput.Touch+=(_,args)=>{if(args.Event?.ActionMasked==MotionEventActions.Down && attachmentTray is not null)attachmentTray.Visibility=ViewStates.Gone;args.Handled=false;};
        composer.AddView(quickInput,new LinearLayout.LayoutParams(0,-2,1));
        var attach=new ImageButton(this){ContentDescription="添加图片或文件",Background=Surface(Color.Rgb(232,241,243),24)};
        attach.SetImageResource(Resource.Drawable.ic_add);attach.SetPadding(Dp(12),Dp(12),Dp(12),Dp(12));attach.Click+=(_,_)=>ToggleAttachmentTray();
        var attachParams=new LinearLayout.LayoutParams(Dp(48),Dp(48));attachParams.SetMargins(Dp(6),0,0,0);composer.AddView(attach,attachParams);
        var send=Button("发送",QuickAdd);send.ContentDescription="发送消息";send.TextSize=14;send.Gravity=GravityFlags.Center;send.SetSingleLine(true);
        send.SetMinWidth(0);send.SetMinimumWidth(0);send.SetPadding(Dp(12),0,Dp(12),0);send.SetIncludeFontPadding(false);
        send.Background=Surface(Green,12);send.SetTextColor(Color.White);
        void UpdateSend(){var hasContent=!string.IsNullOrWhiteSpace(quickInput.Text)||pendingAttachments.Count>0;send.Visibility=hasContent?ViewStates.Visible:ViewStates.Gone;send.Enabled=hasContent&&!attachmentBusy;attach.Enabled=app is not null&&!attachmentBusy;}
        updateSend=UpdateSend;quickInput.TextChanged+=(_,_)=>UpdateSend();UpdateSend();
        var sendParams=new LinearLayout.LayoutParams(-2,Dp(48));sendParams.SetMargins(Dp(6),0,0,0);composer.AddView(send,sendParams);
        pendingPanel=new LinearLayout(this){Orientation=Orientation.Vertical};pendingScroll=new ScrollView(this){VerticalScrollBarEnabled=false,HorizontalScrollBarEnabled=false};pendingScroll.AddView(pendingPanel);root.AddView(pendingScroll,new LinearLayout.LayoutParams(-1,Dp(140)));RenderPendingAttachments();
        var cp=new LinearLayout.LayoutParams(-1,-2);cp.SetMargins(0,Dp(12),0,0);root.AddView(composer,cp);
        attachmentTray=new LinearLayout(this){Orientation=Orientation.Horizontal,Visibility=ViewStates.Gone};
        foreach(var (label,mime) in new[]{("图片","image/*"),("文件","*/*")})
        {
            var option=Button(label,()=>OpenAttachmentPicker(mime));var lp=new LinearLayout.LayoutParams(0,Dp(64),1);lp.SetMargins(Dp(4),Dp(12),Dp(4),0);attachmentTray.AddView(option,lp);
        }
        root.AddView(attachmentTray,new LinearLayout.LayoutParams(-1,-2));
        AddVoiceComposer(composer);
        if(filter=="trash") { composer.Visibility=ViewStates.Gone; pendingScroll.Visibility=ViewStates.Gone; }
        RenderTodos();
        var feed=homeFeed;var offset=listOffsets.GetValueOrDefault(filter);feed.Post(()=>feed.SetSelectionFromTop(offset.Position,offset.Top));
    }
    private void ToggleAttachmentTray()
    {
        if(attachmentTray is null || quickInput is null)return;
        var keyboard=(InputMethodManager)GetSystemService(InputMethodService)!;
        if(attachmentTray.Visibility==ViewStates.Visible)
        {attachmentTray.Visibility=ViewStates.Gone;quickInput.RequestFocus();keyboard.ShowSoftInput(quickInput,ShowFlags.Implicit);}
        else
        {quickInput.ClearFocus();keyboard.HideSoftInputFromWindow(quickInput.WindowToken,HideSoftInputFlags.None);attachmentTray.Visibility=ViewStates.Visible;}
    }
    private void SelectDate()
    {
        var current=selectedDate is null?DateTime.Today:DateTime.Parse(selectedDate);
        var dialog=new DatePickerDialog(this,(_,args)=>{selectedDate=args.Date.ToString("yyyy-MM-dd");Home();},current.Year,current.Month-1,current.Day);
        dialog.SetButton(-3,"全部日期",(_,_)=>{selectedDate=null;Home();});dialog.Show();
    }
    private void QuickAdd()
    {
        var value=quickInput?.Text?.Trim();if(attachmentBusy || (string.IsNullOrWhiteSpace(value)&&pendingAttachments.Count==0))return;
        try
        {
            if (app is null)
            {
                var captureClock = System.Diagnostics.Stopwatch.StartNew();
                AndroidSession.Captures(this).Enqueue(value!);
                global::Android.Util.Log.Info("LanTodo", "EarlyCaptureSavedMs=" + captureClock.ElapsedMilliseconds);
                AndroidSession.ProtectRecentSend();
                sentDuringStartup = true;
                quickDraft = ""; quickInput!.Text = "";
                GetSharedPreferences("preferences",FileCreationMode.Private)!.Edit()!.PutString("quickDraft","")!.Commit();
                RenderTodos();
                _ = FlushStartupCapturesAsync();
                return;
            }
            app.LocalWrites.DrainAsync().GetAwaiter().GetResult();
            app.Store.Save(app.Identity.Id,app.Identity.Name,new TodoData(string.IsNullOrWhiteSpace(value)?pendingAttachments[0].Name:value,Attachments:pendingAttachments.Count==0?null:pendingAttachments.ToArray()),draftKey:"compose");pendingAttachments.Clear();quickDraft="";quickInput!.Text="";RenderPendingAttachments();
            AndroidSession.ProtectRecentSend();
            SaveComposeDraft();
            if(filter!="all" || selectedDate is not null || searchQuery.Length>0){filter="all";selectedDate=null;searchQuery="";Home();}else StoreChanged();
        }
        catch(Exception ex){Error(ex.Message);}
    }
    private void RenderTodos()
    {
        if(homeList is null)return;
        if(app is null) { RenderStartupCaptures(); return; }
        var state=filter+"|"+selectedDate+"|"+searchQuery+"|"+app.ShowDeviceSource+"|"+Interlocked.Read(ref contentVersion);
        if(state==renderedState)return;renderedState=state;
        var all=filter=="trash"?app.Store.Trash():app.Store.List();
        int conflicts=app.Store.List().Count(t=>t.Conflict);
        var position=homeFeed?.FirstVisiblePosition??0;var top=homeFeed?.GetChildAt(0)?.Top??0;
        homeList.RemoveAllViews();
        AddRecoveredDraft();
        AddPendingCaptures();
        if(filter=="done" && all.Any(t=>!t.Conflict&&t.Data.Completed))
        {
            var clear=Button("全部删除",DeleteCompleted);clear.SetTextColor(Color.Rgb(179,78,78));clear.Background=Surface(Color.Rgb(249,236,236));homeList.AddView(clear);
        }
        if(conflicts>0)homeList.AddView(Button($"待确认 · {conflicts} 条  ›",()=>Navigate(ConflictsPage)));
        if(filter=="trash" && all.Length>0) homeList.AddView(Button("全部清除",()=>Confirm("清空回收站",$"彻底清除 {all.Length} 条记录？此操作会同步到所有已连接设备。","全部清除",()=>_ = PurgeTrashAsync(all))));
        var todos=all.Where(t=>!t.Conflict && (filter=="trash" || (filter=="done"?t.Data.Completed:!t.Data.Completed)))
            .Where(t=>selectedDate is null||t.Data.Date==selectedDate).Where(t=>MessageQuery.Matches(t,searchQuery)).ToArray();
        countLabel.Text=$"{todos.Length} 项";
        if(todos.Length==0)
        {
            var empty=new LinearLayout(this){Orientation=Orientation.Vertical};empty.SetGravity(GravityFlags.Center);empty.SetPadding(Dp(20),Dp(52),Dp(20),Dp(32));
            var mark=Text("✓",40,true);mark.SetTextColor(Green);empty.AddView(mark);
            empty.AddView(Text(searchQuery.Length>0?"没有找到匹配的想法":filter=="trash"?"回收站是空的":filter=="done"?"每一小步，都算数":"给想法一个落点",20,true));var hint=Text(searchQuery.Length>0?"试试其他关键词，或调整日期与分类。":filter=="trash"?"删除的内容会先保留在这里。":"在下方输入，轻点发送即可记下。",13);hint.SetTextColor(Muted);empty.AddView(hint);homeList.AddView(empty);
        }
        feedAdapter?.Update(todos);
        homeFeed?.SetSelectionFromTop(position,top);
    }
    private void ConflictsPage()
    {
        Screen("待确认的内容");body.AddView(Text("选择要保留的内容，决定会自动同步。",14));
        foreach(var todo in app.Store.List().Where(t=>t.Conflict))
        {
            body.AddView(Text(todo.Data.Title,20,true));
            foreach (var head in todo.Heads)
            {
                var data=head.Body.Data;
                var card=new LinearLayout(this){Orientation=Orientation.Vertical};card.Background=Surface(Color.White,14);card.SetPadding(Dp(14),Dp(10),Dp(14),Dp(10));
                card.AddView(Text(app.DisplayName(head.Body.Actor,head.Body.DeviceName)+" · "+DateTimeOffset.Parse(head.Body.CreatedUtc).ToLocalTime().ToString("MM-dd HH:mm"),12));
                card.AddView(Text((data.Deleted?"已删除 · ":data.Completed?"已完成 · ":"待办 · ")+data.Title,16,true));
                if(data.Date is not null)card.AddView(Text(data.Date+" "+data.Time,12));
                if(data.Notes.Length>0){var notes=Text(data.Notes,13);notes.SetMaxLines(4);card.AddView(notes);}
                if(data.Attachments is {Length:>0})card.AddView(Text(string.Join(" · ",data.Attachments.Select(a=>a.Name)),12));
                card.AddView(Button(data.Deleted?"采用删除":"保留这个版本",()=>{app.Store.Save(app.Identity.Id,app.Identity.Name,data,todo.Id,todo.VersionIds);ConflictsPage();}));
                var spacing=new LinearLayout.LayoutParams(-1,-2);spacing.SetMargins(0,0,0,Dp(10));body.AddView(card,spacing);
            }
            body.AddView(Button("编辑合并…",()=>Navigate(()=>Editor(todo))));
            body.AddView(Button("删除此条",()=>{app.Store.Save(app.Identity.Id,app.Identity.Name,todo.Data with{Deleted=true},todo.Id,todo.VersionIds);ConflictsPage();}));
        }
        if(!app.Store.List().Any(t=>t.Conflict))body.AddView(Text("✓ 所有内容已确认",20,true));
    }

    private void SettingsPage()
    {
        Screen("设置");
        BeginCard("本机昵称");
        var nickname=Input("其他设备看到的名字",app.Identity.Name);
        body.AddView(Button("保存昵称",()=>{app.RenameSelf(nickname.Text??"");Toast.MakeText(this,"昵称已保存，将自动同步",ToastLength.Short)?.Show();}));
        BeginCard("显示与同步");
        var source=new Switch(this){Text="显示消息的最后修改设备",Checked=app.ShowDeviceSource,TextSize=16};
        source.CheckedChange+=(_,args)=>{try{app.SetShowDeviceSource(args.IsChecked);}catch(Exception ex){Error(ex.Message);}};body.AddView(source);
        body.AddView(Text("空间只管理设备连接，所有空间共用清单；同时加入多个空间的设备会接力同步内容。",13));
        body.AddView(Button("设备与同步  ›",()=>Navigate(Devices)));
        var mobile=new Switch(this){Text="允许移动数据同步（包含原件）",Checked=AndroidSession.MobileEnabled(this),TextSize=16};
        mobile.CheckedChange+=async(_,args)=>{AndroidSession.SetMobile(this,args.IsChecked);try{await AndroidSession.RefreshNetworkAsync();app.RequestSync();}catch(Exception ex){Error(ex.Message);}};body.AddView(mobile);
        body.AddView(Text("默认仅使用 Wi-Fi、以太网或 VPN。移动数据开启后，已配置的远程设备也可通过移动网络同步。",13));
        BeginCard("后台连接");
        var background=new Switch(this){Text="后台与锁屏同步",Checked=AndroidSession.BackgroundEnabled(this),TextSize=16};
        background.SetPadding(Dp(8),Dp(16),Dp(8),Dp(16));body.AddView(background);
        background.CheckedChange+=(_,args)=>
        {
            try
            {
                AndroidSession.SetBackground(this,args.IsChecked);
                if(args.IsChecked)AndroidSession.StartBackground(this);else StopService(new Intent(this,typeof(SyncService)));
            }
            catch(Exception ex){Error("后台连接未启动："+ex.Message);}
        };
        body.AddView(Text("开启后显示常驻通知，有变化时自动同步。空闲时不持续唤醒 CPU；锁屏深度休眠时可能延后同步，回到应用会立即补齐。",13));
        var power=(PowerManager)GetSystemService(PowerService)!;
        body.AddView(Button(power.IsIgnoringBatteryOptimizations(PackageName!)?"锁屏联网：已允许不受电池优化限制":"允许锁屏联网（电池设置）",()=>
        {
            if(!power.IsIgnoringBatteryOptimizations(PackageName!))StartActivity(new Intent(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,global::Android.Net.Uri.Parse("package:"+PackageName)));
            else StartActivity(new Intent(global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings));
        }));
        body.AddView(Text("小米 / HyperOS 若仍停止后台连接，请在系统应用设置中允许后台自启动，并将本应用电池策略设为无限制。",12));
        BeginCard("同步频率");body.AddView(Text("有修改时立即同步；下方间隔用于定期补漏。",13));
        var reconcile=Button($"兜底核对：每 {app.ReconcileHours} 小时（点击切换）");
        reconcile.Click+=(_,_)=>{try{app.SetReconcileHours(app.ReconcileHours==1?2:1);reconcile.Text=$"兜底核对：每 {app.ReconcileHours} 小时（点击切换）";}catch(Exception ex){Error(ex.Message);}};body.AddView(reconcile);
        body.AddView(Button("现在同步一次",ManualSync));
        BeginCard("数据");body.AddView(Button("备份与恢复  ›",()=>Navigate(Backups)));body.AddView(Text($"LanTodo v{PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName} · 数据保存在你的设备",12));
    }
    private void ManualSync() { app?.RequestReconnect(); Toast.MakeText(this,"正在连接已配对设备",ToastLength.Short)?.Show(); }
    private void DeleteCompleted()
    {
        var confirmed=app.Store.List().Where(t=>!t.Conflict&&t.Data.Completed).ToArray();
        if(confirmed.Length==0)return;
        Confirm("删除已完成内容",$"删除全部 {confirmed.Length} 条已完成内容（包含其他日期）？内容将移入回收站，并同步到所有已连接设备。","全部删除",()=>_ = DeleteConfirmedAsync(confirmed));
    }
    private async Task DeleteConfirmedAsync(TodoView[] confirmed)
    {
        try
        {
            var result=await Task.Run(()=>app.Store.DeleteCompleted(app.Identity.Id,app.Identity.Name,confirmed));
            if(!IsDestroyed) { if(showingHome)RenderTodos(); Toast.MakeText(this,result.Skipped>0?$"已删除 {result.Deleted} 条，{result.Skipped} 条新修改已保留":$"已删除 {result.Deleted} 条已完成内容",ToastLength.Long)?.Show(); }
        }
        catch(Exception ex){if(!IsDestroyed)Error("未全部删除，未处理内容仍保留。\n"+ex.Message);}
    }
}
