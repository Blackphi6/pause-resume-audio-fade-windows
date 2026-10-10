using PauseResumeAudioFade.Core;
using Xunit;

namespace PauseResumeAudioFade.Tests;

public class SinkPlannerTests
{
    private static EndpointInfo E(string id, string name, bool def = false, bool busy = false) => new(id, name, def, busy);

    private static SinkPlan Plan(IReadOnlyList<EndpointInfo> eps, string? prefReal = null, string? cfgReal = null,
        string? prefSink = null, bool allowSpare = true) =>
        SinkPlanner.Plan(eps, "CABLE Input", prefReal, cfgReal, prefSink, allowSpare);

    [Fact]
    public void Cable_present_is_used_as_virtual_sink_and_default_is_real()
    {
        var p = Plan(new[] { E("cable", "CABLE Input (VB-Audio Virtual Cable)"), E("spk", "スピーカー", def: true), E("hp", "ヘッドホン") });
        Assert.True(p.Ok);
        Assert.Equal("cable", p.Sink!.Id);
        Assert.True(p.SinkIsVirtual);
        Assert.Equal("spk", p.Real!.Id);
    }

    [Fact]
    public void Other_virtual_cable_devices_are_never_real_output_or_spare()
    {
        // VB-CABLE は「CABLE Input」のほかに「CABLE In 16ch」も作る。実機で見つかった並びを再現
        var eps = new[]
        {
            E("c16", "CABLE In 16ch (VB-Audio Virtual Cable)"),
            E("spk", "スピーカー (Senary Audio)"),
            E("cable", "CABLE Input (VB-Audio Virtual Cable)", def: true),
        };
        var p = Plan(eps);
        Assert.Equal("cable", p.Sink!.Id);
        Assert.Equal("spk", p.Real!.Id);

        // ケーブルが「CABLE Input」と一致しない名前でも、鳴らす先には選ばれない
        var q = SinkPlanner.Plan(eps, "no-such-name", null, null, null, allowSpare: true);
        Assert.Equal("spk", q.Real!.Id);
        Assert.NotEqual("c16", q.Sink?.Id);
        Assert.Equal(PlanFailure.NoSink, q.Failure);
    }

    [Fact]
    public void Cable_is_never_chosen_as_real_output()
    {
        // 既定がケーブルになっていても(前回異常終了など)、実出力にはケーブル以外を選ぶ
        var p = Plan(new[] { E("cable", "CABLE Input", def: true), E("spk", "スピーカー") });
        Assert.Equal("spk", p.Real!.Id);
    }

    [Fact]
    public void Without_cable_a_spare_physical_output_is_borrowed_and_muted()
    {
        var p = Plan(new[] { E("hp", "ヘッドホン", def: true), E("spk", "スピーカー") });
        Assert.True(p.Ok);
        Assert.Equal("spk", p.Sink!.Id);
        Assert.False(p.SinkIsVirtual);
        Assert.Equal("hp", p.Real!.Id);
    }

    [Fact]
    public void Spare_with_active_playback_is_not_borrowed()
    {
        // 別のアプリが今そのデバイスで鳴らしている → ミュートすると困るので借りない
        var p = Plan(new[] { E("hp", "ヘッドホン", def: true), E("spk", "スピーカー", busy: true) });
        Assert.Equal(PlanFailure.NoSink, p.Failure);
        Assert.Equal("hp", p.Real!.Id);
    }

    [Theory]
    [InlineData("ヘッドホン (Senary Audio)", true)]
    [InlineData("Headphones (Realtek(R) Audio)", true)]
    [InlineData("ヘッドホン (WH-1000XM5 Stereo)", true)]
    [InlineData("ヘッドセット イヤホン (WH-1000XM5 Hands-Free)", false)]  // 通話用の低音質プロファイルには切り替えない
    [InlineData("USB Audio DAC", true)]
    [InlineData("スピーカー (Senary Audio)", false)]
    [InlineData("Speakers (Realtek(R) Audio)", false)]
    [InlineData("Digital Audio (HDMI) (High Definition Audio Device)", false)]
    [InlineData("DELL U2720Q (NVIDIA High Definition Audio)", false)]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", false)]
    [InlineData("Steam Streaming Speakers", false)]
    public void AutoSwitch_applies_only_to_newly_connected_listening_devices(string name, bool expected)
    {
        Assert.Equal(expected, SinkPlanner.IsAutoSwitchCandidate(name));
    }

