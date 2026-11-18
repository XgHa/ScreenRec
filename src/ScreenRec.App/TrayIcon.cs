using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ScreenRec.Core;

namespace ScreenRec.App;

/// <summary>
/// 系统托盘图标。不引用 WinForms,直接 Shell_NotifyIcon,发布体积不增加。
/// 左键单击回调 LeftClick,右键回调 RightClick(由宿主弹 WPF 菜单)。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_TRAY = 0x8001; // WM_APP + 1

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;

    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private static readonly IntPtr IDI_APPLICATION = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly HwndSource _source;
    private NOTIFYICONDATA _data;
    private bool _added;
    private readonly bool _ownIcon;

    public event Action? LeftClick;
    public event Action? RightClick;

    public TrayIcon(Window window, string tooltip)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(hwnd)
                  ?? throw new InvalidOperationException("窗口句柄尚未创建,无法添加托盘图标。");

        _source.AddHook(WndProc);

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = LoadAppIcon(out _ownIcon),
            szTip = tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
        Diag.Log($"tray: add={_added} cbSize={_data.cbSize} icon={_data.hIcon} tip={tooltip}");
        if (!_added)
            Diag.Log("tray: Shell_NotifyIcon(NIM_ADD) 失败, err=" + Marshal.GetLastWin32Error());
    }

    /// <summary>优先用 exe 内嵌的应用图标,取不到才退回系统默认图标。</summary>
    private static IntPtr LoadAppIcon(out bool owned)
    {
        owned = false;
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                uint found = ExtractIconEx(exe, 0, out IntPtr large, out IntPtr small, 1);
                if (found > 0)
                {
                    IntPtr icon = small != IntPtr.Zero ? small : large;
                    if (large != IntPtr.Zero && large != icon) DestroyIcon(large);
                    if (icon != IntPtr.Zero)
                    {
                        owned = true;
                        return icon;
                    }
                }
            }
        }
        catch
        {
            // 提取失败就用系统图标
        }
        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    /// <summary>更新悬停提示(例如显示"录制中 00:12")。</summary>
    public void UpdateTip(string tip)
    {
        if (tip.Length > 120) tip = tip[..120];
        _data.szTip = tip;
        if (_added) Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAY) return IntPtr.Zero;

        switch (lParam.ToInt32() & 0xFFFF)
        {
            case WM_LBUTTONUP:
                LeftClick?.Invoke();
                handled = true;
                break;
            case WM_RBUTTONUP:
                RightClick?.Invoke();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added) Shell_NotifyIcon(NIM_DELETE, ref _data);
        _added = false;
        if (_ownIcon && _data.hIcon != IntPtr.Zero) DestroyIcon(_data.hIcon);
        try { _source.RemoveHook(WndProc); } catch { }
    }
}
