"""标准答案：把 template 里的 7 个 bug 修掉 + 实现 by_priority。

用法：python reference-fix.py <项目目录>
只为验证"任务可解"，不参与给 agent 打分。
"""

import io
import os
import sys


def write(root, rel, text):
    path = os.path.join(root, rel)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("  fixed " + rel)


def main(root):
    # 1. util.format_size：补上换算
    write(root, "app/util.py", '''"""通用小工具。"""


def format_size(n):
    """字节数 -> 人看的字符串：512 -> '512 B'，1536 -> '1.5 KB'，2097152 -> '2 MB'。"""
    n = int(n)
    if n < 1024:
        return f"{n} B"
    if n < 1024 * 1024:
        value = n / 1024
        return f"{value:g} KB"
    value = n / (1024 * 1024)
    return f"{value:g} MB"


def normalize(text):
    """去掉首尾空白并把内部连续空白压成一个空格。"""
    return " ".join(str(text).split())
''')

    # 2. model.make_task：补上校验
    write(root, "app/model.py", '''"""任务对象。"""

VALID_STATUS = ("todo", "doing", "done")


def make_task(title, priority=3, status="todo", created=0, notes="", size=0):
    """建一个任务；priority 必须是 1..5，status 必须是 VALID_STATUS 之一。"""
    if not isinstance(priority, int) or priority < 1 or priority > 5:
        raise ValueError("priority 必须是 1..5 的整数，收到 " + repr(priority))
    if status not in VALID_STATUS:
        raise ValueError("status 必须是 " + "/".join(VALID_STATUS) + "，收到 " + repr(status))
    return {
        "title": title,
        "priority": priority,
        "status": status,
        "created": created,
        "notes": notes,
        "size": size,
    }


def priority_of(task):
    return int(task["priority"])
''')

    # 3. store.list_tasks：按 created 排序，尊重 order
    write(root, "app/store.py", '''"""读写任务清单（JSON 文件）。"""

import json
import os


def save(path, tasks):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(tasks, f, ensure_ascii=False)


def load(path):
    if not os.path.isfile(path):
        return []
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def list_tasks(tasks, order="asc"):
    """按创建时间排序返回；order='asc' 从旧到新，'desc' 从新到旧。"""
    reverse = str(order).lower() == "desc"
    return sorted(tasks, key=lambda t: t["created"], reverse=reverse)
''')

    # 4. query：状态不分大小写，关键词搜标题和备注
    write(root, "app/query.py", '''"""过滤任务。"""


def filter_tasks(tasks, status=None, keyword=None):
    """按状态和关键词过滤；status 不分大小写，关键词在标题和备注里都找。"""
    result = []
    want = status.strip().lower() if isinstance(status, str) else None
    word = keyword.lower() if isinstance(keyword, str) else None
    for t in tasks:
        if want is not None and str(t["status"]).lower() != want:
            continue
        if word is not None:
            haystack = (str(t.get("title", "")) + " " + str(t.get("notes", ""))).lower()
            if word not in haystack:
                continue
        result.append(t)
    return result
''')

    # 5. report：done 判定 + 完成率 + 实现 by_priority
    write(root, "app/report.py", '''"""统计。"""

from . import util


def summary(tasks):
    """返回 {'total','done','rate','total_size'}，rate 是完成百分比（保留一位小数）。"""
    total = len(tasks)
    done = 0
    size = 0
    for t in tasks:
        if str(t["status"]).lower() == "done":
            done += 1
        size += int(t.get("size", 0))
    rate = round(done / total * 100, 1) if total else 0.0
    return {
        "total": total,
        "done": done,
        "rate": rate,
        "total_size": util.format_size(size),
    }


def by_priority(tasks):
    """按优先级分组：返回 {1: [...], 2: [...], ...}，只包含出现过的优先级。"""
    groups = {}
    for t in tasks:
        groups.setdefault(t["priority"], []).append(t)
    return groups
''')

    # 6. export：CSV 加引号
    write(root, "app/export.py", '''"""导出 CSV。"""


def _cell(value):
    text = str(value)
    if "," in text or '"' in text or "\\n" in text:
        return '"' + text.replace('"', '""') + '"'
    return text


def to_csv(tasks):
    """导出成 CSV 文本（带表头），字段里含逗号/引号时要按 CSV 规则加引号。"""
    lines = ["title,priority,status"]
    for t in tasks:
        lines.append(",".join([_cell(t["title"]), _cell(t["priority"]), _cell(t["status"])]))
    return "\\n".join(lines) + "\\n"
''')

    # 7. cli：--status 存到正确的键
    write(root, "app/cli.py", '''"""命令行入口。"""

from . import query, report, store


def parse_args(argv):
    """支持：<清单文件> [--status STATUS] [--keyword WORD]"""
    opts = {"file": argv[0] if argv else "tasks.json", "status": None, "keyword": None}
    i = 1
    while i < len(argv):
        if argv[i] == "--status" and i + 1 < len(argv):
            opts["status"] = argv[i + 1]
            i += 2
        elif argv[i] == "--keyword" and i + 1 < len(argv):
            opts["keyword"] = argv[i + 1]
            i += 2
        else:
            i += 1
    return opts


def main(argv):
    opts = parse_args(argv)
    tasks = store.load(opts["file"])
    tasks = query.filter_tasks(tasks, status=opts["status"], keyword=opts["keyword"])
    info = report.summary(tasks)
    print(f"共 {info['total']} 个任务，完成 {info['done']} 个（{info['rate']}%），合计 {info['total_size']}")
    return 0
''')


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
