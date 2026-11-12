# -*- coding: utf-8 -*-
"""Pasteup 应用图标生成：icon/pasteup-icon.png -> src/Pasteup.App/Assets/app.ico

处理逻辑（Windows 图标规范，Win32 非打包应用）：
1. 源图为 1000x1000 RGBA、四角透明、深色圆角底板占画布 79%（alpha bbox 104~896）；
   按通行做法把底板放大到画布 90%，任务栏/托盘观感与其他应用一致。
2. 生成 16/24/32/48/64/128/256 七档；LANCZOS 缩放，16/24 用单独绘制的简化帧
   （底板 + 双纸粗轮廓；母版直缩在此两档会糊成一团）。
3. 手写 ICO 容器：16~48 存 32bpp BGRA BMP 帧（含全零 AND 掩码），64+ 存 PNG 帧
   （PNG 压缩帧在 256px 是官方保证，小尺寸用 BMP 帧规避旧组件兼容疑虑）。
4. 另出 128px PNG 到 src/Pasteup.App/Assets/app-icon.png（WPF Resource，唤起面板 logo 用）。
5. 预览拼图（浅/深底各一排）输出到 docs/images/关闭最小化与应用图标/。

用法：~/Miniconda3/python.exe scripts/make_icon.py
"""

from __future__ import annotations

import io
import struct
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "icon" / "pasteup-icon.png"
ICO_OUT = ROOT / "src" / "Pasteup.App" / "Assets" / "app.ico"
PNG_OUT = ROOT / "src" / "Pasteup.App" / "Assets" / "app-icon.png"
PNG_SIZE = 128          # 面板 logo 显示 26 DIP，128px 覆盖 200%+ DPI
PREVIEW_DIR = ROOT / "docs" / "images" / "关闭最小化与应用图标"

CANVAS = 1024          # 中间画布尺寸（源图 1000 直接放大留整数网格）
PLATE_FILL = 0.90      # 底板占画布比例
SIZES = [16, 24, 32, 48, 64, 128, 256]
BMP_MAX = 48           # <= 该尺寸存 BMP 帧，更大存 PNG 帧
SIMPLE_MAX = 24        # <= 该尺寸用单独绘制的简化帧（母版直缩会糊成一团）

# 从母版采样（2026-11-01）：底板深灰、纸叠米色系、圆角半径/底板边长 ≈ 256/920
PLATE_RGB = (52, 51, 47)
PAPER_BACK_RGB = (226, 213, 192)
PAPER_FRONT_RGB = (243, 239, 230)
RADIUS_RATIO = 256 / 920


def simplified_frame(size: int) -> Image.Image:
    """16/24 专用简化帧：底板 + 前后两张纸的粗轮廓（512 超采样后缩小）。
    小尺寸下旋转只会带来抗锯齿柔边，纸块保持轴对称；靠明度差与对角错位表现「叠」。"""
    from PIL import ImageDraw
    ss = 512
    plate_side = round(ss * PLATE_FILL)
    r = round(plate_side * RADIUS_RATIO)
    off = (ss - plate_side) // 2
    img = Image.new("RGBA", (ss, ss), (0, 0, 0, 0))
    ImageDraw.Draw(img).rounded_rectangle(
        [off, off, off + plate_side, off + plate_side], radius=r, fill=PLATE_RGB + (255,))

    def paper(side_ratio: float, dx: float, dy: float, fill: tuple[int, int, int]) -> None:
        nonlocal img
        layer = Image.new("RGBA", (ss, ss), (0, 0, 0, 0))
        pd = ImageDraw.Draw(layer)
        side = round(ss * side_ratio)
        px = (ss - side) // 2 + round(ss * dx)
        py = (ss - side) // 2 + round(ss * dy)
        pd.rounded_rectangle([px, py, px + side, py + side],
                             radius=round(side * 0.06), fill=fill + (255,))
        img = Image.alpha_composite(img, layer)

    paper(0.62, -0.045, 0.045, PAPER_BACK_RGB)
    paper(0.56, 0.045, -0.045, PAPER_FRONT_RGB)
    return img.resize((size, size), Image.LANCZOS)


