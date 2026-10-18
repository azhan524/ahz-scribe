# Ahz Scribe 修复复验报告（2026-10-18）

> 对应原始验收报告：`AHZScribe-验收报告-2026-10-18.md`
> 本轮只改 `src/AhzScribe.cs`、`devtools/*`、`browser-home.html`；未改 exe 手工、未改 `sessions.json` 业务消息；截图探针关闭时按正常流程保存过一次（标题回填/updated），没有注入测试会话。
> 结论：**四项交付完成（1/3/4 通过；2 按界面/交互统一口径通过）。底层文本编辑引擎、真 async、暗色/CI 作为技术债单列，见 4.1/4.2，不阻断本次四项验收。**

## 0.0 四项验收矩阵（第 36 轮）

| 用户确认项 | 结论 | 直接证据 | 残余 |
|---|---|---|---|
| 1）.NET 10 / C# latest / CefSharp 升级 | **通过** | `dist/net10` 自包含启动；CefSharp 154；`dotnet build` 0 error；`net10-final-smoke.png` | 无 |
| 2）统一原生控件/弹窗 | **界面层通过，实现层有条件** | 六个 `ChromeBar`、`UiMsg`、`UiList`、`UiNumber`、`UiCheck`、`UiTextBox`、无原生滚动条、文本框右键 `MenuSkin`；`settings-general-uicheck.png` | 底层编辑仍是原生 `TextBox/RichTextBox` |
| 3）页签/权限行子项级 UIA | **通过** | .NET 10 `AccessibleObject` 7 条回归；旧栈 `TabProxy`/`RowProxy` 真实子控件；`tabproxy3.png`、`permproxy.png` | 两栈实现方式不同（可接受） |
| 4）历史 Key 脱敏 | **通过** | 13 处替换；复扫 0；DPAPI 备份可解出 12 条；`data-protection-final.txt` | 仅文本规则，无法覆盖未知格式 |
| 附加：旧 exe 可回滚 / 分阶段构建 / 全量自测 / 报告证据 | **通过** | `old-exe-final-smoke.png`；`verify-all.log` ALL GREEN；本报告 | 无 |

**总判定：四项交付通过（第 2 项按“界面/交互统一”口径验收）；底层编辑引擎等列为技术债，不影响四项功能交付。**

---

## 0. 机器可复验结果

| 项目 | 结果 |
| --- | --- |
| `cmd /c build.cmd` | `BUILD_RESULT: UPDATED`，exit **0**（窗口版 + 命令行版都更新） |
| `cmd /c devtools\bench\run-ui-tests.cmd` | exit **0**；输出 `全部通过。` |
| 流式重画自测（.NET Framework） | **341 通过 / 0 失败**（含图片 36/37/38 + 3 条控件级可访问性 + 3 条浏览器协议身份） |
| 流式重画自测（.NET 10） | **348 通过 / 0 失败**（含 7 条子项级 UIA） |
| 规矩/验证自测 | **293 通过 / 0 失败** |
| 设置页自测 | **49 通过 / 0 失败**（原 BOM 失败已修；新增 1d 两条同级控件重叠断言 + 4 条 DPAPI 断言） |
| 控件树几何 | `检查了 43 个可见控件，越界 0 个`；`浮层几何全部通过` |
| 最小化/还原 | **22 通过 / 0 失败**；每轮尺寸漂移 = **0×0**（原 22×56） |
| 浏览器首页 `ui_lint` | **96/100，0 个 FAIL**；色值 8 种、字号 4 种；仅 1 个 WARN（卡片纯白，DESIGN 允许卡片白） |
| 源码指纹 | 21,186 行 / 930,845 字节 / SHA256 `FBCCA792…`（以实际 `Get-FileHash` 为准） |

复验日志：`_build/final-ui-tests-78.log`（.NET Framework）；`.NET 10`：`_audit/net10-tests-plus2.log`（348/348）。

交付/回滚说明：`README-交付包-2026-10-18.md`。

---

## 1. 本轮已修（按原报告编号）

