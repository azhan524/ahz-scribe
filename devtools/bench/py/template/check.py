"""自检：python -B check.py t1|t2|t3

t1 = 单位换算正确；t2 = 函数改名跨文件完成且行为不变；t3 = --top 参数可用
"""

import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "src")
DATA = "数据"


def fail(msg):
    print("FAIL: " + msg)
    sys.exit(1)


def run_main(*args):
    out = subprocess.run(
        [sys.executable, "-B", os.path.join(SRC, "main.py")] + list(args),
        cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return (out.stdout or "") + (out.stderr or "")


def read_src():
    text = ""
    for name in sorted(os.listdir(SRC)):
        if name.endswith(".py"):
            with open(os.path.join(SRC, name), encoding="utf-8") as f:
                text += f.read() + "\n"
    return text


def size_of(text):
    """把 '1.5 KB' 这种字符串换算回字节数，不认的单位返回 -1。"""
    m = re.match(r"^\s*([\d.]+)\s*(B|KB|MB|GB)\s*$", text)
    if not m:
        return -1
    value = float(m.group(1))
    unit = m.group(2)
    return value * {"B": 1, "KB": 1024, "MB": 1024 ** 2, "GB": 1024 ** 3}[unit]


which = (sys.argv[1] if len(sys.argv) > 1 else "t1").lower()

if which == "t1":
    sys.path.insert(0, SRC)
    import util

    cases = [(512, "512 B"), (1536, "1.5 KB"), (2097152, "2 MB")]
    for n, want in cases:
        got = util.format_size(n)
        print(f"  format_size({n}) = {got!r}")
        if got != want:
            fail(f"format_size({n}) 应该是 {want!r}，实际 {got!r}")
        if abs(size_of(got) - n) > 1:
            fail(f"format_size({n}) 换算回来对不上：{got!r}")
    print("PASS: 单位换算正确")
    sys.exit(0)

out = run_main(DATA)
print(out.strip())

if which == "t2":
    src = read_src()
    if not re.search(r"def\s+scan_files\s*\(", src):
        fail("没有找到 scan_files（函数名可能没改）")
    if re.search(r"[^_\w]scan\s*\(", src):
        fail("还有调用点在用旧的 scan(")
    if "3 个文件" not in out:
        fail("改名后行为变了：应该还是 3 个文件")
    print("PASS: 跨文件改名完成，行为不变")
    sys.exit(0)

if which == "t3":
    top = run_main(DATA, "--top", "2")
    print("  --top 2 输出：")
    print(top.strip())
    rows = [l for l in top.splitlines() if re.match(r"^\s*[\d.]+\s*(B|KB|MB|GB)\s", l)]
    if len(rows) != 2:
        fail(f"--top 2 应该输出 2 行，实际 {len(rows)} 行")
    if not rows[0].startswith("32 B") or not rows[1].startswith("21 B"):
        fail(f"--top 没有按大小降序：{rows}")
    rows_def = [l for l in out.splitlines() if re.match(r"^\s*[\d.]+\s*(B|KB|MB|GB)\s", l)]
    if len(rows_def) != 3:
        fail(f"不带参数时应该输出全部 3 行，实际 {len(rows_def)} 行")
    print("PASS: --top 参数可用、排序正确、默认行为不变")
    sys.exit(0)

fail("未知检查项 " + which)
