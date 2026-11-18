using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenRecorderLib;

namespace ScreenRec.Core;

/// <summary>
/// 鼠标特效:指针高亮(实心圆)与点击环(单击单环 / 同位置双击双环)。
///
/// 两者都走 ImageOverlay,由内核合成进编码器输入,录制时屏幕上不画任何东西。
///
/// 性能边界(踩过):叠加层每次 Update 的代价随层数上升,5 层时定时切段的收尾就会
/// 踩到编码器 0xC00D4A3E、严重时 Stop 挂死。所以这里只占 2 层:
/// 一层高亮、一层点击环;点击环换颜色/换单双环靠换图(每次点击最多一次,不是逐帧)。
/// </summary>
public sealed class PointerEffects : IDisposable
{
    private const int TickMs = 33;
    private const int HiddenOffset = -20000;
    private const int SM_CXDOUBLECLK = 36;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private readonly Func<int, int, (int X, int Y)?> _toOutput;

    private readonly int _discDiameter;
    private readonly byte[]? _disc;

    private readonly int _ringCanvas;
    private readonly byte[][]? _singleRings;
    private readonly byte[][]? _doubleRings;
    private readonly long _holdTicks;
    private readonly long _doubleClickTicks;
    private readonly int _doubleClickPx;

    private readonly object _gate = new();

    private ImageOverlay? _highlight;
    private ImageOverlay? _click;
    private Recorder? _recorder;
    private Timer? _timer;

    private bool _leftWasDown;
    private bool _rightWasDown;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _ticking;

    private long _clickHideAt;
    private bool _clickVisible;
    private int _clickShownColor = -1;
    private bool _clickShownDouble;
    private long _lastClickTicks;
    private int _lastClickX;
    private int _lastClickY;

    public PointerEffects(RecordSettings settings, Func<int, int, (int X, int Y)?> toOutput)
    {
        _toOutput = toOutput;
        var fx = settings.MouseEffect;

        _discDiameter = Math.Clamp(fx.HighlightDiameter, 16, 200);
        if (fx.HighlightEnabled)
        {
            uint color = RingImages.ParseArgb(fx.HighlightColor, 0xFF4C8DFF);
            _disc = RingImages.Disc(_discDiameter, RingImages.WithOpacity(color, fx.HighlightOpacity));
        }

        if (fx.Enabled)
        {
            double radius = Math.Clamp(fx.RadiusPx, 8, 200);
            double stroke = Math.Max(2.5, radius * 0.16);
            double outerRadius = radius * 1.5;
            double outerStroke = Math.Max(2.0, stroke * 0.75);
            _ringCanvas = (int)Math.Ceiling((outerRadius + outerStroke) * 2) + 4;

            uint left = RingImages.ParseArgb(fx.LeftColor, 0xFF4C8DFF);
            uint right = RingImages.ParseArgb(fx.RightColor, 0xFFE5484D);

            _singleRings = new[]
            {
                RingImages.Circle(_ringCanvas, radius, stroke, left),
                RingImages.Circle(_ringCanvas, radius, stroke, right),
            };
            _doubleRings = new[]
            {
                RingImages.DoubleCircle(_ringCanvas, radius, stroke, left,
                    outerRadius, outerStroke, RingImages.Mix(left, 0xFFFFFFFF, 0.35)),
                RingImages.DoubleCircle(_ringCanvas, radius, stroke, right,
                    outerRadius, outerStroke, RingImages.Mix(right, 0xFFFFFFFF, 0.35)),
            };

            // 双击判定跟着系统设置走:用户把双击速度调快/调慢,这里跟着变,否则永远判不出来
            try { _doubleClickTicks = (long)Math.Clamp(GetDoubleClickTime(), 200u, 1000u) * Stopwatch.Frequency / 1000; }
            catch { _doubleClickTicks = Stopwatch.Frequency / 2; }

            int distance = 0;
            try { distance = GetSystemMetrics(SM_CXDOUBLECLK); } catch { }
            _doubleClickPx = distance > 0 ? distance : 6;

            _holdTicks = (long)(Math.Clamp(fx.DurationMs, 200, 1500) / 1000.0 * Stopwatch.Frequency);
        }

        Diag.Log($"effects: highlight={_disc is not null} rings={_singleRings is not null} "
                 + $"disc={_discDiameter} canvas={_ringCanvas} hold={fx.DurationMs}ms dblPx={_doubleClickPx}");
    }

    /// <summary>是否自己画点击环(为真时内核自带的实心圆点必须关掉,否则两层叠加)。</summary>
    public bool DrawsClicks => _singleRings is not null;

