"""过滤任务。"""


def filter_tasks(tasks, status=None, keyword=None):
    """按状态和关键词过滤；status 不分大小写，关键词在标题和备注里都找。"""
    result = []
    for t in tasks:
        if status is not None and t["status"] != status:
            continue
        if keyword is not None and keyword not in t["title"]:
            continue
        result.append(t)
    return result
