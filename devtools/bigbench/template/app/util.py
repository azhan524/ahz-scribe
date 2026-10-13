"""通用小工具。"""


def format_size(n):
    """字节数 -> 人看的字符串：512 -> '512 B'，1536 -> '1.5 KB'，2097152 -> '2 MB'。"""
    if n < 1024:
        return f"{n} B"
    if n < 1024 * 1024:
        return f"{n} KB"
    return f"{n} MB"


def normalize(text):
    """去掉首尾空白并把内部连续空白压成一个空格。"""
    return " ".join(str(text).split())
