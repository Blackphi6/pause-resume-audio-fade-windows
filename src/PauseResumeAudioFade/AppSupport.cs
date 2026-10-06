using System.Text.Json;
using System.Text.Json.Serialization;
using PauseResumeAudioFade.Core;

namespace PauseResumeAudioFade.App;

internal static class AppPaths
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PauseResumeAudioFade");

    public static string SettingsFile => Path.Combine(Dir, "settings.json");
    public static string StateFile => Path.Combine(Dir, "state.json");
    public static string LogFile => Path.Combine(Dir, "log.txt");

    public static void EnsureDir() => Directory.CreateDirectory(Dir);
}

/// <summary>ファイルへの簡易ログ(1MBを超えたら古い方を捨てる)。例外は握りつぶす。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static bool AlsoConsole { get; set; }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        if (AlsoConsole) { try { Console.WriteLine(line); } catch { } }
        lock (Gate)
        {
            try
            {
                AppPaths.EnsureDir();
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    var lines = File.ReadAllLines(AppPaths.LogFile);
                    File.WriteAllLines(AppPaths.LogFile, lines.Skip(lines.Length / 2));
                }
                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
            catch { }
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum Preset { Short, Standard, Long, Music }

internal sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public Preset Preset { get; set; } = Preset.Music;
    public int FadeInMs { get; set; } = 300;
    public bool FadeOutEnabled { get; set; } = true;

    /// <summary>既定の再生デバイスの切替先にする仮想デバイスの名前(部分一致)。</summary>
    public string VirtualDeviceName { get; set; } = "CABLE Input";

    /// <summary>実際に鳴らす出力デバイスのID。空なら「切替前の既定デバイス」を自動で使う。</summary>
    public string OutputDeviceId { get; set; } = "";

    /// <summary>切替先デバイスをエンドポイントミュートする(仮想ケーブル以外=物理出力を代用する場合に必要)。自動選択で物理出力を借りたときは、この設定に関わらずミュートする。</summary>
    public bool MuteSink { get; set; } = false;

    /// <summary>仮想ケーブルが無いとき、使っていない別の出力デバイスを自動で借りる。</summary>
    public bool AutoSink { get; set; } = true;

    /// <summary>初回の案内画面を表示済みか。</summary>
    public bool SetupShown { get; set; } = false;

    public FadeParameters ToFadeParameters()
    {
        var p = Preset switch
        {
            Preset.Short => FadeParameters.Short(FadeInMs),
            Preset.Long => FadeParameters.Long(FadeInMs),
            Preset.Music => FadeParameters.Music(FadeInMs),
            _ => FadeParameters.Standard(FadeInMs),
        };
        return p with { FadeOutEnabled = FadeOutEnabled };
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new Settings();
        }
        catch (Exception ex) { Log.Warn($"settings load failed: {ex.Message}"); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDir();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Warn($"settings save failed: {ex.Message}"); }
    }
}
