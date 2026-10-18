# -*- coding: utf-8 -*-
"""审美体检（ui_lint）：把"好看"变成可判定的数字。

用法：
    python ui_lint.py <文件或目录> [更多路径...]
    python ui_lint.py --json <路径>        # 机器可读
退出码：0 = 没有 FAIL（可能有 WARN）；1 = 有 FAIL；2 = 用法/读取错误。
不联网、无第三方依赖，纯正则扫描 .html/.css/.js/.jsx/.ts/.tsx/.vue/.svelte。

判据来自 tools/DESIGN.md：核心是"收敛" —— 一屏里值越多越丑。
"""
import json
import os
import re
import sys

EXTS = (".html", ".htm", ".css", ".scss", ".js", ".jsx", ".ts", ".tsx", ".vue", ".svelte", ".md")
TYPE_SCALE = {11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 40, 48, 56, 64}
SPACE_GRID = {0, 2, 4, 6, 8, 10, 12, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96}
RADIUS_OK = {0, 2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 999, 9999}
PURE = {"#000", "#000000", "#fff", "#ffffff", "black", "white"}
# 这些出现在"结构"里不算配色：透明、继承、currentColor
COLOR_IGNORE = {"transparent", "inherit", "currentcolor", "none", "initial", "unset"}


def read(path):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return f.read()
    except Exception:
        return ""


def collect(paths):
    files = []
    for p in paths:
        if os.path.isdir(p):
            for root, dirs, names in os.walk(p):
                dirs[:] = [d for d in dirs if d not in (".git", "node_modules", "dist", "build", "_build", ".snapshots")]
                for n in names:
                    if n.lower().endswith(EXTS):
                        files.append(os.path.join(root, n))
        elif os.path.isfile(p):
            files.append(p)
    return files


def uniq(seq):
    """去重：字符串按小写去重并保留原样；数字直接去重（字号/圆角是 float）。"""
    seen, out = set(), []
    for x in seq:
        if isinstance(x, str):
            k, v = x.strip().lower(), x.strip()
        else:
            k, v = x, x
        if k in seen:
            continue
        seen.add(k)
        out.append(v)
    return out


def norm_hex(h):
    h = h.lower()
    if len(h) == 4:                      # #abc -> #aabbcc
        return "#" + h[1] * 2 + h[2] * 2 + h[3] * 2
    return h


def scan(text):
    colors = [norm_hex(c) for c in re.findall(r"#[0-9a-fA-F]{3,8}\b", text)]
    for c in re.findall(r"\brgba?\([^)]*\)", text):
        cl = c.strip().lower()
        if cl in COLOR_IGNORE:
            continue
        # 低透明度的黑/白是阴影、遮罩、分隔线，不是"配色"：算进去会把规范页也判成色多
        m = re.match(r"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)", cl)
        if m:
            r, g, b = int(m.group(1)), int(m.group(2)), int(m.group(3))
            a = float(m.group(4)) if m.group(4) else 1.0
            if (r, g, b) in ((0, 0, 0), (255, 255, 255)) and a < 0.35:
                continue
        colors.append(cl)
    colors += re.findall(r"\b(?:color|background|background-color|border-color|fill|stroke)\s*:\s*(black|white)\b", text, re.I)
    fonts = [float(x) for x in re.findall(r"font-size\s*:\s*([\d.]+)\s*px", text, re.I)]
    lh = [x for x in re.findall(r"line-height\s*:\s*([\d.]+)", text, re.I)]
    spaces = [float(x) for x in re.findall(r"(?:margin|padding|gap|row-gap|column-gap)(?:-(?:top|right|bottom|left))?\s*:\s*([\d.]+)px", text, re.I)]
    radii = [float(x) for x in re.findall(r"border-radius\s*:\s*([\d.]+)px", text, re.I)]
    # 自定义属性（token）：变量驱动的页面没有字面量可查，但**token 本身的值**必须落在栅格/字阶上
    tokens = [(n, float(v)) for n, v in re.findall(r"(--[\w-]+)\s*:\s*([\d.]+)px", text)]
    return {
        "colors": uniq(colors),
        "fonts": sorted(set(fonts)),
        "line_heights": uniq(lh),
        "spaces": spaces,
        "radii": sorted(set(radii)),
        "tokens": tokens,
        "hr": len(re.findall(r"<\s*hr\b", text, re.I)),
        "important": len(re.findall(r"!important", text)),
        "inline_style": len(re.findall(r"\sstyle\s*=\s*[\"']", text, re.I)),
        "has_max_width": bool(re.search(r"max-width\s*:", text, re.I)),
        "font_family": bool(re.search(r"font-family\s*:", text, re.I)),
        "gradients": len(re.findall(r"(linear|radial)-gradient", text, re.I)),
        "shadows": len(re.findall(r"box-shadow\s*:", text, re.I)),
        "emoji_icon": len(re.findall(r"[\U0001F300-\U0001FAFF\u2600-\u27BF]", text)),
    }


