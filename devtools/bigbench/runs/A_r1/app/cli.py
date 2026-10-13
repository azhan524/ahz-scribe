"""命令行入口。"""

from . import query, report, store


def parse_args(argv):
    """支持：<清单文件> [--status STATUS] [--keyword WORD]"""
    opts = {"file": argv[0] if argv else "tasks.json", "status": None, "keyword": None}
    i = 1
    while i < len(argv):
        if argv[i] == "--status" and i + 1 < len(argv):
            opts["state"] = argv[i + 1]
            i += 2
        elif argv[i] == "--keyword" and i + 1 < len(argv):
            opts["keyword"] = argv[i + 1]
            i += 2
        else:
            i += 1
    return opts


def main(argv):
    opts = parse_args(argv)
    tasks = store.load(opts["file"])
    tasks = query.filter_tasks(tasks, status=opts["status"], keyword=opts["keyword"])
    info = report.summary(tasks)
    print(f"共 {info['total']} 个任务，完成 {info['done']} 个（{info['rate']}%），合计 {info['total_size']}")
    return 0
