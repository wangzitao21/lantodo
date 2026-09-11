using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using LanTodo.Core;
using Microsoft.Win32;

namespace LanTodo.Windows;

public sealed class StorageWindow : Window
{
    private readonly TextBlock progress = new() { Foreground = Brushes.SlateGray, Margin = new Thickness(0,14,0,0) };
    public StorageWindow(AppRuntime app)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Title = "备份与恢复"; Width = 620; Height = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(SettingsTheme.Heading("备份与恢复"));
        panel.Children.Add(SettingsTheme.Hint("所有清单、附件与历史的统一备份。"));
        var section=new StackPanel();panel.Children.Add(SettingsTheme.Card(section));
        section.Children.Add(new TextBlock {Text="数据存储位置",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
        var location = new TextBox { Text = app.Store.Database.FilePath, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.White }; section.Children.Add(location);
        IsVisibleChanged += (_, _) => location.Text = app.Store.Database.FilePath;
        AddButton("打开数据文件夹", () => { Process.Start(new ProcessStartInfo(app.Store.Root) { UseShellExecute = true }); return Task.CompletedTask; });
        AddButton("打开附件文件夹", () =>
        {
            System.IO.Directory.CreateDirectory(app.Store.Attachments.DirectoryPath);
            Process.Start(new ProcessStartInfo(app.Store.Attachments.DirectoryPath) { UseShellExecute = true }); return Task.CompletedTask;
        });
        section.Children.Add(new TextBlock { Text = "所有附件统一放在 attachments 文件夹。清单、历史和附件不随网络空间切换；完整搬迁时请保存整个数据目录。", Foreground = Brushes.SlateGray, Margin = new Thickness(0,12,0,22) });
        section=new StackPanel();panel.Children.Add(SettingsTheme.Card(section));
        section.Children.Add(new TextBlock { Text = "备份与恢复", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,12) });
        AddButton("导出全部清单与历史", async () =>
        {
            var chooser = new SaveFileDialog { FileName = $"LanTodo-{DateTime.Now:yyyyMMdd-HHmmss}.lantodo.zip", Filter = "LanTodo 备份|*.zip" };
            if (chooser.ShowDialog(this) != true) return;
            progress.Text = "正在导出…"; await Task.Run(() => app.Store.Backup(chooser.FileName)); progress.Text = "备份已保存。";
        });
        AddButton("从备份合并恢复", async () =>
        {
            var chooser = new OpenFileDialog { Filter = "LanTodo 备份|*.zip" };
            if (chooser.ShowDialog(this) != true || ModernDialog.Show(this,"将合并全部历史，可能产生待确认的冲突。继续？","合并恢复",MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            progress.Text = "正在校验并恢复…"; int count = await Task.Run(() => app.Store.Restore(chooser.FileName)); progress.Text = $"已恢复 {count} 个版本。";
        });
        section.Children.Add(new TextBlock { Text = "ZIP 包含全部清单历史、回收站与可用附件，保留附件失效记录。未下载完成的附件需先同步。不含设备身份、空间成员和设置。备份未加密，请保存到独立存储位置。", FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0,12,0,0) });
        panel.Children.Add(progress);
        void AddButton(string label, Func<Task> action)
        {
            var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Left }; section.Children.Add(button);
            button.Click += async (_, _) =>
            {
                IsEnabled = false;
                try { await action(); }
                catch (Exception ex) { progress.Text = "操作未完成。原数据仍保留。"; ModernDialog.Show(this,ex.Message,"未完成操作"); }
                finally { IsEnabled = true; }
            };
        }
    }
}