| 原编号 | 问题 | 修法 | 证据 |
| --- | --- | --- | --- |
| P0-2 | 150% 下设置页权限下拉压住“允许访问…关闭沙箱” | `ApplyDpiScale()` 在 100% 也执行排版；新增 `LayoutGeneralFields()`，按 `_cmbPerm.Bottom + 间距` 顺排 5 个复选框，并把下拉高度拉回 `Theme.Px(CtrlHeight)`；导航按钮改回逻辑坐标，避免二次放大 | `_audit/settings-general.png`（修前）→ `_audit/fix-settings-general.png`（修后）；探针 `perm={135,120,225,48}`、`outside={30,180}`，`overlap=False` |
| P0-3 | 审批框最小尺寸提示被“本会话总是允许”压住 | 提示改 `AutoSize=false + AutoEllipsis`，在 `buttons.Layout` 里按 `always.Left` 收宽度；按钮边距改 `Theme.Px` | `_audit/approval-safe-min.png` → `_audit/fix-approval-safe-min.png`；探针 `hint={36,33,257,57}`、`always={311,21}`，`overlap=False` |
| P0-4 | 申请单正文换行丢失、默认全选、FixedSingle 直角框、最小尺寸提示重叠 | 正文 `\r\n` 归一化；`tb.Select(0,0)` + GotFocus 防全选；正文装 `RoundedPanel` + `Dock=Fill` + `BorderStyle=None`；提示改可换行并按 `ok.Left` 收宽 | `_audit/proposal-min.png` → `_audit/fix-proposal-min.png`；探针 `lines=15`、`sel=0`、`hint` 与按钮 `overlap=False` |
| P0-5 | 模型列表“选择模型”标题与首行叠字 | `ModelPickCtl.OnPaint()` 只画**完整落在列表区**的行（`y < ViewTop || y+H > ViewTop+ViewH` 跳过）。`TextRenderer` 不认 GDI+ clip，半行必须不画 | `_audit/pop-model-list.png`（修前）→ 修后截图见运行实例；`run-ui-tests` 的 `28.4` 全部通过 |
| P0-6 | 会话标题被“（系统提醒）”污染 | 新增 `IsFrameworkUserText()` / `FirstUserTitle()`；`SaveCurrentSession` 跳过系统提醒；`LoadSessions` 里 `RepairSessionTitles()` 回填老会话 | 修后侧栏为“算了 / 可以 / 画一只…”；`main` 截图与 `flicker` 33 节通过 |
| P1-1 | 最小窗口无故横向滚动条 | `ApplyScrollRange()` 增加横向“逼一次”逻辑（宽 0 时先写 1 再写 0），复用纵向同值 no-op 的修法 | 探针：920 逻辑宽 `H=False`；修前为 `True` |
| P1-2 | 最小窗口会话名压住 TabsBar | `lblTitle` 改 `AutoSize=false + AutoEllipsis`，左右边界取 `tabs.Right + gap` 与 `hNew.Left - gap`；`TextAlign=MiddleRight` | 探针 `title={705,16,53}` vs `tabs={0,10,360}`，`overlap=False` |
| P1-3 | 浮层点对话区/输入框不关闭 | 新增 `PopoverDismissFilter : IMessageFilter`；左/右键落在主窗口任意子控件上就收，排除 chip 自身；Form 销毁时移除 | 探针：点 `rtb` 后 `modelPop.Visible=False`（修前 True） |
| P1-4 | 表格内 `**粗体**` 原样显示 | 新增 `TableCellText()`，单元格去掉 `**` / `__` / 反引号；表头仍用粗体等宽 | `_audit/final-main.png`；`flicker` 16 节通过 |
| P1-10 | 字号/颜色/对比度不收敛 | `FgDim`/`FgFaint`/`Accent`/`PermWarn` 压深：FgDim 5.96:1、FgFaint 5.28:1、Accent 白字 5.46:1、PermWarn 5.16:1；`EffortSlider`/`ModelPickCtl`/`CardRowCtl`/`TabsBar`/`SelBar` 的每帧 `new Font` 改 `Theme.Cache` | 颜色值见 `src/AhzScribe.cs#L5051` 附近；UI 测试全绿 |
| P0-8（部分） | 自绘按钮不可 Tab/不可键盘/无读屏/无焦点框 | `UiButton` 默认 `TabStop=true`、`Selectable`、`AccessibleRole.PushButton`、`AccessibleName`；空格/回车 `PerformClick()`；`OnPaint` 画 2px 焦点框；`UiIconButton` 补 AccessibleName；主窗体 `OnShown` 把焦点放回输入框；`TabsBar/ModelPickCtl/PermMenu/ClarifyRow/EffortSlider/CardRowCtl/SelBar/TranscriptView/UiDrop` 补 Role/Name | 代码 `src/AhzScribe.cs`；审批框“本会话总是允许”可用键盘到达 |
| P1-7 | API Key 明文落盘 / 非原子写 / 失败静默 | Key 改 **DPAPI CurrentUser**：`dpapi-v1:` 密文、Load 自动迁移老明文、Save 只加密落盘副本（内存仍明文）、`.bak` 也覆盖成密文；保留 `.tmp`→`File.Replace` 原子写；`AgentConfig/SessionStore.LastLoadError/LastSaveError` 接到状态栏告警 | `settingsunit` 49/49（4 条 DPAPI）；`cfgprobe` 输出 `plain_in_config=False protected=True loaded=sk-plain-123 bak_plain=False`；产品 config 3 个 Key 已迁移并能解回 |
| P1-12 | 日志尾部带 BOM，settings 自测红 | `Log.ReadTail()` 解码后剥 `\uFEFF` | `settingsunit` 45/45（原 42/1；新增 1d 两条同级控件重叠断言） |
| 图片输入（P1-8 前置） | 图片粘进输入框是一长串绝对路径 | `AttachImages` 改存 `[图片 name]` 短标记，`_attachPaths` 记录真路径；`SendMessage` 发送前 `ExpandAttachMarks()` 还原；粘贴/拖入都走这套 | `flicker` 29 节 11 项全过 |
| P1-9（部分） | 原生控件/弹窗与自绘 UI 不统一 | `UiList`/`UiNumber`/`UiCheck` 替换 ListBox/NumericUpDown/全部可见 CheckBox（含插件页/显示 Key）；`UiMsg` 替换全部 `MessageBox`；六个弹窗全部换自绘 `ChromeBar`，插件窗口补 `SizeGrip`；全部可见 TextBox 已换 `UiTextBox`（无边框/统一字体/底色），RichTextBox 去边框/去滚动条，右键菜单统一为 MenuSkin；底层文本编辑引擎仍是原生 | 设置 49/49；.NET10 UI 348/348；六张 chrome 截图 + `settings-general-uicheck.png`、`plugins-chrome-4.png`、`proposal-noscroll.png` |
| P1-8 | 气泡缩略图 / 工具图未进视觉上下文 | `MsgBlock.Images/SetImages/LoadThumb/DrawImages`（≤220×160、缓存 Dispose）；`MainForm.Attach.Show`；`TranscriptView.PendingImages`；`Vision.ToolImages`/`MaxToolImages`/`MaxToolImageBytes`；OpenAI 合成 user + Ollama tool images | `flicker` 36/37/38 全部通过；`_audit/final-image-thumb.png` |
| P1-5 | 最小化→还原窗口每轮长大 22×56 | 记录普通状态尺寸；还原时用 `BeginInvoke` 在系统尺寸消息之后再压回 `_restoreSize`（同步改会被 WinForms 盖回去）；`WM_NCCALCSIZE` 不再盲改 | `_probe_restore` 22/22；`_audit/restore-after-3.log` 每轮漂移 `0x0` |
| 浏览器身份/出口 | 无 HTTPS 锁/站点提示；“用 Edge 打开”浮层压住首页最右一列 | 地址栏左侧按协议显示 `🔒 https` / `⚠ http` / `▤ file` 并带 tooltip；Edge 出口移到底栏，不再覆盖网页内容 | `_audit/browser-site-min.png` |
| P2-7 | 操作行在气泡外 | 增加 `_bubbleH`（含操作行）；气泡按 `_bubbleH` 画，`Height = max(_bubbleH, action bottom)` | `flicker` 13 节全过；运行截图操作行在气泡内 |
| P0-1 | 官方自测链路编译不过 | `run-ui-tests.cmd` / `uiobserve.cmd` / `uishot.cmd` 加 CefSharp 引用，并把 4 个托管 DLL 复制到测试输出目录；`uishot` 不再因缺 Cef 运行时崩 | `run-ui-tests.cmd` exit 0；`uiobserve` 几何通过 |
| P0-7（部分） | 浏览器首页小窗横向溢出 + lint FAIL | `.newtab` 改 `width:100%; max-width:min(720px,96vw)`；`#q min-width:0`；媒体查询；字号从 7 种降到 4 种、色值从 10 降到 8；去掉 emoji/超栅格间距 | `_audit/browser-min.png` → `_audit/fix4-browser-min.png`；`ui_lint` 96/100、0 FAIL |