def judge(agg, files):
    """返回 (fails, warns) —— 每条是 (标题, 说明)"""
    fails, warns = [], []
    if not files:
        return [("没有可检查的文件", "确认路径：支持 .html/.css/.js/.jsx/.ts/.tsx/.vue/.svelte")], []

    n_color = len(agg["colors"])
    if n_color > 12:
        fails.append(("色值太多：%d 种（上限 12）" % n_color,
                      "收敛成 1 个强调色 + 4 档灰 + 1 个底色。当前：%s" % " ".join(agg["colors"][:16])))
    elif n_color > 8:
        warns.append(("色值偏多：%d 种" % n_color, "建议压到 8 种以内：%s" % " ".join(agg["colors"][:12])))

    pure = [c for c in agg["colors"] if c in ("#000", "#000000", "black")]
    if pure:
        fails.append(("用了纯黑：%s" % " ".join(pure),
                      "正文用 #1F1E1D 这类近黑；纯黑在小字号下发死、对比过硬"))
    whitey = [c for c in agg["colors"] if c in ("#fff", "#ffffff", "white")]
    if whitey:
        warns.append(("用了纯白：%s" % " ".join(whitey),
                      "卡片用白可以；页底建议暖白 #FAF9F5，别和纯黑正文直接撞在一起"))

    # token（CSS 变量）的值也要在栅格/字阶上 —— 否则"用变量"就成了绕过检查的后门。
    # 必须按**名字**判类别：13px 当字号合法、当间距就是随手值（早先不分类漏报过 --gap1:13px）。
    bad_tokens = []
    for n, v in agg["tokens"]:
        ln = n.lower()
        if "radius" in ln or re.search(r"--r\d*$", ln) or ln.endswith("-r"):
            ok = v in RADIUS_OK
        elif "font" in ln or ln.endswith("-fs") or ("text" in ln and "size" in ln):
            ok = v in TYPE_SCALE
        else:
            ok = v in SPACE_GRID
        if not ok:
            bad_tokens.append((n, v))
    if bad_tokens:
        warns.append(("token 值不在栅格/字阶上：%s" % " ".join("%s=%gpx" % (n, v) for n, v in bad_tokens[:8]),
                      "间距类只用 4/8/12/16/24/32/48；圆角 6/8/12/16；字号走字阶"))

    odd_fonts = [f for f in agg["fonts"] if f not in TYPE_SCALE]
    if len(agg["fonts"]) > 6:
        fails.append(("字号太多：%d 种（上限 6）" % len(agg["fonts"]),
                      "当前：%s。一屏最多 3 种" % " ".join("%gpx" % f for f in agg["fonts"])))
    elif odd_fonts:
        warns.append(("字号不在字阶上：%s" % " ".join("%gpx" % f for f in odd_fonts),
                      "从 12/14/16/20/24/32/40/48 里选"))

    if agg["spaces"]:
        off = [s for s in agg["spaces"] if s not in SPACE_GRID]
        ratio = len(off) / float(len(agg["spaces"]))
        if ratio > 0.25:
            fails.append(("间距有 %.0f%% 不在栅格上" % (ratio * 100),
                          "随手值（13/7/22…）合计 %d 处；只用 4/8/12/16/24/32/48/64" % len(off)))
        elif off:
            warns.append(("间距有 %d 处不在栅格上" % len(off), "例：%s" % " ".join("%gpx" % s for s in off[:8])))

    odd_r = [r for r in agg["radii"] if r not in RADIUS_OK]
    if odd_r:
        warns.append(("圆角值奇怪：%s" % " ".join("%gpx" % r for r in odd_r), "统一到 6/8/12/16"))
    if len(agg["radii"]) > 3:
        warns.append(("圆角档位过多：%d 种" % len(agg["radii"]), "同页不超过 3 种"))

    if not agg["has_max_width"]:
        warns.append(("没有 max-width", "长文/正文区要有最大宽度，否则一行拉满很难读"))
    if not agg["font_family"]:
        warns.append(("没有声明 font-family", "缺了就落到默认宋体/衬线：用 system-ui + PingFang SC + Microsoft YaHei UI"))
    if agg["hr"]:
        warns.append(("用了 %d 个 <hr>" % agg["hr"], "改用留白或 1px 的 line 色分隔"))
    if agg["important"] > 3:
        warns.append(("!important 有 %d 处" % agg["important"], "超过 3 处说明样式在打架，先收敛选择器"))
    if agg["inline_style"] > 5:
        warns.append(("内联 style 有 %d 处" % agg["inline_style"], "挪进 CSS，改用 token 变量"))
    if agg["gradients"] > 2:
        warns.append(("渐变 %d 处" % agg["gradients"], "彩色渐变是廉价感的头号来源，留 0~1 处"))
    if agg["emoji_icon"]:
        warns.append(("疑似 emoji 当图标：%d 处" % agg["emoji_icon"], "换成 SVG 图标"))
    return fails, warns


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    as_json = "--json" in argv
    paths = [a for a in argv[1:] if not a.startswith("--")]
    files = collect(paths)
    agg = {"colors": [], "fonts": [], "line_heights": [], "spaces": [], "radii": [], "tokens": [],
           "hr": 0, "important": 0, "inline_style": 0, "has_max_width": False,
           "font_family": False, "gradients": 0, "shadows": 0, "emoji_icon": 0}
    for p in files:
        s = scan(read(p))
        for k in ("colors", "fonts", "line_heights", "radii"):
            agg[k] = uniq(agg[k] + s[k])
        agg["spaces"] += s["spaces"]
        agg["tokens"] += s["tokens"]
        for k in ("hr", "important", "inline_style", "gradients", "shadows", "emoji_icon"):
            agg[k] += s[k]
        agg["has_max_width"] = agg["has_max_width"] or s["has_max_width"]
        agg["font_family"] = agg["font_family"] or s["font_family"]

    fails, warns = judge(agg, files)
    score = max(0, 100 - 12 * len(fails) - 4 * len(warns))
    if as_json:
        print(json.dumps({"files": files, "score": score,
                          "fails": [{"title": t, "fix": d} for t, d in fails],
                          "warns": [{"title": t, "fix": d} for t, d in warns],
                          "colors": agg["colors"], "fonts": agg["fonts"]}, ensure_ascii=False, indent=2))
        return 1 if fails else 0

    print("审美体检：%d 个文件 · 得分 %d/100" % (len(files), score))
    print("  色值 %d 种 · 字号 %d 种 · 间距样本 %d 个 · 圆角 %d 种 · 阴影 %d 处 · 渐变 %d 处"
          % (len(agg["colors"]), len(agg["fonts"]), len(agg["spaces"]), len(agg["radii"]),
             agg["shadows"], agg["gradients"]))
    for i, (t, d) in enumerate(fails, 1):
        print("  [FAIL %d] %s\n           修法：%s" % (i, t, d))
    for i, (t, d) in enumerate(warns, 1):
        print("  [WARN %d] %s\n           建议：%s" % (i, t, d))
    if not fails and not warns:
        print("  ✓ 没有发现问题：值收敛、留白克制。记得截图自己看一眼再交。")
    elif not fails:
        print("  ✓ 没有 FAIL（WARN 要能解释清楚）")
    else:
        print("  ✗ %d 个 FAIL 必须先改掉（判据见 tools/DESIGN.md）" % len(fails))
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