    /// <summary>每个分段都要重新建一套叠加层:Recorder 是按 options 持有它们的。</summary>
    public IReadOnlyList<RecordingOverlayBase> CreateOverlays()
    {
        var overlays = new List<RecordingOverlayBase>();

        lock (_gate)
        {
            if (_disc is not null)
            {
                _highlight = new ImageOverlay
                {
                    AnchorPoint = Anchor.TopLeft,
                    Offset = new ScreenSize(HiddenOffset, HiddenOffset),
                    Size = new ScreenSize(_discDiameter, _discDiameter),
                    SourceStream = new MemoryStream(_disc, writable: false),
                };
                overlays.Add(_highlight);
                _lastX = _lastY = int.MinValue;
            }

            if (_singleRings is not null)
            {
                _click = new ImageOverlay
                {
                    AnchorPoint = Anchor.TopLeft,
                    Offset = new ScreenSize(HiddenOffset, HiddenOffset),
                    Size = new ScreenSize(_ringCanvas, _ringCanvas),
                    SourceStream = new MemoryStream(_singleRings[0], writable: false),
                };
                overlays.Add(_click);
                _clickVisible = false;
                _clickShownColor = -1;
            }
        }

        return overlays;
    }

    public void Attach(Recorder recorder)
    {
        lock (_gate)
        {
            _recorder = recorder;
            _leftWasDown = IsDown(VK_LBUTTON);
            _rightWasDown = IsDown(VK_RBUTTON);
            _timer ??= new Timer(Tick, null, TickMs, TickMs);
        }
    }

    /// <summary>停录/暂停/切段前必须先摘掉:与内核收尾并发会把 Stop 挂死。</summary>
    public void Detach()
    {
        lock (_gate)
        {
            _recorder = null;
            _timer?.Dispose();
            _timer = null;
            _highlight = null;
            _click = null;
            _clickVisible = false;
        }
    }

    public void Dispose() => Detach();

    private void Tick(object? state)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            Diag.Log("effects: tick failed " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void TickCore()
    {
        if (!GetCursorPos(out POINT cursor)) return;
        var mapped = _toOutput(cursor.X, cursor.Y);
        long now = Stopwatch.GetTimestamp();

        var dirty = new List<ImageOverlay>(2);

        lock (_gate)
        {
            if (_recorder is null) return;

            if (_highlight is not null)
            {
                int x = HiddenOffset, y = HiddenOffset;
                if (mapped is { } point)
                {
                    x = point.X - _discDiameter / 2;
                    y = point.Y - _discDiameter / 2;
                }

                bool movedEnough = _lastX == int.MinValue
                                   || Math.Abs(x - _lastX) >= 2
                                   || Math.Abs(y - _lastY) >= 2;
                if ((x != _lastX || y != _lastY) && movedEnough)
                {
                    _highlight.Offset = new ScreenSize(x, y);
                    _lastX = x;
                    _lastY = y;
                    dirty.Add(_highlight);
                }
            }

            if (_click is not null)
            {
                bool leftDown = IsDown(VK_LBUTTON);
                bool rightDown = IsDown(VK_RBUTTON);

                if (leftDown && !_leftWasDown && mapped is { } lp) OnClick(0, lp, now, dirty);
                if (rightDown && !_rightWasDown && mapped is { } rp) OnClick(1, rp, now, dirty);
                _leftWasDown = leftDown;
                _rightWasDown = rightDown;

                if (_clickVisible && now >= _clickHideAt)
                {
                    _clickVisible = false;
                    _click.Offset = new ScreenSize(HiddenOffset, HiddenOffset);
                    dirty.Add(_click);
                }
            }
        }

        if (dirty.Count == 0) return;

        lock (_gate)
        {
            if (_recorder is null) return;
            try
            {
                var builder = _recorder.GetDynamicOptionsBuilder();
                foreach (var overlay in dirty) builder.SetUpdatedOverlay(overlay);
                builder.Apply();
            }
            catch (Exception ex)
            {
                Diag.Log("effects: apply failed " + ex.Message);
            }
        }
    }

    /// <summary>
    /// 单击画单环;同一位置、同一按键、在系统双击时间内再点一下,把那一环升级成双环
    /// (外圈绕着第一下的位置长出来,不是挪到第二下的位置)。
    /// </summary>
    private void OnClick(int colorIndex, (int X, int Y) point, long now, List<ImageOverlay> dirty)
    {
        bool isDouble = _clickVisible
                        && colorIndex == _clickShownColor
                        && now - _lastClickTicks <= _doubleClickTicks
                        && Math.Abs(point.X - _lastClickX) <= _doubleClickPx
                        && Math.Abs(point.Y - _lastClickY) <= _doubleClickPx;

        int centerX = isDouble ? _lastClickX : point.X;
        int centerY = isDouble ? _lastClickY : point.Y;

        if (!isDouble)
        {
            _lastClickX = point.X;
            _lastClickY = point.Y;
        }

        bool needImage = _clickShownColor != colorIndex || _clickShownDouble != isDouble;
        if (needImage)
        {
            var png = (isDouble ? _doubleRings! : _singleRings!)[colorIndex];
            _click!.SourceStream = new MemoryStream(png, writable: false);
            _clickShownColor = colorIndex;
            _clickShownDouble = isDouble;
            dirty.Add(_click);
        }

        _click!.Offset = new ScreenSize(centerX - _ringCanvas / 2, centerY - _ringCanvas / 2);
        _clickVisible = true;
        _clickHideAt = now + _holdTicks;
        _lastClickTicks = now;
        if (!dirty.Contains(_click)) dirty.Add(_click);

        Diag.Log($"effects: click at {centerX},{centerY} color={colorIndex} double={isDouble}");
    }

    private static bool IsDown(int virtualKey)
        => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
