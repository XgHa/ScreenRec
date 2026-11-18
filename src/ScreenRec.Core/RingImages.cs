using System.IO.Compression;

namespace ScreenRec.Core;

/// <summary>
/// 鼠标特效的位图生成:指针高亮的实心圆、点击的单环/双环。
///
/// 走纯托管 PNG 编码:Core 不引 System.Drawing、也不引 WPF,agent 进程零额外依赖。
/// 生成的字节直接喂给 ScreenRecorderLib 的 ImageOverlay.SourceStream ——
/// 叠加层由内核合进编码器输入,录制时屏幕上什么都不画。
///
/// 全是静态图:只出现、停留、消失。逐帧动画在这套 API 上会拖垮编码器(实测)。
/// </summary>
public static class RingImages
{
    /// <summary>解析 #RGB / #RRGGBB / #AARRGGBB。</summary>
    public static uint ParseArgb(string? hex, uint fallback = 0xFF4C8DFF)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8) return fallback;
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out uint v)
            ? v
            : fallback;
    }

    /// <summary>给颜色套一个透明度(0~1)。</summary>
    public static uint WithOpacity(uint argb, double opacity)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(((argb >> 24) & 0xFF) * Math.Clamp(opacity, 0, 1)), 0, 255);
        return (argb & 0x00FFFFFFu) | ((uint)alpha << 24);
    }

    /// <summary>把 color 朝 with 混合 t(0~1),用来生成同色系的浅色外环。</summary>
    public static uint Mix(uint color, uint with, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte a = (byte)((color >> 24) & 0xFF);
        byte r = (byte)(((color >> 16) & 0xFF) * (1 - t) + ((with >> 16) & 0xFF) * t);
        byte g = (byte)(((color >> 8) & 0xFF) * (1 - t) + ((with >> 8) & 0xFF) * t);
        byte b = (byte)((color & 0xFF) * (1 - t) + ((with & 0xFF) * t));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>指针高亮:一个实心圆,透明度由调用方决定。</summary>
    public static byte[] Disc(int diameter, uint argb)
        => Render(diameter, filled: true,
            new[] { (Radius: diameter / 2.0 - 0.5, Stroke: diameter / 2.0, Argb: argb) });

    /// <summary>单击:一圈环。</summary>
    public static byte[] Circle(int canvas, double radius, double stroke, uint argb)
        => Render(canvas, filled: false, new[] { (Radius: radius, Stroke: stroke, Argb: argb) });

    /// <summary>同位置双击:在原来那一圈外面再套一圈(外圈浅一点、细一点)。</summary>
    public static byte[] DoubleCircle(int canvas, double radius, double stroke, uint argb,
                                      double outerRadius, double outerStroke, uint outerArgb)
        => Render(canvas, filled: false, new[]
        {
            (Radius: outerRadius, Stroke: outerStroke, Argb: outerArgb),
            (Radius: radius, Stroke: stroke, Argb: argb),
        });

    /// <summary>把一组同心圆(或实心圆)画进 RGBA 缓冲并编码成 PNG。半径单位是像素。</summary>
    private static byte[] Render(int canvas, bool filled, (double Radius, double Stroke, uint Argb)[] circles)
    {
        int size = Math.Clamp(canvas, 8, 640);
        var pixels = new byte[size * size * 4];
        double center = size / 2.0 - 0.5;

        for (int y = 0; y < size; y++)
        {
            double dy = y - center;
            for (int x = 0; x < size; x++)
            {
                double dx = x - center;
                double distance = Math.Sqrt(dx * dx + dy * dy);

                double coverage;
                uint color;

                if (filled)
                {
                    var (radius, _, argb) = circles[0];
                    // 实心圆只按外缘做 1px 过渡
                    double edge = distance - radius;
                    coverage = edge <= -0.5 ? 1 : edge >= 0.5 ? 0 : 0.5 - edge;
                    color = argb;
                }
                else
                {
                    coverage = 0;
                    color = 0;
                    foreach (var (radius, stroke, argb) in circles)
                    {
                        double half = stroke / 2.0;
                        // 到环带的距离:环带内为负,外部为正;0.5px 过渡做抗锯齿
                        double edge = Math.Abs(distance - radius) - half;
                        double a = edge <= -0.5 ? 1 : edge >= 0.5 ? 0 : 0.5 - edge;
                        if (a > coverage) { coverage = a; color = argb; }
                    }
                }

                if (coverage <= 0) continue;

                int index = (y * size + x) * 4;
                pixels[index + 0] = (byte)(color >> 16);
                pixels[index + 1] = (byte)(color >> 8);
                pixels[index + 2] = (byte)color;
                pixels[index + 3] = (byte)Math.Round(((color >> 24) & 0xFF) * coverage);
            }
        }

        return EncodePng(size, size, pixels);
    }

    /// <summary>最小 PNG 编码器:8 位 RGBA、无滤波、单个 IDAT。</summary>
    private static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // color type: RGBA
        header[10] = 0;  // deflate
        header[11] = 0;  // adaptive filtering
        header[12] = 0;  // no interlace
        WriteChunk(output, "IHDR", header);

        int stride = width * 4;
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, y * stride, stride);
        }

        WriteChunk(output, "IDAT", ZlibCompress(raw.ToArray()));
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x9C);
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);

        uint adler = Adler32(data);
        compressed.WriteByte((byte)(adler >> 24));
        compressed.WriteByte((byte)(adler >> 16));
        compressed.WriteByte((byte)(adler >> 8));
        compressed.WriteByte((byte)adler);
        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);

        var typeBytes = new byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        output.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset + 0] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] a, byte[] b)
    {
        var table = _crcTable ??= BuildCrcTable();
        uint crc = 0xFFFFFFFF;
        foreach (byte value in a) crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (byte value in b) crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Adler32(byte[] data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % Mod;
            b = (b + a) % Mod;
        }
        return (b << 16) | a;
    }
}
