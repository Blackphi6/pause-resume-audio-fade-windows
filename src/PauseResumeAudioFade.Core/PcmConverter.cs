using System.Buffers.Binary;

namespace PauseResumeAudioFade.Core;

/// <summary>キャプチャしたバイト列(16/24/32bit PCM または 32bit float)をfloatへ変換する。</summary>
public static class PcmConverter
{
    public static bool IsSupported(int bitsPerSample, bool isFloat) =>
        isFloat ? bitsPerSample == 32 : bitsPerSample is 16 or 24 or 32;

    /// <summary>変換して書き込んだサンプル数を返す。dst は src のサンプル数以上の長さが必要。</summary>
    public static int ToFloat(ReadOnlySpan<byte> src, int bitsPerSample, bool isFloat, Span<float> dst)
    {
        int bytes = bitsPerSample / 8;
        int n = src.Length / bytes;
        if (dst.Length < n) throw new ArgumentException("dst too small", nameof(dst));

        if (isFloat && bitsPerSample == 32)
        {
            for (int i = 0; i < n; i++) dst[i] = BitConverter.ToSingle(src.Slice(i * 4, 4));
        }
        else if (bitsPerSample == 16)
        {
            for (int i = 0; i < n; i++) dst[i] = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(i * 2, 2)) / 32768f;
        }
        else if (bitsPerSample == 24)
        {
            for (int i = 0; i < n; i++)
            {
                int o = i * 3;
                int v = (src[o] | (src[o + 1] << 8) | (src[o + 2] << 16));
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                dst[i] = v / 8388608f;
            }
        }
        else if (bitsPerSample == 32)
        {
            for (int i = 0; i < n; i++) dst[i] = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(i * 4, 4)) / 2147483648f;
        }
        else
        {
            throw new NotSupportedException($"{bitsPerSample}bit isFloat={isFloat}");
        }
        return n;
    }
}