    [Fact]
    public void Our_own_previous_sink_is_not_treated_as_busy_after_restart()
    {
        var eps = new[] { E("hp", "ヘッドホン", def: true), E("spk", "スピーカー", busy: true) };
        Assert.Equal("spk", Plan(eps, prefSink: "spk").Sink!.Id);
        Assert.Equal(PlanFailure.NoSink, Plan(eps).Failure);
    }

    [Fact]
    public void Single_output_needs_guidance()
    {
        var p = Plan(new[] { E("spk", "スピーカー", def: true) });
        Assert.Equal(PlanFailure.NoSink, p.Failure);
        Assert.Equal("spk", p.Real!.Id);
    }

    [Fact]
    public void No_outputs_at_all()
    {
        Assert.Equal(PlanFailure.NoOutput, Plan(Array.Empty<EndpointInfo>()).Failure);
        // ケーブルしか無い場合も「鳴らす先が無い」
        Assert.Equal(PlanFailure.NoOutput, Plan(new[] { E("cable", "CABLE Input", def: true) }).Failure);
    }

    [Fact]
    public void Spare_selection_can_be_disabled()
    {
        var p = Plan(new[] { E("hp", "ヘッドホン", def: true), E("spk", "スピーカー") }, allowSpare: false);
        Assert.Equal(PlanFailure.NoSink, p.Failure);
    }

    [Fact]
    public void Monitor_outputs_are_preferred_as_spare()
    {
        var p = Plan(new[] { E("hp", "ヘッドホン", def: true), E("spk", "スピーカー"), E("hdmi", "Digital Audio (HDMI)") });
        Assert.Equal("hdmi", p.Sink!.Id);
    }

    [Fact]
    public void Previously_used_spare_is_kept_for_stability()
    {
        var eps = new[] { E("hp", "ヘッドホン", def: true), E("spk", "スピーカー"), E("hdmi", "Digital Audio (HDMI)") };
        Assert.Equal("spk", Plan(eps, prefSink: "spk").Sink!.Id);
    }

    [Theory]
    [InlineData("Steam Streaming Speakers")]
    [InlineData("Oculus Virtual Audio Device")]
    [InlineData("Remote Audio")]
    [InlineData("Voicemeeter Input")]
    public void Special_purpose_virtual_devices_are_never_borrowed(string name)
    {
        var p = Plan(new[] { E("hp", "ヘッドホン", def: true), E("x", name) });
        Assert.Equal(PlanFailure.NoSink, p.Failure);
    }

    [Fact]
    public void Preferred_real_beats_default_and_configured()
    {
        var eps = new[] { E("a", "A", def: true), E("b", "B"), E("c", "C") };
        Assert.Equal("c", Plan(eps, prefReal: "c", cfgReal: "b").Real!.Id);
        Assert.Equal("b", Plan(eps, cfgReal: "b").Real!.Id);
        Assert.Equal("a", Plan(eps).Real!.Id);
        // 指定したデバイスがもう無ければ既定にフォールバック
        Assert.Equal("a", Plan(eps, prefReal: "gone").Real!.Id);
    }

    [Fact]
    public void Real_is_never_also_the_sink()
    {
        var p = Plan(new[] { E("hp", "ヘッドホン", def: true), E("spk", "スピーカー") }, prefReal: "spk");
        Assert.Equal("spk", p.Real!.Id);
        Assert.Equal("hp", p.Sink!.Id);
    }
}
