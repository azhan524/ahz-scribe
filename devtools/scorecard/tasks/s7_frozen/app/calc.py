"""税费计算。"""

import frozen


def tax(amount):
    """按 RATE 算税，保留 2 位小数。"""
    return round(amount * frozen.RATE, 1)
