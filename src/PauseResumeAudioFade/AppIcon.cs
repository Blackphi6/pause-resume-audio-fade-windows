using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PauseResumeAudioFade.App;

/// <summary>アプリのアイコン(埋め込みの app.ico)と、トレイ用の「状態バッジ付きアイコン」を作る。</summary>
internal static class AppIcon
{
    private static Icon? _cached;

    /// <summary>ウィンドウ用のアイコン(複数サイズ入り。OSが最適なものを選ぶ)。</summary>
    public static Icon Window => _cached ??= LoadEmbedded();

    private static Icon LoadEmbedded()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            if (s != null) return new Icon(s);
        }
        catch (Exception ex) { Log.Warn($"app icon load failed: {ex.Message}"); }
        return SystemIcons.Application;
    }

    /// <summary>指定サイズのアイコン画像(埋め込みICOから、最も近いサイズの層を取り出す)。</summary>
    private static Bitmap BaseBitmap(int size)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            if (s != null)
            {
                using var ico = new Icon(s, new Size(size, size));
                var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawIcon(ico, new Rectangle(0, 0, size, size));
                return bmp;
            }
        }
        catch (Exception ex) { Log.Warn($"tray base icon failed: {ex.Message}"); }
        return new Bitmap(size, size);
    }

    /// <summary>
    /// トレイ用: アプリのアイコンに、状態を示す小さなバッジを重ねる。
    /// 動作中=アイコンのみ / 停止中=グレーにして暗く / 準備が必要=橙のバッジ / エラー=赤のバッジ。
    /// </summary>
    public static Icon ForStatus(EngineStatus status, int size = 32)
    {
        using var bmp = BaseBitmap(size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (status == EngineStatus.Stopped)
                Desaturate(bmp);

            Color? badge = status switch
            {
                EngineStatus.SinkMissing => Color.FromArgb(240, 150, 0),
                EngineStatus.Error => Color.FromArgb(220, 50, 50),
                _ => null,
            };
            if (badge is { } c)
            {
                int d = Math.Max(7, size * 9 / 20);                  // バッジの直径(32pxで約14px)
                var r = new Rectangle(size - d, size - d, d - 1, d - 1);
                using var white = new SolidBrush(Color.White);
                using var fill = new SolidBrush(c);
                g.FillEllipse(white, r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2);   // 白い縁取りで、どの背景でも見えるように
                g.FillEllipse(fill, r);
            }
        }

        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    private static void Desaturate(Bitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                int v = (int)(p.R * 0.3 + p.G * 0.59 + p.B * 0.11);
                v = 70 + v * 3 / 5;   // 少し暗くして、「止まっている」ことが分かるように
                bmp.SetPixel(x, y, Color.FromArgb(p.A, v, v, v));
            }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
