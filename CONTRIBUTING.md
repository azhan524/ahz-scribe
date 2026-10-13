# 参与贡献

先谢过。这个项目有几条硬约定，照着走能省很多来回。

## 先读这两份

- `AGENTS.md` —— 项目规矩（Codex / Ahz Scribe 会自动读进上下文）。里面「不要碰」那一节是红线。
- `README.md` —— 功能与用法。

## 开发环境

- Windows + 系统自带的 `csc.exe`（`%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`），**不需要装任何 SDK**。
- 语言级别固定 **C# 5**：不能用字符串插值（`$"..."`）、`?.`、`nameof`、表达式体成员等新语法。
- 源码是**单文件** `src/AhzScribe.cs`，不要把代码拆成多个文件。

## 改完必须跑

```cmd
cmd /c build.cmd
devtools\bench\run-ui-tests.cmd
```

- `build.cmd`：退出码 `0` = 窗口版和命令行版都更新了；`3` = 窗口版被占用、只产出了 `.new`（正常，但得先跑「升级.cmd」再重跑确认）。
- `run-ui-tests.cmd`：必须全绿（界面流式刷新 + 规矩/验证 + 设置页 + 真实控件树几何）。
- 动了 agent 行为、调度、提示词这类东西，**再跑一次** `devtools\scorecard\run-scorecard.ps1`，看 `devtools\scorecard\history.csv` 里与上次的分差——没有基线的改动不要合。

## 提交

- **小而准**：一个 PR 一件事，别顺手改风格或重构无关代码。
- 提交信息写清「改了什么、为什么、怎么验证」。
- 别提交 `config.json` / `sessions.json` / `*.exe` / 日志（`.gitignore` 已挡住，也别用 `-f` 绕过去）。

## 最容易踩的几条红线

- 对话区别退回「一整块 RichTextBox」渲染（每来一个 token 就整屏重排 = 闪屏抽搐）；`TranscriptView.Relayout()` 里的坐标补偿、`MsgBlock` 的增量排版（`_laidChars`/`_laidPieces`）都别删。
- 所有 HTTP 请求前先调 `Program.EnsureTls()`，否则连智谱这类平台会在 TLS 握手就失败（现象很像 API Key 不对）。
- 设置窗口的控件位置别只靠 `Anchor`：那个窗口会按 DPI 整体放大，用显式排版（`LayoutModelPage()` 之类），否则控件会被顶出可视区。
- 自绘控件里的尺寸统一走 `Theme.Px()`；**同一处不要再调 `Theme.ScaleBounds`**，那会按 DPI 放大两遍（1.5×1.5）。
