import os
import tempfile
import unittest

from app import model, store


def sample():
    return [
        model.make_task("晚点的任务", created=300, size=1024),
        model.make_task("最早的任务", created=100, size=0),
        model.make_task("中间的任务", created=200, size=2048),
    ]


class StoreTest(unittest.TestCase):
    def test_roundtrip(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "t.json")
            store.save(p, sample())
            back = store.load(p)
        self.assertEqual([t["title"] for t in back], [t["title"] for t in sample()])
        self.assertEqual(back[0]["size"], 1024)

    def test_load_missing_file(self):
        self.assertEqual(store.load("根本不存在的文件.json"), [])

    def test_list_tasks_asc(self):
        titles = [t["title"] for t in store.list_tasks(sample())]
        self.assertEqual(titles, ["最早的任务", "中间的任务", "晚点的任务"])

    def test_list_tasks_desc(self):
        titles = [t["title"] for t in store.list_tasks(sample(), order="desc")]
        self.assertEqual(titles, ["晚点的任务", "中间的任务", "最早的任务"])
