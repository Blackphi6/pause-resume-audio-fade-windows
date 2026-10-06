using PauseResumeAudioFade.Core;
using Xunit;

namespace PauseResumeAudioFade.Tests;

public class PcmConverterTests
{
    [Fact]
    public void Int16_ConvertsToUnitRange()
    {
        byte[] src = { 0x00, 0x80, 0xFF, 0x7F, 0x00, 0x00 }; // -32768, 32767, 0
        var dst = new float[3];
        Assert.Equal(3, PcmConverter.ToFloat(src, 16, false, dst));
        Assert.Equal(-1f, dst[0]);
        Assert.Equal(32767f / 32768f, dst[1], 5);
        Assert.Equal(0f, dst[2]);
    }

    [Fact]
    public void Int24_SignExtends()
    {
        byte[] src = { 0x00, 0x00, 0x80, 0xFF, 0xFF, 0x7F }; // -8388608, 8388607
        var dst = new float[2];
        PcmConverter.ToFloat(src, 24, false, dst);
        Assert.Equal(-1f, dst[0]);
        Assert.Equal(8388607f / 8388608f, dst[1], 6);
    }

    [Fact]
    public void Int32_AndFloat32()
    {
        var i32 = BitConverter.GetBytes(int.MinValue);
        var dst = new float[1];
        PcmConverter.ToFloat(i32, 32, false, dst);
        Assert.Equal(-1f, dst[0]);

        var f32 = BitConverter.GetBytes(0.25f);
        PcmConverter.ToFloat(f32, 32, true, dst);
        Assert.Equal(0.25f, dst[0]);
    }

    [Theory]
    [InlineData(16, false, true)]
    [InlineData(24, false, true)]
    [InlineData(32, false, true)]
    [InlineData(32, true, true)]
    [InlineData(8, false, false)]
    [InlineData(64, true, false)]
    public void IsSupported(int bits, bool isFloat, bool expected) =>
        Assert.Equal(expected, PcmConverter.IsSupported(bits, isFloat));
}
