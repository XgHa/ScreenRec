using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenRec.Core;

namespace ScreenRec.App;

public partial class MainWindow : Window
{
    private RecordSettings _settings = SettingsStore.Load();
    private Process? _agent;
    private bool _recording;
    private bool _paused;
    private TimeSpan _elapsed;
    private FloatingBar? _bar;

    /// <summary>录制中点了关闭:已发出 stop,等 agent 收尾完成后真正退出。</summary>
    private bool _closingAfterStop;

    private HotKeys? _hotKeys;
    private TrayIcon? _tray;

    /// <summary>注册失败(被别的程序占用)的键名;提示行与 ToolTip 要排除掉。</summary>
    private readonly List<string> _failedHotkeys = new();

    /// <summary>悬浮条订阅:时长 tick 与状态切换。</summary>
    public event Action<TimeSpan>? AgentTick;
    public event Action<string>? AgentStateChanged;

    /// <summary>当前录制时长(由 agent tick 驱动)。</summary>
    public TimeSpan CurrentElapsed => _elapsed;

    /// <summary>悬浮条读写位置记忆用。</summary>
    internal RecordSettings Settings => _settings;

    public MainWindow()
    {
        InitializeComponent();
        UpdateAudioLabel();
        UpdateQualityLabel();
        RestoreCaptureMode();
        if (!double.IsNaN(_settings.MainPanelLeft))
        {
            Left = _settings.MainPanelLeft;
            Top = _settings.MainPanelTop;
        }

        Loaded += (_, _) => TryRecoverOrphanSegments();
    }

