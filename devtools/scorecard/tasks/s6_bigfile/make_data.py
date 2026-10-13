"""生成一个 2500 行左右的模块：大量常量表 + 一个带 bug 的函数。"""

import io
import os
import sys


def main(outdir):
    path = os.path.join(outdir, "app", "data.py")
    lines = []
    lines.append("# 自动生成的常量表（不要手改）")
    lines.append("TABLE = {")
    for i in range(1200):
        lines.append('    "key_%04d": %d,' % (i, i * 7 % 997))
    lines.append("}")
    lines.append("")
    lines.append("# 把 value 按比例缩放")
    lines.append("def scale(value, factor=1.0):")
    lines.append("    return value * factor ** 2")
    lines.append("")
    lines.append("def lookup(key):")
    lines.append('    return TABLE.get(key, -1)')
    lines.append("")
    for i in range(600):
        lines.append("# 填充注释 %04d" % i)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print("wrote %s (%d lines)" % (path, len(lines)))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
