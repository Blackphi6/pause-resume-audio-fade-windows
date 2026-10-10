using NAudio.CoreAudioApi;

namespace PauseResumeAudioFade.App;

internal static class Program
{
    // 通常起動: トレイ常駐アプリ。
    // 補助モード:
    //   --watchdog <pid>   親(pid)の終了を待ち、既定デバイス/ミュートが元に戻っていなければ復旧する(自動で起動される)
    //   --restore          前回の異常終了の後始末(既定デバイス/ミュートを元に戻す)だけ行って終了
    //   --quit             起動中のインスタンスに正常終了を要求(既定デバイスを元に戻して終了)
    //   --export-icons <dir>  状態ごとのトレイアイコンをPNGで書き出して終了(開発用)
    // 開発/テスト用オプション(settings.jsonには保存されない限り一時的):
    //   --sink <名前>      切替先デバイスの名前(部分一致)。仮想ケーブルの代わりに別の出力デバイスを使う
    //   --real <名前>      実際に鳴らす出力デバイスの名前(部分一致)
    //   --mute-sink        切替先デバイスをエンドポイントミュートする(物理出力をシンクに使う場合に必要)
    //   --no-tray          トレイアイコンなしで動かす
    //   --console          ログをコンソールにも出す
    [STAThread]
    private static int Main(string[] args)
    {
        Log.AlsoConsole = args.Contains("--console");

        int wi = Array.IndexOf(args, "--watchdog");
        if (wi >= 0 && wi + 1 < args.Length && int.TryParse(args[wi + 1], out int pid))
        {
            RoutingState.RunWatchdog(pid);
            return 0;
        }

        // 開発用: 状態ごとのトレイアイコンをPNGで書き出す(見た目の確認用)
        int ei = Array.IndexOf(args, "--export-icons");
        if (ei >= 0 && ei + 1 < args.Length)
        {
            Directory.CreateDirectory(args[ei + 1]);
            foreach (EngineStatus st in Enum.GetValues<EngineStatus>())
                using (var ic = AppIcon.ForStatus(st, 64)) ic.ToBitmap().Save(Path.Combine(args[ei + 1], $"tray-{st}.png"));
            return 0;
        }

        if (args.Contains("--restore"))
        {
            RoutingState.RestoreIfNeeded("cli");
            return 0;
        }

        if (args.Contains("--quit"))
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(TrayApp.ExitEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\PauseResumeAudioFade.Singleton", out bool created);
        if (!created)
        {
            Log.Info("another instance is already running");
            return 1;
        }

        ApplicationConfiguration.Initialize();
        var settings = Settings.Load();
        ApplyOverrides(settings, args);
        Log.Info($"started (pid {Environment.ProcessId}) args=[{string.Join(' ', args)}]");

        string? dump = args.Contains("--dump") ? args[Array.IndexOf(args, "--dump") + 1] : null;
        Application.Run(new TrayApp(settings, noUi: args.Contains("--no-tray"), dumpDir: dump));
        Log.Info("exited");
        return 0;
    }

    private static void ApplyOverrides(Settings s, string[] args)
    {
        string? Value(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var sink = Value("--sink");
        if (sink != null) { s.VirtualDeviceName = sink; s.AutoSink = false; }   // 明示指定のときは自動選択しない
        if (args.Contains("--mute-sink")) s.MuteSink = true;

        var real = Value("--real");
        if (real != null)
        {
            using var en = new MMDeviceEnumerator();
            var d = Devices.FindByName(en, real);
            if (d != null) s.OutputDeviceId = d.ID;
            else Log.Warn($"--real device not found: {real}");
        }
    }
}