    /// <summary>
    /// 上次异常退出可能留下 _partN.mp4 分段(定时切段后崩溃只丢最后一段,前面的段都完好)。
    /// 启动时扫描输出目录,询问用户是否合并恢复;窗口加载完成后执行,避免构造期弹窗抢焦点。
    /// 崩溃时正在写的那一段没有索引、打不开,提示里先说明会被跳过,别让用户以为能全救回来。
    /// </summary>
    private void TryRecoverOrphanSegments()
    {
        List<OrphanGroup> groups;
        try
        {
            groups = OrphanMerge.Scan(_settings.OutputDirectory).ToList();
        }
        catch (Exception ex)
        {
            Diag.Log("recover: scan threw " + ex.Message);
            return;
        }
        if (groups.Count == 0) return;

        long totalBytes = groups.Sum(g => g.TotalBytes);
        string names = string.Join("\n", groups.Take(3).Select(g =>
            Path.GetFileName(g.FinalPath) + $"  ({g.Parts.Count} 段,{g.TotalBytes / 1024.0 / 1024.0:F1} MB)"));
        if (groups.Count > 3) names += $"\n… 共 {groups.Count} 组";

        var choice = MessageBox.Show(this,
            "发现上次未完成的录制,分段完好:\n\n" + names
            + $"\n\n共 {groups.Count} 组 / {totalBytes / 1024.0 / 1024.0:F1} MB。是否合并恢复为完整视频?"
            + "\n\n崩溃时正在写入的那一段不完整,会被跳过并改名 .broken 保留(不删除)。",
            "ScreenRec", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (choice != MessageBoxResult.Yes)
        {
            Diag.Log("recover: user declined, segments kept");
            return;
        }

        // 逐组合并;Merger 是秒级流复制,串行即可,不弹进度框。
        // 合并前要逐段探测,组多时是十几秒的后台活,绝不能放 UI 线程。
        Task.Run(() =>
        {
            foreach (var g in groups)
            {
                var result = OrphanMerge.Merge(g);
                if (result.Path is null)
                {
                    string note = result.Note;
                    Dispatcher.BeginInvoke(() => ToastWindow.Show("恢复失败",
                        Path.GetFileName(g.FinalPath) + " " + note, null));
                }
                else
                {
                    string path = result.Path;
                    string detail = result.Note.Length > 0 ? " · " + result.Note : string.Empty;
                    Dispatcher.BeginInvoke(() => ToastWindow.Show("已恢复上次录制",
                        Path.GetFileName(path) + $"  {new FileInfo(path).Length / 1024.0 / 1024.0:F1} MB" + detail,
                        () => Reveal(path)));
                }
            }
        });
    }

    /// <summary>按当前设置同步三个模式 tab 的选中态与文案。</summary>
    private void RestoreCaptureMode()
    {
        if (_settings.Target == CaptureTarget.Window && _settings.WindowHandle != 0)
        {
            bool alive;
            try
            {
                alive = WindowCatalog.List().Any(w => w.Handle.ToInt64() == _settings.WindowHandle);
            }
            catch
            {
                alive = false;
            }

            if (!alive)
            {
                _settings.Target = CaptureTarget.FullScreen;
                _settings.WindowHandle = 0;
                _settings.WindowTitle = null;
            }
        }

        if (_settings.Target == CaptureTarget.Region
            && (_settings.RegionWidth < 8 || _settings.RegionHeight < 8))
        {
            _settings.Target = CaptureTarget.FullScreen;
        }

        // 选区记的是「相对某台显示器」的坐标。显示器拔了/改了分辨率后,旧矩形可能整块落在屏外,
        // 再录就是黑屏,所以启动时先校验一遍,越界直接退回全屏。
        if (_settings.Target == CaptureTarget.Region)
        {
            var monitor = ScreenInfo.MonitorByDevice(_settings.MonitorDeviceName);
            bool outOfRange = _settings.RegionX < 0 || _settings.RegionY < 0
                              || _settings.RegionX + _settings.RegionWidth > monitor.Width
                              || _settings.RegionY + _settings.RegionHeight > monitor.Height;
            if (outOfRange)
            {
                Diag.Log($"region: stale rect {_settings.RegionX},{_settings.RegionY} "
                         + $"{_settings.RegionWidth}x{_settings.RegionHeight} on {monitor.DeviceName}, back to fullscreen");
                _settings.Target = CaptureTarget.FullScreen;
            }
            else
            {
                _settings.MonitorDeviceName = monitor.DeviceName;
            }
        }

        TabFull.IsChecked = _settings.Target == CaptureTarget.FullScreen;
        TabWindow.IsChecked = _settings.Target == CaptureTarget.Window;
        TabRegion.IsChecked = _settings.Target == CaptureTarget.Region;

        TabRegion.Content = _settings.Target == CaptureTarget.Region
            ? $"选区 {_settings.RegionWidth}×{_settings.RegionHeight}"
            : "选区";
    }

    private void OnFullModeClick(object sender, RoutedEventArgs e)
    {
        _settings.Target = CaptureTarget.FullScreen;
        _settings.MonitorDeviceName = null; // 全屏=主屏,别继承上次选区所在的副屏
        _settings.WindowHandle = 0;
        _settings.WindowTitle = null;
        TabRegion.Content = "选区";
    }

    /// <summary>点"选区":隐藏主面板,弹出全屏遮罩拖框,确认后记录物理像素矩形。</summary>
    private void OnRegionModeClick(object sender, RoutedEventArgs e)
    {
        Diag.Log("region: opening overlay");
        Hide();
        try
        {
            var overlay = new SelectionOverlay();
            bool? ok = overlay.ShowDialog();
            Diag.Log($"region: dialog closed ok={ok}");

            if (ok == true && overlay.Result is { } rect)
            {
                // 遮罩覆盖整个虚拟桌面,但一次只能录一台显示器:
                // 取覆盖面积最大的那台,并把选区折成相对它的坐标。
                var monitor = ScreenInfo.MonitorForRect(rect.X, rect.Y, rect.Width, rect.Height);
                int rx = Math.Clamp(rect.X - monitor.X, 0, Math.Max(0, monitor.Width - 2));
                int ry = Math.Clamp(rect.Y - monitor.Y, 0, Math.Max(0, monitor.Height - 2));
                int rw = Math.Min(rect.Width, monitor.Width - rx);
                int rh = Math.Min(rect.Height, monitor.Height - ry);

                Diag.Log($"region: picked {rect.X},{rect.Y} {rect.Width}x{rect.Height} "
                         + $"-> {monitor.DeviceName} {rx},{ry} {rw}x{rh}");

                if (rw < 8 || rh < 8)
                {
                    ToastWindow.Show("选区无效", "选区需要完整落在一台显示器内。", null);
                }
                else
                {
                    _settings.Target = CaptureTarget.Region;
                    _settings.MonitorDeviceName = monitor.DeviceName;
                    _settings.RegionX = rx;
                    _settings.RegionY = ry;
                    _settings.RegionWidth = rw;
                    _settings.RegionHeight = rh;
                    _settings.WindowHandle = 0;
                    _settings.WindowTitle = null;
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log("region: FAILED " + ex);
            ToastWindow.Show("选区失败", ex.Message, null);
        }
        finally
        {
            Diag.Log("region: restoring panel");
            try
            {
                Show();
                // 遮罩关闭后前台可能被别的窗口占了,来回切一次 Topmost 强制回到最前
                Topmost = true;
                Topmost = false;
                Activate();
                RestoreCaptureMode();
                Diag.Log("region: panel restored");
            }
            catch (Exception ex)
            {
                // 窗口已被关闭(例如用户在此期间退出了程序),不要再抛到 Dispatcher
                Diag.Log("region: restore skipped " + ex.Message);
            }
        }
    }

    private void OnWindowModeClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("SkinContextMenu") };
        var windows = WindowCatalog.List();

        if (windows.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "没有可录制的窗口", IsEnabled = false });
        }
        else
        {
            foreach (var w in windows.Take(30))
            {
                var item = new MenuItem
                {
                    Header = Shorten(w.Title, 42),
                    IsChecked = _settings.WindowHandle == w.Handle.ToInt64(),
                };
                long handle = w.Handle.ToInt64();
                string title = w.Title;
                item.Click += (_, _) =>
                {
                    _settings.Target = CaptureTarget.Window;
                    _settings.MonitorDeviceName = null;
                    _settings.WindowHandle = handle;
                    _settings.WindowTitle = title;
                    TabFull.IsChecked = false;
                    TabWindow.IsChecked = true;
                    TabRegion.Content = "选区";
                };
                menu.Items.Add(item);
            }
        }

