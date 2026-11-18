using System.Windows;
using System.Windows.Threading;

namespace ScreenRec.App;

public partial class ToastWindow : Window
{
    private static int _stack;
    private Action? _onClick;

    public ToastWindow() => InitializeComponent();

    public static void Show(string title, string sub, Action? onClick)
    {
        var w = new ToastWindow
        {
            _onClick = onClick,
        };
        w.TitleLabel.Text = title;
        w.SubLabel.Text = sub;

        var work = SystemParameters.WorkArea;
        w.Left = work.Right - w.Width - 18;
        w.Top = work.Bottom - w.Height - 18 - (_stack % 4) * (w.Height + 8);
        _stack++;
        w.Show();

        var auto = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        auto.Tick += (_, _) => w.Close();
        auto.Start();
    }

    private void OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        Close();
        _onClick?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        _stack = Math.Max(0, _stack - 1);
        base.OnClosed(e);
    }
}
