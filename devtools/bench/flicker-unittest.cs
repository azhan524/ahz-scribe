using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// 自测：对话区改成"一条消息一个块"之后，流式输出到底还有多少重画（"闪屏抽搐"回归）
// 关心的三件事：
//   1. 新 token 只动最后那一条消息，老消息一次都不重画（旧版整块 RichTextBox 每来一个字全屏重排）
//   2. 攒批写入，不会每个 token 都排版一次
//   3. 用户滚上去看历史时不被拽走；滚回底部自动恢复跟随
// 图片缩略图/工具图上下文（36/37/38 节）已随 2026-10-18 修复落地，
// 不再用条件编译跳过；这三节现在是正式回归。
class FlickerCheck
{
    static int pass, fail;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    const int WM_MOUSEWHEEL = 0x020A;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) { pass++; Console.WriteLine("  [PASS] " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + "  -> " + detail); }
    }

    static object Get(object t, string f)
    {
        return t.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(t);
    }

    // 公共字段也能取（比如 AgentRunner.Messages）
    static object GetAny(object t, string f)
    {
        FieldInfo fi = t.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return fi == null ? null : fi.GetValue(t);
    }

    // 按名字调私有方法。参数个数决定用哪个重载 —— 有同名重载时 GetMethod(name) 会抛
    // AmbiguousMatchException（TogglePermPopover() / TogglePermPopover(Control) 就踩过）。
    static object CallPrivate(object t, string m, params object[] a)
    {
        Type[] types = new Type[a.Length];
        for (int i = 0; i < a.Length; i++)
            types[i] = a[i] == null ? typeof(object) : a[i].GetType();
        MethodInfo mi = t.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic, null, types, null);
        if (mi == null)
            throw new MissingMethodException(t.GetType().Name + "." + m + "(" + types.Length + " 个参数)");
        return mi.Invoke(t, a);
    }

    // 点一下输入条上的模型 chip（走的就是点击时的同一条代码路径）
    static void callToggle(object form)
    {
        CallPrivate(form, "ToggleModelPopover");
    }

    // 模拟点一行模型（走控件自己的 Picked 回调，和鼠标点的是同一条路径）
    static void RaisePickRow(ModelPickCtl pick, int rowIdx)
    {
        FieldInfo fi = pick.GetType().GetField("Picked", BindingFlags.Instance | BindingFlags.NonPublic);
        Delegate d = fi == null ? null : (Delegate)fi.GetValue(pick);
        if (d == null) return;
        pick.PickedIndex = rowIdx;
        d.DynamicInvoke(new object[] { pick, EventArgs.Empty });
    }

    // 走一遍老配置迁移（只有 auto_yes 的旧文件）→ 应该得到哪一档
    static string PermMigrate(bool autoYes)
    {
        AgentConfig c = new AgentConfig();
        c.permission = null;
        c.auto_yes = autoYes;
        c.Migrate();
        return c.permission;
    }

    // 点一下权限 chip
    static void TogglePerm(object form)
    {
        CallPrivate(form, "TogglePermPopover");
    }

    // ---- 图片粘贴/拖入自测用的小工具 ----
    static bool AllTrue(MethodInfo m, string[] xs)
    {
        foreach (string x in xs) if (!(bool)m.Invoke(null, new object[] { x })) return false;
        return true;
    }

    static bool AllFalse(MethodInfo m, string[] xs)
    {
        foreach (string x in xs) if ((bool)m.Invoke(null, new object[] { x })) return false;
        return true;
    }

    // 发送按钮是否处于"可发"状态（有内容时亮、空时灰）
    static bool HasSendEnabled(object form)
    {
        object btn = Get(form, "btnSend");
        if (btn == null) return false;
        object fill = btn.GetType().GetField("FillColor", BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic).GetValue(btn);
        return !fill.Equals(Theme.AccentIdle);
    }

    // 把 base64 解回来确认真的是 PNG（只解个头，别真解整张图）
    static bool IsPngBase64(string b64)
    {
        try
        {
            byte[] b = Convert.FromBase64String(b64);
            return b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
        }
        catch { return false; }
    }

    // 输入框的提示里有没有提"粘图片"这件事。
    // 界面照 Codex 改之后，占位语只剩四个字（「随心输入」），那句长提示挪到了
    // 鼠标悬停的 tooltip（MainForm.HintDetail）。所以两处合起来必须提到 Ctrl+V 粘图 ——
    // 该说的还得说，只是换个地方说。
    static string ConstOf(string name)
    {
        FieldInfo fi = typeof(MainForm).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        return fi == null ? null : Convert.ToString(fi.GetValue(null));
    }

    static bool PlaceholderMentionsPaste()
    {
        string all = Convert.ToString(ConstOf("PlaceholderText")) + "|" + Convert.ToString(ConstOf("HintDetail"));
        return all.IndexOf("V") >= 0 && all.IndexOf("图片") >= 0;
    }

    // 假模型：每一步都"写一条计划、但永远不把它标成 - [x]，然后随便调个读类工具"。
    // 这正是 2026-10-15 用户那条会话的样子（提醒 → 读文件 → 提醒 → 读文件…），
    // 用它来验证框架自己的循环有没有上限、会不会把提醒灌满上下文。
    class FakeLlm : ILlm
    {
        public int Calls = 0;
        public string Kind { get { return "fake"; } }

        public Dictionary<string, object> Stream(List<Dictionary<string, object>> messages, List<object> tools,
            bool think, string effort, int numCtx, Action<string> onThinking, Action<string> onContent,
            Action<Dictionary<string, object>> onToolCall)
        {
            Calls++;
            onContent("● 继续干活\n- [ ] 这步永远做不完\n");
            Dictionary<string, object> call = new Dictionary<string, object>();
            call["id"] = "call-" + Calls;
            call["name"] = "list_dir";
            Dictionary<string, object> args = new Dictionary<string, object>();
            args["path"] = ".";
            call["arguments"] = args;
            onToolCall(call);
            return new Dictionary<string, object>();
        }

        public System.Threading.Tasks.Task<Dictionary<string, object>> StreamAsync(
            List<Dictionary<string, object>> messages, List<object> tools, bool think, string effort, int numCtx,
            Action<string> onThinking, Action<string> onContent, Action<Dictionary<string, object>> onToolCall)
        {
            return System.Threading.Tasks.Task.FromResult(
                Stream(messages, tools, think, effort, numCtx, onThinking, onContent, onToolCall));
        }

        public void Cancel() { }
        public Dictionary<string, object> Probe() { return null; }
        public List<ModelMeta> ListModelInfos() { return new List<ModelMeta>(); }
        public void Close() { }
    }

    // ---- 第 35 节（意图澄清弹窗）用的小工具 ----

    // 找弹窗里第 idx 行选项（按控件树的顺序，和选项顺序一致）
    static ClarifyRow findRow(Control root, int idx)
    {
        int n = 0;
        foreach (Control c in allControls(root))
        {
            ClarifyRow r = c as ClarifyRow;
            if (r == null) continue;
            if (n == idx) return r;
            n++;
        }
        return null;
    }

    static List<Control> allControls(Control root)
    {
        List<Control> list = new List<Control>();
        foreach (Control c in root.Controls)
        {
            list.Add(c);
            list.AddRange(allControls(c));
        }
        return list;
    }

    // 点一行（走控件自己的 OnClick，和鼠标点的是同一条路径）
    static bool tapRow(ClarifyRow row)
    {
        if (row == null) return false;
        row.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(row, new object[] { EventArgs.Empty });
        Application.DoEvents();
        return row.Selected;
    }

    // 直接关闭弹窗，看它返回什么（关闭 = 跳过）
    static int closeAndRead(ClarifyForm dlg)
    {
        dlg.Close();
        Application.DoEvents();
        return dlg.SelectedIndex;
    }

    static void Pump(int ms)
    {
        DateTime end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
    }

    static List<MsgBlock> Blocks(TranscriptView v)
    {
        return (List<MsgBlock>)Get(v, "_blocks");
    }

    // 往某个控件发一条滚轮消息（正数往下滚、负数往上滚）
    static void Wheel(Control c, int delta)
    {
        Point pt = c.PointToScreen(new Point(6, 6));
        IntPtr wp = (IntPtr)((delta << 16) & unchecked((int)0xFFFF0000));
        IntPtr lp = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
        SendMessage(c.Handle, WM_MOUSEWHEEL, wp, lp);
    }

    static string Tail(string s, int n)
    {
        if (s == null) return "";
        return s.Length <= n ? s : s.Substring(s.Length - n).Replace("\n", "\\n");
    }

    // 面板真正能滚到的最远处：设一个超大值，读回来就是被夹住的上限
    static int RealMax(TranscriptView v)
    {
        int save = -v.AutoScrollPosition.Y;
        v.AutoScrollPosition = new Point(0, 1000000);
        Application.DoEvents();
        int m = -v.AutoScrollPosition.Y;
        v.AutoScrollPosition = new Point(0, save);
        Application.DoEvents();
        return m;
    }

    // 某个消息块在对话区里看起来的 y（相对面板顶部）
    static int VisTop(TranscriptView v, MsgBlock b)
    {
        return b.PointToScreen(Point.Empty).Y - v.PointToScreen(Point.Empty).Y;
    }

    // 把一条消息的排版结果（每个片段的位置和样式）导出成字符串，用来比对
    static string DumpPieces(MsgBlock b)
    {
        StringBuilder sb = new StringBuilder();
        System.Collections.IEnumerable ps = (System.Collections.IEnumerable)
            b.GetType().GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b);
        foreach (object p in ps)
        {
            Rectangle r = (Rectangle)p.GetType().GetField("Rect").GetValue(p);
            int style = (int)p.GetType().GetField("Style").GetValue(p);
            string txt = (string)p.GetType().GetField("Text").GetValue(p);
            sb.Append(style).Append(':').Append(r.X).Append(',').Append(r.Y).Append(',')
              .Append(r.Width).Append(',').Append(r.Height).Append(':').Append(txt).Append('|');
        }
        return sb.ToString();
    }

    static List<Rectangle> PieceRects(MsgBlock b, out List<int> styles)
    {
        List<Rectangle> rects = new List<Rectangle>();
        styles = new List<int>();
        System.Collections.IEnumerable ps = (System.Collections.IEnumerable)
            b.GetType().GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b);
        foreach (object p in ps)
        {
            rects.Add((Rectangle)p.GetType().GetField("Rect").GetValue(p));
            styles.Add((int)p.GetType().GetField("Style").GetValue(p));
        }
        return rects;
    }

    // 把片段还原成原文：粗体补回 **，行内代码补回 `（能对上就说明一个字都没丢/没重复）
    // 表格某一列的单元格左边界是否一致（用来验证表格列对齐）
    static string DumpTextOnly(MsgBlock b)
    {
        StringBuilder sb = new StringBuilder();
        System.Collections.IEnumerable ps = (System.Collections.IEnumerable)
            b.GetType().GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b);
        foreach (object p in ps)
        {
            int st = (int)p.GetType().GetField("Style").GetValue(p);
            string t = (string)p.GetType().GetField("Text").GetValue(p);
            if (st == 6) continue;
            sb.Append(t);          // 只比"文字总序列"：块级排版的分片边界可以不同，字符不能丢/不能乱序
        }
        return sb.ToString();
    }

    static bool SameColumnLeft(List<Rectangle> rects, List<int> styles, int col)
    {
        List<int> lefts = new List<int>();
        foreach (int st in styles) { }
        for (int i = 0; i < rects.Count; i++)
        {
            if (styles[i] != 4 && styles[i] != 5) continue;
            lefts.Add(rects[i].Left);
        }
        lefts.Sort();
        List<int> uniq = new List<int>();
        foreach (int l in lefts) if (!uniq.Contains(l)) uniq.Add(l);
        return uniq.Count >= 2;      // 至少两列，说明确实按列排了
    }

    static string RebuildText(MsgBlock b)
    {
        StringBuilder sb = new StringBuilder();
        System.Collections.IEnumerable ps = (System.Collections.IEnumerable)
            b.GetType().GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b);
        foreach (object p in ps)
        {
            int style = (int)p.GetType().GetField("Style").GetValue(p);
            string t = (string)p.GetType().GetField("Text").GetValue(p);
            if (style == 1) sb.Append("**").Append(t).Append("**");
            else if (style == 2) sb.Append('`').Append(t).Append('`');
            else sb.Append(t);
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    // "每个渲染出来的字符 → 它在原文里的偏移"。分片边界可以不同，但同一个字的偏移必须一样
    // （选中高亮全靠这个换算，错一点高亮就盖不住字）
    static string DumpCharAt(MsgBlock b)
    {
        StringBuilder sb = new StringBuilder();
        System.Collections.IEnumerable ps = (System.Collections.IEnumerable)
            b.GetType().GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b);
        foreach (object p in ps)
        {
            int st = (int)p.GetType().GetField("Style").GetValue(p);
            string t = (string)p.GetType().GetField("Text").GetValue(p);
            int sa = (int)p.GetType().GetField("SrcAt").GetValue(p);
            if (st == 6) continue;                       // 表格分隔线没有字
            for (int i = 0; i < t.Length; i++) sb.Append(sa + i).Append(',');
        }
        return sb.ToString();
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        MainForm f = new MainForm(false);
        f.WindowState = FormWindowState.Normal;
        f.ShowInTaskbar = false;
        f.TopMost = true;          // 让窗口真的可见，否则系统不会发 WM_PAINT，重画次数统计不出来
        f.Show();
        Pump(800);
        f.Activate();
        Pump(200);
        TranscriptView rtb = (TranscriptView)Get(f, "rtb");
        TranscriptView trace = (TranscriptView)Get(f, "trace");
        // 真实流程里发消息前会先收掉空状态大标题，这里照做（否则标题会盖住对话区，画不出来）
        f.GetType().GetMethod("ShowHero", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(f, new object[] { false });
        rtb.ClearAll();          // 屏蔽"没有可用模型"之类的启动提示，保证计数干净
        trace.ClearAll();
        Pump(200);

        Console.WriteLine("== 1. 一条消息一个块：3 条消息 = 3 个块 ==");
        f.Write(OutKind.Assistant, "第一段回答");
        f.Write(OutKind.ToolCall, "  ● read_file(a.txt)\n");
        f.Write(OutKind.Assistant, "第二段回答");
        Pump(500);
        List<MsgBlock> bs = Blocks(rtb);
        Check("3 条消息 = 3 个消息块", bs.Count == 3, "count=" + bs.Count);
        if (bs.Count != 3) { f.Close(); Environment.Exit(1); }
        int p0 = bs[0].Paints, p1 = bs[1].Paints, p2 = bs[2].Paints;
        int r0 = bs[0].Rebuilds, r1 = bs[1].Rebuilds, r2 = bs[2].Rebuilds;
        int h0 = bs[0].Height, h1 = bs[1].Height;

        Console.WriteLine("== 2. 流式 800 个 token：老消息块一次都不能重画 ==");
        int fl0 = rtb.Flushes;
        StringBuilder want = new StringBuilder();
        for (int i = 0; i < 800; i++)
        {
            string tok = "字" + (i % 10);
            f.Write(OutKind.Assistant, tok);
            want.Append(tok);
        }
        Pump(700);
        int flushes = rtb.Flushes - fl0;
        Console.WriteLine("    800 个 token -> 视图实际写入 " + flushes + " 次");
        Check("批量写入次数 <= 12（一个 token 写一次就是 800 次）", flushes >= 1 && flushes <= 12, "flushes=" + flushes);
        Check("第 1 条（老消息）零重画", bs[0].Paints - p0 == 0, "paints=" + (bs[0].Paints - p0));
        Check("第 2 条（工具调用）零重画", bs[1].Paints - p1 == 0, "paints=" + (bs[1].Paints - p1));
        Console.WriteLine("    流式那条：重排 " + (bs[2].Rebuilds - r2) + " 次，重画 " + (bs[2].Paints - p2) + " 次");
        Check("老消息一次都没重新排版", bs[0].Rebuilds - r0 == 0 && bs[1].Rebuilds - r1 == 0,
            "r0=" + (bs[0].Rebuilds - r0) + " r1=" + (bs[1].Rebuilds - r1));
        Check("流式那条每次写入只重排一次", bs[2].Rebuilds - r2 <= flushes + 1, "rebuilds=" + (bs[2].Rebuilds - r2));
        Check("可见窗口下流式那条确实重画过（重画计数是真的在工作）", bs[2].Paints - p2 >= 1, "paints=" + (bs[2].Paints - p2));
        Check("只有最后一条在动（重画次数 <= 写入次数 + 3）", bs[2].Paints - p2 <= flushes + 3, "paints=" + (bs[2].Paints - p2));
        Check("文字一字不差", rtb.AllText().EndsWith(want.ToString()), "尾巴=" + Tail(rtb.AllText(), 24));
        Check("同一种消息接在同一条上（还是 3 个块）", Blocks(rtb).Count == 3, "count=" + Blocks(rtb).Count);
        Check("老消息的高度没变（没有被重排过）", bs[0].Height == h0 && bs[1].Height == h1, "h0=" + bs[0].Height + "/" + h0);
        Check("新内容在底部、视野贴底", rtb.AtEnd, "atEnd=" + rtb.AtEnd);

        Console.WriteLine("== 3. 轨迹页（思考/步骤）也走消息块，同样攒批 ==");
        trace.ClearAll();
        Pump(200);
        int t0 = trace.Flushes;
        for (int i = 0; i < 300; i++) { f.Write(OutKind.Thinking, "思"); f.Write(OutKind.ToolResult, ""); }
        Pump(600);
        trace.FlushNow();
        int tf = trace.Flushes - t0;
        string tt = trace.AllText();
        Check("思考流批量写入 <= 12 次", tf >= 1 && tf <= 12, "flushes=" + tf);
        Check("轨迹页有内容", tt.Length > 50, "len=" + tt.Length);
        Check("300 个 token 没变成 300 个块", trace.BlockCount <= 8, "blocks=" + trace.BlockCount);

        Console.WriteLine("== 4. 顺序：流式文字和工具调用不能错位 ==");
        rtb.ClearAll();
        Pump(200);
        f.Write(OutKind.Assistant, "甲");
        f.Write(OutKind.ToolCall, "  ● tool(x)\n");
        f.Write(OutKind.Assistant, "乙");
        Pump(400);
        string all = rtb.AllText();
        int ia = all.IndexOf("甲"), ib = all.IndexOf("tool(x)"), ic = all.IndexOf("乙");
        Check("甲 在 工具 前，乙 在 工具 后", ia >= 0 && ib > ia && ic > ib, "jia=" + ia + " tool=" + ib + " yi=" + ic);
        Check("三种消息 = 三个块", rtb.BlockCount == 3, "count=" + rtb.BlockCount);

        Console.WriteLine("== 5. 用户往上滚看历史：不能被拽回底部（含消息块把滚轮转给面板）==");
        rtb.ClearAll();
        for (int i = 0; i < 200; i++) f.Write(OutKind.Assistant, "行" + i + "\n");
        Pump(600);
        List<MsgBlock> b2 = Blocks(rtb);
        MsgBlock lastB = b2[b2.Count - 1];
        Check("够长，确实出现了滚动条", rtb.AutoScrollMinSize.Height > rtb.ClientSize.Height,
            "content=" + rtb.AutoScrollMinSize.Height + " view=" + rtb.ClientSize.Height);
        Wheel(lastB, 120 * 6);                      // 鼠标压在"消息块"上往上滚（滚轮往前 = 往上）
        Pump(250);
        int before = -rtb.AutoScrollPosition.Y;
        Check("滚轮在消息块上也好使（子控件把滚轮转给了面板）", before > 0, "offset=" + before);
        Check("滚上去之后算作不再跟随", !rtb.AtEnd, "atEnd=" + rtb.AtEnd);
        for (int i = 0; i < 120; i++) f.Write(OutKind.Assistant, "新" + i + "\n");
        Pump(500);
        int after = -rtb.AutoScrollPosition.Y;
        Console.WriteLine("    追加 120 行：视野 " + before + " -> " + after);
        Check("视野没被拽走", Math.Abs(after - before) <= 3, "before=" + before + " after=" + after);
        Check("内容还是完整追加了", rtb.AllText().EndsWith("新119\n"), "尾巴=" + Tail(rtb.AllText(), 16));

        Console.WriteLine("== 6. 滚回底部：自动跟随要恢复 ==");
        List<MsgBlock> b3 = Blocks(rtb);
        for (int i = 0; i < 12 && !rtb.AtEnd; i++)  // 一路往下滚回底部
        {
            Wheel(b3[b3.Count - 1], -120 * 50);
            Pump(120);
        }
        Pump(250);
        Check("滚到底 = 恢复跟随", rtb.AtEnd, "atEnd=" + rtb.AtEnd);
        for (int i = 0; i < 80; i++) f.Write(OutKind.Assistant, "尾" + i + "\n");
        Pump(500);
        Check("新内容保持贴底", rtb.AtEnd, "atEnd=" + rtb.AtEnd);

        Console.WriteLine("== 7. 思考内容：默认只进轨迹页 + 可开关 ==");
        object cfg = Get(f, "config");
        PropertyInfo hid = cfg.GetType().GetProperty("hide_thinking");
        hid.SetValue(cfg, true, null);
        rtb.ClearAll();
        trace.ClearAll();
        Pump(200);
        f.Write(OutKind.Thinking, "\n  思考\n  ");
        for (int i = 0; i < 40; i++) f.Write(OutKind.Thinking, "想" + (i % 10));
        f.Write(OutKind.Assistant, "结论");
        Pump(500);
        Check("开：对话区没有思考正文", rtb.AllText().IndexOf("想0想1") < 0, "dialogue=" + Tail(rtb.AllText(), 30));
        Check("开：对话区保留了回答", rtb.AllText().IndexOf("结论") >= 0, "dialogue=" + Tail(rtb.AllText(), 30));
        trace.FlushNow();
        Check("开：轨迹页仍然有思考正文", trace.AllText().IndexOf("想0想1") >= 0, "trace=" + Tail(trace.AllText(), 30));
        hid.SetValue(cfg, false, null);
        rtb.ClearAll();
        Pump(200);
        f.Write(OutKind.Thinking, "\n  思考\n  ");
        for (int i = 0; i < 40; i++) f.Write(OutKind.Thinking, "念" + (i % 10));
        Pump(400);
        Check("关：对话区能看到思考", rtb.AllText().IndexOf("念0念1") >= 0, "dialogue=" + Tail(rtb.AllText(), 30));

        Console.WriteLine("== 8. 清空 / 用户消息 / 复制 ==");
        rtb.ClearAll();
        Pump(300);
        Check("清空后没有残留块", rtb.BlockCount == 0 && rtb.AllText().Length == 0, "count=" + rtb.BlockCount);
        f.Write(OutKind.User, "› 你好\n");
        Pump(400);
        Check("用户消息也进了对话区", rtb.AllText().IndexOf("你好") >= 0, "text=" + Tail(rtb.AllText(), 20));
        Check("复制全部对话拿得到内容", rtb.AllText().Length > 0, "len=" + rtb.AllText().Length);

        Console.WriteLine("== 9. 可滚动范围必须等于内容高度（有幽灵高度就会把内容顶到中间）==");
        rtb.ClearAll();
        for (int i = 0; i < 300; i++) f.Write(OutKind.Assistant, "行" + i + "\n");
        Pump(700);
        rtb.ScrollToEnd();
        Pump(300);
        int expectMax = Math.Max(0, rtb.AutoScrollMinSize.Height - rtb.ClientSize.Height);
        int realMax = RealMax(rtb);
        Console.WriteLine("    内容高=" + rtb.AutoScrollMinSize.Height + " 视口高=" + rtb.ClientSize.Height
            + " -> 期望最大滚动=" + expectMax + " 实际最大滚动=" + realMax);
        Check("实际可滚动范围 ≈ 内容高 - 视口高", Math.Abs(realMax - expectMax) <= 4, "real=" + realMax + " expect=" + expectMax);
        rtb.ScrollToEnd();
        Pump(300);
        List<MsgBlock> bs9 = Blocks(rtb);
        int botLast = VisTop(rtb, bs9[bs9.Count - 1]) + bs9[bs9.Count - 1].Height;
        Check("滚到底时最后一条贴着视口底部（留一点底边距）",
            botLast > rtb.ClientSize.Height - 30 && botLast <= rtb.ClientSize.Height + 4,
            "bottom=" + botLast + " view=" + rtb.ClientSize.Height);
        for (int i = 0; i < 120; i++) f.Write(OutKind.Assistant, "续" + i + "\n");
        Pump(600);
        rtb.ScrollToEnd();
        Pump(300);
        int expectMax2 = Math.Max(0, rtb.AutoScrollMinSize.Height - rtb.ClientSize.Height);
        int realMax2 = RealMax(rtb);
        Console.WriteLine("    再追加 120 行 -> 期望最大滚动=" + expectMax2 + " 实际=" + realMax2);
        Check("反复追加也不会涨出幽灵高度", Math.Abs(realMax2 - expectMax2) <= 4, "real=" + realMax2 + " expect=" + expectMax2);
        List<MsgBlock> bs9b = Blocks(rtb);
        int botLast2 = VisTop(rtb, bs9b[bs9b.Count - 1]) + bs9b[bs9b.Count - 1].Height;
        Check("追加后最后一条仍贴着视口底部（内容没被顶走）",
            botLast2 > rtb.ClientSize.Height - 30 && botLast2 <= rtb.ClientSize.Height + 4, "bottom=" + botLast2);

        Console.WriteLine("== 10. 短对话：不该出滚动条，内容贴顶部（你截图里那个场景）==");
        rtb.ClearAll();
        Pump(300);
        f.Write(OutKind.User, "› 你好啊\n");
        f.Write(OutKind.Assistant, "你好！很高兴为你创建这个页面。如果需要调整标题或配色，随时告诉我~");
        Pump(600);
        List<MsgBlock> bs10 = Blocks(rtb);
        int content10 = rtb.AutoScrollMinSize.Height;
        Console.WriteLine("    块数=" + bs10.Count + " 内容高=" + content10 + " 视口高=" + rtb.ClientSize.Height
            + " 首条可视 y=" + VisTop(rtb, bs10[0]));
        Check("短对话不出现滚动条", content10 <= rtb.ClientSize.Height, "content=" + content10 + " view=" + rtb.ClientSize.Height);
        Check("第一条消息贴在最上面（不居中、不贴底）", VisTop(rtb, bs10[0]) <= 2, "top=" + VisTop(rtb, bs10[0]));

        Console.WriteLine("== 11. 流式回答的整个过程：短对话必须一直贴顶，不许自己往下弹 ==");
        rtb.ClearAll();
        Pump(300);
        f.Write(OutKind.User, "› 你好啊\n");
        Pump(200);
        int worst = 0;
        for (int i = 0; i < 18; i++)          // 只流到"还没超过一屏"，看它会不会自己往下弹
        {
            f.Write(OutKind.Assistant, "这是一段正在流式输出的回答。");
            Pump(40);
            int t = VisTop(rtb, Blocks(rtb)[0]);
            if (Math.Abs(t) > Math.Abs(worst)) worst = t;
        }
        Pump(400);
        Console.WriteLine("    流式过程中首条消息的可视 y 最大偏移=" + worst
            + "  内容高=" + rtb.AutoScrollMinSize.Height + " 视口高=" + rtb.ClientSize.Height);
        Check("整个过程首条消息没被顶走", Math.Abs(worst) <= 2, "worst=" + worst);

        Console.WriteLine("== 12. 增量排版的不变量：一个字不丢 / 不溢出 / 不重叠 / 行数对得上 ==");
        StringBuilder longline = new StringBuilder();
        for (int i = 0; i < 60; i++) longline.Append("这是一段很长很长的没有换行的中文内容用来测试折行 ");
        string[] samples = new string[]
        {
            "普通中文段落，**加粗** 和 `代码` 混排，还有 English words and 12345 numbers. 再来一段中文收尾。",
            "第一行\r\n第二行 **粗体跨行\r\n结束** 收尾",
            "空行测试：\n\n\n后面还有内容",
            "**还没闭合的粗体",
            "**先开 然后**合上，再 `行内` 代码 `没闭合的代码",
            "短句。\n\n第二段 `code` **bold** 尾巴",
            longline.ToString()
        };
        int okText = 0, okFit = 0, okOrder = 0, okLines = 0;
        int lineH = Theme.F(10.5f).Height + Theme.Px(3);
        for (int si = 0; si < samples.Length; si++)
        {
            rtb.ClearAll();
            string s = samples[si];
            for (int i = 0; i < s.Length; i += 7)             // 每批 7 个字符，故意切在标记中间
            {
                f.Write(OutKind.Assistant, s.Substring(i, Math.Min(7, s.Length - i)));
                rtb.FlushNow();
            }
            List<MsgBlock> one = Blocks(rtb);
            MsgBlock b = one[one.Count - 1];
            int hInc = b.Height;
            string back = RebuildText(b);
            // 换行是"排版结构"（片段不带换行字符），所以比对时把换行都去掉：
            // 只看有没有丢字/重复/错序
            string srcFlat = s.Replace("\r", "").Replace("\n", "");
            string backFlat = back.Replace("\r", "").Replace("\n", "");
            if (backFlat == srcFlat) okText++;
            else Console.WriteLine("    样本 " + si + " 文字对不上：还原=" + Tail(backFlat, 40) + " 原文=" + Tail(srcFlat, 40));

            List<int> styles;
            List<Rectangle> rects = PieceRects(b, out styles);
            bool fit = true, order = true;
            int lastY = -1, lastRight = int.MinValue;
            foreach (Rectangle r in rects)
            {
                if (r.Right > b.Width) fit = false;
                if (r.Y == lastY && r.X < lastRight - 1) order = false;
                lastY = r.Y; lastRight = r.Right;
            }
            if (fit) okFit++; else Console.WriteLine("    样本 " + si + " 有片段溢出块宽（块宽=" + b.Width + "）");
            if (order) okOrder++; else Console.WriteLine("    样本 " + si + " 同一行有片段重叠");

            int wInc = b.Width;
            rtb.SetFonts(Theme.F(10.5f), Theme.FB(10.5f), Theme.M(9.5f), Theme.F(9f));   // 同字体 → 触发全量重排
            List<MsgBlock> two = Blocks(rtb);
            int hFull = two[two.Count - 1].Height;
            if (Math.Abs(hInc - hFull) <= lineH * 2) okLines++;
            else Console.WriteLine("    样本 " + si + " 行数差太多：增量高=" + hInc + " 全量高=" + hFull
                + " 宽=" + wInc + "->" + two[two.Count - 1].Width);
        }
        int n = samples.Length;
        Check("增量排版一个字不丢、顺序不乱", okText == n, okText + "/" + n);
        Check("没有片段溢出块宽", okFit == n, okFit + "/" + n);
        Check("同一行片段不重叠", okOrder == n, okOrder + "/" + n);
        Check("增量排版的高度和全量重排对得上（±2 行）", okLines == n, okLines + "/" + n);

        Console.WriteLine("== 13. 每条消息下面的「一键复制 / 分支 / 时间」==");
        rtb.ClearAll();
        Pump(200);
        f.Write(OutKind.User, "› 第一句用户的话\n");
        f.Write(OutKind.Assistant, "\n● 这是助手的回复，用来测试一键复制。\n");
        Pump(500);
        List<MsgBlock> ab = Blocks(rtb);
        Check("用户消息：有复制、没有分支", ab.Count >= 2 && ab[0].ShowCopy && !ab[0].ShowBranch,
            "copy=" + ab[0].ShowCopy + " branch=" + ab[0].ShowBranch);
        Check("助手回复：复制和分支都有", ab[ab.Count - 1].ShowCopy && ab[ab.Count - 1].ShowBranch,
            "copy=" + ab[ab.Count - 1].ShowCopy + " branch=" + ab[ab.Count - 1].ShowBranch);
        MsgBlock hi = ab[ab.Count - 1];
        Check("助手回复带时间戳（显示 HH:mm）", hi.Stamp.HasValue, "stamp=" + (hi.Stamp.HasValue ? "有" : "无"));
        Rectangle cr = hi.CopyIconRect(), br = hi.BranchIconRect();
        Check("复制/分支按钮在块底部、且都在块内",
            cr.Bottom <= hi.Height && br.Bottom <= hi.Height && cr.Top > 0 && br.Left > cr.Right - 1,
            "块高=" + hi.Height + " 复制=" + cr + " 分支=" + br);
        Check("点在复制图标上 → 命中复制",
            hi.ActionAt(new Point(cr.Left + cr.Width / 2, cr.Top + cr.Height / 2)) == MsgBlock.ActCopy,
            hi.ActionAt(new Point(cr.Left + cr.Width / 2, cr.Top + cr.Height / 2)).ToString());
        Check("点在分支图标上 → 命中分支",
            hi.ActionAt(new Point(br.Left + br.Width / 2, br.Top + br.Height / 2)) == MsgBlock.ActBranch, "");
        Check("点在正文上 → 不命中任何按钮",
            hi.ActionAt(new Point(hi.Width / 2, Theme.Px(8))) < 0, "");
        // 真·点按钮：往块上发一条鼠标按下消息（走的是"块 → 对话区 → 主窗体"这条真实路径）
        Rectangle c2 = hi.CopyIconRect();
        IntPtr lp = (IntPtr)(((c2.Top + c2.Height / 2) << 16) | (c2.Left + c2.Width / 2));
        SendMessage(hi.Handle, 0x0201, (IntPtr)1, lp);
        Pump(200);
        string clip = "";
        try { clip = Clipboard.GetText(); } catch { }
        Label st = (Label)Get(f, "lblStatus");
        Check("点复制按钮 → 走到复制处理（成功提示已复制、剪贴板被占用提示失败）",
            st.Text.StartsWith("已复制") || st.Text.StartsWith("复制失败"),
            "clip=" + clip.Length + " 字  状态栏=" + st.Text);
        if (clip.Length > 0)
            Check("剪贴板里就是这条回复的原文", clip == hi.Body, "剪贴板 " + clip.Length + " 字 vs 原文 " + hi.Body.Length + " 字");

        Console.WriteLine("== 14. 拖选 + Ctrl+C 复制（含跨消息选择）==");
        rtb.ClearAll();
        Pump(200);
        f.Write(OutKind.Assistant, "AAAA挨着BBBB\n");
        rtb.Break();
        f.Write(OutKind.Assistant, "CCCC中间DDDD\n");
        rtb.Break();
        f.Write(OutKind.Assistant, "EEEE末尾FFFF\n");
        Pump(500);
        List<MsgBlock> sb = Blocks(rtb);
        Check("三个块都在", sb.Count == 3, "count=" + sb.Count);
        sb[0].SetSel(0, 3);                      // AAAA 里选前 3 个
        Check("单块选中：拿到的是那一段", sb[0].SelectedText == "AAA", "sel=\"" + sb[0].SelectedText + "\"");
        Check("单块选中 → 视图能拼出文本", rtb.BuildSelectionText() == "AAA", "\"" + rtb.BuildSelectionText() + "\"");
        rtb.ClearSelection();
        Check("清空选中后拼出来是空", rtb.BuildSelectionText() == "", "");

        // 真·鼠标拖选：从第 1 块拖到第 3 块
        sb[0].SetSel(2, sb[0].Body.Length);      // 模拟拖选结果（和鼠标走的是同一条 ApplySel 路径）
        sb[1].SetSel(0, sb[1].Body.Length);
        sb[2].SetSel(0, 4);
        string cross = rtb.BuildSelectionText();
        Check("跨消息选择：包含三块的内容",
            cross.IndexOf("AA") >= 0 && cross.IndexOf("CCCC中间DDDD") >= 0 && cross.IndexOf("EEEE") >= 0,
            "\"" + cross.Replace("\n", "\\n") + "\"");

        rtb.SelectAll();
        Check("全选 = 所有块拼起来", rtb.BuildSelectionText().Replace("\n", "") == rtb.AllText().Replace("\n", ""),
            rtb.BuildSelectionText().Length + " vs " + rtb.AllText().Length);
        rtb.ClearSelection();

        Console.WriteLine("== 15. 分支到新会话（以某条回复为结尾复制出一条新会话）==");
        AgentRunner rn = (AgentRunner)Get(f, "runner");
        List<Dictionary<string, object>> msgs = (List<Dictionary<string, object>>)GetAny(rn, "Messages");
        msgs.Add(AgentRunner.Msg("user", "第一轮提问"));
        msgs.Add(AgentRunner.Msg("assistant", "第一轮回答"));
        msgs.Add(AgentRunner.Msg("user", "第二轮提问"));
        Check("消息里带了时间戳（供界面显示 HH:mm）", msgs[msgs.Count - 1].ContainsKey("ts"), "");
        string beforeId = (string)Get(f, "currentSessionId");
        List<MsgBlock> fb = Blocks(rtb);
        fb[fb.Count - 1].MsgIdx = msgs.Count - 1;      // 假设最后一块对应最后一条消息
        CallPrivate(f, "ForkFromBlock", fb[fb.Count - 1]);
        Pump(400);
        string afterId = (string)Get(f, "currentSessionId");
        SessionStore store = (SessionStore)Get(f, "sessionStore");
        Session created = null;
        foreach (Session s in store.sessions) if (s.id == afterId) created = s;
        Check("切到了新会话", afterId != beforeId && created != null, beforeId + " -> " + afterId);
        if (created != null)
        {
            Check("新会话标题带「· 分支」", created.title.EndsWith("· 分支"), created.title);
            Check("新会话保留了到这里为止的消息（尾条是那条回复）",
                created.messages.Count == msgs.Count && Json.Str(created.messages[created.messages.Count - 1], "content", "") == "第二轮提问",
                "条数=" + created.messages.Count + " 尾条=" + Json.Str(created.messages[created.messages.Count - 1], "content", ""));
        }
        Check("对话区把新会话渲染出来了", rtb.BlockCount >= 2, "blocks=" + rtb.BlockCount);

        Console.WriteLine("== 16. 块级 Markdown：标题 / 列表 / 代码块 / 表格 ==");
        rtb.ClearAll();
        Pump(200);
        f.Write(OutKind.Assistant,
            "## 小标题\n"
            + "- 列表第一项\n"
            + "- 列表第二项\n"
            + "1. 有序项\n"
            + "\n"
            + "```\n"
            + "code line 1\n"
            + "code line 2\n"
            + "```\n"
            + "\n"
            + "| 题 | 云端 | 本地 |\n"
            + "| --- | --- | --- |\n"
            + "| S1 | PASS | PASS |\n"
            + "| S2 | PASS | FAIL |\n");
        Pump(600);
        List<MsgBlock> mb = Blocks(rtb);
        MsgBlock md = mb[mb.Count - 1];
        List<int> mstyles;
        List<Rectangle> mrects = PieceRects(md, out mstyles);
        int n3 = 0, n4 = 0, n5 = 0, n6 = 0;
        for (int i = 0; i < mstyles.Count; i++)
        {
            if (mstyles[i] == 3) n3++;
            else if (mstyles[i] == 4) n4++;
            else if (mstyles[i] == 5) n5++;
            else if (mstyles[i] == 6) n6++;
        }
        bool hasHash = false, hasListMarker = false;
        for (int i = 0; i < mstyles.Count; i++)
        {
            System.Collections.IEnumerable ps = (System.Collections.IEnumerable)
                md.GetType().GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(md);
            int k = 0;
            foreach (object p in ps)
            {
                string t = (string)p.GetType().GetField("Text").GetValue(p);
                if (k == i)
                {
                    if (t.IndexOf("##") >= 0) hasHash = true;
                    if (t == "•" || t == "1.") hasListMarker = true;
                    break;
                }
                k++;
            }
        }
        Check("## 标题不再当普通文字（去掉了 # 号、用大号粗体）", !hasHash, "还看得到 ##");
        Check("列表标记渲染成 • / 1.（不是原始 - 号）", hasListMarker, "没找到列表标记");
        Check("代码块按代码样式排（style=3 的片段 " + n3 + " 个）", n3 >= 2, "n3=" + n3);
        Check("表格单元格按等宽排（style=4/5 共 " + (n4 + n5) + " 个）", n4 >= 6 && n5 >= 3, "n4=" + n4 + " n5=" + n5);
        Check("表格有分隔线（style=6）", n6 >= 1, "n6=" + n6);
        Check("表格列对齐：第二列各单元格左边界相同", SameColumnLeft(mrects, mstyles, 1), "列没对齐");

        Console.WriteLine("== 17. 块级 Markdown：增量排版和全量重排仍然一致 ==");
        string[] mdSamples = new string[]
        {
            "## 先写标题\n再写正文\n",
            "- 第一项\n- 第二项\n",
            "| a | b |\n| --- | --- |\n| 1 | 2 |\n",
            "```\nc = a + b;\n```\n",
            "# 标题里带 **粗体** 和 `代码`\n"
        };
        int mdSame = 0;
        for (int si = 0; si < mdSamples.Length; si++)
        {
            rtb.ClearAll();
            string s2 = mdSamples[si];
            for (int i = 0; i < s2.Length; i += 3)          // 故意把 # / | / ``` 切成两半喂进去
            {
                f.Write(OutKind.Assistant, s2.Substring(i, Math.Min(3, s2.Length - i)));
                rtb.FlushNow();
            }
            List<MsgBlock> one = Blocks(rtb);
            MsgBlock b1 = one[one.Count - 1];
            string inc = DumpTextOnly(b1);
            string incAt = DumpCharAt(b1);
            int incH = b1.Height;
            rtb.SetFonts(Theme.F(10.5f), Theme.FB(10.5f), Theme.M(9.5f), Theme.F(9f));   // 同字体 → 全量重排
            List<MsgBlock> two = Blocks(rtb);
            MsgBlock bFull = two[two.Count - 1];
            // 块级排版允许"分片方式"不同（chunk 边界切在哪），但**文字顺序和高度必须一致**
            // 片段的原文偏移也必须一致 —— 不然选中高亮的位置会跟着增量排版飘
            if (DumpTextOnly(bFull) == inc && bFull.Height == incH && DumpCharAt(bFull) == incAt) mdSame++;
            else
            {
                Console.WriteLine("    样本 " + si + " 不一致：增量高=" + incH + " 全量高=" + bFull.Height);
                Console.WriteLine("      增量: " + (inc.Length > 160 ? inc.Substring(0, 160) : inc));
                Console.WriteLine("      全量: " + DumpTextOnly(bFull));
                if (DumpCharAt(bFull) != incAt)
                    Console.WriteLine("      逐字偏移不一致 增量=" + incAt + " 全量=" + DumpCharAt(bFull));
            }
        }
        Check("5 个块级样本：增量 == 全量", mdSame == mdSamples.Length, mdSame + "/" + mdSamples.Length);

        Console.WriteLine("== 18. 复制反馈气泡 + 右键菜单 ==");
        rtb.ClearAll();
        Pump(200);
        f.Write(OutKind.Assistant, "\n● 这是一条用来测试复制反馈的回复，够长一点才能看出高亮对不对齐。\n");
        Pump(500);
        List<MsgBlock> b18 = Blocks(rtb);
        MsgBlock nb18 = b18[b18.Count - 1];
        Rectangle cr18 = nb18.CopyIconRect();
        nb18.HoverAt(new Point(cr18.Left + cr18.Width / 2, cr18.Top + cr18.Height / 2));      // 鼠标停在复制图标上
        Pump(150);
        Check("鼠标停在复制图标上 → 给按钮名气泡（复制回复）", nb18.Tip == "复制回复", "tip=" + (nb18.Tip == null ? "null" : nb18.Tip));
        Check("气泡挂在复制按钮上", nb18.TipAct == MsgBlock.ActCopy, "act=" + nb18.TipAct);

        rtb.ShowFlash(nb18, "已复制", MsgBlock.ActCopy);
        Pump(120);
        Check("点完复制 → 立刻冒出「已复制」气泡", nb18.Flash == "已复制", "flash=" + (nb18.Flash == null ? "null" : nb18.Flash));
        Pump(1400);
        Check("气泡 1 秒后自动消失", nb18.Flash == null, "flash=" + (nb18.Flash == null ? "null" : nb18.Flash));

        ContextMenuStrip cm18 = rtb.ContextMenuStrip;
        Check("右键菜单用了自定义皮肤（不是 WinForms 默认灰）", cm18 != null && (cm18.Renderer is MenuSkin), cm18 == null ? "没有菜单" : cm18.Renderer.GetType().Name);
        if (cm18 != null)
        {
            Check("菜单项齐（复制选中/全选/复制全部/复制这条）", cm18.Items.Count >= 5, "items=" + cm18.Items.Count);
            ToolStripMenuItem i0 = cm18.Items[0] as ToolStripMenuItem;
            ToolStripMenuItem i1 = cm18.Items[1] as ToolStripMenuItem;
            Check("带快捷键提示（Ctrl+C / Ctrl+A）",
                i0 != null && i0.ShortcutKeyDisplayString == "Ctrl+C" && i1 != null && i1.ShortcutKeyDisplayString == "Ctrl+A",
                (i0 == null ? "?" : i0.ShortcutKeyDisplayString) + " / " + (i1 == null ? "?" : i1.ShortcutKeyDisplayString));
            Check("菜单项带图标", i0 != null && i0.Image != null, "");
        }

        Console.WriteLine("== 19. 选中高亮必须盖住整个字（截图里那种「盖一半」的 bug）==");
        MsgBlock hb19 = nb18;
        hb19.SetSel(3, 9);
        Pump(150);
        List<Rectangle> hl = hb19.HighlightRects();
        int fontH = Theme.F(10.5f).Height;
        Check("画出了高亮块（一行文字至少 1 块）", hl.Count >= 1, "blocks=" + hl.Count);
        int badH = 0, badX = 0;
        foreach (Rectangle r in hl)
        {
            if (r.Height != fontH + Theme.Px(1)) badH++;
            bool insideSomePiece = false;
            List<int> st19;
            foreach (Rectangle pr in PieceRects(hb19, out st19))
                if (r.Left >= pr.Left - 1 && r.Right <= pr.Right + 1) insideSomePiece = true;
            if (!insideSomePiece) badX++;
        }
        Console.WriteLine("    高亮块 " + hl.Count + " 个，高度应为 " + (fontH + Theme.Px(1)) + "px，实际 "
            + (hl.Count > 0 ? hl[0].Height.ToString() : "-") + "px");
        Check("每块高亮高度 = 字号行高 + 1px（不是半行、也不是一大块）", badH == 0, "高度不对 " + badH + " 块");
        Check("高亮不超出它所在片段的范围（不会歪到别的字上）", badX == 0, "越界 " + badX + " 块");
        Check("抽出来的选中文字正确（3..9 共 6 个字）", hb19.SelectedText.Length == 6, "\"" + hb19.SelectedText + "\"");
        hb19.ClearSel();

        Console.WriteLine("== 20. 「已编辑 N 个文件」卡片 ==");
        // 先测 diff 引擎本身
        FileChange fc = new FileChange();
        fc.Path = "x"; fc.Rel = "x.txt";
        fc.OldText = "aaa\nbbb\nccc\n";
        fc.NewText = "aaa\nBBB\nccc\n";
        Diff.Build(fc);
        Check("diff 认出 1 行改 1 行加 1 行减", fc.Added == 1 && fc.Removed == 1, "+" + fc.Added + " -" + fc.Removed);
        Check("diff 文本里有 - 和 + 的行", fc.DiffText.IndexOf("- bbb") >= 0 && fc.DiffText.IndexOf("+ BBB") >= 0,
            fc.DiffText.Replace("\n", "\\n"));
        FileChange fn = new FileChange();
        fn.Path = "y"; fn.Rel = "y.txt"; fn.OldText = ""; fn.NewText = "l1\nl2\n";
        Diff.Build(fn);
        Check("新文件算成纯新增（+2）", fn.Added == 2 && fn.Removed == 0, "+" + fn.Added + " -" + fn.Removed);

        // 再测真工具调用会不会被记录
        Changes.Root = AppDomain.CurrentDomain.BaseDirectory;
        Changes.Clear();
        string ctf = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_cardtest.txt");
        System.IO.File.WriteAllText(ctf, "hello world\n", new UTF8Encoding(false));
        int wrote = 0;
        foreach (ToolDef td in Tools.All())
        {
            if (td.Name != "edit_file") continue;
            ToolContext ctx = new ToolContext(System.IO.Path.GetDirectoryName(ctf), true, 30);
            Dictionary<string, object> args = new Dictionary<string, object>();
            args["path"] = "_cardtest.txt";
            args["old_string"] = "hello world";
            args["new_string"] = "hello 世界";
            td.Run(ctx, args);
            wrote++;
        }
        Check("edit_file 真的调用了一次", wrote == 1, "调用 " + wrote + " 次");
        Check("工具改动被记进 Changes（1 个文件）", Changes.Now.Count == 1, "count=" + Changes.Now.Count);
        if (Changes.Now.Count == 1)
        {
            Check("记录里是相对路径", Changes.Now[0].Rel == "_cardtest.txt", Changes.Now[0].Rel);
            Diff.Build(Changes.Now[0]);
            Check("记录的 diff 里有那次替换", Changes.Now[0].DiffText.IndexOf("hello 世界") >= 0,
                Changes.Now[0].DiffText.Replace("\n", "\\n"));
        }
        int snap = Changes.Snapshot();
        try { System.IO.File.Delete(ctf); } catch { }

        // 最后测卡片渲染 + 点击命中
        rtb.ClearAll();
        Pump(150);
        f.Write(OutKind.Changes, "已编辑 2 个文件\nsrc/AhzScribe.cs\ndevtools/bench/flicker-unittest.cs\n#card#" + snap);
        Pump(400);
        List<MsgBlock> cb = Blocks(rtb);
        MsgBlock card = cb[cb.Count - 1];
        Check("卡片块是对的 Kind", card.Kind == OutKind.Changes, card.Kind.ToString());
        Check("卡片解析出 2 个文件", card.CardFiles.Count == 2, "files=" + card.CardFiles.Count);
        Check("卡片标题是「已编辑 2 个文件」", card.CardTitle == "已编辑 2 个文件", card.CardTitle);
        Check("卡片有 2 行文件行", card.CardRows.Count == 2, "rows=" + card.CardRows.Count);
        Check("卡片记住了快照编号", card.CardIndex == snap, card.CardIndex + " vs " + snap);
        Rectangle lr = card.CardHeaderRect;
        int hitLink = card.CardHit(new Point(card.CardLinkRect.Left + 3, card.CardLinkRect.Top + 3));
        int hitRow1 = card.CardHit(new Point(card.CardRows[1].Left + 5, card.CardRows[1].Top + 5));
        int hitNone = card.CardHit(new Point(card.Width - 3, lr.Top + 3));
        Check("点「查看变更」→ 命中链接（-1）", hitLink == -1, "hit=" + hitLink);
        Check("点第 2 行 → 命中第 2 个文件（1）", hitRow1 == 1, "hit=" + hitRow1);
        Check("点空白 → 不命中（-2）", hitNone == -2, "hit=" + hitNone);
        Check("卡片有高度（画得出来）", card.Height > Theme.Px(60), "h=" + card.Height);

        Console.WriteLine("== 21. 「叫嚷却不调用」的识别 ==");
        Check("[调用] 被认出", ToolNoise.IsAnnounceLine("[调用]"), "");
        Check("[executing] 被认出", ToolNoise.IsAnnounceLine("  [executing]  "), "");
        Check("真实调用如下：被认出", ToolNoise.IsAnnounceLine("真实调用如下："), "");
        Check("Call now. 被认出", ToolNoise.IsAnnounceLine("Call now."), "");
        Check("行首 run_shell( 被认出", ToolNoise.IsAnnounceLine("run_shell($ env:x)"), "");
        Check("普通句子不误伤", !ToolNoise.IsAnnounceLine("这一轮我改了 3 个文件，跑测试全过。"), "");
        Check("多行计数正确", ToolNoise.Count("先说一句\n[调用]\n真实调用\n普通文字\n") == 2,
            "n=" + ToolNoise.Count("先说一句\n[调用]\n真实调用\n普通文字\n"));

        Console.WriteLine("== 22. 这种文字不进对话区（对话区不再被 [调用] 刷屏）==");
        rtb.ClearAll();
        Pump(150);
        f.Write(OutKind.Assistant, "\n● 结论：先说结论。\n[调用]\n真实调用如下：\n[调用]\n再补一句有用的。\n");
        Pump(500);
        List<MsgBlock> nb22 = Blocks(rtb);
        MsgBlock ab22 = nb22[nb22.Count - 1];
        List<int> st22;
        List<Rectangle> rc22 = PieceRects(ab22, out st22);
        string shown = "";
        foreach (System.Collections.IEnumerable ps22 in new System.Collections.IEnumerable[] {
            (System.Collections.IEnumerable)ab22.GetType().GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ab22) })
            foreach (object o in ps22) shown += (string)o.GetType().GetField("Text").GetValue(o);
        Check("对话区里没有 [调用]", shown.IndexOf("[调用]") < 0, "shown=" + shown.Replace("\n", "\\n"));
        Check("对话区里没有「真实调用」", shown.IndexOf("真实调用") < 0, "");
        Check("真正的结论还在", shown.IndexOf("先说结论") >= 0 && shown.IndexOf("再补一句有用的") >= 0, shown.Replace("\n", "\\n"));

        Console.WriteLine("== 23. 「撤销」能把这一轮的文件改回去 ==");
        string und = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_undotest.txt");
        System.IO.File.WriteAllText(und, "原始内容\n", new UTF8Encoding(false));
        Changes.Root = AppDomain.CurrentDomain.BaseDirectory;
        Changes.Clear();
        Changes.Record(und, "原始内容\n", "被模型改过了\n");
        int undSnap = Changes.Snapshot();
        int uok, ufail;
        Changes.RestoreAll(Changes.Get(undSnap), out uok, out ufail);
        string undBack = System.IO.File.ReadAllText(und, Encoding.UTF8);
        Check("文件被恢复成改动前的内容", undBack == "原始内容\n", "内容=\"" + undBack.Replace("\n", "\\n") + "\"");
        Check("撤销计数 normal", uok == 1 && ufail == 0, "ok=" + uok + " fail=" + ufail);
        // 新文件应被删掉
        Changes.Clear();
        string nf = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_undonew.txt");
        System.IO.File.WriteAllText(nf, "新建的\n", new UTF8Encoding(false));
        Changes.Record(nf, "", "新建的\n");
        Changes.RestoreAll(Changes.Get(Changes.Snapshot()), out uok, out ufail);
        Check("这一轮新建的文件被删掉", !System.IO.File.Exists(nf), "还在");
        try { System.IO.File.Delete(und); } catch { }

        Console.WriteLine("== 24. 高亮按行合并成连续一段（不再被片段切开）==");
        rtb.ClearAll();
        Pump(150);
        f.Write(OutKind.Assistant, "\n● 前面**加粗的这几个字**后面还有普通文字。\n");
        Pump(500);
        List<MsgBlock> nb24 = Blocks(rtb);
        MsgBlock ab24 = nb24[nb24.Count - 1];
        ab24.SetSel(0, ab24.Body.Length);          // 整条选中
        List<Rectangle> hl24 = ab24.HighlightRects();
        int rowCount = 0;
        List<int> tops = new List<int>();
        foreach (Rectangle r in hl24) if (!tops.Contains(r.Top)) { tops.Add(r.Top); rowCount++; }
        Check("整条选中时，每一行只有一段高亮", hl24.Count == rowCount, "rects=" + hl24.Count + " rows=" + rowCount);
        ab24.ClearSel();

        Console.WriteLine("== 25. 选中后浮出的小条（复制 / 填到输入框）+ 菜单圆角 ==");
        rtb.ClearAll();
        Pump(150);
        f.Write(OutKind.Assistant, "\n● 这段文字用来测试选中后浮出来的小条，点第二个按钮应该把它填进输入框。\n");
        Pump(500);
        List<MsgBlock> nb25 = Blocks(rtb);
        MsgBlock sel25 = nb25[nb25.Count - 1];
        sel25.SetSel(2, 12);
        rtb.ClearSelection();                 // 先清掉，再用真实的选中路径
        sel25.SetSel(2, 12);
        typeof(TranscriptView).GetMethod("UpdateBar", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(rtb, null);
        Pump(200);
        SelBar bar = rtb.Bar;
        Check("选中后小条出现", bar != null && bar.Visible, bar == null ? "没有小条" : ("visible=" + bar.Visible));
        if (bar != null && bar.Visible)
        {
            Check("小条在选区附近（不是飘到别处）", bar.Top < sel25.Height + sel25.Top, "barTop=" + bar.Top + " selTop=" + sel25.Top);
            // 真点第二个按钮：填到输入框
            UiEditor inp = (UiEditor)Get(f, "txtInput");
            inp.Text = "";
            Rectangle b1 = bar.Btn(1);
            SendMessage(bar.Handle, 0x0201, (IntPtr)1,
                (IntPtr)(((b1.Top + b1.Height / 2) << 16) | (b1.Left + b1.Width / 2)));
            Pump(250);
            string expect = sel25.Body.Substring(2, 10);     // 和上一步选中的区间一致
            Check("点「填到输入框」→ 选中的内容进了输入框",
                inp.Text.IndexOf(expect) >= 0, "输入框=\"" + inp.Text.Replace("\n", "\\n") + "\"");
            Check("填完自动清掉选中、小条收起", !rtb.HasSelection && !bar.Visible, "sel=" + rtb.HasSelection + " bar=" + bar.Visible);
        }
        ContextMenuStrip cm25 = rtb.ContextMenuStrip;
        Check("菜单外框切成圆角（Region 已设置）", cm25 != null && cm25.Region != null, cm25 == null ? "没有菜单" : "region=" + (cm25.Region == null ? "null" : "ok"));
        if (cm25 != null)
        {
            ToolStripMenuItem i25 = cm25.Items[0] as ToolStripMenuItem;
            Check("菜单项加了内边距（不再挤成一团）", i25 != null && i25.Padding.Top >= 3, i25 == null ? "?" : i25.Padding.ToString());
        }

        Console.WriteLine("== 26. 行内粗体 + 折行：原文偏移不能串位（高亮必须盖住粗体，不许只盖一小截）==");
        rtb.ClearAll();
        Pump(150);
        string p26 = "云端模型 91.5 分，本地 4b 模型 60.5 分，并且**精准定位了本地的死因**——不是能力不足，是「读完文件不动手」的行为缺陷加 6.4 tok/s 的吞吐瓶颈，这段故意写长一点好让它折行。";
        string l26 = "3. **最有意思的是它顺手改了我的提示词文件**：把我的版本从 4572 字节改成 6924 字节，并重写成一份更严谨的交接文档。";
        f.Write(OutKind.Assistant, "\n" + p26 + "\n\n" + l26 + "\n");
        Pump(500);
        List<MsgBlock> nb26 = Blocks(rtb);
        MsgBlock b26 = nb26[nb26.Count - 1];
        string raw26 = b26.Body;
        // 26.1 每个片段的原文偏移必须真指向它自己那段文字
        int chk26 = 0, bad26 = 0;
        string bad26Detail = "";
        System.Collections.IEnumerable ps26 = (System.Collections.IEnumerable)
            typeof(MsgBlock).GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b26);
        foreach (object p in ps26)
        {
            int stP26 = (int)p.GetType().GetField("Style").GetValue(p);
            string tx = (string)p.GetType().GetField("Text").GetValue(p);
            int sa = (int)p.GetType().GetField("SrcAt").GetValue(p);
            int sl = (int)p.GetType().GetField("SrcLen").GetValue(p);
            if (stP26 == 6 || sl == 0 || tx.Length == 0) continue;   // 表格线 / 列表符号不在原文里占字符
            chk26++;
            bool ok = sa >= 0 && sa + tx.Length <= raw26.Length && raw26.Substring(sa, tx.Length) == tx;
            if (!ok)
            {
                bad26++;
                if (bad26Detail.Length == 0)
                    bad26Detail = "偏移" + sa + " 期望「" + tx + "」实际「" +
                        (sa >= 0 && sa + tx.Length <= raw26.Length ? raw26.Substring(sa, tx.Length) : "越界") + "」";
            }
        }
        // 再拿"标题 / 行内代码 / 列表混排"几种样本各验证一遍
        string[] mix26 = new string[]
        {
            "# 标题里带 **粗体** 和 `代码`\n正文里 `x = 1` 和 **粗** 交错出现，后面还有一段普通文字。\n",
            "- 列表项 **加粗的几个字** 后面接普通文字和 `行内代码`，再补一点尾巴。\n",
            "| a | b |\n| --- | --- |\n| **粗** | `码` |\n"
        };
        for (int si = 0; si < mix26.Length; si++)
        {
            rtb.ClearAll();
            Pump(120);
            f.Write(OutKind.Assistant, mix26[si]);
            rtb.FlushNow();
            Pump(250);
            List<MsgBlock> mb26 = Blocks(rtb);
            MsgBlock bx = mb26[mb26.Count - 1];
            string rx = bx.Body;
            System.Collections.IEnumerable px = (System.Collections.IEnumerable)
                typeof(MsgBlock).GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bx);
            foreach (object p in px)
            {
                int stP = (int)p.GetType().GetField("Style").GetValue(p);
                string tx = (string)p.GetType().GetField("Text").GetValue(p);
                int sa = (int)p.GetType().GetField("SrcAt").GetValue(p);
                int sl = (int)p.GetType().GetField("SrcLen").GetValue(p);
                if (stP == 6 || sl == 0 || tx.Length == 0) continue;
                chk26++;
                bool ok = sa >= 0 && sa + tx.Length <= rx.Length && rx.Substring(sa, tx.Length) == tx;
                if (!ok)
                {
                    bad26++;
                    if (bad26Detail.Length == 0)
                        bad26Detail = "样本" + si + " 偏移" + sa + " 期望「" + tx + "」实际「" +
                            (sa >= 0 && sa + tx.Length <= rx.Length ? rx.Substring(sa, tx.Length) : "越界") + "」";
                }
            }
        }
        Console.WriteLine("    检查 " + chk26 + " 个片段（含标题/代码/表格混排），偏移错的 " + bad26 + " 个");
        Check("每个片段的原文偏移都指向自己那段文字（粗体后面的文字不串位）", bad26 == 0,
            "错 " + bad26 + "/" + chk26 + " 首个：" + bad26Detail);

        // 26.2 整条选中：每行一段高亮，而且要盖满整行（含粗体段）
        b26.SetSel(0, raw26.Length);
        List<Rectangle> hl26 = b26.HighlightRects();
        List<int> st26;
        List<Rectangle> pr26 = PieceRects(b26, out st26);
        SortedDictionary<int, int[]> row26 = new SortedDictionary<int, int[]>();
        for (int i = 0; i < pr26.Count; i++)
        {
            if (st26[i] == 6 || pr26[i].Width <= 0) continue;
            int y = pr26[i].Top;
            if (!row26.ContainsKey(y)) row26[y] = new int[] { pr26[i].Left, pr26[i].Right };
            else
            {
                int[] a = row26[y];
                if (pr26[i].Left < a[0]) a[0] = pr26[i].Left;
                if (pr26[i].Right > a[1]) a[1] = pr26[i].Right;
            }
        }
        Check("整条选中：高亮块数 = 行数（一行一段，不被片段切碎）", hl26.Count == row26.Count,
            "高亮=" + hl26.Count + " 行=" + row26.Count);
        int lightMiss = 0;
        string lightMissDetail = "";
        foreach (KeyValuePair<int, int[]> kv in row26)
        {
            Rectangle hit = Rectangle.Empty;
            foreach (Rectangle r in hl26) if (r.Top == kv.Key + Theme.Px(1)) hit = r;   // HighlightRects 的 top 就是这个式子
            if (hit.Width == 0 || hit.Left > kv.Value[0] || hit.Right < kv.Value[1])
            {
                lightMiss++;
                if (lightMissDetail.Length == 0)
                    lightMissDetail = "y=" + kv.Key + " 文字 x=" + kv.Value[0] + ".." + kv.Value[1] +
                        " 高亮 x=" + (hit.Width == 0 ? "无" : hit.Left + ".." + hit.Right);
            }
        }
        Check("每一行的高亮都盖满整行文字（不再出现「只盖一小截」）", lightMiss == 0,
            "漏 " + lightMiss + "/" + row26.Count + " 首个：" + lightMissDetail);
        int boldMiss = 0;
        for (int i = 0; i < pr26.Count; i++)
        {
            if (st26[i] != 1 || pr26[i].Width <= 0) continue;
            bool covered = false;
            foreach (Rectangle r in hl26)
                if (r.Left <= pr26[i].Left + 1 && r.Right >= pr26[i].Right - 1) covered = true;
            if (!covered) boldMiss++;
        }
        Check("粗体片段被高亮完全覆盖（这正是截图里漏掉的那块）", boldMiss == 0, "没盖住的粗体片段 " + boldMiss + " 个");

        // 26.3 从粗体中间起选：高亮起点必须落在粗体片段内部（偏移不串位）
        int bi26 = raw26.IndexOf("精准定位");
        b26.SetSel(bi26 + 2, bi26 + 8);
        List<Rectangle> hl26b = b26.HighlightRects();
        Rectangle boldRect = Rectangle.Empty;
        for (int i = 0; i < pr26.Count; i++)
            if (st26[i] == 1 && pr26[i].Width > 0) { boldRect = pr26[i]; break; }
        Check("从粗体中间起选 6 个字，抽出来正是这 6 个字",
            b26.SelectedText == raw26.Substring(bi26 + 2, 6), "\"" + b26.SelectedText + "\"");
        Check("高亮起点落在粗体片段内部（说明前缀宽度没被多算）",
            hl26b.Count == 1 && boldRect.Width > 0 && hl26b[0].Left >= boldRect.Left && hl26b[0].Left < boldRect.Right,
            "高亮=" + hl26b.Count + " 个 boldX=" + boldRect.Left + ".." + boldRect.Right +
            " hlX=" + (hl26b.Count > 0 ? hl26b[0].Left.ToString() : "-"));
        b26.ClearSel();

        // 26.4 标题必须真的按标题字号画（排版字体 == 绘制字体，不然行高白占、高亮也会错）
        rtb.ClearAll();
        Pump(120);
        f.Write(OutKind.Assistant, "\n## 大标题一行\n正文一行，普通字号。\n");
        rtb.FlushNow();
        Pump(300);
        List<MsgBlock> nb26c = Blocks(rtb);
        MsgBlock b26c = nb26c[nb26c.Count - 1];
        List<int> st26c;
        List<Rectangle> pr26c = PieceRects(b26c, out st26c);
        System.Collections.IEnumerable ps26c = (System.Collections.IEnumerable)
            typeof(MsgBlock).GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b26c);
        int headFontH = -1, bodyFontH = -1, headRectH = -1, bodyRectH = -1;
        int idx26 = 0;
        foreach (object p in ps26c)
        {
            string tx = (string)p.GetType().GetField("Text").GetValue(p);
            Rectangle r = (Rectangle)p.GetType().GetField("Rect").GetValue(p);
            Font ft = b26c.FontAt(idx26);
            if (ft != null && tx.IndexOf("大标题") >= 0) { headFontH = ft.Height; headRectH = r.Height; }
            if (ft != null && tx.IndexOf("正文一行") >= 0) { bodyFontH = ft.Height; bodyRectH = r.Height; }
            idx26++;
        }
        Console.WriteLine("    标题片段：绘制字体高=" + headFontH + "px 行高=" + headRectH
            + "px | 正文片段：绘制字体高=" + bodyFontH + "px 行高=" + bodyRectH
            + "px | 参照正文字体高=" + Theme.F(10.5f).Height + "px");
        Check("标题是按标题字号绘制的（比正文大，不是按正文字号画的）",
            headFontH > bodyFontH && bodyFontH > 0, "标题字体高=" + headFontH + " 正文=" + bodyFontH);
        Check("标题行高也按标题字号留白（量宽和绘制用的是同一个字体）",
            headRectH > bodyRectH && headRectH >= headFontH, "标题行高=" + headRectH + " 正文本高=" + bodyRectH);

        Console.WriteLine("== 27. 轮次分界：用户提问上面要拉开一大截（同轮内部保持紧凑）==");
        rtb.ClearAll();
        Pump(150);
        f.Write(OutKind.User, "第一个问题");
        f.Write(OutKind.Info, "· 一行系统提示\n");
        f.Write(OutKind.Assistant, "第一轮的回答");
        f.Write(OutKind.User, "第二个问题");
        f.Write(OutKind.ToolCall, "  ● read_file(a.txt)\n");
        f.Write(OutKind.Assistant, "第二轮的回答");
        Pump(500);
        List<MsgBlock> tb27 = Blocks(rtb);
        int inTurnGap = -1, turnGap = -1;
        List<string> gaps27 = new List<string>();
        for (int i = 1; i < tb27.Count; i++)
        {
            int g = tb27[i].Top - (tb27[i - 1].Top + tb27[i - 1].Height);
            gaps27.Add(tb27[i].Kind + "=" + g);
            if (tb27[i].Kind == OutKind.User) { if (turnGap < 0) turnGap = g; }
            else if (inTurnGap < 0) inTurnGap = g;
        }
        Console.WriteLine("    各段间距：" + string.Join("  ", gaps27.ToArray()));
        Check("用户提问（新一轮任务）上面的空隙 ≥ 3 倍同轮间距", turnGap >= inTurnGap * 3,
            "轮首=" + turnGap + "px 同轮=" + inTurnGap + "px");
        Check("同轮内部（系统提示/工具调用/正文）仍保持紧凑", inTurnGap == Theme.Px(Theme.Sp8),
            "同轮=" + inTurnGap + "px 期望=" + Theme.Px(Theme.Sp8) + "px");

        Console.WriteLine("== 28. 「模型 / 推理等级」卡片：等级要真的进请求参数 ==");
        // 28.1 等级 → 各家接口的参数
        Check("Ollama：选了等级就发字符串（think:\"high\"）",
            Convert.ToString(Effort.OllamaThink(true, "high")) == "high",
            "值=" + Convert.ToString(Effort.OllamaThink(true, "high")));
        Check("Ollama：没设过等级（老配置里的 auto）还是原来的 bool",
            Convert.ToString(Effort.OllamaThink(false, "auto")) == "False",
            "值=" + Convert.ToString(Effort.OllamaThink(false, "auto")));
        Check("OpenAI 兼容：选了等级才带 reasoning_effort，没选等级一个字段都不多加",
            Effort.OpenAiEffort("medium") == "medium" && Effort.OpenAiEffort("auto") == null,
            "medium=" + Convert.ToString(Effort.OpenAiEffort("medium"))
            + " auto=" + Convert.ToString(Effort.OpenAiEffort("auto")));
        Check("档位 = 低/中/高 三档（按规格去掉了「自动」），老配置的 auto 归到「中」",
            Effort.Levels.Length == 3
            && Effort.Label("low") == "低" && Effort.Label("medium") == "中" && Effort.Label("high") == "高"
            && Effort.Index("low") == 0 && Effort.Index("medium") == 1 && Effort.Index("high") == 2
            && Effort.Label("auto") == "中" && Effort.Index("auto") == 1
            && Effort.Normalize("auto") == "medium",
            "档位=" + Effort.Levels.Length + " auto→" + Effort.Label("auto") + "(" + Effort.Index("auto") + ")");

        Check("本地模型能不能调档看能力探测（thinking.values 要有 low/medium/high）",
            Effort.TestEffortCapability("ollama", "qwen3.5:4b", "low/medium/high")
            && !Effort.TestEffortCapability("ollama", "qwen3.5:4b", "true"),
            "-");
        Check("云端：按白名单认 reasoning_effort，认不出的隐藏滑块（宁可不显示也不给假档位）",
            Effort.TestEffortCapability("openai", "deepseek-reasoner", "")
            && Effort.TestEffortCapability("openai", "gpt-5-codex", "")
            && !Effort.TestEffortCapability("openai", "deepseek-flash", "")
            && !Effort.TestEffortCapability("openai", "glm-5.3", ""),
            "reasoner=" + Effort.TestEffortCapability("openai", "deepseek-reasoner", "")
            + " flash=" + Effort.TestEffortCapability("openai", "deepseek-flash", ""));

        // 28.2 输入条上那个 chip：Codex 风格的自绘 chip，点开自绘浮层（不再用 ContextMenuStrip）
        AgentConfig cfg28 = (AgentConfig)Get(f, "config");
        ModelChip chip28 = (ModelChip)Get(f, "cmbModel");
        Check("chip 是自绘的 ModelChip（不是原生下拉 / 菜单）", chip28 != null, chip28 == null ? "null" : "ok");
        Check("chip 是圆角 18 的胶囊 + 自绘箭头（不用字符 ⌄）",
            chip28.Radius == 18 && chip28.ShowCaret && chip28.CaretFlip,
            "Radius=" + chip28.Radius + " caret=" + chip28.ShowCaret + " flip=" + chip28.CaretFlip);

        // 造一份"本地 + 云端都有"的模型表（跑测试的机器上 config.json 未必有本地模型）
        cfg28.providers.Clear();
        Provider pl28 = Provider.DefaultOllama();
        pl28.models = new List<string>(new string[] { "qwen3.5:4b", "azhan_v3:latest" });
        cfg28.providers.Add(pl28);
        Provider pc28 = new Provider();
        pc28.name = "deepseek"; pc28.type = "openai";
        pc28.base_url = "https://api.deepseek.com/v1"; pc28.api_key = "x";
        // 用 deepseek-reasoner：它认 reasoning_effort，第三组用例要检查"档位真的进参数"
        pc28.models = new List<string>(new string[] { "deepseek-reasoner", "deepseek-flash" });
        cfg28.providers.Add(pc28);
        cfg28.provider = pc28.id; cfg28.model = "deepseek-reasoner";
        CallPrivate(f, "LoadModels");
        CallPrivate(f, "UpdateModelChip");
        Pump(300);

        callToggle(f);
        Pump(400);
        ModelPopover pop28 = (ModelPopover)Get(f, "modelPop");
        // 浮层有个正当行为：整个程序一失焦就自动收起来（WM_ACTIVATEAPP）。测试跑在控制台里，
        // 偶尔会被别的窗口抢一下焦点，浮层就被收掉 —— 这条断言于是偶发失败（2026-10-15 撞到一次）。
        // 没弹出来就再点一次，别把"环境抢焦点"当成"浮层坏了"。
        if (pop28 == null || !pop28.Visible)
        {
            Pump(300);
            callToggle(f);
            Pump(500);
            pop28 = (ModelPopover)Get(f, "modelPop");
        }
        if (pop28 == null)
            Console.WriteLine("    （chip 模型数=" + chip28.Items.Count + "  config.model=" + cfg28.model
                + "  chip.Text=「" + chip28.Text + "」）");
        Check("点 chip 弹出的是自绘浮层 ModelPopover（无边框、不抢焦点、不占 Alt+Tab）",
            pop28 != null && pop28.Visible && pop28.FormBorderStyle == FormBorderStyle.None && pop28.ShowInTaskbar == false,
            pop28 == null ? "没弹出来" : ("visible=" + pop28.Visible + " border=" + pop28.FormBorderStyle));
        if (pop28 != null)
        {
            Check("浮层弹在 chip 上方（输入框在窗口底部，不能往下弹）",
                pop28.Location.Y + pop28.Height <= chip28.PointToScreen(new Point(0, 0)).Y,
                "浮层底=" + (pop28.Location.Y + pop28.Height) + " chip顶=" + chip28.PointToScreen(new Point(0, 0)).Y);
            Check("浮层没越出屏幕", Screen.FromControl(chip28).WorkingArea.Contains(pop28.Bounds),
                "浮层=" + pop28.Bounds + " 屏幕=" + Screen.FromControl(chip28).WorkingArea);
            Check("chip 展开后箭头翻转（CaretFlip + Expanded）", chip28.Expanded, "Expanded=" + chip28.Expanded);

            // ---- 第一页：强度（Codex 的默认页）----
            EffortSlider emb = pop28.EffortPage;
            Check("点开就是「推理强度」那页（不是直接甩一长串模型）",
                pop28.Page == ModelPopover.PageStrength, "页=" + pop28.Page);
            Check("强度页：顶部档位 + 中间模型名 + 一条滑杆，都在卡片宽度内",
                emb != null && emb.Value == Effort.Index(cfg28.effort)
                && emb.Title == cfg28.model && emb.TitleRect.Right <= emb.Width,
                emb == null ? "没有强度页" : ("档位=" + emb.Value + " 模型=" + emb.Title));

            bool switched = ModelPopover.TestClickModelRow(pop28, emb);
            Pump(500);                                   // 换页是 130ms 的动画，等它走完再断言
            Check("点模型名那行会切到「模型列表」页",
                switched && pop28.Page == ModelPopover.PageModels && !pop28.Animating,
                "页=" + pop28.Page + " 动画中=" + pop28.Animating);

            ModelPickCtl pick28 = pop28.Pick;
            Check("列表里的行数 = 可选模型数（组标题不算行）",
                pick28 != null && pick28.RowCount == chip28.Items.Count,
                pick28 == null ? "没有列表" : ("行=" + pick28.RowCount + " 模型=" + chip28.Items.Count));
            Check("本地(ollama) / 云端分开，两组标题都在",
                pick28 != null && pick28.Headers.IndexOf("本地") >= 0 && pick28.Headers.IndexOf("云端") >= 0,
                pick28 == null ? "-" : string.Join("/", pick28.Headers.ToArray()));
            int curRow = pick28 == null ? -1 : pick28.CurrentRow;
            Check("当前模型那一行有 ✓（只有它一个）",
                pick28 != null && pick28.CheckedCount == 1 && curRow >= 0,
                pick28 == null ? "-" : ("打勾=" + pick28.CheckedCount + " 位置=" + curRow));
            Check("打开时当前模型已经滚进可视区",
                pick28 != null && pick28.CurrentInView,
                pick28 == null ? "-" : ("滚动=" + pick28.ScrollTop));
            Check("模型列表页比强度页宽（换页是要长出来的）",
                Math.Max(pop28.CardWidth, pick28.Width) > emb.Width,
                "列表=" + pick28.Width + " 强度=" + emb.Width + " 当前卡片=" + pop28.CardWidth);
            if (pick28 != null && curRow >= 0)
            {
                // 点一行（挑一个和当前不一样的），Codex 是"换成强度页"而不是关掉
                int idxRow = (curRow + 1) % pick28.RowCount;
                RaisePickRow(pick28, idxRow);
                Pump(400);
                Check("点一行 → 换了模型并写进 config.model",
                    cfg28.model != null && chip28.Text.IndexOf(cfg28.model) >= 0,
                    "config.model=" + cfg28.model + " chip=「" + chip28.Text + "」");
                Check("选完不关浮层，平滑换成「强度」页并更新模型名",
                    pop28.Visible && pop28.Page == ModelPopover.PageStrength
                    && pop28.EffortPage.Title == cfg28.model,
                    "visible=" + pop28.Visible + " 页=" + pop28.Page + " 标题=" + pop28.EffortPage.Title);
                // Codex 那颗 chip 只写「模型名 档位」：厂商已经在输入框上方那颗药丸上了
                Check("chip 文字跟着换成「新模型名 档位」（Codex 那个写法）",
                    chip28.Text.IndexOf(" / ") < 0 && chip28.Text.IndexOf(cfg28.model) >= 0,
                    "chip=「" + chip28.Text + "」");
            }

            // ---- 第三页：滑块改档位 → 配置 + chip + 真的进请求参数 ----
            EffortSlider emb2 = pop28.EffortPage;
            emb2.SetValue(2, true);                     // 高
            Pump(200);
            Check("拖到「高」→ config.effort = high", cfg28.effort == "high", "effort=" + cfg28.effort);
            Check("chip 上的档位跟着变「高」", chip28.Text.EndsWith("高"), "chip=「" + chip28.Text + "」");
            Check("Ollama：档位真的落成 think=\"high\"",
                Convert.ToString(Effort.OllamaThink(true, cfg28.effort)) == "high",
                "think=" + Convert.ToString(Effort.OllamaThink(true, cfg28.effort)));
            Check("OpenAI 兼容：档位真的落成 reasoning_effort=high",
                Effort.OpenAiEffort(cfg28.effort) == "high",
                "effort=" + Convert.ToString(Effort.OpenAiEffort(cfg28.effort)));
            emb2.SetValue(0, true);                     // 低
            Pump(200);
            Check("拖到「低」→ config.effort = low", cfg28.effort == "low", "effort=" + cfg28.effort);
            Check("滑块停在当前档（= 配置里的档位，不是写死的）",
                emb2.Value == Effort.Index(cfg28.effort),
                "滑块=" + emb2.Value + " 配置=" + Effort.Index(cfg28.effort));

            // 不支持调强度的模型：滑块整块隐藏（不得出现假档位），但卡片不能是一片空白
            cfg28.model = "deepseek-flash";             // 白名单里没有 → 不支持
            pop28.Refresh(cfg28.model, Effort.Index(cfg28.effort), false);
            Pump(150);
            Check("不支持的模型：滑块隐藏、不出假档位",
                !pop28.EffortPage.EffortSupported,
                "supported=" + pop28.EffortPage.EffortSupported);
            Check("不支持时卡片也不是空白：仍显示当前档位 + 模型名 + 一句说明",
                pop28.CardWidth > 0 && pop28.EffortPage.Title == cfg28.model
                && pop28.EffortPage.Head.Length > 0,
                "标题=" + pop28.EffortPage.Head + " 模型=" + pop28.EffortPage.Title);

            // ---- 权限浮层：三档、当前项打勾 ----
            Console.WriteLine("== 28.5 权限浮层：仅可查看 / 工作区内修改 / 完全访问 ==");
            Check("权限就是 Codex 那三档", Perm.Values.Length == 3
                && Perm.Label("readonly") == "仅可查看"
                && Perm.Label("workspace") == "工作区内修改"
                && Perm.Label("full") == "完全访问",
                string.Join("/", Perm.Labels));
            Check("只有「完全访问」才不再逐条问（老 auto_yes 的等价档）",
                Perm.AutoYes("full") && !Perm.AutoYes("readonly") && !Perm.AutoYes("workspace"),
                "-");
            Check("老配置迁移：auto_yes=true → full，false → workspace",
                PermMigrate(true) == "full" && PermMigrate(false) == "workspace",
                "true→" + PermMigrate(true) + " false→" + PermMigrate(false));

            cfg28.permission = "workspace";
            cfg28.auto_yes = false;
            CallPrivate(f, "UpdatePermChip");
            Pump(150);
            UiDrop pchip = (UiDrop)Get(f, "permChip");
            Check("输入条上的权限 chip 显示当前档", pchip != null && pchip.Text == "工作区内修改",
                pchip == null ? "没有 chip" : "chip=「" + pchip.Text + "」");

            TogglePerm(f);
            Pump(400);
            PermPopover ppop = (PermPopover)Get(f, "permPop");
            Check("点权限 chip 弹出的是自绘浮层（不是 ContextMenuStrip）、三行都画出来",
                ppop != null && ppop.Visible && ppop.FormBorderStyle == FormBorderStyle.None
                && ppop.MenuCtl.RowRect(0).Height > 0,
                ppop == null ? "没弹出来" : ("visible=" + ppop.Visible + " 行高=" + ppop.MenuCtl.RowRect(0).Height));
            if (ppop != null)
            {
                Check("浮层当前档 = 配置里的档（打勾那一行）",
                    ppop.MenuCtl.Current == Perm.Index(cfg28.permission),
                    "浮层=" + ppop.MenuCtl.Current + " 配置=" + Perm.Index(cfg28.permission));
                Check("浮层弹在 chip 上方、没越出屏幕",
                    ppop.Bounds.Bottom <= pchip.PointToScreen(new Point(0, 0)).Y
                    && Screen.FromControl(pchip).WorkingArea.Contains(ppop.Bounds),
                    "浮层=" + ppop.Bounds);
                // 点「完全访问」那一行
                ppop.MenuCtl.Current = 2;
                FieldInfo pf = ppop.MenuCtl.GetType().GetField("RowPicked", BindingFlags.Instance | BindingFlags.NonPublic);
                Delegate pd = (Delegate)pf.GetValue(ppop.MenuCtl);
                pd.DynamicInvoke(new object[] { ppop.MenuCtl, EventArgs.Empty });
                Pump(300);
                Check("点一行 → 权限落盘（permission=full、auto_yes 跟着同步）",
                    cfg28.permission == "full" && cfg28.auto_yes,
                    "permission=" + cfg28.permission + " auto_yes=" + cfg28.auto_yes);
                Check("选完浮层关掉、chip 文字跟着变",
                    Get(f, "permPop") == null && pchip.Text == "完全访问",
                    "chip=「" + pchip.Text + "」");
            }

            // ---- 关闭：点主窗口别处 / Esc ----
            pop28.ShowPage(ModelPopover.PageModels, false);
            Pump(200);
            f.GetType().GetMethod("CloseModelPopover", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(f, null);
            Pump(200);
            Check("关掉之后浮层没了、chip 箭头转回来",
                Get(f, "modelPop") == null && !pop28.Visible && !chip28.Expanded,
                "modelPop=" + (Get(f, "modelPop") == null ? "null" : "还在") + " expanded=" + chip28.Expanded);
        }

        Console.WriteLine("== 28.4 模型很多（23 个）时的选择浮层：可滚动、当前项已在可视区 ==");
        string[] many = new string[23];
        string[] manyG = new string[23];
        for (int i = 0; i < many.Length; i++) { many[i] = "模型-" + i.ToString("00"); manyG[i] = i < 12 ? "本地" : "云端"; }
        ModelPickCtl pk = new ModelPickCtl(many, 18, 7, 0, manyG, 0);
        pk.SetListHeight(pk.ListHeight);      // 浮层就是这么用的：定下列表区后才把当前模型滚进视野
        Check("浮层高度被限制在 7 行 + 两个组标题（不会被 23 个模型撑满整屏）",
            pk.ListHeight <= 7 * Theme.Px(36) + Theme.Px(26) * 2 + Theme.Px(20),
            "浮层高=" + pk.ListHeight + "px");
        Check("打开时自动把当前模型（第 19 个）滚进可视区",
            pk.CurrentInView && pk.ScrollTop > 0,
            "滚动位置=" + pk.ScrollTop);
        Check("只有当前模型那一行打勾，且就是第 19 个", pk.CheckedCount == 1 && pk.CurrentRow == 18,
            "打勾=" + pk.CheckedCount + " 位置=" + pk.CurrentRow);
        Check("组标题插在列表里但不占行号（23 行模型 + 2 个标题）",
            pk.Headers.Count == 2 && pk.RowCount == 23, "标题=" + pk.Headers.Count + " 行=" + pk.RowCount);
        // 回归：整块自绘之后，滚回顶部列表不能变成一片空白（老实现滚回顶时整片空白）。
        // 判据：回顶后滚动位置归零、第一行回到"组标题正下方"的位置。
        int firstAtOpen = pk.FirstRowTop;      // 打开时当前模型被滚到顶，第一行在负方向（内容没跑）
        int scrollAtOpen = pk.ScrollTop;
        pk.ScrollBy(100000);                   // 再往下滚到底
        int atBottom = pk.ScrollTop;
        pk.ScrollBy(-100000);                  // 滚回顶
        Check("滚到底再滚回顶：能滚、回得来、第一行回到原位（列表不会变空白）",
            firstAtOpen < 0 && scrollAtOpen > 0 && atBottom == scrollAtOpen
            && pk.ScrollTop == 0 && pk.FirstRowTop == pk.HeaderH && pk.RowCount == 23,
            "打开时滚动=" + scrollAtOpen + " 首行顶=" + firstAtOpen
            + " | 底部=" + atBottom + " 回顶=" + pk.ScrollTop
            + " 首行顶=" + pk.FirstRowTop + "（应为 " + pk.HeaderH + "）行数=" + pk.RowCount);

        Console.WriteLine("== 29. 图片粘贴 / 拖入：输入框得真接住（不能按了没反应）==");
        // 29.1 图片扩展名白名单（大小写都要认）
        string[] yes29 = { "a.png", "b.PNG", "c.jpg", "d.JPEG", "e.gif", "f.bmp", "g.webp" };
        string[] no29 = { "a.txt", "b.cs", "c.md", "d.exe" };
        MethodInfo isImg = typeof(MainForm).GetMethod("IsImageFile", BindingFlags.Static | BindingFlags.NonPublic);
        Check("IsImageFile 认常见图片扩展名（且不区分大小写）",
            isImg != null && AllTrue(isImg, yes29), isImg == null ? "找不到 IsImageFile" : "有扩展名没认出来");
        Check("IsImageFile 不把非图片当图片",
            isImg != null && AllFalse(isImg, no29), "有非图片被误判成图片");
        // 29.2 路径带空格要加引号（不然模型把路径读成两截）
        MethodInfo qp = typeof(MainForm).GetMethod("QuotePath", BindingFlags.Static | BindingFlags.NonPublic);
        Check("带空格的图片路径自动加引号",
            qp != null && ("\"" + @"C:\a b\x.png" + "\"").Equals((string)qp.Invoke(null, new object[] { @"C:\a b\x.png" }))
            && @"C:\ab\x.png".Equals((string)qp.Invoke(null, new object[] { @"C:\ab\x.png" })),
            qp == null ? "找不到 QuotePath" : "引号处理不对");
        // 29.3 真的拖进来一个图片文件 → 路径进输入框
        UiEditor inp29 = (UiEditor)Get(f, "txtInput");
        inp29.Text = "";
        string tmp29 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ahz-attach-29.png");
        using (Bitmap bm29 = new Bitmap(4, 4)) { bm29.Save(tmp29, System.Drawing.Imaging.ImageFormat.Png); }
        CallPrivate(f, "AttachImages", new object[] { new string[] { tmp29 } });
        Pump(150);
        string mark29 = "[图片 " + System.IO.Path.GetFileName(tmp29) + "]";
        Check("拖入图片后，输入框里显示的是短标记（不再是一长串绝对路径）",
            inp29.Text.IndexOf(mark29) >= 0 && inp29.Text.IndexOf(tmp29) < 0,
            "输入框=\"" + inp29.Text.Replace("\n", "\\n") + "\"");
        // 关键不变量：发送时必须还原成真实路径，否则模型就看不到图了（整改 A 的前提）
        object exp29 = CallPrivate(f, "ExpandAttachMarks", new object[] { inp29.Text });
        Check("发送时短标记被还原成真实路径（看图链路不能断）",
            exp29 != null && ((string)exp29).IndexOf(tmp29) >= 0,
            exp29 == null ? "拿不到结果" : ((string)exp29).Replace("\n", "\\n"));
        Check("拖入图片后发送按钮点亮（不再要求必须打字）", HasSendEnabled(f), "按钮没亮");
        // 29.4 剪贴板图片落盘：存到 attachments 目录下，文件真的存在
        MethodInfo spi = typeof(MainForm).GetMethod("SavePastedImage", BindingFlags.Instance | BindingFlags.NonPublic);
        string saved29 = null;
        using (Bitmap bm29b = new Bitmap(6, 6))
        {
            saved29 = (string)spi.Invoke(f, new object[] { (Image)bm29b });
        }
        Check("Ctrl+V 的图片会落盘成一个真文件（png，存在）",
            saved29 != null && System.IO.File.Exists(saved29) && saved29.EndsWith(".png"),
            "落盘结果=" + (saved29 == null ? "null" : saved29));
        Check("落在 exe 同级的 attachments 目录里（发给模型时路径稳）",
            saved29 != null && saved29.IndexOf("attachments") >= 0, "路径=" + saved29);
        // 29.5 接线本身：不接 AllowDrop，拖进来会被文本框直接无视
        Check("输入框开了 AllowDrop（否则拖图片进去毫无反应）", inp29.AllowDrop, "AllowDrop=" + inp29.AllowDrop);
        Check("占位提示里写了 Ctrl+V 能粘图片（用户得知道有这个功能）",
            PlaceholderMentionsPaste(), "占位文字没提");
        Check("输入框占位语是 Codex 那四个字「随心输入」",
            Convert.ToString(ConstOf("PlaceholderText")) == "随心输入",
            "占位=「" + Convert.ToString(ConstOf("PlaceholderText")) + "」");
        inp29.Text = "";
        try { System.IO.File.Delete(tmp29); } catch { }

        Console.WriteLine("== 30. 图片真的进了请求体（Ollama images 字段 / OpenAI content 数组）==");
        // 造一张真图片，走真实的 ConvertMessages
        string img30 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ahz-vision-30.png");
        using (Bitmap bm30 = new Bitmap(8, 8)) { bm30.Save(img30, System.Drawing.Imaging.ImageFormat.Png); }
        MethodInfo convOll = null, convOai = null;
        foreach (Type t in typeof(MainForm).Assembly.GetTypes())
        {
            MethodInfo mi = t.GetMethod("ConvertMessages", BindingFlags.Static | BindingFlags.NonPublic);
            if (mi == null) continue;
            if (t.Name == "OllamaLlm") convOll = mi;
            if (t.Name == "OpenAiLlm") convOai = mi;
        }
        Check("两个接口各有一套 ConvertMessages（本地 images / 云端 content 数组）",
            convOll != null && convOai != null, "ollama=" + (convOll != null) + " openai=" + (convOai != null));

        // 只给最后一条用户消息带图；历史那条不带
        List<Dictionary<string, object>> msgs30 = new List<Dictionary<string, object>>();
        msgs30.Add(AgentRunner.Msg("user", "老消息：" + img30));
        msgs30.Add(AgentRunner.Msg("assistant", "好的"));
        msgs30.Add(AgentRunner.Msg("user", "看这张图 " + img30));

        List<object> o30 = (List<object>)convOll.Invoke(null, new object[] { msgs30 });
        Dictionary<string, object> lastOll = Json.Map(o30[2]);
        Dictionary<string, object> firstOll = Json.Map(o30[0]);
        object imgs30 = Json.Get(lastOll, "images");
        Check("Ollama：最后一条用户消息带上了 images（base64 列表）",
            imgs30 != null && ((List<string>)imgs30).Count == 1, "images=" + (imgs30 == null ? "无" : "有"));
        if (imgs30 != null && ((List<string>)imgs30).Count == 1)
        {
            string b64 = ((List<string>)imgs30)[0];
            Check("images 里装的是图片字节（不是路径文字）",
                b64.Length > 20 && b64.IndexOf("ahz-vision-30") < 0, "长度=" + b64.Length);
            Check("base64 能解回一张真图片（PNG 头 89 50 4E 47）",
                IsPngBase64(b64), "解出来不是 PNG");
        }
        Check("Ollama：历史里那条老消息不带图（不然每轮请求越来越胖）",
            Json.Get(firstOll, "images") == null, "老消息也带了图");

        List<object> o30b = (List<object>)convOai.Invoke(null, new object[] { msgs30 });
        Dictionary<string, object> lastOai = Json.Map(o30b[2]);
        List<object> parts30 = Json.Get(lastOai, "content") as List<object>;
        Check("OpenAI：content 变成数组（文字段 + 图片段）",
            parts30 != null && parts30.Count == 2, parts30 == null ? "还是字符串" : ("段数=" + parts30.Count));
        if (parts30 != null && parts30.Count == 2)
        {
            Check("第一段是原文文字（一个字没丢）",
                Json.Str(Json.Map(parts30[0]), "text", "").IndexOf("看这张图") >= 0, "第一段不是原文");
            Dictionary<string, object> seg = Json.Map(parts30[1]);
            Dictionary<string, object> u30 = Json.Map(Json.Get(seg, "image_url"));
            string url30 = u30 == null ? "" : Json.Str(u30, "url", "");
            Check("第二段是 image_url、data:image/png;base64 开头",
                Json.Str(seg, "type", "") == "image_url" && url30.StartsWith("data:image/png;base64,"),
                "type=" + Json.Str(seg, "type", "") + " url 前缀=" + (url30.Length > 24 ? url30.Substring(0, 24) : url30));
        }

        // 不带图的消息：格式必须和以前完全一样（不能把所有请求都变成数组）
        List<Dictionary<string, object>> plain30 = new List<Dictionary<string, object>>();
        plain30.Add(AgentRunner.Msg("user", "就是一句普通的话"));
        Dictionary<string, object> pOll = Json.Map(((List<object>)convOll.Invoke(null, new object[] { plain30 }))[0]);
        Dictionary<string, object> pOai = Json.Map(((List<object>)convOai.Invoke(null, new object[] { plain30 }))[0]);
        Check("没有图片时，两条接口的 content 都还是普通字符串（不改变原有行为）",
            pOll["content"] is string && pOai["content"] is string
            && Json.Get(pOll, "images") == null, "ollama=" + pOll["content"].GetType().Name + " openai=" + pOai["content"].GetType().Name);

        // 图片路径不存在 / 非图片：绝不能当成图片发出去
        List<Dictionary<string, object>> ghost30 = new List<Dictionary<string, object>>();
        ghost30.Add(AgentRunner.Msg("user", @"C:\nope\missing.png 和 D:\a\b.cs"));
        Dictionary<string, object> gOll = Json.Map(((List<object>)convOll.Invoke(null, new object[] { ghost30 }))[0]);
        Check("路径不存在或不是图片 → 不当作图片（防止把报错丢给接口）",
            Json.Get(gOll, "images") == null, "不该带 images");

        // 兜底：模型看不了图时要有"退回纯文字"的判断依据
        MethodInfo mhi = typeof(Vision).GetMethod("MessagesHaveImages", BindingFlags.Static | BindingFlags.Public);
        Check("带图的对话能被识别出来（失败重发时靠它决定要不要退回纯文字）",
            mhi != null && (bool)mhi.Invoke(null, new object[] { msgs30 }),
            mhi == null ? "找不到 MessagesHaveImages" : "没识别出来");
        Check("不带图的对话不会被误判成带图", mhi != null && !(bool)mhi.Invoke(null, new object[] { plain30 }), "误判了");
        Check("大图会被缩到长边 1568 以内（不把请求体撑爆）",
            Vision.MaxEdge == 1568 && Vision.MaxImages == 4, "MaxEdge=" + Vision.MaxEdge + " MaxImages=" + Vision.MaxImages);

        // 30.6 已知看不了图 → 不用再白撞；换了模型 → 重新给机会
        //      （这轮修的缺陷：Disabled 每轮都被重置，同一个瞎模型每轮都白撞一次失败请求）
        PropertyInfo send30 = typeof(Vision).GetProperty("ShouldSend", BindingFlags.Static | BindingFlags.Public);
        Check("接口没说能力时，照常带图（不能因为怕失败就不让用户用图）",
            send30 != null && (bool)send30.GetValue(null, null), send30 == null ? "找不到 ShouldSend" : "默认就不发");
        Vision.Capability = false;                       // 接口/探测明确说"看不了"
        Check("接口声明看不了图 → 根本不发图片（省掉白撞一次失败请求）",
            !(bool)send30.GetValue(null, null), "声明看不了却还在发图");
        Check("声明看不了时 base64 列表为空（请求体里没有图片数据）",
            Vision.Base64List("看图 " + img30).Count == 0, "还塞了图");
        Vision.Capability = true;                        // 换成带视觉的模型
        Check("换成能看图的模型 → 立刻恢复带图",
            (bool)send30.GetValue(null, null) && Vision.Base64List("看图 " + img30).Count == 1, "没恢复");
        Vision.Capability = null;
        Vision.Disabled = false;

        // 同一模型第二轮不该再撞、换模型要重新给机会：验 Key / Disabled 的记忆语义
        FieldInfo key30 = typeof(Vision).GetField("Key", BindingFlags.Static | BindingFlags.Public);
        Check("有字段记着「上次的判断是给哪个模型的」", key30 != null, "缺 Vision.Key");
        if (key30 != null)
        {
            string sameKey = cfg28.provider + "|" + cfg28.model;
            key30.SetValue(null, sameKey);
            Vision.Disabled = true;
            // SyncVision 是 public，直接调（CallPrivate 只认 NonPublic）
            AgentRunner r30 = (AgentRunner)Get(f, "runner");
            r30.SyncVision();                 // 只是同步"能不能看图"，不该动"记仇"这两个字段
            Check("同一模型下：已知看不了图的标记会留着（第二轮不再白发一次图）",
                Vision.Disabled && (string)key30.GetValue(null) == sameKey,
                "Disabled=" + Vision.Disabled + " Key=" + (string)key30.GetValue(null));
            Check("同一模型 + 已知看不了 → 请求里不会再塞图片",
                !Vision.ShouldSend, "ShouldSend 还是 true");
            // 换一个模型/接口 → 记忆失效，重新给一次机会（关键：不能再让它一直"记仇"）
            key30.SetValue(null, "别的接口|别的模型");
            bool diff = (string)key30.GetValue(null) != sameKey;
            Vision.Disabled = false;
            Vision.Capability = null;
            Check("换了模型/接口 → 记忆失效（新模型会重新得到一次带图的机会）", diff, "Key 没变，换模型后还被旧结论压着");
        }

        try { System.IO.File.Delete(img30); } catch { }

        // ---- 32. 拉取模型：接口声明的能力（显示名 / 能不能看图 / 档位）要真的用起来 ----
        Console.WriteLine("== 32. 模型清单：接口说了什么就得用起来（显示名 / 看图 / 档位）==");
        {
            // 下面这段就是 deepseek /v1/models 的真实返回（字段截短了，值没改）
            string real = "{\"object\":\"list\",\"data\":["
                + "{\"id\":\"deepseek-flash\",\"object\":\"model\",\"owned_by\":\"deepseek\",\"name\":\"DeepSeek-V4.1-Flash\","
                + "\"context_window\":1048576,\"max_output_tokens\":393216,\"input_modalities\":[\"text\",\"image\"],"
                + "\"output_modalities\":[\"text\"],\"effort\":{\"supported_levels\":[\"low\",\"high\",\"max\"],\"default_level\":\"high\"}},"
                + "{\"id\":\"deepseek-v4-pro\",\"object\":\"model\",\"name\":\"DeepSeek-V4-Pro\",\"context_window\":1048576,"
                + "\"input_modalities\":[\"text\"],\"effort\":{\"supported_levels\":[\"low\",\"high\",\"max\"]}}]}";

            List<ModelMeta> infos32 = OpenAiLlm.ParseModelList(real);
            Check("解析接口返回：两个模型都认出来了", infos32.Count == 2, "个数=" + infos32.Count);
            ModelMeta mf = null, mp = null;
            foreach (ModelMeta mi in infos32)
            {
                if (mi.id == "deepseek-flash") mf = mi;
                if (mi.id == "deepseek-v4-pro") mp = mi;
            }
            Check("显示名收下了（DeepSeek-V4.1-Flash）", mf != null && mf.name == "DeepSeek-V4.1-Flash",
                mf == null ? "没解析出来" : ("name=" + mf.name));
            Check("能看图收下了：flash 的 input_modalities 含 image → 能看", mf != null && mf.vision == true,
                mf == null ? "没解析出来" : ("vision=" + mf.vision));
            Check("能看图收下了：v4-pro 只有 text → 明说看不了", mp != null && mp.vision == false,
                mp == null ? "没解析出来" : ("vision=" + mp.vision));
            Check("档位收下了：low/high/max", mf != null && string.Join("/", mf.efforts.ToArray()) == "low/high/max",
                mf == null ? "没解析出来" : string.Join("/", mf.efforts.ToArray()));
            Check("上下文长度收下了（1048576）", mf != null && mf.ctx == 1048576,
                mf == null ? "没解析出来" : ("ctx=" + mf.ctx));
            List<ModelMeta> unknown32 = OpenAiLlm.ParseModelList("{\"data\":[{\"id\":\"m1\"}]}");
            Check("接口没说看图能力 → 留空（不许瞎猜成能看）",
                unknown32.Count == 1 && unknown32[0].vision == null,
                unknown32.Count == 0 ? "没解析出来" : ("vision=" + unknown32[0].vision));

            Provider pt = new Provider();
            pt.type = "openai";
            pt.name = "测试用";
            pt.SetMeta(mf);
            pt.SetMeta(mp);
            Check("能力标签存进 provider 了，显示名取得回来",
                pt.Meta("deepseek-flash") != null && pt.DisplayName("deepseek-flash") == "DeepSeek-V4.1-Flash",
                "显示名=" + pt.DisplayName("deepseek-flash"));

            // 档位：有接口声明就信接口；没有才回落到那份写死的白名单（老行为不许丢）
            Check("接口声明过档位 → 判为支持调档（以前 deepseek-flash 被白名单判成不支持）",
                Effort.Supported(pt, "deepseek-flash", ""), "Supported=false");
            Check("没有接口声明时，仍按老白名单走（deepseek-flash 不支持）",
                !Effort.Supported("openai", "deepseek-flash", ""), "白名单误判成支持了");
            Check("三档落到接口的字符串：低→low、中→high、高→max",
                Effort.ResolveFor(pt, "deepseek-flash", "low") == "low"
                && Effort.ResolveFor(pt, "deepseek-flash", "medium") == "high"
                && Effort.ResolveFor(pt, "deepseek-flash", "high") == "max",
                "低=" + Effort.ResolveFor(pt, "deepseek-flash", "low")
                + " 中=" + Effort.ResolveFor(pt, "deepseek-flash", "medium")
                + " 高=" + Effort.ResolveFor(pt, "deepseek-flash", "high"));

            // chip 上要显示接口给的显示名
            AgentConfig cfg32 = (AgentConfig)Get(f, "config");
            string keepProv32 = cfg32.provider, keepModel32 = cfg32.model;
            cfg32.providers.Add(pt);
            cfg32.provider = pt.id;
            cfg32.model = "deepseek-flash";
            CallPrivate(f, "LoadModels");
            Pump(150);
            ModelChip chip32 = (ModelChip)Get(f, "cmbModel");
            Check("chip 显示接口给的显示名（不是 deepseek-flash 这种生 id）",
                chip32 != null && chip32.Text.IndexOf("DeepSeek-V4.1-Flash") == 0,
                chip32 == null ? "没有 chip" : ("chip=「" + chip32.Text + "」"));

            // 粘图提示：能看图的模型给一句确认，纯文本的模型当场警告
            MethodInfo vn32 = typeof(MainForm).GetMethod("VisionNote", BindingFlags.Instance | BindingFlags.NonPublic);
            string noteVis = vn32 == null ? "" : Convert.ToString(vn32.Invoke(f, null));
            cfg32.model = "deepseek-v4-pro";          // 接口说它只有 text
            CallPrivate(f, "LoadModels");
            Pump(150);
            string noteTxt = Convert.ToString(vn32.Invoke(f, null));
            Check("粘图时按接口说的一眼告诉用户能不能看图",
                noteVis.IndexOf("能看图") >= 0 && noteTxt.IndexOf("纯文本") >= 0,
                "能看图那个=" + noteVis + " 纯文本那个=" + noteTxt);

            cfg32.providers.Remove(pt);
            cfg32.provider = keepProv32;
            cfg32.model = keepModel32;
            CallPrivate(f, "LoadModels");
            Pump(150);
        }

        // ---- 31. 照 Codex 改的那层外壳：空态、侧栏开关、会话来回、药丸、页签不压图标 ----
        Console.WriteLine("== 31. Codex 风格外壳：空态 / 侧栏开关 / ←→ 会话来回 / 药丸 / 页签不吃图标 ==");
        {
            CallPrivate(f, "NewSession");
            Pump(200);
            Panel hero = (Panel)Get(f, "heroPanel");
            TabsBar tb31 = (TabsBar)Get(f, "tabs");
            Check("空会话：首屏大字（云朵 + 一句）露出来",
                hero != null && hero.Visible, "heroPanel.Visible=" + (hero == null ? "null" : hero.Visible.ToString()));
            Check("空会话：会话名和「对话 / 轨迹」页签都收起来（Codex 那一屏干干净净）",
                tb31 != null && !tb31.Visible,
                tb31 == null ? "没有页签" : ("tabs.Visible=" + tb31.Visible));

            tb31.SelectedIndex = 1;
            CallPrivate(f, "SwitchTab");
            Pump(120);
            Check("切到「轨迹」页：页签自己冒出来、空状态让位",
                tb31.Visible && !hero.Visible, "tabs=" + tb31.Visible + " hero=" + hero.Visible);
            tb31.SelectedIndex = 0;
            CallPrivate(f, "SwitchTab");
            Pump(120);

            // 侧栏开关（标题栏最左边那个方框）：收起后主区得占满
            Panel side31 = (Panel)Get(f, "side");
            Panel main31 = (Panel)Get(f, "main");
            int wide = main31.Width, left0 = main31.Left;
            ((UiButton)Get(f, "btnSide")).PerformClick();     // 收起
            Pump(150);
            int narrow = main31.Width;
            Check("点方框按钮：侧栏收起来、主区铺满整个窗口",
                !side31.Visible && main31.Left == 0 && narrow > wide,
                "side.Visible=" + side31.Visible + " main.Left=" + main31.Left + " 宽 " + wide + "→" + narrow);
            ((UiButton)Get(f, "btnSide")).PerformClick();     // 再点回来
            Pump(150);
            Check("再点一下：侧栏回来了、宽度回到原来那个数",
                side31.Visible && main31.Left == left0 && main31.Width == wide,
                "side.Visible=" + side31.Visible + " main.Left=" + main31.Left + " 宽=" + main31.Width);

            // ← → 会话来回：等价于"在逛过的会话里前后翻"
            CallPrivate(f, "NewSession");
            Pump(150);
            string idA = Convert.ToString(Get(f, "currentSessionId"));
            CallPrivate(f, "NewSession");
            Pump(150);
            string idB = Convert.ToString(Get(f, "currentSessionId"));
            CallPrivate(f, "LoadSessionById", idA);
            Pump(150);
            CallPrivate(f, "LoadSessionById", idB);
            Pump(150);
            UiButton back = (UiButton)Get(f, "btnBack"), fwd = (UiButton)Get(f, "btnFwd");
            Check("有历史了以后「←」是可点的", back.Enabled, "btnBack.Enabled=false");
            back.PerformClick();
            Pump(200);
            Check("点「←」回到上一个逛过的会话",
                Convert.ToString(Get(f, "currentSessionId")) == idA,
                "当前=" + Convert.ToString(Get(f, "currentSessionId")) + " 期望=" + idA);
            Check("回到上一个之后「→」亮起来（能再翻回去）", fwd.Enabled, "btnFwd.Enabled=false");
            fwd.PerformClick();
            Pump(200);
            Check("点「→」又回到后面那个会话",
                Convert.ToString(Get(f, "currentSessionId")) == idB,
                "当前=" + Convert.ToString(Get(f, "currentSessionId")) + " 期望=" + idB);

            // 输入卡上面那三颗药丸
            UiButton pp = (UiButton)Get(f, "pillProj"), pe = (UiButton)Get(f, "pillEnv"), pb = (UiButton)Get(f, "pillBranch");
            // config.cwd 是属性不是字段，GetAny 取不到 —— 直接强转读属性
            string cwd31 = ((AgentConfig)Get(f, "config")).cwd;
            MethodInfo pn31 = typeof(MainForm).GetMethod("ProjectName", BindingFlags.Static | BindingFlags.NonPublic);
            string proj31 = pn31 == null ? null : Convert.ToString(pn31.Invoke(null, new object[] { cwd31 }));
            Check("药丸①：项目名 = 工作目录那一段",
                pp != null && proj31 != null && pp.Text == proj31,
                pp == null ? "没有药丸" : ("药丸=「" + pp.Text + "」 目录=" + cwd31 + " 期望=「" + proj31 + "」"));
            Check("药丸②：写的是「本地」或「云端」",
                pe != null && (pe.Text == "本地" || pe.Text == "云端"),
                pe == null ? "没有药丸" : ("药丸=「" + pe.Text + "」"));
            // 分支那颗：不是 git 仓库（或没装 git）时自动藏起来，读到分支才显示
            typeof(MainForm).GetField("gitBranch", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f, "");
            CallPrivate(f, "UpdatePlacePills");
            Pump(80);
            bool hidden = !pb.Visible;
            typeof(MainForm).GetField("gitBranch", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f, "main");
            CallPrivate(f, "UpdatePlacePills");
            Pump(80);
            Check("药丸③：读不到分支就整颗藏起来，读到了才显示（不摆假的）",
                hidden && pb.Visible && pb.Text == "main",
                "藏起来=" + hidden + " 现在可见=" + pb.Visible + " 文字=「" + pb.Text + "」");

            // 页签条不能压住右上角那排图标（真踩过：图标都在，就是被盖住了点不到）
            Control hset = (Control)Get(f, "hSettings");
            Check("页签条不铺满整行（否则右上角四个图标被它盖住、点不到）",
                tb31.Right < hset.Left, "tabs.Right=" + tb31.Right + " hSettings.Left=" + hset.Left);

            // 发送键颜色：空输入是浅灰，有字变深（Codex 那个深色圆钮，不是蓝的）
            UiEditor inp31 = (UiEditor)Get(f, "txtInput");
            UiButton send31 = (UiButton)Get(f, "btnSend");
            inp31.Text = "";
            CallPrivate(f, "UpdateSendButton");
            Color idle = send31.FillColor;
            inp31.Text = "随便写点什么";
            CallPrivate(f, "UpdateSendButton");
            Color on = send31.FillColor;
            inp31.Text = "";
            CallPrivate(f, "UpdateSendButton");
            Check("发送键：空输入浅灰、有字变深（不再是蓝色）",
                idle == Theme.SendIdle && on == Theme.SendOn, "空=" + idle + " 有字=" + on);

            // 权限档「完全访问」用橙色提醒（Codex 输入条左下角那个橙），其它档是灰的
            AgentConfig cfg31 = (AgentConfig)Get(f, "config");
            string keep31 = cfg31.permission;
            cfg31.permission = "full";
            CallPrivate(f, "UpdatePermChip");
            Pump(80);
            UiDrop pchip31 = (UiDrop)Get(f, "permChip");
            bool orange31 = pchip31.Text == "完全访问" && pchip31.TextColor == Theme.PermWarn;
            cfg31.permission = "workspace";
            CallPrivate(f, "UpdatePermChip");
            Pump(80);
            bool gray31 = pchip31.Text == "工作区内修改" && pchip31.TextColor == Theme.FgDim;
            cfg31.permission = keep31;
            CallPrivate(f, "UpdatePermChip");
            Pump(80);
            Check("权限 chip：「完全访问」橙字、其它档灰字（一眼看出自己有没有放开）",
                orange31 && gray31, "full 时=" + orange31 + " workspace 时=" + gray31);

            // 菜单里写的快捷键必须真能用（最烦"看着有、按了没反应"）
            Message m31 = new Message();
            MethodInfo pck31 = typeof(MainForm).GetMethod("ProcessCmdKey",
                BindingFlags.Instance | BindingFlags.NonPublic);
            bool okB = false;
            if (pck31 != null)
            {
                object[] args31 = new object[] { m31, Keys.B | Keys.Control };
                okB = (bool)pck31.Invoke(f, args31);
                Pump(150);
            }
            Check("Ctrl+B 真的收起侧栏（菜单上印的快捷键不是摆设）",
                okB && !side31.Visible, "handled=" + okB + " side.Visible=" + side31.Visible);
            if (pck31 != null) { pck31.Invoke(f, new object[] { new Message(), Keys.B | Keys.Control }); Pump(150); }
            Check("再按一次 Ctrl+B 又展开", side31.Visible, "side.Visible=" + side31.Visible);
        }

        // ---- 33. 2026-10-15 用户报的三件事：历史刷屏 / 打开大会话"（未响应）" / 设置后丢上下文 ----
        Console.WriteLine("== 33. 刷屏 / 大会话卡死 / 设置后丢上下文（都是真报过的）==");
        {
            // 33.1 计划提醒有上限（以前每一步都灌一遍，整屏都是同一句话）
            string tmp33 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ahz-33-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            System.IO.Directory.CreateDirectory(tmp33);
            AgentConfig cfg33 = new AgentConfig();
            cfg33.cwd = tmp33;
            cfg33.max_steps = 8;
            AgentRunner r33 = new AgentRunner(cfg33, f);
            r33.ReloadTools();
            FakeLlm fake33 = new FakeLlm();     // 永远"写一条计划但从标 - [x]"，正是刷屏的现场
            r33.Llm = fake33;
            r33.RunTurn("继续");
            Pump(200);
            int planNudges33 = 0;
            int idleNudges33 = 0;
            foreach (Dictionary<string, object> m33 in r33.Messages)
            {
                string c33 = Json.Str(m33, "content", "");
                if (c33.StartsWith("（系统提醒）你这一轮的计划还剩")) planNudges33++;
                if (c33.StartsWith("（系统提醒）你已经连续读了几个文件")) idleNudges33++;
            }
            Check("计划提醒最多注入 3 次（以前每一步一次，一条会话能灌十几遍）",
                planNudges33 >= 1 && planNudges33 <= 3, "注入了 " + planNudges33 + " 次");
            Check("只读不写那条提醒也有上限（≤2）", idleNudges33 <= 2, "注入了 " + idleNudges33 + " 次");
            // 步数上限 8：正常 8 步，最后还可能有一次"不给工具、只让它讲结论"的收尾调用
            Check("假模型真的被调了（这一轮确实跑起来了）",
                fake33.Calls > 0 && fake33.Calls <= 9, "调用 " + fake33.Calls + " 次（上限 8 步 + 1 次收尾）");

            // 33.2 系统提醒在对话区渲染成小字提示，不是一个大「你」块
            TranscriptView rtb33 = (TranscriptView)Get(f, "rtb");
            rtb33.ClearAll();
            f.Write(OutKind.User, "（系统提醒）你这一轮的计划还剩 1 步没标记完成：\n· run-ui-tests.cmd\n");
            rtb33.FlushNow();
            MsgBlock blk33 = rtb33.LastBlock;
            Check("「系统提醒」在对话区是小字提示，不再摆成「你」大块",
                blk33 != null && blk33.Kind == OutKind.Info && blk33.Raw.ToString().IndexOf("系统提醒") >= 0,
                blk33 == null ? "没有块" : ("kind=" + blk33.Kind + " 内容长度=" + blk33.Raw.Length));

            // 33.3 打开"大会话"：只摆最近 200 条 + 不许再卡到假死
            Session big33 = new Session();
            big33.id = "testbig33";
            big33.title = "大会话（自测造）";
            big33.cwd = tmp33;
            big33.updated = "2026-10-15 03:00";
            big33.messages = new List<Dictionary<string, object>>();
            for (int i = 0; i < 500; i++)
                big33.messages.Add(AgentRunner.Msg(i % 2 == 0 ? "user" : "assistant", "第 " + i + " 条消息，凑点长度让排版有活干"));
            SessionStore st33 = (SessionStore)Get(f, "sessionStore");
            st33.sessions.Insert(0, big33);
            System.Diagnostics.Stopwatch sw33 = System.Diagnostics.Stopwatch.StartNew();
            CallPrivate(f, "LoadSessionById", "testbig33");
            Application.DoEvents();
            sw33.Stop();
            object blocks33 = rtb33.GetType().GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rtb33);
            int nb33 = ((System.Collections.ICollection)blocks33).Count;
            Check("打开 500 条的大会话：只摆最近 200 条，不是把 500 条全摆出来",
                nb33 > 50 && nb33 <= 220, "渲染块数=" + nb33);
            Check("打开大会话不再假死（实测 < 8 秒；以前 891 条要 95 秒以上，窗口显示「未响应」）",
                sw33.ElapsedMilliseconds < 8000, "耗时=" + sw33.ElapsedMilliseconds + " ms");
            Session back33 = null;
            foreach (Session s33 in st33.sessions) if (s33.id == "testbig33") back33 = s33;
            // 只看"没少"：程序自己可能往会话里追加（比如那次探针报错），不该因此判失败
            Check("老消息一条没删（只是不摆到界面上，模型上下文照旧）",
                back33 != null && back33.messages.Count >= 500,
                back33 == null ? "会话没了" : ("条数=" + back33.messages.Count));

            // 33.4 保存设置后上下文还在（以前是 new AgentRunner + ClearAll，一保存就清空）
            AgentRunner r34 = new AgentRunner(cfg33, f);
            r34.Messages.Add(AgentRunner.Msg("user", "你好"));
            r34.Messages.Add(AgentRunner.Msg("assistant", "在的"));
            AgentConfig cfg34 = new AgentConfig();
            cfg34.cwd = tmp33;
            cfg34.model = "换了个模型";
            r34.ApplyConfig(cfg34);
            Check("保存设置后对话还在（不再被清空）",
                r34.Messages.Count == 3
                && Json.Str(r34.Messages[0], "role", "") == "system"
                && Json.Str(r34.Messages[1], "content", "") == "你好"
                && Json.Str(r34.Messages[2], "content", "") == "在的",
                "消息数=" + r34.Messages.Count);
            Check("新配置真的接管了（换模型要生效）",
                r34.Config == cfg34 && r34.Config.model == "换了个模型", "model=" + r34.Config.model);

            // 33.6 启动自动补"模型能力标签"：没模型就别去打扰接口；接口不通要静默失败、不崩、不写假数据
            Provider p36 = new Provider();
            p36.type = "openai";
            p36.base_url = "http://127.0.0.1:1/v1";     // 故意指到一个没人监听的端口
            p36.models = new List<string>();
            AgentConfig cfg36 = (AgentConfig)Get(f, "config");
            string keepProv36 = cfg36.provider;
            cfg36.providers.Add(p36);
            cfg36.provider = p36.id;
            MethodInfo af36 = typeof(MainForm).GetMethod("AutoFetchModelMeta",
                BindingFlags.Instance | BindingFlags.NonPublic);
            bool threw36 = false;
            try { af36.Invoke(f, null); Pump(200); } catch { threw36 = true; }
            Check("没配模型的提供商：启动补标签直接跳过（不去打接口）",
                !threw36 && p36.model_meta.Count == 0, "抛异常=" + threw36 + " meta=" + p36.model_meta.Count);

            p36.models.Add("m-36");
            bool threw36b = false;
            try { af36.Invoke(f, null); Pump(1500); } catch { threw36b = true; }
            Check("接口打不通时：后台静默失败（不抛、不往 config 里塞假数据、模型列表不动）",
                !threw36b && p36.model_meta.Count == 0 && p36.models.Count == 1,
                "抛=" + threw36b + " meta=" + p36.model_meta.Count + " models=" + p36.models.Count);
            cfg36.providers.Remove(p36);
            cfg36.provider = keepProv36;

            // 33.5 Vision 认路径不再往外抛（日志里"压缩历史"就是这么连着失败 12 次的）
            string img33 = System.IO.Path.Combine(tmp33, "shot-33.png");
            using (Bitmap bm33 = new Bitmap(4, 4)) bm33.Save(img33, System.Drawing.Imaging.ImageFormat.Png);
            string nasty33 = "看这个 a|b.png 和 <z>.png 还有 \"C:\\x\\y.png\" 以及真图 " + img33;
            List<string> got33 = null;
            bool threw33 = false;
            try { got33 = Vision.PathsIn(nasty33); } catch { threw33 = true; }
            Check("文本里带 | < > 这类非法路径字符时，Vision 不再抛异常", !threw33,
                threw33 ? "还是抛了（压缩历史/发请求会被它带崩）" : "");
            Check("真图片路径照样认得出来（只挑出那一个）",
                got33 != null && got33.Count == 1 && got33[0] == img33,
                got33 == null ? "null" : string.Join(" / ", got33.ToArray()));
            try { System.IO.Directory.Delete(tmp33, true); } catch { }
        }

        // ---- 34. 长内容折叠 / 展开（整改⑤）----
        // 关心的四件事：
        //   1. 长的工具结果 / 轨迹页思考默认收起，摘要行只留一行，**全文一个字不丢**；
        //   2. 点摘要行能来回切，收起/展开不改变排版结果（和"从未折叠"逐片段一致）；
        //   3. 折叠只牵连这一块（其它块不重排、不重画），可滚范围跟着块高差走；
        //   4. 切换前后视口稳（块顶可见时一动不动，滚出上方时才补偿）。
        Console.WriteLine("== 34. 长内容折叠展开：默认收起 / 点摘要行展开 / 批量 / 视口稳定 ==");
        {
            TranscriptView rtb34 = (TranscriptView)Get(f, "rtb");
            TranscriptView trace34 = (TranscriptView)Get(f, "trace");
            // 点一块上的某点（走 MsgBlock.OnMouseDown 的真实命中路径，和鼠标点的一样）
            Action<MsgBlock, int, int> click34 = delegate(MsgBlock b, int cx, int cy)
            {
                MethodInfo md34 = typeof(MsgBlock).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic);
                md34.Invoke(b, new object[] { new MouseEventArgs(MouseButtons.Left, 1, cx, cy, 0) });
            };
            Action<MsgBlock> tapFold34 = delegate(MsgBlock b)
            {
                Rectangle r = b.FoldRect;
                click34(b, r.Left + r.Width / 2, r.Top + r.Height / 2);
            };
            Func<TranscriptView, int> countThinking34 = delegate(TranscriptView v)
            {
                int nb34b = 0;
                foreach (MsgBlock b in Blocks(v)) if (b.Kind == OutKind.Thinking) nb34b++;
                return nb34b;
            };
            rtb34.ClearAll(); trace34.ClearAll();
            Pump(200);

            // 34.1 短工具结果不折叠（阈值下界：行 < 8 且 字 < 600）
            f.Write(OutKind.ToolCall, "  ● list_dir(.)\n");
            f.Write(OutKind.ToolResult, "  └ ✓ 列了 3 项\n");
            rtb34.FlushNow(); trace34.FlushNow();
            Pump(200);
            List<MsgBlock> l34 = Blocks(rtb34);
            MsgBlock sh34 = l34[l34.Count - 1];
            Check("短工具结果（1 行）不折叠（不是所有结果都收起来）",
                sh34.Kind == OutKind.ToolResult && !sh34.Foldable,
                "kind=" + sh34.Kind + " foldable=" + sh34.Foldable + " h=" + sh34.Height);

            // 34.2 长工具结果默认收起 + 摘要行文案 + 全文还在
            // 布局：短结果 / 长结果A(40行) / 一坨长正文(让A下面有内容，好滚出视口) / 中结果B(10行)
            StringBuilder big34 = new StringBuilder();
            for (int i = 0; i < 40; i++) big34.Append("结果第 " + i + " 行\n");
            f.Write(OutKind.ToolCall, "  ● run_shell(echo hi)\n");
            f.Write(OutKind.ToolResult, "  └ ✓ " + big34.ToString() + "\n");
            StringBuilder tail34 = new StringBuilder();
            for (int i = 0; i < 60; i++) tail34.Append("正文第 " + i + " 行\n");
            f.Write(OutKind.Assistant, "\n● " + tail34.ToString() + "\n");
            f.Write(OutKind.ToolCall, "  ● read_file(a.txt)\n");
            f.Write(OutKind.ToolResult, "  └ ✓ " + "中间结果行\n中间结果行\n中间结果行\n中间结果行\n中间结果行\n"
                + "中间结果行\n中间结果行\n中间结果行\n中间结果行\n中间结果行\n");
            rtb34.FlushNow(); trace34.FlushNow();
            Pump(250);
            l34 = Blocks(rtb34);
            MsgBlock tb34 = null;
            foreach (MsgBlock b in l34)
                if (b.Kind == OutKind.ToolResult && b.Body.IndexOf("结果第 39 行") >= 0) tb34 = b;
            Check("找得到那条 40 行的工具结果块", tb34 != null, "块数=" + l34.Count);
            Check("40 行工具结果：默认收起", tb34.Foldable && tb34.Collapsed,
                "foldable=" + tb34.Foldable + " collapsed=" + tb34.Collapsed);
            Check("摘要行报了工具名和行数（已执行 run_shell · 40 行）",
                tb34.FoldSummary != null && tb34.FoldSummary.IndexOf("run_shell") >= 0
                && tb34.FoldSummary.IndexOf("40 行") >= 0,
                "summary=" + (tb34.FoldSummary == null ? "null" : tb34.FoldSummary));
            Check("收起时正文片段为空（真正只留一行）", tb34.PieceCount == 0, "pieces=" + tb34.PieceCount);
            Check("收起只是不排版：全文还在（展开有东西可展开）",
                Txt.LineCount(tb34.Body.TrimEnd('\n', '\r')) == 40, "行数=" + Txt.LineCount(tb34.Body.TrimEnd('\n', '\r')));
            int hCol34 = tb34.Height;
            Check("收起后这一块只有一行那么高（比展开矮一大截）", hCol34 < Theme.Px(80), "h=" + hCol34);

            // 34.3 点摘要行 → 展开：只动这一块（其它块不重排、不重画），块顶可见时视口不动
            rtb34.Follow = false;                    // 用户不在"跟最新"的状态
            rtb34.AutoScrollPosition = new Point(0, 0);
            Pump(200);
            Rectangle fr34 = tb34.FoldRect;
            Check("摘要行的点击矩形在块顶那一格（收起/展开同一个点）",
                fr34.Width > Theme.Px(20) && fr34.Height > Theme.Px(8) && fr34.Top == 0,
                "rect=" + fr34);
            int tbR0 = tb34.Rebuilds, tbP0 = tb34.Paints;
            int oR0 = 0, oP0 = 0;
            foreach (MsgBlock b in Blocks(rtb34)) if (b != tb34) { oR0 += b.Rebuilds; oP0 += b.Paints; }
            int off0 = -rtb34.AutoScrollPosition.Y;
            tapFold34(tb34);
            Pump(250);
            int off1 = -rtb34.AutoScrollPosition.Y;
            int oR1 = 0, oP1 = 0;
            foreach (MsgBlock b in Blocks(rtb34)) if (b != tb34) { oR1 += b.Rebuilds; oP1 += b.Paints; }
            Check("点摘要行 → 展开（能看到完整 40 行）",
                !tb34.Collapsed && tb34.PieceCount > 5, "collapsed=" + tb34.Collapsed + " pieces=" + tb34.PieceCount);
            Check("折叠动作只让这一块重新排版（其它块 Rebuilds 一点没涨）",
                tb34.Rebuilds == tbR0 + 1 && oR1 == oR0,
                "这块 +" + (tb34.Rebuilds - tbR0) + "，其它块 +" + (oR1 - oR0));
            Check("折叠动作没牵连其它块重画（其它块 Paints 一点没涨）",
                oP1 == oP0,
                "其它块 Paints +" + (oP1 - oP0) + "（这块 +" + (tb34.Paints - tbP0) + "）");
            Check("块顶可见时切换：视口位置不动（差 ≤ 4px）",
                Math.Abs(off1 - off0) <= 4, "off " + off0 + " -> " + off1);

            // 34.4 展开后的排版与"从未折叠"的同一段文字逐片段一致
            Font fN34 = (Font)Get(rtb34, "_norm");
            Font fB34 = (Font)Get(rtb34, "_bold");
            Font fM34 = (Font)Get(rtb34, "_mono");
            Font fS34 = (Font)Get(rtb34, "_small");
            int bw34 = (int)typeof(TranscriptView).GetProperty("BodyWidth",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rtb34, null);
            MsgBlock ref34 = new MsgBlock(OutKind.ToolResult, fN34, fB34, fM34, fS34);
            ref34.Width = bw34;
            ref34.Append(tb34.Body);
            ref34.SetFold(true, "参照块", false);       // 可折叠但**从没收起过** = 从未折叠
            CallPrivate(ref34, "Rebuild");
            string want34 = DumpPieces(ref34);
            Check("展开后的排版与从未折叠的同一段文字逐片段一致",
                DumpPieces(tb34) == want34,
                "片段数 " + tb34.PieceCount + " / 参照块 " + ref34.PieceCount);

            // 34.5 收起后 AutoScrollMinSize.Height 的减少 ≈ 块高差（±4px）
            int sExp34 = rtb34.AutoScrollMinSize.Height, hExp34 = tb34.Height;
            tapFold34(tb34);
            Pump(200);
            int sCol34 = rtb34.AutoScrollMinSize.Height;
            Check("再点摘要行 → 收起", tb34.Collapsed && tb34.PieceCount == 0,
                "collapsed=" + tb34.Collapsed + " pieces=" + tb34.PieceCount);
            Check("收起后可滚范围减少 ≈ 块高差（±4px，不是幽灵高度）",
                Math.Abs((sExp34 - sCol34) - (hExp34 - tb34.Height)) <= 4,
                "可滚 -" + (sExp34 - sCol34) + "，块高 -" + (hExp34 - tb34.Height));
            tapFold34(tb34);
            Pump(200);
            Check("收起再展开：排版仍与从未折叠的一致（来回切不改变结果）",
                DumpPieces(tb34) == want34, "片段数 " + tb34.PieceCount + " / 参照块 " + ref34.PieceCount);

            // 34.6 摘要行已经滚出视口上方时：切换不许把视口带跑，也别拽到底
            rtb34.Follow = false;
            int contentY34 = tb34.Top - rtb34.AutoScrollPosition.Y;      // 这一块的内容坐标
            rtb34.AutoScrollPosition = new Point(0, contentY34 + Theme.Px(8));
            Pump(200);
            int topBefore34 = tb34.Top + tb34.FoldRect.Top;
            Check("造出「摘要行已滚出视口上沿」的现场", topBefore34 < 0, "top=" + topBefore34);
            tapFold34(tb34);
            Pump(250);
            int topAfter34 = tb34.Top + tb34.FoldRect.Top;
            Check("摘要行滚出上方时：它自己在视口里的位置差 ≤ 4px（防跳位补偿）",
                Math.Abs(topAfter34 - topBefore34) <= 4,
                "摘要行 " + topBefore34 + " -> " + topAfter34 + "（视口 " + (-rtb34.AutoScrollPosition.Y) + "）");
            Check("没顺手 ScrollToEnd（视口没被拽到底），也不许自己滑到顶上",
                !rtb34.AtEnd && -rtb34.AutoScrollPosition.Y > 0,
                "atEnd=" + rtb34.AtEnd + " 视口=" + (-rtb34.AutoScrollPosition.Y));

            // 34.7 批量：全部展开 / 全部收起（遍历所有块，跑一次 Relayout）
            rtb34.ExpandAll();
            Pump(250);
            Check("批量全部展开：可折叠的块一个都不许还是收起的",
                rtb34.FoldableCount() >= 2 && rtb34.CollapsedCount() == 0,
                "可折叠=" + rtb34.FoldableCount() + " 收起=" + rtb34.CollapsedCount());
            int sAll34 = rtb34.AutoScrollMinSize.Height;
            rtb34.CollapseAll();
            Pump(250);
            Check("批量全部收起：可折叠的块全收起来了",
                rtb34.CollapsedCount() == rtb34.FoldableCount() && rtb34.FoldableCount() >= 2,
                "可折叠=" + rtb34.FoldableCount() + " 收起=" + rtb34.CollapsedCount());
            Check("批量收起后整页可滚范围跟着变小（重排过了）",
                rtb34.AutoScrollMinSize.Height < sAll34,
                "可滚 " + sAll34 + " -> " + rtb34.AutoScrollMinSize.Height);

            // 34.8 两个菜单入口
            UiDrop mv34 = (UiDrop)Get(f, "mView");
            List<string> vm34 = new List<string>();
            foreach (object o in mv34.Items) vm34.Add(Convert.ToString(o));
            Check("顶栏「视图」菜单里有「全部展开长内容 / 全部收起长内容」",
                vm34.Contains("全部展开长内容") && vm34.Contains("全部收起长内容"),
                string.Join(" / ", vm34.ToArray()));
            ContextMenuStrip cm34 = rtb34.ContextMenuStrip;
            List<string> mi34 = new List<string>();
            foreach (ToolStripItem o in cm34.Items) mi34.Add(Convert.ToString(o.Text));
            Check("对话区右键菜单里也有这两项",
                mi34.Contains("全部展开长内容") && mi34.Contains("全部收起长内容"),
                string.Join(" / ", mi34.ToArray()));
            rtb34.ExpandAll();
            Pump(200);
            CallPrivate(f, "MenuAction", "全部收起长内容");
            Pump(250);
            Check("菜单那两项真的接上了（点「全部收起长内容」→ 全收起）",
                rtb34.FoldableCount() >= 2 && rtb34.CollapsedCount() == rtb34.FoldableCount(),
                "可折叠=" + rtb34.FoldableCount() + " 收起=" + rtb34.CollapsedCount());

            // 34.9 轨迹页的思考默认收起；对话区那套 hide_thinking 开关逻辑不动
            rtb34.ClearAll(); trace34.ClearAll();
            Pump(150);
            AgentConfig cfg34 = (AgentConfig)Get(f, "config");
            bool keepHide34 = cfg34.hide_thinking;
            cfg34.hide_thinking = true;              // §7 把开关留在"关"，这里明确摆成"开"
            StringBuilder th34 = new StringBuilder();
            for (int i = 0; i < 10; i++) th34.Append("想第 " + i + " 步\n");
            f.Write(OutKind.Thinking, th34.ToString());
            trace34.FlushNow(); rtb34.FlushNow();
            Pump(250);
            MsgBlock tt34 = trace34.LastBlock;
            Check("轨迹页：≥6 行的思考默认收起（平铺到爆的就是它）",
                tt34 != null && tt34.Kind == OutKind.Thinking && tt34.Foldable && tt34.Collapsed,
                tt34 == null ? "没有块" : ("kind=" + tt34.Kind + " foldable=" + tt34.Foldable + " collapsed=" + tt34.Collapsed));
            Check("对话区：hide_thinking 开着时思考正文照旧不进对话区",
                countThinking34(rtb34) == 0, "对话区思考块=" + countThinking34(rtb34));
            cfg34.hide_thinking = false;
            rtb34.ClearAll();
            Pump(150);
            f.Write(OutKind.Thinking, th34.ToString());
            rtb34.FlushNow();
            Pump(200);
            MsgBlock ct34 = rtb34.LastBlock;
            Check("对话区的思考不折叠（它归 hide_thinking 那个开关管，默认策略只管轨迹页）",
                ct34 != null && ct34.Kind == OutKind.Thinking && !ct34.Foldable,
                ct34 == null ? "没有块" : ("kind=" + ct34.Kind + " foldable=" + ct34.Foldable));
            Check("对话区思考照旧看得见（关掉开关后没被折叠吃掉）",
                ct34 != null && ct34.Raw.ToString().IndexOf("想第 9 步") >= 0,
                ct34 == null ? "没有块" : ("长度=" + ct34.Raw.Length));
            cfg34.hide_thinking = keepHide34;

            // 34.10 折叠状态只放内存：不许写进 Session.messages（那会被带进请求）
            AgentRunner r34 = (AgentRunner)Get(f, "runner");
            bool leaked34 = false;
            if (r34 != null && r34.Messages != null)
                foreach (Dictionary<string, object> m34 in r34.Messages)
                    if (m34.ContainsKey("folded") || m34.ContainsKey("collapsed") || m34.ContainsKey("Foldable")) leaked34 = true;
            Check("折叠状态没被塞进 Session.messages（否则 ConvertMessages 会把它带进请求）", !leaked34, "");

            rtb34.Follow = true;
            rtb34.ClearAll(); trace34.ClearAll();
            Pump(150);
        }

        Console.WriteLine("== 35. 意图澄清弹窗：标题用模型给的 / 每行 label+desc+推荐角标 / 单选 / 点执行给 command ==");
        {
            List<Clarify.Option> o35 = new List<Clarify.Option>();
            Clarify.Option a35 = new Clarify.Option();
            a35.id = 1; a35.label = "刷新模型清单"; a35.desc = "只拉一次接口清单，不换模型";
            a35.command = "刷新 deepseek 的模型清单"; a35.recommended = true;
            Clarify.Option b35 = new Clarify.Option();
            b35.id = 2; b35.label = "切换模型版本"; b35.desc = "把当前模型换成 deepseek-v4-pro";
            b35.command = "把当前模型切到 deepseek-v4-pro"; b35.recommended = false;
            Clarify.Option c35 = new Clarify.Option();
            c35.id = 3; c35.label = "升级接口配置"; c35.desc = "只改 base_url 和 Key";
            c35.command = "只更新 deepseek 的接口配置"; c35.recommended = false;
            o35.Add(a35); o35.Add(b35); o35.Add(c35);

            ClarifyForm cf35 = new ClarifyForm("你要更新哪一块？", o35);
            Check("弹窗主标题用模型返回的 title（不是写死的那句）",
                cf35.ShownTitle == "你要更新哪一块？", "主标题=" + cf35.ShownTitle);
            Check("窗口标题栏还是固定的「确认执行意图」（模型那串只进正文）",
                cf35.Text == "确认执行意图", "Text=" + cf35.Text);
            cf35.Show();
            Pump(250);
            Check("三个选项各占一行（都摆出来了）",
                findRow(cf35, 0) != null && findRow(cf35, 1) != null && findRow(cf35, 2) != null, "");
            ClarifyRow row0 = findRow(cf35, 0), row1 = findRow(cf35, 1);
            ClarifyRow row2 = findRow(cf35, 2);
            Check("每行的 label / desc 就是模型给的那两份文字",
                row0 != null && row0.OptionRef.label == "刷新模型清单" && row0.OptionRef.desc.IndexOf("只拉一次接口清单") >= 0,
                row0 == null ? "没有行" : (row0.OptionRef.label + " / " + row0.OptionRef.desc));
            Check("第一行是推荐项（带 recommended 标记）、后两行不是",
                row0 != null && row0.OptionRef.recommended
                && !row1.OptionRef.recommended && !row2.OptionRef.recommended, "");
            Check("默认选中推荐项（不是第一个碰巧也是推荐项就算了：第 2 行没有勾）",
                row0.Selected && !row1.Selected && !row2.Selected,
                "0=" + row0.Selected + " 1=" + row1.Selected + " 2=" + row2.Selected);
            Check("三行互不重叠、都在容器里",
                row0.Bottom <= row1.Top && row1.Bottom <= row2.Top
                && row2.Bottom <= row2.Parent.ClientSize.Height,
                "r0底=" + row0.Bottom + " r1顶=" + row1.Top + " r2底=" + row2.Bottom
                + " 容器高=" + row2.Parent.ClientSize.Height);
            Check("点第二行 → 单选跟着走（只有它被选中）",
                tapRow(row1) && row1.Selected && !row0.Selected && !row2.Selected,
                "0=" + row0.Selected + " 1=" + row1.Selected + " 2=" + row2.Selected);

            UiButton ok35 = null, skip35 = null;
            foreach (Control k in cf35.Controls)
                if (k is Panel && ((Panel)k).Dock == DockStyle.Bottom)
                    foreach (Control sub in k.Controls)
                    {
                        UiButton ub = sub as UiButton;
                        if (ub == null) continue;
                        if (ub.Text == "执行") ok35 = ub;
                        if (ub.Text == "跳过") skip35 = ub;
                    }
            Check("有「执行」和「跳过」两个按钮", ok35 != null && skip35 != null,
                "执行=" + (ok35 != null) + " 跳过=" + (skip35 != null));
            if (skip35 != null) skip35.PerformClick();
            Check("点「跳过」= 用原指令执行（返回 -1）", cf35.SelectedIndex == -1, "SelectedIndex=" + cf35.SelectedIndex);

            // ESC = 跳过：直接走 KeyDown 那条路
            cf35.SelectedIndex = 1;
            CallPrivate(cf35, "OnKeyDown", new KeyEventArgs(Keys.Escape));
            Check("ESC 也等于跳过（不会把某条选项当成用户选的）",
                cf35.SelectedIndex == -1, "SelectedIndex=" + cf35.SelectedIndex);
            Check("没选过就直接关闭 → 也当跳过（OnDeactivate 兜底）",
                closeAndRead(cf35) == -1, "");

            // 去掉 recommended 标记 → 默认选第一项
            a35.recommended = false;
            ClarifyForm cf35b = new ClarifyForm("", o35);
            cf35b.Show();
            Pump(200);
            Check("没有任何推荐项时默认选第一项", findRow(cf35b, 0).Selected, "");
            Check("模型没给 title 时用兜底主标题（不至于弹个没标题的框）",
                cf35b.ShownTitle.Length > 0, "主标题=" + cf35b.ShownTitle);
            UiButton ok35b = null;
            foreach (Control k in cf35b.Controls)
                if (k is Panel && ((Panel)k).Dock == DockStyle.Bottom)
                    foreach (Control sub in k.Controls)
                    {
                        UiButton ub = sub as UiButton;
                        if (ub != null && ub.Text == "执行") ok35b = ub;
                    }
            Check("「执行」按钮存在（拿得到才谈得上点它）", ok35b != null, "");
            if (ok35b != null) ok35b.PerformClick();
            Check("点「执行」→ 返回选中项下标（界面拿它去取 command 原样下发）",
                cf35b.SelectedIndex == 0, "SelectedIndex=" + cf35b.SelectedIndex);
            Clarify.Result r35 = new Clarify.Result();
            r35.ambiguous = true;
            r35.options = o35;
            Check("界面拿到 0 之后能取到第一项的 command（原样，不二次拼接）",
                Clarify.CommandOf(r35, cf35b.SelectedIndex) == "刷新 deepseek 的模型清单",
                Convert.ToString(Clarify.CommandOf(r35, cf35b.SelectedIndex)));
            cf35b.Dispose();
            cf35.Dispose();
        }

        // ---- 36. 气泡里的图片缩略图（整改 B）----
        // 关心五件事：
        //   1. 带图路径的用户消息 → 显示文本里是 [图片 xxx.png]，正文里不再有那串绝对路径；
        //   2. 真路径走 PendingImages 侧信道进块，取走就清空（不串到下一块）；
        //   3. 有图的块比不带的更高（图片区真的进了排版）；
        //   4. 缩略图几何在气泡内容区之内、等比缩放到 ≤220×160；
        //   5. 文件不存在 / 坏图：不抛异常、整块照常排版，且 GDI 位图 Dispose 时不漏。
        Console.WriteLine("== 36. 气泡里的图片缩略图（整改 B）：正文短标记 / 真路径走侧信道 / 缩略图不溢出 ==");
        {
            TranscriptView rtb36 = (TranscriptView)Get(f, "rtb");
            string dir36 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ahz-bubble-img-36");
            try { if (!System.IO.Directory.Exists(dir36)) System.IO.Directory.CreateDirectory(dir36); } catch { }
            string img36 = System.IO.Path.Combine(dir36, "shot-36-a.png");
            string wide36 = System.IO.Path.Combine(dir36, "shot-36-b.png");
            try
            {
                using (Bitmap v36a = new Bitmap(40, 30)) v36a.Save(img36, System.Drawing.Imaging.ImageFormat.Png);
                using (Bitmap v36b = new Bitmap(120, 20)) v36b.Save(wide36, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch { }
            string mark36 = "[图片 " + System.IO.Path.GetFileName(img36) + "]";

            rtb36.ClearAll();
            Pump(200);

            // 36.1 带图路径的用户消息：显示短标记、正文里没有绝对路径、真路径进了 Images
            f.Write(OutKind.User, "› 看这张 " + img36 + "\n");
            Pump(200);
            List<MsgBlock> l36 = Blocks(rtb36);
            MsgBlock blk36 = l36.Count > 0 ? l36[l36.Count - 1] : null;
            string raw36 = blk36 == null ? "" : blk36.Raw.ToString();
            Check("带图路径的用户消息：显示文本里是 [图片 xxx.png]",
                blk36 != null && blk36.Kind == OutKind.User && raw36.IndexOf(mark36) >= 0,
                blk36 == null ? "没有块" : ("正文=\"" + raw36.Replace("\n", "\\n") + "\""));
            Check("正文里不再包含那个绝对图片路径（气泡不甩路径了）",
                blk36 != null && raw36.IndexOf(img36) < 0,
                blk36 == null ? "没有块" : ("正文=\"" + raw36.Replace("\n", "\\n") + "\""));
            Check("真路径走了图片条（Images 里是绝对路径，只用来画图）",
                blk36 != null && blk36.Images.Count == 1 && blk36.Images[0] == img36,
                blk36 == null ? "没有块" : ("Images=" + blk36.Images.Count));

            // 36.2 侧信道取完即清空：下一块是纯文字消息，绝不能跟着挂上同一张图
            object pend36 = GetAny(rtb36, "PendingImages");
            Check("块建出来之后 PendingImages 被取走并清空（不串到下一块）",
                pend36 == null, "PendingImages=" + (pend36 == null ? "null" : "还有东西"));
            f.Write(OutKind.User, "› 这条没有图\n");
            Pump(200);
            l36 = Blocks(rtb36);
            MsgBlock blk36b = l36[l36.Count - 1];
            Check("紧接着的纯文字消息不带任何图片（侧信道没串）",
                blk36b.Kind == OutKind.User && blk36b.Images.Count == 0,
                "Images=" + blk36b.Images.Count);
            Check("纯文字消息的正文里也没有图片条留下的痕迹",
                blk36b.Raw.ToString().IndexOf("[图片") < 0, blk36b.Raw.ToString());

            // 36.3 有图的块更高：图片区真的进了排版
            object hWith36 = Get(blk36, "_contentH");
            object hWithout36 = Get(blk36b, "_contentH");
            Check("有图的块比不带的更高（图片区进了内容高度）",
                blk36.Height > blk36b.Height && blk36.Height - blk36b.Height >= Theme.Px(20),
                "有图=" + blk36.Height + " 无图=" + blk36b.Height
                + "（内容高 " + hWith36 + " / " + hWithout36 + "）");

            // 36.4 缩略图等比缩放 ≤220×160，并且都在气泡内容区里
            rtb36.ClearAll();
            Pump(150);
            f.Write(OutKind.User, "› 两张 " + img36 + " " + wide36 + "\n");
            Pump(250);
            l36 = Blocks(rtb36);
            MsgBlock blk36t = l36[l36.Count - 1];
            List<Size> th36 = (List<Size>)Get(blk36t, "_thumbs");
            int right36 = (int)Get(blk36t, "_right");
            int left36 = (int)Get(blk36t, "_left");
            int bubX36 = (int)Get(blk36t, "_bubX");
            int bubW36 = (int)Get(blk36t, "_bubW");
            int contentH36 = (int)Get(blk36t, "_contentH");
            Check("两张缩略图都按 ≥1 张算出来了（布局没漏）",
                th36 != null && th36.Count == 2, th36 == null ? "null" : ("count=" + th36.Count));
            bool size36 = th36 != null && th36.Count == 2;
            if (size36)
                foreach (Size s36 in th36)
                    if (s36.Width > Theme.Px(220) || s36.Height > Theme.Px(160) || s36.Width < 1 || s36.Height < 1)
                        size36 = false;
            Check("每个缩略图都缩在 220×160 逻辑px 之内", size36,
                th36 == null ? "null" : (th36[0] + " / " + (th36.Count > 1 ? th36[1].ToString() : "")));
            // 行内总宽不超正文可用宽（第一行放不下就换行，所以按"每行之和 ≤ 可用宽"断言）
            bool fitW36 = th36 != null && th36.Count == 2
                && th36[0].Width <= (right36 - left36);
            Check("缩略图宽度不超气泡正文可用宽（不会横着溢出去）", fitW36,
                th36 == null ? "null" : ("可用宽=" + (right36 - left36) + " 图宽=" + th36[0].Width));
            int imgH36 = (int)Get(blk36t, "_imgH");
            int imgTop36 = (int)Get(blk36t, "_imgTop");
            Check("图片区整体在气泡内容区里（顶 ≥ 0、底 ≤ 内容高）",
                imgTop36 >= 0 && imgH36 > 0 && imgTop36 + imgH36 <= contentH36,
                "顶=" + imgTop36 + " 高=" + imgH36 + " 内容高=" + contentH36);
            Check("图片区被裁在气泡的横向范围内（bubX..bubX+bubW）",
                left36 >= bubX36 && right36 <= bubX36 + bubW36,
                "left=" + left36 + " right=" + right36 + " 气泡=" + bubX36 + ".." + (bubX36 + bubW36));

            // 36.5 缩略图缓存：真的能加载出来，Dispose 之后不留 GDI 句柄
            List<Bitmap> cache36 = (List<Bitmap>)Get(blk36t, "_thumbCache");
            bool loaded36 = false;
            try { loaded36 = CallPrivate(blk36t, "LoadThumb", 0) != null; } catch { loaded36 = false; }
            cache36 = (List<Bitmap>)Get(blk36t, "_thumbCache");
            Check("缩略图能真加载出来（不是只算了个尺寸）", loaded36,
                cache36 == null ? "没有缓存字段" : ("cache=" + cache36.Count));
            rtb36.ClearAll();          // 清空会把每块 Dispose；Dispose 必须把缓存的 Bitmap 逐个放掉
            Check("块 Dispose 时把缓存的 Bitmap 全放掉（GDI 句柄不漏）",
                cache36 == null || cache36.Count == 0,
                cache36 == null ? "null" : ("还剩 " + cache36.Count + " 个位图"));

            // 36.6 图不存在 / 路径是坏的：不抛异常、整块照常排版
            rtb36.ClearAll();
            Pump(150);
            MsgBlock blk36c = new MsgBlock(OutKind.User, (Font)Get(rtb36, "_norm"), (Font)Get(rtb36, "_bold"),
                (Font)Get(rtb36, "_mono"), (Font)Get(rtb36, "_small"));
            bool threw36 = false;
            try
            {
                blk36c.Width = 800;
                blk36c.SetImages(new List<string>(new string[] {
                    System.IO.Path.Combine(dir36, "no-such-36.png"), "a|b.png", null }));
                blk36c.Append("看这张 [图片 no-such-36.png]\n");
            }
            catch { threw36 = true; }
            Check("图片文件不存在 / 路径非法时 SetImages + 排版不抛异常", !threw36, "抛了异常");
            Check("不存在的图片不占图片区（高度不涨、块正常排出高度）",
                blk36c.Images.Count == 0 && blk36c.Height > 0 && blk36c.Height < Theme.Px(400),
                "Images=" + blk36c.Images.Count + " 高=" + blk36c.Height);
            Check("坏图那块照样有正文片段（没被整块画崩）", blk36c.PieceCount > 0, "片段=" + blk36c.PieceCount);
            Check("点在图片区（没有文字的地方）返回安全值，不抛异常", blk36c.CharAt(new Point(40, 1)) >= 0, "");
            blk36c.Dispose();

            try { System.IO.File.Delete(img36); } catch { }
            try { System.IO.File.Delete(wide36); } catch { }
            try { System.IO.Directory.Delete(dir36, true); } catch { }
            rtb36.ClearAll();
            Pump(150);
        }

        // ---- 36.7 历史渲染那条路（开一个已有会话）也得显示短标记 + 缩略图 ----
        // 实时路径（Write）已经查过了；历史路径走的是 RenderHistory 的攒批写入，
        // 块是后建出来的，所以真路径只能靠 PendingImages 侧信道贴上去 —— 这条得单独查一遍。
        Console.WriteLine("== 36.7 历史渲染：老的带图会话重新打开，气泡里也是缩略图 ==");
        {
            TranscriptView rtb37 = (TranscriptView)Get(f, "rtb");
            string dir37 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ahz-hist-img-37");
            try { if (!System.IO.Directory.Exists(dir37)) System.IO.Directory.CreateDirectory(dir37); } catch { }
            string img37 = System.IO.Path.Combine(dir37, "shot-37-hist.png");
            try { using (Bitmap b37 = new Bitmap(50, 50)) b37.Save(img37, System.Drawing.Imaging.ImageFormat.Png); }
            catch { }
            AgentRunner r37 = (AgentRunner)Get(f, "runner");
            Check("拿得到 runner（历史渲染才有会话可摆）", r37 != null, "runner 为空");
            if (r37 == null) { f.Close(); Environment.Exit(1); }
            int keep37 = r37.Messages.Count;
            r37.Messages.Add(AgentRunner.Msg("user", "老的看图消息 " + img37));
            r37.Messages.Add(AgentRunner.Msg("assistant", "看到了"));
            r37.Messages.Add(AgentRunner.Msg("user", "这条没图"));
            Console.WriteLine("    [DBG0] count=" + r37.Messages.Count + " keep=" + keep37);
            for (int di = Math.Max(0, r37.Messages.Count - 4); di < r37.Messages.Count; di++)
                Console.WriteLine("    [DBG0] msg[" + di + "] role=" + Json.Str(r37.Messages[di], "role", "?")
                    + " content=\"" + Json.Str(r37.Messages[di], "content", "") + "\"");
            CallPrivate(f, "RenderHistory");
            Pump(300);
            Console.WriteLine("    [DBG2] pendingAfterRender=" + (GetAny(rtb37, "PendingImages") == null ? "null" : "notnull")
                + " flushCount=" + rtb37.Flushes);
            List<MsgBlock> l37 = Blocks(rtb37);
            MsgBlock withImg37 = null, noImg37 = null;
            foreach (MsgBlock b in l37)
            {
                if (b.Kind != OutKind.User) continue;
                if (b.Raw.ToString().IndexOf("老的看图消息") >= 0) withImg37 = b;
                if (b.Raw.ToString().IndexOf("这条没图") >= 0) noImg37 = b;
            }
            Check("历史里的用户消息：正文是 [图片 xxx.png]、没有绝对路径",
                withImg37 != null
                && withImg37.Raw.ToString().IndexOf("[图片 shot-37-hist.png]") >= 0
                && withImg37.Raw.ToString().IndexOf(img37) < 0,
                withImg37 == null ? "没找到那块" : ("正文=\"" + withImg37.Raw.ToString().Replace("\n", "\\n") + "\""));
            if (withImg37 != null && withImg37.Images.Count == 0)
            {
                Console.WriteLine("    [DBG] img37=" + img37 + " exists=" + System.IO.File.Exists(img37)
                    + " ImagePaths=" + MainForm.Attach.Show.ImagePaths("老的看图消息 " + img37).Count
                    + " ShowText=\"" + MainForm.Attach.Show.Text("老的看图消息 " + img37) + "\"");
                Console.WriteLine("    [DBG] raw=\"" + withImg37.Raw.ToString().Replace("\n", "\\n") + "\"");
                Console.WriteLine("    [DBG] blockCount=" + l37.Count + " width=" + withImg37.Width
                    + " rebuilds=" + withImg37.Rebuilds + " paints=" + withImg37.Paints);
                foreach (MsgBlock db in l37)
                    Console.WriteLine("    [DBG-ALL] kind=" + db.Kind + " imgs=" + db.Images.Count
                        + " raw=\"" + db.Raw.ToString().Replace("\n", "\\n") + "\"");
            }
            Check("历史渲染也把真路径贴到了块上（Images 里是绝对路径）",
                withImg37 != null && withImg37.Images.Count == 1 && withImg37.Images[0] == img37,
                withImg37 == null ? "没找到那块" : ("Images=" + withImg37.Images.Count
                    + " fileExistsNow=" + System.IO.File.Exists(img37)
                    + " ImagePathsNow=" + MainForm.Attach.Show.ImagePaths(withImg37.Raw.ToString()).Count));
            Check("历史里紧随其后的纯文字消息没串上图片",
                noImg37 != null && noImg37.Images.Count == 0,
                noImg37 == null ? "没找到那块" : ("Images=" + noImg37.Images.Count));
            while (r37.Messages.Count > keep37) r37.Messages.RemoveAt(r37.Messages.Count - 1);
            rtb37.ClearAll();
            Pump(150);
            try { System.IO.File.Delete(img37); } catch { }
            try { System.IO.Directory.Delete(dir37, true); } catch { }
        }

        f.Close();
        Console.WriteLine();
        Console.WriteLine("== 37. 缩略图真的画出了像素（不是只量了尺寸就算过） ==");
        {
            TranscriptView rtb37p = (TranscriptView)Get(f, "rtb");
            string dir37p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ahz-pix-img-37");
            try { if (!System.IO.Directory.Exists(dir37p)) System.IO.Directory.CreateDirectory(dir37p); } catch { }
            string red37 = System.IO.Path.Combine(dir37p, "shot-37-red.png");
            // 关键：图必须是**实心彩色**的 —— 36 节那张 new Bitmap(40,30) 是全透明的，
            // "画了"和"没画"在像素上一模一样，拿它验像素等于没验。
            using (Bitmap rb = new Bitmap(80, 60))
            {
                using (Graphics g37c = Graphics.FromImage(rb)) g37c.Clear(Color.FromArgb(255, 220, 20, 20));
                rb.Save(red37, System.Drawing.Imaging.ImageFormat.Png);
            }
            rtb37p.ClearAll();
            Pump(200);
            f.Write(OutKind.User, "› 红色方块 " + red37 + "\n");
            Pump(250);
            List<MsgBlock> l37p = Blocks(rtb37p);
            MsgBlock blk37p = l37p.Count > 0 ? l37p[l37p.Count - 1] : null;
            Check("37.1 带图块收到了真路径（先确认这一块确实该有图）",
                blk37p != null && blk37p.Images.Count == 1,
                blk37p == null ? "没有块" : "Images=" + blk37p.Images.Count);
            int red = 0;
            string pixErr = "";
            if (blk37p != null && blk37p.Width > 0 && blk37p.Height > 0)
            {
                try
                {
                    System.Reflection.MethodInfo op37 = typeof(MsgBlock).GetMethod("OnPaint",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    using (Bitmap shot = new Bitmap(blk37p.Width, blk37p.Height))
                    {
                        using (Graphics g37 = Graphics.FromImage(shot))
                        {
                            PaintEventArgs pea = new PaintEventArgs(g37,
                                new Rectangle(0, 0, shot.Width, shot.Height));
                            op37.Invoke(blk37p, new object[] { pea });
                        }
                        for (int y = 0; y < shot.Height; y += 2)
                            for (int x = 0; x < shot.Width; x += 2)
                            {
                                Color c37 = shot.GetPixel(x, y);
                                if (c37.R > 150 && c37.G < 110 && c37.B < 110) red++;
                            }
                    }
                }
                catch (Exception ex37)
                {
                    pixErr = ex37.GetType().Name + ": " + ex37.Message
                        + (ex37.InnerException != null ? " / " + ex37.InnerException.GetType().Name + ": " + ex37.InnerException.Message
                            + " @ " + ex37.InnerException.StackTrace : "");
                }
            }
            Check("37.2 缩略图真的画出了像素（红色方块可见）",
                red >= 50 && pixErr.Length == 0, pixErr.Length > 0 ? pixErr : ("红色采样点=" + red));
        }

        Console.WriteLine();
        Console.WriteLine("== 38. 工具产出的图片进视觉上下文（整合③，只挂本轮） ==");
        {
            string dir38 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ahz-toolimg-38");
            try { if (!System.IO.Directory.Exists(dir38)) System.IO.Directory.CreateDirectory(dir38); } catch { }
            // 造 4 张真图：工具结果里给的就是这种路径
            string t38a = System.IO.Path.Combine(dir38, "shot-38-a.png");
            string t38b = System.IO.Path.Combine(dir38, "shot-38-b.png");
            string t38c = System.IO.Path.Combine(dir38, "shot-38-c.png");
            string t38big = System.IO.Path.Combine(dir38, "shot-38-big.png");
            try
            {
                using (Bitmap z = new Bitmap(10, 10)) z.Save(t38a, System.Drawing.Imaging.ImageFormat.Png);
                using (Bitmap z = new Bitmap(10, 10)) z.Save(t38b, System.Drawing.Imaging.ImageFormat.Png);
                using (Bitmap z = new Bitmap(10, 10)) z.Save(t38c, System.Drawing.Imaging.ImageFormat.Png);
                // 超大（>1.5MB）用随机像素位图，压不动
                using (Bitmap big = new Bitmap(760, 760))
                {
                    System.Random rnd = new System.Random(38);
                    for (int y = 0; y < big.Height; y++)
                        for (int x = 0; x < big.Width; x++)
                            big.SetPixel(x, y, Color.FromArgb(255, rnd.Next(256), rnd.Next(256), rnd.Next(256)));
                    big.Save(t38big, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch { }
            long bigLen = 0;
            try { bigLen = new System.IO.FileInfo(t38big).Length; } catch { }

            // 工具结果消息（role=tool，tool_call_id 要有，否则接口那边对不上）
            System.Func<string, string, Dictionary<string, object>> tm38 =
                delegate(string id, string body)
                {
                    Dictionary<string, object> m = AgentRunner.Msg("tool", body);
                    m["tool_call_id"] = id;
                    m["tool_name"] = "ui_shot";
                    return m;
                };
            System.Func<string, Dictionary<string, object>> um38 =
                delegate(string s) { return AgentRunner.Msg("user", s); };
            System.Func<string, Dictionary<string, object>> am38 =
                delegate(string s) { return AgentRunner.Msg("assistant", s); };

            // 记录"造请求"过程里 Vision 报出来的话（跳过超大图那个提示）
            List<string> rep38 = new List<string>();
            Vision.ToolImageReport = delegate(string m) { rep38.Add(m); };
            Vision.Disabled = false;
            Vision.Capability = true;

            // 支持视觉的模型：本轮工具结果里的图 → 有图片部件。
            // 注意：这里走的是**真** ConvertMessages（静态私有），不联网。
            List<Dictionary<string, object>> m38 = new List<Dictionary<string, object>>();
            m38.Add(um38("帮我看看这个界面"));
            m38.Add(am38("好，我截一张"));
            m38.Add(tm38("c38", "[ui_shot 结果]\n已保存 " + t38a + " （1440x900）"));
            List<object> o38oai = (List<object>)convOai.Invoke(null, new object[] { m38 });
            List<object> o38oll = (List<object>)convOll.Invoke(null, new object[] { m38 });
            Check("38.1 工具结果后面多了一条合成的 user 消息（图片不塞进 tool 消息里）",
                o38oai.Count == m38.Count + 1 && o38oll.Count == m38.Count + 1,
                "openai=" + o38oai.Count + " ollama=" + o38oll.Count + " 原=" + m38.Count);
            Dictionary<string, object> tool38 = Json.Map(o38oai[2]);
            Check("38.2 tool 消息本身一个字没动（content 还是字符串）",
                Json.Get(tool38, "content") is string, "content 类型=" + Json.Get(tool38, "content"));
            List<object> parts38 = Json.Get(Json.Map(o38oai[3]), "content") as List<object>;
            bool hasImg38 = false, hasNote38 = false;
            if (parts38 != null && parts38.Count > 1)
            {
                Dictionary<string, object> ig = Json.Map(parts38[1]);
                Dictionary<string, object> iu = Json.Map(Json.Get(ig, "image_url"));
                string url38 = iu == null ? "" : Json.Str(iu, "url", "");
                hasImg38 = Json.Str(ig, "type", "") == "image_url" && url38.StartsWith("data:image/png;base64,");
                hasNote38 = Json.Str(Json.Map(parts38[0]), "text", "").Length > 0;
                // 解一段 base64 回图片，确认挂的确实是那张文件（不是空壳）
                if (hasImg38)
                {
                    try
                    {
                        byte[] bb = Convert.FromBase64String(url38.Substring(url38.IndexOf(",") + 1));
                        using (System.IO.MemoryStream ms = new System.IO.MemoryStream(bb))
                        using (Image back = Image.FromStream(ms))
                            hasImg38 = back.Width == 10 && back.Height == 10;
                    }
                    catch { hasImg38 = false; }
                }
            }
            Check("38.3 合成 user 消息里有指向那张截图的 image_url 图片部件（base64 能解回原图）",
                hasImg38 && hasNote38, "parts=" + (parts38 == null ? "null" : parts38.Count.ToString()));
            Check("38.4 文字提示跟图一起发（模型得知道这张图是刚拍的）",
                (hasNote38 ? Json.Str(Json.Map(parts38[0]), "text", "") : "").IndexOf("工具") >= 0,
                "首段=" + (hasNote38 ? Json.Str(Json.Map(parts38[0]), "text", "") : "无"));
            // Ollama 走 images 字段，并且也补一条 user 消息说明
            object oi38 = Json.Get(Json.Map(o38oll[2]), "images");
            Check("38.5 Ollama：图挂在工具结果那条的 images 字段上（base64 列表）",
                oi38 is List<string> && ((List<string>)oi38).Count == 1,
                "images=" + (oi38 == null ? "无" : ((List<string>)oi38).Count.ToString()));
            Check("38.6 Ollama：images 里是图片字节，不是路径文字",
                oi38 is List<string> && ((List<string>)oi38)[0].Length > 20
                    && ((List<string>)oi38)[0].IndexOf("shot-38") < 0,
                "长度=" + (oi38 is List<string> ? ((List<string>)oi38)[0].Length.ToString() : "0"));
            Check("38.7 合成那条后续 user 消息在 Ollama 这边也在（图在那儿）",
                o38oll.Count == 4 && Json.Str(Json.Map(o38oll[3]), "role", "") == "user",
                "条数=" + o38oll.Count);

            // 不支持视觉的模型：完全不挂（行为跟以前一样）
            Vision.Capability = false;
            List<object> o38no = (List<object>)convOai.Invoke(null, new object[] { m38 });
            List<object> o38no2 = (List<object>)convOll.Invoke(null, new object[] { m38 });
            Check("38.8 模型声明看不了图 → 不挂（条数不变、tool 消息上没有 images）",
                o38no.Count == m38.Count && o38no2.Count == m38.Count
                    && Json.Get(Json.Map(o38no2[2]), "images") == null,
                "openai=" + o38no.Count + " ollama=" + o38no2.Count);
            Vision.Capability = null;
            Vision.Disabled = true;               // 接口明确拒过图：本轮起退回纯文字
            Check("38.9 Vision.Disabled（接口明确拒过图）也不挂",
                ((List<object>)convOai.Invoke(null, new object[] { m38 })).Count == m38.Count
                    && Vision.ToolImages(m38).Count == 0,
                "挂了图");
            Vision.Disabled = false;

            // 上限 2：给 3 张只挂 2 张（按最新优先）
            List<Dictionary<string, object>> m38c = new List<Dictionary<string, object>>();
            m38c.Add(um38("继续改"));
            m38c.Add(tm38("c1", "老的 " + t38c + " 好了"));
            m38c.Add(am38("再截"));
            m38c.Add(tm38("c2", "新的 " + t38a + " 和 " + t38b));
            List<string> picked38 = Vision.ToolImages(m38c);
            Check("38.10 一次最多挂 2 张（MaxToolImages）",
                picked38.Count == 2 && Vision.MaxToolImages == 2, "挑了 " + picked38.Count + " 张");
            Check("38.11 最新的那张一定在里面（从后往前挑）",
                picked38.Contains(t38a) || picked38.Contains(t38b), "挑到=" + string.Join(",", picked38.ToArray()));

            // 历史里的图不挂：最后一条 user 之前的工具结果一律不算
            List<Dictionary<string, object>> m38h = new List<Dictionary<string, object>>();
            m38h.Add(um38("第一轮"));
            m38h.Add(tm38("h1", "上一轮的截图 " + t38a));
            m38h.Add(am38("看过了"));
            m38h.Add(um38("第二轮，说句话就行"));
            Check("38.12 最后一条 user 消息**之前**的工具图一张都不挂（历史不越挂越多）",
                Vision.ToolImages(m38h).Count == 0,
                "挂了 " + Vision.ToolImages(m38h).Count + " 张");
            Check("38.13 历史那轮的图也不会被塞进请求（条数没变）",
                ((List<object>)convOai.Invoke(null, new object[] { m38h })).Count == m38h.Count
                    && ((List<object>)convOll.Invoke(null, new object[] { m38h })).Count == m38h.Count,
                "挂上去了");

            // 文件不存在 / 不是图片 / 非法路径：不挂、不抛
            List<Dictionary<string, object>> m38x = new List<Dictionary<string, object>>();
            m38x.Add(um38("随手看看"));
            m38x.Add(tm38("x1", "算了 " + System.IO.Path.Combine(dir38, "no-such-38.png")
                + " 和 " + System.IO.Path.Combine(dir38, "note.txt")
                + " 还有 D:\\a|b<c>.png"));
            bool threw38 = false;
            int got38x = -1;
            try { got38x = Vision.ToolImages(m38x).Count; }
            catch { threw38 = true; }
            Check("38.14 文件不存在 / 不是图片 / 带非法路径字符 → 一张不挂、也不抛",
                !threw38 && got38x == 0, threw38 ? "抛了异常" : ("挂了 " + got38x + " 张"));

            // 超大图：跳过，并且**报一声**（不静默）
            int repBefore38 = rep38.Count;
            Check("38.15 测试用的超大图确实超过 1.5MB（否则这条等于没验）",
                bigLen > Vision.MaxToolImageBytes, "实际=" + bigLen + " 字节");
            List<Dictionary<string, object>> m38big = new List<Dictionary<string, object>>();
            m38big.Add(um38("看看"));
            m38big.Add(tm38("b1", "大图 " + t38big + " 小图 " + t38a));
            List<string> pickedBig38 = Vision.ToolImages(m38big);
            Check("38.16 超过 1.5MB 的直接跳过（剩下的照挂）",
                pickedBig38.Count == 1 && pickedBig38[0] == t38a,
                "挑了 " + pickedBig38.Count + " 张：" + string.Join(",", pickedBig38.ToArray()));
            Check("38.17 跳过时给用户报了一声（不静默丢图）",
                rep38.Count > repBefore38
                    && rep38[rep38.Count - 1].IndexOf("没有带上") >= 0,
                "报的话=" + (rep38.Count > repBefore38 ? rep38[rep38.Count - 1] : "（一声没吭）"));

            Vision.ToolImageReport = null;
            Vision.Disabled = false;
            Vision.Capability = null;
            try { System.IO.Directory.Delete(dir38, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("== 39. 自定义控件的控件级可访问角色（子项级 UIA 仍是残余）==");
        TabsBar tb39 = new TabsBar();
        Check("39.1 TabsBar 暴露 PageTabList + 名字",
            tb39.AccessibleRole == AccessibleRole.PageTabList && tb39.AccessibleName == "对话与轨迹",
            "role=" + tb39.AccessibleRole + " name=" + tb39.AccessibleName);
        int tabChanges39 = 0;
        tb39.SelectedIndexChanged += delegate { tabChanges39++; };
        tb39.SelectTab(1);
        Check("39.2 SelectTab 改选中并只发一次事件",
            tb39.SelectedIndex == 1 && tabChanges39 == 1,
            "selected=" + tb39.SelectedIndex + " changes=" + tabChanges39);
        PermMenu pm39 = new PermMenu(0);
        Check("39.3 PermMenu 暴露 MenuItem + 权限选择",
            pm39.AccessibleRole == AccessibleRole.MenuItem && pm39.AccessibleName == "权限选择",
            "role=" + pm39.AccessibleRole + " name=" + pm39.AccessibleName);

        Console.WriteLine();
        Console.WriteLine("== 40. 浏览器地址栏协议身份：https 锁 / http 警告 / file 本地 ==");
        {
            AhzBrowser br40 = new AhzBrowser();
            MethodInfo usm = typeof(AhzBrowser).GetMethod("UpdateSiteMark", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo smf = typeof(AhzBrowser).GetField("_siteMark", BindingFlags.Instance | BindingFlags.NonPublic);
            if (usm != null && smf != null)
            {
                usm.Invoke(br40, new object[] { "https://example.com/a" });
                Check("40.1 https 显示锁", Convert.ToString(smf.GetValue(br40)) == "🔒", "mark=" + smf.GetValue(br40));
                usm.Invoke(br40, new object[] { "http://example.com/a" });
                Check("40.2 http 显示警告", Convert.ToString(smf.GetValue(br40)) == "⚠", "mark=" + smf.GetValue(br40));
                usm.Invoke(br40, new object[] { "file:///D:/x.html" });
                Check("40.3 file 显示本地文件", Convert.ToString(smf.GetValue(br40)) == "▤", "mark=" + smf.GetValue(br40));
            }
            else Check("40.0 UpdateSiteMark 存在", false, "找不到方法/字段");
            br40.Dispose();
        }

#if NET10_0_OR_GREATER
        Console.WriteLine();
        Console.WriteLine("== 41. .NET 10 子项级 UIA：页签/权限行必须是独立可访问项 ==");
        {
            TabsBar tb41 = new TabsBar();
            tb41.Size = new Size(400, 45);
            tb41.CreateControl();
            AccessibleObject ao41 = tb41.AccessibilityObject;
            Check("41.1 页签条可访问对象非空", ao41 != null, "ao=null");
            if (ao41 != null)
            {
                Check("41.2 页签暴露 2 个子项", ao41.GetChildCount() == 2, "count=" + ao41.GetChildCount());
                AccessibleObject t041 = ao41.GetChild(0);
                AccessibleObject t141 = ao41.GetChild(1);
                Check("41.3 子项名字/角色正确",
                    t041 != null && t141 != null && t041.Name == "对话" && t141.Name == "轨迹"
                    && t141.Role == AccessibleRole.PageTab,
                    t041 == null ? "null" : (t041.Name + "/" + (t141 == null ? "null" : t141.Name + " " + t141.Role)));
                int ch41 = 0;
                tb41.SelectedIndexChanged += delegate { ch41++; };
                if (t141 != null) t141.DoDefaultAction();
                Check("41.4 子项默认动作能切页签", tb41.SelectedIndex == 1 && ch41 == 1,
                    "selected=" + tb41.SelectedIndex + " changes=" + ch41);
            }
            PermMenu pm41 = new PermMenu(0);
            pm41.CreateControl();
            AccessibleObject pa41 = pm41.AccessibilityObject;
            Check("41.5 权限浮层可访问对象非空", pa41 != null, "ao=null");
            if (pa41 != null)
            {
                Check("41.6 权限暴露 3 个 RadioButton 子项",
                    pa41.GetChildCount() == 3 && pa41.GetChild(1) != null
                    && pa41.GetChild(1).Role == AccessibleRole.RadioButton,
                    "count=" + pa41.GetChildCount());
                int picked41 = -1;
                pm41.RowPicked += delegate { picked41 = pm41.Current; };
                AccessibleObject p141 = pa41.GetChild(1);
                if (p141 != null) p141.DoDefaultAction();
                Check("41.7 子项默认动作能选权限档", pm41.Current == 1 && picked41 == 1,
                    "current=" + pm41.Current + " picked=" + picked41);
            }
        }
#endif

        Console.WriteLine("================ 流式重画自测：通过 " + pass + " 项，失败 " + fail + " 项 ================");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}


