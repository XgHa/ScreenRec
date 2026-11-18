using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenRec.Core;

namespace ScreenRec.App;

/// <summary>
/// 选区遮罩:覆盖整个虚拟桌面(所有显示器)。拖拽画框,框内拖动整体移动,8 个手柄按坐标调整大小。
/// Enter 或双击确认,Esc/右键取消。Result 是虚拟桌面物理像素坐标,由调用方折算到具体显示器。
///
/// 手柄故意不用 Thumb:Thumb 会捕获鼠标并留在可视树里,实测会把后续的按下事件全部吃掉,
/// 导致画完第一次框以后遮罩就再也不响应。这里手柄只负责画,命中判断自己做。
/// </summary>
public partial class SelectionOverlay : Window
{
    private enum DragMode { None, Create, Move, N, S, E, W, NW, NE, SW, SE }

    private static readonly DragMode[] HandleModes =
    {
        DragMode.NW, DragMode.N, DragMode.NE, DragMode.W,
        DragMode.E, DragMode.SW, DragMode.S, DragMode.SE,
    };

    private const double MinSize = 16;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    private const double HandleSize = 9;
    private const double HandleTolerance = 7;

    private readonly Dictionary<DragMode, Rectangle> _handles = new();

    /// <summary>虚拟桌面左上角的物理像素坐标(副屏在左/上时为负)。</summary>
    private readonly int _originX;
    private readonly int _originY;

    private DragMode _mode = DragMode.None;
    private Point _dragStart;
    private Rect _dragStartRect = Rect.Empty;
    private Rect _sel = Rect.Empty;

    /// <summary>确认后的选区(虚拟桌面物理像素);取消或未选时为 null。</summary>
    public Int32Rect? Result { get; private set; }

    public SelectionOverlay()
    {
        InitializeComponent();

        // 覆盖整个虚拟桌面,否则副屏上的区域根本框不到。
        // 位置/尺寸用物理像素换 DIP;换算基准是系统(主屏)DPI,混合 DPI 多屏时副屏会有偏差。
        var bounds = ScreenInfo.VirtualScreenBounds();
        double scale = ScreenInfo.SystemScale;
        _originX = bounds.X;
        _originY = bounds.Y;

        Left = bounds.X / scale;
        Top = bounds.Y / scale;
        Width = bounds.Width / scale;
        Height = bounds.Height / scale;

        Diag.Log($"overlay: {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height} scale={scale:F2}");

        CreateHandles();

        Loaded += (_, _) =>
        {
            UpdateMask();
            Activate();
            Focus();
            // Activate 不一定能抢到前台(启动器不是前台时),遮罩收不到鼠标就等于没用
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
        };

        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += (_, _) => Cancel();
    }

