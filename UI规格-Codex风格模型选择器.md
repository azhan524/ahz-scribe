# UI 规格：Codex 风格的「模型 + 推理档位」选择器

> 目标：把输入卡右下角的模型 chip 换成 Codex 那套交互（chip → 自绘浮层 → 模型列表 + 强度滑块）。
> 参考图见对话中的三张截图，下面按截图逐项拆解。

---

## 一、三张截图分别是什么

**截图 A（收起态 chip）**
一个浅灰圆角胶囊（pill，圆角约 18），内文深灰：
```
智谱 GLM / glm-5.3 · 低        ⌄
```
即 **`厂商 / 模型 · 档位`**，右侧一个下箭头。它位于输入卡内部右侧，紧邻深色圆形"↑"发送按钮。

**截图 B（强度浮层）**
在 chip **上方**弹出的白se圆角卡片（圆角约 16，带柔和阴影）：
- 顶部居中：**档位文字**（当前档位，**强调蓝色**、加粗、较大字号）——图中为「高」
- 其下一行居中：**`模型名 ›`**（灰色小字）——图中为「DeepSeek-V4.1-Flash ›」
- 再下一行：**横向强度滑块**（左端一个小圆点，右端一个大白色圆形滑块，已填充段为强调蓝）
- 同时下方的 chip 变成占位文案「选择强度」+ 下箭头

**截图 C（模型列表浮层）**
同样位置弹出的白色圆角卡片：
- 顶部灰色小标题：**「选择模型」**
- 列表行：每行一个模型名（左对齐）；**当前模型行有浅灰底高亮 + 右侧 ✓**
- 图中两行：`DeepSeek-V4.1-Flash ✓`（选中）、`DeepSeek-V4-Pro`
- chip 同步显示：`DeepSeek-V4.1-Flash 高 ⌄`

> 注意 C 里 chip 是"模型名 + 档位"，A 里是"厂商 / 模型 · 档位"。建议**统一成 A 的格式**（`厂商 / 模型 · 档位`），因为本项目的模型 chip 需要区分 Ollama 本地和云端。

---

## 二、视觉规格（沿用本项目 `Theme`，不要新造色板）

| 元素 | 规格 |
| --- | --- |
| chip | 圆角 18 的胶囊；底色 `Theme.Chip`；悬停 `Theme.ChipHover`；文字 `Theme.Fg`；字号 9f；内边距左右 14；高度 34 |
| chip 右侧箭头 | 自绘小三角（不要用字符 `⌄`，字体渲染不稳），颜色 `Theme.FgDim`，随展开旋转 180° |
| 浮层卡片 | 白底、圆角 16、1px `Theme.Line` 描边 + 柔和阴影；**向上弹出**（输入框在底部），与 chip 间距 8 |
| 浮层内边距 | 12 |
| 标题「选择模型」 | `Theme.FgDim`、8.5f，左对齐，上边距 4、下边距 6 |
| 列表行 | 高 36、圆角 10；悬停 `Theme.Hover`；当前行 `Theme.Sel` + 右侧 ✓（`Theme.Accent`） |
| 档位文字（浮层顶部） | `Theme.Accent`、加粗、15f、居中 |
| 当前模型名（档位下一行） | `Theme.FgDim`、9f、居中 |
| 滑块 | 轨道高 10、圆角 5；已填充 `Theme.Accent`，未填充 `Theme.LineSoft`；滑块旋钮直径 26、白色、1px `Theme.Line` 描边 |
| 滑块左端小圆点 | 直径 6、`Theme.Accent`（图中左端那个小点，表示"最低档"锚点） |

**所有控件必须自绘 + 双缓冲**（`SetStyle(..., OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint, true)`），不要用原生 `ComboBox` / `ContextMenuStrip`（本项目已统一自绘，原生控件在浅色主题下会露出系统边框，观感塌陷）。

---

## 三、交互规格

