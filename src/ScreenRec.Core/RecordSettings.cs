using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenRec.Core;

/// <summary>
/// 全局 JSON 配置:允许 NaN/Infinity 字面量。
/// RecordSettings.MainPanelLeft/Top 默认值是 double.NaN(未保存过位置),
/// 用默认选项序列化会直接抛 ArgumentException,UI 发给 agent 的 start 命令会静默失败。
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}

public enum QualityLevel { Low, Medium, High, Ultra }

public enum AudioMode { SystemOnly, MicOnly, Both, Muted }

/// <summary>录制源:整屏(主显示器)或单个窗口。</summary>
public enum CaptureTarget { FullScreen, Window, Region }

public sealed class MouseEffectSettings
{
    public bool Enabled { get; set; } = true;
    public string LeftColor { get; set; } = "#4C8DFF";
    public string RightColor { get; set; } = "#E5484D";

    /// <summary>
    /// 点击圆点半径(px),直接对应内核的 MouseClickDetectionRadius。
    /// 旧版本存的是 MinRadius/MaxRadius 两个值、内核取中值,滑条数字对不上实际效果,已废弃。
    /// </summary>
    public int RadiusPx { get; set; } = 40;

    public int DurationMs { get; set; } = 600;

    /// <summary>双环描边宽度:内核只画实心圆点,该值留作 M3 自研合成使用。</summary>
    public int RingWidthPx { get; set; } = 4;

    /// <summary>指针高亮:给鼠标指针套一圈空心环,同样只在成片里出现。</summary>
    public bool HighlightEnabled { get; set; } = true;

    /// <summary>高亮环直径(px),成片分辨率下的实际像素,不随 DPI 缩放。</summary>
    public int HighlightDiameter { get; set; } = 56;

    public string HighlightColor { get; set; } = "#4C8DFF";

    /// <summary>高亮圆的透明度(0.1~1.0)。实心圆太实会盖住指针下的内容,默认给 35%。</summary>
    public double HighlightOpacity { get; set; } = 0.35;
}

public sealed class RecordSettings
{
    public string Skin { get; set; } = "蒙德";
    public int FrameRate { get; set; } = 30;
    public QualityLevel Quality { get; set; } = QualityLevel.High;
    public EncoderChoice Encoder { get; set; } = EncoderChoice.Auto;
    public AudioMode AudioMode { get; set; } = AudioMode.Both;

    /// <summary>录制源。Window 模式下 WindowHandle 必须有效,否则启动会失败。</summary>
    public CaptureTarget Target { get; set; } = CaptureTarget.FullScreen;

    /// <summary>
    /// 录制哪台显示器(GDI 设备名,如 \\.\DISPLAY1)。为空或无效时退回主屏。
    /// 选区模式下由选区所在屏自动决定。
    /// </summary>
    public string? MonitorDeviceName { get; set; }

    public long WindowHandle { get; set; }
    public string? WindowTitle { get; set; }

    /// <summary>Region 模式的裁剪矩形,单位是物理像素,坐标相对 MonitorDeviceName 那台显示器。</summary>
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }
    public float SystemVolume { get; set; } = 1f;
    public float MicVolume { get; set; } = 1f;
    public string? MicDeviceName { get; set; }

    /// <summary>是否把鼠标指针录进成片(与点击波纹相互独立)。</summary>
    public bool ShowMousePointer { get; set; } = true;

    /// <summary>
    /// 全局快捷键,存虚拟键码,0 = 禁用。
    /// 只开放 F1~F12:全局热键抢到字母/数字键会让用户在别处打不出字,而且很难联想到是谁抢的。
    /// </summary>
    public int HotkeyToggleRecord { get; set; } = 0x78; // F9

    public int HotkeyTogglePause { get; set; } = 0x79;  // F10

    public int HotkeySnapshot { get; set; } = 0x7A;     // F11

    /// <summary>VK → "F9";不在 F1..F12 范围内返回 null(视为禁用)。</summary>
    public static string? HotkeyName(int virtualKey)
        => virtualKey is >= 0x70 and <= 0x7B ? $"F{virtualKey - 0x70 + 1}" : null;

    /// <summary>F1..F12 的虚拟键码是连续的 0x70..0x7B。</summary>
    public static int HotkeyVirtualKey(int functionKeyNumber)
        => 0x70 + Math.Clamp(functionKeyNumber, 1, 12) - 1;

    /// <summary>配置里混进了非法值(手改过 settings.json)时按禁用处理,别让下拉框空白。</summary>
    public static int NormalizeHotkey(int virtualKey)
        => virtualKey is >= 0x70 and <= 0x7B ? virtualKey : 0;

    /// <summary>
    /// 定时切段秒数:0 = 关闭,默认 5 分钟。到点结束当前段落盘、静默新开一段,
    /// 崩溃最多丢最后一段。切段瞬间内核 stop/start 约损失数百毫秒画面。
    /// </summary>
    public int SegmentSeconds { get; set; } = 300;

    public MouseEffectSettings MouseEffect { get; set; } = new();
    public string OutputDirectory { get; set; } = DefaultOutputDirectory();
    public string FileNameTemplate { get; set; } = "{yyyyMMdd_HHmmss}";
    public double MainPanelLeft { get; set; } = double.NaN;
    public double MainPanelTop { get; set; } = double.NaN;
    public double FloatingBarLeft { get; set; } = double.NaN;
    public double FloatingBarTop { get; set; } = double.NaN;

    public static string DefaultOutputDirectory()
    {
        try
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (!string.IsNullOrEmpty(videos)) return Path.Combine(videos, "ScreenRec");
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ScreenRec");
    }

    /// <summary>
    /// 画质档位映射码率:1080p 基准,按像素量缩放。
    /// 分辨率固定为主屏原生(录屏不做降采样),档位只决定码率,
    /// 桌面文字/代码类内容明显比视频更吃码率,所以基准给得偏高。
    /// </summary>
    public long VideoBitrate(int width, int height) => BitrateFor(Quality, width, height);

    public static long BitrateFor(QualityLevel quality, int width, int height)
    {
        double scale = (double)(width * height) / (1920.0 * 1080.0);
        double baseMbps = quality switch
        {
            QualityLevel.Low => 8,
            QualityLevel.Medium => 16,
            QualityLevel.High => 24,
            _ => 40,
        };
        long bps = (long)(baseMbps * scale * 1_000_000);
        return Math.Clamp(bps, 2_000_000, 120_000_000);
    }

    /// <summary>档位的显示文案,如 "高 · 1080P · 24 Mbps"。</summary>
    public static string QualityLabel(QualityLevel quality, string qualityName, int width, int height)
    {
        long bps = BitrateFor(quality, width, height);
        return $"{qualityName} · {ScreenInfo.ResolutionLabel} · {bps / 1_000_000.0:0.#} Mbps";
    }

    public string BuildFileName(string extension)
    {
        var name = FileNameTemplate.Replace("{yyyyMMdd_HHmmss}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name + extension;
    }
}

/// <summary>设置持久化:%APPDATA%\ScreenRec\settings.json。</summary>
public static class SettingsStore
{
    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRec");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static RecordSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<RecordSettings>(
                           File.ReadAllText(FilePath), JsonDefaults.Options)
                       ?? new RecordSettings();
        }
        catch { }
        return new RecordSettings();
    }

    public static void Save(RecordSettings settings)
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }));
        }
        catch { }
    }
}
