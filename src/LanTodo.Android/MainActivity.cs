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
public partial class MainActivity : Activity
{
    private AppRuntime app = null!;
    private LinearLayout body = null!, root = null!, homeList = null!;
    private TextView status = null!, countLabel = null!;
    private EditText? quickInput;
    private string quickDraft = "", filter = "all";
    private string? selectedDate;
    private bool showingHome, showingDevices;
    private bool started;
    private readonly Stack<PageState> pages = new();
    private readonly CancellationTokenSource activityToken = new();
    private BackHandler? backHandler;
    private SplashExitHandler? splashExit;
    private SplashScreenView? splashView;
    private bool homeReady;
    private static readonly Color Ink = Color.Rgb(31,41,55), Green = Color.Rgb(22,125,141), Paper = Color.Rgb(245,247,250), Muted = Color.Rgb(123,135,149);
    private sealed record PageState(LinearLayout Root, LinearLayout Body, TextView Status, bool Home, bool Devices);

    protected override async void OnCreate(Bundle? state)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        SetTheme(Resource.Style.LanTodoTheme);
        base.OnCreate(state);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            splashExit = new SplashExitHandler(view =>
            {
                if (homeReady || IsDestroyed || IsFinishing) { if (OperatingSystem.IsAndroidVersionAtLeast(31)) view.Remove(); }
                else splashView = view;
            });
            SplashScreen!.SetOnExitAnimationListener(splashExit);
        }
        var loading = new FrameLayout(this);
        loading.SetBackgroundColor(Paper);
        var logo = new ImageView(this);
        logo.SetImageResource(Resource.Drawable.ic_launcher);
        loading.AddView(logo, new FrameLayout.LayoutParams(Dp(64), Dp(64), GravityFlags.Center));
        SetContentView(loading);
        try
        {
            app = await AndroidSession.GetAsync(this);
            if (IsDestroyed || IsFinishing) return;
            app.Store.Changed += StoreChanged; app.Node.Changed += StatusChanged; app.Identity.TrustChanged += TrustChanged;
            quickDraft = GetSharedPreferences("preferences",FileCreationMode.Private)!.GetString("quickDraft", "") ?? "";
            Home();
            root.Post(() => { if (!IsDestroyed) { FinishSplash(); ReportFullyDrawn(); global::Android.Util.Log.Info("LanTodo", "ReadyMs=" + clock.ElapsedMilliseconds); } });
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                backHandler = new BackHandler(GoBack);
                OnBackInvokedDispatcher!.RegisterOnBackInvokedCallback(0, backHandler);
                if (CheckSelfPermission("android.permission.POST_NOTIFICATIONS") != global::Android.Content.PM.Permission.Granted)
                    RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, 5);
            }
            if (started) await StartNetworkAsync();
        }
        catch (Exception ex) { FinishSplash(); Error("无法打开本地资料，请保留应用数据。\n" + ex.Message); }
    }
    private void FinishSplash()
    {
        homeReady = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) { splashView?.Remove(); splashView = null; }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("android31.0")]
    private sealed class SplashExitHandler(Action<SplashScreenView> action) : Java.Lang.Object, ISplashScreenOnExitAnimationListener
    { public void OnSplashScreenExit(SplashScreenView view) => action(view); }
    protected override async void OnStart()
    {
        base.OnStart();
        started = true;
        AndroidSession.ActivityVisible = true;
        if (app is not null) await StartNetworkAsync();
    }
    private async Task StartNetworkAsync()
    {
        try
        {
            if (AndroidSession.BackgroundEnabled(this)) AndroidSession.StartBackground(this);
            else await app.SetNetworkAsync(true);
        }
        catch (Exception ex) { Error("后台连接未启动，本机仍可使用。\n" + ex.Message); }
    }
    protected override async void OnStop()
    {
        base.OnStop();
        started = false;
        AndroidSession.ActivityVisible = false;
        if (app is null) return;
        GetSharedPreferences("preferences", FileCreationMode.Private)!.Edit()!.PutString("quickDraft",quickDraft)!.Apply();
        try { await AndroidSession.RefreshNetworkAsync(); }
        catch (Exception ex) { global::Android.Util.Log.Warn("LanTodo", ex.GetType().Name); }
    }
    protected override void OnDestroy()
    {
        FinishSplash();
        activityToken.Cancel();
        if (app is not null) { app.Store.Changed -= StoreChanged; app.Node.Changed -= StatusChanged; app.Identity.TrustChanged -= TrustChanged; app.Identity.CancelInvite(); }
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && backHandler is not null) OnBackInvokedDispatcher!.UnregisterOnBackInvokedCallback(backHandler);
        base.OnDestroy();
    }
