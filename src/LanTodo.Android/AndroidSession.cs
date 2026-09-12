using Android.Content;
using Android.OS;
using Android.Net;
using Android.Net.Wifi;
using LanTodo.Core;

namespace LanTodo.Android;

internal static class AndroidSession
{
    private static readonly object gate = new();
    private static Task<AppRuntime>? initialization;
    private static Context? application;
    private static ConnectivityManager? connectivity;
    private static NetworkObserver? observer;
    private static readonly Dictionary<Network, (bool Local, bool Wifi, bool Mobile, bool Vpn)> networks = new();
    private static WifiManager.MulticastLock? multicast;
    private static Handler? discoveryHandler;
    private static readonly SemaphoreSlim refreshGate = new(1);
    private static Network? boundNetwork;
    private static QuickCaptureInbox? captures;
    private static Handler? sendHandler;
    private static PowerManager.WakeLock? sendWakeLock;
    private static long sendUntil;
    public static QuickCaptureInbox Captures(Context context)
    {
        lock (gate) return captures ??= new QuickCaptureInbox(Path.Combine(context.FilesDir!.AbsolutePath, "quick-captures.json"));
    }
    public static async Task FlushCapturesAsync(Context context)
    {
        var inbox = Captures(context);
        bool pendingSend = inbox.Pending.Length > 0;
        var runtime = await GetAsync(context).ConfigureAwait(false);
        int added = await Task.Run(() => inbox.CommitTo(runtime.Store, runtime.Identity.Id, runtime.Identity.Name)).ConfigureAwait(false);
        if (pendingSend || added > 0) { ProtectRecentSend(); runtime.RequestSync(); }
    }
    public static void ProtectRecentSend()
    {
        sendHandler?.Post(() =>
        {
            if (application is null || !BackgroundEnabled(application)) return;
            var power = (PowerManager)application.GetSystemService(Context.PowerService)!;
            sendWakeLock ??= power.NewWakeLock(WakeLockFlags.Partial, "lantodo:send")!;
            sendWakeLock.SetReferenceCounted(false);
            sendUntil = SystemClock.ElapsedRealtime() + 15000;
            sendWakeLock.Acquire(15000);
            if (initialization?.IsCompletedSuccessfully == true) initialization.Result.SetLowPower(false);
            sendHandler!.RemoveCallbacksAndMessages(null);
            sendHandler.PostDelayed(ReleaseSendWindow, 15000);
            FinishConfirmedSend();
        });
    }
    private static void FinishConfirmedSend()
    {
        if (sendUntil != 0 && initialization?.IsCompletedSuccessfully == true && captures?.Pending.Length is not > 0 && initialization.Result.Status.StartsWith("已同步"))
            ReleaseSendWindow();
    }
    private static void ReleaseSendWindow()
    {
        sendUntil = 0;
        if (sendWakeLock?.IsHeld == true) sendWakeLock.Release();
        if (initialization?.IsCompletedSuccessfully == true) initialization.Result.SetLowPower(!ActivityVisible);
    }
    public static bool ActivityVisible { get; set; }
    public static bool ServiceRunning { get; set; }
    public static bool MobileEnabled(Context context) => context.GetSharedPreferences("preferences", FileCreationMode.Private)!.GetBoolean("mobileSync", false);
    public static void SetMobile(Context context, bool enabled) => context.GetSharedPreferences("preferences", FileCreationMode.Private)!.Edit()!.PutBoolean("mobileSync", enabled)!.Apply();
    public static async Task RefreshNetworkAsync()
    {
        if (initialization?.IsCompletedSuccessfully != true) return;
        await refreshGate.WaitAsync();
        try
        {
            bool available, defaultVpn; Network? local;
            bool mobile = MobileEnabled(application!);
            lock (gate)
            {
                local = networks.Where(p => p.Value.Local).OrderByDescending(p => p.Value.Vpn).ThenByDescending(p => p.Value.Wifi).Select(p => p.Key).FirstOrDefault();
                available = local is not null || mobile && networks.Values.Any(n => n.Mobile);
                defaultVpn = local is not null && networks[local].Vpn && Equals(local, connectivity!.ActiveNetwork);
            }
            // Preserve an active VPN; never bind around it to the underlying Wi-Fi.
            var desired = mobile || defaultVpn ? null : local;
            if (!Equals(boundNetwork, desired))
            {
                // Close old routes before applying the user's transport preference.
                await initialization.Result.SetNetworkAsync(false);
                if (!connectivity!.BindProcessToNetwork(desired))
                { discoveryHandler?.Post(ConfigureDiscovery); return; }
                boundNetwork = desired;
            }
            initialization.Result.SetLowPower(!ActivityVisible && SystemClock.ElapsedRealtime() >= sendUntil);
            await initialization.Result.SetNetworkAsync(available && (ActivityVisible || ServiceRunning));
            discoveryHandler?.Post(ConfigureDiscovery);
            if (!ActivityVisible && !ServiceRunning) sendHandler?.Post(ReleaseSendWindow);
        }
        finally { refreshGate.Release(); }
    }
    public static Task<AppRuntime> GetAsync(Context context)
    {
        var path = Path.Combine(context.FilesDir!.AbsolutePath, "LanTodo"); var name = Build.Model ?? "我的手机";
        lock (gate)
        {
            if (initialization is not null) return initialization;
            application = context.ApplicationContext!;
            discoveryHandler = new Handler(Looper.MainLooper!);
            sendHandler = new Handler(Looper.MainLooper!);
            var inbox = Captures(context);
            initialization = Task.Run(() =>
            {
                // Binder calls and network enumeration must not delay the first UI frame.
                connectivity = (ConnectivityManager)application.GetSystemService(Context.ConnectivityService)!;
                var wifi = (WifiManager?)application.GetSystemService(Context.WifiService);
                multicast = wifi?.CreateMulticastLock("lantodo:discovery"); multicast?.SetReferenceCounted(false);
                observer = new NetworkObserver();
                using var request = new NetworkRequest.Builder()!.RemoveCapability(NetCapability.NotVpn)!.Build();
                connectivity.RegisterNetworkCallback(request!, observer);
#pragma warning disable CA1422
                foreach (var network in connectivity.GetAllNetworks() ?? [])
                    if (connectivity.GetNetworkCapabilities(network) is { } caps)
                        lock (gate) networks[network] = Describe(caps);
#pragma warning restore CA1422
                var runtime = new AppRuntime(path, name, startupTiming: (stage, ms) => global::Android.Util.Log.Info("LanTodo", stage + "Ms=" + ms));
                try { inbox.CommitTo(runtime.Store, runtime.Identity.Id, runtime.Identity.Name); }
                catch (Exception ex) { global::Android.Util.Log.Warn("LanTodo", "Capture replay: " + ex.GetType().Name); }
                runtime.StatusChanged += () => sendHandler?.Post(FinishConfirmedSend);
                return runtime;
            });
            _ = ConnectWhenReadyAsync(initialization);
            return initialization;
        }
    }
    private static async Task ConnectWhenReadyAsync(Task<AppRuntime> ready)
    {
        try
        {
            var runtime = await ready.ConfigureAwait(false);
            await RefreshNetworkAsync().ConfigureAwait(false);
            runtime.RequestSync();
        }
        catch (Exception ex) { global::Android.Util.Log.Warn("LanTodo", "Initial connection: " + ex.GetType().Name); }
    }
    private static (bool Local, bool Wifi, bool Mobile, bool Vpn) Describe(NetworkCapabilities caps) =>
        (caps.HasTransport(TransportType.Wifi) || caps.HasTransport(TransportType.Ethernet) || caps.HasTransport(TransportType.Vpn),
         caps.HasTransport(TransportType.Wifi), caps.HasTransport(TransportType.Cellular), caps.HasTransport(TransportType.Vpn));
    private static void ConfigureDiscovery()
    {
        discoveryHandler!.RemoveCallbacksAndMessages(null);
        bool wifi; lock (gate) wifi = networks.Values.Any(n => n.Wifi);
        if (!wifi || (!ActivityVisible && !ServiceRunning))
        { if (multicast?.IsHeld == true) multicast.Release(); return; }
        if (multicast?.IsHeld == false) multicast.Acquire();
        if (initialization?.IsCompletedSuccessfully == true) initialization.Result.Discover();
        if (ActivityVisible) return;
        // Keep known peers connected; only open a short multicast window to discover new peers.
        discoveryHandler.PostDelayed(() => { if (!ActivityVisible && multicast?.IsHeld == true) multicast.Release(); }, 15000);
        discoveryHandler.PostDelayed(ConfigureDiscovery, 120000);
    }
    private sealed class NetworkObserver : ConnectivityManager.NetworkCallback
    {
        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities caps)
        {
            var value = Describe(caps);
            lock (gate) { if (networks.TryGetValue(network, out var previous) && previous == value) return; networks[network] = value; }
            Update();
        }
        public override void OnLost(Network network) { lock (gate) networks.Remove(network); Update(); }
        public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties) => Update();
        private static async void Update()
        {
            try { await RefreshNetworkAsync(); if (initialization?.IsCompletedSuccessfully == true) initialization.Result.RequestReconnect(); }
            catch (Exception ex) { global::Android.Util.Log.Warn("LanTodo", ex.GetType().Name); }
        }
    }
    public static bool BackgroundEnabled(Context context) => context.GetSharedPreferences("preferences", FileCreationMode.Private)!.GetBoolean("backgroundSync", true);
    public static void SetBackground(Context context, bool enabled) => context.GetSharedPreferences("preferences", FileCreationMode.Private)!.Edit()!.PutBoolean("backgroundSync", enabled)!.Apply();
    public static void StartBackground(Context context) => context.StartForegroundService(new Intent(context, typeof(SyncService)));
}
