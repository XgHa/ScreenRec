using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenRec.Core;

namespace ScreenRec.App;

public partial class SettingsWindow : Window
{
    private readonly RecordSettings _settings;

    /// <summary>
    /// 下拉项必须用带「属性」的类型。之前用的是 ValueTuple((值, 文案)),
    /// 而 WPF 的 DisplayMemberPath / SelectedValuePath 走 PropertyPath,只认属性不认字段,
    /// Item1/Item2 恰好是字段 —— 结果这些下拉全是空白的,选中项也永远匹配不上。
    /// </summary>
    private sealed record Choice<T>(T Value, string Text);

    public SettingsWindow(RecordSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        // 拖滑条会连续触发 ValueChanged,防抖一下,松手/停顿后再播
        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            PlayPreview(true, null);
        };

        _previewHideTimer.Tick += (_, _) =>
        {
            _previewHideTimer.Stop();
            PreviewClickInner.Visibility = Visibility.Collapsed;
            PreviewClickOuter.Visibility = Visibility.Collapsed;
        };

        InitControls();
        Loaded += OnWindowLoaded;
    }

    private readonly DispatcherTimer _previewDebounce;

    /// <summary>双环预览到点就收:成片里也是停留到时长结束直接消失。</summary>
    private readonly DispatcherTimer _previewHideTimer = new();

    /// <summary>布局完成前 ActualWidth 还是 0,不能画预览。</summary>
    private bool _previewReady;

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _previewReady = true;

        // 光标先摆在右下角,别和自动播放的圆点叠在一起
        if (PreviewCanvas.ActualWidth > 30)
        {
            Canvas.SetLeft(PreviewCursor, PreviewCanvas.ActualWidth - 60);
            Canvas.SetTop(PreviewCursor, PreviewCanvas.ActualHeight - 42);
        }

        if (ChkMouse.IsChecked is true) PlayPreview(true, null);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e) => DragMove();

    private void InitControls()
    {
        CmbSkin.ItemsSource = Skins.Order;
        CmbSkin.SelectedItem = Skins.All.ContainsKey(_settings.Skin) ? _settings.Skin : Skins.Order[0];

        CmbFrameRate.ItemsSource = new[] { 15, 24, 30, 60 };
        CmbFrameRate.SelectedItem = _settings.FrameRate;

        CmbQuality.ItemsSource = new[]
        {
            new Choice<QualityLevel>(QualityLevel.Low,
                RecordSettings.QualityLabel(QualityLevel.Low, "低", ScreenInfo.PrimaryWidth, ScreenInfo.PrimaryHeight)),
            new Choice<QualityLevel>(QualityLevel.Medium,
                RecordSettings.QualityLabel(QualityLevel.Medium, "中", ScreenInfo.PrimaryWidth, ScreenInfo.PrimaryHeight)),
            new Choice<QualityLevel>(QualityLevel.High,
                RecordSettings.QualityLabel(QualityLevel.High, "高", ScreenInfo.PrimaryWidth, ScreenInfo.PrimaryHeight)),
            new Choice<QualityLevel>(QualityLevel.Ultra,
                RecordSettings.QualityLabel(QualityLevel.Ultra, "极高", ScreenInfo.PrimaryWidth, ScreenInfo.PrimaryHeight)),
        };
        CmbQuality.DisplayMemberPath = nameof(Choice<QualityLevel>.Text);
        CmbQuality.SelectedValuePath = nameof(Choice<QualityLevel>.Value);
        CmbQuality.SelectedValue = _settings.Quality;

        // ScreenRecorderLib 只能控制硬件编码开关,指定不了 NVENC/QSV/AMF 具体后端,
        // 所以这里只给两个真实生效的选项;旧配置里的厂商值统一归到"自动"。
        CmbEncoder.ItemsSource = new[]
        {
            new Choice<EncoderChoice>(EncoderChoice.Auto, "自动(硬件优先)"),
            new Choice<EncoderChoice>(EncoderChoice.X264, "软件编码 (CPU)"),
        };
        CmbEncoder.DisplayMemberPath = nameof(Choice<EncoderChoice>.Text);
        CmbEncoder.SelectedValuePath = nameof(Choice<EncoderChoice>.Value);
        CmbEncoder.SelectedValue = _settings.Encoder == EncoderChoice.X264
            ? EncoderChoice.X264
            : EncoderChoice.Auto;

        // 定时切段:0 = 关闭。默认 5 分钟;切段瞬间约损失数百毫秒画面(ToolTip 已说明)。
        // 手改过 settings.json 的非预设值也放进选项里,别让下拉空白。
        var segmentItems = new List<Choice<int>>
        {
            new(0, "关闭"), new(60, "1 分钟"), new(300, "5 分钟"),
            new(600, "10 分钟"), new(900, "15 分钟"),
        };
        if (segmentItems.All(i => i.Value != _settings.SegmentSeconds))
            segmentItems.Add(new Choice<int>(_settings.SegmentSeconds,
                _settings.SegmentSeconds % 60 == 0
                    ? $"{_settings.SegmentSeconds / 60} 分钟"
                    : $"{_settings.SegmentSeconds} 秒"));
        CmbSegment.ItemsSource = segmentItems;
        CmbSegment.DisplayMemberPath = nameof(Choice<int>.Text);
        CmbSegment.SelectedValuePath = nameof(Choice<int>.Value);
        CmbSegment.SelectedValue = Math.Max(0, _settings.SegmentSeconds);

        CmbAudioMode.ItemsSource = new[]
        {
            new Choice<AudioMode>(AudioMode.SystemOnly, "仅系统声"),
            new Choice<AudioMode>(AudioMode.MicOnly, "仅麦克风"),
            new Choice<AudioMode>(AudioMode.Both, "双轨(系统声 + 麦克风)"),
            new Choice<AudioMode>(AudioMode.Muted, "静音"),
        };
        CmbAudioMode.DisplayMemberPath = nameof(Choice<AudioMode>.Text);
        CmbAudioMode.SelectedValuePath = nameof(Choice<AudioMode>.Value);
        CmbAudioMode.SelectedValue = _settings.AudioMode;

        // 麦克风设备选择暂不提供:Recorder.GetSystemAudioCaptureDevices() 在本机直接段错误,
        // 只能用系统默认设备。详见 README 待完善。

        SldSystemVol.Value = _settings.SystemVolume;
        SldSystemVol.ValueChanged += (_, _) => UpdateVolumeLabels();
        SldMicVol.Value = _settings.MicVolume;
        SldMicVol.ValueChanged += (_, _) => UpdateVolumeLabels();
        UpdateVolumeLabels();

        ChkPointer.IsChecked = _settings.ShowMousePointer;
        ChkMouse.IsChecked = _settings.MouseEffect.Enabled;

        ChkHighlight.IsChecked = _settings.MouseEffect.HighlightEnabled;
        SldHighlight.Value = Math.Clamp(_settings.MouseEffect.HighlightDiameter, 16, 140);
        SldHighlight.ValueChanged += (_, _) =>
        {
            LblHighlight.Text = $"{(int)SldHighlight.Value} px";
            if (_ringPoint is { } point) ShowPreviewHighlight(point);
        };
        LblHighlight.Text = $"{(int)SldHighlight.Value} px";

        SldOpacity.Value = Math.Clamp(_settings.MouseEffect.HighlightOpacity * 100, 10, 100);
        SldOpacity.ValueChanged += (_, _) =>
        {
            LblOpacity.Text = $"{(int)SldOpacity.Value}%";
            if (_ringPoint is { } point) ShowPreviewHighlight(point);
        };
        LblOpacity.Text = $"{(int)SldOpacity.Value}%";

        // 三种鼠标颜色都走色盘:选之前就能看到颜色本身,不用靠名字猜
        PalLeftColor.SelectedColor = _settings.MouseEffect.LeftColor;
        PalLeftColor.ColorChanged += _ => SchedulePreview();

        PalRightColor.SelectedColor = _settings.MouseEffect.RightColor;
        PalRightColor.ColorChanged += _ => SchedulePreview();

        PalHighlight.SelectedColor = _settings.MouseEffect.HighlightColor;
        PalHighlight.ColorChanged += _ =>
        {
            if (_ringPoint is { } point) ShowPreviewHighlight(point);
        };

        // 滑条直接就是内核的圆点半径(px),不再做 (min+max)/2 的隐性换算
        SldRadius.Value = _settings.MouseEffect.RadiusPx;
        SldRadius.ValueChanged += (_, _) =>
        {
            LblRadius.Text = $"{(int)SldRadius.Value} px";
            SchedulePreview();
        };
        LblRadius.Text = $"{(int)SldRadius.Value} px";

        SldDuration.Value = _settings.MouseEffect.DurationMs;
        SldDuration.ValueChanged += (_, _) =>
        {
            LblDuration.Text = $"{(int)SldDuration.Value} ms";
            SchedulePreview();
        };
        LblDuration.Text = $"{(int)SldDuration.Value} ms";

        UpdateMouseDetailEnabled();
        UpdatePreviewHint();

        // 快捷键只放开 F1~F12 和「禁用」:全局热键占住字母键会让人在别处打不出字,而且很难想到是谁抢的
        var hotkeyItems = Enumerable.Range(1, 12)
            .Select(i => new Choice<int>(RecordSettings.HotkeyVirtualKey(i), $"F{i}"))
            .Append(new Choice<int>(0, "禁用"))
            .ToArray();

        foreach (var (combo, value) in new[]
                 {
                     (CmbHotkeyRecord, _settings.HotkeyToggleRecord),
                     (CmbHotkeyPause, _settings.HotkeyTogglePause),
                     (CmbHotkeySnapshot, _settings.HotkeySnapshot),
                 })
        {
            combo.ItemsSource = hotkeyItems;
            combo.DisplayMemberPath = nameof(Choice<int>.Text);
            combo.SelectedValuePath = nameof(Choice<int>.Value);
            combo.SelectedValue = RecordSettings.NormalizeHotkey(value);
        }

        TxtOutputDir.Text = _settings.OutputDirectory;
        TxtFileNameTpl.Text = _settings.FileNameTemplate;

        // 版本号放按钮行左侧:不占滚动区高度,但排查"桌面这份是不是旧的"时还用得上
        LblVersion.Text = "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
    }

    private void UpdateVolumeLabels()
    {
        LblSystemVol.Text = $"{SldSystemVol.Value * 100:0}%";
        LblMicVol.Text = $"{SldMicVol.Value * 100:0}%";
    }

    /// <summary>波纹关掉时,颜色/半径/时长没有意义,灰掉避免误以为在生效。</summary>
    private void UpdateMouseDetailEnabled()
    {
        bool on = ChkMouse.IsChecked is true;
        MouseDetail.IsEnabled = on;
        MouseDetail.Opacity = on ? 1.0 : 0.45;

        bool highlight = ChkHighlight.IsChecked is true;
        HighlightDetail.IsEnabled = highlight;
        HighlightDetail.Opacity = highlight ? 1.0 : 0.45;

        if (!on)
        {
            PreviewClickInner.Visibility = Visibility.Collapsed;
            PreviewClickOuter.Visibility = Visibility.Collapsed;
            _previewHideTimer.Stop();
        }
    }

    private void OnMouseEffectToggled(object sender, RoutedEventArgs e)
    {
        UpdateMouseDetailEnabled();
        UpdatePreviewHint();
        if (ChkMouse.IsChecked is true) PlayPreview(true, null);
    }

    private void OnHighlightToggled(object sender, RoutedEventArgs e)
    {
        UpdateMouseDetailEnabled();
        if (ChkHighlight.IsChecked is not true) PreviewHighlight.Visibility = Visibility.Collapsed;
        UpdatePreviewHint();
    }

    /// <summary>提示语要跟着开关走:没开高亮就别让人把鼠标在画布上晃来晃去找环。</summary>
    private void UpdatePreviewHint()
    {
        if (PreviewHint is null) return;

        bool highlight = ChkHighlight.IsChecked is true;
        bool ripple = ChkMouse.IsChecked is true;
        PreviewHint.Text = (highlight, ripple) switch
        {
            (true, true) => "鼠标移进画布能看到实心高亮圆;点一下是单环,原地再点一下变双环",
            (true, false) => "把鼠标移到画布上就能看到成片里的高亮圆(直径和透明度都是真实值)",
            (false, true) => "点一下是单环,在原地再点一下变双环(左键 / 右键各用各的颜色)",
            _ => "高亮和点击环都关着,成片里不会有鼠标特效",
        };
    }

    /// <summary>参数改了就用新值重播一次(防抖,滑条拖动时不至于闪成一片)。</summary>
    private void SchedulePreview()
    {
        if (!_previewReady) return;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void OnPreviewOptionChanged(object sender, SelectionChangedEventArgs e) => SchedulePreview();

    /// <summary>最近一次鼠标在预览画布里的位置,改滑条时要按它重画高亮圆。</summary>
    private Point? _ringPoint;

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (ChkHighlight.IsChecked is not true)
        {
            PreviewHighlight.Visibility = Visibility.Collapsed;
            return;
        }

        _ringPoint = e.GetPosition(PreviewCanvas);
        ShowPreviewHighlight(_ringPoint.Value);
    }

    private void OnPreviewMouseLeave(object sender, MouseEventArgs e)
    {
        PreviewHighlight.Visibility = Visibility.Collapsed;
        _ringPoint = null;
    }

    /// <summary>
    /// 按当前参数在鼠标位置画一个实心圆:直径和透明度就是成片里的值(像素 ÷ DPI 缩放),
    /// 用户不用录一段才知道"140px、35% 透明"到底是什么样。
    /// </summary>
    private void ShowPreviewHighlight(Point at)
    {
        if (!_previewReady || PreviewCanvas.ActualWidth < 8) return;
        if (ChkHighlight.IsChecked is not true)
        {
            PreviewHighlight.Visibility = Visibility.Collapsed;
            return;
        }

        double dpi = ScreenInfo.SystemScale;
        if (dpi <= 0) dpi = 1;

        double size = Math.Max(8, SldHighlight.Value) / dpi;
        double maxSize = Math.Min(PreviewCanvas.ActualWidth, PreviewCanvas.ActualHeight) * 0.98;
        if (size > maxSize) size = maxSize;

        Color color = ParseColor(PalHighlight.SelectedColor ?? _settings.MouseEffect.HighlightColor);
        color.A = (byte)Math.Clamp(SldOpacity.Value * 2.55, 0, 255);

        PreviewHighlight.Width = size;
        PreviewHighlight.Height = size;
        PreviewHighlight.Fill = new SolidColorBrush(color);
        Canvas.SetLeft(PreviewHighlight, at.X - size / 2);
        Canvas.SetTop(PreviewHighlight, at.Y - size / 2);
        PreviewHighlight.Visibility = Visibility.Visible;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // ClickCount 由 WPF 按系统双击时间算好:预览里的"双击变双环"和成片判定口径一致
        PlayPreview(e.ChangedButton != MouseButton.Right, e.GetPosition(PreviewCanvas), e.ClickCount >= 2);
        e.Handled = true;
    }

    /// <summary>
    /// 在预览区按当前参数播一次点击效果。at 是画布坐标,传 null 则居中。
    /// 半径按物理像素折算成 DIP(除以 DPI 缩放),预览里的环才和成片里一样大。
    /// isDouble 为真时在外面多画一圈 —— 对应成片里"同位置双击"的那张图。
    /// </summary>
    private void PlayPreview(bool leftButton, Point? at, bool isDouble = false)
    {
        if (!_previewReady || PreviewCanvas.ActualWidth < 8) return;
        if (ChkMouse.IsChecked is not true) return;

        double dpi = ScreenInfo.SystemScale;
        if (dpi <= 0) dpi = 1;

        double radius = Math.Max(2, SldRadius.Value) / dpi;
        double maxRadius = Math.Min(PreviewCanvas.ActualWidth, PreviewCanvas.ActualHeight) * 0.45;
        bool scaled = radius > maxRadius;
        if (scaled) radius = maxRadius;

        string hex = leftButton
            ? PalLeftColor.SelectedColor ?? _settings.MouseEffect.LeftColor
            : PalRightColor.SelectedColor ?? _settings.MouseEffect.RightColor;

        Color color = ParseColor(hex);
        double stroke = Math.Max(1.5, radius * 0.16);

        PreviewClickInner.Width = radius * 2;
        PreviewClickInner.Height = radius * 2;
        PreviewClickInner.Stroke = new SolidColorBrush(color);
        PreviewClickInner.StrokeThickness = stroke;
        PreviewClickInner.Fill = Brushes.Transparent;

        var center = at ?? new Point(PreviewCanvas.ActualWidth / 2, PreviewCanvas.ActualHeight / 2);
        double cx = Math.Clamp(center.X, 0, PreviewCanvas.ActualWidth);
        double cy = Math.Clamp(center.Y, 0, PreviewCanvas.ActualHeight);

        Canvas.SetLeft(PreviewClickInner, cx - radius);
        Canvas.SetTop(PreviewClickInner, cy - radius);
        PreviewClickInner.Visibility = Visibility.Visible;

        if (isDouble)
        {
            double outerRadius = radius * 1.5;
            PreviewClickOuter.Width = outerRadius * 2;
            PreviewClickOuter.Height = outerRadius * 2;
            PreviewClickOuter.Stroke = new SolidColorBrush(MixWithWhite(color, 0.35));
            PreviewClickOuter.StrokeThickness = Math.Max(1.2, stroke * 0.75);
            Canvas.SetLeft(PreviewClickOuter, cx - outerRadius);
            Canvas.SetTop(PreviewClickOuter, cy - outerRadius);
            PreviewClickOuter.Visibility = Visibility.Visible;
        }
        else
        {
            PreviewClickOuter.Visibility = Visibility.Collapsed;
        }

        Canvas.SetLeft(PreviewCursor, Math.Min(cx + 2, PreviewCanvas.ActualWidth - 14));
        Canvas.SetTop(PreviewCursor, Math.Min(cy + 2, PreviewCanvas.ActualHeight - 20));

        PreviewHint.Text = scaled
            ? $"预览区放不下,环已缩小显示;实际半径 {(int)SldRadius.Value} px"
            : (isDouble
                ? "这是双击的效果:外面多一圈 · 实际以成片为准"
                : "单击一圈;在原地再点一下会变成双环 · 实际以成片为准");

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(SldDuration.Value, 50, 5000));

        // 预览必须和成片一致:成片里的环是停留到时长结束就消失,没法做透明渐变
        PreviewClickInner.Opacity = 1;
        PreviewClickOuter.Opacity = 1;
        _previewHideTimer.Stop();
        _previewHideTimer.Interval = duration;
        _previewHideTimer.Start();
    }

    /// <summary>把颜色朝白色混合(0~1),用来生成双环里圈的浅色。</summary>
    private static Color MixWithWhite(Color color, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            color.A,
            (byte)(color.R + (255 - color.R) * t),
            (byte)(color.G + (255 - color.G) * t),
            (byte)(color.B + (255 - color.B) * t));
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color color) return color;
        }
        catch
        {
            // 非法色值不该阻塞预览
        }

        return Color.FromRgb(0x4C, 0x8D, 0xFF);
    }

    /// <summary>切换主题即时预览(取消或关闭时还原为已保存值)。</summary>
    private void OnSkinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSkin.SelectedItem is string skin) Skins.Publish(skin);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择输出目录",
            Multiselect = false,
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(TxtOutputDir.Text) && Directory.Exists(TxtOutputDir.Text))
                dlg.InitialDirectory = TxtOutputDir.Text;
        }
        catch
        {
            // 目录不可访问就用系统默认起始位置
        }

        if (dlg.ShowDialog(this) is true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            TxtOutputDir.Text = dlg.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        int hotkeyRecord = CmbHotkeyRecord.SelectedValue is int hkRecord ? hkRecord : 0;
        int hotkeyPause = CmbHotkeyPause.SelectedValue is int hkPause ? hkPause : 0;
        int hotkeySnapshot = CmbHotkeySnapshot.SelectedValue is int hkSnapshot ? hkSnapshot : 0;

        // 三个键不能撞:撞了的话后注册的会被前一个顶掉,用户只会觉得"改了没反应"
        var assigned = new List<int>();
        foreach (var vk in new[] { hotkeyRecord, hotkeyPause, hotkeySnapshot })
        {
            if (vk == 0) continue;
            if (assigned.Contains(vk))
            {
                MessageBox.Show(this,
                    $"{RecordSettings.HotkeyName(vk)} 同时分配给了两个功能,请换一个。",
                    "ScreenRec", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            assigned.Add(vk);
        }

        _settings.Skin = CmbSkin.SelectedItem as string ?? Skins.Order[0];
        _settings.FrameRate = (int?)CmbFrameRate.SelectedItem ?? 30;
        _settings.Quality = CmbQuality.SelectedValue is QualityLevel q ? q : QualityLevel.High;
        _settings.Encoder = CmbEncoder.SelectedValue is EncoderChoice ec ? ec : EncoderChoice.Auto;
        _settings.AudioMode = CmbAudioMode.SelectedValue is AudioMode am ? am : AudioMode.Both;
        _settings.SystemVolume = (float)SldSystemVol.Value;
        _settings.MicVolume = (float)SldMicVol.Value;
        _settings.ShowMousePointer = ChkPointer.IsChecked is true;
        _settings.MouseEffect.Enabled = ChkMouse.IsChecked is true;
        if (!string.IsNullOrWhiteSpace(PalLeftColor.SelectedColor)) _settings.MouseEffect.LeftColor = PalLeftColor.SelectedColor;
        if (!string.IsNullOrWhiteSpace(PalRightColor.SelectedColor)) _settings.MouseEffect.RightColor = PalRightColor.SelectedColor;
        if (!string.IsNullOrWhiteSpace(PalHighlight.SelectedColor)) _settings.MouseEffect.HighlightColor = PalHighlight.SelectedColor;
        _settings.MouseEffect.RadiusPx = (int)SldRadius.Value;
        _settings.MouseEffect.DurationMs = (int)SldDuration.Value;
        _settings.MouseEffect.HighlightEnabled = ChkHighlight.IsChecked is true;
        _settings.MouseEffect.HighlightDiameter = (int)SldHighlight.Value;
        _settings.MouseEffect.HighlightOpacity = Math.Clamp(SldOpacity.Value / 100.0, 0.1, 1.0);
        _settings.HotkeyToggleRecord = hotkeyRecord;
        _settings.HotkeyTogglePause = hotkeyPause;
        _settings.HotkeySnapshot = hotkeySnapshot;
        _settings.SegmentSeconds = CmbSegment.SelectedValue is int seg ? Math.Max(0, seg) : 300;
        _settings.OutputDirectory = string.IsNullOrWhiteSpace(TxtOutputDir.Text)
            ? RecordSettings.DefaultOutputDirectory()
            : TxtOutputDir.Text.Trim();

        // 模板里没有时间占位符的话,每次录制都生成同一个文件名,会静默覆盖上一场,必须拦下
        string template = TxtFileNameTpl.Text?.Trim() ?? string.Empty;
        if (template.Length == 0)
        {
            template = "{yyyyMMdd_HHmmss}";
        }
        else if (!template.Contains("{yyyyMMdd_HHmmss}"))
        {
            MessageBox.Show(this, "文件名模板必须包含占位符 {yyyyMMdd_HHmmss},否则每次录制会互相覆盖。",
                "ScreenRec", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.FileNameTemplate = template;

        SettingsStore.Save(_settings);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Skins.Publish(_settings.Skin); // 取消:还原主题
        Close();
    }
}