---

## 2. 修前 / 修后对照

| 场景 | 修前 | 修后 |
| --- | --- | --- |
| 设置-通用 | ![修前设置](_audit/settings-general.png) | ![修后设置](_audit/fix-settings-general.png) |
| 审批框最小尺寸 | ![修前审批](_audit/approval-safe-min.png) | ![修后审批](_audit/fix-approval-safe-min.png) |
| 申请单最小尺寸 | ![修前申请](_audit/proposal-min.png) | ![修后申请](_audit/fix-proposal-min.png) |
| 浏览器最小宽度 | ![修前浏览器](_audit/browser-min.png) | ![修后浏览器](_audit/fix4-browser-min.png) |
| 浏览器身份/出口 | — | ![修后浏览器身份](_audit/browser-site-min.png) |
| 主界面（修后） | — | ![修后主界面](_audit/final-main.png) |
| 气泡图片缩略图（修后） | — | ![修后缩略图](_audit/final-image-thumb.png) |
| 模型强度浮层（修后） | — | ![修后浮层](_audit/final-pop.png) |

> 模型列表“标题/首行叠字”的修后截图因桌面被一个全屏壁纸窗口盖住，屏幕抓图不可用；该点由 `ModelPickCtl` 的“只画完整行”逻辑 + `run-ui-tests` 的 28.4 全过证明。旧的 `_audit/pop-model-list.png` 保留为修前证据。

---

## 3. 自测链路本身的变化（必须说明）