#pragma warning disable CS0672, CA1422
    public override void OnBackPressed() => GoBack();
#pragma warning restore CS0672, CA1422
    private sealed class BackHandler(Action action) : Java.Lang.Object, IOnBackInvokedCallback
    { public void OnBackInvoked() => action(); }
    private void Navigate(Action render)
    { pages.Push(new(root,body,status,showingHome,showingDevices)); render(); }
    private void GoBack()
    {
        if (app is null) { MoveTaskToBack(true); return; }
        if (pages.TryPop(out var page))
        {
            if (page.Home) { Home(); return; }
            root=page.Root; body=page.Body; status=page.Status; showingHome=page.Home; showingDevices=page.Devices;
            SetContentView(root); root.RequestApplyInsets(); StatusChanged();
        }
        else if (!showingHome) Home();
        else MoveTaskToBack(true);
    }
    private void StoreChanged() => RunOnUiThread(() => { if (showingHome && !IsDestroyed) RenderTodos(); });
    private void StatusChanged() => RunOnUiThread(() => { if (!IsDestroyed && status is not null) status.Text = "● " + app.Node.Status; });
    private void TrustChanged() => RunOnUiThread(() => { if (showingDevices && !IsDestroyed) Devices(); });
    private int Dp(int n) => (int)(n * Resources!.DisplayMetrics!.Density);
    private GradientDrawable Surface(Color color, int radius = 14, bool border = false)
    {
        var drawable = new GradientDrawable(); drawable.SetColor(color); drawable.SetCornerRadius(Dp(radius));
        if (border) drawable.SetStroke(Dp(1),Color.Rgb(225,231,237));
        return drawable;
    }
    private void Screen(string title, bool home = false)
    {
        showingHome = home; showingDevices = false;
        root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Paper); root.SetPadding(Dp(20),Dp(24),Dp(20),Dp(20));
        root.SetOnApplyWindowInsetsListener(new Insets(this));
        var heading = new LinearLayout(this) { Orientation = Orientation.Horizontal }; heading.SetGravity(GravityFlags.CenterVertical);
        if (!home)
        {
            var back=Button("‹",GoBack); back.TextSize=28; back.ContentDescription="返回上一级";
            var backParams=new LinearLayout.LayoutParams(Dp(40),Dp(44));backParams.SetMargins(0,0,Dp(12),0);heading.AddView(back,backParams);
        }
        heading.AddView(Text(title,home?28:24,true),new LinearLayout.LayoutParams(0,-2,1));
        if (home)
        {
            var sync=Button("同步",ManualSync);sync.ContentDescription="手动同步";sync.SetPadding(0,0,0,0);sync.SetMinimumWidth(0);sync.SetMinWidth(0);sync.SetSingleLine(true);sync.TextSize=13;
            var syncParams=new LinearLayout.LayoutParams(Dp(52),Dp(40));syncParams.SetMargins(0,0,Dp(8),0);heading.AddView(sync,syncParams);
            var settings=Button("设置",()=>Navigate(SettingsPage));settings.ContentDescription="设置";settings.SetPadding(0,0,0,0);settings.SetMinimumWidth(0);settings.SetMinWidth(0);settings.SetSingleLine(true);settings.TextSize=13;
            heading.AddView(settings,new LinearLayout.LayoutParams(Dp(52),Dp(40)));
        }
        root.AddView(heading);
        status=Text("● " + app.Node.Status,11); status.SetTextColor(Muted); status.SetPadding(0,Dp(5),0,Dp(14)); root.AddView(status);
        var scroll = new ScrollView(this) { FillViewport = true };
        body=new LinearLayout(this) { Orientation=Orientation.Vertical };
        scroll.AddView(body); root.AddView(scroll,new LinearLayout.LayoutParams(-1,0,1)); SetContentView(root);
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
        view.SetTextColor(Ink); view.Typeface=Typeface.Create(strong?"sans-serif-medium":"sans-serif",TypefaceStyle.Normal);
        view.SetPadding(0,Dp(6),0,Dp(6)); return view;
    }
    private Button Button(string label,Action action)
    {
        var button=new Button(this) { Text=label, TextSize=14, StateListAnimator=null };
        button.SetAllCaps(false); button.SetMinHeight(Dp(44)); button.SetMinimumHeight(Dp(44));
        button.SetTextColor(Green); button.Background=Surface(Color.Rgb(232,241,243),12);
        button.SetPadding(Dp(12),Dp(8),Dp(12),Dp(8));
        var p=new LinearLayout.LayoutParams(-1,-2); p.SetMargins(0,Dp(5),0,Dp(5)); button.LayoutParameters=p;
        button.Click+=(_,_)=> { try { action(); } catch(Exception ex){Error(ex.Message);} }; return button;
    }
    private EditText Input(string hint,string value="",bool multiline=false)
    {
        var input=new EditText(this) { Hint=hint,Text=value,TextSize=15 };
        input.SetTextColor(Ink); input.SetHintTextColor(Muted); input.SetSingleLine(!multiline); input.Background=Surface(Color.White,12,true);
        input.SetPadding(Dp(14),Dp(12),Dp(14),Dp(12));
        var p=new LinearLayout.LayoutParams(-1,-2); p.SetMargins(0,Dp(5),0,Dp(10)); body.AddView(input,p);
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
        pages.Clear(); Screen("LanTodo",true);
        var intro=new LinearLayout(this){Orientation=Orientation.Horizontal};intro.SetGravity(GravityFlags.CenterVertical);
        countLabel=Text("",13);countLabel.SetTextColor(Muted);intro.AddView(countLabel,new LinearLayout.LayoutParams(0,-2,1));
        intro.AddView(Button(selectedDate is null?"日期":selectedDate,()=>
        {
            var now=DateTime.Today;var dialog=new DatePickerDialog(this,(_,args)=>{selectedDate=args.Date.ToString("yyyy-MM-dd");Home();},now.Year,now.Month-1,now.Day);
            dialog.SetButton(-3,"全部日期",(_,_)=>{selectedDate=null;Home();});dialog.Show();
        }),new LinearLayout.LayoutParams(Dp(100),Dp(40)));
        body.AddView(intro);
        var tabs=new LinearLayout(this){Orientation=Orientation.Horizontal};
        foreach(var (key,label) in new[]{("all","清单"),("today","今天"),("done","已完成")})
        {
            var chip=Button(label,()=>{filter=key;Home();});if(filter==key){chip.Background=Surface(Green,12);chip.SetTextColor(Color.White);}
            var lp=new LinearLayout.LayoutParams(0,Dp(42),1);lp.SetMargins(0,Dp(8),Dp(6),Dp(16));tabs.AddView(chip,lp);
        }
        body.AddView(tabs);homeList=new LinearLayout(this){Orientation=Orientation.Vertical};body.AddView(homeList);
        var composer=new LinearLayout(this){Orientation=Orientation.Horizontal};composer.SetGravity(GravityFlags.CenterVertical);
        composer.Background=Surface(Color.White,22,true);composer.SetPadding(Dp(8),Dp(5),Dp(5),Dp(5));
        quickInput=new EditText(this){Hint="记下一个想法…",Text=quickDraft,TextSize=16,ImeOptions=ImeAction.Send};
        quickInput.SetTextColor(Ink);quickInput.SetHintTextColor(Muted);quickInput.Background=null;quickInput.SetSingleLine(true);quickInput.ImeOptions=ImeAction.Send;
        quickInput.SetPadding(Dp(8),Dp(9),Dp(8),Dp(9));quickInput.SetFilters(new global::Android.Text.IInputFilter[]{new global::Android.Text.InputFilterLengthFilter(500)});
        quickInput.TextChanged+=(_,_)=>quickDraft=quickInput.Text??"";
        quickInput.EditorAction+=(_,args)=>{if(args.ActionId==ImeAction.Send){QuickAdd();args.Handled=true;}};
        composer.AddView(quickInput,new LinearLayout.LayoutParams(0,-2,1));
        var send=Button("✓",QuickAdd);send.TextSize=25;send.SetTypeface(Typeface.Default,TypefaceStyle.Bold);send.ContentDescription="添加待办";send.Background=Surface(Green,18);send.SetTextColor(Color.White);
        composer.AddView(send,new LinearLayout.LayoutParams(Dp(46),Dp(46)));
        var cp=new LinearLayout.LayoutParams(-1,-2);cp.SetMargins(0,Dp(12),0,0);root.AddView(composer,cp);
        RenderTodos();
    }
    private void QuickAdd()
    {
        var value=quickInput?.Text?.Trim();if(string.IsNullOrWhiteSpace(value))return;
        try
        {
            app.Store.Save(app.Identity.Id,app.Identity.Name,new TodoData(value));quickDraft="";quickInput!.Text="";
            GetSharedPreferences("preferences",FileCreationMode.Private)!.Edit()!.PutString("quickDraft","")!.Apply();
            if(filter!="all" || selectedDate is not null){filter="all";selectedDate=null;Home();}else RenderTodos();
        }
        catch(Exception ex){Error(ex.Message);}
    }
    private void RenderTodos()
    {
        if(homeList is null)return;
        homeList.RemoveAllViews();var all=app.Store.List();countLabel.Text=$"{DateTime.Now:MM 月 dd 日}  ·  {all.Count(t=>!t.Conflict&&!t.Data.Completed)} 项待办";
        int conflicts=all.Count(t=>t.Conflict);
        if(filter=="done" && all.Any(t=>!t.Conflict&&t.Data.Completed))
        {
            var clear=Button("全部删除",DeleteCompleted);clear.SetTextColor(Color.Rgb(179,78,78));clear.Background=Surface(Color.Rgb(249,236,236));homeList.AddView(clear);
        }
        if(conflicts>0)homeList.AddView(Button($"{conflicts} 条内容需要确认  ›",()=>Navigate(ConflictsPage)));
        var todos=all.Where(t=>!t.Conflict && (filter=="done"?t.Data.Completed:!t.Data.Completed) && (filter!="today"||t.Data.Date==DateTime.Today.ToString("yyyy-MM-dd")))
            .Where(t=>selectedDate is null||t.Data.Date==selectedDate).ToArray();
        if(todos.Length==0)
        {
            var empty=new LinearLayout(this){Orientation=Orientation.Vertical};empty.SetGravity(GravityFlags.Center);empty.SetPadding(Dp(20),Dp(52),Dp(20),Dp(32));
            var mark=Text("✓",40,true);mark.SetTextColor(Green);empty.AddView(mark);
            empty.AddView(Text(filter=="done"?"每一小步，都算数":"给想法一个落点",20,true));var hint=Text("在下方输入，轻点发送即可记下。",13);hint.SetTextColor(Muted);empty.AddView(hint);homeList.AddView(empty);
        }
        foreach(var todo in todos)
        {
            var row=new LinearLayout(this){Orientation=Orientation.Horizontal};row.SetGravity(GravityFlags.CenterVertical);
            row.Background=Surface(Color.White,16);row.SetPadding(Dp(8),Dp(11),Dp(14),Dp(11));
            var lp=new LinearLayout.LayoutParams(-1,-2);lp.SetMargins(0,0,0,Dp(8));homeList.AddView(row,lp);
            var check=Text(todo.Data.Completed?"✓":"○",26);check.Gravity=GravityFlags.Center;check.SetTextColor(todo.Data.Completed?Green:Color.Rgb(182,196,205));
            check.ContentDescription=todo.Data.Completed?"标为未完成":"完成待办";
            check.Click+=(_,_)=>{try{app.Store.Save(app.Identity.Id,app.Identity.Name,todo.Data with{Completed=!todo.Data.Completed},todo.Id,todo.VersionIds);}catch(Exception ex){Error(ex.Message);}};
            row.AddView(check,new LinearLayout.LayoutParams(Dp(44),Dp(48)));
            var content=new LinearLayout(this){Orientation=Orientation.Vertical};var title=Text(todo.Data.Title,16,true);if(todo.Data.Completed){title.SetTextColor(Muted);title.PaintFlags|=PaintFlags.StrikeThruText;}content.AddView(title);
            if(todo.Data.Date is not null){var when=Text(todo.Data.Date+"  "+todo.Data.Time,12);when.SetTextColor(Green);content.AddView(when);}
            if(todo.Data.Notes.Length>0){var preview=Text(todo.Data.Notes,12);preview.SetTextColor(Muted);preview.SetMaxLines(1);if(todo.Data.Completed)preview.PaintFlags|=PaintFlags.StrikeThruText;content.AddView(preview);}
            content.Click+=(_,_)=>Navigate(()=>Editor(todo));row.AddView(content,new LinearLayout.LayoutParams(0,-2,1));
        }
    }
    private void ConflictsPage()
    {
        Screen("待确认的内容");body.AddView(Text("双方的修改均已保留，请选择最终内容。",14));
        foreach(var todo in app.Store.List().Where(t=>t.Conflict))body.AddView(Button(todo.Data.Title+"  ›",()=>Navigate(()=>Editor(todo))));
    }
    private void SettingsPage()
    {
        Screen("设置");body.AddView(Text("连接与同步",19,true));
        body.AddView(Button("我的设备  ›",()=>Navigate(Devices)));
        body.AddView(Button("NAS 辅助同步  ›",()=>Navigate(NasSettings)));
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
        body.AddView(Text("开启后显示常驻通知，关闭界面或锁屏仍保持设备连接。会增加电量消耗；系统强制停止应用后需重新打开。",13));
        var power=(PowerManager)GetSystemService(PowerService)!;
        body.AddView(Button(power.IsIgnoringBatteryOptimizations(PackageName!)?"锁屏联网：已允许不受电池优化限制":"允许锁屏联网（电池设置）",()=>
        {
            if(!power.IsIgnoringBatteryOptimizations(PackageName!))StartActivity(new Intent(global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,global::Android.Net.Uri.Parse("package:"+PackageName)));
            else StartActivity(new Intent(global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings));
        }));
        body.AddView(Text("小米 / HyperOS 若仍停止后台连接，请在系统应用设置中允许后台自启动，并将本应用电池策略设为无限制。",12));
        body.AddView(Text("有修改时立即同步",18,true));body.AddView(Text("无修改时不反复交换数据。下方间隔仅用于兜底核对；轻量设备发现独立进行。",13));
        body.AddView(Button($"兜底核对：每 {app.ReconcileHours} 小时（点击切换）",()=>{app.SetReconcileHours(app.ReconcileHours==1?2:1);SettingsPage();}));
        body.AddView(Button("现在同步一次",ManualSync));
        body.AddView(Text("数据",19,true));body.AddView(Button("备份与恢复  ›",()=>Navigate(Backups)));body.AddView(Text($"LanTodo v{PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName} · 数据保存在你的设备",12));
    }
    private void ManualSync() { app.Node.RequestSync(); Toast.MakeText(this,"已请求同步，正在连接已配对设备",ToastLength.Short)?.Show(); }
    private void DeleteCompleted()
    {
        var confirmed=app.Store.List().Where(t=>!t.Conflict&&t.Data.Completed).ToArray();
        if(confirmed.Length==0)return;
        Confirm("删除已完成内容",$"删除全部 {confirmed.Length} 条已完成内容（包含其他日期）？删除会同步到其他设备，修改历史仍保留。","全部删除",()=>_ = DeleteConfirmedAsync(confirmed));
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
