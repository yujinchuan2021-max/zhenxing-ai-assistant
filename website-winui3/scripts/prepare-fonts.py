#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
枕星图吧AI助手 官网 · 正文字体资源生成

站点提供三种正文字体（顶栏可切换，默认更纱黑体），本脚本负责把字体文件准备好：

  ① 更纱黑体 Sarasa UI SC —— SIL OFL 1.1（可修改/子集），取自官方 release
     https://github.com/be5invis/Sarasa-Gothic/releases （v1.0.41，Unhinted TTF 包）
     子集化为 woff2（400/500/700）。
  ② Noto Sans SC —— SIL OFL 1.1，直接用项目内已有 OTF（应用 Assets/Fonts，只读不写），
     子集化为 woff2（400/500/700）。
  ③ HarmonyOS Sans SC —— 华为 HarmonyOS Sans 字体许可协议（**非开源**）。
     协议明确「不得对字体做任何修改」，故本脚本**不做子集、不转 woff2**，
     整文件原样复制并随站点保留许可原文与显著署名。

依赖（不污染项目依赖，按需临时拉起）：
    uv run --with fonttools --with brotli --with py7zr python scripts/prepare-fonts.py

产物：public/zxai/fonts/（下载缓存与机器可读清单在 .font-cache/）
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import re
import shutil
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / ".font-cache"
OUT = ROOT / "public" / "zxai" / "fonts"
APP_FONTS = Path(r"D:\AI2\tubatools\TubaWinUi3.WinUI3\Assets\Fonts")

SARASA_TAG = "v1.0.41"
SARASA_ASSET = "SarasaUiSC-TTF-Unhinted-1.0.41.7z"
SARASA_BASE = f"https://github.com/be5invis/Sarasa-Gothic/releases/download/{SARASA_TAG}"

HARMONY_URL = "https://developer.huawei.com/images/download/general/HarmonyOS-Sans.zip"
HARMONY_ZIP = CACHE / "HarmonyOS-Sans.zip"

NOTO_SRC = {
    "regular": APP_FONTS / "NotoSansSC-Regular.otf",
    "medium": APP_FONTS / "NotoSansSC-Medium.otf",
    "bold": APP_FONTS / "NotoSansSC-Bold.otf",
}

NOTO_LICENSE_URL = "https://raw.githubusercontent.com/notofonts/noto-cjk/main/Sans/LICENSE"

# 站点实际用到的字重：400 正文 / 500 导航与强调 / 700 标题与按钮
# Sarasa 官方只提供 ExtraLight/Light/Regular/SemiBold/Bold，没有 Medium；
# 这里把 SemiBold 映射到 500 槽位（网页端常见的三档做法），使三套字体在 400/500/700 下都有对应字重。
SARASA_FACES = [("regular", "Regular", 400), ("semibold", "SemiBold", 500), ("bold", "Bold", 700)]
NOTO_FACES = [("regular", "Regular", 400), ("medium", "Medium", 500), ("bold", "Bold", 700)]

SCAN_TARGETS = [ROOT / "index.html", ROOT / "src" / "main.ts", ROOT / "src" / "zxai"]
SCAN_SUFFIXES = {".vue", ".ts", ".css", ".html"}

# 保底字符：文案调整后仍然常见（与旧 Maple 子集同一套口径）
FALLBACK_CHARS = (
    "".join(chr(code) for code in range(0x20, 0x7F))
    + "　、。〃〈〉《》「」『』【】〔〕・ー―‐–—‘’“”…※←↑→↓↔↗↙⇧✓✔✕✖×÷±≈≠≤≥∞°№"
    + "①②③④⑤⑥⑦⑧⑨⑩⑴⑵⑶⑷⑸⑹⑺⑻⑼⑽ⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩ"
    + "：；！？／｜·•▪▫◆◇○●□■△▲▽▼☆★♡♥☀☾☽"
)


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(url: str, dest: Path) -> Path:
    if dest.exists() and dest.stat().st_size > 0:
        print(f"[下载] 已缓存 {dest.name}（{dest.stat().st_size / 1048576:.1f} MB）")
        return dest
    dest.parent.mkdir(parents=True, exist_ok=True)
    print(f"[下载] {url}")
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler(
            {
                "http": os.environ.get("HTTP_PROXY", ""),
                "https": os.environ.get("HTTPS_PROXY", ""),
            }
            if os.environ.get("HTTPS_PROXY")
            else {}
        )
    )
    with opener.open(url, timeout=300) as response, dest.open("wb") as handle:
        shutil.copyfileobj(response, handle, 1 << 20)
    print(f"[下载] 完成 {dest.name}（{dest.stat().st_size / 1048576:.1f} MB）")
    return dest


