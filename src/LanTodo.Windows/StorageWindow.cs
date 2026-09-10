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
        panel.Children.Add(new TextBlock { Text = "数据存储位置", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,14) });
        var location = new TextBox { Text = app.Store.Database.FilePath, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.White }; panel.Children.Add(location);
        IsVisibleChanged += (_, _) => location.Text = app.Store.Database.FilePath;
        AddButton("打开数据文件夹", () => { Process.Start(new ProcessStartInfo(app.Store.Root) { UseShellExecute = true }); return Task.CompletedTask; });
        AddButton("更改数据位置…", async () =>
        {
            var chooser = new OpenFolderDialog { Title = "选择存放数据库的文件夹", Multiselect = false };
            if (chooser.ShowDialog(this) != true) return;
            if (ModernDialog.Show(this,$"将完整清单、历史和配对迁移到：\n{chooser.FolderName}\n\n校验成功后立即切换位置，并移除原数据库。目标不能已有LanTodo数据。","更改数据位置",MessageBoxButton.YesNo,MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            progress.Text = "正在校验和迁移，请稍候…";
            await ((App)Application.Current).MigrateAsync(chooser.FolderName);
            location.Text = app.Store.Database.FilePath; progress.Text = "数据库已移动，新位置立即生效。";
        });
        panel.Children.Add(new TextBlock { Text = "SQLite 数据库 · 清单、历史、设备身份和配对均保存在这一个文件中。默认与程序放在一起；更改位置后，程序旁只增加一个位置指引文件。", Foreground = Brushes.SlateGray, Margin = new Thickness(0,12,0,22) });
        panel.Children.Add(new TextBlock { Text = "备份与恢复", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,12) });
        AddButton("导出清单与全部历史", async () =>
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
        panel.Children.Add(new TextBlock { Text = "导出为 ZIP，内含 JSON 格式的全部清单历史（含删除和冲突），便于跨设备合并恢复。它不是数据库镜像，不含设备身份、配对和设置。备份未加密，请保存到独立存储位置。", FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0,12,0,0) });
        panel.Children.Add(progress);
        void AddButton(string label, Func<Task> action)
        {
            var button = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Left }; panel.Children.Add(button);
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
