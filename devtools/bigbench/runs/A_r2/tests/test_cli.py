import contextlib
import io
import os
import tempfile
import unittest

from app import cli, model, store


class CliTest(unittest.TestCase):
    def test_parse_args_status_and_keyword(self):
        opts = cli.parse_args(["t.json", "--status", "done", "--keyword", "tag"])
        self.assertEqual(opts["file"], "t.json")
        self.assertEqual(opts["status"], "done")
        self.assertEqual(opts["keyword"], "tag")

    def test_main_prints_filtered_summary(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "t.json")
            store.save(p, [
                model.make_task("做完的", status="done", size=1024),
                model.make_task("没做的", status="todo", size=0),
            ])
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                cli.main([p, "--status", "done"])
        out = buf.getvalue()
        self.assertIn("共 1 个任务", out)
        self.assertIn("完成 1 个", out)
        self.assertIn("1 KB", out)
