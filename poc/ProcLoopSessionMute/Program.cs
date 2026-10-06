using NAudio.CoreAudioApi;
using NAudio.Wave;

// 実験2: 「セッション音量を極小(例 1e-4)にして、取り込み側をデジタルで補正する」は成立するか?
//   ProcLoopSessionMute <targetPid> [include|exclude]
//   exclude: 自分(この計測ツール)のプロセス以外すべてを取り込む = 実運用と同じ構成
int pid = int.Parse(args[0]);
bool exclude = args.Length > 1 && args[1] == "exclude";
using var en = new MMDeviceEnumerator();
var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
Console.WriteLine($"default render: {dev.FriendlyName}  mode={(exclude ? "EXCLUDE self" : "INCLUDE target")}");

SimpleAudioVolume? vol = null;
var sm = dev.AudioSessionManager.Sessions;
for (int i = 0; i < sm.Count; i++) if (sm[i].GetProcessID == (uint)pid) vol = sm[i].SimpleAudioVolume;
if (vol == null) { Console.WriteLine("target session not found"); return; }

var rec = new WasapiRecorderBuilder()
    .WithProcessLoopback(exclude ? (uint)Environment.ProcessId : (uint)pid,
                         exclude ? ProcessLoopbackMode.ExcludeTargetProcessTree : ProcessLoopbackMode.IncludeTargetProcessTree)
    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))
    .BuildAsync().GetAwaiter().GetResult();

var buf = new List<float>();
rec.DataAvailable += (b, flags, dp, qpc) =>
{
    var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(b);
    lock (buf) { for (int i = 0; i < f.Length; i += 2) buf.Add(f[i]); }   // Lのみ
};
rec.StartRecording();

float[] Grab(double sec)
{
    Thread.Sleep(500);
    lock (buf) buf.Clear();
    Thread.Sleep((int)(sec * 1000));
    lock (buf) return buf.ToArray();
}
(double peak, double rms) Stats(float[] x, double scale = 1)
{
    double p = 0, s = 0; foreach (var v in x) { p = Math.Max(p, Math.Abs(v * scale)); s += (v * scale) * (v * scale); }
    return (p, Math.Sqrt(s / Math.Max(1, x.Length)));
}

vol.Mute = false; vol.Volume = 1f;
var baseX = Grab(2.0);
var (bp, br) = Stats(baseX);
Console.WriteLine($"baseline (vol=1)      peak={bp:F5} rms={br:F5}  frames={baseX.Length}");

foreach (float v in new[] { 0.5f, 0.25f, 0.1f, 0.05f, 0.02f, 0.01f, 0.005f, 0.002f, 1e-3f, 1e-4f, 1e-5f })
{
    vol.Volume = v;
    var x = Grab(2.0);
    var (p, r) = Stats(x);
    var (cp, cr) = Stats(x, 1.0 / v);
    // 波形の歪み(補正後の値と基準の違い)の目安: 補正後RMS/基準RMS と 補正後ピーク/基準ピーク
    Console.WriteLine($"vol={v:E0}  raw peak={p:E3}  -> compensated peak={cp:F5} (x{cp / bp:F4} of base)  rms={cr:F5} (x{cr / br:F4})");
}
vol.Volume = 1f;
rec.StopRecording(); rec.Dispose();
