using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

// PoC: 指定プロセス名のオーディオセッションを見つけて、
// ISimpleAudioVolume (AudioSessionControl.SimpleAudioVolume) を
// コードから滑らかに動かせるか検証する。
//
// 使い方: dotnet run -- <process-name-substring> [down|up]
//   例: dotnet run -- chrome down

string targetSubstring = args.Length > 0 ? args[0] : "chrome";
string direction = args.Length > 1 ? args[1] : "down";

Console.WriteLine($"対象プロセス名(部分一致): \"{targetSubstring}\"  方向: {direction}");

using var enumerator = new MMDeviceEnumerator();
var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
Console.WriteLine($"既定の出力デバイス: {device.FriendlyName}");

var sessionManager = device.AudioSessionManager;
var sessions = sessionManager.Sessions;

Console.WriteLine($"セッション数: {sessions.Count}");

var matches = new List<AudioSessionControl>();
for (int i = 0; i < sessions.Count; i++)
{
    var session = sessions[i];
    try
    {
        uint pid = session.GetProcessID;
        string procName = "(system)";
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            procName = proc.ProcessName;
        }
        catch { /* プロセスが既に終了している等 */ }

        string displayName = session.DisplayName;
        float vol = session.SimpleAudioVolume.Volume;
        Console.WriteLine($"  [{i}] pid={pid} proc={procName} display=\"{displayName}\" state={session.State} volume={vol:F2}");

        if (procName.Contains(targetSubstring, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add(session);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [{i}] セッション情報取得エラー: {ex.Message}");
    }
}

if (matches.Count == 0)
{
    Console.WriteLine($"\"{targetSubstring}\" を含むプロセス名のセッションが見つかりませんでした。対象アプリで音声を再生した状態で実行してください。");
    return;
}

Console.WriteLine($"\n{matches.Count} 件のセッションにフェードを適用します。");

// raised-cosine風のカーブで滑らかにフェード。ステップごとにSimpleAudioVolume.Volumeへ書き込み、
// 実際の音量変化が追従するか(=コードからのリアルタイム制御が可能か)を検証する。
const int steps = 60; // 約600ms分、10ms間隔
const int stepDelayMs = 10;

foreach (var session in matches)
{
    var simpleVolume = session.SimpleAudioVolume;
    var meter = session.AudioMeterInformation;
    float startVol = simpleVolume.Volume;
    float endVol = direction == "down" ? 0f : 1f;

    Console.WriteLine($"フェード開始: {startVol:F2} -> {endVol:F2}  (peak={meter.MasterPeakValue:F3})");

    for (int i = 0; i <= steps; i++)
    {
        double t = (double)i / steps;
        // raised cosine: 0->1をなめらかに
        double eased = 0.5 - 0.5 * Math.Cos(Math.PI * t);
        float vol = (float)(startVol + (endVol - startVol) * eased);
        simpleVolume.Volume = Math.Clamp(vol, 0f, 1f);
        Thread.Sleep(stepDelayMs);

        if (i % 10 == 0)
        {
            Console.WriteLine($"  t={t:F2} volume={simpleVolume.Volume:F2} peak={meter.MasterPeakValue:F3}");
        }
    }

    Console.WriteLine($"フェード完了: 現在値={simpleVolume.Volume:F2}  (peak={meter.MasterPeakValue:F3})");
}