def collect_chars() -> set[str]:
    chars: set[str] = set(FALLBACK_CHARS)
    scanned = 0
    for target in SCAN_TARGETS:
        files = (
            [target]
            if target.is_file()
            else [p for p in sorted(target.rglob("*")) if p.suffix in SCAN_SUFFIXES]
        )
        for path in files:
            chars |= set(path.read_text(encoding="utf-8"))
            scanned += 1
    print(f"[子集] 扫描 {scanned} 个文件，收集 {len(chars)} 个字符")
    return chars


def font_names(path: Path) -> dict:
    """读字体内部 name 表，作为「确实是我们声明的那个字体」的证据。"""
    from fontTools.ttLib import TTFont

    font = TTFont(str(path), lazy=True)
    names = {1: "family", 2: "subfamily", 4: "full", 5: "version", 6: "psName"}
    out: dict[str, str] = {}
    for record in font["name"].names:
        if record.nameID in names and record.platformID == 3:
            try:
                out[names[record.nameID]] = record.toUnicode()
            except Exception:  # noqa: BLE001
                pass
        if record.nameID == 0 and record.platformID == 3 and "copyright" not in out:
            try:
                out["copyright"] = record.toUnicode()[:120]
            except Exception:  # noqa: BLE001
                pass
    font.close()
    return out


def subset(source: Path, dest: Path, text_file: Path, label: str) -> dict:
    from fontTools.subset import main as subset_main

    dest.parent.mkdir(parents=True, exist_ok=True)
    if subset_main(
        [
            str(source),
            f"--text-file={text_file}",
            "--flavor=woff2",
            f"--output-file={dest}",
            "--no-hinting",
            "--desubroutinize",
            "--name-IDs=1,2,4,6",
        ]
    ):
        raise SystemExit(f"子集失败：{label}")
    size_kb = dest.stat().st_size / 1024
    print(f"[子集] {label:26s} → {dest.name:36s} {size_kb:7.1f} KB")
    return {
        "file": dest.name,
        "bytes": dest.stat().st_size,
        "sha256": sha256_of(dest),
        "source": str(source),
        "source_sha256": sha256_of(source),
        "subset": True,
        "weights": [],
    }