def build_master() -> Image.Image:
    """源图裁到 alpha bbox，等比放大到画布 90%，居中贴到透明画布。"""
    src = Image.open(SRC).convert("RGBA")
    bbox = src.getchannel("A").getbbox()
    if bbox is None:
        raise SystemExit("源图全透明？")
    plate = src.crop(bbox)
    side = max(plate.size)
    # 以短边为准等比缩放（源图近似正方形）
    target = round(CANVAS * PLATE_FILL)
    scaled = plate.resize((round(plate.width * target / side), round(plate.height * target / side)),
                          Image.LANCZOS)
    canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    canvas.paste(scaled, ((CANVAS - scaled.width) // 2, (CANVAS - scaled.height) // 2), scaled)
    return canvas


def bmp_frame(img: Image.Image) -> bytes:
    """32bpp BGRA 自底向上 BITMAPINFOHEADER + 全零 AND 掩码。"""
    s = img.width
    px = img.load()
    rows = bytearray()
    for y in range(s - 1, -1, -1):          # 自底向上
        for x in range(s):
            r, g, b, a = px[x, y]
            rows += bytes((b, g, r, a))
    mask_row = ((s + 31) // 32) * 4         # AND 掩码行，DWORD 对齐，全零（alpha 已表达透明度）
    mask = bytes(mask_row * s)
    header = struct.pack("<IiiHHIIiiII", 40, s, s * 2, 1, 32, 0, len(rows) + len(mask), 0, 0, 0, 0)
    return header + bytes(rows) + mask


def png_frame(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(frames: list[tuple[int, bytes]], path: Path) -> None:
    """frames: [(size, blob), ...] 小尺寸在前。"""
    count = len(frames)
    offset = 6 + 16 * count
    entries = bytearray(struct.pack("<HHH", 0, 1, count))
    blobs = bytearray()
    for size, blob in frames:
        dim = 0 if size >= 256 else size    # 256 记 0
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        blobs += blob
        offset += len(blob)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(bytes(entries + blobs))


def build_preview(master: Image.Image, frames: dict[int, Image.Image]) -> None:
    PREVIEW_DIR.mkdir(parents=True, exist_ok=True)
    show = [16, 24, 32, 48, 64, 128, 256]
    pad = 28
    for name, bg in (("light", (245, 245, 243)), ("dark", (38, 38, 40))):
        label = (110, 110, 110) if name == "light" else (185, 185, 185)
        height = 256 + pad * 2 + 40
        width = pad + sum(max(frames[s].width, 64) + pad for s in show)
        band = Image.new("RGB", (width, height), bg)
        from PIL import ImageDraw
        draw = ImageDraw.Draw(band)
        x = pad
        for s in show:
            f = frames[s]
            # 1:1 像素贴入，底对齐；小尺寸旁留空展示真实观感
            band.paste(f, (x, height - pad - f.height), f)
            draw.text((x, pad), f"{s}px", fill=label)
            x += max(f.width, 64) + pad
        band.save(PREVIEW_DIR / f"preview-{name}.png")
    # 小尺寸放大检视图：16/24 x4、32/48 x2（近邻放大，检视像素本身）
    zoom = [(16, 4), (24, 4), (32, 2), (48, 2)]
    zp, zb = 28, 28
    zwidth = zp + sum(frames[s].width * k + zb for s, k in zoom)
    zheight = 256 * 2 + zp * 2
    zimg = Image.new("RGB", (zwidth, zheight), (245, 245, 243))
    from PIL import ImageDraw
    zd = ImageDraw.Draw(zimg)
    x = zp
    for s, k in zoom:
        big = frames[s].resize((frames[s].width * k, frames[s].width * k), Image.NEAREST)
        zimg.paste(big, (x, zheight - zp - big.height), big)
        zd.text((x, zp), f"{s}px x{k}", fill=(110, 110, 110))
        x += big.width + zb
    zimg.save(PREVIEW_DIR / "preview-zoom.png")
    master.save(PREVIEW_DIR / "master-1024.png")


def main() -> None:
    master = build_master()
    frames_img: dict[int, Image.Image] = {
        s: simplified_frame(s) if s <= SIMPLE_MAX else master.resize((s, s), Image.LANCZOS)
        for s in SIZES
    }
    frames: list[tuple[int, bytes]] = [
        (s, bmp_frame(frames_img[s]) if s <= BMP_MAX else png_frame(frames_img[s]))
        for s in SIZES
    ]
    write_ico(frames, ICO_OUT)
    PNG_OUT.parent.mkdir(parents=True, exist_ok=True)
    master.resize((PNG_SIZE, PNG_SIZE), Image.LANCZOS).save(PNG_OUT, optimize=True)
    build_preview(master, frames_img)
    print("OK", ICO_OUT.relative_to(ROOT), ICO_OUT.stat().st_size, "bytes")
    print("OK", PNG_OUT.relative_to(ROOT), PNG_OUT.stat().st_size, "bytes")
    for s, blob in frames:
        print(f"  {s:>3}px {'BMP' if s <= BMP_MAX else 'PNG'} {len(blob):>7} bytes")


if __name__ == "__main__":
    sys.exit(main())
