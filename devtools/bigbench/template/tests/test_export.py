import unittest

from app import export, model


class ExportTest(unittest.TestCase):
    def test_csv_quotes_fields_with_comma(self):
        tasks = [
            model.make_task("普通任务", priority=2, status="todo"),
            model.make_task("带,逗号的任务", priority=5, status="done"),
        ]
        text = export.to_csv(tasks)
        lines = text.strip().split("\n")
        self.assertEqual(lines[0], "title,priority,status")
        self.assertEqual(lines[1], "普通任务,2,todo")
        self.assertEqual(lines[2], '"带,逗号的任务",5,done')

    def test_csv_ends_with_newline(self):
        self.assertTrue(export.to_csv([]).endswith("\n"))
