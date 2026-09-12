using Android.App;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Widget;
using LanTodo.Core;

namespace LanTodo.Android;

public partial class MainActivity
{
    private MediaRecorder? recorder;
    private string? recordingPath;
    private long recordingStarted;
    private bool cancelVoice;
    private Button? recordButton;
    private MediaPlayer? audioPlayer;
    private Button? playingButton;
    private string? playingLabel;

    private void AddVoiceComposer(LinearLayout composer)
    {
        var toggle=Button("语音");toggle.SetPadding(0,0,0,0);toggle.SetMinWidth(0);toggle.SetMinimumWidth(0);
        EnableWhenReady(toggle);
        toggle.ContentDescription="切换文字或语音输入";
        var layout=new LinearLayout.LayoutParams(Dp(48),Dp(48));layout.SetMargins(0,0,Dp(6),0);
        composer.AddView(toggle,0,layout);
        var hold=recordButton=Button("按住说话");hold.Visibility=ViewStates.Gone;hold.Gravity=GravityFlags.Center;
        hold.ContentDescription="按住录音，松开发送，上滑取消，最长60秒";
        composer.AddView(hold,2,new LinearLayout.LayoutParams(0,Dp(48),1));
        toggle.Click+=(_,_)=>
        {
            bool voice=hold.Visibility!=ViewStates.Visible;
            CancelRecording();quickInput!.Visibility=voice?ViewStates.Gone:ViewStates.Visible;
            hold.Visibility=voice?ViewStates.Visible:ViewStates.Gone;toggle.Text=voice?"文字":"语音";
            if(voice){((global::Android.Views.InputMethods.InputMethodManager)GetSystemService(InputMethodService)!).HideSoftInputFromWindow(quickInput.WindowToken,0);attachmentTray!.Visibility=ViewStates.Gone;}
        };
        float downY=0;
        hold.Touch+=(_,args)=>
        {
            var e=args.Event;if(e is null)return;args.Handled=true;
            switch(e.ActionMasked)
            {
                case MotionEventActions.Down:
                    downY=e.RawY;cancelVoice=false;StartRecording();break;
                case MotionEventActions.Move:
                    if(recorder is not null){cancelVoice=downY-e.RawY>Dp(64);UpdateRecordingLabel();}break;
                case MotionEventActions.Up:
                    _=FinishRecordingAsync(cancelVoice);break;
                case MotionEventActions.Cancel:
                    CancelRecording();break;
            }
        };
    }
    private void StartRecording()
    {
        if(attachmentBusy || recorder is not null)return;
        if(CheckSelfPermission("android.permission.RECORD_AUDIO")!=Permission.Granted)
        {RequestPermissions(["android.permission.RECORD_AUDIO"],61);return;}
        StopAudio();
        try
        {
            recordingPath=Path.Combine(CacheDir!.AbsolutePath,"voice-"+Guid.NewGuid().ToString("N")+".m4a");
#pragma warning disable CA1422
            recorder=OperatingSystem.IsAndroidVersionAtLeast(31)?new MediaRecorder(this):new MediaRecorder();
#pragma warning restore CA1422
            recorder.SetAudioSource(AudioSource.Mic);recorder.SetOutputFormat(OutputFormat.Mpeg4);
            recorder.SetAudioEncoder(AudioEncoder.Aac);recorder.SetAudioChannels(1);
            recorder.SetAudioSamplingRate(44100);recorder.SetAudioEncodingBitRate(64000);
            recorder.SetOutputFile(recordingPath);recorder.SetMaxDuration(60000);
            recorder.Info+=(_,args)=>{if(args.What==MediaRecorderInfo.MaxDurationReached)RunOnUiThread(()=>_ = FinishRecordingAsync(cancelVoice));};
            recorder.Error+=(_,_)=>RunOnUiThread(()=>{CancelRecording();Error("录音中断，请重试。");});
            recorder.Prepare();recorder.Start();recordingStarted=SystemClock.ElapsedRealtime();
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            TickRecording(recorder);
        }
        catch(Exception ex){CancelRecording();Error("无法录音："+ex.Message);}
    }
    private void UpdateRecordingLabel()
    {
        if(recordButton is not null)recordButton.Text=cancelVoice?"松开取消":"松开发送 · "+Math.Min(60,(SystemClock.ElapsedRealtime()-recordingStarted)/1000)+"秒";
    }
    private void TickRecording(MediaRecorder current)
    {
        if(recorder!=current || IsDestroyed)return;
        UpdateRecordingLabel();root.PostDelayed(()=>TickRecording(current),1000);
    }
    private void ReleaseRecorder()
    {
        recorder?.Release();recorder?.Dispose();recorder=null;
        Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        if(recordButton is not null)recordButton.Text="按住说话";
    }
    private void CancelRecording()
    {
        try{recorder?.Stop();}catch(Exception){}finally{ReleaseRecorder();}
        if(recordingPath is {} path){try{File.Delete(path);}catch(IOException){}recordingPath=null;}
    }
    private async Task FinishRecordingAsync(bool cancel)
    {
        if(recorder is null)return;
        if(cancel || SystemClock.ElapsedRealtime()-recordingStarted<700)
        {CancelRecording();Toast.MakeText(this,cancel?"已取消":"录音太短，请重试",ToastLength.Short)?.Show();return;}
        var path=recordingPath!;recordingPath=null;
        var seconds=Math.Clamp((int)Math.Round((SystemClock.ElapsedRealtime()-recordingStarted)/1000d),1,60);
        attachmentBusy=true;updateSend?.Invoke();
        try
        {
            recorder.Stop();ReleaseRecorder();
            var attachment=await Task.Run(()=>{using var file=File.OpenRead(path);return app.Store.Attachments.Add(file,$"语音-{DateTime.Now:yyyyMMdd-HHmmss}-{seconds}秒.m4a","file");});
            try{app.Store.Save(app.Identity.Id,app.Identity.Name,new TodoData($"语音 · {seconds}秒",Attachments:[attachment]));}
            catch{pendingAttachments.Add(attachment);RenderPendingAttachments();throw;}
            AndroidSession.ProtectRecentSend();
            if(!IsDestroyed){if(filter!="all"){filter="all";Home();}else StoreChanged();}
        }
        catch(Exception ex){if(!IsDestroyed)Error("语音未发送："+ex.Message);}
        finally{ReleaseRecorder();try{File.Delete(path);}catch(IOException){}attachmentBusy=false;updateSend?.Invoke();}
    }
    public override void OnRequestPermissionsResult(int requestCode,string[] permissions,Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode,permissions,grantResults);
        if(requestCode==61)Toast.MakeText(this,grantResults.Length>0 && grantResults[0]==Permission.Granted?"已允许录音，请按住说话":"未允许麦克风，仍可使用文字和附件",ToastLength.Long)?.Show();
    }
    protected override void OnPause(){CancelRecording();StopAudio();base.OnPause();}
    private void StopAudio()
    {
        audioPlayer?.Release();audioPlayer?.Dispose();audioPlayer=null;
        if(playingButton is not null)playingButton.Text=playingLabel;
        playingButton=null;
    }
    private void PlayAudio(Attachment item,Button button)
    {
        bool stop=playingButton==button;StopAudio();if(stop)return;
        try
        {
            var player=audioPlayer=new MediaPlayer();playingButton=button;playingLabel=button.Text;button.Text="正在加载语音…";
            player.SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Speech)!.Build());
            player.SetDataSource(app.Store.Attachments.PathFor(item.Hash));
            player.Prepared+=(_,_)=>{if(audioPlayer!=player)return;button.Text=$"■ 停止播放 · {Math.Max(1,player.Duration/1000)}秒";player.Start();};
            player.Completion+=(_,_)=>{if(audioPlayer==player)StopAudio();};
            player.Error+=(_,args)=>{args.Handled=true;if(audioPlayer==player){StopAudio();Error("无法播放此语音文件。");}};
            player.PrepareAsync();
        }
        catch(Exception ex){StopAudio();Error(ex.Message);}
    }
}