1. **点 chip** → 打开浮层；**再点 chip** 或**点浮层外任意处** → 关闭；**Esc** → 关闭。
2. 浮层打开时不抢输入框焦点（不要 Focus 输入框导致丢文字）。
3. 模型列表：
   - 悬停高亮、点击即选中并写入 `config.model`（+ `config.provider`），浮层关闭（或切到档位面板，二选一，建议**关闭**，保持 Codex 的轻量感）；
   - 列表超过 6 项要**可滚动**（本项目本地模型可能十几项）；
   - 分组：本地（ollama）与云端分开，组标题用「本地」「云端」小灰字。
4. 档位滑块：
   - 拖动时**实时**更新浮层顶部的档位文字（低 / 中 / 高）；
   - 松手后写入配置并持久化；
   - 支持键盘 ←/→ 微调。
5. **能力缺失时隐藏滑块**（重要）：见第四节映射表，不支持档位的模型只显示模型列表，浮层顶部不出现滑块。

---

## 四、档位 → 实际参数 的映射（**需要设计决策，别乱接**）

档位必须**真的影响请求**，否则就是装饰。建议按下表：

| 提供商类型 | 档位映射 | 说明 |
| --- | --- | --- |
| `ollama`（本地） | 低 = `think=false`；中 = `think=true` + `num_predict` 默认；高 = `think=true` + 更大 `num_predict` | 本项目已是原生 `/api/chat`，`think` 直接可用 |
| `openai` 兼容（DeepSeek 等） | 若接口支持 `reasoning_effort` → 低/中/高映射 `low/medium/high`；**不支持则隐藏滑块** | 不要伪造参数，不要往 payload 里塞无效字段（有的网关会 400） |
| 其余 | 隐藏滑块 | 宁可不显示，也别给假档位 |

**默认档位**：`中`。切换模型时按该模型能力重新决定是否显示滑块，并保留用户上次选择（存 `config`，字段名建议 `reason_level`，取值 `low/mid/high`）。

---

## 五、代码落点（本项目实际位置）

- **chip 现在的位置**：输入卡底部那行里的 `cmbModel`（类型 `DropdownField`），在 `MainForm` 的输入卡构建段；本次要把它替换成新的自绘 chip 控件（建议新类 `ModelChip`）。
- **浮层**：新建 `ModelPopover`（自绘 `Form`/`Panel` 浮层，无边框 `FormBorderStyle.None` + `ShowWithoutActivation`），或用一个绝对定位的 `Panel` 覆盖在主窗体上（注意 z-order，加完要 `BringToFront()`）。
- **颜色字体**：一律走 `Theme`（`Theme.Chip` / `ChipHover` / `Hover` / `Sel` / `Line` / `LineSoft` / `Fg` / `FgDim` / `Accent`）。
- **DPI**：本项目按 DPI 整体 `ApplyDpiScale`。浮层与 chip 的**位置必须显式算**（参考 `LayoutModelPage()` / `LayoutOtherPages()` 的思路），**不要只靠 `Anchor`**；150% 缩放下要让浮层完整可见、不越出窗口。
- **设置页无关**：本改动只动输入卡，`settings-unittest.cs` 不受影响，但**必须跑** `devtools\bench\run-ui-tests.cmd`（界面流式 + 规矩用例）。

---

## 六、验收标准

1. `cmd /c build.cmd` 零错误；
2. `devtools\bench\run-ui-tests.cmd` 全绿；
3. 150% DPI（用户实际环境）下：chip 文字不截断、浮层不越界、滑块可拖动、"↑"按钮不被遮挡；
4. 三个场景手工确认：
   - 收起态显示 `厂商 / 模型 · 档位`；
   - 点开模型列表：当前项有 ✓，点击可切换并写入 `config`；
   - 点开强度面板：拖到"高"后，**实际请求参数确实变了**（用 `Log.Audit` 记一条，或抓一次请求验证）；
5. 不支持档位的模型：不显示滑块（不得出现假档位）。

---

## 七、禁止事项

- ❌ 不要用原生 `ComboBox` / `ContextMenuStrip` / `ToolTip` 做这个浮层；
- ❌ 不要在 `Theme.ScaleBounds` 里递归放大 `FieldBox`（会 1.5×1.5 放大两次）；
- ❌ 不要新增"看起来能调但实际不生效"的档位；
- ❌ 不要因为改这个而动 `TranscriptView` / `MsgBlock` / `Relayout` 相关代码。
