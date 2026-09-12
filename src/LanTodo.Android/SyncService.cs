using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using LanTodo.Core;

namespace LanTodo.Android;

[Service(Name = "app.lantodo.local.SyncService", Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class SyncService : Service
{
    private const int NotificationId = 1042;
    private const string ChannelId = "lan-sync";
    private AppRuntime? app;
    private Task? running;
    private bool destroyed;
    public override IBinder? OnBind(Intent? intent) => null;
    public override void OnCreate()
    {
        base.OnCreate();
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "局域网后台同步", NotificationImportance.Low));
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(SyncService)).SetAction("stop"), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("LanTodo · 后台同步已开启")!
            .SetContentText("有修改时同步；点击回到清单")!
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetContentIntent(open)!.SetOngoing(true)!
            .AddAction(new Notification.Action.Builder(null, "停止后台同步", stop).Build())!
            .Build();
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeConnectedDevice);
        else StartForeground(NotificationId, notification);
    }
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "stop") AndroidSession.SetBackground(this, false);
        if (!AndroidSession.BackgroundEnabled(this)) { StopSelf(); return StartCommandResult.NotSticky; }
        AndroidSession.ServiceRunning = true;
        running ??= RunAsync();
        return StartCommandResult.Sticky;
    }
    private async Task RunAsync()
    {
        try
        {
            if (app is null)
            {
                app = await AndroidSession.GetAsync(this);
                if (destroyed) return;

            }
            AndroidSession.ServiceRunning = true;
            await AndroidSession.RefreshNetworkAsync();
        }
        catch (Exception ex) { global::Android.Util.Log.Error("LanTodo", "Background sync: " + ex.GetType().Name); StopSelf(); }
    }
    public override async void OnDestroy()
    {
        destroyed = true;
        AndroidSession.ServiceRunning = false;
        base.OnDestroy();
        try { await AndroidSession.RefreshNetworkAsync(); }
        catch (Exception ex) { global::Android.Util.Log.Warn("LanTodo", "Stopping sync: " + ex.GetType().Name); }
    }
}
