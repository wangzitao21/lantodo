using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using LanTodo.Core;
using Camera = Android.Hardware.Camera;

namespace LanTodo.Android;

#pragma warning disable CS0618 // Camera preview is a bounded QR-only view, compatible with API 26+.
[Activity(Label = "扫描空间二维码", Exported = false, ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class QrScannerActivity : Activity, ISurfaceHolderCallback, Camera.IPreviewCallback
{
    private SurfaceView preview = null!;
    private TextView hint = null!;
    private Camera? camera;
    private int width, height, decoding;
    private long lastFrame;
    private bool resumed, finished;
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        hint = new TextView(this) { Text = "对准另一台设备上的 LanTodo 二维码", TextSize = 18 }; hint.SetPadding(24,24,24,24); layout.AddView(hint);
        preview = new SurfaceView(this); preview.Holder!.AddCallback(this); layout.AddView(preview, new LinearLayout.LayoutParams(-1, 0, 1));
        var back = new Button(this) { Text = "返回，改用授权码或相册" }; back.Click += (_, _) => Finish(); layout.AddView(back); SetContentView(layout);
        if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted) RequestPermissions([global::Android.Manifest.Permission.Camera], 1);
    }
    protected override void OnResume() { base.OnResume(); resumed = true; StartPreview(); }
    protected override void OnPause() { resumed = false; StopPreview(); base.OnPause(); }
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != 1) return;
        if (grantResults.FirstOrDefault() == Permission.Granted && grantResults.Length > 0) StartPreview();
        else hint.Text = "未获得相机权限。可返回并从相册读取二维码，或粘贴授权码。";
    }
    public void SurfaceCreated(ISurfaceHolder holder) => StartPreview();
    public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int w, int h) { }
    public void SurfaceDestroyed(ISurfaceHolder holder) => StopPreview();
    private void StartPreview()
    {
        if (!resumed || finished || camera is not null || preview.Holder?.Surface?.IsValid != true || CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted) return;
        try
        {
            camera = Camera.Open() ?? throw new IOException("相机不可用。");
            var settings = camera.GetParameters()!;
            var size = settings.SupportedPreviewSizes!.Where(s => s.Width <= 1920 && s.Height <= 1080).OrderByDescending(s => s.Width * s.Height).FirstOrDefault() ?? settings.PreviewSize!;
            width = size.Width; height = size.Height; settings.SetPreviewSize(width, height);
            settings.PreviewFormat = global::Android.Graphics.ImageFormatType.Nv21;
            if (settings.SupportedFocusModes?.Contains(Camera.Parameters.FocusModeContinuousPicture) == true) settings.FocusMode = Camera.Parameters.FocusModeContinuousPicture;
            camera.SetParameters(settings); camera.SetDisplayOrientation(90); camera.SetPreviewDisplay(preview.Holder);
            camera.SetPreviewCallback(this); camera.StartPreview();
        }
        catch (Exception) { StopPreview(); hint.Text = "相机暂不可用，请返回并从相册读取二维码。"; }
    }
    private void StopPreview()
    {
        var previous = camera; camera = null;
        if (previous is null) return;
        try { previous.SetPreviewCallback(null); previous.StopPreview(); previous.Release(); } catch (Exception) { }
        previous.Dispose();
    }
    public void OnPreviewFrame(byte[]? data, Camera? source)
    {
        if (data is null || !resumed || finished || SystemClock.ElapsedRealtime() - lastFrame < 350 || Interlocked.CompareExchange(ref decoding, 1, 0) != 0) return;
        lastFrame = SystemClock.ElapsedRealtime(); int w = width, h = height; var copy = data.ToArray();
        _ = Task.Run(() =>
        {
            try
            {
                var value = InviteQr.Decode(new ZXing.PlanarYUVLuminanceSource(copy, w, h, 0, 0, w, h, false));
                if (value is not null) RunOnUiThread(() => { if (!resumed || finished || IsDestroyed) return; finished = true; SetResult(Result.Ok, new Intent().PutExtra("code", value)); Finish(); });
            }
            catch (Exception) { }
            finally { Interlocked.Exchange(ref decoding, 0); }
        });
    }
}
#pragma warning restore CS0618
