using System.Windows;
using System.Windows.Media;

namespace ScreenRec.App;

/// <summary>一套皮肤的 4 个核心色:背景 / 面板 / 元素主色 / 文字。</summary>
public class SkinDef
{
    public string Bg;
    public string Panel;
    public string Accent;
    public string Text;

    public SkinDef(string bg, string panel, string accent, string text)
    {
        Bg = bg;
        Panel = panel;
        Accent = accent;
        Text = text;
    }
}

/// <summary>
/// 皮肤系统:14 套原神主题,辅助色由核心色自动派生。
/// 颜色发布到 Application.Resources,界面用 DynamicResource 引用,切换即全局刷新。
/// </summary>
public static class Skins
{
    public static readonly List<string> Order = new();
    public static readonly Dictionary<string, SkinDef> All = new();
    public static string Current = "蒙德";

    public static Color Bg, Card, CardHover, Border, Text, Muted, Accent, OnAccent, Rec;

    static Skins()
    {
        All["蒙德"] = new SkinDef("#F5FBF9", "#E9F4F0", "#2E9E85", "#1F3A33");
        All["璃月"] = new SkinDef("#FBF6EA", "#F2E8D2", "#C9972F", "#40331D");
        All["稻妻"] = new SkinDef("#F7F4FB", "#EAE4F5", "#7E5FD6", "#322A47");
        All["须弥"] = new SkinDef("#F2F9EE", "#E2F0DA", "#4E9A3D", "#263A1F");
        All["枫丹"] = new SkinDef("#F1F6FB", "#E0EBF6", "#2B6FB8", "#1E3040");
        All["纳塔"] = new SkinDef("#FBF4F0", "#F3E3DA", "#D4552A", "#40271F");
        All["至冬"] = new SkinDef("#F7FAFD", "#EBF2F9", "#8FB9DE", "#21313F");
        All["龙脊雪山"] = new SkinDef("#F4FAFC", "#E6F1F5", "#4FA8B8", "#1E343B");
        All["层岩巨渊"] = new SkinDef("#23241F", "#2E2F28", "#C9A227", "#EAE6D8");
        All["沉玉谷"] = new SkinDef("#F2F6E8", "#E2EBD2", "#8FA63C", "#29341F");
        All["渊下宫"] = new SkinDef("#1F2430", "#293041", "#AEC6FF", "#DDE3F0");
        All["千壑沙地"] = new SkinDef("#FBF4E4", "#F0E5CC", "#A99B2F", "#3A311D");
        All["旧日之海"] = new SkinDef("#142331", "#1D3042", "#3FB0C8", "#D8E6EE");
        All["远古圣山"] = new SkinDef("#261C18", "#33251F", "#E07830", "#F0E6DC");

        Order.AddRange(new[]
        {
            "蒙德", "璃月", "稻妻", "须弥", "枫丹", "纳塔", "至冬", "龙脊雪山",
            "层岩巨渊", "沉玉谷", "渊下宫", "千壑沙地", "旧日之海", "远古圣山",
        });

        Apply("蒙德");
    }

    /// <summary>计算指定皮肤的全部颜色。</summary>
    public static void Apply(string name)
    {
        if (!All.TryGetValue(name, out var s)) { name = "蒙德"; s = All["蒙德"]; }
        Current = name;

        var bg = Hex(s.Bg);
        var panel = Hex(s.Panel);
        var accent = Hex(s.Accent);
        bool dark = Lum(bg) < 0.5;

        // 正文往背景的反方向再推一档:浅色皮肤更黑、深色皮肤更白。
        // 小字号(11~12.5px)在原色上偏灰,尤其深色皮肤的低饱和正文,不推这一档看不清。
        var text = dark
            ? Mix(Hex(s.Text), Colors.White, 0.10)
            : Mix(Hex(s.Text), Colors.Black, 0.12);

        Bg = bg;
        Card = panel;
        Border = Mix(panel, text, 0.26);
        Text = text;

        // 次要文字原来掺 58% 背景色,几乎糊在底上;收到 42%,层次还在但能分辨。
        Muted = Mix(text, bg, 0.42);

        Accent = accent;
        OnAccent = Lum(accent) > 0.55 ? Mix(accent, bg, 0.85) : Colors.White;
        CardHover = Mix(panel, accent, 0.13);
        Rec = accent; // REC 按钮与录制指示点跟随主题主色
    }

    /// <summary>把当前皮肤颜色发布到应用资源字典(键与 Theme.xaml 的 fallback 同名,发布后覆盖)。</summary>
    public static void Publish(string skinName)
    {
        Apply(skinName);
        var r = Application.Current.Resources;
        r["Skin.Bg"] = Frozen(Bg);
        r["Skin.Card"] = Frozen(Card);
        r["Skin.CardHover"] = Frozen(CardHover);
        r["Skin.Border"] = Frozen(Border);
        r["Skin.Text"] = Frozen(Text);
        r["Skin.Muted"] = Frozen(Muted);
        r["Skin.Accent"] = Frozen(Accent);
        r["Skin.OnAccent"] = Frozen(OnAccent);
        r["Skin.Rec"] = Frozen(Rec);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Color Hex(string s)
    {
        s = s.TrimStart('#');
        return Color.FromRgb(
            Convert.ToByte(s.Substring(0, 2), 16),
            Convert.ToByte(s.Substring(2, 2), 16),
            Convert.ToByte(s.Substring(4, 2), 16));
    }

    public static Color Mix(Color a, Color b, double t)
    {
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static double Lum(Color c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}
