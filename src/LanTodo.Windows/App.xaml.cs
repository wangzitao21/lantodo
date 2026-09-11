using System.IO;
using System.Windows;
using LanTodo.Core;

namespace LanTodo.Windows;

public partial class App : Application
{
    private AppRuntime? runtime;
    private SingleInstance? instance;
    private System.Windows.Forms.NotifyIcon? tray;
    public bool IsExiting { get; private set; }
    private bool explicitProfile;
    private bool migrating;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var dataArg = Array.IndexOf(e.Args, "--data-dir");
            explicitProfile = dataArg >= 0 && dataArg + 1 < e.Args.Length;
            string path = explicitProfile ? Path.GetFullPath(e.Args[dataArg + 1]) : DataLocation.PortableRoot;
            instance = new SingleInstance(path);
            if (!instance.IsPrimary)
            {
                await instance.SignalAsync(e.Args.Contains("--quit") ? (byte)2 : (byte)1);
                Shutdown(); return;
            }
            if (e.Args.Contains("--quit")) { Shutdown(); return; }
            if (!explicitProfile && !File.Exists(Path.Combine(path, SqliteProfile.FileName)))
            {
                var previous = File.Exists(DataLocation.ConfigPath) ? DataLocation.Read() : DataLocation.PreviousProfile();
                if (previous is not null && !Path.GetFullPath(previous).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    using var previousInstance = new SingleInstance(previous);
                    if (!previousInstance.IsPrimary) throw new IOException("请先退出使用旧数据目录的 LanTodo，再启动新版。");
                    await using var previousApp = new AppRuntime(previous,Environment.MachineName);
                    await previousApp.MoveToAsync(path, () => DataLocation.Save(path));
                }
            }
            runtime = new(path, Environment.MachineName);
            MainWindow = new MainWindow(runtime);
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开LanTodo", null, (_, _) => ShowWindow());
            menu.Items.Add("立即同步", null, (_, _) => runtime.RequestSync());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出LanTodo", null, async (_, _) => await ExitAsync());
            tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
                Text = "LanTodo · 后台自动同步", Visible = true, ContextMenuStrip = menu
            };
            tray.DoubleClick += (_, _) => ShowWindow();
            MainWindow.Closing += (_, args) => { if (!IsExiting) { args.Cancel = true; if (!migrating) MainWindow.Hide(); } };
            instance.Listen(command => Dispatcher.BeginInvoke(async () => { if (command == 2) await ExitAsync(); else ShowWindow(); }));
            MainWindow.Show();
            await runtime.SetNetworkAsync(true);
        }
        catch (Exception ex)
        {
            ModernDialog.Show("无法打开本地数据。请检查是否已运行另一个窗口；如数据损坏，请先保留整个数据目录。\n\n" + ex.Message, "LanTodo · 数据保护", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private void ShowWindow()
    {
        if (IsExiting || MainWindow is null) return;
        MainWindow.Show(); MainWindow.WindowState = WindowState.Normal; MainWindow.Activate();
    }
    private async Task ExitAsync()
    {
        if (IsExiting || migrating) return;
        IsExiting = true;
        // Await while the dispatcher is still running; never block OnExit on UI-captured continuations.
        try { if (runtime is not null) await runtime.DisposeAsync(); }
        catch (Exception ex) { ModernDialog.Show("退出时遇到连接问题，已提交的数据仍保存在本机。\n" + ex.Message, "LanTodo"); }
        finally { Shutdown(); }
    }
    public async Task MigrateAsync(string destination)
    {
        if (runtime is null || migrating || IsExiting) return;
        if (MainWindow is MainWindow editor && editor.HasPendingAttachments) throw new IOException("请先发送或移除待发送附件，再更改数据位置。");
        migrating = true; MainWindow!.IsEnabled = false;
        var previousConfig = File.Exists(DataLocation.ConfigPath) ? File.ReadAllBytes(DataLocation.ConfigPath) : null;
        SingleInstance? nextInstance = null; bool moved = false;
        try
        {
            nextInstance = new SingleInstance(destination);
            if (!nextInstance.IsPrimary) throw new IOException("目标目录已被另一个窗口使用。");
            await runtime.SetNetworkAsync(false);
            await runtime.MoveToAsync(destination, () =>
            {
                if (!explicitProfile) DataLocation.Save(destination);
            });
            moved = true;
            instance?.Dispose(); instance = nextInstance; nextInstance = null;
            instance.Listen(command => Dispatcher.BeginInvoke(async () => { if (command == 2) await ExitAsync(); else ShowWindow(); }));
        }
        catch
        {
            if (!explicitProfile && !moved)
            {
                if (previousConfig is not null) AtomicFile.Write(DataLocation.ConfigPath, previousConfig, true);
                else if (File.Exists(DataLocation.ConfigPath)) File.Delete(DataLocation.ConfigPath);
            }
            throw;
        }
        finally
        {
            nextInstance?.Dispose(); migrating = false; MainWindow.IsEnabled = true;
            await runtime.SetNetworkAsync(true);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        instance?.Dispose();
        base.OnExit(e);
    }
}
