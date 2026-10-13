"""读写任务清单（JSON 文件）。"""

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
    return sorted(tasks, key=lambda t: t["created"], reverse=(order == "desc"))
