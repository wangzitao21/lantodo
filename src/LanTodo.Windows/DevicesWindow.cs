using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using LanTodo.Core;

namespace LanTodo.Windows;

public sealed class DevicesWindow : Window
{
    private CancellationTokenSource operation = new();
    public DevicesWindow(AppRuntime app)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Title = "设备与同步"; Width = 720; Height = 780; MinWidth = 600; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(245,247,250));
        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = panel, Background=Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) };
        Border Card(UIElement content) => SettingsTheme.Card(content);
        panel.Children.Add(SettingsTheme.Heading("设备与同步"));
        panel.Children.Add(SettingsTheme.Hint("空间只划分设备连接，所有空间共用同一份清单与附件。"));
        var spacePanel=new StackPanel();panel.Children.Add(Card(spacePanel));
        var spacePicker=new Button { MinHeight=46, FontSize=20, FontWeight=FontWeights.SemiBold, HorizontalContentAlignment=HorizontalAlignment.Left, Margin=new Thickness(0,0,0,8) };spacePanel.Children.Add(spacePicker);
        spacePicker.Click+=(_,_)=>
        {
            var menu=new ContextMenu {PlacementTarget=spacePicker,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom};
            foreach(var space in app.Spaces)
            {
                var item=new MenuItem{Header=(space.Selected?"✓  ":"")+space.Name+(space.Member?"":" · 已退出")};
                item.Click+=(_,_)=>{try{app.SelectSpace(space.Key);}catch(Exception ex){ModernDialog.Show(this,ex.Message,"未完成操作");}};menu.Items.Add(item);
            }
            menu.IsOpen=true;
        };
        var summary = Text(""); spacePanel.Children.Add(summary);
        var spaceName=new TextBox {MaxLength=100,MinWidth=200};
        var naming=new DockPanel {Visibility=Visibility.Collapsed};var renameSpace=new Button{Content="保存名称"};DockPanel.SetDock(renameSpace,Dock.Right);naming.Children.Add(renameSpace);naming.Children.Add(spaceName);spacePanel.Children.Add(naming);
        renameSpace.Click+=(_,_)=>{try{app.RenameSpace(spaceName.Text);naming.Visibility=Visibility.Collapsed;}catch(Exception ex){ModernDialog.Show(this,ex.Message,"未完成操作");}};
        var spaceActions=new StackPanel {Orientation=Orientation.Horizontal};spacePanel.Children.Add(spaceActions);
        var editName=new Button {Content="修改名称"};spaceActions.Children.Add(editName);editName.Click+=(_,_)=>{naming.Visibility=Visibility.Visible;spaceName.Focus();spaceName.SelectAll();};
        var message = Text(""); Action reload = () => { };
        var deleteSpace=new Button {Content="删除此空间"};SettingsTheme.Danger(deleteSpace);spaceActions.Children.Add(deleteSpace);
        deleteSpace.Click+=async(_,_)=>
        {
            var key=app.ActiveSpaceKey;var name=app.Identity.SpaceName;
            if(ModernDialog.Show(this,"删除“"+name+"”？\n\n"+AppRuntime.DeleteSpaceNotice,"删除空间",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            IsEnabled=false;
            try { await app.DeleteSpaceAsync(key);reload();message.Text="已移除网络连接，全部内容保留。"; }
            catch(Exception ex){ModernDialog.Show(this,ex.Message,"删除未完成");}
            finally { IsEnabled=true; }
        };
        var migration = new Button { Content = "升级为统一空间" }; panel.Children.Add(migration);
        migration.Click += (_, _) =>
        {
            if (ModernDialog.Show(this, "将在本机创建统一空间，旧设备需要使用新的授权码重新加入。\n\n" + DeviceIdentity.JoinNotice, "升级连接规则", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { app.Identity.EnsureSpace(true); reload(); } catch (Exception ex) { message.Text = ex.Message; }
        };
        var members = new StackPanel(); panel.Children.Add(Card(members));
        var statusLabels = new Dictionary<string, TextBlock>();
        var sync = new Button { Content = "立即同步", HorizontalAlignment = HorizontalAlignment.Right };
        sync.Click += (_, _) => { app.Node.RequestSync(); message.Text = "已开始寻找在线成员；离线设备连接后会自动补同步。"; };
        var invitePanel = new StackPanel(); var invite = new Expander { Header = "＋ 添加设备", Content = invitePanel }; panel.Children.Add(Card(invite));
        invitePanel.Children.Add(Text("让另一台设备扫码或粘贴授权码加入。5 分钟有效，仅供一台设备使用；请保持本机在线。"));
        var generate = new Button { Content = "生成授权码与二维码" }; invitePanel.Children.Add(generate);
        var qr = new Image { Width = 280, Height = 280, Margin = new Thickness(0, 12, 0, 12), Visibility = Visibility.Collapsed }; invitePanel.Children.Add(qr);
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        var code = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; invitePanel.Children.Add(code);
        var expiry = Text(""); invitePanel.Children.Add(expiry); DateTimeOffset? expires = null;
        generate.Click += async (_, _) =>
        {
            generate.IsEnabled = false;
            try
            {
                string value = app.Node.CreateInvite(); code.Text = value; expires = DateTimeOffset.UtcNow.AddMinutes(5);
                var bitmap = await Task.Run(() => { using var stream = new MemoryStream(InviteQr.Png(value)); var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.StreamSource = stream; b.EndInit(); b.Freeze(); return b; });
                if (code.Text == value) { qr.Source = bitmap; qr.Visibility = Visibility.Visible; }
            }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { generate.IsEnabled = true; }
        };
        var inviteActions = new StackPanel { Orientation = Orientation.Horizontal }; invitePanel.Children.Add(inviteActions);
        var copy = new Button { Content = "复制授权码" }; inviteActions.Children.Add(copy); copy.Click += (_, _) => { if (code.Text.Length > 0) Clipboard.SetText(code.Text); };
        void ClearInvite() { app.Identity.CancelInvite(); code.Clear(); qr.Source = null; qr.Visibility = Visibility.Collapsed; expires = null; expiry.Text = ""; }
        var cancel = new Button { Content = "作废" }; inviteActions.Children.Add(cancel); cancel.Click += (_, _) => ClearInvite();
        var joinPanel = new StackPanel(); var join = new Expander { Header = "加入已有空间", Content = joinPanel }; panel.Children.Add(Card(join));
        joinPanel.Children.Add(Text(AppRuntime.JoinNotice));
        var input = new TextBox { TextWrapping = TextWrapping.Wrap, MaxLength = 8192, MinHeight = 55, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; joinPanel.Children.Add(input);
        var loadQr = new Button { Content = "从二维码图片读取授权码" }; joinPanel.Children.Add(loadQr);
        loadQr.Click += async (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "二维码图片|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var value = await Task.Run(() =>
                {
                    using var file = File.OpenRead(dialog.FileName);
                    var header = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
                    if (header.PixelWidth >= header.PixelHeight) source.DecodePixelWidth = Math.Min(1800, header.PixelWidth); else source.DecodePixelHeight = Math.Min(1800, header.PixelHeight);
                    source.UriSource = new Uri(dialog.FileName); source.EndInit();
                    var pixels = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0); var bytes = new byte[pixels.PixelWidth * pixels.PixelHeight * 4]; pixels.CopyPixels(bytes, pixels.PixelWidth * 4, 0);
                    return InviteQr.Decode(new ZXing.RGBLuminanceSource(bytes, pixels.PixelWidth, pixels.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32));
                });
                if (value is null) throw new InvalidDataException("未找到 LanTodo 空间二维码，请选择完整清晰的图片。"); input.Text = value;
            }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        var address = new TextBox { MaxLength = 300, ToolTip = "例如 192.168.1.10:42851；通常留空即可" };
        joinPanel.Children.Add(new Expander { Header = "高级：邀请设备地址（通常无需填写）", Content = address });
        var confirm = new CheckBox { Content = "我了解本机全部内容会与这些设备同步", Margin = new Thickness(0, 12, 0, 8) }; joinPanel.Children.Add(confirm);
        var pair = new Button { Content = "加入并自动同步" }; joinPanel.Children.Add(pair);
        pair.Click += async (_, _) =>
        {
            if (confirm.IsChecked != true) { message.Text = "请先确认上方的空间说明。"; return; }
            pair.IsEnabled = false; message.Text = "正在连接邀请设备…";
            try { await app.JoinSpaceAsync(input.Text, address.Text, operation.Token); input.Clear(); confirm.IsChecked = false; join.IsExpanded = false; message.Text = "已加入空间，成员和数据正在自动同步。"; reload(); }
            catch (Exception ex) { message.Text = ex is OperationCanceledException ? "连接已取消或超时。" : ex.Message; }
            finally { pair.IsEnabled = true; }
        };
        var advanced = new Expander { Header = "高级连接设置", Content = new NasSettingsPanel(app) }; panel.Children.Add(Card(advanced));
        string? advancedSpace=null;
        var leave = new Button { Content = "退出当前连接空间", HorizontalAlignment = HorizontalAlignment.Left }; panel.Children.Add(leave);
        leave.Click += (_, _) =>
        {
            if (ModernDialog.Show(this, "退出后停止与此空间同步，本机清单和历史仍会保留。其他在线成员将收到退出记录，离线成员稍后收到。", "退出空间", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { app.Identity.LeaveSpace(); ClearInvite(); reload(); } catch (Exception ex) { message.Text = ex.Message; }
        };
        var create = new Button { Content = "＋ 新建空间", HorizontalAlignment = HorizontalAlignment.Left }; spaceActions.Children.Add(create);
        spaceActions.Children.Remove(deleteSpace);spaceActions.Children.Add(deleteSpace);
        create.Click += async (_, _) => { try { await app.CreateSpaceAsync("新空间"); reload(); naming.Visibility=Visibility.Visible; spaceName.Focus(); spaceName.SelectAll(); } catch (Exception ex) { message.Text = ex.Message; } };
        panel.Children.Add(message);
        void RefreshStatus()
        {
            foreach (var (id, label) in statusLabels) label.Text = app.Node.DeviceState(id);
            if (expires is { } end)
            {
                var seconds = (int)(end - DateTimeOffset.UtcNow).TotalSeconds;
                if (seconds <= 0) { ClearInvite(); expiry.Text = "授权码已过期，请重新生成。"; }
                else expiry.Text = $"剩余 {seconds / 60}:{seconds % 60:00} · 仅供一台设备使用";
            }
        }
        void Reload()
        {
            var space = app.Identity.Space; bool active = space?.Contains(app.Identity.Id) == true;
            if(advancedSpace!=app.ActiveSpaceKey){advanced.Content=new NasSettingsPanel(app);advancedSpace=app.ActiveSpaceKey;}
            spacePicker.Content=app.Identity.SpaceName+"  ▾";
            spaceName.Text=app.Identity.SpaceName;
            summary.Text=active ? $"{space!.Members.Length} 台设备 · "+app.Identity.Name+"（本机）" : "已退出 · 本机内容仍保留";
            migration.Visibility=space is null?Visibility.Visible:Visibility.Collapsed;
            generate.IsEnabled=active;renameSpace.IsEnabled=active;leave.Visibility=active?Visibility.Visible:Visibility.Collapsed;

            if(sync.Parent is Panel oldHeader)oldHeader.Children.Remove(sync);
            members.Children.Clear(); statusLabels.Clear();
            var memberHeading=new DockPanel();DockPanel.SetDock(sync,Dock.Right);memberHeading.Children.Add(sync);memberHeading.Children.Add(Text("空间成员",18));members.Children.Add(memberHeading);
            members.Children.Add(Text(app.Identity.Name+" · 本机",14));
            foreach (var device in app.Identity.Devices)
            {
                var row = new DockPanel(); var remove = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
                var labels = new StackPanel {Margin=new Thickness(0,6,14,6)}; labels.Children.Add(Text(device.Name, 16)); var state = Text("", 12); labels.Children.Add(state); statusLabels[device.Id] = state; row.Children.Add(labels); members.Children.Add(row);
                remove.Click += (_, _) => { if (ModernDialog.Show(this, $"将 {device.Name} 从整个空间移除？\n所有成员会同步此决定；对方本机已有数据仍保留。重新加入需要新授权码。", "移除空间成员", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { try { app.Identity.Revoke(device.Id); reload(); } catch (Exception ex) { message.Text = ex.Message; } } };
            }
            if (app.Identity.Devices.Length == 0) members.Children.Add(Text("目前只有本机。添加设备后，无需逐台配对。"));
            RefreshStatus();
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => RefreshStatus();
        void TrustChanged() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (IsVisible) reload(); }); }
        void SpaceChanged(){ClearInvite();input.Clear();naming.Visibility=Visibility.Collapsed;reload();}
        app.ActiveSpaceChanged+=SpaceChanged;
        app.MembershipChanged += TrustChanged;
        Closed += (_, _) => { operation.Cancel(); ClearInvite(); timer.Stop(); app.MembershipChanged -= TrustChanged;app.ActiveSpaceChanged-=SpaceChanged; };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { operation.Dispose(); operation = new(); reload(); timer.Start(); }
            else { operation.Cancel(); timer.Stop(); ClearInvite(); input.Clear(); }
        };
        reload = Reload; reload();
    }
}
