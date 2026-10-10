namespace PauseResumeAudioFade.Core;

/// <summary>
/// 先読み(lookahead)バッファ+「遡って」かけるフェード処理。デバイスに依存しない純粋なDSP。
///
/// 使い方: キャプチャ側のスレッドが <see cref="Write"/> で音声(インターリーブfloat)を流し込み、
/// 再生側のスレッドが <see cref="Read"/> で常に <see cref="FadeParameters.LookaheadMs"/> だけ遅れた音声を取り出す。
///
/// 仕組み(Mac版FadeProcessorと同じ発想):
///  - 一時停止すると音声が厳密な無音(デジタルゼロ)になる。その無音が MinSilenceMs 続いた時点で、
///    まだ再生されていない無音直前の FadeOutMs 分の音声に、その場でフェードアウトのカーブを掛け直す。
///    (フェードは無音の開始点で必ず0になる = 切断クリックが出ない)
///  - 無音の長さで再開/シーク/瞬断を分類し、音が戻ってきた最初のサンプルからフェードインを掛ける。
///  - キャプチャ側と再生側のクロックのずれは、バッファ残量を見て読み出し速度をごくわずかに変える(補間)ことで吸収する。
/// </summary>
public sealed class LookaheadProcessor
{
    private readonly int _rate;
    private readonly int _ch;
    private readonly float[] _ring;
    private readonly long _cap; // フレーム数
    private readonly object _lock = new();

    private FadeParameters _p;
    private long _write;       // 書き込んだ総フレーム数
    private double _readPos;   // 読み出し位置(総フレーム数、小数)
    private bool _primed;
    private double _fillEma;
    private bool _fillEmaInit;

    private long _silenceStart = -1;
    private bool _fadeOutCommitted;
    private int _fadeInTotal;
    private int _fadeInPos;

    public long UnderrunCount { get; private set; }
    public long OverrunCount { get; private set; }

    /// <summary>無音区間が終わって音が戻ったときに発火(ロックの外から呼ばれる)。診断/ログ用。</summary>
    public event Action<GapInfo>? GapDetected;

    public int SampleRate => _rate;
    public int Channels => _ch;

    /// <summary>現在のバッファ残量(ms)= 実質的な遅延。</summary>
    public double FillMs { get { lock (_lock) return (_write - _readPos) * 1000.0 / _rate; } }

