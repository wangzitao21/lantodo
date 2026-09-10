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
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
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
            var input=(TextBox)main.FindName("QuickInput");Check(Math.Abs(input.ActualHeight-40)<1,"Composer height changed");
            var date=(DateSelector)main.FindName("DateFilter");
            date.SelectedDate=new DateTime(2026,9,15);Check(((StackPanel)main.FindName("ItemsPanel")).Children.Count==1,"Date filter failed");
            date.SelectedDate=null;
            Render(main,"windows-compact.png",840,540);
            ((Button)main.FindName("DoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(main,"windows-completed.png",1120,740);
            Check(((Button)main.FindName("DeleteCompletedButton")).Visibility==Visibility.Visible,"Completed deletion unavailable");
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
            try { CheckEscape(storage); CheckEscape(devices); }
            finally { main.Close(); }
            Console.WriteLine("PASS: window style, compact composer, date filter, completed controls; ten offscreen UI images including NAS settings rendered; both settings windows close with Escape from a textbox and reopen twice.");
            return 0;

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
        finally { runtime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        void Render(Window window,string file,double width,double height)
        {
            var content=(FrameworkElement)window.Content;
            content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
            content.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, window.FontSize);
            if(content is Grid grid)grid.Background=window.Background;
            RenderElement(content,file,width,height);
        }
        void RenderElement(FrameworkElement element,string file,double width,double height)
        {
            element.Measure(new Size(width,height));element.Arrange(new Rect(0,0,width,height));element.UpdateLayout();
            var image=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);
            var background=new DrawingVisual();using(var context=background.RenderOpen())context.DrawRectangle(file.Contains("calendar")?Brushes.White:new SolidColorBrush(Color.FromRgb(245,247,250)),null,new Rect(0,0,width,height));
            image.Render(background);image.Render(element);
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
