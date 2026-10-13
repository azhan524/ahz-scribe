"""导出 CSV。"""

import csv
import io


def to_csv(tasks):
    """导出成 CSV 文本（带表头），字段里含逗号/引号时要按 CSV 规则加引号。"""
    buf = io.StringIO()
    writer = csv.writer(buf, lineterminator="\n")
    writer.writerow(["title", "priority", "status"])
    for t in tasks:
        writer.writerow([t["title"], t["priority"], t["status"]])
    return buf.getvalue()
