import unittest

from app import model


class ModelTest(unittest.TestCase):
    def test_priority_range(self):
        for p in range(1, 6):
            self.assertEqual(model.make_task("x", priority=p)["priority"], p)
        for bad in (0, 6, -1):
            with self.assertRaises(ValueError):
                model.make_task("x", priority=bad)

    def test_status_checked(self):
        self.assertEqual(model.make_task("x", status="doing")["status"], "doing")
        with self.assertRaises(ValueError):
            model.make_task("x", status="бред")