    public LookaheadProcessor(int sampleRate, int channels, FadeParameters parameters, int ringSeconds = 3)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _rate = sampleRate;
        _ch = channels;
        _p = parameters;
        _cap = (long)sampleRate * ringSeconds;
        _ring = new float[_cap * channels];
    }

    public FadeParameters Parameters
    {
        get { lock (_lock) return _p; }
    }

    public void UpdateParameters(FadeParameters p)
    {
        lock (_lock)
        {
            long oldLook = Frames(_p.LookaheadMs);
            long newLook = Frames(p.LookaheadMs);
            _p = p;
            if (newLook < oldLook)
            {
                // 先読みを減らす: 余分な分は先へ飛ばす(一度だけの小さな不連続)
                _readPos = Math.Max(_readPos, _write - newLook);
            }
            else if (newLook > oldLook)
            {
                // 先読みを増やす: 溜まり直すまで一旦無音を出す
                _primed = false;
            }
            _fillEmaInit = false;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _write = 0;
            _readPos = 0;
            _primed = false;
            _fillEmaInit = false;
            _silenceStart = -1;
            _fadeOutCommitted = false;
            _fadeInTotal = _fadeInPos = 0;
            Array.Clear(_ring);
        }
    }

    private long Frames(double ms) => (long)Math.Round(ms * _rate / 1000.0);

    /// <summary>インターリーブfloatのフレーム列を流し込む(キャプチャスレッドから)。</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        List<GapInfo>? gaps = null;
        lock (_lock)
        {
            var p = _p;
            int frames = samples.Length / _ch;
            long minSil = Math.Max(1, Frames(p.MinSilenceMs));
            float thr = p.SilenceThreshold;
            float resumeThr = Math.Max(thr, p.ResumeThreshold);

            for (int f = 0; f < frames; f++)
            {
                int si = f * _ch;
                // 無音と確定した後は、より大きい振幅を超えるまで無音が続いているとみなす(微小な雑音での誤認を防ぐ)
                float limit = _silenceStart >= 0 && _fadeOutCommitted ? resumeThr : thr;
                bool silent = true;
                for (int c = 0; c < _ch; c++)
                {
                    if (Math.Abs(samples[si + c]) > limit) { silent = false; break; }
                }

                long idx = _write;
                if (silent)
                {
                    if (_silenceStart < 0)
                    {
                        _silenceStart = idx;
                        _fadeOutCommitted = false;
                    }
                    if (!_fadeOutCommitted && idx - _silenceStart + 1 >= minSil)
                    {
                        _fadeOutCommitted = true;
                        if (p.FadeOutEnabled) CommitFadeOut(_silenceStart, p);
                    }
                }
                else if (_silenceStart >= 0)
                {
                    if (_fadeOutCommitted)
                    {
                        double lenMs = (idx - _silenceStart) * 1000.0 / _rate;
                        GapKind kind = lenMs < p.SeekMinMs ? GapKind.Micro
                                     : lenMs < p.ResumeMinMs ? GapKind.Seek
                                     : GapKind.Resume;
                        int fadeMs = kind switch
                        {
                            GapKind.Micro => p.MicroFadeInMs,
                            GapKind.Seek => p.EffectiveSeekFadeInMs,
                            _ => p.FadeInMs,
                        };
                        _fadeInTotal = (int)Math.Max(0, Frames(fadeMs));
                        _fadeInPos = 0;
                        (gaps ??= new()).Add(new GapInfo(_silenceStart, lenMs, kind));
                    }
                    _silenceStart = -1;
                    _fadeOutCommitted = false;
                }

                float gain = 1f;
                if (_fadeInPos < _fadeInTotal)
                {
                    gain = (float)(0.5 - 0.5 * Math.Cos(Math.PI * (_fadeInPos + 0.5) / _fadeInTotal));
                    _fadeInPos++;
                }

                int ri = (int)(idx % _cap) * _ch;
                for (int c = 0; c < _ch; c++) _ring[ri + c] = samples[si + c] * gain;
                _write = idx + 1;
            }
        }

        if (gaps != null)
        {
            var handler = GapDetected;
            if (handler != null) foreach (var g in gaps) handler(g);
        }
    }

    /// <summary>無音開始点 S の直前 FadeOutMs 分(未再生部分のみ)に、Sで0になるフェードアウトを掛ける。</summary>
    private void CommitFadeOut(long silenceStart, FadeParameters p)
    {
        long fadeFrames = Math.Max(2, Frames(p.FadeOutMs));
        long unread = (long)Math.Floor(_readPos) + 1;
        long oldest = _write - _cap + 1;
        long start = Math.Max(silenceStart - fadeFrames, Math.Max(unread, Math.Max(oldest, 0)));
        long n = silenceStart - start;
        if (n < 2) return;

        for (long i = 0; i < n; i++)
        {
            float g = (float)(0.5 + 0.5 * Math.Cos(Math.PI * (i + 0.5) / n));
            int ri = (int)((start + i) % _cap) * _ch;
            for (int c = 0; c < _ch; c++) _ring[ri + c] *= g;
        }
    }

    /// <summary>先読み分だけ遅れた音声を取り出す(再生スレッドから)。常に dest 全体を埋めて返す(足りなければ無音)。</summary>
    public int Read(Span<float> dest)
    {
        lock (_lock)
        {
            int frames = dest.Length / _ch;
            double target = Frames(_p.LookaheadMs);

            if (!_primed)
            {
                if (_write - _readPos >= target)
                {
                    _primed = true;
                    _fillEmaInit = false;
                }
                else
                {
                    dest.Clear();
                    return dest.Length;
                }
            }

            double fillNow = _write - _readPos;
            if (fillNow > target * 4 + Frames(200))
            {
                // 再生側が止まっていた等で溜まりすぎた: 目標位置まで飛ばす
                _readPos = _write - target;
                fillNow = target;
                OverrunCount++;
            }

            if (!_fillEmaInit) { _fillEma = fillNow; _fillEmaInit = true; }
            else _fillEma += (fillNow - _fillEma) * 0.1;

            // バッファ残量が目標より多ければ少し速く、少なければ少し遅く読む(±0.3%まで)
            double rate = 1.0 + Math.Clamp((_fillEma - target) * 2e-6, -3e-3, 3e-3);

            for (int f = 0; f < frames; f++)
            {
                long i0 = (long)Math.Floor(_readPos);
                if (i0 + 1 >= _write)
                {
                    dest.Slice(f * _ch).Clear();
                    UnderrunCount++;
                    _primed = false;
                    break;
                }

                float frac = (float)(_readPos - i0);
                int a = (int)(i0 % _cap) * _ch;
                int b = (int)((i0 + 1) % _cap) * _ch;
                int di = f * _ch;
                for (int c = 0; c < _ch; c++)
                {
                    float x0 = _ring[a + c];
                    float x1 = _ring[b + c];
                    dest[di + c] = x0 + (x1 - x0) * frac;
                }
                _readPos += rate;
            }

            return dest.Length;
        }
    }
}
