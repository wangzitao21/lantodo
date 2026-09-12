using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using LanTodo.Core;

namespace LanTodo.Windows;

public partial class MainWindow
{
    private readonly List<Attachment> pendingAttachments = new();
    private bool attachmentBusy;
    public bool HasPendingAttachments => attachmentBusy || pendingAttachments.Count > 0;
    private void SetupAttachments()
    {
        AllowDrop = true;
        PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        PreviewDrop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) { e.Handled = true; await AddPaths(paths); } };
        // TextBox's default paste command rejects image-only clipboards before Pasting is raised.
        // Handle the routed command so Ctrl+V, Shift+Insert and the context-menu paste all work.
        QuickInput.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, async (_, e) =>
        {
            try
            {
                if (Clipboard.ContainsFileDropList()) { e.Handled = true; await AddPaths(Clipboard.GetFileDropList().Cast<string>().ToArray()); }
                else if (Clipboard.ContainsImage()) { e.Handled = true; await PasteImage(); }
            }
            catch (Exception ex) { ModernDialog.Show(this, ex.Message, "附件未添加"); }
        }, (_, e) =>
        {
            try { if (Clipboard.ContainsFileDropList() || Clipboard.ContainsImage()) { e.CanExecute = true; e.Handled = true; } }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }));
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16)); border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(232,241,243)));
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content); template.VisualTree = border; AttachButton.Template = template;
    }
    private async Task PasteImage()
    {
        if (attachmentBusy) return;
        var bitmap = Clipboard.GetImage(); if (bitmap is null) return;
        if (pendingAttachments.Count >= 32) throw new IOException("每条消息最多 32 个附件。");
        // Clipboard bitmaps have no original encoding; PNG preserves pixels losslessly.
        bitmap.Freeze(); attachmentBusy = true;
        try
        {
            var item = await Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var buffer = new MemoryStream(); encoder.Save(buffer); buffer.Position = 0;
                return app.Store.Attachments.Add(buffer, $"剪贴板-{DateTime.Now:yyyyMMdd-HHmmss}.png", "image");
            });
            pendingAttachments.Add(item); RenderPending();
        }
        finally { attachmentBusy = false; }
    }
    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var files = new MenuItem { Header = "选择图片或文件" }; files.Click += async (_, _) => { var picker = new OpenFileDialog { Multiselect = true }; if (picker.ShowDialog(this) == true) await AddPaths(picker.FileNames); };
        var folder = new MenuItem { Header = "选择文件夹" }; folder.Click += async (_, _) => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(this) == true) await AddPaths([picker.FolderName]); };
        menu.Items.Add(files); menu.Items.Add(folder); menu.PlacementTarget = AttachButton; menu.IsOpen = true;
    }
    private async Task AddPaths(string[] paths)
    {
        if (attachmentBusy) return;
        attachmentBusy = true; AttachButton.IsEnabled = false; StatusText.Text = "正在读取附件原件…";
        try
        {
            if (pendingAttachments.Count + paths.Length > 32) throw new IOException("每条消息最多 32 个附件。");
            foreach (var path in paths) { var item = await Task.Run(() => app.Store.Attachments.AddPath(path)); pendingAttachments.Add(item); RenderPending(); }
        }
        catch (Exception ex) { ModernDialog.Show(this, ex.Message, "附件未添加"); }
        finally { attachmentBusy = false; AttachButton.IsEnabled = true; RefreshStatus(); }
    }
    private void RenderPending()
    {
        SaveComposeDraft();
        PendingPanel.Children.Clear();
        foreach (var item in pendingAttachments.ToArray())
        {
            var chip = new Button { Content = item.Name + "  ×", ToolTip = item.Description + " · 点击移除", MaxWidth = 260, FontSize = 12, Padding = new Thickness(10,5,10,5) };
            chip.Click += (_, _) => { pendingAttachments.Remove(item); RenderPending(); }; PendingPanel.Children.Add(chip);
        }
    }
    private void AddAttachmentView(StackPanel body, Attachment item)
    {
        var available = app.Store.Attachments.Has(item);
        if(item.IsAudio)
        {
            var play=new Button { Content=available?"▶ 播放语音":"语音 · "+app.Store.Attachments.Availability(item),IsEnabled=available,HorizontalAlignment=HorizontalAlignment.Left,ContextMenu=AttachmentMenu(item) };
            play.Click+=(_,_)=>PlayAudio(item);body.Children.Add(play);
        }
        if (available && item.Kind == "image")
        {
            var image = new Image { MaxWidth = 240, MaxHeight = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,8,0,4), Cursor = Cursors.Hand, ContextMenu = AttachmentMenu(item) };
            image.MouseLeftButtonUp += (_, e) => { e.Handled = true; PreviewImage(item); };
            body.Children.Add(image); _ = LoadThumbnail(image, item);
        }
        var button = new Button { Content = new TextBlock { Text = item.Description + (available ? "  · 保存原件" : "  · " + app.Store.Attachments.Availability(item)), TextWrapping = TextWrapping.Wrap }, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10,7,10,7), ContextMenu = AttachmentMenu(item) };
        button.Click += (_, _) => { if (app.Store.Attachments.Has(item)) SaveAttachment(item); else RefreshSoon(); }; body.Children.Add(button);
    }
    private readonly Dictionary<string, BitmapSource> thumbnails = new();
    private readonly System.Threading.SemaphoreSlim thumbnailSlots = new(2);
    private async Task LoadThumbnail(Image image, Attachment item)
    {
        if (thumbnails.TryGetValue(item.Hash, out var cached)) { image.Source = cached; return; }
        await thumbnailSlots.WaitAsync();
        try
        {
            if (!thumbnails.TryGetValue(item.Hash, out cached))
            {
                var path = app.Store.Attachments.PathFor(item.Hash);
                cached = await Task.Run(() => { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 320; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); return bitmap; });
                if (thumbnails.Count >= 96) thumbnails.Clear(); thumbnails[item.Hash] = cached;
            }
            image.Source = cached;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.IO.FileFormatException) { }
        finally { thumbnailSlots.Release(); }
    }
    private ContextMenu AttachmentMenu(Attachment item)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "用默认程序打开" };
        var folder = new MenuItem { Header = "打开文件所在文件夹" };
        var save = new MenuItem { Header = "另存为…" };
        open.Click += (_, _) => Run(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app.Store.Attachments.PathFor(item.Hash)) { UseShellExecute = true }));
        folder.Click += (_, _) => Run(() => { var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.Arguments = "/select,\"" + app.Store.Attachments.PathFor(item.Hash) + "\""; System.Diagnostics.Process.Start(start); });
        save.Click += (_, _) => SaveAttachment(item);
        menu.Items.Add(open); menu.Items.Add(folder); menu.Items.Add(save);
        menu.Opened += (_, _) => { var exists = app.Store.Attachments.Has(item); open.IsEnabled = save.IsEnabled = exists; folder.IsEnabled = Directory.Exists(Path.GetDirectoryName(app.Store.Attachments.PathFor(item.Hash))); if (!exists) RefreshSoon(); };
        return menu;
    }
    private void SaveAttachment(Attachment item) => Run(() =>
    {
        var picker = new SaveFileDialog { FileName = item.Name, Title = "保存附件原件" };
        if (picker.ShowDialog(this) == true) File.Copy(app.Store.Attachments.PathFor(item.Hash), picker.FileName, true);
    });
    private void PreviewImage(Attachment item) => Run(() =>
    {
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(app.Store.Attachments.PathFor(item.Hash));bitmap.EndInit();bitmap.Freeze();
        var panel=new DockPanel();var toolbar=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(8) };DockPanel.SetDock(toolbar,Dock.Bottom);panel.Children.Add(toolbar);
        var picture=new Image {Source=bitmap,Stretch=Stretch.Fill,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,ContextMenu=AttachmentMenu(item)};
        var scroll=new ScrollViewer {Content=picture,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Brushes.WhiteSmoke};panel.Children.Add(scroll);
        var zoomText=new TextBlock {VerticalAlignment=VerticalAlignment.Center,MinWidth=56,TextAlignment=TextAlignment.Center};
        double zoom=1;
        void SetZoom(double value)
        {
            var old=zoom;zoom=Math.Clamp(value,0.05,8);picture.Width=bitmap.PixelWidth*zoom;picture.Height=bitmap.PixelHeight*zoom;zoomText.Text=$"{zoom:P0}";
            scroll.ScrollToHorizontalOffset((scroll.HorizontalOffset+scroll.ViewportWidth/2)*zoom/old-scroll.ViewportWidth/2);
            scroll.ScrollToVerticalOffset((scroll.VerticalOffset+scroll.ViewportHeight/2)*zoom/old-scroll.ViewportHeight/2);
        }
        void Fit()=>SetZoom(Math.Min(1,Math.Min(Math.Max(1,scroll.ViewportWidth-8)/bitmap.PixelWidth,Math.Max(1,scroll.ViewportHeight-8)/bitmap.PixelHeight)));
        void ActionButton(string label,Action action){var button=new Button{Content=label};button.Click+=(_,_)=>action();toolbar.Children.Add(button);}
        ActionButton("−",()=>SetZoom(zoom/1.25));toolbar.Children.Add(zoomText);ActionButton("＋",()=>SetZoom(zoom*1.25));ActionButton("适应窗口",Fit);ActionButton("100%",()=>SetZoom(1));ActionButton("保存原件",()=>SaveAttachment(item));
        scroll.PreviewMouseWheel+=(_,e)=>{SetZoom(zoom*(e.Delta>0?1.25:0.8));e.Handled=true;};
        Point? drag=null;double startX=0,startY=0;
        scroll.PreviewMouseLeftButtonDown+=(_,e)=>{drag=e.GetPosition(scroll);startX=scroll.HorizontalOffset;startY=scroll.VerticalOffset;scroll.CaptureMouse();scroll.Cursor=Cursors.Hand;e.Handled=true;};
        scroll.PreviewMouseMove+=(_,e)=>{if(drag is {} origin){var current=e.GetPosition(scroll);scroll.ScrollToHorizontalOffset(startX+origin.X-current.X);scroll.ScrollToVerticalOffset(startY+origin.Y-current.Y);}};
        scroll.PreviewMouseLeftButtonUp+=(_,e)=>{drag=null;scroll.ReleaseMouseCapture();scroll.Cursor=Cursors.Arrow;e.Handled=true;};
        var window=new Window{Title=item.Name,Owner=this,Width=900,Height=700,Content=panel,WindowStartupLocation=WindowStartupLocation.CenterOwner};window.Loaded+=(_,_)=>Fit();window.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape)window.Close();};window.ShowDialog();
    });
    private void PlayAudio(Attachment item) => Run(() =>
    {
        var player=new MediaPlayer();var panel=new StackPanel{Margin=new Thickness(24)};
        panel.Children.Add(new TextBlock{Text=item.Name,TextWrapping=TextWrapping.Wrap,FontSize=16});
        var label=new TextBlock{Text="正在加载语音…",Margin=new Thickness(0,12,0,12)};panel.Children.Add(label);
        var play=new Button{Content="暂停",IsEnabled=false};panel.Children.Add(play);bool playing=true;
        var window=new Window{Title="语音消息",Owner=this,Width=420,Height=240,Content=panel,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        player.MediaOpened+=(_,_)=>{label.Text=player.NaturalDuration.HasTimeSpan?$"语音 · {player.NaturalDuration.TimeSpan.TotalSeconds:0}秒":"语音";play.IsEnabled=true;player.Play();};
        player.MediaEnded+=(_,_)=>{player.Position=TimeSpan.Zero;player.Pause();playing=false;play.Content="重新播放";};
        player.MediaFailed+=(_,args)=>{label.Text="无法播放："+args.ErrorException.Message;play.IsEnabled=false;};
        play.Click+=(_,_)=>{if(playing)player.Pause();else player.Play();playing=!playing;play.Content=playing?"暂停":"播放";};
        window.Closed+=(_,_)=>player.Close();window.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape)window.Close();};
        player.Open(new Uri(app.Store.Attachments.PathFor(item.Hash)));window.ShowDialog();
    });
    private Window? settingsWindow;
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (settingsWindow is not null) { settingsWindow.ShowDialog(); return; }
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(SettingsTheme.Heading("设置"));
        panel.Children.Add(SettingsTheme.Hint("管理本机偏好、空间与数据。"));
        var profile=new StackPanel();panel.Children.Add(SettingsTheme.Card(profile));
        var connections=new StackPanel();panel.Children.Add(SettingsTheme.Card(connections));
        profile.Children.Add(new TextBlock { Text = "本机昵称（其他设备也会看到）" });
        var nickname = new TextBox { Text = app.Identity.Name, MaxLength = 100 };
        profile.Children.Add(nickname);
        var rename = new Button { Content = "保存昵称" }; rename.Click += (_, _) => Run(() => app.RenameSelf(nickname.Text)); profile.Children.Add(rename);
        var source = new CheckBox { Content = "在消息右侧显示最后修改设备", IsChecked = app.ShowDeviceSource };
        source.Click += (_, _) => Run(() => { app.SetShowDeviceSource(source.IsChecked == true); Refresh(); }); profile.Children.Add(source);
        connections.Children.Add(SettingsTheme.Hint("所有网络空间共用清单，在线设备自动同步。"));
        var devices = new Button { Content = "设备与同步" }; devices.Click += Devices_Click; connections.Children.Add(devices);
        var backup = new Button { Content = "备份与恢复" }; backup.Click += Backup_Click; connections.Children.Add(backup);
        panel.Children.Add(new TextBlock { Text = "LanTodo v1.0.1", Foreground = Brushes.Gray, Margin = new Thickness(0,20,0,0) });
        var window = new Window { Title = "设置", Owner = this, Width = 540, Height = 650, Content = new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.IsVisibleChanged += (_, _) => { if (window.IsVisible) nickname.Text = app.Identity.Name; };
        settingsWindow = Prepare(window); window.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) window.Hide(); }; window.ShowDialog();
    }
}
