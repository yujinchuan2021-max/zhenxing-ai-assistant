#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 Assets/Fonts/app-font.json（字体唯一权威配置）生成所有派生资产：

  1. TubaWinUi3.WinUI3/Assets/Fonts/app-font.css       —— Web 端 @font-face（全部候选 + 等宽）+ CSS 变量（相对同目录字体文件）
  2. TubaWinUi3.WinUI3/Services/AppFontFallback.g.cs   —— json 缺失/损坏时的编译期兜底镜像（测试断言与 json 一致）
  3. TubaWinUi3.Compatible/app-font-package.props      —— 兼容版（net48）随包字体/许可清单（csproj 导入；不再是手改点）

说明（v2 目录化）：
  · choices[] = 用户可选的界面字体目录（顺序即「设置 > 外观 > 界面字体」列表顺序；首项 = 默认）；
    每项含 id / displayName / familyName / regular / bold / license / attribution。
  · mono = 代码与数值等宽字体（与界面字体独立，保持随包）；monoStack 首族必须等于 mono.familyName。
  · uiStackSuffix = UI 字体栈的公共回退尾；运行时栈 = '<所选族名>', <suffix>。
  · App.xaml 不再由本脚本生成：资源字典根为 <svc:AppFontDictionary/>（启动时按已保存选择注入字体键），
    因此本脚本不触碰 App.xaml（结构由 FontSingleSourceTests 钉住）。

用法（任意目录）:
  python scripts/generate-app-font.py             # 生成/更新三份产物
  python scripts/generate-app-font.py --check     # 不写盘：校验三份产物与配置一致（过期/漂移检查）
  python scripts/generate-app-font.py --selftest  # 特殊字符负控：转义→解析/往返 + 拒绝路径（合成配置，不碰仓库文件）

转义口径（不做"加禁字符"式规避；结构性禁字符有明确原因，见 validate_cfg）：
  · XAML 属性（MSBuild props）→ XML 实体转义；C# 字符串字面量 → 反斜杠/引号/控制字符转义；
  · CSS 双引号字符串（face 规则）→ 反斜杠/引号转义；CSS 单引号字符串（变量行）→ 反斜杠/单引号转义。
幂等：重复运行为零改动；不改变既有文件的行尾风格。
测试 FontSingleSourceTests 会检查生成物不过期 / 与配置一致 / 特殊字符转义正确（后者经本文件 --selftest 钉住）。
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "TubaWinUi3.WinUI3"
FONTS = APP / "Assets" / "Fonts"
CFG = FONTS / "app-font.json"
CSS = FONTS / "app-font.css"
GCS = APP / "Services" / "AppFontFallback.g.cs"
PROPS = ROOT / "TubaWinUi3.Compatible" / "app-font-package.props"

VALID_ID_RE = re.compile(r"^[a-z][a-z0-9-]{0,31}$")
CONTROL_RE = re.compile(r"[\x00-\x1f\x7f]")
ATTR_REQUIRED = ("author", "url", "licenseText")
CHOICE_FILE_KEYS = ("regular", "bold", "license")
MONO_FILE_KEYS = ("regular", "bold", "license")


class ConfigError(Exception):
    """配置校验失败（fail-closed：不产出任何生成物）。"""


def fail(msg: str) -> None:
    print(f"[FAIL] {msg}")
    sys.exit(1)


# ---------- 转义（按输出语法） ----------

