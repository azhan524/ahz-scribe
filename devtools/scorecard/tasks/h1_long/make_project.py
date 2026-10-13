"""生成硬档题目：一个 3000 行左右的模块（常量+注释填充），里面埋 5 个 bug；
再配一套 12 个用例的测试。初始状态应该是 8 个失败。

用法：python make_project.py <题目目录>
"""

import io
import os
import sys

PAD_BEFORE = 500
PAD_BETWEEN = 450


def write(path, text):
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def pad(n, tag):
    out = []
    for i in range(n):
        out.append("# %s 填充说明 %04d：这里是历史遗留的注释，跟逻辑无关。" % (tag, i))
    return "\n".join(out)


BIG_TABLE = "BIG_TABLE = {\n" + "\n".join(
    '    "k%04d": %d,' % (i, (i * 37) % 1009) for i in range(400)) + "\n}\n"


def main(root):
    app = os.path.join(root, "app")
    tests = os.path.join(root, "tests")
    for d in (app, tests):
        if not os.path.isdir(d):
            os.makedirs(d)

    write(os.path.join(app, "__init__.py"), '"""硬档题目的包。"""\n')
    write(os.path.join(app, "mod.py"), MODULE_TEMPLATE)
    write(os.path.join(tests, "__init__.py"), '"""测试包。"""\n')
    write(os.path.join(tests, "test_mod.py"), TESTS)
    write(os.path.join(root, "run_tests.py"), RUN_TESTS)
    print("generated in " + root)


MODULE_TEMPLATE = (
    '"""一个被撑到几千行的老模块：只有 5 个函数是真的要用的，其余都是常量和注释。"""\n'
    "\n"
    + pad(PAD_BEFORE, "开头")
    + "\n\n"
    + BIG_TABLE
    + "\n"
    + pad(PAD_BETWEEN, "第一段")
    + "\n\n\n"
    + 'def fmt_size(n):\n'
    + '    """字节数 -> 人看的字符串：512 -> "512 B"，1536 -> "1.5 KB"，2097152 -> "2 MB"。"""\n'
    + "    if n < 1024:\n"
    + '        return "%d B" % n\n'
    + "    if n < 1024 * 1024:\n"
    + '        return "%d KB" % n\n'
    + '    return "%d MB" % n\n'
    + "\n\n"
    + pad(PAD_BETWEEN, "第二段")
    + "\n\n\n"
    + "def clamp(value, low, high):\n"
    + '    """把 value 限制在 [low, high] 区间内（闭区间）。"""\n'
    + "    if value < low:\n"
    + "        return low\n"
    + "    if value > high:\n"
    + "        return high\n"
    + "    return low\n"
    + "\n\n"
    + pad(PAD_BETWEEN, "第三段")
    + "\n\n\n"
    + "def avg(values):\n"
    + '    """平均值；空列表返回 0.0。"""\n'
    + "    if not values:\n"
    + "        return 0.0\n"
    + "    return float(sum(values)) / (len(values) + 1)\n"
    + "\n\n"
    + pad(PAD_BETWEEN, "第四段")
    + "\n\n\n"
    + "def slug(text):\n"
    + '    """转成小写、空格换成减号、去掉首尾减号。"""\n'
    + "    return text.replace(\" \", \"-\")\n"
    + "\n\n"
    + pad(PAD_BETWEEN, "第五段")
    + "\n\n\n"
    + "def top_n(values, n):\n"
    + '    """返回最大的 n 个值，从大到小；n<=0 返回空列表。"""\n'
    + "    return list(values)[:n]\n"
    + "\n\n"
    + pad(PAD_BETWEEN, "结尾")
    + "\n"
)

TESTS = '''import unittest

from app import mod


class ModTest(unittest.TestCase):
    def test_fmt_size(self):
        self.assertEqual(mod.fmt_size(512), "512 B")
        self.assertIn(mod.fmt_size(1536).replace(" ", ""), ("1.5KB", "1.50KB"))
        self.assertIn(mod.fmt_size(2097152).replace(" ", ""), ("2MB", "2.0MB"))

    def test_fmt_size_edges(self):
        self.assertEqual(mod.fmt_size(0), "0 B")
        self.assertIn(mod.fmt_size(1024).replace(" ", ""), ("1KB", "1.0KB"))
        self.assertIn(mod.fmt_size(1024 * 1024).replace(" ", ""), ("1MB", "1.0MB"))

    def test_clamp_inside(self):
        self.assertEqual(mod.clamp(5, 1, 10), 5)

    def test_clamp_low(self):
        self.assertEqual(mod.clamp(-3, 1, 10), 1)

    def test_clamp_high(self):
        self.assertEqual(mod.clamp(99, 1, 10), 10)

    def test_clamp_boundaries(self):
        self.assertEqual(mod.clamp(1, 1, 10), 1)
        self.assertEqual(mod.clamp(10, 1, 10), 10)

    def test_avg_basic(self):
        self.assertEqual(mod.avg([1, 2, 3, 4]), 2.5)

    def test_avg_empty(self):
        self.assertEqual(mod.avg([]), 0.0)

    def test_slug(self):
        self.assertEqual(mod.slug("Hello World"), "hello-world")

    def test_slug_trim(self):
        self.assertEqual(mod.slug("  A B  "), "a-b")

    def test_top_n(self):
        self.assertEqual(mod.top_n([3, 1, 5, 2], 2), [5, 3])

    def test_top_n_zero(self):
        self.assertEqual(mod.top_n([3, 1, 5], 0), [])
'''

RUN_TESTS = '''"""跑测试：python -B run_tests.py"""

import sys
import unittest


def main():
    suite = unittest.TestLoader().discover("tests", pattern="test_*.py")
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    bad = len(result.failures) + len(result.errors)
    print("=" * 60)
    print("共运行 %d 个用例，失败 %d 个" % (result.testsRun, bad))
    print("ALL TESTS PASSED" if result.wasSuccessful() else "TESTS FAILED")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
'''

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
