#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
枕星图吧AI助手 官网 · 素材生成脚本

做的事（全部离线、可重复执行）：
1. 显式指定 shots 与 --shot-source-dir 时，处理维护者提供的新截图；
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
import argparse
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                      # website-winui3/
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
SHOT_KEYS = ("ai-home-dark", "ai-home-light", "tools-dark", "tools-light", "hardware-dark")

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

def prepare_shots(source_dir: Path | None) -> list[Path]:
    if source_dir is None:
        raise SystemExit("更新截图须显式提供 --shot-source-dir；不会自动覆盖当前新版截图。")
    sources = {key: source_dir / f"{key}.png" for key in SHOT_KEYS}
    for source in sources.values():
        if not source.is_file():
            raise SystemExit(f"[prepare-assets] 缺少源截图: {source}")
    SHOTS_OUT.mkdir(parents=True, exist_ok=True)
    outputs: list[Path] = []

    for key, source in sources.items():
        image = Image.open(source).convert("RGBA")
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
    draw = ImageDraw.Draw(canvas)

    # Code-native brand and workflow illustration, never a fabricated app screenshot.
    draw.rounded_rectangle([64, 48, 116, 52], radius=2, fill=ACCENT)
    draw_star(canvas, 90, 125, 27, (*ACCENT, 255))
    draw.text((140, 85), "枕星图吧AI助手", font=font("SarasaUiSC-Bold.ttf", 54), fill=TEXT_MAIN)
    draw.text((64, 193), "从一个目标开始，把工具用起来。",
              font=font("SarasaUiSC-Regular.ttf", 34), fill=TEXT_MAIN)
    draw.text((64, 262), "0.1.1 公开预览版 · Windows x64",
              font=font("SarasaUiSC-Regular.ttf", 25), fill=ACCENT)
    draw.text((64, 338), "使用流程示意", font=font("SarasaUiSC-Regular.ttf", 18), fill=TEXT_MUTED)
    for index, label in enumerate(("明确目标", "选择方案", "准备工具", "开始使用")):
        left = 64 + index * 272
        draw.rounded_rectangle([left, 377, left + 248, 489], radius=15,
                               fill=(38, 35, 31), outline=(74, 65, 55), width=1)
        draw.text((left + 22, 393), f"0{index + 1}", font=font("SarasaUiSC-Bold.ttf", 18), fill=ACCENT)
        draw.text((left + 22, 431), label, font=font("SarasaUiSC-Bold.ttf", 25), fill=TEXT_MAIN)
    draw.text((64, 553), "AI 任务工作台 · 技能库 · 电脑工具中心",
              font=font("SarasaUiSC-Regular.ttf", 21), fill=TEXT_MUTED)
    draw.text((64, 596), "zhenxingai.com", font=font("SarasaUiSC-Regular.ttf", 16), fill=TEXT_MUTED)

    out = PUBLIC_OUT / "og.png"
    canvas.convert("RGB").save(out, "PNG", optimize=True)
    print(f"[og]   {out.relative_to(ROOT)}  {width}x{height}  {out.stat().st_size // 1024} KB")
    return [out]


def main() -> None:
    parser = argparse.ArgumentParser(description="准备品牌素材；更新截图须明确提供新截图目录")
    parser.add_argument("parts", nargs="*", choices=("shots", "icons", "og"))
    parser.add_argument("--shot-source-dir", type=Path)
    args = parser.parse_args()
    # Preserve reviewed client screenshots unless the maintainer explicitly replaces them.
    parts = set(args.parts) or {"icons", "og"}
    products: list[Path] = []
    if "shots" in parts:
        products += prepare_shots(args.shot_source_dir)
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
