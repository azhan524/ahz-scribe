# Ahz Scribe 项目规矩

> 这个文件会被自动读进系统提示；改完保存，下一轮对话立刻生效。
> `AGENTS.md` 是 Codex / DSH / 本项目的 Ahz Scribe **都认**的通用名字（AHZ.md 是它以前的名字，仍然兼容，同名时 AHZ.md 优先）。

## 验证命令

验证命令: cmd /c build.cmd

（改完源码后用它确认能编译通过。纯文档/脚本改动不用跑。）

## 手边这几件家伙（先看这里再动手）

- **编译写不进去**：`build.cmd` 不会整个失败了 —— 正式版被占用时它自动产出 `.new` 并报出占用进程的 PID。**但退出码会变成 3（`BUILD_RESULT: NEW_ONLY`）**：`0` 才是"两个正式版都更新了"。看到 3 就先跑「升级.cmd」再重跑一次确认。想知道谁锁着：`devtools\filelock.cmd <文件> [--kill]`。
- **改坏了想回滚**：每轮改完文件会自动落一份快照到 `.snapshots\`。`ahzscribe-cli.exe --cwd <目录> --snapshots` 列出，`--restore <id>` 恢复。**不要**再手工堆 `.bak`/`.bak2`。
- **找文件 / 看结构 / 看日志**：`glob`（支持 `**`，已自动跳过 `_build`/`.snapshots` 等噪声目录）、`outline`（主文件 466KB → 6KB，先看结构再决定读哪段）、`tail_file`（日志只看末尾）。别一层层 `list_dir`，也别为了找一个函数整读大文件。
- **验证 exe 里到底有没有某段字符串，别用 `ReadAllText(Unicode)`**：PE 文件长度常常是奇数，整体按 UTF-16 解一遍会**错位半个字节**，于是到处都"搜不到"，让人误判成"这版没编进去"（2026-10-13 实测踩到：`AhzScribe.exe` 明明有新措辞却被判 False，差点白改一次）。正确做法：把要找的串 `[Text.Encoding]::Unicode.GetBytes()` 成字节，在文件**字节流**里搜（对齐无关）。
- **改 UI 后别只靠截图下判断**：截图会被别的窗口盖住、也会被"还没合成"骗（实测反复踩）。用 `devtools\uiobserve.cmd tree|inside|popovers|shot` —— 它观测**真实控件树的几何**，不看像素；`inside` 断言控件不越界、`popovers` 断言浮层对位，这两条已接进 `run-ui-tests.cmd`。
- **任务清单**：模型写的 `- [ ] / - [x]` 会被抽出来，每步注入"还剩几步"、收尾做对账。所以让它用这个格式写计划，别用自由文本。
- **权限三档都真生效了**：`仅可查看` 会**真的拒绝** write_file/edit_file/run_shell（闸门在 `Tools.GuardReadOnly`，与"有没有点允许"无关）；`工作区内修改` 照旧逐条问；`完全权限` 不再问。所以调试写类工具时，先确认自己不在只读档。
- **整体覆盖有体量护栏**：`write_file` 覆盖已存在文件时，新内容比原文少 30% 以上且少了 200 字符以上会被**拒绝**（防"没写完就覆盖"，主文件 46 万字符）。真要大幅精简就显式传 `allow_shrink=true`；小改一律用 `edit_file`。
- **记分卡**：`run-scorecard.ps1` 跑完会自动归档到 `devtools\scorecard\history.csv` 并打印与上次的分差（**只有全量跑才归档，`-Only` 单题跑不写**——否则会算出假分差）；归档逻辑自己的自测在 `scorecard-history.ps1 -SelfTest`（秒级，不调模型）。注意护栏（G）**不进题数也不进总分**，单独一行展示 —— 它退化不会有分数变化，要看那一行。

## 项目约定

- **单文件源码**：所有 C# 代码都在 `src/AhzScribe.cs` 里，不要拆成多个文件。
- **C# 5 语法限制**：系统自带的 `csc.exe` 只支持到 C# 5——不能用字符串插值（`$"..."`）、`?.`、`nameof`、表达式体成员等新语法。
- **对话区渲染别改回 RichTextBox**：对话区和轨迹页都是 `TranscriptView`（一条消息一个 `MsgBlock` 控件 + 双缓冲 + 攒批 120ms）。不要退回"一整块 RichTextBox 渲染整段对话"——那样每来一个 token 都要整块重排、整屏上移，就是用户说的"闪屏抽搐"。改完 UI 跑 `devtools\bench\run-ui-tests.cmd`（界面流式 172 项 + 规矩 107 项 + 设置页 40 项 + 控件几何 2 组，条数以脚本实际输出为准）。
- **`TranscriptView.Relayout()` 里的坐标补偿别删**：`AutoScroll` 面板滚动后，子控件 `Top` 是客户区坐标，排版写的是内容坐标，必须 `Top = 内容y + AutoScrollPosition.Y`；删了就会出现"滚动范围越涨越大、内容自己弹到中间"。
- **`MsgBlock` 的增量排版（`_laidChars` / `_laidPieces`）别删**：它就是"只重排最后一段"的那套定稿点，删了长回答会退回每批 50ms 的 O(n²)。定稿点只能在"这一段不含未配对的 `**` / 反引号"时推进；换宽度/换字号要 `ResetLayout()`。
- **别再对超长字符串直接 `TextRenderer.MeasureText`**：文本超过测量框时返回的是被截断的错宽度，会让片段溢出。走 `MsgBlock.Fitting()`（先估上限再二分）。
- **设置窗口的控件位置别只靠 `Anchor`**：这个窗口会按 DPI 整体放大（`ApplyDpiScale`），带 `Anchor=Bottom/Right` 的控件会被推出可视区（曾经把「＋ 添加」「从接口拉取模型」顶到 y=1700、用户根本点不到）。位置一律走 `LayoutModelPage()` / `LayoutOtherPages()` 这种"按页面实际尺寸算"的显式排版，并且 `devtools\bench\settings-unittest.cs` 会检查三页所有控件都在可视区内。
- **`Theme.ScaleBounds` 不要递归进 `FieldBox`**：`FieldBox` 自己会按容器尺寸摆内部输入框，递归放大等于乘两次（1.5×1.5），输入框会比外框还宽、文字还偏下（用户说的"密钥跑出框外 / 内容有点歪"）。DPI 放大后调一次 `RepositionFields(this)` 收尾。
- **所有 HTTP 请求前先 `Program.EnsureTls()`**：.NET 默认只开 SSL3/TLS1.0，智谱等平台只收 TLS1.2+，不打开就会报"未能创建 SSL/TLS 安全通道"（看着像 Key 不对）。别把这个调用删了。
- **对话区"能选中、能复制"别弄丢**：`MsgBlock.SrcAt` / `CharAt` 是拖选基础，每条消息底部的操作行（复制 / 分支 / 时间）靠 `ActionAt` + `CopyIconRect()`/`BranchIconRect()` 命中；分支靠 `MsgBlock.MsgIdx` 找切点，时间靠消息里的 `ts`。
- **界面相关**：颜色/字体统一走 `Theme`；自绘控件在 `WinForms` 里跑，改完最好确认 `AhzScribe.exe` 能启动。
- **不要碰**：`AhzScribe.exe` / `ahzscribe-cli.exe` 是编译产物，别手改；`sessions.json`、`config.json` 是运行时数据。
- **改了源码要重新编译**：`build.cmd` 会同时产出窗口版和命令行版两个 exe。
- **exe 正在运行时 csc 写不进去**：报「未能写入输出文件…拒绝访问」就是程序没关，先让用户退出程序再编译，或临时输出成 `AhzScribe.exe.new` 交给 `升级.cmd`。
- **评测优先于改造**：动 agent 行为/调度之前，先跑一遍记分卡拿基线（`devtools\scorecard\run-scorecard.ps1`），改完用同一套剧本复跑对比；具体见 `devtools\极限测试报告-2026-10-11.md`。数据不支持的改动不要做。
- **记分卡基线（2026-10-11 实测）**：云端 deepseek-flash 91.5/100，本地 qwen3.5:4b 60.5/100；本地失败主因是「只读不写」（读完文件就收工）和 6.4 tok/s 吞吐不足，不是框架缺陷（护栏 8/8 双方都过）。
- **交接文档别写死数字**：`src/AhzScribe.cs` 行数、自测条数等以实际 wc/脚本输出为准（可写「约」），避免文档很快过期误导后人。
- 回答用中文；改动尽量小而准，别顺手大改风格。
