using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using LanTodo.Core;

namespace LanTodo.Android;

public partial class MainActivity
{
    private EditText? spaceCodeInput;
    private readonly Dictionary<string, TextView> spaceStatusLabels = new();
    private void SpaceNameDialog(bool create)
    {
        var input=Field("例如：家里、公司",create?"":app.Identity.SpaceName);
        var container=new LinearLayout(this){Orientation=Orientation.Vertical};container.SetPadding(Dp(24),Dp(12),Dp(24),0);container.AddView(input);
        new AlertDialog.Builder(this).SetTitle(create?"新建连接空间":"空间名称")!.SetView(container)!.SetNegativeButton("取消",(_,_)=>{})!
            .SetPositiveButton("保存",async(_,_)=>{try{if(create)await app.CreateSpaceAsync(input.Text??"");else app.RenameSpace(input.Text??"");Devices();}catch(Exception ex){Error(ex.Message);}})!.Show();
    }
    private void Devices()
    {
        Screen("设备与同步"); showingDevices=true;spaceStatusLabels.Clear();
        var space=app.Identity.Space;bool active=space?.Contains(app.Identity.Id)==true;
        LinearLayout Card()
        {
            var card=new LinearLayout(this){Orientation=Orientation.Vertical};card.Background=Surface(Color.White,14,true);card.SetPadding(Dp(18),Dp(14),Dp(18),Dp(14));
            var lp=new LinearLayout.LayoutParams(-1,-2);lp.SetMargins(0,0,0,Dp(14));body.AddView(card,lp);return card;
        }
        var overview=Card();overview.AddView(Text("当前连接空间",12));overview.AddView(Text(app.Identity.SpaceName,24,true));
        overview.AddView(Text(active?$"{space!.Members.Length} 台设备 · 共用本机全部内容":"已退出 · 本机内容仍保留",12));
        var spaceActions=new LinearLayout(this){Orientation=Orientation.Horizontal};overview.AddView(spaceActions);
        var switcher=Button("切换空间",()=>
        {
            var spaces=app.Spaces;
            new AlertDialog.Builder(this).SetTitle("选择空间")!.SetItems(spaces.Select(s=>(s.Selected?"✓ ":"")+s.Name+(s.Member?"":" · 已退出")).ToArray(),(_,args)=>{try{app.SelectSpace(spaces[args.Which].Key);Devices();}catch(Exception ex){Error(ex.Message);}})!.Show();
        });spaceActions.AddView(switcher,new LinearLayout.LayoutParams(0,-2,1));
        var rename=Button("修改名称",()=>SpaceNameDialog(false));rename.Enabled=active;spaceActions.AddView(rename,new LinearLayout.LayoutParams(0,-2,1));
        var managing=new LinearLayout(this){Orientation=Orientation.Horizontal};overview.AddView(managing);
        managing.AddView(Button("＋ 新建空间",()=>SpaceNameDialog(true)),new LinearLayout.LayoutParams(0,-2,1));
        managing.AddView(Button("加入空间",()=>Navigate(JoinSpacePage)),new LinearLayout.LayoutParams(0,-2,1));
        var delete=Button("删除此空间",()=>
        {
            var key=app.ActiveSpaceKey;var name=app.Identity.SpaceName;
            Confirm("删除“"+name+"”",AppRuntime.DeleteSpaceNotice,"删除空间",async()=>
            {
                try { await app.DeleteSpaceAsync(key);if(!IsDestroyed)Devices(); }
                catch(Exception ex){if(!IsDestroyed)Error(ex.Message);}
            });
        });delete.Background=Surface(Color.Rgb(255,240,242),12);delete.SetTextColor(Color.Rgb(179,64,82));overview.AddView(delete);
        foreach(var actions in new[]{spaceActions,managing})
            for(int i=0;i<actions.ChildCount;i++){var layout=new LinearLayout.LayoutParams(0,-2,1);layout.SetMargins(i==0?0:Dp(4),Dp(4),i==0?Dp(4):0,Dp(4));actions.GetChildAt(i)!.LayoutParameters=layout;}
        if(space is null)overview.AddView(Button("升级原有连接",()=>Confirm("升级连接规则","旧设备需要使用新授权码加入，原数据保留。","升级",()=>{app.Identity.EnsureSpace(true);Devices();})));
        var members=Card();members.AddView(Text("空间成员",18,true));members.AddView(Text(app.Identity.Name+" · 本机",15,true));
        foreach(var device in app.Identity.Devices)
        {
            var row=new LinearLayout(this){Orientation=Orientation.Horizontal};row.SetGravity(GravityFlags.CenterVertical);
            var labels=new LinearLayout(this){Orientation=Orientation.Vertical};labels.AddView(Text(device.Name,16,true));
            var state=Text(app.Node.DeviceState(device.Id),12);state.SetTextColor(Muted);labels.AddView(state);spaceStatusLabels[device.Id]=state;
            row.AddView(labels,new LinearLayout.LayoutParams(0,-2,1));
            row.AddView(Button("···",()=>new AlertDialog.Builder(this).SetTitle(device.Name)!.SetItems(new[]{"移出此空间"},(_,_)=>Confirm("移出此空间","其他成员会同步此决定；对方的其他空间不受影响。","移除",()=>{app.Identity.Revoke(device.Id);Devices();}))!.Show()),new LinearLayout.LayoutParams(Dp(54),Dp(44)));
            members.AddView(row);
        }
        if(app.Identity.Devices.Length==0)members.AddView(Text("添加手机或电脑，开始自动同步。",13));
        if(active)members.AddView(Button("＋ 添加设备 · 授权码 / 二维码",()=>Navigate(SpaceInvitePage)));
        var connection=Card();connection.AddView(Text("自动同步",18,true));connection.AddView(Text("在线成员自动接收修改；离线成员上线后补齐。NAS 可以作为常在线成员。",13));
        connection.AddView(Button("立即同步所有空间",()=>{app.RequestSync();Toast.MakeText(this,"正在连接在线成员",ToastLength.Short)?.Show();}));
        connection.AddView(Button("高级连接设置  ›",()=>Navigate(NasSettings)));
        if(active)connection.AddView(Button("退出此空间",()=>Confirm("退出空间","停止此空间的同步，保留本机内容；其他空间不受影响。","退出",()=>{app.Identity.LeaveSpace();Devices();})));
        body.AddView(Text("后台同步受系统电池策略限制。打开应用后会自动补齐。",12));
    }
    private void SpaceInvitePage()
    {
        Screen("添加设备");
        BeginCard();
        body.AddView(Text("另一台设备扫码或粘贴授权码，即可加入整个空间。每次授权码仅供一台设备使用，5 分钟有效；请保持本机在线。"));
        var image = new ImageView(this); image.SetScaleType(ImageView.ScaleType.FitCenter); body.AddView(image, new LinearLayout.LayoutParams(-1, Dp(310)));
        var code = Field("生成后显示授权码",multiline:true);code.TextSize=11;code.KeyListener = null; code.SetTextIsSelectable(true); code.SetMaxLines(3); body.AddView(code);
        var hint = Text(""); body.AddView(hint);
        var generate = Button("生成授权码与二维码"); body.AddView(generate);
        generate.Click += async (_, _) =>
        {
            generate.Enabled = false;
            try
            {
                var value = app.Node.CreateInvite(); code.Text = value; hint.Text = "有效至 " + DateTime.Now.AddMinutes(5).ToString("HH:mm:ss");
                var bitmap = await Task.Run(() => { var png = InviteQr.Png(value); return BitmapFactory.DecodeByteArray(png, 0, png.Length); });
                if (!IsDestroyed && code.Text == value) image.SetImageBitmap(bitmap); else bitmap?.Dispose();
            }
            catch (Exception ex) { if (!IsDestroyed) hint.Text = ex.Message; }
            finally { if (!IsDestroyed) generate.Enabled = true; }
        };
        body.AddView(Button("复制授权码", () => { if (string.IsNullOrEmpty(code.Text)) return; ((ClipboardManager)GetSystemService(ClipboardService)!).PrimaryClip = ClipData.NewPlainText("LanTodo 空间授权码", code.Text); Toast.MakeText(this, "已复制", ToastLength.Short)?.Show(); }));
        body.AddView(Button("作废当前授权码", () => { app.Identity.CancelInvite(); code.Text = ""; image.SetImageDrawable(null); hint.Text = "已作废"; }));
    }
    private void JoinSpacePage()
    {
        Screen("加入已有空间");
        BeginCard();
        body.AddView(Text(AppRuntime.JoinNotice));
        spaceCodeInput = Input("粘贴另一台设备生成的完整授权码", multiline: true); var input = spaceCodeInput;
        body.AddView(Button("扫描二维码", () => StartActivityForResult(new Intent(this, typeof(QrScannerActivity)), 72)));
        body.AddView(Button("从相册读取二维码", () => { var intent = new Intent(Intent.ActionOpenDocument); intent.AddCategory(Intent.CategoryOpenable); intent.SetType("image/*"); StartActivityForResult(intent, 71); }));
        var advanced = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone }; body.AddView(Button("高级：邀请设备地址（通常无需填写）", () => advanced.Visibility = advanced.Visibility == ViewStates.Gone ? ViewStates.Visible : ViewStates.Gone)); body.AddView(advanced);
        var address = Field("IP 或域名:同步端口"); advanced.AddView(address);
        var confirm = new CheckBox(this) { Text = "我了解本机全部内容会与这些设备同步" }; body.AddView(confirm);
        var result = Text(""); var join = Button("加入并自动同步"); body.AddView(join); body.AddView(result);
        join.Click += async (_, _) =>
        {
            if (!confirm.Checked) { result.Text = "请先确认上方的空间说明。"; return; }
            join.Enabled = false; result.Text = "正在连接邀请设备…";
            try { await app.JoinSpaceAsync(input.Text ?? "", address.Text, activityToken.Token); if (!IsDestroyed) { input.Text = ""; Devices(); } }
            catch (Exception ex) { if (!IsDestroyed) result.Text = ex is OperationCanceledException ? "连接已取消或超时。" : ex.Message; }
            finally { if (!IsDestroyed) join.Enabled = true; }
        };
    }
    private async Task ReadSpaceQr(Intent data)
    {
        try
        {
            var value = await Task.Run(() =>
            {
                using var bounds = ContentResolver!.OpenInputStream(data.Data!);
                using var options = new BitmapFactory.Options { InJustDecodeBounds = true }; BitmapFactory.DecodeStream(bounds, null, options);
                int sample = 1; while (Math.Max(options.OutWidth, options.OutHeight) / sample > 1800) sample *= 2;
                using var source = ContentResolver.OpenInputStream(data.Data!);
                using var decode = new BitmapFactory.Options { InSampleSize = sample };
                using var bitmap = BitmapFactory.DecodeStream(source, null, decode) ?? throw new IOException("无法读取图片。");
                var pixels = new int[bitmap.Width * bitmap.Height]; bitmap.GetPixels(pixels, 0, bitmap.Width, 0, 0, bitmap.Width, bitmap.Height);
                var rgb = new byte[pixels.Length * 3];
                for (int i = 0; i < pixels.Length; i++) { rgb[i*3] = (byte)(pixels[i] >> 16); rgb[i*3+1] = (byte)(pixels[i] >> 8); rgb[i*3+2] = (byte)pixels[i]; }
                return InviteQr.Decode(new ZXing.RGBLuminanceSource(rgb, bitmap.Width, bitmap.Height, ZXing.RGBLuminanceSource.BitmapFormat.RGB24));
            });
            if (value is null) throw new InvalidDataException("未找到 LanTodo 空间二维码，请选择完整清晰的图片。");
            if (!IsDestroyed && spaceCodeInput is not null) spaceCodeInput.Text = value;
        }
        catch (Exception ex) { if (!IsDestroyed) Error(ex.Message); }
    }
}