        // 没选窗口就点外面关掉:退回全屏,别让 tab 与实际录制源不一致
        menu.Closed += (_, _) =>
        {
            if (_settings.Target != CaptureTarget.Window)
            {
                TabFull.IsChecked = true;
                TabWindow.IsChecked = false;
            }
        };

        menu.PlacementTarget = TabWindow;
        menu.IsOpen = true;
    }

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private void OnDrag(object sender, MouseButtonEventArgs e) => DragMove();

    /// <summary>最小化到托盘:隐藏窗口,托盘图标常驻,左键/双击唤回。</summary>
    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        // 托盘没建起来(Shell_NotifyIcon 失败)时不能再 Hide —— 窗口会没有任何入口能叫回来
        if (_tray is null)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var wa = SystemParameters.WorkArea;
        if (Left + Width > wa.Right || Left < wa.Left) Left = Math.Max(wa.Left, wa.Right - Width - 12);
        if (Top < wa.Top) Top = wa.Top;

        SetupHotKeys();
        SetupTray();
    }

    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon(this, "ScreenRec · 左键显示面板");
            _tray.LeftClick += ShowFromTray;
            _tray.RightClick += ShowTrayMenu;
        }
        catch (Exception ex)
        {
            Diag.Log("tray: init failed " + ex.Message);
        }
    }

    /// <summary>从托盘唤回主面板。录制中主面板本就隐藏,改为提示悬浮条。</summary>
    private void ShowFromTray()
    {
        if (_recording)
        {
            ToastWindow.Show("正在录制", "用顶部悬浮条或 F9 停止。", null);
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        // 从托盘唤回时前台不一定给面子,来回切 Topmost 强制置顶
        Topmost = true;
        Topmost = false;
        Activate();
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("SkinContextMenu") };

        var show = new MenuItem { Header = _recording ? "录制中 · 显示提示" : "显示主面板" };
        show.Click += (_, _) => ShowFromTray();
        menu.Items.Add(show);

        menu.Items.Add(new Separator());

        var rec = new MenuItem
        {
            Header = _recording ? "停止录制" : "开始录制",
            InputGestureText = RecordSettings.HotkeyName(_settings.HotkeyToggleRecord) ?? string.Empty,
        };
        rec.Click += (_, _) =>
        {
            if (_recording) StopRecording();
            else
            {
                ShowFromTray();
                StartRecordingAsync();
            }
        };
        menu.Items.Add(rec);

        var pause = new MenuItem
        {
            Header = _paused ? "继续录制" : "暂停录制",
            IsEnabled = _recording,
            InputGestureText = RecordSettings.HotkeyName(_settings.HotkeyTogglePause) ?? string.Empty,
        };
        pause.Click += (_, _) => TogglePause();
        menu.Items.Add(pause);

        menu.Items.Add(new Separator());

        var folder = new MenuItem { Header = "打开输出目录" };
        folder.Click += (_, _) => OnFolderClick(this, new RoutedEventArgs());
        menu.Items.Add(folder);

        var settings = new MenuItem { Header = "设置" };
        settings.Click += (_, _) => OnSettingsClick(this, new RoutedEventArgs());
        menu.Items.Add(settings);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 按当前设置注册全局快捷键。改完设置会重新调用,所以先把旧的注销掉 ——
    /// 不注销的话同一个键还挂在旧注册上,新注册必然失败。
    /// </summary>
    private void SetupHotKeys()
    {
        _hotKeys?.Dispose();
        _hotKeys = null;
        _failedHotkeys.Clear();

        try
        {
            _hotKeys = new HotKeys(this);
            _hotKeys.ToggleRecord += () =>
            {
                if (_recording) StopRecording();
                else StartRecordingAsync();
            };
            _hotKeys.TogglePause += TogglePause;
            _hotKeys.Snapshot += TakeSnapshot;

            RegisterHotkey(HotKeys.IdToggleRecord, _settings.HotkeyToggleRecord);
            RegisterHotkey(HotKeys.IdTogglePause, _settings.HotkeyTogglePause);
            RegisterHotkey(HotKeys.IdSnapshot, _settings.HotkeySnapshot);

            if (_failedHotkeys.Count > 0)
                ToastWindow.Show("快捷键未全部生效",
                    $"{string.Join(" / ", _failedHotkeys)} 已被其他程序占用。", null);
        }
        catch (Exception ex)
        {
            ToastWindow.Show("快捷键注册失败", ex.Message, null);
        }

        UpdateHotkeyHint();
    }

    private void RegisterHotkey(int id, int virtualKey)
    {
        if (virtualKey == 0) return; // 用户把这个功能禁用了
        if (!_hotKeys!.Register(id, (uint)virtualKey))
            _failedHotkeys.Add(RecordSettings.HotkeyName(virtualKey) ?? $"0x{virtualKey:X2}");
    }

    /// <summary>当前真正生效的键名;禁用或被占用返回 null。悬浮条等外部组件用它显示键位。</summary>
    internal string? ActiveHotkey(int virtualKey)
    {
        string? name = RecordSettings.HotkeyName(virtualKey);
        return name is null || _failedHotkeys.Contains(name) ? null : name;
    }

    /// <summary>
    /// 主面板底部那行键位提示。只显示当前配置、且确实注册成功的键 ——
    /// 被别的程序占用的键显示出来只会误导用户。
    /// </summary>
    private void UpdateHotkeyHint()
    {
        var parts = new List<string>();

        void Add(int virtualKey, string action)
        {
            string? name = RecordSettings.HotkeyName(virtualKey);
            if (name is null || _failedHotkeys.Contains(name)) return;
            parts.Add($"{name} {action}");
        }

        Add(_settings.HotkeyToggleRecord, "录制");
        Add(_settings.HotkeyTogglePause, "暂停");
        Add(_settings.HotkeySnapshot, "截图");

        HotkeyHint.Text = string.Join(" · ", parts);
        HotkeyHint.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        string? recName = RecordSettings.HotkeyName(_settings.HotkeyToggleRecord);
        RecButton.ToolTip = recName is null || _failedHotkeys.Contains(recName)
            ? "开始录制"
            : $"开始录制 ({recName})";
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotKeys?.Dispose();
        _hotKeys = null;
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    private void OnRecClick(object sender, RoutedEventArgs e)
    {
        if (!_recording) StartRecordingAsync();
    }

    private async void StartRecordingAsync()
    {
        RecButton.IsEnabled = false;
        try
        {
            var exe = AgentLauncher.Locate()
                ?? throw new InvalidOperationException("未找到 ScreenRec.Agent.exe,请先构建解决方案。");

            // agent 进程 + localhost TCP:录制内核与 WPF UI 解耦,UI 重绘/卡顿不拖累编码。
            // cmd /c 中转保留(此前用于规避"无控制台进程链"的疑似环境问题);
            // 时长缩水的真因已定位为 DDA/WGC 变化驱动 + IsFixedFramerate=false,已在 Core 修正。
            //
            // 端口冲突重试:AgentPort.PickFree 先探测可绑定,残余竞争或 agent 启动失败时
            // ConnectAgentAsync 经进程退出快速失败,换端口重来,最多 3 次。
            Exception? spawnError = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                _agentPort = AgentPort.PickFree();

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /s /c \" \"{exe}\" --port {_agentPort} \"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                _agent = Process.Start(psi);
                _agent!.EnableRaisingEvents = true;
                _agent.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    if (_recording)
                    {
                        _recording = false;
                        _paused = false;
                        RecButton.IsEnabled = true;
                        _bar?.Close();
                        _bar = null;
                        Show();

                        // 进程崩了最终的 mp4 可能没写完,但分段文件一般还在:把路径直接摆给用户,
                        // 别只说一句"进程意外退出"让人自己去目录里翻。
                        string? salvaged = FindRecentSegment();
                        ToastWindow.Show("录制进程意外退出",
                            salvaged is null
                                ? "没有找到可用的分段文件。"
                                : $"已保留分段:{Path.GetFileName(salvaged)}",
                            salvaged is null ? null : () => Reveal(salvaged));
                    }

                    if (_closingAfterStop) Close();
                });

                try
                {
                    await ConnectAgentAsync(_agentPort);
                    spawnError = null;
                    break;
                }
                catch (Exception ex)
                {
                    spawnError = ex;
                    Diag.Log($"start: agent spawn attempt {attempt} (port {_agentPort}) failed: {ex.Message}");
                    CleanupAgent();
                }
            }

            if (spawnError is not null)
                throw new InvalidOperationException("录制引擎(agent)连续 3 次启动失败。", spawnError);

            SendAgent(new { cmd = "start", settings = _settings });
        }
        catch (Exception ex)
        {
            ToastWindow.Show("无法开始录制", ex.Message, null);
            RecButton.IsEnabled = true;
            CleanupAgent();
        }
        await Task.CompletedTask;
    }

    /// <summary>最近 10 分钟内写过的 _part*.mp4,用于 agent 崩溃后告诉用户文件在哪。</summary>
    private string? FindRecentSegment()
    {
        try
        {
            if (!Directory.Exists(_settings.OutputDirectory)) return null;

            return Directory.EnumerateFiles(_settings.OutputDirectory, "*_part*.mp4")
                .Select(p => new FileInfo(p))
                .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 10)
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>关闭管道并收拾 agent 进程树(含 cmd 的子进程)。</summary>
    private void CleanupAgent()
    {
        try { _agentWriter?.Dispose(); } catch { }
        try { _agentReader?.Dispose(); } catch { }
        try { _agentStream?.Dispose(); } catch { }
        _agentWriter = null;
        _agentReader = null;
        _agentStream = null;

        try
        {
            if (_agent is { HasExited: false }) _agent.Kill(true);
        }
        catch { }
        _agent = null;
    }

    private int _agentPort;

    private async Task ConnectAgentAsync(int port)
    {
        var tcp = new System.Net.Sockets.TcpClient();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            // agent 进程链已退出(典型:端口被占导致绑定失败):立即报错换端口,
            // 不把 10 秒超时耗完 —— cmd /c 中转下 cmd 与 agent 同生共死
            if (_agent is { HasExited: true })
                throw new InvalidOperationException("agent 进程提前退出(端口可能被占用)。");

            try
            {
                await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
                last = null;
                break;
            }
            catch (Exception ex) { last = ex; }
            await Task.Delay(300);
        }
        if (last is not null || !tcp.Connected)
            throw new InvalidOperationException("无法连接录制引擎(agent)。", last);

        _agentStream = tcp.GetStream();
        _agentReader = new StreamReader(_agentStream, System.Text.Encoding.UTF8);
        _agentWriter = new StreamWriter(_agentStream, System.Text.Encoding.UTF8) { AutoFlush = true };

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await _agentReader.ReadLineAsync()) is not null)
                    _ = Dispatcher.BeginInvoke(() => HandleAgentLine(line));
            }
            catch { }
        });
    }

    private NetworkStream? _agentStream;
    private StreamReader? _agentReader;
    private StreamWriter? _agentWriter;

    private void HandleAgentLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var ev = root.GetProperty("ev").GetString();
            switch (ev)
            {
                case "attached":
                    // 握手完成,等 agent 发 started
                    break;

                case "started":
                    _recording = true;
                    _paused = false;
                    _elapsed = TimeSpan.Zero;
                    RecButton.IsEnabled = false;
                    Hide();
                    _bar = new FloatingBar(this);
                    _bar.Show();
                    break;

                case "state":
                    _paused = root.GetProperty("s").GetString() == "paused";
                    AgentStateChanged?.Invoke(_paused ? "paused" : "recording");
                    break;

                case "tick":
                    _elapsed = TimeSpan.FromSeconds(root.GetProperty("sec").GetDouble());
                    AgentTick?.Invoke(_elapsed);
                    break;

                case "notice":
                    ToastWindow.Show("提示", root.GetProperty("msg").GetString() ?? "", null);
                    break;

                case "snapshot":
                {
                    bool ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
                    string? shot = root.TryGetProperty("path", out var pathProp) ? pathProp.GetString() : null;
                    if (ok && shot is not null)
                        ToastWindow.Show("已截图", Path.GetFileName(shot), () => Reveal(shot));
                    else
                        ToastWindow.Show("截图失败", "录制内核未能生成图片。", null);
                    break;
                }

                case "completed":
                {
                    _recording = false;
                    _paused = false;
                    RecButton.IsEnabled = true;
                    _bar?.Close();
                    _bar = null;
                    Show();

                    var path = root.GetProperty("path").GetString() ?? "";
                    var bytes = root.GetProperty("bytes").GetInt64();
                    int segments = root.TryGetProperty("segments", out var segProp) ? segProp.GetInt32() : 1;

                    string detail = $"{Path.GetFileName(path)}  {bytes / 1024.0 / 1024.0:F1} MB";
                    if (segments > 1) detail += $"  ·  {segments} 段已合并";

                    ToastWindow.Show("录制完成", detail, () => Reveal(path));

                    CleanupAgent(); // 会话已完结,agent 不会再收命令,提前关管道让它退出
                    if (_closingAfterStop) Close();
                    break;
                }

                case "failed":
                {
                    _recording = false;
                    _paused = false;
                    RecButton.IsEnabled = true;
                    _bar?.Close();
                    _bar = null;
                    Show();
                    ToastWindow.Show("录制出错", root.GetProperty("msg").GetString() ?? "", null);

                    CleanupAgent();
                    if (_closingAfterStop) Close();
                    break;
                }

                case "probe":
                    // 编码器探测信息,忽略或后续入日志
                    break;
            }
        }
        catch { }
    }

    private void SendAgent(object payload)
    {
        try
        {
            _agentWriter?.WriteLine(JsonSerializer.Serialize(payload, JsonDefaults.Options));
        }
        catch (Exception ex)
        {
            // 序列化/管道异常不能静默:之前的 NaN 序列化失败就是被这里吞掉,表现为点录制没反应
            ToastWindow.Show("控制指令发送失败", ex.Message, null);
        }
    }

    /// <summary>悬浮条:暂停/继续切换。</summary>
    public void TogglePause()
    {
        if (!_recording) return;
        SendAgent(new { cmd = _paused ? "resume" : "pause" });
    }

    /// <summary>悬浮条:停止并保存。</summary>
    public void StopRecording()
    {
        if (!_recording) return;
        SendAgent(new { cmd = "stop" });
    }

    /// <summary>录制中截图(F11)。</summary>
    public void TakeSnapshot()
    {
        if (!_recording) return;
        SendAgent(new { cmd = "snapshot" });
    }

    private void OnAudioClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("SkinContextMenu") };
        AddAudioItem(menu, "仅系统声", AudioMode.SystemOnly);
        AddAudioItem(menu, "仅麦克风", AudioMode.MicOnly);
        AddAudioItem(menu, "双轨(系统声 + 麦克风)", AudioMode.Both);
        AddAudioItem(menu, "静音", AudioMode.Muted);
        menu.PlacementTarget = AudioButton;
        menu.IsOpen = true;
    }

    private void AddAudioItem(ContextMenu menu, string label, AudioMode mode)
    {
        var item = new MenuItem { Header = label, IsChecked = _settings.AudioMode == mode };
        item.Click += (_, _) => { _settings.AudioMode = mode; UpdateAudioLabel(); };
        menu.Items.Add(item);
    }

    private void OnQualityClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("SkinContextMenu") };
        menu.Items.Add(new MenuItem { Header = "帧率", IsEnabled = false });
        foreach (var fps in new[] { 15, 24, 30, 60 })
        {
            var item = new MenuItem { Header = fps + " fps", IsChecked = _settings.FrameRate == fps };
            int f = fps;
            item.Click += (_, _) => { _settings.FrameRate = f; UpdateQualityLabel(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuItem { Header = $"画质 · 输出 {ScreenInfo.SizeLabel}", IsEnabled = false });
        foreach (var (level, name) in new[]
                 {
                     (QualityLevel.Low, "低"), (QualityLevel.Medium, "中"),
                     (QualityLevel.High, "高"), (QualityLevel.Ultra, "极高"),
                 })
        {
            var item = new MenuItem
            {
                // 分辨率固定为主屏原生,档位只决定码率
                Header = RecordSettings.QualityLabel(
                    level, name, ScreenInfo.PrimaryWidth, ScreenInfo.PrimaryHeight),
                IsChecked = _settings.Quality == level,
            };
            var lv = level;
            item.Click += (_, _) => { _settings.Quality = lv; UpdateQualityLabel(); };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = QualityButton;
        menu.IsOpen = true;
    }

    private void UpdateAudioLabel() => AudioButton.Content = _settings.AudioMode switch
    {
        AudioMode.SystemOnly => "仅系统声",
        AudioMode.MicOnly => "仅麦克风",
        AudioMode.Both => "双轨收音",
        _ => "静音",
    };

    private void UpdateQualityLabel() => QualityButton.Content =
        $"{_settings.FrameRate}fps · {QualityName(_settings.Quality)} · {ScreenInfo.ResolutionLabel}";

    private static string QualityName(QualityLevel q) => q switch
    {
        QualityLevel.Low => "低",
        QualityLevel.Medium => "中",
        QualityLevel.High => "高",
        _ => "极高",
    };

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings) { Owner = this };
        win.ShowDialog();
        UpdateAudioLabel();
        UpdateQualityLabel();
        SetupHotKeys(); // 快捷键可能改了:重新注册并刷新底部提示
    }

    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputDirectory);
            Process.Start("explorer.exe", "\"" + _settings.OutputDirectory + "\"");
        }
        catch { }
    }

    internal static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 录制中关窗口 = 停止并保存:先拦下关闭,等 agent 完成收尾再真正退出
        if (_recording)
        {
            e.Cancel = true;
            if (!_closingAfterStop)
            {
                _closingAfterStop = true;
                try { SendAgent(new { cmd = "stop" }); } catch { }
                if (_bar is not null) _bar.IsEnabled = false;
            }
            return;
        }

        try { SendAgent(new { cmd = "quit" }); } catch { }

        _settings.MainPanelLeft = Left;
        _settings.MainPanelTop = Top;
        SettingsStore.Save(_settings);
        base.OnClosing(e);
    }
}
