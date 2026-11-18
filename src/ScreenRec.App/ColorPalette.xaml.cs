using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenRec.App;

/// <summary>
/// 色盘:一行「色块 + 名字」的按钮,点开是 28 色网格。
///
/// 鼠标特效三种颜色(指针高亮、左键、右键)共用这个控件。用色盘而不是下拉框,
/// 是因为下拉里只能看到颜色名字,选之前根本不知道那个蓝到底多深。
/// </summary>
public partial class ColorPalette : UserControl
{
    /// <summary>色板:鲜艳 → 深色 → 浅色 → 中性,四排刚好铺满 216px。</summary>
    private static readonly (string Hex, string Name)[] Palette =
    {
        ("#E5484D", "红"), ("#FF7A45", "橙"), ("#F5C518", "黄"), ("#46C46F", "绿"),
        ("#22C3C9", "青"), ("#4C8DFF", "蓝"), ("#9B6BFF", "紫"), ("#FF6BC1", "粉"),
        ("#B3262B", "深红"), ("#C25E1F", "深橙"), ("#B58A00", "深黄"), ("#2E8B4F", "深绿"),
        ("#1B8F96", "深青"), ("#2F5FD0", "深蓝"), ("#6B3FBF", "深紫"), ("#C23A87", "深粉"),
        ("#FFB3B5", "浅红"), ("#FFC79A", "浅橙"), ("#FFF08A", "浅黄"), ("#A8E6B8", "浅绿"),
        ("#A0E7EA", "浅青"), ("#A9C7FF", "浅蓝"), ("#CDB4FF", "浅紫"), ("#FFB8E0", "浅粉"),
        ("#FFFFFF", "白"), ("#C9CED6", "浅灰"), ("#6B7280", "灰"), ("#1F2328", "黑"),
    };

    private readonly Dictionary<Border, string> _chips = new();

    public ColorPalette()
    {
        InitializeComponent();
        BuildSwatches();
        UpdateLabel();
    }

    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(string), typeof(ColorPalette),
        new FrameworkPropertyMetadata("#4C8DFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) =>
            {
                var palette = (ColorPalette)d;
                palette.UpdateLabel();
                palette.ColorChanged?.Invoke(palette.SelectedColor);
            }));

    /// <summary>选中的颜色变化(含代码里直接赋值),设置页靠它刷新 1:1 预览。</summary>
    public event Action<string>? ColorChanged;

    /// <summary>选中的颜色,#RRGGBB。</summary>
    public string SelectedColor
    {
        get => (string)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    private void BuildSwatches()
    {
        foreach (var (hex, name) in Palette)
        {
            var chip = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{name} {hex}",
                Tag = hex,
            };
            chip.MouseLeftButtonDown += (_, e) =>
            {
                SelectedColor = (string)chip.Tag;
                Pop.IsOpen = false;
                e.Handled = true;
            };
            _chips[chip] = hex;
            Swatches.Children.Add(chip);
        }
    }

    private void OnToggleClick(object sender, RoutedEventArgs e) => Pop.IsOpen = !Pop.IsOpen;

    private void UpdateLabel()
    {
        string hex = string.IsNullOrWhiteSpace(SelectedColor) ? "#4C8DFF" : SelectedColor.Trim();
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(hex); }
        catch { color = Color.FromRgb(0x4C, 0x8D, 0xFF); hex = "#4C8DFF"; }

        Chip.Background = new SolidColorBrush(color);
        string name = Palette.FirstOrDefault(p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase)).Name;
        Label.Text = string.IsNullOrEmpty(name) ? hex : $"{name}  {hex}";

        // 选中项描边加粗,一眼能看出当前用的是哪一格
        Brush accent = TryFindResource("Skin.Accent") as Brush
                       ?? new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x85));
        foreach (var (chip, chipHex) in _chips)
        {
            bool selected = string.Equals(chipHex, hex, StringComparison.OrdinalIgnoreCase);
            chip.BorderThickness = new Thickness(selected ? 2.5 : 1);
            chip.BorderBrush = selected
                ? accent
                : new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
        }
    }
}
