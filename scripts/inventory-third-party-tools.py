#!/usr/bin/env python3
"""List the actual Tools payload without inferring any redistribution rights."""

from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "TubaWinUi3.WinUI3" / "Tools"
OUTPUT = ROOT / "zxai-docs" / "third-party-tools-inventory.md"
LICENSE_NAME = re.compile(r"(^|[._-])(licen[cs]e|copying|notice)([._-]|$)", re.IGNORECASE)


def escape(value: str) -> str:
    return value.replace("|", "\\|").replace("\n", " ")


def main() -> None:
    if not TOOLS.is_dir():
        raise SystemExit(f"Missing Tools directory: {TOOLS}")

    rows = []
    total_files = 0
    tool_dirs = sorted(
        (child for category in TOOLS.iterdir() if category.is_dir()
         for child in category.iterdir() if child.is_dir()),
        key=lambda path: str(path.relative_to(TOOLS)).casefold(),
    )
    for directory in tool_dirs:
        files = sorted(
            (path for path in directory.rglob("*") if path.is_file()),
            key=lambda path: str(path.relative_to(directory)).casefold(),
        )
        total_files += len(files)
        license_candidates = [
            path.relative_to(directory).as_posix()
            for path in files if LICENSE_NAME.search(path.name)
        ]
        license_text = ", ".join(f"`{escape(name)}`" for name in license_candidates)
        rows.append(
            f"| `{escape(directory.relative_to(TOOLS).as_posix())}` | {len(files)} | "
            f"{license_text or '—'} | 待核 |"
        )

    loose = sorted(path for path in TOOLS.rglob("*") if path.is_file()
                   and path.parent not in tool_dirs
                   and not any(directory in path.parents for directory in tool_dirs))
    total_files += len(loose)
    lines = [
        "# 随包第三方工具来源与许可审核清单",
        "",
        "> 自动列出当前源码目录中的资产；**这不是许可结论或发布批准**。",
        "> 生成命令：`python scripts/inventory-third-party-tools.py`。",
        "> **当前 109 项全部待核：完整 Tools.zip、含工具便携包及安装包尚不得当作已获许可的枕星版发行。**",
        "",
        f"扫描范围：`TubaWinUi3.WinUI3/Tools/`，{len(tool_dirs)} 个工具目录、"
        f"{total_files} 个文件（含目录外散文件）。实际发布还须检查 `Tools.zip`、便携包、安装包及运行时下载的差异。",
        "现有 `release-assets/Tools.zip` 是独立归档快照，尚未与这份源码目录清单逐文件比对，也未完成许可复核。",
        "",
        "## 发布前逐项核验流程",
        "",
        "1. 对每个目录记录开发者、官方网站或源码仓库、准确版本及下载地址；比对发行文件 SHA-256 和签名。",
        "2. 阅读该版本的许可原文及所有嵌套组件声明，保留许可证/NOTICE；确认允许将**当前二进制和附带文件**随本版安装包、便携包与 Tools.zip 再分发。仅有 GPL/MIT 等名称或作者官网链接不足以代替核对。",
        "3. 把每项的来源、版本、证据链接/存档位置、审核人及日期写入独立记录；可核验后才将状态改为“允许随包”。不允许再分发的项目改为用户自行从作者渠道下载、仅保留网页入口，或移出发行包。",
        "4. 对精简包、完整包和安装包分别检查实际清单；任何无明确结论的目录在枕星版发布前保持待核，不能因历史上游曾收录就自动放行。",
        "",
        "已有的 [2026-09-19 资产扫描](compliance-audit-2026-09-19.md) 对 `ULTRAISO`、`HDTune`、`finaldata` 给出重点风险线索；那是历史快照，需重新核对当前文件。脚本只根据文件名列出许可文件候选，不阅读许可内容，也不把候选文件存在视为授权。",
        "",
        "| 当前相对目录 | 文件数 | 目录内许可文件候选 | 再分发结论 |",
        "| --- | ---: | --- | --- |",
        *rows,
    ]
    if loose:
        lines.extend(["", "## 工具目录外文件", ""])
        lines.extend(f"- `{escape(path.relative_to(TOOLS).as_posix())}`（待核）" for path in loose)
    lines.extend(["", "## 审核记录模板", "",
                  "| 相对目录 | 作者/来源 | 版本/哈希 | 许可原文与再分发依据 | 审核人/日期 | 结论及发行处置 |",
                  "| --- | --- | --- | --- | --- | --- |",
                  "| （逐项补全） |  |  |  |  |  |", ""])
    OUTPUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {OUTPUT}: {len(tool_dirs)} directories, {total_files} files")


if __name__ == "__main__":
    main()
