"""跑测试：python -B run_tests.py"""

import sys
import unittest


def main():
    loader = unittest.TestLoader()
    suite = loader.discover("tests", pattern="test_*.py")
    runner = unittest.TextTestRunner(verbosity=2)
    result = runner.run(suite)
    tests = result.testsRun
    bad = len(result.failures) + len(result.errors)
    print("=" * 60)
    print(f"共运行 {tests} 个用例，失败 {bad} 个")
    print("ALL TESTS PASSED" if result.wasSuccessful() else "TESTS FAILED")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