    /// <summary>只负责画,不参与命中测试(命中由 HitHandle 按坐标判断)。</summary>
    private void CreateHandles()
    {
        var stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));

        foreach (var mode in HandleModes)
        {
            var handle = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = stroke,
                StrokeThickness = 1,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Root.Children.Add(handle);
            _handles[mode] = handle;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);

        if (e.ClickCount == 2 && !_sel.IsEmpty)
        {
            Confirm();
            return;
        }

        HintBox.Visibility = Visibility.Collapsed;

        var handle = HitHandle(p);
        if (handle != DragMode.None)
        {
            _mode = handle;
            _dragStart = p;
            _dragStartRect = _sel;
            return;
        }

        if (!_sel.IsEmpty && _sel.Contains(p))
        {
            // 框内按下 = 整体移动
            _mode = DragMode.Move;
            _dragStart = p;
            _dragStartRect = _sel;
            return;
        }

        _mode = DragMode.Create;
        _dragStart = p;
        _dragStartRect = _sel;
        _sel = new Rect(p, new Size(0, 0));
        UpdateMask();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);

        // 没在拖:换光标提示可调整方向
        if (_mode == DragMode.None)
        {
            Cursor = HitHandle(p) switch
            {
                DragMode.NW or DragMode.SE => Cursors.SizeNWSE,
                DragMode.NE or DragMode.SW => Cursors.SizeNESW,
                DragMode.N or DragMode.S => Cursors.SizeNS,
                DragMode.W or DragMode.E => Cursors.SizeWE,
                _ => Cursors.Cross,
            };
            return;
        }

        double dx = p.X - _dragStart.X;
        double dy = p.Y - _dragStart.Y;

        switch (_mode)
        {
            case DragMode.Create:
                _sel = Normalize(
                    Math.Min(_dragStart.X, p.X), Math.Min(_dragStart.Y, p.Y),
                    Math.Max(_dragStart.X, p.X), Math.Max(_dragStart.Y, p.Y));
                break;

            case DragMode.Move:
            {
                var r = _dragStartRect;
                double x = Math.Clamp(r.X + dx, 0, Math.Max(0, Width - r.Width));
                double y = Math.Clamp(r.Y + dy, 0, Math.Max(0, Height - r.Height));
                _sel = new Rect(x, y, r.Width, r.Height);
                break;
            }

            default:
            {
                double l = _dragStartRect.Left, t = _dragStartRect.Top;
                double rr = _dragStartRect.Right, b = _dragStartRect.Bottom;

                if (_mode is DragMode.N or DragMode.NW or DragMode.NE) t += dy;
                if (_mode is DragMode.S or DragMode.SW or DragMode.SE) b += dy;
                if (_mode is DragMode.W or DragMode.NW or DragMode.SW) l += dx;
                if (_mode is DragMode.E or DragMode.NE or DragMode.SE) rr += dx;

                l = Math.Clamp(l, 0, Width);
                rr = Math.Clamp(rr, 0, Width);
                t = Math.Clamp(t, 0, Height);
                b = Math.Clamp(b, 0, Height);

                _sel = Normalize(Math.Min(l, rr), Math.Min(t, b), Math.Max(l, rr), Math.Max(t, b));
                break;
            }
        }

        UpdateMask();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _mode = DragMode.None;

        // 误点或调得太小:当作取消,回到初始提示
        if (!_sel.IsEmpty && (_sel.Width < MinSize || _sel.Height < MinSize))
        {
            _sel = Rect.Empty;
            HintBox.Visibility = Visibility.Visible;
        }

        UpdateMask();
    }

    private DragMode HitHandle(Point p)
    {
        if (_sel.IsEmpty) return DragMode.None;

        foreach (var mode in HandleModes)
        {
            var (x, y) = HandleCenter(mode);
            if (Math.Abs(p.X - x) <= HandleTolerance && Math.Abs(p.Y - y) <= HandleTolerance)
                return mode;
        }
        return DragMode.None;
    }

    private static Rect Normalize(double l, double t, double r, double b)
        => new(l, t, Math.Max(0, r - l), Math.Max(0, b - t));

    private void UpdateMask()
    {
        double w = Width;
        double h = Height;

        if (_sel.IsEmpty)
        {
            Place(MaskTop, 0, 0, w, h);
            Place(MaskBottom, 0, 0, 0, 0);
            Place(MaskLeft, 0, 0, 0, 0);
            Place(MaskRight, 0, 0, 0, 0);
            SelBorder.Visibility = Visibility.Collapsed;
            SizeTag.Visibility = Visibility.Collapsed;
            UpdateHandles();
            return;
        }

        Place(MaskTop, 0, 0, w, _sel.Top);
        Place(MaskBottom, 0, _sel.Bottom, w, h - _sel.Bottom);
        Place(MaskLeft, 0, _sel.Top, _sel.Left, _sel.Height);
        Place(MaskRight, _sel.Right, _sel.Top, w - _sel.Right, _sel.Height);

        Place(SelBorder, _sel.X, _sel.Y, _sel.Width, _sel.Height);
        SelBorder.Visibility = Visibility.Visible;

        double scale = ScreenInfo.SystemScale;
        int pw = Math.Max(2, (int)Math.Round(_sel.Width * scale));
        int ph = Math.Max(2, (int)Math.Round(_sel.Height * scale));
        SizeText.Text = $"{pw} × {ph}";

        // 不要在这里 Measure:鼠标每动一帧就强制一次布局,事件会积压到看起来像卡死
        SizeTag.Visibility = Visibility.Visible;
        Canvas.SetLeft(SizeTag, Math.Clamp(_sel.Left, 0, Math.Max(0, w - 130)));
        Canvas.SetTop(SizeTag, _sel.Top >= 30 ? _sel.Top - 28 : Math.Min(h - 28, _sel.Bottom + 6));

        UpdateHandles();
    }

    private void UpdateHandles()
    {
        foreach (var (mode, handle) in _handles)
        {
            if (_sel.IsEmpty)
            {
                handle.Visibility = Visibility.Collapsed;
                continue;
            }

            var (x, y) = HandleCenter(mode);
            Canvas.SetLeft(handle, x - HandleSize / 2);
            Canvas.SetTop(handle, y - HandleSize / 2);
            handle.Visibility = Visibility.Visible;
        }
    }

    private (double X, double Y) HandleCenter(DragMode mode)
    {
        double l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom;
        double cx = (l + r) / 2, cy = (t + b) / 2;

        return mode switch
        {
            DragMode.NW => (l, t),
            DragMode.N => (cx, t),
            DragMode.NE => (r, t),
            DragMode.W => (l, cy),
            DragMode.E => (r, cy),
            DragMode.SW => (l, b),
            DragMode.S => (cx, b),
            DragMode.SE => (r, b),
            _ => (cx, cy),
        };
    }

    private static void Place(FrameworkElement element, double x, double y, double w, double h)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, w);
        element.Height = Math.Max(0, h);
    }

    private void Confirm()
    {
        Diag.Log($"overlay: confirm sel={_sel}");
        if (_sel.Width < 8 || _sel.Height < 8)
        {
            Cancel();
            return;
        }

        // 窗口内 DIP × 缩放后要加回虚拟桌面原点,才是跨屏可用的物理坐标
        double scale = ScreenInfo.SystemScale;
        Result = new Int32Rect(
            _originX + (int)Math.Round(_sel.X * scale),
            _originY + (int)Math.Round(_sel.Y * scale),
            Math.Max(2, (int)Math.Round(_sel.Width * scale)),
            Math.Max(2, (int)Math.Round(_sel.Height * scale)));

        Diag.Log($"region: picked {Result.Value.X},{Result.Value.Y} {Result.Value.Width}x{Result.Value.Height}");
        DialogResult = true;
    }

    private void Cancel()
    {
        Result = null;
        DialogResult = false;
    }
}
