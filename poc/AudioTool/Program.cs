using NAudio.CoreAudioApi;

// 診断/復旧用CLI。
//   AudioTool status          既定再生デバイスのミュート/マスター音量/ピークメーター/全セッション音量
//   AudioTool unmute|mute     マスターミュートの解除/設定
//   AudioTool vol <name> <v>  指定プロセス名(部分一致)のセッション音量を設定(0..1)
//   AudioTool fixvol          全セッションの音量を1.0に戻す(Idle除く)
//   AudioTool meter [sec]     エンドポイントのピークメーターを一定時間表示

using var en = new MMDeviceEnumerator();
var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
string cmd = args.Length > 0 ? args[0] : "status";

switch (cmd)
{
    case "unmute": dev.AudioEndpointVolume.Mute = false; Console.WriteLine("unmuted"); break;
    case "mute": dev.AudioEndpointVolume.Mute = true; Console.WriteLine("muted"); break;
    case "mastervol": dev.AudioEndpointVolume.MasterVolumeLevelScalar = float.Parse(args[1]); Console.WriteLine($"default master -> {args[1]}"); break;
    case "volumes":
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            Console.WriteLine($"{d.FriendlyName}: vol={d.AudioEndpointVolume.MasterVolumeLevelScalar:F3} mute={d.AudioEndpointVolume.Mute}");
        break;
    case "vol":
        SetVol(args[1], float.Parse(args[2]));
        break;
    case "fixvol":
        for (int i = 0; i < dev.AudioSessionManager.Sessions.Count; i++)
        {
            var s = dev.AudioSessionManager.Sessions[i];
            if (s.GetProcessID == 0) continue;
            if (s.SimpleAudioVolume.Volume < 0.99f) { s.SimpleAudioVolume.Volume = 1f; Console.WriteLine($"fixed pid={s.GetProcessID}"); }
        }
        break;
    case "probe": Probe.Run(); break;
    case "setdefault": SetDefault.Run(en, args[1]); break;
    case "sessions":
    {
        // 全アクティブ再生デバイスのセッション一覧(どのアプリがどのデバイスに出しているかの確認用)
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var dflt = d.ID == dev.ID ? " (既定)" : "";
            Console.WriteLine($"[{d.FriendlyName}]{dflt} mute={d.AudioEndpointVolume.Mute}");
            var sm = d.AudioSessionManager.Sessions;
            for (int i = 0; i < sm.Count; i++)
            {
                var s = sm[i];
                string name = "?";
                try { name = System.Diagnostics.Process.GetProcessById((int)s.GetProcessID).ProcessName; } catch { }
                Console.WriteLine($"    pid={s.GetProcessID} {name} state={s.State} vol={s.SimpleAudioVolume.Volume:F2}");
            }
        }
        break;
    }
    case "devices":
    {
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        {
            string id = d.ID.Length > 40 ? d.ID[..40] : d.ID;
            Console.WriteLine($"{d.DataFlow,-8} {d.State,-12} {d.FriendlyName}  [{d.DeviceFriendlyName}]  {id}");
        }
        break;
    }
    case "tone":
    {
        // WasapiOutで直接トーンを鳴らす(マスターミュート中に自分の再生音が聞こえるかの確認用)
        int sec = args.Length > 1 ? int.Parse(args[1]) : 5;
        var sig = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Gain = 0.3, Frequency = 440 };
        using var wo = new NAudio.Wave.WasapiOut(dev, AudioClientShareMode.Shared, true, 50);
        wo.Init(NAudio.Wave.WaveExtensionMethods.ToWaveProvider(sig));
        wo.Play();
        Console.WriteLine($"tone playing {sec}s (mute={dev.AudioEndpointVolume.Mute})");
        Thread.Sleep(sec * 1000);
        wo.Stop();
        break;
    }
    case "meter":
    {
        int sec = args.Length > 1 ? int.Parse(args[1]) : 5;
        var end = DateTime.UtcNow.AddSeconds(sec);
        while (DateTime.UtcNow < end)
        {
            Console.WriteLine($"mute={dev.AudioEndpointVolume.Mute} endpointPeak={dev.AudioMeterInformation.MasterPeakValue:F4}");
            Thread.Sleep(200);
        }
        break;
    }
    default:
        Console.WriteLine($"device: {dev.FriendlyName}");
        Console.WriteLine($"mute={dev.AudioEndpointVolume.Mute} masterVolume={dev.AudioEndpointVolume.MasterVolumeLevelScalar:F2} endpointPeak={dev.AudioMeterInformation.MasterPeakValue:F4}");
        for (int i = 0; i < dev.AudioSessionManager.Sessions.Count; i++)
        {
            var s = dev.AudioSessionManager.Sessions[i];
            string name = "?";
            try { name = System.Diagnostics.Process.GetProcessById((int)s.GetProcessID).ProcessName; } catch { }
            Console.WriteLine($"  pid={s.GetProcessID} {name} state={s.State} vol={s.SimpleAudioVolume.Volume:F2} muted={s.SimpleAudioVolume.Mute}");
        }
        break;
}

void SetVol(string name, float v)
{
    for (int i = 0; i < dev.AudioSessionManager.Sessions.Count; i++)
    {
        var s = dev.AudioSessionManager.Sessions[i];
        try
        {
            string pn = System.Diagnostics.Process.GetProcessById((int)s.GetProcessID).ProcessName;
            if (pn.Contains(name, StringComparison.OrdinalIgnoreCase)) { s.SimpleAudioVolume.Volume = v; Console.WriteLine($"{pn} -> {v}"); }
        }
        catch { }
    }
}
