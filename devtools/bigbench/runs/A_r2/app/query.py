"""过滤任务。"""


def filter_tasks(tasks, status=None, keyword=None):
    """按状态和关键词过滤；status 不分大小写，关键词在标题和备注里都找。"""
    result = []
    for t in tasks:
        if status is not None and t["status"].lower() != status.lower():
            continue
        if keyword is not None:
            haystack = f"{t.get('title', '')} {t.get('notes', '')}".lower()
            if keyword.lower() not in haystack:
                continue
        result.append(t)
    return result
