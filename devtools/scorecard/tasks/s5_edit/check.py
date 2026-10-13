"""自检：文件不存在时必须抛 FileNotFoundError（原来直接返回 {} 了）。"""

import os
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(ROOT, "app"))


def fail(msg):
    print("FAIL: " + msg)
    sys.exit(1)


import loader  # noqa: E402

cfg = loader.Config(os.path.join(ROOT, "根本不存在.json"))
try:
    cfg.load()
except FileNotFoundError:
    print("PASS: 缺文件时抛了 FileNotFoundError")
except Exception as e:
    fail("抛的不是 FileNotFoundError，而是 " + type(e).__name__ + "：" + str(e))
else:
    fail("缺文件时没有抛异常，还是返回了值")

# 文件存在时仍然要能正常读
ok_path = os.path.join(ROOT, "ok.json")
with open(ok_path, "w", encoding="utf-8") as f:
    f.write('{"a": 1}')
data = loader.Config(ok_path).load()
if data != {"a": 1}:
    fail("文件存在时读出来不对：" + repr(data))
print("PASS: 正常文件依然能读")
sys.exit(0)
