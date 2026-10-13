import unittest

from app import model, query


class QueryTest(unittest.TestCase):
    def setUp(self):
        self.tasks = [
            model.make_task("写文档", status="todo", notes="记得写 README"),
            model.make_task("修 bug", status="doing", notes="登录页的问题"),
            model.make_task("发版本", status="done", notes="打 tag 并写 changelog"),
        ]

    def test_filter_by_status_case_insensitive(self):
        self.assertEqual(len(query.filter_tasks(self.tasks, status="TODO")), 1)
        self.assertEqual(len(query.filter_tasks(self.tasks, status="done")), 1)
        self.assertEqual(len(query.filter_tasks(self.tasks, status="Doing")), 1)

    def test_filter_by_keyword_searches_notes_too(self):
        hit = query.filter_tasks(self.tasks, keyword="README")
        self.assertEqual([t["title"] for t in hit], ["写文档"])
        hit2 = query.filter_tasks(self.tasks, keyword="changelog")
        self.assertEqual([t["title"] for t in hit2], ["发版本"])

    def test_filter_combined(self):
        self.assertEqual(len(query.filter_tasks(self.tasks, status="done", keyword="tag")), 1)
