"""一些通用小工具。"""

from scanner import scan


def format_size(n):
    """把字节数变成人看的字符串：512 -> '512 B'，1536 -> '1.5 KB'，2097152 -> '2 MB'"""
    if n < 1024:
        return f"{n} B"
    if n < 1024 * 1024:
        return f"{n} KB"
    return f"{n} MB"


def summary(dir_path):
    """用扫描结果算一个可读的摘要。"""
    files = scan(dir_path)
    total = sum(size for _, size in files)
    return f"{len(files)} 个文件，共 {format_size(total)}"
