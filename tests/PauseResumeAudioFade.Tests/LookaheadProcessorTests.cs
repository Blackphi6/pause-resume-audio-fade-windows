using PauseResumeAudioFade.Core;
using Xunit;

namespace PauseResumeAudioFade.Tests;

public class LookaheadProcessorTests
{
    private const int Rate = 48000;
    private const int Ch = 2;
    private const double Amp = 0.4;
    // 440Hz・振幅0.4の正弦波が1サンプルで動く最大量(これを超える段差=クリック)
    private static readonly double NaturalMaxStep = Amp * 2 * Math.PI * 440 / Rate;
    // 1.5秒+1/4周期: 波形のピーク(振幅0.4)で切断される長さ。ゼロクロスで切ると、フェードが無くてもクリックが出ず試験にならない
    private const double PeakCutSec = 1.5 + 1.0 / (4 * 440.0);
    // このシミュレーション(480フレームずつ write→read)では、実遅延は 先読み - 1ブロック(10ms)
    private static double OutTime(double inSec, int lookaheadMs) => inSec + lookaheadMs / 1000.0 - 0.010;

    /// <summary>区間リスト(秒, 音ありか)から、位相が連続した440Hz正弦波(無音区間は厳密なゼロ)を作る。</summary>
    private static float[] MakeInput(params (double sec, bool tone)[] segs)
    {
        int total = (int)Math.Round(segs.Sum(s => s.sec) * Rate);
        var buf = new float[total * Ch];
        int pos = 0;
        foreach (var (sec, tone) in segs)
        {
            int n = (int)Math.Round(sec * Rate);
            for (int i = 0; i < n; i++, pos++)
            {
                float v = tone ? (float)(Amp * Math.Sin(2 * Math.PI * 440.0 * pos / Rate)) : 0f;
                for (int c = 0; c < Ch; c++) buf[pos * Ch + c] = v;
            }
        }
        return buf;
    }

    /// <summary>480フレームずつ書いて480フレームずつ読む(同一クロック)シミュレーション。出力は入力と同じ長さ。</summary>
    private static float[] Run(LookaheadProcessor p, float[] input, int block = 480)
    {
        var output = new float[input.Length];
        int frames = input.Length / Ch;
        for (int f = 0; f < frames; f += block)
        {
            int n = Math.Min(block, frames - f);
            p.Write(input.AsSpan(f * Ch, n * Ch));
            p.Read(output.AsSpan(f * Ch, n * Ch));
        }
        return output;
    }

    private static double MaxStep(float[] o, double fromSec, double toSec)
    {
        int a = (int)(fromSec * Rate), b = (int)(toSec * Rate);
        double max = 0;
        for (int i = Math.Max(1, a); i < b; i++)
            max = Math.Max(max, Math.Abs(o[i * Ch] - o[(i - 1) * Ch]));
        return max;
    }

    private static double PeakIn(float[] o, double fromSec, double toSec)
    {
        int a = (int)(fromSec * Rate), b = Math.Min((int)(toSec * Rate), o.Length / Ch);
        double max = 0;
        for (int i = a; i < b; i++) max = Math.Max(max, Math.Abs(o[i * Ch]));
        return max;
    }

