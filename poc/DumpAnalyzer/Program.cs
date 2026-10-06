// エンジンの --dump で書き出した生float(インターリーブ)を解析する。
//   DumpAnalyzer <file.f32> <rate> <channels>
// 目的: 「一時停止の切断点の後にどれだけ"しっぽ"が残るか」「フェードの形」「クリック有無」を数値で見る。
using System.Runtime.InteropServices;

string path = args[0];
int rate = int.Parse(args[1]);
int ch = int.Parse(args[2]);

var bytes = File.ReadAllBytes(path);
var all = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
int n = all.Length / ch;
var x = new float[n];
for (int i = 0; i < n; i++) x[i] = all[i * ch];
Console.WriteLine($"{Path.GetFileName(path)}: {n} frames = {n / (double)rate:F2}s");

int ms = rate / 1000;
double Peak(int a, int b)
{
    double m = 0;
    for (int k = Math.Max(0, a); k < Math.Min(n, b); k++) m = Math.Max(m, Math.Abs(x[k]));
    return m;
}
double MaxStep(int a, int b)
{
    double m = 0;
    for (int k = Math.Max(1, a); k < Math.Min(n, b); k++) m = Math.Max(m, Math.Abs(x[k] - x[k - 1]));
    return m;
}

// 定常振幅: 1ms窓ピークの全体での99.5パーセンタイル
var winPeaks = new List<double>();
for (int i = 0; i + ms <= n; i += ms) winPeaks.Add(Peak(i, i + ms));
winPeaks.Sort();
double steady = winPeaks.Count > 0 ? winPeaks[(int)(winPeaks.Count * 0.995)] : 0;
Console.WriteLine($"steady peak ~ {steady:F3}");
double natural = steady * 2 * Math.PI * 440 / rate;
Console.WriteLine($"natural max step (440Hz) = {natural:F4}");

// 1ms窓ごとの「鳴っている(>5%定常)/いない」で大まかな区間に分け、境界ごとに詳細を出す
bool Loud(int w) => Peak(w * ms, (w + 1) * ms) > steady * 0.05;
int W = n / ms;
var bounds = new List<(bool toLoud, int w)>();
bool prev = Loud(0);
// 短い欠け(正弦波のゼロクロス付近の窓)で境界を誤検出しないよう、3ms以上続いたときだけ境界とする
for (int w = 1; w < W - 3; w++)
{
    bool cur = Loud(w);
    if (cur != prev)
    {
        bool stable = true;
        for (int k = 1; k < 3; k++) if (Loud(w + k) != cur) stable = false;
        if (stable) { bounds.Add((cur, w)); prev = cur; }
    }
}

Console.WriteLine($"\n{bounds.Count} boundaries\n");
foreach (var (toLoud, w) in bounds)
{
    int idx = w * ms;
    if (!toLoud)
    {
        // 音 → 無音 (切断)
        Console.WriteLine($"--- CUT at {idx / (double)rate:F3}s ---");
        // しっぽ: 切断(5%を下回った点)以降、窓ピークが 1e-2 / 1e-3 / 1e-4 を最後に超えていた時刻までの時間
        foreach (double th in new[] { 1e-2, 1e-3, 1e-4 })
        {
            int last = idx;
            for (int k = idx; k < Math.Min(n, idx + rate / 2); k++) if (Math.Abs(x[k]) > th) last = k;
            Console.WriteLine($"  tail above {th:E0}: {(last - idx) * 1000.0 / rate:F1} ms after the cut");
        }
        Console.Write("  envelope(ms before cut, 10ms steps) : ");
        for (int back = 120; back >= 0; back -= 10) Console.Write($"{Peak(idx - back * ms, idx - back * ms + 10 * ms) / steady:F2} ");
        Console.WriteLine();
        Console.WriteLine($"  maxStep(-300ms..+20ms) = {MaxStep(idx - 300 * ms, idx + 20 * ms):F4} ({MaxStep(idx - 300 * ms, idx + 20 * ms) / natural:F2}x natural)");
    }
    else
    {
        // 無音 → 音 (再開)
        Console.WriteLine($"--- ONSET at {idx / (double)rate:F3}s ---");
        // 無音の長さ(前の境界から)
        Console.Write("  envelope(10ms steps, 0..400ms)       : ");
        for (int t = 0; t < 400; t += 10) Console.Write($"{Peak(idx + t * ms, idx + (t + 10) * ms) / steady:F2} ");
        Console.WriteLine();
        Console.WriteLine($"  maxStep(-20ms..+500ms) = {MaxStep(idx - 20 * ms, idx + 500 * ms):F4} ({MaxStep(idx - 20 * ms, idx + 500 * ms) / natural:F2}x natural)");
    }
}
