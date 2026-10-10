namespace PauseResumeAudioFade.Core;

/// <summary>フェード/検出の設定値。Mac版(lookaheadプリセット、無音250ms以上=再開、60〜250ms=シーク)に準拠。</summary>
public sealed record FadeParameters
{
    /// <summary>出力を常時遅延させる量(先読み)。フェードアウトはこの範囲内でしかかけられない。</summary>
    public int LookaheadMs { get; init; } = 100;

    /// <summary>フェードアウト長。LookaheadMsより短くする(切断点でゲインを確実に0にするため)。</summary>
    public int FadeOutMs { get; init; } = 84;

    /// <summary>再開時のフェードイン長。</summary>
    public int FadeInMs { get; init; } = 300;

    /// <summary>シーク時のフェードイン長(0なら FadeInMs と同じ)。</summary>
    public int SeekFadeInMs { get; init; } = 0;

    /// <summary>無音(デジタルゼロ)がこの長さ続いたら「音が止まった」とみなす。先読み−フェード長(標準で16ms)以内にする必要がある。短くしすぎると、デジタルな無音が短く挟まる音源(ゲートの効いた電子音楽等)の音を誤ってフェードしてしまう。実機ではOSの拡張処理が切断後に約10msの「しっぽ」を残すことがあり、20ms未満の瞬断は検出できないが許容する。</summary>
    public int MinSilenceMs { get; init; } = 10;

    /// <summary>この長さ未満の無音は単なる瞬断(短いフェードインのみ)。以上はシーク扱い。</summary>
    public int SeekMinMs { get; init; } = 60;

    /// <summary>この長さ以上の無音は「再開」扱い。SeekMinMs〜この値の間が「シーク」。</summary>
    public int ResumeMinMs { get; init; } = 250;

    /// <summary>瞬断(SeekMinMs未満)の後のフェードイン長。</summary>
    public int MicroFadeInMs { get; init; } = 15;

    /// <summary>この振幅以下のサンプルを無音とみなす(厳密なデジタルゼロに近い値)。</summary>
    public float SilenceThreshold { get; init; } = 1e-4f;

    /// <summary>
    /// 無音と確定したあと、「音が戻った」とみなす振幅。SilenceThreshold より大きくする(ヒステリシス)。
    /// 仮想ケーブル等の変換で、無音の途中に1e-4前後の微小な雑音が数サンプル混ざることがあり、これを「再開」と誤認すると、
    /// 本物の音が戻る前にフェードインが進んでしまい、再開の瞬間に段差が出る。約−60dBなら、可聴な再開を取りこぼさない。
    /// </summary>
    public float ResumeThreshold { get; init; } = 1e-3f;

    /// <summary>falseならフェードアウトを行わない(フェードインのみ)。</summary>
    public bool FadeOutEnabled { get; init; } = true;

    public int EffectiveSeekFadeInMs => SeekFadeInMs > 0 ? SeekFadeInMs : FadeInMs;

    /// <summary>Mac版プリセット: フェードアウト長 = 先読み - 16ms(標準 100ms→84ms、音楽 350ms→334ms)。</summary>
    public static FadeParameters ForLookahead(int lookaheadMs, int fadeInMs = 300) => new()
    {
        LookaheadMs = lookaheadMs,
        FadeOutMs = Math.Max(1, lookaheadMs - 16),
        FadeInMs = fadeInMs,
    };

    public static FadeParameters Short(int fadeInMs = 300) => ForLookahead(50, fadeInMs);
    public static FadeParameters Standard(int fadeInMs = 300) => ForLookahead(100, fadeInMs);
    public static FadeParameters Long(int fadeInMs = 300) => ForLookahead(200, fadeInMs);
    public static FadeParameters Music(int fadeInMs = 300) => ForLookahead(350, fadeInMs);
}

public enum GapKind
{
    /// <summary>SeekMinMs未満の無音(瞬断)。</summary>
    Micro,
    /// <summary>SeekMinMs〜ResumeMinMs(シーク)。</summary>
    Seek,
    /// <summary>ResumeMinMs以上(一時停止→再開)。</summary>
    Resume,
}

public readonly record struct GapInfo(long StartFrame, double LengthMs, GapKind Kind);
