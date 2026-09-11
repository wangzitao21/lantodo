using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using LanTodo.Core;
using LanTodo.Windows;

internal static class Program
{
    // Synthetic views only: render offscreen and exercise keyboard routing in offscreen test windows.
    [STAThread]
    private static int Main(string[] args)
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        bool renderUnavailable = false;
        var profile = Path.Combine(output,"layout-profile");
        var application = new App(); application.InitializeComponent();
        var runtime = new AppRuntime(profile,"界面测试");
        try
        {
            if (runtime.Store.List().Length == 0)
            {
                runtime.Store.Save(runtime.Identity.Id,runtime.Identity.Name,new TodoData("准备明天的项目讨论","整理想法，确认下一步安排","2026-09-15","09:30"));
                runtime.Store.Save(runtime.Identity.Id,runtime.Identity.Name,new TodoData("记下一个转瞬即逝的灵感"));
                runtime.Store.Save(runtime.Identity.Id,runtime.Identity.Name,new TodoData("完成今天的阅读","读完后记录三个要点",Completed:true));
            }
            var main = new MainWindow(runtime);
            Check(main.Background is SolidColorBrush color && color.Color == Color.FromRgb(245,247,250),"Derived window style was not applied");
            Render(main,"windows-home.png",1120,740);
            var itemsTop=((StackPanel)main.FindName("ItemsPanel")).TranslatePoint(new Point(0,0),(UIElement)main.Content).Y;
            var navigationTop=((Button)main.FindName("AllButton")).TranslatePoint(new Point(0,0),(UIElement)main.Content).Y;
            Check(Math.Abs(itemsTop-navigationTop)<1.1,$"List top {itemsTop} is not aligned with navigation {navigationTop}");
            Check(main.FindName("TodayButton") is null && main.FindName("TrashButton") is Button,"Today navigation remains or trash is missing");
            var brandIcon=(Image)main.FindName("BrandIcon");var brandTitle=(TextBlock)main.FindName("BrandTitle");
            var iconCenter=brandIcon.TranslatePoint(new Point(0,brandIcon.ActualHeight/2),(UIElement)main.Content).Y;
            var titleCenter=brandTitle.TranslatePoint(new Point(0,brandTitle.ActualHeight/2),(UIElement)main.Content).Y;
            Check(Math.Abs(iconCenter-titleCenter)<1,"Brand title and icon are not vertically centered");
            var input=(TextBox)main.FindName("QuickInput");Check(Math.Abs(input.ActualHeight-40)<1,"Composer height changed");
            var date=(DateSelector)main.FindName("DateFilter");
            date.SelectedDate=new DateTime(2026,9,15);Check(((StackPanel)main.FindName("ItemsPanel")).Children.Count==1,"Date filter failed");
            date.SelectedDate=null;
            Render(main,"windows-compact.png",840,540);
            var settings=(Button)main.FindName("SettingsButton");
            var settingsPosition=settings.TranslatePoint(new Point(0,0),(UIElement)main.Content);
            Check(settings.ActualHeight>0 && settingsPosition.Y+settings.ActualHeight<=540,"Settings button clipped in compact layout");
            ((Button)main.FindName("DoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(main,"windows-completed.png",1120,740);
            Check(((Button)main.FindName("DeleteCompletedButton")).Visibility==Visibility.Visible,"Completed deletion unavailable");
            var trashRevision=runtime.Store.Save(runtime.Identity.Id,runtime.Identity.Name,new("回收站里的记录","可以恢复，或彻底清除。",Deleted:true));
            ((Button)main.FindName("TrashButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(main,"windows-trash.png",1120,740);
            Check(((Button)main.FindName("DeleteCompletedButton")).Content.ToString()=="全部清除" && ((StackPanel)main.FindName("Composer")).Visibility==Visibility.Collapsed,"Trash actions or composer incorrect");
            var trashCard=(Border)((StackPanel)main.FindName("ItemsPanel")).Children[0];
            Check(trashCard.Background is SolidColorBrush trashColor && trashColor.Color==Colors.White,"Trash message card should remain white");
            Check(((ScrollViewer)main.FindName("ListScroll")).Background is SolidColorBrush trashSurface && trashSurface.Color==Colors.Transparent,"Trash page should retain the normal surface");
            Check(trashCard.ContextMenu!.Items.OfType<MenuItem>().Any(i=>i.Header.ToString()=="恢复"),"Trash restore unavailable");
            runtime.Store.Purge(runtime.Identity.Id,runtime.Identity.Name,runtime.Store.Trash().Single(t=>t.Id==trashRevision.Body.TodoId));
            ((Button)main.FindName("DoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var completed = runtime.Store.List().Single(t=>t.Data.Completed);
            var editor=new TodoEditor(runtime,completed);Render(editor,"windows-details.png",640,690);
            var storage=new StorageWindow(runtime);Render(storage,"windows-storage.png",620,600);
            var devices=new DevicesWindow(runtime);Render(devices,"windows-devices.png",650,620);
            RenderElement(new NasSettingsPanel(runtime),"windows-nas.png",560,520);
            var confirmation = new ModernDialog("删除全部 3 条已完成内容？删除会同步到其他设备，修改历史仍保留。", "删除已完成内容", true);Render(confirmation,"windows-confirmation.png",460,240);
            var menu = new ContextMenu(); menu.Items.Add(new MenuItem { Header="标记为完成" }); menu.Items.Add(new MenuItem { Header="删除", Foreground=Brushes.IndianRed });RenderElement(menu,"windows-context-menu.png",185,106);
            // Render the actual calendar tree without opening a native popup.
            typeof(DateSelector).GetMethod("RenderCalendar",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(date,null);
            var calendar=(StackPanel)typeof(DateSelector).GetField("calendar",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(date)!;
            RenderElement(calendar,"windows-calendar.png",266,352);
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = -32000; main.Top = -32000; main.ShowInTaskbar = false; main.Show();
            input.Text="跨网络保留的草稿";
            runtime.CreateSpaceAsync("公司网络").GetAwaiter().GetResult();
            Check(input.Text=="跨网络保留的草稿" && ((StackPanel)main.FindName("ItemsPanel")).Children.Count==1,"Network selection changed the draft or filtered list");
            input.Text="";
            try { CheckSettings(); CheckSingleClick(); CheckEscape(storage); CheckEscape(devices); CheckEscape(editor); }
            finally { main.Close(); }
            Console.WriteLine("PASS: window style, compact composer, date filter, completed controls; both settings windows close with Escape from a textbox and reopen twice.");
            Console.WriteLine(renderUnavailable ? "SKIP: this Windows session returns transparent WPF raster output; geometry and keyboard checks passed, visual screenshots require an interactive desktop." : "PASS: WPF raster images exported.");
            return 0;

            void CheckSettings()
            {
                bool rendered=false;
                main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>
                {
                    foreach(var window in application.Windows.OfType<Window>().Where(w=>w.IsVisible && w.Title=="设置").ToArray())
                    {
                        RenderElement((FrameworkElement)window.Content,"windows-settings.png",520,610);
                        rendered=true;window.Hide();
                    }
                });
                settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(rendered,"Settings page did not open");
            }

            void CheckSingleClick()
            {
                bool opened=false;
                main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,()=>
                {
                    foreach(var dialog in application.Windows.OfType<TodoEditor>().Where(w=>w.IsVisible).ToArray())
                    {opened=true;dialog.Close();}
                });
                var card=(Border)((StackPanel)main.FindName("ItemsPanel")).Children[0];
                card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.MouseUpEvent});
                Check(opened,"A single click did not open the message editor");
            }

            void CheckEscape(Window window)
            {
                // Use the actual cached-window closing policy from the main window.
                typeof(MainWindow).GetMethod("Prepare", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .MakeGenericMethod(window.GetType()).Invoke(main, new object[] { window });
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000; window.Top = -32000; window.ShowInTaskbar = false;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    bool escaped = false, otherKeyKeptOpen = false;
                    Exception? failure = null;
                    var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    timeout.Tick += (_, _) => window.Hide();
                    timeout.Start();
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    {
                        try
                        {
                            if (window is DevicesWindow && ((ScrollViewer)window.Content).Content is StackPanel sections)
                                foreach (var section in sections.Children.OfType<Border>())
                                    if (section.Child is Expander expander && expander.Header?.ToString() == "加入已有空间") expander.IsExpanded = true;
                            window.UpdateLayout();
                            var input = FindTextBox(window)!;
                            Check(input is not null, "Settings window has no textbox");
                            input!.Focus();
                            var source = PresentationSource.FromVisual(window)!;
                            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F6) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                            otherKeyKeptOpen = window.IsVisible;
                            var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                            input.RaiseEvent(key);
                            escaped = key.Handled && !window.IsVisible;
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { window.Hide(); }
                    });
                    window.ShowDialog(); timeout.Stop();
                    if (failure is not null) throw failure;
                    Check(otherKeyKeptOpen && escaped, $"{window.Title}: Escape did not close the reusable dialog on opening {attempt + 1}");
                }
            }
        }
        finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); application.Shutdown(); }
        void Render(Window window,string file,double width,double height)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -32000; window.Top = -32000;
            window.ShowInTaskbar = false; window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var content=(FrameworkElement)window.Content;
            content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
            content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
            if(content is Grid grid)grid.Background=window.Background;
            RenderElement(content,file,width,height);
            window.Hide();
        }
        void RenderElement(FrameworkElement element,string file,double width,double height)
        {
            element.Measure(new Size(width,height));element.Arrange(new Rect(0,0,width,height));element.UpdateLayout();
            var image=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);
            var drawing=new DrawingVisual();
            using(var context=drawing.RenderOpen())
            {
                context.DrawRectangle(file.Contains("calendar")?Brushes.White:new SolidColorBrush(Color.FromRgb(245,247,250)),null,new Rect(0,0,width,height));
                context.DrawRectangle(new VisualBrush(element){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,width,height)},null,new Rect(0,0,width,height));
            }
            image.Render(drawing);
            var pixels = new byte[(int)width * (int)height * 4]; image.CopyPixels(pixels, (int)width * 4, 0);
            if (!pixels.Where((_,i) => i % 4 == 3).Any(a => a != 0))
            {
                renderUnavailable = true;
                if (args.Contains("--require-images")) throw new Exception("Rendered image is transparent: " + file);
                File.Delete(Path.Combine(output,file)); // Never leave a stale or empty image labelled as current evidence.
                return;
            }
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream=File.Create(Path.Combine(output,file));encoder.Save(stream);
        }
    }
    private static void Check(bool result,string message) { if(!result)throw new Exception(message); }
    private static TextBox? FindTextBox(DependencyObject parent)
    {
        if (parent is TextBox text) return text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindTextBox(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
}
