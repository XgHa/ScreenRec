using System.Runtime.InteropServices;

namespace ScreenRec.Core;

/// <summary>
/// 一台显示器在虚拟桌面里的位置(单位:物理像素)。
/// DeviceName 是 GDI 设备名(如 \\.\DISPLAY1),录制内核按它选屏。
/// </summary>
public sealed record MonitorInfo(
    string DeviceName, int Index, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public string Label => IsPrimary
        ? $"显示器 {Index} · 主屏 · {Width}×{Height}"
        : $"显示器 {Index} · {Width}×{Height}";
}

/// <summary>
/// 显示器信息(物理像素)。录制分辨率 = 录制源原生分辨率,画质档位只影响码率。
/// </summary>
public static class ScreenInfo
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int MONITORINFOF_PRIMARY = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public static int PrimaryWidth => Math.Max(1, GetSystemMetrics(SM_CXSCREEN));

    public static int PrimaryHeight => Math.Max(1, GetSystemMetrics(SM_CYSCREEN));

    /// <summary>按高度命名,如 1080P / 1440P。</summary>
    public static string ResolutionLabel => PrimaryHeight >= 2160 ? "4K"
        : PrimaryHeight >= 1440 ? "1440P"
        : PrimaryHeight >= 1080 ? "1080P"
        : PrimaryHeight >= 720 ? "720P"
        : $"{PrimaryHeight}P";

    public static string SizeLabel => $"{PrimaryWidth}x{PrimaryHeight}";

    /// <summary>系统 DPI 缩放(96 为 1.0),用于物理像素与 DIP 互换。</summary>
    public static double SystemScale
    {
        get
        {
            try
            {
                uint dpi = GetDpiForSystem();
                return dpi > 0 ? dpi / 96.0 : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }
    }

    /// <summary>
    /// 枚举当前所有显示器(按系统顺序,Index 从 1 开始)。
    /// 不调用 ScreenRecorderLib 的 Recorder.GetDisplays():该库的枚举 API 有直接崩进程的前科。
    /// </summary>
    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();

        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data) =>
        {
            var info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (GetMonitorInfo(hMonitor, ref info))
            {
                list.Add(new MonitorInfo(
                    info.szDevice ?? string.Empty,
                    list.Count + 1,
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    Math.Max(1, info.rcMonitor.Right - info.rcMonitor.Left),
                    Math.Max(1, info.rcMonitor.Bottom - info.rcMonitor.Top),
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        };

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败时退化成单屏
        }

        if (list.Count == 0)
            list.Add(new MonitorInfo(@"\\.\DISPLAY1", 1, 0, 0, PrimaryWidth, PrimaryHeight, true));

        return list;
    }

    /// <summary>
    /// 主显示器在虚拟桌面中的物理像素范围。多屏且副屏在左/上时,主屏原点不是 (0,0),
    /// 覆盖遮罩必须用这个矩形定位,否则会盖到副屏上。
    /// </summary>
    public static (int X, int Y, int Width, int Height) PrimaryMonitorBounds()
    {
        var monitors = Monitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        return (primary.X, primary.Y, primary.Width, primary.Height);
    }

    /// <summary>整个虚拟桌面的物理像素范围(所有显示器并集)。</summary>
    public static (int X, int Y, int Width, int Height) VirtualScreenBounds()
    {
        var monitors = Monitors();
        int left = monitors.Min(m => m.X);
        int top = monitors.Min(m => m.Y);
        int right = monitors.Max(m => m.Right);
        int bottom = monitors.Max(m => m.Bottom);
        return (left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>按设备名取显示器;找不到时退回主屏。</summary>
    public static MonitorInfo MonitorByDevice(string? deviceName)
    {
        var monitors = Monitors();
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var hit = monitors.FirstOrDefault(
                m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>矩形覆盖面积最大的那台显示器(跨屏时取主覆盖屏)。</summary>
    public static MonitorInfo MonitorForRect(int x, int y, int width, int height)
    {
        var monitors = Monitors();
        MonitorInfo? best = null;
        long bestArea = 0;

        foreach (var m in monitors)
        {
            int ix = Math.Max(x, m.X);
            int iy = Math.Max(y, m.Y);
            int ir = Math.Min(x + width, m.Right);
            int ib = Math.Min(y + height, m.Bottom);
            if (ir <= ix || ib <= iy) continue;

            long area = (long)(ir - ix) * (ib - iy);
            if (area > bestArea)
            {
                bestArea = area;
                best = m;
            }
        }

        return best ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    /// <summary>
    /// 窗口在虚拟桌面上的实际范围(物理像素)。
    /// 优先用 DWM 的扩展边框:它在缩放/无边框窗口下比 GetWindowRect 更贴近实际画面,
    /// 指针高亮的坐标换算全靠它。
    /// </summary>
    public static (int X, int Y, int Width, int Height) WindowFrameBounds(long handle)
    {
        var ptr = new IntPtr(handle);
        RECT rect = default;
        try
        {
            int hr = DwmGetWindowAttribute(ptr, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>());
            if (hr != 0) GetWindowRect(ptr, out rect);
        }
        catch
        {
            try { GetWindowRect(ptr, out rect); } catch { return (0, 0, 0, 0); }
        }

        int width = Math.Max(0, rect.Right - rect.Left);
        int height = Math.Max(0, rect.Bottom - rect.Top);
        return (rect.Left, rect.Top, width, height);
    }
}