def main() -> int:
    CACHE.mkdir(parents=True, exist_ok=True)
    OUT.mkdir(parents=True, exist_ok=True)
    manifest: dict = {"fonts": {}, "subset_chars": 0}

    # ---------- 字符集 ----------
    chars = collect_chars()
    manifest["subset_chars"] = len(chars)
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".txt", delete=False) as handle:
        handle.write("".join(sorted(chars)))
        text_file = Path(handle.name)

    # ---------- ① 更纱黑体 Sarasa UI SC ----------
    import py7zr

    archive = download(f"{SARASA_BASE}/{SARASA_ASSET}", CACHE / SARASA_ASSET)
    manifest_txt = download(f"{SARASA_BASE}/SHA-256.txt", CACHE / "sarasa-SHA-256.txt")
    expected = None
    for line in manifest_txt.read_text(encoding="utf-8").splitlines():
        if SARASA_ASSET in line:
            expected = line.split()[0].strip()
    actual = sha256_of(archive)
    if not expected or expected != actual:
        raise SystemExit(f"Sarasa 归档哈希不一致：期望 {expected}，实际 {actual}")
    print(f"[校验] Sarasa 归档 SHA-256 与官方清单一致：{actual}")

    sarasa_src = CACHE / "sarasa"
    want = {f"SarasaUiSC-{face}.ttf" for _, face, _ in SARASA_FACES}
    with py7zr.SevenZipFile(archive) as box:
        present = [n for n in box.getnames() if n.endswith(".ttf")]
        targets = [n for n in present if n.split("/")[-1] in want]
        if len(targets) != len(want):
            print(f"[解包] 警告：归档里只找到 {[t.split('/')[-1] for t in targets]}", file=sys.stderr)
        print(f"[解包] Sarasa 取 {len(targets)} 个字重：{[t.split('/')[-1] for t in targets]}")
        box.extract(path=sarasa_src, targets=targets)

    sarasa_files = []
    for key, face, weight in SARASA_FACES:
        name = f"SarasaUiSC-{face}.ttf"
        src = next(sarasa_src.rglob(name))
        entry = subset(src, OUT / f"sarasa-ui-sc-subset-{key}.woff2", text_file, f"Sarasa {name}")
        entry["weights"] = [weight]
        entry["names"] = font_names(src)
        sarasa_files.append(entry)
    manifest["fonts"]["sarasa"] = {
        "label": "更纱黑体 Sarasa UI SC",
        "license": "SIL Open Font License 1.1",
        "open_source": True,
        "source_url": f"https://github.com/be5invis/Sarasa-Gothic/releases/tag/{SARASA_TAG}",
        "archive": SARASA_ASSET,
        "archive_sha256": actual,
        "files": sarasa_files,
    }

    # ---------- ② Noto Sans SC（项目已有 OTF） ----------
    noto_files = []
    for key, face, weight in NOTO_FACES:
        src = NOTO_SRC[key]
        if not src.exists():
            raise SystemExit(f"找不到项目内的 Noto OTF：{src}")
        entry = subset(src, OUT / f"noto-sans-sc-subset-{key}.woff2", text_file, f"Noto {src.name}")
        entry["weights"] = [weight]
        entry["names"] = font_names(src)
        noto_files.append(entry)
    manifest["fonts"]["noto"] = {
        "label": "Noto Sans SC",
        "license": "SIL Open Font License 1.1",
        "open_source": True,
        "source_url": NOTO_LICENSE_URL,
        "files": noto_files,
    }

    # ---------- ③ HarmonyOS Sans SC（不可修改：原样复制） ----------
    zip_path = download(HARMONY_URL, HARMONY_ZIP)
    keep: list[tuple[str, str]] = []
    with zipfile.ZipFile(zip_path) as box:
        for name in box.namelist():
            base = name.rsplit("/", 1)[-1]
            if name.startswith("HarmonyOS Sans/HarmonyOS_Sans_SC/") and base in {
                "HarmonyOS_Sans_SC_Regular.ttf",
                "HarmonyOS_Sans_SC_Bold.ttf",
                "LICENSE.txt",
            }:
                keep.append((name, base))
        harmony_files = []
        for name, base in sorted(keep):
            if base == "LICENSE.txt":
                dest = OUT / "LICENSE-HarmonyOS-Sans.txt"
            else:
                dest = OUT / base
            data = box.read(name)
            dest.write_bytes(data)
            digest = hashlib.sha256(data).hexdigest()
            same = digest == sha256_of(dest)
            print(
                f"[原样] {base:34s} → {dest.name:34s} {len(data) / 1048576:6.2f} MB"
                f"  与官方包一致：{same}"
            )
            harmony_files.append(
                {
                    "file": dest.name,
                    "bytes": len(data),
                    "sha256": digest,
                    "source": f"{HARMONY_URL} → {name}",
                    "subset": False,
                    "unmodified": True,
                    "weights": [400] if base.endswith("Regular.ttf") else ([700] if base.endswith("Bold.ttf") else []),
                    "names": font_names(dest) if base.endswith(".ttf") else {},
                }
            )
    manifest["fonts"]["harmony"] = {
        "label": "HarmonyOS Sans SC",
        "license": "HarmonyOS Sans 字体许可协议（非开源；禁止修改，故不子集、不转 woff2）",
        "open_source": False,
        "source_url": HARMONY_URL,
        "archive_sha256": sha256_of(zip_path),
        "files": harmony_files,
    }

    # ---------- 许可文件（随站点保留原文） ----------
    download(
        "https://raw.githubusercontent.com/be5invis/Sarasa-Gothic/master/LICENSE",
        CACHE / "LICENSE-sarasa-gothic-OFL.txt",
    )
    download(NOTO_LICENSE_URL, CACHE / "LICENSE-noto-sans-sc-OFL.txt")
    for name in ("LICENSE-sarasa-gothic-OFL.txt", "LICENSE-noto-sans-sc-OFL.txt"):
        shutil.copyfile(CACHE / name, OUT / name)
    print("[许可] 已随站点保留：Sarasa OFL 1.1 / Noto OFL 1.1 / HarmonyOS Sans 协议原文")

    manifest["generated_at"] = "2026-09-23"
    (CACHE / "fonts-manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    total = sum(p.stat().st_size for p in sorted(OUT.iterdir()))
    print(f"\n[总计] public/zxai/fonts/ 共 {len(list(OUT.iterdir()))} 个文件，{total / 1048576:.2f} MB")
    print(f"[清单] {CACHE / 'fonts-manifest.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
