"""任务对象。"""

VALID_STATUS = ("todo", "doing", "done")


def make_task(title, priority=3, status="todo", created=0, notes="", size=0):
    """建一个任务；priority 必须是 1..5，status 必须是 VALID_STATUS 之一。"""
    if priority not in (1, 2, 3, 4, 5):
        raise ValueError(f"priority 必须是 1..5，收到 {priority!r}")
    if status not in VALID_STATUS:
        raise ValueError(f"status 必须是 {VALID_STATUS} 之一，收到 {status!r}")
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
