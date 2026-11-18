using System.Diagnostics;
using ScreenRecorderLib;

namespace ScreenRec.Core;

/// <summary>
/// 一次录制会话:ScreenRecorderLib 内核(WGC/DDA + Media Foundation 编码,系统声/麦克风原生采集)。
///
/// 暂停/恢复采用分段方案:内核的 Pause/Resume 在恢复时固定丢约 1.3 秒画面(实测),
/// 因此暂停 = 结束当前段并落盘,恢复 = 新开一段,停止时用原生 Media Foundation 合并器流复制合并。
/// 段与段之间时间线连续,不会出现空洞;合并失败则保留分段文件。
///
/// 关键参数:
/// - IsFixedFramerate = true:静止画面也按帧率出帧,否则成片时长 = 有效帧数 / 帧率。
/// - SourceOptions 必须显式指定,RecorderOptions 默认构造全为 null。
/// - 音频源必须用 .Default,无参构造的 DeviceName 为空,成片没有音轨。
/// </summary>
public sealed class RecordingSession
{
    public event Action<SessionState>? StateChanged;
    public event Action<RecordingResult>? Completed;
    public event Action<string>? Failed;

    /// <summary>非致命提示(设备缺失降级、合并失败等)。</summary>
    public event Action<string>? Notice;

    private readonly RecordSettings _settings;

    private Recorder? _rec;
    private string _baseName = string.Empty;
    private string _finalPath = string.Empty;
    private readonly List<string> _segments = new();

    /// <summary>本场录制实际产生过的分段数。合并后会清空 _segments,所以单独记。</summary>
    private int _segmentTotal;

    private long _runStart;
    private long _pauseStart;
    private long _pauseTotalTicks;
    private int _captureWidth;
    private int _captureHeight;

    /// <summary>录制开始时所在显示器的分辨率,watch 用它判断分辨率是否变了。</summary>
    private int _monitorWidth;
    private int _monitorHeight;

    /// <summary>录制源在虚拟桌面里的原点(物理像素),指针特效要把屏幕坐标换成成片坐标。</summary>
    private int _originX;
    private int _originY;

    /// <summary>指针高亮 / 点击双环;两者都关时为 null。</summary>
    private PointerEffects? _effects;

    /// <summary>当前 Stop 是暂停引起的:不要触发 Completed,改为等待下一次 Resume。</summary>
    private bool _stopForPause;

    /// <summary>当前 Stop 是定时切段引起的:落盘后立刻新开一段,状态保持 Recording。</summary>
    private bool _stopForRollover;

    /// <summary>当前分段开始时刻;切段计时器按它判断是否到点。</summary>
    private long _segmentStart;

    /// <summary>切段收尾期间收到的暂停请求:新段起来后立刻执行,不能对正在停的 recorder 再 Stop 一次。</summary>
    private bool _pausePending;

    /// <summary>定时切段计时器:每 5 秒醒一次,到点才切,避免 finalize 耗时导致周期漂移。</summary>
    private Timer? _segmentTimer;

    public SessionState State { get; private set; } = SessionState.Idle;

    /// <summary>已完成落盘的分段数(含正在录的那段之前的所有段)。</summary>
    public int SegmentCount => _segments.Count;

