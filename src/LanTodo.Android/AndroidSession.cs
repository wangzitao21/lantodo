using Android.Content;
using Android.OS;
using LanTodo.Core;

namespace LanTodo.Android;

internal static class AndroidSession
{
    private static readonly object gate = new();
    private static Task<AppRuntime>? initialization;
    public static bool ActivityVisible { get; set; }
    public static bool ServiceRunning { get; set; }
    public static Task RefreshNetworkAsync() => initialization?.IsCompletedSuccessfully == true ? initialization.Result.SetNetworkAsync(ActivityVisible || ServiceRunning) : Task.CompletedTask;
    public static Task<AppRuntime> GetAsync(Context context)
    {
        var path = Path.Combine(context.FilesDir!.AbsolutePath, "LanTodo"); var name = Build.Model ?? "我的手机";
        lock (gate) return initialization ??= Task.Run(() => new AppRuntime(path,name));
    }
    public static bool BackgroundEnabled(Context context) => context.GetSharedPreferences("preferences", FileCreationMode.Private)!.GetBoolean("backgroundSync", true);
    public static void SetBackground(Context context, bool enabled) => context.GetSharedPreferences("preferences", FileCreationMode.Private)!.Edit()!.PutBoolean("backgroundSync", enabled)!.Apply();
    public static void StartBackground(Context context) => context.StartForegroundService(new Intent(context, typeof(SyncService)));
}
