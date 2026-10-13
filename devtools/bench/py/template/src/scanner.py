"""扫描目录。"""

import os


def scan(dir_path):
    """扫一层目录，返回 [(路径, 大小)]，按大小从大到小。"""
    result = []
    if not os.path.isdir(dir_path):
        return result
    for name in os.listdir(dir_path):
        full = os.path.join(dir_path, name)
        if os.path.isfile(full):
            result.append((full, os.path.getsize(full)))
    result.sort(key=lambda item: item[1], reverse=True)
    return result
