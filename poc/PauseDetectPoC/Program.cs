using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

// 診断ツール: 一時停止を検出した瞬間から500ms間、ピークメーターの生値を5ms間隔で記録する。
// 目的: ブラウザが一時停止した瞬間、既に音声データの送出が止まっていて「フェードする余地(まだ鳴っている音)」が
// 残っていないのか、それともしばらく余韻があるのかを確認する。
//
// 使い方: dotnet run -- <process-name-substring>

static string Ts() => DateTime.UtcNow.ToString("HH:mm:ss.fff");

string[] targetCandidates = args.Length > 0 ? args : new[] { "chrome", "msedge", "firefox" };
Console.WriteLine($"[{Ts()}] 対象プロセス名候補(部分一致): {string.Join(", ", targetCandidates)}");

using var enumerator = new MMDeviceEnumerator();
var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

AudioSessionControl? target = null;
string? matchedProcName = null;
var findDeadline = DateTime.UtcNow.AddSeconds(30);

while (target == null && DateTime.UtcNow < findDeadline)
{
    var sessions = device.AudioSessionManager.Sessions;
    for (int i = 0; i < sessions.Count; i++)
    {
        var session = sessions[i];
        try
        {
            uint pid = session.GetProcessID;
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            if (targetCandidates.Any(c => proc.ProcessName.Contains(c, StringComparison.OrdinalIgnoreCase)))
            {
                target = session;
                matchedProcName = proc.ProcessName;
                break;
            }
        }
        catch { }
    }
    if (target == null) Thread.Sleep(1000);
}

if (target == null)
{
    Console.WriteLine($"[{Ts()}] タイムアウト: 対象セッションが見つかりませんでした。");
    return;
}

Console.WriteLine($"[{Ts()}] 対象セッションを発見: proc={matchedProcName}");
Console.WriteLine($"[{Ts()}] 監視開始。一時停止を検出するたびに、その瞬間から500ms間のピーク値を細かく記録します(60秒間)。");

var meter = target.AudioMeterInformation;
var eventHandler = new StateChangeLogger();
target.RegisterEventClient(eventHandler);

var overallDeadline = DateTime.UtcNow.AddSeconds(300);
while (DateTime.UtcNow < overallDeadline && !eventHandler.SawInactive)
{
    Thread.Sleep(50);
}

if (eventHandler.SawInactive)
{
    Console.WriteLine($"[{Ts()}] Inactive検出。ここから800ms、5ms間隔でピーク値とSimpleAudioVolumeを記録:");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < 800)
    {
        float peak, vol;
        try
        {
            peak = meter.MasterPeakValue;
            vol = target.SimpleAudioVolume.Volume;
        }
        catch { break; }
        Console.WriteLine($"  +{sw.ElapsedMilliseconds,4}ms peak={peak:F4} volume={vol:F4}");
        Thread.Sleep(5);
    }
}
else
{
    Console.WriteLine($"[{Ts()}] タイムアウト: Inactiveが検出されませんでした(一時停止を試してください)。");
}

target.UnRegisterEventClient(eventHandler);
Console.WriteLine($"[{Ts()}] 終了。");

class StateChangeLogger : IAudioSessionEventsHandler
{
    public volatile bool SawInactive;
    static string Ts() => DateTime.UtcNow.ToString("HH:mm:ss.fff");

    public void OnVolumeChanged(float volume, bool isMuted) { }
    public void OnDisplayNameChanged(string displayName) { }
    public void OnIconPathChanged(string iconPath) { }
    public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }
    public void OnGroupingParamChanged(ref Guid groupingId) { }

    public void OnStateChanged(AudioSessionState state)
    {
        Console.WriteLine($"[{Ts()}] EVENT_StateChanged state={state}");
        if (state == AudioSessionState.AudioSessionStateInactive) SawInactive = true;
    }

    public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) { }
}
