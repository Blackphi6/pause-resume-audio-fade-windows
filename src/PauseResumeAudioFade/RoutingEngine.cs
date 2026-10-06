using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PauseResumeAudioFade.Core;

namespace PauseResumeAudioFade.App;

internal enum EngineStatus { Stopped, Running, SinkMissing, Error }

/// <summary>LookaheadProcessor を NAudio の ISampleProvider として見せるアダプタ。</summary>
internal sealed class ProcessorSampleProvider : ISampleProvider
{
    private readonly LookaheadProcessor _p;
    public ProcessorSampleProvider(LookaheadProcessor p)
    {
        _p = p;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(p.SampleRate, p.Channels);
    }
    public WaveFormat WaveFormat { get; }

    /// <summary>診断用: 処理後の出力をそのままファイルへ落とす(--dump)。無制限に膨らまないよう約150MBで打ち切る(48kHz/2chで約6.5分)。</summary>
    public Stream? Dump { get; set; }
    public const long DumpCapBytes = 150L * 1024 * 1024;
    private long _dumped;

    public int Read(Span<float> buffer)
    {
        int n = _p.Read(buffer);
        var d = Dump;
        if (d != null && _dumped < DumpCapBytes)
        {
            try { d.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer[..n])); _dumped += n * 4L; } catch { }
        }
        return n;
    }
}

/// <summary>常に無音を返すだけのソース(キープアライブ用)。</summary>
internal sealed class SilentSampleProvider : ISampleProvider
{
    public SilentSampleProvider(int rate, int channels) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
    public WaveFormat WaveFormat { get; }
    public int Read(Span<float> buffer)
    {
        buffer.Clear();
        return buffer.Length;
    }
}

/// <summary>チャンネル数の違うデバイスへ繋ぐための単純な変換(mono↔stereo、多chは前2chのみ使用)。</summary>
internal sealed class ChannelAdapter : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _inCh, _outCh;
    private float[] _tmp = new float[4096];

    public ChannelAdapter(ISampleProvider src, int outChannels)
    {
        _src = src;
        _inCh = src.WaveFormat.Channels;
        _outCh = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, outChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / _outCh;
        int need = frames * _inCh;
        if (_tmp.Length < need) _tmp = new float[need];
        int got = _src.Read(_tmp.AsSpan(0, need));
        int gotFrames = got / _inCh;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < _outCh; c++)
            {
                float v;
                if (f >= gotFrames) v = 0f;
                else if (_outCh == 1) { float s = 0; for (int k = 0; k < _inCh; k++) s += _tmp[f * _inCh + k]; v = s / _inCh; }
                else if (c < _inCh) v = _tmp[f * _inCh + c];
                else v = _inCh == 1 ? _tmp[f * _inCh] : 0f;
                buffer[f * _outCh + c] = v;
            }
        }
        return buffer.Length;
    }
}