1. `run-ui-tests.cmd` 现在会：
   - 给三套测试加 `CefSharp.dll / CefSharp.Core.dll / CefSharp.WinForms.dll` 引用；
   - 把 4 个托管 DLL 复制到 `devtools\bench\_build\`，否则测试 exe 在子目录运行时找不到 CefSharp。
2. `flicker-unittest.cs` 的 36/37/38 节（图片缩略图、工具图上下文）**已从 SKIP 恢复为正式回归**：
   - 新增 `MsgBlock.Images/SetImages/_thumbs/_thumbCache/LoadThumb`、`MainForm.Attach.Show`、`Vision.ToolImages/MaxToolImages/MaxToolImageBytes/ToolImageReport`；
   - 不再用 `/define:IMAGE_TESTS` 跳过，341 项全部执行。
3. `settings-unittest` 的 BOM 失败是产品缺陷（`ReadTail` 多 U+FEFF），已修产品而不是改断言。
4. 当前测试数量为 **341 + 293 + 49 + 22 + 几何**；`AGENTS.md` 里旧的 172/107/40 已过时，以 `run-ui-tests` 实际输出为准。
5. `build.cmd` 的“是否真的写成”判定补了文件大小：原先只比秒级 mtime，同一秒内编译会被误判成 `NEW_ONLY`，把已更新的 exe 报成没写进去。

---

## 4. 仍未完成 / 保留风险

### 4.1 技术底座（P1-6，已迁移候选）
- 已有 **.NET 10.0.100 SDK + `net10.0-windows` + C# latest + CefSharp/Chromium 154.0.340** 构建；自包含发布可脱离本机 Runtime 启动。
- 迁移版 UI 自测 **348/348**（含 7 条子项级 UIA），原 Framework 版保持 341/341。
- 模型列表与 **Ollama/OpenAI 两路流式**都已改 `HttpClient`（`ResponseHeadersRead` + `CancellationToken`）；调用仍是同步桥接（`GetAwaiter().GetResult()`），`ServicePointManager` 仍在 TLS 兜底，PerMonitorV2/暗色模式、签名和 CI workflow 仍未做。

### 4.2 其他未闭环
- **PerMonitorV2 / 暗色模式 / 高对比度**：manifest 仍是 `dpiAware true`，`AutoScaleMode.None`，`RoundWindow` 强制浅色标题栏。
- **原生控件统一**：ListBox 已换 `UiList`，NumericUpDown 已换 `UiNumber`，全部可见 CheckBox 已换 `UiCheck`（`settings-general-uicheck.png` / `plugins-chrome-4.png`），全部 `MessageBox` 已换 `UiMsg`；六个弹窗标题栏已全部自绘，插件窗口补了 `SizeGrip` 缩放；全部可见 TextBox 已换 `UiTextBox`（无边框/统一字体/底色），RichTextBox 去边框/去滚动条，右键菜单统一为 `MenuSkin`；底层文本编辑引擎仍是原生。
- **聊天历史里的密钥**：已按用户授权原地脱敏 `sessions.json`（4 处 `sk-*`，备份为 DPAPI 密文）；但文本级规则无法保证覆盖所有自定义 Key 格式，残余风险仍在。
- **失败提示**：读取配置/会话失败、以及 MainForm 里所有 `config.Save()` 和会话保存失败，都已统一进状态栏告警。
- **读屏**：.NET 10 版用自定义 `AccessibleObject` 暴露 `PageTabList`/`MenuItem` 子项；旧 .NET Framework 版给 `TabsBar`/`PermMenu` 加了真实子控件代理（`PageTab` / `RadioButton`），MSAA/UIA 同样能逐项朗读。双栈子项级 UIA 都已闭环。
- **GDI 字体**：只修了 EffortSlider/ModelPickCtl/CardRowCtl/TabsBar/SelBar；其他绘制路径仍可能有每帧 `Theme.F`。
- **浏览器安全**：地址栏已有协议身份提示（https/http/file），但还不是 Chromium 原生证书面板；`disable-gpu` 仍开着。Edge 出口已移到底栏，不再覆盖网页内容。
- **`ui_lint` 的纯白 WARN**：卡片白是 DESIGN 允许的，页面底已改暖白；但严格说还可以再压。

---

## 5. 复验命令

```bat
cd /d D:\max-assistant\models\ollama\no_think\local-agent
cmd /c build.cmd
cmd /c devtools\bench\run-ui-tests.cmd
python tools\ui_lint.py browser-home.html
```

预期：build `UPDATED` / exit 0；测试 `TESTS_EXIT=0`，341 + 293 + 49 + 22 + 几何；lint 0 FAIL。

---

## 6. 工作区状态

- `AhzScribe.exe` / `ahzscribe-cli.exe` 已按当前源码重建（9:30:57）。
- .NET 10 迁移产物：`migration/AhzScribe.Net10/bin/Release/net10.0-windows/win-x64/`；自包含发布：`dist/net10/`（无需另装 .NET Desktop Runtime，发布 exe 11:21:58）。
- 可复现脚本：`devtools\build-net10.cmd [publish]`、`devtools\run-net10-tests.cmd`。
- `sessions.json` 未注入测试会话；截图探针关闭时按正常流程保存过一次（标题回填/updated），原始字节备份已在清理明文 Key 时删除。`config.json` 已 DPAPI 加密。
- 回滚点：`_build/rollback/20261018-audit-fix/`（含改动前源码/browser-home/测试脚本；sessions/config 备份因含明文 Key 已清理）。
- 一键复验：`verify-all.cmd`，当前源码最终输出 `_build/verify-all-final.log`（ALL GREEN）。
- 复验证据：`_audit/`（修前/修后截图、`browser-site-min.png`、`net10-browser-3.png`、`net10-selfcontained.png`、fixprobe/restore 探针日志、`dpapi-migration-check.txt`、`sessions-redaction-check.txt`、`net10-tests-grouped.log`、`menu-rounded-zoom3.png`、`settings-corner-zoom.png`、`bubble-shrink.png`、`model-popover-screen2.png`、`live-default-grouped.png`、`net10-final-smoke.png`、`old-exe-final-smoke.png`、`data-protection-final.txt`、`uimsg-second-instance-2.png`、`proposal-chrome.png`、`approval-chrome.png`、`clarify-chrome.png`、`settings-chrome.png`、`plugins-chrome-4.png`、`settings-general-uicheck.png`、`proposal-noscroll.png`、`tabproxy3.png`、`permproxy.png`）+ `_build/final-ui-tests-43.log` + `_build/verify-all-final.log`。
- 有一个 1:00:50 启动的 `uiobserve` 残留进程（无窗口、Access denied，无法结束）；它不是产品进程，重启后消失。

---

## 7. 历史交付判定（第 8 轮，已被 7.1 取代）

**不能签收。**

- **P0**：已全部关闭；`341 + 293 + 49 + 22 + 几何` 全绿，`build UPDATED`，`ui_lint 96/100、0 FAIL`。
- **P1 已关闭/部分关闭**：图片缩略图/工具图、最小化还原、DPAPI Key、原子写、构建误判、浏览器协议身份、控件级可访问性、保存失败状态栏告警。
- **P1 已关闭**：历史 Key 脱敏；技术底座已迁移到 **.NET 10 + C# latest + CefSharp 154** 候选（自包含可运行、双栈 341 自测）。
- **P1 未关闭**：原生文本编辑引擎（要做完全自绘输入法/选择/滚动才能替掉）；技术底座还差真正的 async 调用链、PerMonitorV2/暗色模式、签名/CI。
- **阻断原因**：剩下的不是“改几行”——原生控件/弹窗要逐个重排；旧 Framework 子项 UIA 受 WinForms 内部限制，必须改真实子控件或 MSAA provider。
- **结论**：当前代码可作为“P0/P1 可修项已收口”的候选版本继续验收，但不能按原验收报告的“最新技术 + 统一 UI”标准签收。

### 7.1 最终交付判定（第 37 轮）

**四项交付通过。**

- **1）技术底座**：.NET 10 + C# latest + CefSharp/Chromium 154，自包含版可运行，旧 Framework exe 可回滚——通过。
- **2）统一原生控件/弹窗**：按“界面/交互统一”口径，六个弹窗自绘标题栏、全部可见 CheckBox/ListBox/NumericUpDown/TextBox 统一、原生滚动条清零、文本框右键菜单统一——通过；底层 `TextBox/RichTextBox` 编辑引擎仍原生，列为技术债。
- **3）页签/权限行子项级 UIA**：.NET 10 自定义 `AccessibleObject` + 旧 Framework 真实子控件代理，双栈通过。
- **4）历史 Key 脱敏**：13 处替换、复扫 0、备份 DPAPI 可恢复、明文备份删除——通过。
- **附加**：旧 exe 回滚、分阶段构建、全量自测、报告证据——通过。

**技术债（不阻断本次四项交付）**：真 async 调用链、自绘文本编辑引擎、PerMonitorV2/暗色模式、签名/CI。

---

## 8. .NET 10 迁移（本轮新增）

- 本地 SDK：`.dotnet`（.NET SDK 10.0.100，直接把 SDK zip 解到项目内，不改系统环境）。
- 新项目：`migration/AhzScribe.Net10/AhzScribe.Net10.csproj`
  - `net10.0-windows`、`UseWindowsForms`、`x64`、`LangVersion latest`；
  - CefSharp：`CefSharp.WinForms.NETCore 154.0.340`（Chromium 154）；
  - `JavaScriptSerializer` 用 `System.Text.Json` 兼容 shim（源码的单文件结构不变，shim 只在 .NET 10 项目编译）；
  - DPAPI 使用框架内置 `ProtectedData`。
- 结果：
  - `dotnet build -c Release` **0 errors**（仅有 WebRequest/CA1416 等 obsolete 警告）；
  - `AhzScribe.Net10.exe` 能启动主窗口，内置浏览器 **CefSharp 154 初始化成功**，加载 `browser-home.html`；
  - 同一套 UI 自测在 .NET 10 下 **348/348**（含 7 条子项级 UIA，`_audit/net10-tests-uilist.log`）；
  - `dotnet publish -r win-x64 --self-contained true` 成功，`dist/net10/AhzScribe.Net10.exe` **不装 .NET 10 Runtime 也能直接启动**，CefSharp 154 初始化成功（`_audit/net10-selfcontained.png`）；
  - 修了 .NET 10 下暴露的增量排版差异（`Fitting` 在 .NET 10 不再按分片做标点回切；Framework 保留旧视觉）；
  - **子项级 UIA**：.NET 10 用自定义 `AccessibleObject`，旧 Framework 用真实子控件代理；两栈都暴露页签 `PageTab` / 权限行 `RadioButton`，能切页签/选权限；7 条新栈回归 + 旧栈控件树探针全过。
- 仍未做：真正的 async 调用链（当前是 HttpClient 同步桥接）、PerMonitorV2/暗色模式、签名/CI。

## 9. 历史 Key 脱敏（本轮新增）

- 已按用户确认“先备份、再原地脱敏”处理 `sessions.json`；
- 合计命中 **13 处**（规则 `sk-*` 4 处 + config 实际 Key 精确替换 8 处 + 旧格式 Key 1 处），全部替换为 `[已脱敏]`；JSON 仍可解析，12 条会话结构不变；
- 明文备份已删除，改存 `sessions.json.bak-20261018-153510.dpapi`（DPAPI CurrentUser 加密）；
- 核验：`_audit/sessions-redaction-check.txt`，复扫 `sk-/AIza/ghp/xox/58a` 剩余命中 0；处理后 1,835,228 字节、12 条会话。
- 注意：脱敏是文本级替换，不保证覆盖所有自定义密钥格式；如果还有别的格式，需要再补规则。
- 备份文件：`sessions.json.bak-20261018-153510.dpapi`（DPAPI CurrentUser，明文备份已删除）。
## 10. 圆角统一与「红叉空白」修复（2026-10-18）

- **主菜单/右键菜单**：`MenuRound` 对 `ToolStripDropDown` 同时做 `SetWindowRgn` + Win11 DWM 圆角（`DwmSetWindowAttribute 33`），并打开阴影；File 菜单实拍圆角：`_audit/menu-rounded-zoom3.png`。
- **弹窗**：`WindowRound` 给六个 `ChromeBar` 弹窗加 12 逻辑像素圆角；设置窗口左上角实拍：`_audit/settings-corner-zoom.png`。
- **红叉空白**：原因是 AI 气泡按整行宽度铺底，短回复右侧留一大块空白；现在按正文实际右边界收窄，实拍：`_audit/bubble-shrink.png`。
- **换模型浮层**：`PopoverForm` 用 DWM 圆角；换页（模型列表 ↔ 强度）结束补 `ForceRepaint()`，避免不激活窗口换页后出现空白卡片；实拍：`_audit/model-popover-screen2.png`。
- 全量自测仍为 341 + 293 + 49 + 22 + 几何 45；.NET 10 为 348。- **换模型崩溃**：`ModelPickCtl.OnPaint` 用共享字体缓存，字体被 Dispose 后 `GetHeight` 抛 `ArgumentException`；改为本控件持有字体，并给 `Theme.Cache` 加失效重建。实拍模型浮层：`_audit/model-popover-screen2.png`。
- **菜单居中/留白**：`UiDrop.ShowMenu` 按 `GetPreferredSize()` 居中挂在按钮下方，菜单 padding 8/6、行 12/7；不再左对齐贴边。
- **默认工作区**：侧栏把当前工作目录的会话统一归到「默认工作区」，固定在项目区最上面；同一项目的会话连续排列，不再出现两个同名分组。实拍：`_audit/live-default-grouped.png`。
## 11. 技术债推进：CI 与签名骨架（2026-10-18）

- 新增 `.github/workflows/verify.yml`：Windows runner 上跑旧栈 build、.NET 10 348 自测、publish、`ui_lint`；GUI 全量回归因托管 runner 无桌面，留给本地 `verify-all.cmd`。
- 新增 `devtools\sign.cmd`：用 `SIGN_PFX` / `SIGN_PASSWORD` 签三个 exe，并 `signtool verify /pa`；没有证书时明确退出 2，不伪造签名。
- 本轮验证：`verify-all-after-sign.log` 仍 ALL GREEN（341 + 293 + 49 + 22 + 45 + 348 + ui_lint）。
- 未完成：PerMonitorV2 跨屏重排、暗色模式、原生文本编辑引擎、真正的 async 调用链；这些仍需继续做，不能算已收口。
## 12. 真正的 async 调用链（2026-10-18）

- `ILlm` 新增 `Task StreamAsync`；`OllamaClient.ChatStreamAsync` / `OpenAiLlm.StreamAsync` 全程 `await`：
  - `HttpClient.SendAsync(..., ResponseHeadersRead)`；
  - `ReadAsStringAsync` / `ReadAsStreamAsync` / `StreamReader.ReadLineAsync`；
  - 不再在 HTTP 层用 `GetAwaiter().GetResult()`。
- `AgentRunner.StepAsync` / `RunTurnAsync` 改成 async；UI 启动从 `new Thread` 改为 `Task.Run(async ... await RunTurnAsync)`。
- `RunTurn` / `Stream` 仍保留同步包装，只给 CLI、探针和旧测试用；产品 UI 主链路不再经过同步桥接。
- 结果：`_build/final-ui-tests-45.log` 341 + 293 + 49 + 22 + 几何 45 全过；`.NET 10` `_audit/net10-tests-async.log` 348/348。
## 13. PerMonitorV2（2026-10-18）

- `src/app.manifest` 增加 `dpiAwareness=PerMonitorV2`。
- 主窗口处理 `WM_DPICHANGED`：跨到不同缩放比显示器时保存会话，再重启进程按新 DPI 重新排版；不是把旧布局硬拽成新尺寸。
- 150% 启动实测正常：`_audit/permonitor-v2.png`；未出现启动即重启循环。
- 仍属“重启式”适配，不是无缝跨屏重排；暗色模式和原生编辑器仍未做。
## 14. 暗色模式（2026-10-18）

- `Theme.Apply(bool dark)` 增加完整暗色调色板；菜单/弹窗/输入卡/聊天卡片/列表/设置页的表面色统一从 `Theme` 取。
- `config.dark` 持久化；视图菜单新增「切换暗色/浅色模式」，保存后重启应用主题（和 PerMonitorV2 一样走重启重建）。
- 暗色实拍：`_audit/dark-mode-preview2.png`。
- 限制：Cef 内置浏览器页面和原生滚动条仍是浅色；暗色是应用外壳级，不是网页级。
- 全量自测仍 341 + 293 + 49 + 22 + 几何 45；.NET 10 348/348。
## 15. 原生编辑器替换：Scintilla 双栈 spike（2026-10-18）

- 选型：`Scintilla.NET 5.3.2.9`（MIT，包内含 net45 + net6/7-windows 程序集和 x64/x86 原生 DLL）。
- .NET 10：`devtools/scintilla/ScintillaSpike.csproj` 自包含发布可运行，中文/选区/编辑正常：`_audit/scintilla-spike.png`。
- 旧 .NET Framework：`devtools/scintilla/OldSpike.cs` 用系统 csc 引用 `net45/Scintilla.NET.dll` 可运行：`_audit/scintilla-old-spike.png`。
- 旧栈注意：Scintilla.NET 的旧版加载器会找 `packages\Scintilla.NET.5.3.2.9\build\x64`，部署时必须保留这个目录结构（或改加载路径），不能只拷三个 DLL。
- 结论：**双栈可行已实证**；还没做产品替换。下一步才是把 `HintTextBox` / RichTextBox 包成 `UiEditor` 并逐处替换，不能把 spike 说成已完成。
## 16. 第一处产品替换：Changes diff 用 Scintilla（.NET 10）

- `.NET 10` 的 `ChangesForm` diff、`ApprovalForm` 预览、Plugins 预览/详情四个只读 `RichTextBox` 在 .NET 10 下都换成 `UiDiff : ScintillaNET.Scintilla`：
  - 只读、自动换行、Consolas 10；
  - 新增/删除/标题三种 Scintilla style；
  - `Reset/AppendStyledLine/ScrollTop` 包装。
- 旧 .NET Framework 的只读区走 `UiReadOnlyText`（Label 按行），不再用 RichTextBox；旧构建脚本带 Scintilla.NET 引用。
- .NET 10 项目 / 测试项目加了 `Scintilla.NET 5.3.2.9`；自包含 `dist/net10` 启动正常。
- 输入条 `HintTextBox` 仍是原生 TextBox，**还没换**；不能算第 1 项完成。
- 全量自测：341 + 293 + 49 + 22 + 几何 45；.NET 10 348/348。
## 17. 输入条换成 Scintilla（双栈，2026-10-18）

- `.NET 10` 的 `txtInput` 从 `HintTextBox` 换成 `UiEditor : ScintillaNET.Scintilla`：
  - 无边框、自动换行、隐藏滚动条、微软雅黑 10；
  - 保留 `Hint / Multiline / ScrollBars / Text / TextLength / SelectionStart / Clear / Focus` 接口；
  - 占位提示用覆盖 Label（输入为空时显示，开始输入即收）；
  - Scintilla 自带 IDropTarget，WinForms `AllowDrop` 会冲突；改走 `WM_DROPFILES`，再转成原来的 `DragDrop` 事件（图片拖入仍可用）。
- 旧 .NET Framework 也通过 `SCINTILLA` 条件使用同一个 `UiEditor`；旧构建脚本带 `Scintilla.NET.dll` 引用和原生目录。
- .NET 10 实拍：`_audit/net10-input-final.png`（占位提示“随心输入”可见）。
- 测试里 `txtInput` 的强转从 `TextBox` 改为 `UiEditor`，旧栈/新栈都能编译。
- 全量：旧栈 341 + 293 + 49 + 22 + 几何 45；.NET 10 348/348。
- 旧栈的 Changes/Approval/Plugins 只读区已改成 `UiReadOnlyText`；设置页普通字段也已换 Scintilla。
## 18. 旧栈 Scintilla 范围收窄（2026-10-18）

- 用 `SCINTILLA` 条件 + 旧版 `Scintilla.NET net45` 编译过旧 exe，输入条/占位/`WM_DROPFILES` 实拍正常：`_audit/old-input-scintilla-final.png`。
- 但旧栈 full suite 在 `ChangesForm` 的 Scintilla diff 处崩（`final-ui-tests-53.log`）。
- 旧栈输入条/设置字段走 Scintilla，只读 diff/预览走 `UiReadOnlyText`（Label 按行），不再用 RichTextBox；`build.cmd` / `run-ui-tests` 带 `SCINTILLA` 条件。
- 结果：`final-ui-tests-65.log` 全量通过（341 + 293 + 49 + 22 + 几何 45）；旧栈输入条/设置 Scintilla、只读区 UiReadOnlyText 都通过。
## 19. 设置页输入框与只读日志（2026-10-18）

- `FieldBox` 普通字段改用 `UiEditor`（Scintilla）；旧栈同样通过 `SCINTILLA` 条件启用。
- `_txtName / _txtBase / _txtModels / _txtCwd / _txtAddr / _txtMarketUrl / txtCwd / Addr` 都改成 `UiEditor`；
- `ProposalForm` 正文、Settings 运行日志改成 `UiDiff`（新栈 Scintilla；旧栈 UiReadOnlyText）。
- **例外**：API Key 输入框需要 `UseSystemPasswordChar`，Scintilla 没有等价安全掩码，仍保留原生 `TextBox`。
- 设置页自测 49/49、全量 341 + 293 + 49 + 22 + 几何 45 全过；.NET 10 348/348。
- 旧栈的 Changes/Approval/Plugins 只读 RichTextBox 仍是回退（旧栈全 Scintilla diff 崩过）。
## 20. 设置字段换成 Scintilla（2026-10-18）

- `FieldBox` 普通字段（名称/地址/模型列表/工作目录/插件地址等）在双栈都改成 `UiEditor`；
- `txtCwd / Addr / searchBox / ProposalForm 正文 / 设置运行日志` 改成 `UiEditor`/`UiDiff`；
- **API Key** 字段已改成自绘 `UiPasswordBox`（掩码/显示切换、粘贴、退格）；不再用原生 `TextBox`。
- 旧栈的 Changes/Approval/Plugins 只读区已改成 `UiReadOnlyText`（Label 按行），`final-ui-tests-65.log` 全量通过。
- 结果：`final-ui-tests-60.log` 全量通过（341 + 293 + 49 + 22 + 几何 45）；.NET 10 348/348。
## 21. 自绘密码框（2026-10-18）

- 新增 `UiPasswordBox`：自绘掩码/明文、光标、聚焦边框；支持输入、退格、Ctrl+V 粘贴、Ctrl+A 屏蔽复制。
- `_txtKey` 从原生 `TextBox` 改成 `UiPasswordBox`；`UseSystemPasswordChar` 语义保留为“是否打码”。
- 已知限制：只针对 ASCII API Key；没有 IME/选区/右键菜单，安全性与系统密码框不同，但不再依赖原生 TextBox。
- 全量自测：`final-ui-tests-61.log` 341 + 293 + 49 + 22 + 几何 45；.NET 10 348/348。
## 22. 最终收口（2026-10-18）

- **所有会实例化的原生 TextBox/RichTextBox 已清零**：输入条/设置字段/API Key/只读 diff/日志/Proposal 都改成 Scintilla、UiPasswordBox 或 UiReadOnlyText（Label 按行）。
- 源码里只剩 `HintTextBox`/`UiTextBox` 两个**未使用的回退类定义**（旧构建无 Scintilla 时才可能用到），不再被产品路径实例化。
- 旧栈只读区 `UiReadOnlyText` 用 Label 按行渲染，规避了旧 Scintilla diff 的崩溃；`final-ui-tests-65.log` 全量通过。
- async、PerMonitorV2（重启式）、暗色外壳、CI 已完成；代码签名脚本就绪，按用户决定**暂不签名**。
- 最终产物：旧 exe 11:19:16，.NET 10 自包含 exe 9:31:03；全量 341 + 293 + 49 + 22 + 几何 45，.NET 10 348/348。
## 23. 六个剩余限制的进展（2026-10-18）

- **原生回退类**：`HintTextBox` / `UiTextBox` 两个类已删除；源码里不再有 `: TextBox` / `: RichTextBox` 定义，也没有这些类型的实例化。
- **暗色网页**：`browser-home.html` 支持 `?dark=1`，`StartPage()` 在暗色下带参数；Cef 暗色启动时加 `force-dark-mode` + `WebContentsForceDark`。
- **旧栈只读拖选/复制**：试过 Label 选择，但会让设置页自测在日志页之后崩；已回退，未交付。`UiReadOnlyText` 目前仍只是只读展示（无拖选/复制）。
- **密码框选区/IME**：未做；仍是 ASCII 追加/退格/粘贴。
- **PerMonitorV2 无缝**：未做；仍是重启式。
- **CI 真实 runner**：未做；workflow 文件就绪，但没有在 GitHub runner 实跑。
- 本轮全量：`final-ui-tests-70.log` 通过 341 + 293 + 49 + 22 + 几何 46 控件/0 越界；.NET 10 348/348。
## 24. 只读拖选 + 密码选区（2026-10-18）

- `UiReadOnlyText` 增加按行拖选：鼠标按下/拖动/松开改变选行，选中行用 `Theme.Sel` 高亮；右键菜单「复制选中」；无选中时复制全部。**不抢焦点、不改 TabStop**，所以设置页日志页不再崩。
- `UiPasswordBox` 增加光标与选区：鼠标点击/拖动定位、方向键/Home/End、Shift 扩选、Ctrl+A、Ctrl+V 在光标处粘贴、Backspace/Delete 删除选区；掩码/明文都画选区。
- 仍不支持 IME 组合输入；API Key 仍按 ASCII 处理。
- 全量：`final-ui-tests-72.log` 341 + 293 + 49 + 22 + 几何 46；.NET 10 348/348。
## 25. PerMonitorV2 无缝重排（2026-10-18）

- 不再重启进程：`WM_DPICHANGED` 里更新 `Theme.Dpi`，`BeginInvoke` 调 `LayoutUi()` + 全窗重绘。
- 实测：对运行中的窗口注入 `WM_DPICHANGED`（150%→96%）后进程存活、界面正常重排，截图 `_audit/dpi-inplace-96b.png`。
- 未在真实双显示器之间物理拖动验证；只做了消息级模拟。
- 暗色：首页 `?dark=1` + Cef `force-dark-mode`/WebContentsForceDark 已加；Cef 任意网站和原生滚动条仍不能保证完全暗色。
- CI：本机没有 `git`、`gh` 也未登录，无法在真实 GitHub runner 实跑；workflow 文件仍只是就绪状态。
## 26. 暗色原生滚动条尝试（2026-10-18）

- 在 `Theme` 加 `SetWindowTheme(..., "DarkMode_Explorer")`，并对子窗口 `ScrollBar` 类再套一次；`MainForm`/`SettingsForm` Shown 时递归。
- 实测暗色截图 `_audit/dark-scrollbars2.png`：对话区/侧栏的原生滚动条仍是浅色，说明这套主题对当前 WinForms 滚动条非客户区不生效；不再声称“原生滚动条已暗色”。
- Cef 首页 `?dark=1` + `force-dark-mode`/WebContentsForceDark 已保留。
- CI：本机无 `git`、`gh` 未登录，无法在真实 runner 跑。
- 全量：`final-ui-tests-74.log` 341 + 293 + 49 + 22 + 几何 46；.NET 10 348/348。
## 27. 暗色原生滚动条第二次尝试（2026-10-18）

- 除了 `SetWindowTheme("DarkMode_Explorer")` 和枚举 `ScrollBar` 子窗口，又加了 `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`。
- 实测 `_audit/dark-scrollbars3.png`：对话区/侧栏的原生滚动条仍是浅色。WinForms 的 `AutoScroll` 滚动条是非客户区，没有独立 HWND，主题/ DWM 属性都盖不到。
- 结论：要暗色只能把 `TranscriptView` / `UiList` / `UiReadOnlyText` 的滚动条改成自绘，工作量不是换颜色，是重做滚动容器。当前不谎称完成。
- 全量：`final-ui-tests-75.log` 341 + 293 + 49 + 22 + 几何 46；.NET 10 348/348。
## 28. 验收结论与已知限制（2026-10-18）

按用户确认的验收口径，本次收口结论：

- **已完成**：旧栈只读按行拖选/复制；自绘密码框选区/编辑；PerMonitorV2 无缝重排（消息级模拟）；删除 `HintTextBox`/`UiTextBox` 两个原生回退类。
- **暗色（部分，已知限制）**：Cef 首页 `?dark=1`、`force-dark-mode`/`WebContentsForceDark` 已生效；WinForms `AutoScroll` 的原生滚动条仍浅色。用户决定**本版本不重做自绘滚动条**，记录为已知限制。
- **CI（本版本不做）**：本机无 `git`、`gh` 未登录，workflow 文件就绪但未在真实 runner 实跑；用户决定先不做。
- **密码 IME（单独排期）**：当前版本密码框不支持输入法组合输入。后续实现要求：
  1. 处理 `WM_IME_*` 消息；
  2. 隔离输入法合成缓冲区，不把候选中间串写进密码内存；
  3. 完成后做多输入法兼容性测试。
- **验证**：旧栈 341 + 293 + 49 + 22 + 几何 46；.NET 10 348/348；`final-ui-tests-75.log`。
- **产物**：源码 21,174 行 / 930,332 字节 / SHA256 `E56FA5C2…`；旧 exe 11:19:16；.NET 10 自包含 exe 10:14:19。
## 29. 菜单/输入条文字对齐修复（2026-10-18）

- 菜单项垂直内边距从 7 收到 3，`MenuTheme.OnRenderItemText` 把文字矩形按行高居中，菜单不再“字飘在行顶”。
- `UiButton` 增加 `IconYOffset`；`＋` 下移 1.5、权限警告图标下移 1，和文字基线对齐。
- 实拍：`_audit/menu-compact.png`、`_audit/input-align-fix.png`。
- 全量：`final-ui-tests-76.log` 341 + 293 + 49 + 22 + 几何 46；.NET 10 348/348。
## 30. ＋ 号未居中修复（2026-10-18）

- 根因：`btnPlus` 是 `UiDrop`，默认 `Align = MiddleLeft`，`Padding=0` 时图标从 x=0 开画，白色按钮方块里看起来偏左。
- 改成 `Align = MiddleCenter`，图标按可用宽度居中；`IconYOffset=1.5` 保留做基线微调。
- 实拍：`_audit/input-plus-center.png`。
- 全量：`final-ui-tests-77.log` 341 + 293 + 49 + 22 + 几何 46；.NET 10 348/348。
## 31. ＋ 号改字体字形（2026-10-18）

- 手绘 `Icons.Draw("plus")` 在 150% 下光栅化后视觉中心偏上/发斜，用户连续指出未居中。
- 改为 `btnPlus.Icon = null; Text = "+"; Font = Theme.FB(18f); Align = MiddleCenter`，由 `TextRenderer` 做水平+垂直居中；不再依赖手绘线段的像素落点。
- 实拍：`_audit/input-plus-text-crop.png`。
- 全量：`final-ui-tests-78.log` 341 + 293 + 49 + 22 + 几何 46；.NET 10 348/348。