    [Fact]
    public void SteadyTone_PassesThroughUnchangedAfterLookahead()
    {
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.Standard());
        var o = Run(p, MakeInput((3.0, true)));
        Assert.InRange(PeakIn(o, 0.5, 3.0), Amp * 0.99, Amp * 1.01);
        Assert.True(MaxStep(o, 0.5, 3.0) <= NaturalMaxStep * 1.05, "steady tone must stay click-free");
        Assert.InRange(p.FillMs, 85, 105); // 先読み100ms(read直後は1ブロック分少ない)
        Assert.Equal(0, p.UnderrunCount);
    }

    [Fact]
    public void FirstAudioIsDelayedByLookahead()
    {
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.Standard());
        var o = Run(p, MakeInput((0.5, false), (1.0, true)));
        // 入力は0.5sから鳴る。出力は 0.5s + 100ms(+フェードイン開始) より前は無音のはず
        Assert.True(PeakIn(o, 0.0, 0.59) < 0.01);
        Assert.True(PeakIn(o, 0.6, 1.5) > 0.2);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(350)]
    public void AbruptStop_HasNoClick_AndEndsAtZero(int lookaheadMs)
    {
        var prm = FadeParameters.ForLookahead(lookaheadMs);
        var p = new LookaheadProcessor(Rate, Ch, prm);
        // 約1.5s鳴る(ピークで切断) → 0.5s無音(=一時停止) → 1.0s鳴る
        var o = Run(p, MakeInput((PeakCutSec, true), (0.5, false), (1.0, true)));
        double cutOut = OutTime(PeakCutSec, lookaheadMs); // 切断点が出力に現れる時刻

        // 切断前後の全域でクリック(自然な正弦波以上の段差)がない
        Assert.True(MaxStep(o, cutOut - 0.3, cutOut + 0.3) <= NaturalMaxStep * 1.2,
            $"click detected: {MaxStep(o, cutOut - 0.3, cutOut + 0.3):F4} > {NaturalMaxStep * 1.2:F4}");

        // 切断点の直前にはほぼ0まで落ちている
        Assert.True(PeakIn(o, cutOut - 0.002, cutOut + 0.01) < 0.01, "must be silent right at the cut point");
        // フェード開始前(切断の fade長+30ms 前)はまだ満振幅
        Assert.True(PeakIn(o, cutOut - prm.FadeOutMs / 1000.0 - 0.03, cutOut - prm.FadeOutMs / 1000.0 - 0.005) > Amp * 0.95);
    }

    [Fact]
    public void WithoutLookaheadFade_TheCutWouldClick()
    {
        // 比較用: フェードアウト無効だと切断点で大きな段差が出る(テスト自体が有意であることの確認)
        var prm = FadeParameters.Standard() with { FadeOutEnabled = false };
        var p = new LookaheadProcessor(Rate, Ch, prm);
        var o = Run(p, MakeInput((PeakCutSec, true), (0.5, false), (1.0, true)));
        double cutOut = OutTime(PeakCutSec, 100);
        Assert.True(MaxStep(o, cutOut - 0.05, cutOut + 0.05) > NaturalMaxStep * 3);
    }

    [Fact]
    public void Resume_FadesInSmoothlyOver300ms()
    {
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.Standard());
        var o = Run(p, MakeInput((PeakCutSec, true), (0.5, false), (1.5, true)));
        double resumeOut = OutTime(PeakCutSec + 0.5, 100); // 再開音の先頭が出力に現れる時刻
        Assert.True(PeakIn(o, resumeOut, resumeOut + 0.010) < 0.06, "fade-in must start near zero");
        double mid = PeakIn(o, resumeOut + 0.140, resumeOut + 0.160);
        Assert.InRange(mid, Amp * 0.35, Amp * 0.65);
        Assert.True(PeakIn(o, resumeOut + 0.33, resumeOut + 0.40) > Amp * 0.97);
        Assert.True(MaxStep(o, resumeOut - 0.05, resumeOut + 0.5) <= NaturalMaxStep * 1.2);
    }

    [Theory]
    [InlineData(0.020, GapKind.Micro)]
    [InlineData(0.150, GapKind.Seek)]
    [InlineData(0.500, GapKind.Resume)]
    public void GapsAreClassifiedByLength(double gapSec, GapKind expected)
    {
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.Standard());
        var gaps = new List<GapInfo>();
        p.GapDetected += g => gaps.Add(g);
        Run(p, MakeInput((1.0, true), (gapSec, false), (1.0, true)));
        var gap = Assert.Single(gaps);
        Assert.Equal(expected, gap.Kind);
        Assert.InRange(gap.LengthMs, gapSec * 1000 - 1, gapSec * 1000 + 1);
    }

    [Fact]
    public void SeekGap_UsesSeekFadeIn()
    {
        var prm = FadeParameters.Standard(fadeInMs: 300) with { SeekFadeInMs = 100 };
        var p = new LookaheadProcessor(Rate, Ch, prm);
        var o = Run(p, MakeInput((1.0, true), (0.15, false), (1.0, true)));
        double resumeOut = OutTime(1.15, 100);
        Assert.True(PeakIn(o, resumeOut + 0.11, resumeOut + 0.2) > Amp * 0.97, "seek fade-in should be done in ~100ms");
    }

    [Fact]
    public void ShortSilenceBelowMinimum_IsIgnored()
    {
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.Standard());
        var gaps = new List<GapInfo>();
        p.GapDetected += g => gaps.Add(g);
        var o = Run(p, MakeInput((1.0, true), (0.003, false), (1.0, true)));
        Assert.Empty(gaps);
        Assert.True(PeakIn(o, 1.1, 1.12) > Amp * 0.9);
    }

    [Theory]
    [InlineData(100, 0.020)]
    [InlineData(350, 0.020)]
    [InlineData(350, 0.035)]
    public void MicroGap_FadeInIsSmooth_WithoutJumps(int lookaheadMs, double gapSec)
    {
        // 実機E2Eで、約20msの瞬断の再開時だけ段差(自然な波形の2.8倍)が出た。処理ロジック側の問題か切り分ける再現テスト
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.ForLookahead(lookaheadMs));
        var gaps = new List<GapInfo>();
        p.GapDetected += g => gaps.Add(g);
        double gapStart = PeakCutSec + 2.0;
        var o = Run(p, MakeInput((gapStart, true), (gapSec, false), (3.0, true)));
        Assert.Single(gaps);
        Assert.Equal(GapKind.Micro, gaps[0].Kind);

        double onsetOut = OutTime(gapStart + gapSec, lookaheadMs);
        double worst = MaxStep(o, onsetOut - 0.02, onsetOut + 0.5);
        Assert.True(worst <= NaturalMaxStep * 1.3, $"step after micro gap = {worst:F4} (natural {NaturalMaxStep:F4})");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(350)]
    public void TinyNoiseBlipInsideSilence_DoesNotStartFadeInEarly(int lookaheadMs)
    {
        // VB-CABLE 実機E2Eで見つかった不具合の再現: 無音の途中に、振幅1.2e-4(16bitで約4LSB)の雑音が17サンプルだけ混ざる。
        // これを「再開」と誤認すると、本物の音が戻る前にフェードインが進み、再開の瞬間に自然な波形の約3倍の段差が出た。
        int cutFrame = (int)((PeakCutSec + 2.0) * Rate);
        int gapFrames = (int)(0.030 * Rate);
        int total = cutFrame + gapFrames + 3 * Rate;
        var input = new float[total * Ch];
        for (int i = 0; i < total; i++)
        {
            double v;
            if (i < cutFrame) v = Amp * Math.Sin(2 * Math.PI * 440.0 * i / Rate);
            else if (i < cutFrame + gapFrames)
            {
                int k = i - cutFrame;
                v = (k >= 480 && k < 497) ? 1.2e-4 : 0.0; // 無音の途中の雑音
            }
            else v = Amp * Math.Sin(2 * Math.PI * 440.0 * i / Rate);
            for (int c = 0; c < Ch; c++) input[i * Ch + c] = (float)v;
        }

        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.ForLookahead(lookaheadMs));
        var gaps = new List<GapInfo>();
        p.GapDetected += g => gaps.Add(g);
        var o = Run(p, input);

        Assert.Single(gaps);
        Assert.True(gaps[0].LengthMs >= 29, $"gap was cut short by the noise blip: {gaps[0].LengthMs:F1}ms");
        double onsetOut = OutTime((cutFrame + gapFrames) / (double)Rate, lookaheadMs);
        double worst = MaxStep(o, onsetOut - 0.02, onsetOut + 0.5);
        Assert.True(worst <= NaturalMaxStep * 1.3, $"step after gap = {worst:F4} (natural {NaturalMaxStep:F4})");
    }

    [Fact]
    public void MicroGapAfterOsTail_IsStillSmoothed()
    {
        // 実機(拡張処理つきのエンドポイント)の挙動の再現: 切断後に約10msの減衰「しっぽ」が残り、
        // その後の厳密な無音はわずか10msしかない20msの瞬断。最小無音長が長すぎると検出できず、フルレベルの急な切断が出力に出てしまう。
        int cutFrame = (int)(1.0 * Rate + Rate / (4 * 440)); // 波形のピーク付近で切断
        int total = 3 * Rate;
        var input = new float[total * Ch];
        for (int i = 0; i < total; i++)
        {
            double v;
            if (i < cutFrame) v = Amp * Math.Sin(2 * Math.PI * 440.0 * i / Rate);
            else if (i < cutFrame + Rate / 100) // 10msの指数減衰のしっぽ
                v = Amp * Math.Sin(2 * Math.PI * 440.0 * i / Rate) * Math.Exp(-(i - cutFrame) / (0.002 * Rate));
            else if (i < cutFrame + Rate / 50) v = 0; // 続く10msは厳密な無音
            else v = Amp * Math.Sin(2 * Math.PI * 440.0 * i / Rate);
            for (int c = 0; c < Ch; c++) input[i * Ch + c] = (float)v;
        }
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.Standard());
        var gaps = new List<GapInfo>();
        p.GapDetected += g => gaps.Add(g);
        var o = Run(p, input);
        Assert.Single(gaps);
        double cutOut = OutTime(cutFrame / (double)Rate, 100);
        Assert.True(MaxStep(o, cutOut - 0.3, cutOut + 0.3) <= NaturalMaxStep * 1.3,
            $"click remained: {MaxStep(o, cutOut - 0.3, cutOut + 0.3):F4}");
    }

    [Fact]
    public void ClockDrift_IsAbsorbedWithoutUnderrunsOrGlitches()
    {
        // 書き込み側(キャプチャ)が読み出し側(再生)より 0.05% 速い状態を 120 秒続ける
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.Standard());
        double writeRate = Rate * 1.0005;
        double wDebt = 0, rDebt = 0;
        var wbuf = new float[Rate / 1000 * 2 * Ch * 2];
        var rbuf = new float[Rate / 1000 * 2 * Ch * 2];
        long wPos = 0;
        double prevL = 0;
        double maxStep = 0;
        for (int ms = 0; ms < 120_000; ms++)
        {
            wDebt += writeRate / 1000.0;
            int wn = (int)wDebt; wDebt -= wn;
            for (int i = 0; i < wn; i++, wPos++)
            {
                float v = (float)(Amp * Math.Sin(2 * Math.PI * 440.0 * wPos / writeRate));
                for (int c = 0; c < Ch; c++) wbuf[i * Ch + c] = v;
            }
            p.Write(wbuf.AsSpan(0, wn * Ch));

            rDebt += Rate / 1000.0;
            int rn = (int)rDebt; rDebt -= rn;
            p.Read(rbuf.AsSpan(0, rn * Ch));
            if (ms > 500)
                for (int i = 0; i < rn; i++)
                {
                    maxStep = Math.Max(maxStep, Math.Abs(rbuf[i * Ch] - prevL));
                    prevL = rbuf[i * Ch];
                }
            else prevL = rbuf[Math.Max(0, rn - 1) * Ch];
        }
        Assert.Equal(0, p.UnderrunCount);
        Assert.Equal(0, p.OverrunCount);
        Assert.InRange(p.FillMs, 90, 110);
        Assert.True(maxStep <= NaturalMaxStep * 1.3, $"resampling glitch: {maxStep:F4}");
    }

    [Fact]
    public void Underrun_RecoversAndPlaysAgain()
    {
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.Standard());
        var input = MakeInput((2.0, true));
        var output = new float[input.Length];
        int frames = input.Length / Ch;
        for (int f = 0; f < frames; f += 480)
        {
            bool starved = f >= Rate * 3 / 4 && f < Rate * 3 / 4 + Rate / 2; // 0.75s〜1.25sはキャプチャが止まる
            if (!starved) p.Write(input.AsSpan(f * Ch, 480 * Ch));
            p.Read(output.AsSpan(f * Ch, 480 * Ch));
        }
        Assert.True(p.UnderrunCount > 0);
        Assert.True(PeakIn(output, 1.7, 2.0) > Amp * 0.9, "must recover after underrun");
    }

    [Fact]
    public void PresetChange_AdjustsLookahead()
    {
        var p = new LookaheadProcessor(Rate, Ch, FadeParameters.Standard());
        var input = MakeInput((4.0, true));
        var output = new float[input.Length];
        int frames = input.Length / Ch;
        for (int f = 0; f < frames; f += 480)
        {
            if (f == Rate) p.UpdateParameters(FadeParameters.Music());
            p.Write(input.AsSpan(f * Ch, 480 * Ch));
            p.Read(output.AsSpan(f * Ch, 480 * Ch));
        }
        Assert.InRange(p.FillMs, 340, 360);
    }

    [Fact]
    public void FourChannelsAndMono_Work()
    {
        foreach (int ch in new[] { 1, 4 })
        {
            var p = new LookaheadProcessor(Rate, ch, FadeParameters.Standard());
            var buf = new float[480 * ch];
            var outb = new float[480 * ch];
            for (int k = 0; k < 40; k++)
            {
                for (int i = 0; i < buf.Length; i++) buf[i] = 0.3f;
                p.Write(buf);
                p.Read(outb);
            }
            Assert.Equal(0.3f, outb[0], 3);
        }
    }
}