def esc_xml_text(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def esc_xml_attr(s: str) -> str:
    return esc_xml_text(s).replace('"', "&quot;")


def esc_cs(s: str) -> str:
    return (s.replace("\\", "\\\\").replace('"', '\\"')
             .replace("\r", "\\r").replace("\n", "\\n").replace("\t", "\\t"))


def esc_css_str(s: str) -> str:
    """CSS 双引号字符串内容转义（face 规则）。"""
    return s.replace("\\", "\\\\").replace('"', '\\"')


def esc_css_single(s: str) -> str:
    """CSS 单引号字符串内容转义（:root 变量行）。"""
    return s.replace("\\", "\\\\").replace("'", "\\'")


def _unbackslash(s: str) -> str:
    out = []
    i = 0
    while i < len(s):
        if s[i] == "\\" and i + 1 < len(s):
            out.append(s[i + 1])
            i += 2
        else:
            out.append(s[i])
            i += 1
    return "".join(out)


def primary_family_of(stack: str) -> str:
    """取字体栈首个族名（与 C# AppFontSpec.PrimaryFamilyOf 同一口径）。"""
    seg = stack.split(",")[0].strip()
    if len(seg) >= 2 and seg[0] == seg[-1] and seg[0] in "'\"":
        seg = _unbackslash(seg[1:-1])
    return seg


def format_of(file_name: str) -> str:
    """CSS format() 由文件扩展名推导（不写死 truetype：Noto 为 OTF/CFF）。"""
    ext = Path(file_name).suffix.lower()
    return {".otf": "opentype", ".otc": "opentype", ".woff2": "woff2", ".woff": "woff", ".ttf": "truetype"}.get(ext, "truetype")


# ---------- 校验 ----------

def _check_family(fam: str, where: str) -> None:
    if fam != fam.strip():
        raise ConfigError(f"{where} 首尾不能有空白: {fam!r}")
    if "#" in fam:
        raise ConfigError(f"{where} 不能包含 #（打包 URI 用 # 分隔文件路径与族名）: {fam!r}")
    if "," in fam:
        raise ConfigError(f"{where} 不能包含 ,（字体栈/列表以逗号分隔族名）: {fam!r}")
    if CONTROL_RE.search(fam):
        raise ConfigError(f"{where} 含控制字符: {fam!r}")


def _check_file_name(v: str, where: str, require_files: bool, base: Path) -> None:
    if not isinstance(v, str) or not v.strip():
        raise ConfigError(f"{where} 缺失或为空")
    if "/" in v or "\\" in v or v in (".", ".."):
        raise ConfigError(f"{where} 必须是单纯的 Fonts 目录内文件名: {v!r}")
    if "#" in v:
        raise ConfigError(f"{where} 不能包含 #（打包 URI 用 # 分隔文件路径与族名）: {v!r}")
    if CONTROL_RE.search(v):
        raise ConfigError(f"{where} 含控制字符: {v!r}")
    if require_files and not (base / v).exists():
        raise ConfigError(f"字体/许可文件不存在: {base / v}")


def _check_attribution(attr, where: str) -> None:
    if not isinstance(attr, dict):
        raise ConfigError(f"{where}.attribution 缺失或不是对象")
    for key in ATTR_REQUIRED:
        v = attr.get(key)
        if not isinstance(v, str) or not v.strip():
            raise ConfigError(f"{where}.attribution.{key} 缺失或为空")
        if CONTROL_RE.search(v):
            raise ConfigError(f"{where}.attribution.{key} 含控制字符")
    if not re.match(r"^https?://\S+$", attr["url"]):
        raise ConfigError(f"{where}.attribution.url 必须是 http(s):// 绝对地址: {attr['url']!r}")


def validate_choice(choice, index: int, require_files: bool) -> None:
    where = f"choices[{index}]"
    if not isinstance(choice, dict):
        raise ConfigError(f"{where} 必须是对象")
    cid = choice.get("id")
    if not isinstance(cid, str) or not VALID_ID_RE.match(cid):
        raise ConfigError(f"{where}.id 必须是 ^[a-z][a-z0-9-]{{0,31}}$: {cid!r}")
    disp = choice.get("displayName")
    if not isinstance(disp, str) or not disp.strip():
        raise ConfigError(f"{where}.displayName 缺失或为空")
    if CONTROL_RE.search(disp):
        raise ConfigError(f"{where}.displayName 含控制字符")
    fam = choice.get("familyName")
    if not isinstance(fam, str) or not fam.strip():
        raise ConfigError(f"{where}.familyName 缺失或为空")
    _check_family(fam, f"{where}.familyName")
    for key in CHOICE_FILE_KEYS:
        _check_file_name(choice.get(key), f"{where}.{key}", require_files, FONTS)
    _check_attribution(choice.get("attribution"), where)


def validate_cfg(cfg: dict, require_files: bool = True) -> None:
    if not isinstance(cfg, dict):
        raise ConfigError("配置必须是 JSON 对象")

    for key in ("uiStackSuffix", "monoStack"):
        v = cfg.get(key)
        if not isinstance(v, str) or not v.strip():
            raise ConfigError(f"配置字段缺失或为空: {key}")
        if CONTROL_RE.search(v):
            raise ConfigError(f"配置 {key} 含控制字符: {v!r}")

    mono = cfg.get("mono")
    if not isinstance(mono, dict):
        raise ConfigError("配置字段缺失或为空: mono")
    mfam = mono.get("familyName")
    if not isinstance(mfam, str) or not mfam.strip():
        raise ConfigError("mono.familyName 缺失或为空")
    _check_family(mfam, "mono.familyName")
    for key in MONO_FILE_KEYS:
        _check_file_name(mono.get(key), f"mono.{key}", require_files, FONTS)

    first_mono = primary_family_of(cfg["monoStack"])
    if first_mono != mfam:
        raise ConfigError(f"monoStack 的首个字体族必须是 {mfam!r}（实际 {first_mono!r}）")

    choices = cfg.get("choices")
    if not isinstance(choices, list) or not choices:
        raise ConfigError("配置字段缺失或为空: choices（至少一个候选字体）")
    seen = set()
    for i, choice in enumerate(choices):
        validate_choice(choice, i, require_files)
        cid = choice["id"]
        if cid in seen:
            raise ConfigError(f"choices[{i}].id 重复: {cid!r}")
        seen.add(cid)


def load_cfg() -> dict:
    if not CFG.exists():
        fail(f"配置不存在: {CFG}")
    try:
        cfg = json.loads(CFG.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        fail(f"配置不是合法 JSON: {e}")
    try:
        validate_cfg(cfg, require_files=True)
    except ConfigError as e:
        fail(str(e))
    return cfg


# ---------- 生成 ----------

def css_faces(cfg: dict):
    """返回 [(family, file)] 的有序 face 列表：全部候选（Regular/Bold）+ 等宽（Regular/Bold）。"""
    faces = []
    for choice in cfg["choices"]:
        faces.append((choice["familyName"], choice["regular"], 400))
        faces.append((choice["familyName"], choice["bold"], 700))
    mono = cfg["mono"]
    faces.append((mono["familyName"], mono["regular"], 400))
    faces.append((mono["familyName"], mono["bold"], 700))
    return faces


def build_css(cfg: dict) -> str:
    default_family = cfg["choices"][0]["familyName"]
    lines = [
        "/* 由 scripts/generate-app-font.py 从 app-font.json 生成 —— 请勿手改（改配置后重跑生成器）。 */",
        "/* 同一份文件在三种宿主下同源生效：zxassets.local / bench.local 虚拟主机、file:// 同目录、HTTP 的 /fonts 路由 */",
        "/* @font-face 覆盖全部候选字体（按需加载）；--app-font 由运行时按已保存选择注入覆盖（WebView 文档创建脚本 / 服务端模板）。 */",
    ]
    for family, file_name, weight in css_faces(cfg):
        lines += [
            "@font-face {",
            f'    font-family: "{esc_css_str(family)}";',
            f'    src: url("{esc_css_str(file_name)}") format("{format_of(file_name)}");',
            f"    font-weight: {weight};",
            "    font-display: swap;",
            "}",
        ]
    lines += [
        ":root {",
        f"    --app-font: '{esc_css_single(default_family)}', {cfg['uiStackSuffix']};",
        f"    --app-font-mono: {cfg['monoStack']};",
        "}",
        "",
    ]
    return "\n".join(lines)


def build_gcs(cfg: dict) -> str:
    lines = [
        "// <auto-generated>",
        "// 由 scripts/generate-app-font.py 从 Assets/Fonts/app-font.json 生成 —— 请勿手改。",
        "// 用途：app-font.json 缺失/损坏时 AppFonts 启动读取失败的编译期兜底镜像；",
        "//       FontSingleSourceTests 断言其与 app-font.json 逐字段一致（防两处漂移）。",
        "// </auto-generated>",
        "namespace TubaWinUi3.Services;",
        "",
        "public sealed partial class AppFontCatalog",
        "{",
        "    /// <summary>配置缺失/损坏时的兜底镜像（与 app-font.json 逐字段一致，由 FontSingleSourceTests 钉住）。</summary>",
        "    public static AppFontCatalog Fallback { get; } = new()",
        "    {",
        f'        UiStackSuffix = "{esc_cs(cfg["uiStackSuffix"])}",',
        f'        MonoStack = "{esc_cs(cfg["monoStack"])}",',
        "        Mono = new AppFontMonoSpec",
        "        {",
        f'            FamilyName = "{esc_cs(cfg["mono"]["familyName"])}",',
        f'            RegularFileName = "{esc_cs(cfg["mono"]["regular"])}",',
        f'            BoldFileName = "{esc_cs(cfg["mono"]["bold"])}",',
        f'            LicenseFileName = "{esc_cs(cfg["mono"]["license"])}",',
        "        },",
        "        Choices = new List<AppFontSpec>",
        "        {",
    ]
    for choice in cfg["choices"]:
        attr = choice["attribution"]
        lines += [
            "            new AppFontSpec",
            "            {",
            f'                Id = "{esc_cs(choice["id"])}",',
            f'                DisplayName = "{esc_cs(choice["displayName"])}",',
            f'                FamilyName = "{esc_cs(choice["familyName"])}",',
            f'                RegularFileName = "{esc_cs(choice["regular"])}",',
            f'                BoldFileName = "{esc_cs(choice["bold"])}",',
            f'                LicenseFileName = "{esc_cs(choice["license"])}",',
            "                Attribution = new AppFontSpec.FontAttribution",
            "                {",
            f'                    Author = "{esc_cs(attr["author"])}",',
            f'                    Url = "{esc_cs(attr["url"])}",',
            f'                    LicenseText = "{esc_cs(attr["licenseText"])}",',
            "                },",
            "            },",
        ]
    lines += [
        "        },",
        "    };",
        "}",
        "",
    ]
    return "\n".join(lines)


def props_items(cfg: dict):
    """兼容版随包清单条目（顺序即测试断言口径）：json + 每个候选的 regular/bold/license。"""
    items = ["app-font.json"]
    for choice in cfg["choices"]:
        items += [choice["regular"], choice["bold"], choice["license"]]
    return items


def build_props(cfg: dict) -> str:
    lines = [
        "<!-- 由 scripts/generate-app-font.py 从 Assets/Fonts/app-font.json 生成 —— 请勿手改（改配置后重跑生成器）。 -->",
        "<Project>",
        "  <ItemGroup>",
    ]
    for name in props_items(cfg):
        inc = "$(MSBuildThisFileDirectory)..\\TubaWinUi3.WinUI3\\Assets\\Fonts\\" + name
        link = "Assets\\Fonts\\" + name
        lines += [
            f'    <Content Include="{esc_xml_attr(inc)}">',
            f"      <Link>{esc_xml_text(link)}</Link>",
            "      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>",
            "    </Content>",
        ]
    lines += ["  </ItemGroup>", "</Project>", ""]
    return "\n".join(lines)


def read_raw(path: Path) -> str:
    with path.open("r", encoding="utf-8", newline="") as f:
        return f.read()


def write_raw(path: Path, text: str) -> None:
    with path.open("w", encoding="utf-8", newline="") as f:
        f.write(text)


def write_if_changed(path: Path, content: str) -> bool:
    old = read_raw(path) if path.exists() else None
    if old == content:
        print(f"[UNCHANGED] {path.relative_to(ROOT)}")
        return False
    write_raw(path, content)
    print(f"[WROTE]     {path.relative_to(ROOT)}")
    return True


def check(cfg: dict) -> int:
    stale = []
    if not CSS.exists() or read_raw(CSS) != build_css(cfg):
        stale.append(CSS)
    if not GCS.exists() or read_raw(GCS) != build_gcs(cfg):
        stale.append(GCS)
    if not PROPS.exists() or read_raw(PROPS) != build_props(cfg):
        stale.append(PROPS)
    for path in stale:
        print(f"[STALE]     {path.relative_to(ROOT)}（与 app-font.json 不一致，请重跑本生成器）")
    if stale:
        print(f"[CHECK-FAIL] {len(stale)} 个生成物过期/缺失")
        return 3
    print(f"[CHECK-OK]   三份生成物与 app-font.json 一致（choices={[c['id'] for c in cfg['choices']]}）")
    return 0


# ---------- 特殊字符负控（--selftest） ----------

def _deep(d):
    if isinstance(d, dict):
        return {k: _deep(v) for k, v in d.items()}
    if isinstance(d, list):
        return [_deep(v) for v in d]
    return d


def selftest() -> int:
    import xml.etree.ElementTree as ET

    def choice(cid, disp, fam, reg, bold, lic, author, url, ltext):
        return {
            "id": cid, "displayName": disp, "familyName": fam,
            "regular": reg, "bold": bold, "license": lic,
            "attribution": {"author": author, "url": url, "licenseText": ltext},
        }

    def catalog(choices, suffix, mono_stack, mono_fam, mono_files):
        return {
            "uiStackSuffix": suffix,
            "monoStack": mono_stack,
            "mono": {
                "familyName": mono_fam,
                "regular": mono_files[0], "bold": mono_files[1], "license": mono_files[2],
            },
            "choices": choices,
        }

    positives = [
        catalog(
            [
                choice("sarasa", "更纱黑体 Sarasa UI SC", "Sarasa UI SC", "S.ttf", "SB.ttf", "S.txt",
                       "Renzhi Li", "https://github.com/be5invis/Sarasa-Gothic", "SIL OFL 1.1"),
                choice("noto", "Noto Sans SC", "Noto Sans SC", "N.otf", "NB.otf", "N.txt",
                       "Google", "https://fonts.google.com/noto", "SIL OFL 1.1"),
                choice("harmony", "HarmonyOS Sans SC", "HarmonyOS Sans SC", "H.ttf", "HB.ttf", "H.txt",
                       "Huawei", "https://developer.huawei.com/x.zip", "华为许可"),
            ],
            "'Segoe UI Variable', system-ui, sans-serif", "'Mono CN', monospace", "Mono CN", ("MR.ttf", "MB.ttf", "ML.txt"),
        ),
        catalog(
            [
                choice("a-b", '字体 "X" & <Y>', "A & B", "R&A.ttf", "B&B.ttf", "L&L.txt",
                       "A & B Studio", "https://example.com/?a=1&b=2", "许可 <说明>"),
                choice("c", "O'Brien", "O'Brien \\ Co", "O'Brien.ttf", "B.ttf", "L.txt",
                       "O'Brien", "https://example.com/y", 'He said "hi" & left <now>'),
            ],
            "'A & B', 'O\\'Brien', sans-serif", "'O\\'Brien \\\\ Co', monospace", "O'Brien \\ Co",
            ("O'Brien.ttf", "B.ttf", "L.txt"),
        ),
    ]

    def assert_css(cfg):
        css = build_css(cfg)
        fams = [(m.group(1), m.group(2)) for m in re.finditer(
            r'font-family: "((?:[^"\\]|\\.)*)";\s*\n\s*src: url\("((?:[^"\\]|\\.)*)"\) format\("([a-z0-9]+)"\);', css)]
        want = []
        for c in cfg["choices"]:
            want += [(c["familyName"], c["regular"]), (c["familyName"], c["bold"])]
        want += [(cfg["mono"]["familyName"], cfg["mono"]["regular"]), (cfg["mono"]["familyName"], cfg["mono"]["bold"])]
        got = [(_unbackslash(f), _unbackslash(u)) for f, u in fams]
        assert got == want, f"css face 往返失败:\n  got={got}\n  want={want}"
        var_m = re.search(r"--app-font: '((?:[^'\\]|\\.)*)', ", css)
        assert var_m, "css 缺少 --app-font 变量行"
        assert _unbackslash(var_m.group(1)) == cfg["choices"][0]["familyName"], "css --app-font 往返失败"
        assert f"--app-font-mono: {cfg['monoStack']};" in css, "css monoStack 未原样入栈"
        # format() 由扩展名推导：.otf → opentype（不写死 truetype）
        for c in cfg["choices"]:
            for key in ("regular", "bold"):
                if c[key].lower().endswith(".otf"):
                    assert f'url("{esc_css_str(c[key])}") format("opentype")' in css, "otf format 未写 opentype"
                if c[key].lower().endswith(".ttf"):
                    assert f'url("{esc_css_str(c[key])}") format("truetype")' in css, "ttf format 未写 truetype"

    def assert_gcs(cfg):
        gcs = build_gcs(cfg)
        pat = re.compile(r'^\s*(?P<name>\w+) = "(?P<v>(?:[^"\\\x00-\x1f]|\\.)*)",$', re.M)
        seen = [(_unbackslash(m.group("v"))) for m in pat.finditer(gcs)]
        expected = []
        for c in cfg["choices"]:
            expected += [c["id"], c["displayName"], c["familyName"], c["regular"], c["bold"], c["license"],
                         c["attribution"]["author"], c["attribution"]["url"], c["attribution"]["licenseText"]]
        expected += [cfg["uiStackSuffix"], cfg["monoStack"], cfg["mono"]["familyName"], cfg["mono"]["regular"],
                     cfg["mono"]["bold"], cfg["mono"]["license"]]
        for v in expected:
            if v == "":
                continue
            assert v in seen, f"g.cs 缺少往返值: {v!r}"

    def assert_props(cfg):
        props = build_props(cfg)
        root = ET.fromstring(props)
        items = props_items(cfg)
        links = [e.text for e in root.iter("Link")]
        assert links == ["Assets\\Fonts\\" + n for n in items], f"props Link 往返失败: {links!r}"
        incs = [e.attrib["Include"] for e in root.iter("Content")]
        assert len(incs) == len(items), f"props Content 数量不符: {len(incs)}"
        for inc, n in zip(incs, items):
            assert inc.endswith(n), f"props Include 末段不符: {inc!r}"

    negs = []
    n = catalog([choice("sarasa", "d", "F", "a.ttf", "b.ttf", "c.txt", "A", "https://e.com", "L")],
                "'F', sans-serif", "'M', monospace", "M", ("m.ttf", "mb.ttf", "ml.txt"))
    n["choices"] = []; negs.append((n, "choices"))
    n = catalog([choice("S!", "d", "F", "a.ttf", "b.ttf", "c.txt", "A", "https://e.com", "L")],
                "'F', sans-serif", "'M', monospace", "M", ("m.ttf", "mb.ttf", "ml.txt")); negs.append((n, "id"))
    n = _deep(positives[0]); n["choices"][1]["id"] = "sarasa"; negs.append((n, "重复"))
    n = _deep(positives[0]); n["choices"][0]["familyName"] = "A#B"; negs.append((n, "familyName"))
    n = _deep(positives[0]); n["choices"][0]["regular"] = "../evil.ttf"; negs.append((n, "文件名"))
    n = _deep(positives[0]); n["choices"][0]["attribution"]["url"] = "ftp://e.com/x"; negs.append((n, "http"))
    n = _deep(positives[0]); del n["choices"]; negs.append((n, "choices"))
    n = _deep(positives[0]); n["monoStack"] = "'Other', monospace"; negs.append((n, "monoStack"))
    n = _deep(positives[0]); n["uiStackSuffix"] = "bad\x01suffix"; negs.append((n, "控制字符"))
    n = _deep(positives[0]); del n["mono"]; negs.append((n, "mono"))
    n = _deep(positives[0]); n["choices"][0]["displayName"] = ""; negs.append((n, "displayName"))

    try:
        for i, cfg in enumerate(positives, 1):
            try:
                validate_cfg(cfg, require_files=False)
            except ConfigError as e:
                raise AssertionError(f"正控 {i} 被误拒: {e}")
            assert_css(cfg)
            assert_gcs(cfg)
            assert_props(cfg)
        for i, (cfg, frag) in enumerate(negs, 1):
            try:
                validate_cfg(cfg, require_files=False)
            except ConfigError as e:
                assert frag in str(e), f"负控 {i} 信息不含 {frag!r}: {e}"
            else:
                raise AssertionError(f"负控 {i}（期望含 {frag!r}）未被拒绝")
    except AssertionError as e:
        print(f"[SELFTEST-FAIL] {e}")
        return 1
    print(f"[SELFTEST-OK] 特殊字符负控通过：{len(positives)} 组转义→解析/往返 + {len(negs)} 组拒绝路径")
    return 0


def main() -> int:
    if "--selftest" in sys.argv[1:]:
        return selftest()
    cfg = load_cfg()
    if "--check" in sys.argv[1:]:
        return check(cfg)
    changed = False
    changed |= write_if_changed(CSS, build_css(cfg))
    changed |= write_if_changed(GCS, build_gcs(cfg))
    changed |= write_if_changed(PROPS, build_props(cfg))
    print(f"[DONE] choices={[c['id'] for c in cfg['choices']]} changed={changed}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
