import unittest

from app import util


class UtilTest(unittest.TestCase):
    def test_format_size_units(self):
        self.assertEqual(util.format_size(512), "512 B")
        self.assertEqual(util.format_size(1536), "1.5 KB")
        self.assertEqual(util.format_size(2097152), "2 MB")

    def test_normalize(self):
        self.assertEqual(util.normalize("  a   b \t c "), "a b c")