/// <summary>
/// 仮想デバイス(シンク)を既定の再生先にし、そのループバックを取り込み → 先読み+フェード処理 → 実デバイスへ再生する。
/// 元の音はシンク(物理的に鳴らない)へ流れるので、遅延させた処理済みの音だけが聞こえる。
/// </summary>
internal sealed class RoutingEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enum = new();
    private MMDeviceNotificationClient? _notif;
    private Settings _settings;

    private LookaheadProcessor? _proc;
    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _out;
    private WasapiOut? _keepAlive;
    private MMDevice? _sink;
    private MMDevice? _real;
    private bool _sinkMutedByUs;
    private bool _watchdogStarted;
    private DateTime _ignoreDefaultChangeUntil = DateTime.MinValue;
    private float[] _scratch = new float[8192];
    private bool _disposed;

    // 自己修復(スーパーバイザ)用の状態。ユーザーが「動かしたい」と望んでいる間は、デバイスの抜き差しや
    // ストリーム異常があっても、エンジン自身が何度でも立て直す。
    private bool _wantRunning;
    private volatile bool _restartRequested;
    private string? _pendingDefaultId;
    private string? _preferredRealId;
    private bool _dumpsOpenedOnce;
    private bool _sinkIsVirtual = true;
    private bool _muteSinkEffective;
    private string? _preferredSinkId;
    private System.Threading.Timer? _reconcileTimer;
    private const int TickMs = 2000;

    public EngineStatus Status { get; private set; } = EngineStatus.Stopped;
    public string StatusText { get; private set; } = "停止中";
    public event Action? StatusChanged;
    public event Action<GapInfo>? GapDetected;

    public double LatencyMs => _proc?.FillMs ?? 0;
    public long Underruns => _proc?.UnderrunCount ?? 0;
    public string? RealDeviceName => _real?.FriendlyName;
    public string? SinkDeviceName => _sink?.FriendlyName;
    /// <summary>動作中のシンクが仮想ケーブルか(falseなら、使っていない物理出力を借りてミュートしている)。</summary>
    public bool SinkIsVirtual => _sinkIsVirtual;

    /// <summary>診断用: 入力(処理前)と出力(処理後)の生floatを書き出すフォルダ。--dump で指定。</summary>
    public string? DumpDir { get; set; }
    private FileStream? _dumpIn, _dumpOut;
    private long _dumpedIn;
    private ProcessorSampleProvider? _renderSource;

    private void OpenDumps()
    {
        if (string.IsNullOrEmpty(DumpDir)) return;
        Directory.CreateDirectory(DumpDir);
        // 自動復旧による再起動のたびに診断ファイルを上書きしない(最初の1回だけ作り直し、以降は追記)
        var mode = _dumpsOpenedOnce ? FileMode.Append : FileMode.Create;
        _dumpsOpenedOnce = true;
        _dumpIn = new FileStream(Path.Combine(DumpDir, "input.f32"), mode, FileAccess.Write, FileShare.Read, 1 << 16);
        _dumpOut = new FileStream(Path.Combine(DumpDir, "output.f32"), mode, FileAccess.Write, FileShare.Read, 1 << 16);
        if (_renderSource != null) _renderSource.Dump = _dumpOut;
        Log.Info($"dumping raw input/output floats to {DumpDir} ({_proc?.SampleRate}Hz x{_proc?.Channels}ch)");
    }

    private void CloseDumps()
    {
        try { if (_renderSource != null) _renderSource.Dump = null; } catch { }
        try { _dumpIn?.Flush(); _dumpIn?.Dispose(); } catch { }
        try { _dumpOut?.Flush(); _dumpOut?.Dispose(); } catch { }
        _dumpIn = null;
        _dumpOut = null;
    }

    public RoutingEngine(Settings settings)
    {
        _settings = settings;
        try
        {
            _notif = _enum.CreateNotificationClient(true);
            _notif.DefaultDeviceChanged += (_, e) => OnDefaultDeviceChanged(e.Flow, e.Role, e.DeviceId);
            _notif.DeviceStateChanged += (_, e) => OnDeviceStateChanged(e.DeviceId, e.NewState);
        }
        catch (Exception ex) { Log.Warn($"device notification registration failed: {ex.Message}"); }

        // 通知の取りこぼし(スリープ復帰・ドライバ再起動・COMイベント欠落)に備えた定期点検。
        _reconcileTimer = new System.Threading.Timer(_ => Reconcile(), null, TickMs, TickMs);
    }

    private void SetStatus(EngineStatus s, string text)
    {
        // 自己修復の再試行が2秒ごとに同じ状態を繰り返すので、変化したときだけ記録・通知する
        if (Status == s && StatusText == text) return;
        Status = s;
        StatusText = text;
        Log.Info($"status: {s} - {text}");
        StatusChanged?.Invoke();
    }

    // ---------------------------------------------------------------- 開始/停止

    /// <summary>動かす(ユーザー操作)。以降、異常があっても自己修復で動かし続ける。</summary>
    public bool Start()
    {
        lock (_gate)
        {
            _wantRunning = true;
            return StartCore();
        }
    }

    private bool StartCore()
    {
        lock (_gate)
        {
            if (_disposed || Status == EngineStatus.Running) return Status == EngineStatus.Running;
            try
            {
                RoutingState.RestoreIfNeeded("startup");

                var plan = SinkPlanner.Plan(BuildEndpointInfos(), _settings.VirtualDeviceName,
                    _preferredRealId, _settings.OutputDeviceId, _preferredSinkId, _settings.AutoSink);
                if (plan.Failure == PlanFailure.NoOutput)
                {
                    SetStatus(EngineStatus.Error, "出力先の再生デバイスが見つかりません。接続されると自動で再開します");
                    return false;
                }
                if (plan.Failure == PlanFailure.NoSink)
                {
                    SetStatus(EngineStatus.SinkMissing,
                        "仮想ケーブル(VB-CABLE)も、使っていない出力デバイスも見つかりません。導入すると自動で開始します");
                    return false;
                }

                var sink = Devices.ById(_enum, plan.Sink!.Id);
                var real = Devices.ById(_enum, plan.Real!.Id);
                if (sink == null || real == null)
                {
                    SetStatus(EngineStatus.Error, "デバイスの状態が変わりました。再試行します");
                    return false;
                }

                _sink = sink;
                _real = real;
                _sinkIsVirtual = plan.SinkIsVirtual;
                _muteSinkEffective = _settings.MuteSink || !plan.SinkIsVirtual;
                _preferredRealId ??= real.ID;
                _preferredSinkId = sink.ID;
                _restartRequested = false;
                if (!_sinkIsVirtual) Log.Info($"no virtual cable; borrowing unused output as sink: {sink.FriendlyName}");

                BuildCapture(sink);
                OpenDumps();
                BuildRender(real);

                _capture!.StartRecording();
                StartKeepAlive(sink);
                _out!.Play();

                bool mute = false;
                if (_muteSinkEffective)
                {
                    bool prev = sink.AudioEndpointVolume.Mute;
                    sink.AudioEndpointVolume.Mute = true;
                    mute = !prev;
                }
                _sinkMutedByUs = mute;

                SaveState();
                StartWatchdogOnce();

                _ignoreDefaultChangeUntil = DateTime.UtcNow.AddSeconds(2);
                PolicyConfig.SetDefault(sink.ID);
                SetupVolumeMirror();

                SetStatus(EngineStatus.Running, $"動作中: {sink.FriendlyName} → {real.FriendlyName}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("start failed", ex);
                StopCore(restoreDefault: true);
                SetStatus(EngineStatus.Error, $"開始に失敗: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>止める(ユーザー操作)。自己修復も止まる。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _wantRunning = false;
            StopCore(restoreDefault: true);
            if (Status == EngineStatus.Running || Status == EngineStatus.Error || Status == EngineStatus.SinkMissing)
                SetStatus(EngineStatus.Stopped, "停止中");
        }
    }

    /// <summary>
    /// 既定を戻す先。元の出力先が無い(抜かれた)ときは、残っている別のデバイスへ。
    /// これを怠ると、シンクが既定のまま残って「どこからも音が出ない」状態になる。
    /// </summary>
    private MMDevice? RestoreTarget()
    {
        if (_sink == null) return null;
        var real = _real == null ? null : Devices.ById(_enum, _real.ID);
        if (real != null) return real;
        var pref = _preferredRealId == null ? null : Devices.ById(_enum, _preferredRealId);
        if (pref != null) return pref;
        return Devices.ActiveRender(_enum).FirstOrDefault(d => d.ID != _sink.ID);
    }

    private void StopCore(bool restoreDefault)
    {
        TeardownVolumeMirror(restore: restoreDefault);
        if (_sink != null && restoreDefault)
        {
            try
            {
                var def = Devices.DefaultRender(_enum);
                var target = RestoreTarget();
                if (def != null && def.ID == _sink.ID && target != null)
                {
                    _ignoreDefaultChangeUntil = DateTime.UtcNow.AddSeconds(2);
                    PolicyConfig.SetDefault(target.ID);
                }
            }
            catch (Exception ex) { Log.Warn($"restore default failed: {ex.Message}"); }

            if (_sinkMutedByUs)
            {
                try { _sink.AudioEndpointVolume.Mute = false; } catch { }
            }
        }
        _sinkMutedByUs = false;

        try { _capture?.StopRecording(); } catch { }
        try { _out?.Stop(); } catch { }
        try { _keepAlive?.Stop(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _out?.Dispose(); } catch { }
        try { _keepAlive?.Dispose(); } catch { }
        _capture = null;
        _out = null;
        _keepAlive = null;
        CloseDumps();
        _renderSource = null;
        _proc = null;
        RoutingState.Delete();
    }

    // ---------------------------------------------------------------- パイプライン構築

    /// <summary>
    /// 有効な再生デバイスの一覧(選択ロジック用)。別のアプリが今鳴らしているデバイスは「使用中」とし、
    /// 余った出力として借りない(ミュートすると、そのアプリの音が消えてしまうため)。
    /// </summary>
    private List<EndpointInfo> BuildEndpointInfos()
    {
        string? defId = Devices.DefaultRender(_enum)?.ID;
        uint self = (uint)Environment.ProcessId;
        var list = new List<EndpointInfo>();
        foreach (var d in Devices.ActiveRender(_enum))
        {
            bool busy = false;
            try
            {
                var sm = d.AudioSessionManager.Sessions;
                for (int i = 0; i < sm.Count && !busy; i++)
                {
                    var s = sm[i];
                    if (s.GetProcessID != self && s.State == AudioSessionState.AudioSessionStateActive) busy = true;
                }
            }
            catch { /* 取れないデバイスは「使用中ではない」扱い */ }
            list.Add(new EndpointInfo(d.ID, d.FriendlyName, d.ID == defId, busy));
        }
        return list;
    }

    /// <summary>
    /// シンク(仮想デバイス)に無音を鳴らし続けるストリーム。仮想デバイスは再生中のストリームが無いとエンジンが止まり、
    /// ループバックに無音パケットすら配信されないことがある。そうなると、ブラウザが一時停止でストリームを閉じた瞬間に
    /// 「無音が始まった」ことを観測できず、フェードアウトが掛からない。常にストリームを1本開けておいて配信を途切れさせない。
    /// </summary>
    private void StartKeepAlive(MMDevice sink)
    {
        try
        {
            var fmt = _capture!.WaveFormat;
            var silent = new SilentSampleProvider(fmt.SampleRate, fmt.Channels);
            var wo = new WasapiOut(_enum.GetDevice(sink.ID), AudioClientShareMode.Shared, true, 100);
            wo.Init(silent.ToWaveProvider());
            wo.Play();
            _keepAlive = wo;
            Log.Info("keep-alive silent stream started on sink");
        }
        catch (Exception ex)
        {
            // 無くても多くの環境(通常のハードウェア出力等)では問題ないので、失敗しても動作は続ける
            Log.Warn($"keep-alive stream failed: {ex.Message}");
        }
    }

    private void BuildCapture(MMDevice sink)
    {
        _capture = new WasapiLoopbackCapture(sink);
        var wf = _capture.WaveFormat;
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (wf is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        int bits = wf.BitsPerSample;
        if (!PcmConverter.IsSupported(bits, isFloat))
            throw new NotSupportedException($"未対応のキャプチャ形式: {wf}");

        var proc = new LookaheadProcessor(wf.SampleRate, wf.Channels, _settings.ToFadeParameters());
        proc.GapDetected += g =>
        {
            Log.Info($"gap detected: {g.Kind} {g.LengthMs:F0}ms");
            GapDetected?.Invoke(g);
        };
        _proc = proc;

        _capture.DataAvailable += (_, e) =>
        {
            int samples = e.BytesRecorded / (bits / 8);
            if (_scratch.Length < samples) _scratch = new float[samples];
            int n = PcmConverter.ToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), bits, isFloat, _scratch);
            var dumpIn = _dumpIn;
            if (dumpIn != null && _dumpedIn < ProcessorSampleProvider.DumpCapBytes)
            {
                try { dumpIn.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_scratch.AsSpan(0, n))); _dumpedIn += n * 4L; } catch { }
            }
            proc.Write(_scratch.AsSpan(0, n));
        };
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                Log.Error("capture stopped with error", e.Exception);
                RequestRestart("キャプチャが停止しました");
            }
        };
        Log.Info($"capture format: {wf} (sink: {sink.FriendlyName})");
    }

    private void BuildRender(MMDevice real)
    {
        var probe = _enum.GetDevice(real.ID);
        var mix = probe.AudioClient.MixFormat;

        var procProvider = new ProcessorSampleProvider(_proc!) { Dump = _dumpOut };
        _renderSource = procProvider;
        ISampleProvider src = procProvider;
        if (src.WaveFormat.SampleRate != mix.SampleRate)
            src = new WdlResamplingSampleProvider(src, mix.SampleRate);
        if (src.WaveFormat.Channels != mix.Channels)
            src = new ChannelAdapter(src, mix.Channels);

        var wo = new WasapiOut(real, AudioClientShareMode.Shared, true, 40);
        wo.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                Log.Error("render stopped with error", e.Exception);
                RequestRestart("再生が停止しました");
            }
        };
        wo.Init(src.ToWaveProvider());
        _out = wo;
        Log.Info($"render: {real.FriendlyName} mix={mix}");
    }

    // ---------------------------------------------------------------- 音量の連動

    private MMDevice? _volSink;
    private float _sinkOrigVolume = -1f;
    private bool _sinkOrigMute;
    private AudioEndpointVolumeNotificationDelegate? _volHandler;

    /// <summary>
    /// ループバックはデバイス音量・ミュートの「手前」の信号を取るため、Windowsの音量スライダー(=既定デバイス=シンクの音量)を
    /// そのままでは出力に反映できない。そこで、シンクの音量/ミュートの変化を実デバイスへ連動させる。
    /// 起動時はシンクを実デバイスの音量に合わせるので、聞こえる大きさは急に変わらない。
    /// </summary>
    private void SetupVolumeMirror()
    {
        TeardownVolumeMirror(restore: false);
        try
        {
            if (_sink == null || _real == null) return;
            var sinkDev = _enum.GetDevice(_sink.ID);
            var sv = sinkDev.AudioEndpointVolume;
            var rv = _real.AudioEndpointVolume;

            _sinkOrigVolume = sv.MasterVolumeLevelScalar;
            _sinkOrigMute = sv.Mute;
            _volSink = sinkDev;

            float rvol = rv.MasterVolumeLevelScalar;
            if (Math.Abs(sv.MasterVolumeLevelScalar - rvol) > 0.001f) sv.MasterVolumeLevelScalar = rvol;
            if (!_muteSinkEffective && sv.Mute != rv.Mute) sv.Mute = rv.Mute;

            _volHandler = data =>
            {
                try
                {
                    var r = _real;
                    if (r == null) return;
                    var rev = r.AudioEndpointVolume;
                    if (Math.Abs(rev.MasterVolumeLevelScalar - data.MasterVolume) > 0.0005f)
                        rev.MasterVolumeLevelScalar = data.MasterVolume;
                    if (!_muteSinkEffective) { if (rev.Mute != data.Muted) rev.Mute = data.Muted; }
                    else if (!data.Muted)
                    {
                        // 物理出力を借りているとき: 音量キー操作などでWindowsが自動でミュート解除するので、すぐ戻す
                        // (ここは鳴らさないデバイス。実際の出力のミュート状態は触らない)
                        _volSink!.AudioEndpointVolume.Mute = true;
                    }
                }
                catch (Exception ex) { Log.Warn($"volume mirror failed: {ex.Message}"); }
            };
            sv.OnVolumeNotification += _volHandler;
            Log.Info($"volume mirror: sink slider -> {_real.FriendlyName} (now {rvol:P0})");
        }
        catch (Exception ex) { Log.Warn($"volume mirror setup failed: {ex.Message}"); }
    }

    private void TeardownVolumeMirror(bool restore)
    {
        try
        {
            if (_volSink != null && _volHandler != null)
                _volSink.AudioEndpointVolume.OnVolumeNotification -= _volHandler;
            if (restore && _volSink != null && _sinkOrigVolume >= 0f)
            {
                var sv = _volSink.AudioEndpointVolume;
                sv.MasterVolumeLevelScalar = _sinkOrigVolume;
                if (!_muteSinkEffective) sv.Mute = _sinkOrigMute;
            }
        }
        catch { }
        _volHandler = null;
        _volSink = null;
        _sinkOrigVolume = -1f;
    }

    private void SaveState()
    {
        new RoutingState
        {
            OwnerPid = Environment.ProcessId,
            RealDeviceId = _real!.ID,
            SinkDeviceId = _sink!.ID,
            SinkMutedByUs = _sinkMutedByUs,
        }.Save();
    }

    private void StartWatchdogOnce()
    {
        if (_watchdogStarted) return;
        _watchdogStarted = true;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Log.Warn($"watchdog start failed: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- 設定の反映

    public void ApplySettings(Settings s)
    {
        bool needRestart;
        lock (_gate)
        {
            needRestart = s.VirtualDeviceName != _settings.VirtualDeviceName ||
                          s.OutputDeviceId != _settings.OutputDeviceId ||
                          s.MuteSink != _settings.MuteSink ||
                          s.AutoSink != _settings.AutoSink;
            _settings = s;
            _proc?.UpdateParameters(s.ToFadeParameters());
        }
        if (needRestart && Status == EngineStatus.Running)
        {
            Stop();
            if (s.Enabled) Start();
        }
    }

    // ---------------------------------------------------------------- デバイス変化への追従

    /// <summary>ストリーム異常などで作り直しが必要。実際の再構築は Reconcile が(排他の中で1か所だけで)行う。</summary>
    private void RequestRestart(string reason)
    {
        if (_disposed) return;
        Log.Warn($"restart requested: {reason}");
        _restartRequested = true;
        KickReconcile(300);
    }

    /// <summary>少し待ってから点検を1回走らせる(イベントが連続しても1回にまとまる)。定期点検とは別。</summary>
    private void KickReconcile(int delayMs)
    {
        if (_disposed) return;
        try { _reconcileTimer?.Change(delayMs, TickMs); } catch { }
    }

    /// <summary>
    /// 自己修復の本体。「あるべき状態」と「いまの状態」を突き合わせて直す。
    /// 停止中/エラー中 → 開始を試みる。動作中 → シンク・出力先の生存、再生状態、ユーザーによる既定変更、
    /// 好みの出力先の復帰を点検する。冪等で、何度呼んでもよい。
    /// </summary>
    private void Reconcile()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || !_wantRunning || !_settings.Enabled) return;

                if (Status != EngineStatus.Running)
                {
                    StartCore();   // 仮想デバイス/出力先が戻ってきていれば、これで復帰する
                    return;
                }

                if (_sink == null || Devices.ById(_enum, _sink.ID) == null)
                {
                    Log.Warn("sink device went away");
                    StopCore(restoreDefault: true);
                    SetStatus(EngineStatus.SinkMissing, "仮想デバイスが無効になりました。戻ると自動で再開します");
                    return;
                }

                // ユーザー(またはWindowsのジャック検出)が既定を変えた → 新しい出力先として採用
                var pending = _pendingDefaultId;
                _pendingDefaultId = null;
                if (pending != null && pending != _sink.ID)
                {
                    var dev = Devices.ById(_enum, pending);
                    if (dev != null && dev.ID != _real?.ID)
                    {
                        Log.Info($"adopting new output device: {dev.FriendlyName}");
                        _preferredRealId = dev.ID;
                        SwitchReal(dev);
                        return;
                    }
                }

                if (_restartRequested)
                {
                    StopCore(restoreDefault: true);
                    Status = EngineStatus.Stopped;
                    StartCore();
                    return;
                }

                // 出力先が抜けた/無効になった
                if (_real == null || Devices.ById(_enum, _real.ID) == null)
                {
                    Log.Warn($"real output device went away: {_real?.FriendlyName}");
                    var fallback = Devices.ActiveRender(_enum).FirstOrDefault(d => d.ID != _sink.ID);
                    if (fallback != null) { SwitchReal(fallback); return; }

                    StopCore(restoreDefault: true);
                    SetStatus(EngineStatus.Error, "出力先デバイスがありません。接続されると自動で再開します");
                    return;
                }

                // 代替出力で動いている間に、好みの出力先(イヤホン等)が戻ってきた → そちらへ戻す
                if (_preferredRealId != null && _preferredRealId != _real.ID)
                {
                    var pref = Devices.ById(_enum, _preferredRealId);
                    if (pref != null && pref.ID != _sink.ID)
                    {
                        Log.Info($"preferred output returned: {pref.FriendlyName}");
                        SwitchReal(pref);
                        return;
                    }
                }

                // 物理出力を借りて動いている間に、仮想ケーブルが導入された → ケーブルへ切り替える(ミュートが不要になる)
                if (!_sinkIsVirtual && Devices.FindByName(_enum, _settings.VirtualDeviceName) != null)
                {
                    Log.Info("virtual cable became available; switching to it");
                    _preferredSinkId = null;
                    _restartRequested = true;
                }

                // 出力ストリームが実際に再生中か
                if (_out != null && _out.PlaybackState != PlaybackState.Playing)
                {
                    Log.Warn($"render is not playing ({_out.PlaybackState}); restarting");
                    _restartRequested = true;
                }
            }
        }
        catch (Exception ex) { Log.Error("reconcile failed", ex); }
    }

    /// <summary>出力先(実デバイス)だけを差し替える。キャプチャと処理状態はそのまま。</summary>
    private void SwitchReal(MMDevice dev)
    {
        lock (_gate)
        {
            if (Status != EngineStatus.Running || _sink == null || _proc == null) return;
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            _out = null;
            _real = dev;
            try
            {
                BuildRender(dev);
                _out!.Play();
                SaveState();
                _ignoreDefaultChangeUntil = DateTime.UtcNow.AddSeconds(2);
                PolicyConfig.SetDefault(_sink.ID);
                SetupVolumeMirror();
                SetStatus(EngineStatus.Running, $"動作中: {_sink.FriendlyName} → {dev.FriendlyName}");
            }
            catch (Exception ex)
            {
                // 切替先がその瞬間に消えた等。全体を作り直して、次の点検で選び直す
                Log.Error($"switching output to {dev.FriendlyName} failed", ex);
                _restartRequested = true;
            }
        }
    }

    private void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render || role == Role.Communications) return;
        if (Status != EngineStatus.Running || _sink == null) return;
        if (DateTime.UtcNow < _ignoreDefaultChangeUntil) return;
        if (defaultDeviceId == _sink.ID)
        {
            // 借りている物理出力を、ユーザーが自分で「聞くデバイス」に選んだ → そちらを実出力にして組み直す(消音のままにしない)
            if (!_sinkIsVirtual)
            {
                Log.Info("user selected the borrowed output as default; switching roles");
                _preferredRealId = defaultDeviceId;
                _preferredSinkId = null;
                RequestRestart("借りていた出力がユーザーに選ばれました");
            }
            return;
        }

        // ユーザー(またはWindows)が既定を別のデバイスに変えた → 新しい出力先として採用し、既定をシンクへ戻す(Reconcileで実施)
        Log.Info($"default device changed by user to {defaultDeviceId}");
        _pendingDefaultId = defaultDeviceId;
        KickReconcile(400);
    }

    /// <summary>デバイスの状態変化(抜き差し・有効/無効)。どのデバイスの変化でも、結果は Reconcile が総合的に判断する。</summary>
    private void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        Log.Info($"device state changed: {deviceId} -> {newState}");
        KickReconcile(500);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _wantRunning = false;
            _reconcileTimer?.Dispose();
            StopCore(restoreDefault: true);
        }
        try { _notif?.Dispose(); } catch { }
        _enum.Dispose();
    }
}
