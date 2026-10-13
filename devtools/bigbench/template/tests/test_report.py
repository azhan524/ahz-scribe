import unittest

from app import model, report


class ReportTest(unittest.TestCase):
    def test_summary_counts(self):
        tasks = [
            model.make_task("a", status="done", size=0),
            model.make_task("b", status="todo", size=0),
            model.make_task("c", status="done", size=1024 * 1024),
            model.make_task("d", status="doing", size=0),
        ]
        info = report.summary(tasks)
        self.assertEqual(info["total"], 4)
        self.assertEqual(info["done"], 2)
        self.assertEqual(info["rate"], 50.0)
        self.assertEqual(info["total_size"], "1 MB")

    def test_summary_empty(self):
        info = report.summary([])
        self.assertEqual(info["total"], 0)
        self.assertEqual(info["rate"], 0.0)
        self.assertEqual(info["total_size"], "0 B")
