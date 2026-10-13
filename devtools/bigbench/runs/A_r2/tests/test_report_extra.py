import unittest

from app import model, report


class ByPriorityTest(unittest.TestCase):
    """新功能：按优先级分组。现在 app/report.py 里的 by_priority 还没实现。"""

    def test_by_priority_groups(self):
        tasks = [
            model.make_task("a", priority=1),
            model.make_task("b", priority=3),
            model.make_task("c", priority=3),
        ]
        groups = report.by_priority(tasks)
        self.assertEqual(sorted(groups.keys()), [1, 3])
        self.assertEqual([t["title"] for t in groups[3]], ["b", "c"])
        self.assertEqual([t["title"] for t in groups[1]], ["a"])

    def test_by_priority_empty(self):
        self.assertEqual(report.by_priority([]), {})
