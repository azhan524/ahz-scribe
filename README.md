# Ahz Scribe

一个 Windows 桌面智能体：能读写文件、执行命令、调用本地 Ollama 或 OpenAI 兼容接口，并保留审批、回滚和自迭代能力。

界面是自绘的 WinForms 外壳（无系统标题栏），支持浅色 / 暗色、150% DPI、PerMonitorV2 跨屏重排、对话 / 轨迹双页签、内置 CefSharp 浏览器。

![主界面](screenshot.png)

## 两个可运行版本

| 版本 | 文件 | 说明 |
|---|---|---|
| 旧 .NET Framework 4.8 | `AhzScribe.exe` | 系统自带运行时，双击即用；仓库内保留可回滚 |
| .NET 10 | `dist/net10/AhzScribe.Net10.exe` | 自包含发布，不需要另装 .NET Desktop Runtime；CefSharp 154 |

命令行版：`ahzscribe-cli.exe`。

## 快速开始

1. 本地模型：先启动 Ollama；
2. 双击 `AhzScribe.exe`（或 .NET 10 的 exe）；
3. 在底部输入框写任务，`Enter` 发送，`Shift+Enter` 换行；
4. 首次使用在“设置 → 模型”里添加提供商 / 填接口地址和 API Key。

API Key 默认用 Windows DPAPI（CurrentUser）加密后写入 `config.json`；`config.json`、`sessions.json` 和日志不会进仓库（`.gitignore` 已挡住）。

## 主要能力

- **对话 / 轨迹**：一条消息一个控件，流式输出只重排最后一条；
- **工具审批**：仅可查看 / 工作区内修改 / 完全访问三档；
- **回滚**：源码快照 + 上一版 exe，可撤销一轮文件改动；
- **自迭代**：模型可提交改造申请，批准后按“改代码 → build → 自测 → 升级重启”执行；
- **模型能力以接口声明为准**：显示名、看图、推理档位、上下文长度都从 `/models` 元数据读取；
- **内置编辑器**：输入条 / 设置字段用 Scintilla，只读 diff / 日志用自绘控件；
- **单文件源码**：所有 C# 都在 `src/AhzScribe.cs`，同时被旧栈和 .NET 10 项目引用。

## 构建

旧 .NET Framework（系统 `csc.exe`）：

```bat
build.cmd
```

.NET 10（需要 SDK；仓库的 `devtools\build-net10.cmd` 默认找 `.dotnet\dotnet.exe`，没有则用 PATH 里的 `dotnet`）：

```bat
devtools\build-net10.cmd
devtools\build-net10.cmd publish
```

产物：

```text
AhzScribe.exe / ahzscribe-cli.exe
dist/net10/AhzScribe.Net10.exe
```

旧栈输入条依赖 Scintilla.NET；仓库 CI 会从 NuGet 恢复 `Scintilla.NET.dll` 和 `packages\Scintilla.NET.5.3.2.9\build\x64`。本地若缺这两个，旧栈构建会失败。

## 测试

```bat
devtools\bench\run-ui-tests.cmd
devtools\run-net10-tests.cmd
verify-all.cmd
```

当前基线（2026-10-18）：

```text
旧栈：341 + 293 + 53 + 22 + 几何 46 控件 / 0 越界
.NET 10：348/348
```

`run-ui-tests.cmd` 需要交互式 Windows 桌面；GitHub 托管 runner 没有桌面，因此 CI 只跑：

```text
build.cmd
.NET 10 自测
.NET 10 自包含 publish
ui_lint
```

## 目录

```text
src/AhzScribe.cs              主源码（单文件）
build.cmd                     旧 .NET Framework 构建
migration/AhzScribe.Net10/    .NET 10 应用项目
migration/AhzScribe.Net10.Tests/  .NET 10 自测
devtools/                     构建、UI 测试、截图、验收工具
migration/                    迁移工程
tools/ui_lint.py              浏览器首页审美检查
browser-home.html             内置浏览器首页（支持 ?dark=1）
.github/workflows/verify.yml  GitHub Actions
AHZScribe-修复复验-2026-10-18.md  逐项验收与证据
README-交付包-2026-10-18.md       交付/回滚/已知限制
```

## 已知限制

- 未做真实代码签名（没有 PFX）；个人自用可忽略，别人下载 exe 时可能出现 SmartScreen 提示；
- CefSharp 任意网站的暗色不保证，只保证首页 `?dark=1` 和 Chromium 的 force-dark；
- 密码框已处理 `WM_IME_*` 并隔离合成缓冲，但仍需微软拼音 / 搜狗等真实输入法手工兼容性测试；
- 旧栈只读区是按行拖选，不是字符级；
- CI 不跑完整 UI 回归（runner 无桌面），完整 UI 以本地为准。

## License

MIT，见 [LICENSE](LICENSE)。
