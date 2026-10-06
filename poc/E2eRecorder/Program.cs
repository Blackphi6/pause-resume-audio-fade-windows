using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// E2E検証用: 指定プロセス(=テスト対象のエンジン)が実際に再生している音声を、Process Loopback Capture(Include)で録音し、
// 波形を解析する(クリック/フェードの形/無音区間の長さ)。ミュートやマスター音量の影響を受けない(pre-endpoint)。
//   E2eRecorder <pid> <seconds>
const int Rate = 48000;
const double Amp = 0.4;
double natural = Amp * 2 * Math.PI * 440 / Rate;

uint pid = uint.Parse(args[0]);
int seconds = int.Parse(args[1]);

var client = await AudioClient.ActivateProcessLoopbackAsync(pid, ProcessLoopbackMode.IncludeTargetProcessTree);
var wf = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 2_000_000, 0, wf, Guid.Empty);
var cap = client.AudioCaptureClient;
client.Start();
Console.WriteLine($"recording pid={pid} for {seconds}s ...");

var x = new List<float>(Rate * (seconds + 2));
long baseQpc = -1;
var end = DateTime.UtcNow.AddSeconds(seconds);
while (DateTime.UtcNow < end)
{
    int sz = cap.GetNextPacketSize();
    if (sz == 0) { Thread.Sleep(2); continue; }
    while (sz > 0)
    {
        IntPtr p = cap.GetBuffer(out int frames, out var flags, out long devPos, out long qpc);
        if (baseQpc < 0) baseQpc = qpc;
        long expected = (long)Math.Round((qpc - baseQpc) / 1e7 * Rate);
        while (x.Count < expected) x.Add(0f); // パケットが来なかった期間は無音として埋める
        if ((flags & AudioClientBufferFlags.Silent) != 0 || frames == 0)
        {
            for (int i = 0; i < frames; i++) x.Add(0f);
        }
        else
        {
            var tmp = new float[frames * 2];
            Marshal.Copy(p, tmp, 0, tmp.Length);
            for (int i = 0; i < frames; i++) x.Add(tmp[i * 2]);
        }
        cap.ReleaseBuffer(frames);
        sz = cap.GetNextPacketSize();
    }
}
client.Stop();
var s = x.ToArray();
Console.WriteLine($"captured {s.Length} frames ({s.Length / (double)Rate:F2}s)");

// ---- 解析 ----
const float thr = 1e-4f;
int minRun = Rate / 200; // 5ms
var segs = new List<(bool tone, int start, int end)>();
int i0 = 0;
while (i0 < s.Length)
{
    bool silent = Math.Abs(s[i0]) <= thr;
    int j = i0;
    while (j < s.Length && (Math.Abs(s[j]) <= thr) == silent) j++;
    segs.Add((!silent, i0, j));
    i0 = j;
}
// 5ms未満の無音(正弦波のゼロクロス付近)は音の一部として結合
var merged = new List<(bool tone, int start, int end)>();
foreach (var sg in segs)
{
    if (!sg.tone && sg.end - sg.start < minRun && merged.Count > 0) { var l = merged[^1]; merged[^1] = (l.tone, l.start, sg.end); continue; }
    if (merged.Count > 0 && merged[^1].tone == sg.tone) { var l = merged[^1]; merged[^1] = (l.tone, l.start, sg.end); continue; }
    merged.Add(sg);
}

double Sec(int idx) => idx / (double)Rate;
double PeakIn(int a, int b)
{
    double m = 0;
    for (int k = Math.Max(0, a); k < Math.Min(s.Length, b); k++) m = Math.Max(m, Math.Abs(s[k]));
    return m;
}
double MaxStep(int a, int b)
{
    double m = 0;
    for (int k = Math.Max(1, a); k < Math.Min(s.Length, b); k++) m = Math.Max(m, Math.Abs(s[k] - s[k - 1]));
    return m;
}

Console.WriteLine("\n=== segments ===");
foreach (var sg in merged)
{
    if (!sg.tone && sg.start == 0) continue;
    Console.WriteLine($"{(sg.tone ? "TONE   " : "SILENCE")} start={Sec(sg.start):F3}s  dur={(sg.end - sg.start) * 1000.0 / Rate:F1}ms");
}

// 定常部の実測振幅・周波数(各トーン区間の中央を使う)で基準を作り直す
double steadyPeak = 0; int zc = 0, zcSamples = 0;
foreach (var sg in merged.Where(m => m.tone && m.end - m.start > Rate * 12 / 10))
{
    int a = sg.start + Rate * 6 / 10, b = sg.end - Rate * 6 / 10;
    steadyPeak = Math.Max(steadyPeak, PeakIn(a, b));
    for (int k = a + 1; k < b; k++) { if ((s[k - 1] < 0) != (s[k] < 0)) zc++; }
    zcSamples += b - a;
}
double freq = zcSamples > 0 ? zc / 2.0 / (zcSamples / (double)Rate) : 0;
if (steadyPeak > 0) natural = steadyPeak * 2 * Math.PI * 440 / Rate;
Console.WriteLine($"\nsteady peak = {steadyPeak:F3}   estimated frequency = {freq:F1} Hz");
Console.WriteLine($"natural max step of this tone = {natural:F4}");
double refPeak = steadyPeak > 0 ? steadyPeak : Amp;
int idx = 0;
double worst = 0;
for (int k = 0; k < merged.Count; k++)
{
    var sg = merged[k];
    if (!sg.tone) continue;
    int ms1 = Rate / 1000;

    // 音の始まり(直前が無音)
    if (k > 0 && !merged[k - 1].tone)
    {
        int st = sg.start;
        int r90 = st;
        while (r90 < sg.end && PeakIn(r90, r90 + ms1) < refPeak * 0.9) r90 += ms1;
        double step = MaxStep(st - 20, st + (int)(0.5 * Rate));
        worst = Math.Max(worst, step);
        Console.WriteLine($"[onset #{++idx}] first5ms-peak={PeakIn(st, st + 5 * ms1):F3}  ramp-to-90%={(r90 - st) / (double)ms1:F0}ms  maxStep={step:F4} ({step / natural:F2}x natural)");
    }
    // 音の終わり(直後が無音)
    if (k + 1 < merged.Count && !merged[k + 1].tone)
    {
        int en = sg.end;
        int f90 = en;
        while (f90 > sg.start && PeakIn(f90 - ms1, f90) < refPeak * 0.9) f90 -= ms1;
        double endLevel = PeakIn(en - 2 * ms1, en);
        double step = MaxStep(en - (int)(0.4 * Rate), en + 20);
        worst = Math.Max(worst, step);
        Console.WriteLine($"[cutoff #{idx}] level-in-last-2ms={endLevel:F4}  fade-length={(en - f90) / (double)ms1:F0}ms  maxStep={step:F4} ({step / natural:F2}x natural)");
    }
}
Console.WriteLine($"\nWORST step = {worst:F4} = {worst / natural:F2}x natural   => {(worst <= natural * 1.5 ? "PASS (no click)" : "FAIL (click)")}");
