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

            rtb.SetFonts(Theme.F(10.5f), Theme.FB(10.5f), Theme.M(9.5f), Theme.F(9f));   // 同字体 → 触发全量重排
            List<MsgBlock> two = Blocks(rtb);
            int hFull = two[two.Count - 1].Height;
            if (Math.Abs(hInc - hFull) <= lineH * 2) okLines++;
            else Console.WriteLine("    样本 " + si + " 行数差太多：增量高=" + hInc + " 全量高=" + hFull);
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
            TextBox inp = (TextBox)Get(f, "txtInput");
            inp.Clear();
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
        Check("同轮内部（系统提示/工具调用/正文）仍保持紧凑", inTurnGap == Theme.Px(6),
            "同轮=" + inTurnGap + "px 期望=" + Theme.Px(6) + "px");

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
                Check("chip 文字跟着换成「新厂商 / 新模型 · 档位」",
                    chip28.Text.IndexOf(" / ") > 0 && chip28.Text.IndexOf(" · ") > 0,
                    "chip=「" + chip28.Text + "」");
            }

            // ---- 第三页：滑块改档位 → 配置 + chip + 真的进请求参数 ----
            EffortSlider emb2 = pop28.EffortPage;
            emb2.SetValue(2, true);                     // 高
            Pump(200);
            Check("拖到「高」→ config.effort = high", cfg28.effort == "high", "effort=" + cfg28.effort);
            Check("chip 上的档位跟着变「高」", chip28.Text.IndexOf("· 高") > 0, "chip=「" + chip28.Text + "」");
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
            Console.WriteLine("== 28.5 权限浮层：仅可查看 / 工作区内修改 / 完全权限 ==");
            Check("权限就是 Codex 那三档", Perm.Values.Length == 3
                && Perm.Label("readonly") == "仅可查看"
                && Perm.Label("workspace") == "工作区内修改"
                && Perm.Label("full") == "完全权限",
                string.Join("/", Perm.Labels));
            Check("只有「完全权限」才不再逐条问（老 auto_yes 的等价档）",
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
                // 点「完全权限」那一行
                ppop.MenuCtl.Current = 2;
                FieldInfo pf = ppop.MenuCtl.GetType().GetField("RowPicked", BindingFlags.Instance | BindingFlags.NonPublic);
                Delegate pd = (Delegate)pf.GetValue(ppop.MenuCtl);
                pd.DynamicInvoke(new object[] { ppop.MenuCtl, EventArgs.Empty });
                Pump(300);
                Check("点一行 → 权限落盘（permission=full、auto_yes 跟着同步）",
                    cfg28.permission == "full" && cfg28.auto_yes,
                    "permission=" + cfg28.permission + " auto_yes=" + cfg28.auto_yes);
                Check("选完浮层关掉、chip 文字跟着变",
                    Get(f, "permPop") == null && pchip.Text == "完全权限",
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

        f.Close();
        Console.WriteLine();
        Console.WriteLine("================ 流式重画自测：通过 " + pass + " 项，失败 " + fail + " 项 ================");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
