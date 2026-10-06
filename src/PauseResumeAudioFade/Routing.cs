using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace PauseResumeAudioFade.App;

/// <summary>
/// 既定の再生デバイスを切り替えるための、Windows非公開(ただし長年安定して使われている)COMインターフェイス IPolicyConfig。
/// SoundSwitch / AudioDeviceCmdlets 等も使っている。
/// </summary>
internal static class PolicyConfig
{
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool def, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool fx, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool fx, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    /// <summary>Console と Multimedia の既定を切り替える。Communications(通話用)は遅延を避けるため触らない。</summary>
    public static void SetDefault(string deviceId)
    {
        var cfg = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia })
            {
                int hr = cfg.SetDefaultEndpoint(deviceId, role);
                if (hr != 0) throw new COMException($"SetDefaultEndpoint({role}) failed", hr);
            }
        }
        finally { Marshal.ReleaseComObject(cfg); }
    }
}

internal static class Devices
{
    /// <summary>
    /// 試験専用: 環境変数 PRAF_CHAOS=1 のとき、設定フォルダの chaos-hide.txt に書かれた文字列を含むIDのデバイスを
    /// 「抜かれた」ものとして扱う。イヤホンの抜き差し(消失→復帰)を実機を触らずに再現するために使う。通常は何もしない。
    /// </summary>
    private static bool ChaosHidden(string id)
    {
        if (Environment.GetEnvironmentVariable("PRAF_CHAOS") != "1") return false;
        try
        {
            var p = Path.Combine(AppPaths.Dir, "chaos-hide.txt");
            if (!File.Exists(p)) return false;
            var frag = File.ReadAllText(p).Trim();
            return frag.Length > 0 && id.Contains(frag, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static List<MMDevice> ActiveRender(MMDeviceEnumerator en) =>
        en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Where(d => !ChaosHidden(d.ID)).ToList();

    public static MMDevice? ById(MMDeviceEnumerator en, string id)
    {
        if (string.IsNullOrEmpty(id) || ChaosHidden(id)) return null;
        try
        {
            var d = en.GetDevice(id);
            return d.State == DeviceState.Active ? d : null;
        }
        catch { return null; }
    }

    public static MMDevice? DefaultRender(MMDeviceEnumerator en)
    {
        try { return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { return null; }
    }

    public static MMDevice? FindByName(MMDeviceEnumerator en, string nameContains)
    {
        if (string.IsNullOrWhiteSpace(nameContains)) return null;
        return ActiveRender(en).FirstOrDefault(d =>
            d.FriendlyName.Contains(nameContains, StringComparison.OrdinalIgnoreCase) ||
            d.DeviceFriendlyName.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>異常終了しても既定デバイスを元に戻せるように、切替内容をファイルに残す。</summary>
internal sealed class RoutingState
{
    public int OwnerPid { get; set; }
    public string RealDeviceId { get; set; } = "";
    public string SinkDeviceId { get; set; } = "";
    public bool SinkMutedByUs { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save()
    {
        AppPaths.EnsureDir();
        File.WriteAllText(AppPaths.StateFile, JsonSerializer.Serialize(this, Json));
    }

    public static RoutingState? Load()
    {
        try
        {
            return File.Exists(AppPaths.StateFile)
                ? JsonSerializer.Deserialize<RoutingState>(File.ReadAllText(AppPaths.StateFile), Json)
                : null;
        }
        catch { return null; }
    }

    public static void Delete()
    {
        try { File.Delete(AppPaths.StateFile); } catch { }
    }

    /// <summary>
    /// 前回の実行が異常終了していた場合に、既定デバイスとミュートを元に戻す。
    /// 現在の既定がまだ切替先(シンク)のときだけ戻す(ユーザーが手動で変えていたら触らない)。
    /// </summary>
    public static bool RestoreIfNeeded(string reason)
    {
        var st = Load();
        if (st == null) return false;

        try
        {
            using var en = new MMDeviceEnumerator();
            var def = Devices.DefaultRender(en);
            var real = Devices.ById(en, st.RealDeviceId);
            if (real == null)
                real = Devices.ActiveRender(en).FirstOrDefault(d => d.ID != st.SinkDeviceId);

            if (def != null && def.ID == st.SinkDeviceId && real != null)
            {
                PolicyConfig.SetDefault(real.ID);
                Log.Info($"[{reason}] default device restored to: {real.FriendlyName}");
            }

            if (st.SinkMutedByUs)
            {
                var sink = Devices.ById(en, st.SinkDeviceId);
                if (sink != null) { sink.AudioEndpointVolume.Mute = false; Log.Info($"[{reason}] sink unmuted"); }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[{reason}] restore failed", ex);
            return false;
        }
        finally
        {
            Delete();
        }
        return true;
    }

    /// <summary>親プロセスの終了(正常/異常を問わず)を待ち、状態が残っていれば復旧する。</summary>
    public static void RunWatchdog(int ownerPid)
    {
        try
        {
            using var parent = Process.GetProcessById(ownerPid);
            parent.WaitForExit();
        }
        catch { /* 既に終了している */ }

        Thread.Sleep(300);
        var st = Load();
        if (st != null && st.OwnerPid == ownerPid) RestoreIfNeeded("watchdog");
    }
}
