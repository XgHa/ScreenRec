using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenRec.App;

public partial class FloatingBar : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private readonly MainWindow _host;
    private DispatcherTimer? _timer;

    public FloatingBar(MainWindow host)
    {
        InitializeComponent();
        _host = host;
        _host.AgentStateChanged += OnHostStateChanged;

        // 键位跟着设置走,禁用或被占用时退回不带键位的说明
        string? hkPause = host.ActiveHotkey(host.Settings.HotkeyTogglePause);
        string? hkStop = host.ActiveHotkey(host.Settings.HotkeyToggleRecord);

        PauseButton.ToolTip = hkPause is null
            ? "暂停/继续(分段录制,停止时自动合并)"
            : $"暂停/继续 ({hkPause})";
        StopButton.ToolTip = hkStop is null ? "停止并保存" : $"停止并保存 ({hkStop})";
    }

    private void OnHostStateChanged(string state)
    {
        Dispatcher.BeginInvoke(() =>
            PauseIcon.Text = state == "paused" ? "\uE768" : "\uE769");
    }

    private void OnDrag(object sender, System.Windows.Input.MouseButtonEventArgs e) => DragMove();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        var s = _host.Settings;
        if (double.IsNaN(s.FloatingBarLeft) || double.IsNaN(s.FloatingBarTop))
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + 10;
        }
        else
        {
            Left = s.FloatingBarLeft;
            Top = s.FloatingBarTop;
        }

        // 多屏边界保护:上次拖到副屏后副屏被拔/改布局,位置残留会落在屏外,夹回虚拟屏可见范围
        double vsLeft = SystemParameters.VirtualScreenLeft;
        double vsTop = SystemParameters.VirtualScreenTop;
        double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(Left, vsLeft, Math.Max(vsLeft, vsRight - Width));
        Top = Math.Clamp(Top, vsTop, Math.Max(vsTop, vsBottom - Height));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => TimeLabel.Text = Format(_host.CurrentElapsed);
        _timer.Start();
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");

    private void OnPauseClick(object sender, RoutedEventArgs e) => _host.TogglePause();

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        _timer?.Stop();
        _host.StopRecording();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 位置写入宿主 settings,等主面板关闭时随 SettingsStore.Save 一起落盘
        try
        {
            var s = _host.Settings;
            s.FloatingBarLeft = Left;
            s.FloatingBarTop = Top;
        }
        catch { }

        _host.AgentStateChanged -= OnHostStateChanged; // 每场录制新建一个悬浮条,不退订会累积死处理器
        _timer?.Stop();
        base.OnClosed(e);
    }
}
