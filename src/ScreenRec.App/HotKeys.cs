using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenRec.App;

/// <summary>
/// 全局快捷键。RegisterHotKey 绑定主窗口句柄,窗口 Hide() 之后依然有效,
/// 所以录制中(主面板已隐藏)也能用。
/// </summary>
public sealed class HotKeys : IDisposable
{
    public const int IdToggleRecord = 0x1001;
    public const int IdTogglePause = 0x1002;
    public const int IdSnapshot = 0x1003;

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly List<int> _registered = new();

    public event Action? ToggleRecord;
    public event Action? TogglePause;
    public event Action? Snapshot;

    public HotKeys(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)
                  ?? throw new InvalidOperationException("主窗口句柄尚未创建,无法注册全局快捷键。");
        _source.AddHook(WndProc);
    }

    /// <summary>注册一个全局热键;返回 false 表示已被其他程序占用。</summary>
    public bool Register(int id, uint virtualKey, uint modifiers = 0)
    {
        if (!RegisterHotKey(_hwnd, id, modifiers, virtualKey)) return false;
        _registered.Add(id);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        switch (wParam.ToInt32())
        {
            case IdToggleRecord:
                ToggleRecord?.Invoke();
                handled = true;
                break;
            case IdTogglePause:
                TogglePause?.Invoke();
                handled = true;
                break;
            case IdSnapshot:
                Snapshot?.Invoke();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered)
        {
            try { UnregisterHotKey(_hwnd, id); } catch { }
        }
        _registered.Clear();
        try { _source.RemoveHook(WndProc); } catch { }
    }
}
