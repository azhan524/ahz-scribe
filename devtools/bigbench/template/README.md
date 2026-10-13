# 任务清单小工具

- `app/` 里是源码：`util.py`、`model.py`、`store.py`、`query.py`、`report.py`、`export.py`、`cli.py`
- `tests/` 里是测试：`python -B run_tests.py` 会跑全部用例

约定：

- 状态只有 `todo` / `doing` / `done` 三种，源码里统一用小写
- `priority` 是 1..5 的整数
- `report.summary()` 的 `total_size` 用 `util.format_size()` 换算
- `report.by_priority()` 是新加的接口，测试已经写好
