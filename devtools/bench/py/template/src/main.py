"""列出目录里的文件（按大小从大到小）。"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from util import format_size, summary
from scanner import scan


def main(argv):
    dir_path = argv[1] if len(argv) > 1 else "."
    files = scan(dir_path)
    print(summary(dir_path))
    for path, size in files:
        print(f"{format_size(size)}  {os.path.basename(path)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
