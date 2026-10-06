using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// テスト音源。
//   ToneTestPlayer                    3秒再生→Pause→2秒→Resume を3回(WaveOut.Pause方式)
//   ToneTestPlayer continuous         30秒連続再生
//   ToneTestPlayer script "tone:2,gap:0.5,tone:2,gap:0.15,tone:2"
//        ブラウザの挙動を再現: ストリームは止めず、tone=440Hz正弦波(振幅0.4,位相連続)、gap=厳密なデジタル無音を出し続ける。
//        gap の長さで 一時停止(>=0.25s) / シーク(0.06-0.25s) / 瞬断 を再現できる。

static string Ts() => DateTime.UtcNow.ToString("HH:mm:ss.fff");

string mode = args.Length > 0 ? args[0] : "cycle";
Console.WriteLine($"[{Ts()}] ToneTestPlayer start (PID={Environment.ProcessId}) mode={mode}");

if (mode == "script")
{
    var segs = new List<(bool tone, double sec)>();
    foreach (var part in args[1].Split(','))
    {
        var kv = part.Split(':');
        segs.Add((kv[0].Trim() == "tone", double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture)));
    }

    var src = new ScriptedTone(48000, 2, segs);
    using var wo = new WaveOutEvent();
    wo.Init(src.ToWaveProvider());
    wo.Play();
    Console.WriteLine($"[{Ts()}] PLAY (total {segs.Sum(s => s.sec):F2}s)");
    while (!src.Finished) Thread.Sleep(50);
    Thread.Sleep(600);
    wo.Stop();
    Console.WriteLine($"[{Ts()}] DONE");
    return;
}

var signal = new SignalGenerator(44100, 1) { Gain = 0.2, Frequency = 440, Type = SignalGeneratorType.Sin };
using var output = new WaveOutEvent();
output.Init(signal);
output.Play();
Console.WriteLine($"[{Ts()}] PLAY_CALLED (initial)");

if (mode == "continuous")
{
    Thread.Sleep(30000);
}
else
{
    for (int cycle = 1; cycle <= 3; cycle++)
    {
        Thread.Sleep(3000);
        output.Pause();
        Console.WriteLine($"[{Ts()}] PAUSE_CALLED (cycle {cycle})");
        Thread.Sleep(2000);
        output.Play();
        Console.WriteLine($"[{Ts()}] PLAY_CALLED (cycle {cycle})");
    }
}
Thread.Sleep(2000);
output.Stop();
Console.WriteLine($"[{Ts()}] STOP_CALLED");

sealed class ScriptedTone : ISampleProvider
{
    private readonly List<(bool tone, double sec)> _segs;
    private int _seg;
    private long _posInSeg;
    private long _total;
    private readonly int _rate, _ch;
    public bool Finished { get; private set; }
    public WaveFormat WaveFormat { get; }

    public ScriptedTone(int rate, int ch, List<(bool tone, double sec)> segs)
    {
        _rate = rate; _ch = ch; _segs = segs;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, ch);
    }

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / _ch;
        for (int f = 0; f < frames; f++)
        {
            float v = 0f;
            if (_seg < _segs.Count)
            {
                var (tone, sec) = _segs[_seg];
                if (tone) v = (float)(0.4 * Math.Sin(2 * Math.PI * 440.0 * _total / _rate));
                _posInSeg++;
                _total++;
                if (_posInSeg >= (long)Math.Round(sec * _rate)) { _seg++; _posInSeg = 0; }
            }
            else Finished = true;
            for (int c = 0; c < _ch; c++) buffer[f * _ch + c] = v;
        }
        return buffer.Length;
    }
}
