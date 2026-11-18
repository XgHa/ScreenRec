"""生成 ScreenRec 的应用图标 (app.ico)。

风格参照腾讯会议:蓝色圆角方块 + 硬边白色摄像机。
- 背景用 45° 对角渐变,并在对角线上压一道明暗台阶,做出折面的棱角感;
- 摄像机是直角机身 + 尖锐梯形镜头,不用圆角,整体水平居中。

改配色/形状后重跑:
    python tools/make-icon.py
"""

import struct
from pathlib import Path

from PIL import Image, ImageDraw

S = 1024                 # 超采样尺寸
RADIUS = int(S * 0.222)  # 方块圆角
BRIGHT = (0x3D, 0x8B, 0xFF)   # 左上亮蓝
DARK = (0x08, 0x3B, 0xC4)     # 右下深蓝

# 机身:横向矩形,圆角极小
BODY = (0.205, 0.350, 0.568, 0.650)
BODY_RADIUS = int(S * 0.008)

# 镜头:梯形,四角尖锐,右侧比左侧高
LENS = [(0.582, 0.415), (0.795, 0.330), (0.795, 0.670), (0.582, 0.585)]

SIZES = [256, 128, 64, 48, 32, 16]


def px(v: float) -> int:
    return int(S * v)


def diagonal_gradient() -> Image.Image:
    """左上 → 右下的对角渐变。

    不用 Image.linear_gradient:它在本机 Pillow 上旋转后取样区域全黑,
    这里用对角线族直接画,方向可控。
    """
    scale = 512
    g = Image.new("L", (scale, scale))
    d = ImageDraw.Draw(g)
    n = scale * 2
    for c in range(n):
        v = int(255 * c / (n - 1))
        if c <= scale:
            d.line([(c, 0), (0, c)], fill=v)
        else:
            d.line([(scale, c - scale), (c - scale, scale)], fill=v)
    return g.resize((S, S), Image.BICUBIC)


def render() -> Image.Image:
    img = Image.composite(
        Image.new("RGBA", (S, S), DARK + (255,)),
        Image.new("RGBA", (S, S), BRIGHT + (255,)),
        diagonal_gradient(),
    )

    # 对角线台阶:左上三角提亮、右下三角压暗,形成一道硬边折面
    shade = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shade)
    sd.polygon([(0, 0), (S, 0), (0, S)], fill=(130, 185, 255, 32))
    sd.polygon([(S, 0), (S, S), (0, S)], fill=(0, 24, 96, 28))
    img = Image.alpha_composite(img, shade)

    # 圆角裁剪
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=RADIUS, fill=255)
    img.putalpha(mask)

    white = (255, 255, 255, 255)
    d = ImageDraw.Draw(img)

    d.rounded_rectangle(
        [px(BODY[0]), px(BODY[1]), px(BODY[2]), px(BODY[3])],
        radius=BODY_RADIUS, fill=white)

    d.polygon([(px(x), px(y)) for x, y in LENS], fill=white)

    return img


def bmp_entry(img: Image.Image, size: int) -> bytes:
    """一档图标的 BMP 数据(BGRA + 全 0 AND 掩码),32 位带 alpha。"""
    im = img.resize((size, size), Image.LANCZOS)
    px_bytes = im.tobytes("raw", "BGRA")
    stride = size * 4
    rows = [px_bytes[i * stride:(i + 1) * stride] for i in range(size)]
    rows.reverse()
    xor = b"".join(rows)
    and_stride = ((size + 31) // 32) * 4
    and_mask = bytes(and_stride * size)
    # BITMAPINFOHEADER:高度写两倍(XOR 图 + AND 掩码)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0,
                         len(xor) + len(and_mask), 0, 0, 0, 0)
    return header + xor + and_mask


def write_ico(path: Path, img: Image.Image) -> None:
    """手工拼 ICO。

    必须用 BMP 条目而不是 PNG 压缩条目:MSBuild 嵌 exe 图标时不吃 PNG 写法,
    表现是 exe 体积不变、图标始终是默认的。
    """
    entries = []
    payload = b""
    offset = 6 + 16 * len(SIZES)

    for size in SIZES:
        data = bmp_entry(img, size)
        dim = 0 if size >= 256 else size
        entries.append(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
        payload += data

    header = struct.pack("<HHH", 0, 1, len(SIZES))
    path.write_bytes(header + b"".join(entries) + payload)


if __name__ == "__main__":
    icon = render()
    out = Path(__file__).resolve().parent.parent / "src" / "ScreenRec.App" / "app.ico"
    write_ico(out, icon)
    print(f"written: {out} ({out.stat().st_size / 1024:.1f} KB)")

    preview = Path(__file__).parent / "_preview.png"
    icon.resize((256, 256), Image.LANCZOS).save(preview)
    print(f"preview: {preview}")
