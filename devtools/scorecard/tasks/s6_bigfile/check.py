"""自检：scale(3, 2) 应该是 6（现在是 12，因为乘法写成了平方）。"""

import os
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(ROOT, "app"))


def fail(msg):
    print("FAIL: " + msg)
    sys.exit(1)


import data  # noqa: E402

cases = [(3, 2, 6), (10, 0.5, 5), (7, 1, 7)]
for value, factor, want in cases:
    got = data.scale(value, factor)
    print("  scale(%s, %s) = %s（期望 %s）" % (value, factor, got, want))
    if abs(got - want) > 1e-9:
        fail("scale 还是不对")
if data.lookup("key_0000") != 0 or data.lookup("不存在") != -1:
    fail("lookup 被改坏了")
print("PASS: scale 修好了，lookup 没动坏")
sys.exit(0)
