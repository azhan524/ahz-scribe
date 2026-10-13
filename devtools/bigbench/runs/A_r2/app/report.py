"""统计。"""

from . import util


def summary(tasks):
    """返回 {'total','done','rate','total_size'}，rate 是完成百分比（保留一位小数）。"""
    total = len(tasks)
    done = 0
    size = 0
    for t in tasks:
        if t["status"].lower() == "done":
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
    """按优先级分组：返回 {1: [...], 2: [...], ...}，只包含出现过的优先级。

    注意：这个函数还没实现，测试已经在 tests/test_report_extra.py 里等着了。
    """
    groups = {}
    for t in tasks:
        groups.setdefault(int(t["priority"]), []).append(t)
    return groups
