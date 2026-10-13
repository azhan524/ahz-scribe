"""读配置。"""

import json
import os


class Config(object):

    def __init__(self, path):
        self.path = path
        self.data = {}

    def load(self):
        if not os.path.isfile(self.path):
            return {}
        with open(self.path, encoding="utf-8") as f:
            return json.load(f)

    def get(self, key, default=None):
        return self.data.get(key, default)
