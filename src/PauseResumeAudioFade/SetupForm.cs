using System.Diagnostics;

namespace PauseResumeAudioFade.App;

/// <summary>
/// 初回(および「導入ガイド」メニュー)に出す案内画面。いまの状態に応じて、
/// 「準備できました」か「必要な準備の手順」を表示する。1秒ごとに状態を見直すので、VB-CABLEを入れ終わると自動で切り替わる。
/// </summary>
internal sealed class SetupForm : Form
{
    public const string CableUrl = "https://vb-audio.com/Cable/";

    private readonly RoutingEngine _engine;
    private readonly Label _title = new() { AutoSize = true, Font = new Font("Yu Gothic UI", 15f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _body = new() { AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, 0, 0, 12) };
    private readonly Button _openSite = new() { Text = "VB-CABLE の公式サイトを開く", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _close = new() { Text = "閉じる", AutoSize = true, Padding = new Padding(16, 4, 16, 4) };
    private readonly CheckBox _autostart = new() { Text = "Windows を起動したとき、自動で起動する", AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private EngineStatus _shown = (EngineStatus)(-1);
    private bool _shownVirtual;
    private bool _loading;

    public SetupForm(RoutingEngine engine, Func<bool> getAutostart, Action<bool> setAutostart)
    {
        _engine = engine;
        Text = "Pause Resume Audio Fade";
        Icon = AppIcon.Window;
        Font = new Font("Yu Gothic UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        ShowInTaskbar = true;

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.Add(_openSite);
        buttons.Controls.Add(_close);

        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(_title);
        layout.Controls.Add(_body);
        layout.Controls.Add(_autostart);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        _loading = true;
        _autostart.Checked = getAutostart();
        _loading = false;
        _autostart.CheckedChanged += (_, _) => { if (!_loading) setAutostart(_autostart.Checked); };

        _openSite.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(CableUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn($"open failed: {ex.Message}"); }
        };
        _close.Click += (_, _) => Close();
        AcceptButton = _close;

        _timer.Tick += (_, _) => Refresh_();
        _timer.Start();
        FormClosed += (_, _) => _timer.Stop();
        Refresh_();
    }

    private void Refresh_()
    {
        var st = _engine.Status;
        bool virt = _engine.SinkIsVirtual;
        if (st == _shown && virt == _shownVirtual && st != EngineStatus.Running) return;
        _shown = st;
        _shownVirtual = virt;

        _openSite.Visible = st == EngineStatus.SinkMissing;

        switch (st)
        {
            case EngineStatus.Running:
                _title.Text = "✔ 準備できました";
                _title.ForeColor = Color.FromArgb(30, 130, 60);
                _body.Text =
                    "動画や音楽を再生して、一時停止・再開・シークを試してみてください。音が自然に消えて、ふわっと戻ります。\n\n" +
                    $"・音は約 {_engine.LatencyMs:F0}ms 遅れて聞こえます(トレイメニューの「プリセット」で変えられます)。\n" +
                    (_engine.SinkIsVirtual
                        ? $"・仮想ケーブル「{_engine.SinkDeviceName}」を使っています。\n"
                        : $"・使っていない出力「{_engine.SinkDeviceName}」を一時的にミュートして借りています(終了すると元に戻ります)。\n") +
                    "・このアプリは画面右下(タスクトレイ)に常駐します。終了はアイコンを右クリック →「終了」です。";
                break;

            case EngineStatus.SinkMissing:
                _title.Text = "もう1つだけ準備が必要です";
                _title.ForeColor = Color.FromArgb(190, 120, 0);
                _body.Text =
                    "フェードをかけるには、元の音を逃がすための「使っていない出力」が1つ必要です。このPCには見つかりませんでした。\n\n" +
                    "【おすすめ】無料の仮想ケーブル VB-CABLE を入れる(約2分)\n" +
                    "  1. 下のボタンで公式サイトを開き、ZIP をダウンロードして「すべて展開」\n" +
                    "  2. VBCABLE_Setup_x64.exe を右クリック →「管理者として実行」→ Install Driver\n" +
                    "  3. 再起動を求められたら再起動\n" +
                    "入れ終わると、このアプリが自動で見つけて動き始めます(この画面も自動で切り替わります)。\n\n" +
                    "【または】HDMIモニターなど、別の出力デバイスをつなぐだけでも使えます。\n\n" +
                    "※ VB-CABLE は VB-Audio Software の寄付制(donationware)ソフトで、個人利用は無料です(vb-audio.com)。";
                break;

            case EngineStatus.Error:
                _title.Text = "音の出力先が見つかりません";
                _title.ForeColor = Color.FromArgb(190, 50, 50);
                _body.Text = "スピーカーやイヤホンを接続してください。つなぐと自動で再開します。\n\n" + _engine.StatusText;
                break;

            default:
                _title.Text = "停止中です";
                _title.ForeColor = SystemColors.GrayText;
                _body.Text = "トレイアイコンを右クリックして「有効」にすると、動き始めます。";
                break;
        }
    }
}