    public TimeSpan Elapsed
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            long paused = _pauseTotalTicks;
            if (State == SessionState.Paused && _pauseStart > 0)
                paused += now - _pauseStart; // 暂停中先扣掉,不等 Resume
            return TimeSpan.FromSeconds(Math.Max(0, (now - _runStart - paused) / (double)Stopwatch.Frequency));
        }
    }

    private RecordingSession(RecordSettings settings)
    {
        _settings = settings;
    }

    public static RecordingSession Create(RecordSettings settings, Action<string>? log = null)
        => new RecordingSession(settings);

    /// <summary>启动录制。失败不抛出,统一经 Failed 事件上报。</summary>
    public Task StartAsync() => Task.Run(() =>
    {
        try
        {
            StartCore();
        }
        catch (Exception ex)
        {
            Diag.Log("start: FAILED " + ex);
            CleanupIncomplete();
            SetState(SessionState.Idle);
            Failed?.Invoke("启动录制失败:" + ex.Message);
        }
    });

    private void StartCore()
    {
        Diag.Log("start: enter");
        _runStart = Stopwatch.GetTimestamp();
        _pauseTotalTicks = 0;
        _segments.Clear();
        _segmentTotal = 0;

        Directory.CreateDirectory(_settings.OutputDirectory);
        _baseName = Path.Combine(_settings.OutputDirectory, _settings.BuildFileName(string.Empty));
        _finalPath = _baseName + ".mp4";
        Diag.Log($"start: base={_baseName}");

        bool hasRegion = _settings.Target == CaptureTarget.Region
                         && _settings.RegionWidth > 1 && _settings.RegionHeight > 1;
        var monitor = ScreenInfo.MonitorByDevice(_settings.MonitorDeviceName);
        _captureWidth = hasRegion ? _settings.RegionWidth : monitor.Width;
        _captureHeight = hasRegion ? _settings.RegionHeight : monitor.Height;
        _monitorWidth = monitor.Width;
        _monitorHeight = monitor.Height;

        // 指针特效的坐标基准:选区扣除选区原点,窗口用窗口实际边框
        if (_settings.Target == CaptureTarget.Window && _settings.WindowHandle != 0)
        {
            var bounds = ScreenInfo.WindowFrameBounds(_settings.WindowHandle);
            _originX = bounds.X;
            _originY = bounds.Y;
            if (bounds.Width > 1 && bounds.Height > 1)
            {
                _captureWidth = bounds.Width;
                _captureHeight = bounds.Height;
            }
        }
        else if (hasRegion)
        {
            _originX = monitor.X + _settings.RegionX;
            _originY = monitor.Y + _settings.RegionY;
        }
        else
        {
            _originX = monitor.X;
            _originY = monitor.Y;
        }

        Diag.Log($"start: screen={_captureWidth}x{_captureHeight} target={_settings.Target} display={monitor.DeviceName}");

        var fx = _settings.MouseEffect;
        if (fx.HighlightEnabled || fx.Enabled)
            _effects = new PointerEffects(_settings, MapPointerToOutput);

        EnsureDiskSpace(_captureWidth, _captureHeight);
        StartSegment(1);
        SetState(SessionState.Recording);
        Diag.Log("start: state=recording");
    }

    /// <summary>新建一个分段录制器。段之间参数完全一致,便于流复制合并。</summary>
    private void StartSegment(int index)
    {
        string path = $"{_baseName}_part{index}.mp4";
        _segmentTotal = index;
        _segmentStart = Stopwatch.GetTimestamp();

        // 叠加层归 Recorder 所有:每个分段都要新建一套,并把当前指针状态接上去
        var overlays = _effects?.CreateOverlays();
        var options = BuildOptions(_captureWidth, _captureHeight, overlays);
        _rec = Recorder.CreateRecorder(options);
        _rec.OnRecordingComplete += OnLibRecordingComplete;
        _rec.OnRecordingFailed += OnLibRecordingFailed;

        Diag.Log($"segment {index}: {path}");
        _rec.Record(path);

        if (_effects is not null && overlays is { Count: > 0 })
            _effects.Attach(_rec);
    }

    /// <summary>屏幕坐标 → 成片坐标;不在录制范围内返回 null(指针移出选区时就不画了)。</summary>
    private (int X, int Y)? MapPointerToOutput(int screenX, int screenY)
    {
        int x = screenX - _originX;
        int y = screenY - _originY;
        if (x < 0 || y < 0 || x >= _captureWidth || y >= _captureHeight) return null;
        return (x, y);
    }

    private RecorderOptions BuildOptions(int width, int height, IReadOnlyList<RecordingOverlayBase>? overlays)
    {
        RecorderOptions options;
        if (_settings.Target == CaptureTarget.Window && _settings.WindowHandle != 0)
        {
            options = RecorderOptions.Default;
            options.SourceOptions = new SourceOptions
            {
                RecordingSources = { new WindowRecordingSource(new IntPtr(_settings.WindowHandle)) },
            };
            Diag.Log($"opt: window handle={_settings.WindowHandle} title={_settings.WindowTitle}");
        }
        else
        {
            // 显式指定显示器:DefaultMainMonitor 只认主屏,副屏上的选区会裁到主屏范围外(黑屏/错位)。
            // 选区矩形的坐标是相对这台显示器的,所以用 source 自己的 SourceRect,而不是 OutputOptions。
            var monitor = ScreenInfo.MonitorByDevice(_settings.MonitorDeviceName);
            var display = string.IsNullOrWhiteSpace(monitor.DeviceName)
                ? DisplayRecordingSource.MainMonitor
                : new DisplayRecordingSource(monitor.DeviceName);

            bool hasRegion = _settings.Target == CaptureTarget.Region
                             && _settings.RegionWidth > 1 && _settings.RegionHeight > 1;
            if (hasRegion)
            {
                display.SourceRect = new ScreenRect(
                    _settings.RegionX, _settings.RegionY,
                    _settings.RegionWidth, _settings.RegionHeight);
                Diag.Log($"opt: region {_settings.RegionX},{_settings.RegionY} "
                         + $"{_settings.RegionWidth}x{_settings.RegionHeight} on {monitor.DeviceName}");
            }

            options = RecorderOptions.Default;
            options.SourceOptions = new SourceOptions { RecordingSources = { display } };
        }

        options.AudioOptions = BuildAudioOptions();

        options.VideoEncoderOptions = new VideoEncoderOptions
        {
            Bitrate = (int)_settings.VideoBitrate(width, height),
            Framerate = Math.Clamp(_settings.FrameRate, 5, 60),
            // 强制按帧率补帧:否则静止画面不产帧,成片时长 = 有效帧数 / 帧率
            IsFixedFramerate = true,
            IsThrottlingDisabled = false,
            IsHardwareEncodingEnabled = _settings.Encoder != EncoderChoice.X264,
            Quality = 90,
            Encoder = new H264VideoEncoder
            {
                EncoderProfile = H264Profile.High,
                BitrateMode = H264BitrateControlMode.UnconstrainedVBR,
            },
        };

        // 指针与点击波纹是两个独立开关:关波纹仍可以要指针,反之亦然。
        // 注意不能只在波纹开启时才设 MouseOptions —— 那样"关掉指针"永远不生效(内核默认指针是开的)。
        var fx = _settings.MouseEffect;
        bool ownClicks = _effects?.DrawsClicks == true;
        options.MouseOptions = new MouseOptions
        {
            IsMousePointerEnabled = _settings.ShowMousePointer,
            // 自己画双环时关掉内核实心圆点,否则两层叠在一起
            IsMouseClicksDetected = fx.Enabled && !ownClicks,
            MouseLeftClickDetectionColor = fx.LeftColor,
            MouseRightClickDetectionColor = fx.RightColor,
            MouseClickDetectionRadius = Math.Clamp(fx.RadiusPx, 4, 200),
            MouseClickDetectionDuration = Math.Clamp(fx.DurationMs, 50, 5000),
            MouseClickDetectionMode = MouseDetectionMode.Polling,
        };

        // 指针高亮 / 双环走叠加层:内核把它们合成进成片,录制时屏幕上不出现任何东西
        if (overlays is { Count: > 0 })
        {
            options.OverlayOptions = new OverLayOptions
            {
                Overlays = overlays.ToList(),
            };
        }

        // 录制内核日志:停帧 / 编码协商失败时唯一的证据来源
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenRec", "logs");
            Directory.CreateDirectory(logDir);
            options.LogOptions = new LogOptions
            {
                IsLogEnabled = true,
                LogSeverityLevel = LogLevel.Debug,
                LogFilePath = Path.Combine(logDir, "recorder.log"),
            };
        }
        catch
        {
            // 日志目录不可写不影响录制
        }

        return options;
    }

    private AudioOptions BuildAudioOptions()
    {
        var audio = new AudioOptions
        {
            Bitrate = AudioBitrate.bitrate_128kbps,
            Channels = AudioChannels.Stereo,
        };

        switch (_settings.AudioMode)
        {
            case AudioMode.SystemOnly:
                audio.IsAudioEnabled = TryAddLoopback(audio);
                break;

            case AudioMode.MicOnly:
                audio.IsAudioEnabled = TryAddMic(audio);
                break;

            case AudioMode.Both:
            {
                bool sys = TryAddLoopback(audio);
                bool mic = TryAddMic(audio);
                audio.IsAudioEnabled = sys || mic;

                // 没麦克风是常态(台式机),静默降级;系统声缺失会影响成片,需要告知
                if (!sys && mic)
                    Notice?.Invoke("未找到系统音频输出设备,本次仅录麦克风。");
                break;
            }

            case AudioMode.Muted:
            default:
                audio.IsAudioEnabled = false;
                break;
        }

        if (audio.AudioSources.Count == 0 && _settings.AudioMode != AudioMode.Muted)
        {
            audio.IsAudioEnabled = false;
            Notice?.Invoke("未找到可用的音频设备,本次录制为无声。");
        }

        return audio;
    }

    private bool TryAddLoopback(AudioOptions audio)
    {
        try
        {
            var source = LoopbackAudioSource.Default;
            if (source is null) return false;
            source.Volume = _settings.SystemVolume;
            audio.AudioSources.Add(source);
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log("audio: loopback Default failed " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private bool TryAddMic(AudioOptions audio)
    {
        try
        {
            // 只能用系统默认麦克风:Recorder.GetSystemAudioCaptureDevices() 会直接段错误(实测)
            var source = CaptureAudioSource.Default;
            if (source is null) return false;
            source.Volume = _settings.MicVolume;
            audio.AudioSources.Add(source);
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log("audio: mic Default failed " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 暂停:结束当前段。落盘在 OnLibRecordingComplete 回调里完成并切到 Paused,
    /// 所以本方法立即返回,不等 finalize。
    /// </summary>
    public void Pause()
    {
        if (State != SessionState.Recording || _rec is null) return;

        // 切段收尾的几百毫秒里 recorder 正在 Stop,再 Stop 一次会抛异常并把整场录制判失败。
        // 记下请求,等新段起来立刻暂停 —— 用户看到的就是"暂停生效了,晚了半秒"。
        if (_stopForRollover)
        {
            _pausePending = true;
            Diag.Log("pause: deferred until rollover completes");
            return;
        }

        Diag.Log("pause: stopping current segment");
        _stopForPause = true;
        _pauseStart = Stopwatch.GetTimestamp();

        // 先停特效再停内核:否则叠加层刷新会和 recorder 收尾抢同一把锁(实测能把 Stop 挂死)
        _effects?.Detach();

        try
        {
            _rec.Stop();
        }
        catch (Exception ex)
        {
            _stopForPause = false;
            Fail("暂停失败:" + ex.Message);
        }
    }

    /// <summary>恢复:新开一段,时间线紧接着上一段。</summary>
    public void Resume()
    {
        if (State != SessionState.Paused) return;

        try
        {
            _pauseTotalTicks += Stopwatch.GetTimestamp() - _pauseStart;
            StartSegment(_segments.Count + 1);
            SetState(SessionState.Recording);
            Diag.Log("resume: new segment started");
        }
        catch (Exception ex)
        {
            Fail("恢复失败:" + ex.Message);
        }
    }

    /// <summary>请求停止:结果经 Completed/Failed 事件异步送达。</summary>
    public void Stop()
    {
        if (State is SessionState.Idle or SessionState.Finalizing) return;

        if (State == SessionState.Paused)
        {
            // 暂停中停止:没有活动段,直接合并已有段(空录一场时可能只有 1 段)
            _pauseTotalTicks += Stopwatch.GetTimestamp() - _pauseStart;
            var elapsed = Elapsed;
            SetState(SessionState.Finalizing);
            Finish(elapsed);
            return;
        }

        if (_rec is null) return;

        // 切段已经发出 Stop:那一次完成回调会走 Finish,这里再 Stop 一次只会把内核惹毛。
        // 先切状态、清标志,让回调按"正常结束"处理即可。
        bool rolloverInFlight = _stopForRollover;

        SetState(SessionState.Finalizing);
        _stopForPause = false;
        _stopForRollover = false;
        _pausePending = false;

        if (rolloverInFlight)
        {
            Diag.Log("stop: rollover already in flight, waiting for finalize");
            return;
        }

        // 先停特效再停内核:叠加层刷新若与收尾并发,Stop 会卡住不返回(实测)
        _effects?.Detach();

        try
        {
            _rec.Stop();
        }
        catch (Exception ex)
        {
            Fail("停止失败:" + ex.Message);
        }
    }

    private void OnLibRecordingComplete(object? sender, RecordingCompleteEventArgs e)
    {
        // 这段已经停止:先摘掉特效,别往停止的 recorder 下发动态选项
        _effects?.Detach();

        if (!string.IsNullOrEmpty(e.FilePath) && File.Exists(e.FilePath))
            _segments.Add(e.FilePath);

        Diag.Log($"segment done: {e.FilePath} total={_segments.Count}");

        if (_stopForPause)
        {
            // 暂停触发的停止:切到 Paused,等 Resume 再开新段
            _stopForPause = false;
            _stopForRollover = false;
            _rec = null;
            SetState(SessionState.Paused);
            Diag.Log("pause: segment closed");
            return;
        }

        if (_stopForRollover)
        {
            // 定时切段:状态保持 Recording,立刻新开一段
            _stopForRollover = false;
            _rec = null;
            try
            {
                StartSegment(_segments.Count + 1);
                Diag.Log("segment: rollover ok, next part started");

                // 切段期间用户按过暂停:新段已经起来了,现在补上
                if (_pausePending)
                {
                    _pausePending = false;
                    Pause();
                }
            }
            catch (Exception ex)
            {
                _pausePending = false;
                Diag.Log("segment: rollover start FAILED " + ex);
                SetState(SessionState.Idle);
                Failed?.Invoke("切段失败:" + ex.Message);
            }
            return;
        }

        Finish(Elapsed);
    }

    private void OnLibRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        _effects?.Detach();
        Diag.Log("segment failed: " + e.Error);
        if (_stopForPause)
        {
            _stopForPause = false;
            _stopForRollover = false;
            _rec = null;
            SetState(SessionState.Paused);
            Notice?.Invoke("暂停时内核报错,已保留当前分段。");
            return;
        }

        if (_stopForRollover)
        {
            // 切段时内核报错:换新段接着录,别让整场录制陪葬
            _stopForRollover = false;
            _rec = null;
            try
            {
                StartSegment(_segments.Count + 1);
                Notice?.Invoke("切段时内核报错,已自动继续录制。");

                if (_pausePending)
                {
                    _pausePending = false;
                    Pause();
                }
            }
            catch (Exception ex)
            {
                _pausePending = false;
                Diag.Log("segment: rollover-recovery start FAILED " + ex);
                SetState(SessionState.Idle);
                Failed?.Invoke("切段失败:" + ex.Message);
            }
            return;
        }

        SetState(SessionState.Idle);
        Failed?.Invoke(e.Error ?? "录制失败(未知原因)");
    }

    /// <summary>把分段合成最终文件;失败则保留分段。</summary>
    private void Finish(TimeSpan elapsed)
    {
        string resultPath;

        if (_segments.Count == 0)
        {
            SetState(SessionState.Idle);
            Failed?.Invoke("没有生成任何录制分段。");
            return;
        }

        if (_segments.Count == 1)
        {
            // 没有暂停过:直接把 part1 改名为最终文件,零成本
            resultPath = _segments[0];
            try
            {
                File.Move(_segments[0], _finalPath, overwrite: true);
                resultPath = _finalPath;
            }
            catch (Exception ex)
            {
                Diag.Log("finish: rename failed " + ex.Message);
            }
        }
        else
        {
            resultPath = MergeSegments() ?? _segments[0];
        }

        long bytes = File.Exists(resultPath) ? new FileInfo(resultPath).Length : 0;
        _effects?.Dispose();
        _effects = null;
        SetState(SessionState.Idle);
        Diag.Log($"finish: {resultPath} bytes={bytes} parts={_segmentTotal}");
        Completed?.Invoke(new RecordingResult(resultPath, bytes, elapsed, Math.Max(1, _segmentTotal)));
    }

    /// <summary>用原生 Media Foundation 合并器流复制分段(不重编码)。失败时保留全部分段。</summary>
    private string? MergeSegments()
    {
        if (MediaTool.LocateMerger() is null)
        {
            Notice?.Invoke($"未找到 ScreenRec.Merger.exe,已保留 {_segments.Count} 个分段文件。");
            return null;
        }

        try
        {
            var args = new List<string> { "--merge", _finalPath };
            args.AddRange(_segments);

            // 内核 finalize 完不代表句柄立刻释放:SinkWriter 收尾与合并之间只有几百毫秒,
            // 偶发命中 0x80070020(共享冲突)。这不是真失败,退一步重试就好。
            int exitCode = 0;
            string stdout = string.Empty, stderr = string.Empty;
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                exitCode = MediaTool.Invoke(120_000, out stdout, out stderr, args.ToArray());
                bool sharingViolation = stderr.Contains("0x80070020", StringComparison.OrdinalIgnoreCase)
                                        || stderr.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);
                if (exitCode == 0 || !sharingViolation) break;

                Diag.Log($"merge: file busy (attempt {attempt}), retrying");
                Thread.Sleep(400);
            }

            if (exitCode != 0 || !File.Exists(_finalPath) || new FileInfo(_finalPath).Length == 0)
            {
                Diag.Log($"merge: native failed rc={exitCode} {stderr}");
                Notice?.Invoke("分段合并失败,已保留原始分段文件。");
                return null;
            }

            Diag.Log("merge: native ok -> " + _finalPath + " " + stdout.Trim().Replace('\n', ' '));
            foreach (var seg in _segments)
            {
                try { File.Delete(seg); } catch { }
            }
            _segments.Clear();
            _segments.Add(_finalPath);
            return _finalPath;
        }
        catch (Exception ex)
        {
            Diag.Log("merge: native exception " + ex);
            Notice?.Invoke("分段合并出错,已保留原始分段文件。");
            return null;
        }
    }

    /// <summary>录制中截一张 PNG(存输出目录),失败返回 null。</summary>
    public string? TakeSnapshot()
    {
        if (_rec is null || State is not (SessionState.Recording or SessionState.Paused))
            return null;

        try
        {
            Directory.CreateDirectory(_settings.OutputDirectory);
            string path = Path.Combine(
                _settings.OutputDirectory,
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_snap.png");

            bool ok = _rec.TakeSnapshot(path);
            Diag.Log($"snapshot: ok={ok} path={path}");
            return ok && File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            Diag.Log("snapshot: FAILED " + ex.Message);
            return null;
        }
    }

    /// <summary>开录前检查输出盘空间:按当前码率录 10 分钟所需的量,且不低于 1 GB。</summary>
    private void EnsureDiskSpace(int width, int height)
    {
        try
        {
            long free = AvailableOutputBytes();
            if (free == long.MaxValue) return;

            long bitrate = _settings.VideoBitrate(width, height);
            long tenMinutes = (long)(bitrate / 8.0 * 600);
            long need = Math.Max(1L * 1024 * 1024 * 1024, tenMinutes + 200L * 1024 * 1024);

            if (free < need)
                throw new InvalidOperationException(
                    $"输出磁盘空间不足:剩余 {free / 1073741824.0:F1} GB,"
                    + $"按当前画质录 10 分钟约需 {need / 1073741824.0:F1} GB。");

            Diag.Log($"start: disk free={free / 1048576}MB need={need / 1048576}MB");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Diag.Log("start: disk check skipped " + ex.Message);
        }
    }

    /// <summary>输出盘剩余空间(字节);取不到时返回 long.MaxValue 表示不限制。</summary>
    public long AvailableOutputBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.OutputDirectory));
            if (string.IsNullOrEmpty(root)) return long.MaxValue;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : long.MaxValue;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// 录制中所在显示器的分辨率是否变了(拔显示器/改分辨率,继续录只会黑屏,立即停)。
    /// 全屏比对主屏;选区比对选区所在的那台显示器 —— 不能拿选区尺寸去比,
    /// 否则选区录制每 5 秒就被 watch 误杀(实测)。
    /// 窗口模式跟着窗口走,不受显示器分辨率影响。
    /// </summary>
    public bool PrimaryResolutionChanged()
    {
        if (_settings.Target == CaptureTarget.Window || _monitorWidth <= 0) return false;

        var monitor = ScreenInfo.MonitorByDevice(_settings.MonitorDeviceName);
        return monitor.Width != _monitorWidth || monitor.Height != _monitorHeight;
    }

    private void Fail(string message)
    {
        if (State == SessionState.Idle) return;
        try { _rec?.Stop(); } catch { }
        _rec = null;
        _pausePending = false;
        _effects?.Dispose();
        _effects = null;
        SetState(SessionState.Idle);
        Failed?.Invoke(message);
    }

    private void CleanupIncomplete()
    {
        try { _rec?.Stop(); } catch { }
        _rec = null;
        _stopForPause = false;
        _stopForRollover = false;
        _pausePending = false;
        _effects?.Dispose();
        _effects = null;
    }

    private void SetState(SessionState state)
    {
        State = state;
        if (state == SessionState.Recording)
            StartSegmentTimer();
        else
            StopSegmentTimer();
        StateChanged?.Invoke(state);
    }

    /// <summary>进入 Recording 时启动切段计时;SegmentSeconds <= 0 视为关闭。</summary>
    private void StartSegmentTimer()
    {
        StopSegmentTimer();
        int seconds = _settings.SegmentSeconds;
        if (seconds <= 0) return;

        _segmentTimer = new Timer(OnSegmentTimerTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        Diag.Log($"segment timer: every {seconds}s");
    }

    private void StopSegmentTimer()
    {
        try { _segmentTimer?.Dispose(); } catch { }
        _segmentTimer = null;
    }

    private void OnSegmentTimerTick(object? state)
    {
        if (State != SessionState.Recording || _rec is null) return;

        int seconds = _settings.SegmentSeconds;
        if (seconds <= 0) return;

        double segmentElapsed = (Stopwatch.GetTimestamp() - _segmentStart) / (double)Stopwatch.Frequency;
        if (segmentElapsed < seconds) return;

        RolloverSegment();
    }

    /// <summary>
    /// 定时切段:结束当前段落盘后立刻新开一段,状态保持 Recording。
    /// 复用暂停的段落收尾链路(_stopForRollover 与 _stopForPause 互斥,Stop() 会清掉两者)。
    /// </summary>
    private void RolloverSegment()
    {
        if (State != SessionState.Recording || _rec is null) return;

        Diag.Log("segment: timed rollover");
        _stopForRollover = true;
        if (State != SessionState.Recording)
        {
            // 与 Stop() 并发:Stop 已切到 Finalizing 并清标志,本次切段作废
            _stopForRollover = false;
            return;
        }

        // 切段同样要先摘特效:新段起来时 StartSegment 会重新挂上
        _effects?.Detach();

        try
        {
            _rec.Stop();
        }
        catch (Exception ex)
        {
            // 切段失败不结束录制:当前段继续录,下个周期再试
            _stopForRollover = false;
            Diag.Log("segment: rollover stop failed " + ex.Message);
        }
    }
}
