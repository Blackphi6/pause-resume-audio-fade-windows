using System.Diagnostics;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace PauseResumeAudioFade.App;

/// <summary>システムトレイ常駐のUI。設定の変更はすぐエンジンに反映し、settings.json に保存する。</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PauseResumeAudioFade";

    private readonly Settings _settings;
    private readonly RoutingEngine _engine;
    private readonly NotifyIcon? _tray;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _enabledItem = new("有効") { CheckOnClick = false };
    private readonly ToolStripMenuItem _fadeOutItem = new("フェードアウトを使う") { CheckOnClick = false };
    private readonly ToolStripMenuItem _presetMenu = new("プリセット(遅延 / フェードアウト長)");
    private readonly ToolStripMenuItem _fadeInMenu = new("フェードイン長");
    private readonly ToolStripMenuItem _deviceMenu = new("出力デバイス");
    private readonly ToolStripMenuItem _autoSwitchItem = new("新しく接続したヘッドホン等へ自動で切り替える") { CheckOnClick = false };
    private readonly ToolStripMenuItem _autostartItem = new("Windows起動時に自動起動") { CheckOnClick = false };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private readonly SynchronizationContext _ui;
    private bool _cableHintShown;
    private SetupForm? _setup;
    private EngineStatus _lastIconStatus = (EngineStatus)(-1);

    public TrayApp(Settings settings, bool noUi, string? dumpDir = null)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _engine = new RoutingEngine(settings) { DumpDir = dumpDir };
        _engine.StatusChanged += () => _ui.Post(_ => OnStatusChanged(), null);

        if (!noUi)
        {
            _tray = new NotifyIcon { Visible = true, ContextMenuStrip = BuildMenu(), Text = "Pause Resume Audio Fade" };
            _tray.DoubleClick += (_, _) => ToggleEnabled();
            _tray.BalloonTipClicked += (_, _) => ShowSetup();
            UpdateIcon();
            _uiTimer.Tick += (_, _) => RefreshTooltip();
            _uiTimer.Start();
        }

        if (_settings.Enabled) _engine.Start();
        OnStatusChanged();

        // 初回だけ案内画面を出す(エンジンが状態を決めた後に表示するため、少し待つ)
        if (!_settings.SetupShown && !noUi)
        {
            var first = new System.Windows.Forms.Timer { Interval = 1500 };
            first.Tick += (_, _) =>
            {
                first.Stop();
                first.Dispose();
                _settings.SetupShown = true;
                _settings.Save();
                ShowSetup();
            };
            first.Start();
        }

        // `--quit` からの終了要求を受け付ける(スクリプト/テスト用)
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var t = new Thread(() =>
        {
            exitEvent.WaitOne();
            _ui.Post(_ => ExitApp(), null);
        }) { IsBackground = true };
        t.Start();
    }

    public const string ExitEventName = @"Local\PauseResumeAudioFade.Exit";

    // ---------------------------------------------------------------- メニュー

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _enabledItem.Click += (_, _) => ToggleEnabled();
        _fadeOutItem.Click += (_, _) =>
        {
            _settings.FadeOutEnabled = !_settings.FadeOutEnabled;
            SaveAndApply();
        };

        foreach (var (preset, label) in new[]
        {
            (Preset.Short, "短い(50ms / 約34ms)"),
            (Preset.Standard, "標準(100ms / 84ms)"),
            (Preset.Long, "長い(200ms / 184ms)"),
            (Preset.Music, "音楽(350ms / 334ms)"),
        })
        {
            var item = new ToolStripMenuItem(label) { Tag = preset };
            item.Click += (_, _) => { _settings.Preset = preset; SaveAndApply(); };
            _presetMenu.DropDownItems.Add(item);
        }

        foreach (int ms in new[] { 150, 300, 500, 1000, 2000 })
        {
            var item = new ToolStripMenuItem($"{ms} ms") { Tag = ms };
            item.Click += (_, _) => { _settings.FadeInMs = ms; SaveAndApply(); };
            _fadeInMenu.DropDownItems.Add(item);
        }

        _autostartItem.Click += (_, _) => SetAutostart(!IsAutostart());
        _autoSwitchItem.Click += (_, _) => { _settings.AutoSwitchToNewDevice = !_settings.AutoSwitchToNewDevice; SaveAndApply(); };

        var openSettings = new ToolStripMenuItem("設定ファイルを開く");
        openSettings.Click += (_, _) => { _settings.Save(); OpenPath(AppPaths.SettingsFile); };
        var openLog = new ToolStripMenuItem("ログを開く");
        openLog.Click += (_, _) => OpenPath(AppPaths.LogFile);
        var cableHelp = new ToolStripMenuItem("はじめに / 導入ガイド...");
        cableHelp.Click += (_, _) => ShowSetup();
        var retry = new ToolStripMenuItem("再検出して開始");
        retry.Click += (_, _) => { _settings.Enabled = true; SaveAndApply(); _engine.Stop(); _engine.Start(); };
        var exit = new ToolStripMenuItem("終了");
        exit.Click += (_, _) => ExitApp();

        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem, new ToolStripSeparator(),
            _enabledItem, _fadeOutItem, _presetMenu, _fadeInMenu, _deviceMenu, _autoSwitchItem, new ToolStripSeparator(),
            retry, _autostartItem, new ToolStripSeparator(),
            openSettings, openLog, cableHelp, new ToolStripSeparator(),
            exit,
        });

        menu.Opening += (_, _) => RefreshMenu();
        return menu;
    }

    private void RefreshMenu()
    {
        _statusItem.Text = _engine.StatusText;
        _enabledItem.Checked = _settings.Enabled;
        _fadeOutItem.Checked = _settings.FadeOutEnabled;
        _autostartItem.Checked = IsAutostart();
        _autoSwitchItem.Checked = _settings.AutoSwitchToNewDevice;
        foreach (ToolStripMenuItem i in _presetMenu.DropDownItems) i.Checked = (Preset)i.Tag! == _settings.Preset;
        foreach (ToolStripMenuItem i in _fadeInMenu.DropDownItems) i.Checked = (int)i.Tag! == _settings.FadeInMs;

        _deviceMenu.DropDownItems.Clear();
        var auto = new ToolStripMenuItem("自動(切替前の既定デバイス)") { Checked = string.IsNullOrEmpty(_settings.OutputDeviceId) };
        auto.Click += (_, _) => { _settings.OutputDeviceId = ""; SaveAndApply(); };
        _deviceMenu.DropDownItems.Add(auto);
        try
        {
            using var en = new MMDeviceEnumerator();
            var sink = Devices.FindByName(en, _settings.VirtualDeviceName);
            foreach (var d in Devices.ActiveRender(en))
            {
                if (sink != null && d.ID == sink.ID) continue;
                string id = d.ID;
                var item = new ToolStripMenuItem(d.FriendlyName) { Checked = _settings.OutputDeviceId == id };
                item.Click += (_, _) => { _settings.OutputDeviceId = id; SaveAndApply(); };
                _deviceMenu.DropDownItems.Add(item);
            }
        }
        catch (Exception ex) { Log.Warn($"device list failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- 状態反映

    private void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        _settings.Save();
        if (_settings.Enabled) _engine.Start(); else _engine.Stop();
        OnStatusChanged();
    }

    private void SaveAndApply()
    {
        _settings.Save();
        _engine.ApplySettings(_settings);
        OnStatusChanged();
    }

    private void OnStatusChanged()
    {
        UpdateIcon();
        RefreshTooltip();
        if (_engine.Status == EngineStatus.SinkMissing && !_cableHintShown && _tray != null)
        {
            _cableHintShown = true;
            _tray.ShowBalloonTip(8000, "もう1つだけ準備が必要です",
                "クリックすると導入手順(VB-CABLE)が開きます。導入後は自動で動き始めます。", ToolTipIcon.Warning);
        }
    }

    private void RefreshTooltip()
    {
        if (_tray == null) return;
        string t = _engine.Status == EngineStatus.Running
            ? $"Pause Resume Audio Fade\n動作中 (遅延 {_engine.LatencyMs:F0}ms)"
            : $"Pause Resume Audio Fade\n{_engine.StatusText}";
        _tray.Text = t.Length > 127 ? t[..127] : t;
    }

    private void UpdateIcon()
    {
        if (_tray == null || _lastIconStatus == _engine.Status) return;
        _lastIconStatus = _engine.Status;
        var old = _tray.Icon;
        _tray.Icon = AppIcon.ForStatus(_engine.Status);
        old?.Dispose();
    }

    // ---------------------------------------------------------------- 補助

    private static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return k?.GetValue(RunValueName) != null;
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (on) k.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --autostart");
        else k.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"open failed: {ex.Message}"); }
    }

    private void ShowSetup()
    {
        if (_setup is { IsDisposed: false })
        {
            _setup.Activate();
            return;
        }
        _setup = new SetupForm(_engine, IsAutostart, SetAutostart);
        _setup.Show();
        _setup.Activate();
    }

    private void ExitApp()
    {
        _uiTimer.Stop();
        if (_tray != null) _tray.Visible = false;
        _engine.Dispose();
        _tray?.Dispose();
        ExitThread();
    }
}
