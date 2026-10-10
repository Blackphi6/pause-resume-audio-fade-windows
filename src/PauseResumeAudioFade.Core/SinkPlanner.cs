using System.Text.RegularExpressions;

namespace PauseResumeAudioFade.Core;

/// <summary>再生デバイス1台の、選択に必要な情報だけを抜き出したもの(NAudio非依存にしてテストしやすくする)。</summary>
public sealed record EndpointInfo(string Id, string Name, bool IsDefault, bool HasActiveSession);

public enum PlanFailure
{
    None,
    /// <summary>実際に鳴らす出力デバイスが1台も無い。</summary>
    NoOutput,
    /// <summary>元の音を逃がす「使っていない出力」(仮想ケーブル、または余っている物理出力)が無い。</summary>
    NoSink,
}

/// <param name="Sink">アプリの音を流し込む(鳴らさない)デバイス。</param>
/// <param name="Real">加工後の音を実際に鳴らすデバイス。</param>
/// <param name="SinkIsVirtual">仮想ケーブルならtrue。falseの場合は物理出力を借りるので、エンドポイントミュートが必要。</param>
public sealed record SinkPlan(EndpointInfo? Sink, EndpointInfo? Real, bool SinkIsVirtual, PlanFailure Failure)
{
    public bool Ok => Failure == PlanFailure.None;
}

/// <summary>
/// どのデバイスをシンク(元の音の逃がし先)にし、どれで実際に鳴らすかを決める。
/// 優先順位: 仮想ケーブル(VB-CABLE等) → 余っている別の出力デバイス → 見つからない(案内を出す)。
/// </summary>
public static class SinkPlanner
{
    // 他の目的で使われている仮想デバイスは、勝手にミュートすると困るので借りない
    private static readonly Regex Denied = new(
        @"steam|oculus|meta quest|virtual desktop|remote audio|リモート オーディオ|nvidia broadcast|voicemeeter|vb-audio hi-fi|\bobs\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MonitorLike = new(
        @"hdmi|displayport|display|デジタル|digital|nvidia high|amd high",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 仮想ケーブル系のデバイス(VB-CABLEは「CABLE Input」のほかに「CABLE In 16ch」なども作る)。
    // 指定した名前に一致しなくても、実際に鳴らす先や「借りる出力」には決して選ばない
    private static readonly Regex VirtualLike = new(
        @"vb-audio|virtual cable|\bcable[ -]?(in|out|a|b|c|d)\b|voicemeeter",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsVirtualCable(EndpointInfo e, string virtualName) =>
        !string.IsNullOrWhiteSpace(virtualName) && e.Name.Contains(virtualName, StringComparison.OrdinalIgnoreCase);

    public static SinkPlan Plan(
        IReadOnlyList<EndpointInfo> endpoints,
        string virtualName,
        string? preferredRealId,
        string? configuredRealId,
        string? preferredSinkId,
        bool allowSpare)
    {
        var cable = endpoints.FirstOrDefault(e => IsVirtualCable(e, virtualName));
        var candidates = endpoints.Where(e => !IsVirtualCable(e, virtualName) && !VirtualLike.IsMatch(e.Name)).ToList();

        // 実際に鳴らす先: 前回使っていたもの → 設定で指定したもの → いまの既定 → 最初の1台
        var real =
            Find(candidates, preferredRealId) ??
            Find(candidates, configuredRealId) ??
            candidates.FirstOrDefault(e => e.IsDefault) ??
            candidates.FirstOrDefault();

        if (real == null) return new SinkPlan(null, null, false, PlanFailure.NoOutput);

        if (cable != null) return new SinkPlan(cable, real, true, PlanFailure.None);
        if (!allowSpare) return new SinkPlan(null, real, false, PlanFailure.NoSink);

        // 余っている物理出力: 実出力以外で、他のアプリが今使っておらず、他用途の仮想デバイスでないもの
        var spare = candidates
            // 前回自分で選んだシンクは「使用中」とみなさない(再起動の直後は、アプリの音がまだそこに残っているため)
            .Where(e => e.Id != real.Id && (!e.HasActiveSession || e.Id == preferredSinkId) && !Denied.IsMatch(e.Name))
            .OrderByDescending(e => e.Id == preferredSinkId)   // 前回と同じものを優先(安定させる)
            .ThenByDescending(e => MonitorLike.IsMatch(e.Name)) // モニター出力は、ミュートしても困りにくい
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        return spare != null
            ? new SinkPlan(spare, real, false, PlanFailure.None)
            : new SinkPlan(null, real, false, PlanFailure.NoSink);
    }

    private static EndpointInfo? Find(List<EndpointInfo> list, string? id) =>
        string.IsNullOrEmpty(id) ? null : list.FirstOrDefault(e => e.Id == id);
}
