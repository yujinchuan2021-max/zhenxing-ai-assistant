#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
枕星图吧AI助手 官网 · 素材生成脚本

做的事（全部离线、可重复执行）：
1. 把「应用实拍截图」从证据目录复制到 scripts/shot-sources/（保留来源，管线自包含）；
2. 裁掉窗口截图四周的黑边，套圆角蒙版，压缩为 WebP 输出到 src/assets/zxai/shots/；
3. 生成站点图标（icon-180/192/512）与社交分享图 og.png（品牌四角星为原创矢量，脚本内采样绘制）；
4. 生成 site.webmanifest 与 favicon.svg；
5. 打印产物清单（路径 + SHA-256）供报告引用。

依赖：Pillow。用法：
    python scripts/prepare-assets.py
"""

from __future__ import annotations

import hashlib
import json
import os
import struct
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                      # website-winui3/
SHOT_SOURCES = HERE / "shot-sources"
SHOTS_OUT = ROOT / "src" / "assets" / "zxai" / "shots"
PUBLIC_OUT = ROOT / "public" / "zxai"
# OG 图正文用的字体：官网默认正文字体「更纱黑体 Sarasa UI SC」的官方字重文件。
# 由 scripts/prepare-fonts.py 下载到 .font-cache/sarasa/（SIL OFL 1.1）。
# （旧版这里读桌面应用的 Maple Mono CN；站点正文字体换成更纱后不再依赖应用目录。）
FONT_DIR = ROOT / ".font-cache" / "sarasa"

# 站点品牌色（与 src/zxai/styles/zxai.css 的深色主题一致）
BG_DARK = (19, 18, 16)
ACCENT = (240, 167, 95)
TEXT_MAIN = (242, 238, 231)
TEXT_MUTED = (182, 175, 164)
TEXT_DIM = (143, 136, 124)

# --------------------------------------------------------------------------
# 截图来源：均为本项目应用（枕星图吧AI助手）在隔离数据根下的真实截图
# --------------------------------------------------------------------------
SOURCES = {
    "ai-home-dark": r"D:\AI2\枕星图吧AI助手\toolbox-merge-20260923-evidence\01-dark-ai-home.png",
    "ai-home-light": r"D:\AI2\枕星图吧AI助手\toolbox-merge-20260923-evidence\05-light-ai-via-launcher.png",
    "tools-dark": r"D:\AI2\枕星图吧AI助手\toolbox-merge-20260923-evidence\02-dark-harddisk-29.png",
    "tools-light": r"D:\AI2\枕星图吧AI助手\toolbox-merge-20260923-evidence\10-light-others-77.png",
    "hardware-dark": (
        r"D:\AI2\tubatools\TubaWinUi3.WinUI3\artifacts\font-unification-2026-09-23-r2"
        r"\evidence\r2-05-hardware.png"
    ),
}

# 普通窗口截图：去掉四周黑边（PrintWindow 抓到的窗口圆角/边框）
BORDER_BOX = (8, 1, 2039, 1104)
CORNER_RADIUS = 16

# 细节特写（AI 页建议行 + 输入区），坐标基于裁边后的图
DETAIL_BOX = (750, 415, 1690, 1096)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def rounded_mask(size: tuple[int, int], radius: int) -> Image.Image:
    mask = Image.new("L", size, 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle([0, 0, size[0] - 1, size[1] - 1], radius=radius, fill=255)
    return mask


def star_points(cx: float, cy: float, radius: float, samples: int = 96) -> list[tuple[float, float]]:
    """四角星（与站点 SVG 同一条路径）：四段三次贝塞尔，凹边外扩。"""
    r = radius
    anchors = [(0, -r), (r, 0), (0, r), (-r, 0)]
    # 每段：起点锚点 → 控制点1（沿起点方向外推）→ 控制点2 → 终点锚点
    # 与 SVG 路径 "M24 1.5 C24 13.4,13.4 24,1.5 24 ..." 等价（半径缩放到 r）
    segments = [
        ((0, -r), (0, -r * 0.48), (-r * 0.48, 0), (-r, 0)),
        ((-r, 0), (-r * 0.48, 0), (0, r * 0.48), (0, r)),
        ((0, r), (0, r * 0.48), (r * 0.48, 0), (r, 0)),
        ((r, 0), (r * 0.48, 0), (0, -r * 0.48), (0, -r)),
    ]
    points: list[tuple[float, float]] = []
    for p0, p1, p2, p3 in segments:
        for i in range(samples + 1):
            t = i / samples
            u = 1 - t
            x = (u ** 3) * p0[0] + 3 * (u ** 2) * t * p1[0] + 3 * u * (t ** 2) * p2[0] + (t ** 3) * p3[0]
            y = (u ** 3) * p0[1] + 3 * (u ** 2) * t * p1[1] + 3 * u * (t ** 2) * p2[1] + (t ** 3) * p3[1]
            points.append((cx + x, cy + y))
    return points


def draw_star(image: Image.Image, cx: float, cy: float, radius: float, color: tuple[int, int, int, int]) -> None:
    """在 RGBA 图上画一个抗锯齿的四角星（4 倍超采样后缩放）。"""
    scale = 4
    size = int(radius * 2.4)
    layer = Image.new("RGBA", (size * scale, size * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    points = star_points(size * scale / 2, size * scale / 2, radius * scale)
    draw.polygon(points, fill=color)
    layer = layer.resize((size, size), Image.LANCZOS)
    image.alpha_composite(layer, (int(cx - size / 2), int(cy - size / 2)))


def font(name: str, size: int) -> ImageFont.FreeTypeFont:
    path = FONT_DIR / name
    if not path.exists():
        raise SystemExit(
            f"找不到 OG 图字体：{path}\n请先运行 npm run fonts:prepare（或 "
            "uv run --with fonttools --with brotli --with py7zr python scripts/prepare-fonts.py）"
        )
    return ImageFont.truetype(str(path), size)


# --------------------------------------------------------------------------
# 1. 截图处理
# --------------------------------------------------------------------------

def prepare_shots() -> list[Path]:
    SHOT_SOURCES.mkdir(parents=True, exist_ok=True)
    SHOTS_OUT.mkdir(parents=True, exist_ok=True)
    outputs: list[Path] = []

    for key, source in SOURCES.items():
        source_path = Path(source)
        if not source_path.exists():
            raise SystemExit(f"[prepare-assets] 缺少源截图: {source_path}")
        local = SHOT_SOURCES / f"{key}.png"
        if not local.exists() or local.stat().st_size != source_path.stat().st_size:
            local.write_bytes(source_path.read_bytes())

        image = Image.open(local).convert("RGBA")
        cropped = image.crop(BORDER_BOX)
        cropped.putalpha(rounded_mask(cropped.size, CORNER_RADIUS))

        out = SHOTS_OUT / f"{key}.webp"
        cropped.save(out, "WEBP", quality=84, method=6)
        outputs.append(out)
        print(f"[shot] {out.relative_to(ROOT)}  {cropped.size[0]}x{cropped.size[1]}  "
              f"{out.stat().st_size // 1024} KB")

    # AI 页细节特写（建议行 + 输入区）
    for theme in ("dark", "light"):
        base = Image.open(SHOTS_OUT / f"ai-home-{theme}.webp").convert("RGBA")
        detail = base.crop(DETAIL_BOX)
        detail.putalpha(rounded_mask(detail.size, 12))
        out = SHOTS_OUT / f"ai-detail-{theme}.webp"
        detail.save(out, "WEBP", quality=86, method=6)
        outputs.append(out)
        print(f"[shot] {out.relative_to(ROOT)}  {detail.size[0]}x{detail.size[1]}  "
              f"{out.stat().st_size // 1024} KB")

    return outputs


# --------------------------------------------------------------------------
# 2. 图标 / OG 图 / manifest
# --------------------------------------------------------------------------

def prepare_icons() -> list[Path]:
    PUBLIC_OUT.mkdir(parents=True, exist_ok=True)
    outputs: list[Path] = []

    # 圆角深底 + 暖色四角星
    for size in (180, 192, 512):
        image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        draw = ImageDraw.Draw(image)
        radius = int(size * 0.22)
        draw.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=BG_DARK)
        draw_star(image, size / 2, size / 2, size * 0.30, (*ACCENT, 255))
        out = PUBLIC_OUT / f"icon-{size}.png"
        image.save(out, "PNG", optimize=True)
        outputs.append(out)
        print(f"[icon] {out.relative_to(ROOT)}  {size}x{size}")

    # favicon.svg：与站内 ZxStar 同一条路径
    favicon = (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">'
        f'<rect width="48" height="48" rx="11" fill="#131210"/>'
        '<path d="M24 6c0 9.9-8.6 17.5-17.5 18C15.4 24.5 24 32.1 24 42c0-9.9 8.6-17.5 17.5-18C32.6 23.5 24 15.9 24 6Z" '
        f'fill="rgb({ACCENT[0]},{ACCENT[1]},{ACCENT[2]})"/></svg>'
    )
    (PUBLIC_OUT / "favicon.svg").write_text(favicon, encoding="utf-8")
    outputs.append(PUBLIC_OUT / "favicon.svg")
    print(f"[icon] {(PUBLIC_OUT / 'favicon.svg').relative_to(ROOT)}")

    # site.webmanifest
    manifest = {
        "name": "枕星图吧AI助手",
        "short_name": "枕星图吧",
        "description": "Windows 桌面工具箱：AI 助手 + 完整工具集 + 硬件信息。",
        "lang": "zh-CN",
        "start_url": "/",
        "display": "standalone",
        "background_color": "#131210",
        "theme_color": "#131210",
        "icons": [
            {"src": "/zxai/icon-180.png", "sizes": "180x180", "type": "image/png"},
            {"src": "/zxai/icon-192.png", "sizes": "192x192", "type": "image/png"},
            {"src": "/zxai/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any maskable"},
        ],
    }
    manifest_path = PUBLIC_OUT / "site.webmanifest"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    outputs.append(manifest_path)
    print(f"[icon] {manifest_path.relative_to(ROOT)}")

    return outputs


def prepare_og() -> list[Path]:
    width, height = 1200, 630
    canvas = Image.new("RGBA", (width, height), (*BG_DARK, 255))

    # 顶部暖色光晕
    glow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    glow_draw.ellipse([width * 0.1, -height * 0.9, width * 0.9, height * 0.6],
                      fill=(*ACCENT, 48))
    glow = glow.filter(ImageFilter.GaussianBlur(90))
    canvas = Image.alpha_composite(canvas, glow)
    draw = ImageDraw.Draw(canvas)

    # 左：品牌标记 + 标题
    draw_star(canvas, 92, 104, 32, (*ACCENT, 255))

    draw.text((136, 74), "枕星图吧AI助手", font=font("SarasaUiSC-Bold.ttf", 52), fill=TEXT_MAIN)
    draw.text((138, 166), "AI 助手 × 完整工具箱 × 硬件信息",
              font=font("SarasaUiSC-Regular.ttf", 26), fill=TEXT_MUTED)
    draw.text((138, 226), "开发中 · 尚未公开发布", font=font("SarasaUiSC-Bold.ttf", 22), fill=ACCENT)
    draw.text((138, 560), "基于上游开源项目「图吧工具箱CE」（GPL-3.0）的衍生改造",
              font=font("SarasaUiSC-Regular.ttf", 19), fill=TEXT_DIM)

    # 右：应用实拍截图（AI 助手页，裁到 520 宽避免与文字重叠）
    shot = Image.open(SHOTS_OUT / "ai-home-dark.webp").convert("RGBA")
    target_h = 420
    ratio = target_h / shot.height
    shot = shot.resize((int(shot.width * ratio), target_h), Image.LANCZOS)
    shot = shot.crop((0, 0, 520, target_h))
    frame = Image.new("RGBA", (shot.width + 14, shot.height + 14), (255, 255, 255, 24))
    frame.alpha_composite(shot, (7, 7))
    frame.putalpha(ImageChops.multiply(frame.getchannel("A"), rounded_mask(frame.size, 18)))
    canvas.alpha_composite(frame, (width - frame.width - 40, (height - frame.height) // 2))

    out = PUBLIC_OUT / "og.png"
    canvas.convert("RGB").save(out, "PNG", optimize=True)
    print(f"[og]   {out.relative_to(ROOT)}  {width}x{height}  {out.stat().st_size // 1024} KB")
    return [out]


def main() -> None:
    # 可选只跑其中一类：python scripts/prepare-assets.py og
    parts = {arg.lower() for arg in sys.argv[1:]} or {"shots", "icons", "og"}
    products: list[Path] = []
    if "shots" in parts:
        products += prepare_shots()
    if "icons" in parts:
        products += prepare_icons()
    if "og" in parts:
        products += prepare_og()
    print("\n[prepare-assets] 产物清单（SHA-256）：")
    for path in products:
        print(f"  {sha256(path)}  {path.relative_to(ROOT)}")


if __name__ == "__main__":
    os.chdir(ROOT)
    main()
