"""导出 CSV。"""


def to_csv(tasks):
    """导出成 CSV 文本（带表头），字段里含逗号/引号时要按 CSV 规则加引号。"""
    lines = ["title,priority,status"]
    for t in tasks:
        lines.append(f"{t['title']},{t['priority']},{t['status']}")
    return "\n".join(lines) + "\n"
