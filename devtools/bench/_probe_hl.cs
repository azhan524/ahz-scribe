using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 探针：1) 校验每个排版片段的 SrcAt 是否真指向原文（选中换算全靠它）
//       2) 逐行量化"选中高亮(蓝)有没有把所有字形墨迹盖住"
class ProbeHL
{
    static object Get(object t, string f)
    {
        return t.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(t);
    }

    static void Pump(int ms)
    {
        DateTime end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(5); }
    }

    [STAThread]
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();

        MainForm f = new MainForm(false);
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.Show();
        Pump(900);
        TranscriptView rtb = (TranscriptView)Get(f, "rtb");
        f.GetType().GetMethod("ShowHero", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(f, new object[] { false });
        rtb.ClearAll();
        Pump(200);

        // 真实场景：一长段（行内粗体在中间）+ 一条列表（行首粗体），都会折行
        string para = "云端模型 91.5 分，本地 4b 模型 60.5 分，并且**精准定位了本地的死因**——不是能力不足，是「读完文件不动手」的行为缺陷 + 6.4 tok/s 的吞吐瓶颈。这数据直接证伪了我早先的判断：本地模型本来不至于差到那种程度。";
        string li = "3. **最有意思的是它顺手改了我的提示词文件**：把我的版本（4572 字节 → 6924 字节）重写成了一份更严谨的交接文档——加上了「已知边界（实测数据）」章节、每条 P0 配了「验收标准」、提醒后人「具体行数以 wc 为准，别信文档」。";
        f.Write(OutKind.Assistant, "\n" + para + "\n\n" + li + "\n");
        Pump(600);

        List<MsgBlock> bs = (List<MsgBlock>)Get(rtb, "_blocks");
        MsgBlock b = bs[bs.Count - 1];
        string raw = b.Body.Replace("\r\n", "\n");

        FieldInfo fPieces = typeof(MsgBlock).GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic);
        IList pieces = (IList)fPieces.GetValue(b);
        Type pt = null;
        Console.WriteLine("原文行数=" + raw.Split('\n').Length + "  片段数=" + pieces.Count);

        // ---------- 1) SrcAt 不变量 ----------
        int checkedN = 0, badN = 0;
        string firstBad = null;
        for (int i = 0; i < pieces.Count; i++)
        {
            object p = pieces[i];
            if (pt == null) pt = p.GetType();
            int style = (int)pt.GetField("Style").GetValue(p);
            string tx = (string)pt.GetField("Text").GetValue(p);
            int sa = (int)pt.GetField("SrcAt").GetValue(p);
            int sl = (int)pt.GetField("SrcLen").GetValue(p);
            if (style == 6) continue;                 // 表格分隔线，没字
            if (sl == 0) continue;                    // 列表符号，不在原文里占字符
            if (tx.Length == 0) continue;
            checkedN++;
            string want = (sa >= 0 && sa + tx.Length <= raw.Length) ? raw.Substring(sa, tx.Length) : "<越界>";
            if (want != tx)
            {
                badN++;
                if (firstBad == null)
                    firstBad = "  片段[" + i + "] 原文偏移=" + sa + " 期望读到「" + tx + "」实际读到「" + want + "」";
            }
        }
        Console.WriteLine("SrcAt 不变量: 检查 " + checkedN + " 个片段, 错的 " + badN + " 个");
        if (firstBad != null) Console.WriteLine(firstBad);

        // ---------- 2) 全选后逐行看覆盖 ----------
        b.SetSel(0, raw.Length);
        Pump(200);
        List<Rectangle> hl = b.HighlightRects();
        Console.WriteLine("全选高亮块数=" + hl.Count);
        foreach (Rectangle r in hl)
            Console.WriteLine("  高亮块 x=" + r.Left + ".." + r.Right + " y=" + r.Top + ".." + r.Bottom);

        SortedDictionary<int, int[]> rows = new SortedDictionary<int, int[]>();   // y -> {minLeft, maxRight}
        for (int i = 0; i < pieces.Count; i++)
        {
            object p = pieces[i];
            Rectangle r = (Rectangle)pt.GetField("Rect").GetValue(p);
            if ((int)pt.GetField("Style").GetValue(p) == 6) continue;
            if (r.Width <= 0) continue;
            if (!rows.ContainsKey(r.Y)) rows[r.Y] = new int[] { r.Left, r.Right };
            else { int[] a = rows[r.Y]; if (r.Left < a[0]) a[0] = r.Left; if (r.Right > a[1]) a[1] = r.Right; }
        }

        using (Bitmap bmp = new Bitmap(b.Width, b.Height))
        {
            b.DrawToBitmap(bmp, new Rectangle(0, 0, b.Width, b.Height));
            bmp.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_hl.png"), System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("逐行覆盖（墨迹列里没有蓝的 = 高亮没盖住）：");
            int badRows = 0, badRowCount = 0;
            foreach (KeyValuePair<int, int[]> kv in rows)
            {
                badRowCount++;
                int y0 = kv.Key, y1 = Math.Min(bmp.Height - 1, kv.Key + Theme.Px(26));
                bool[] ink = new bool[bmp.Width];
                bool[] blue = new bool[bmp.Width];
                for (int y = Math.Max(0, y0); y <= y1; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        // 墨迹：任何"既不白也不蓝"的像素（含抗锯齿边缘）；蓝：选中底色
                        bool isBlue = c.B - c.R > 25 && c.B > 150;
                        if (!isBlue && (c.R + c.G + c.B) < 640) ink[x] = true;
                        if (c.B - c.R > 25 && c.B > 150) blue[x] = true;
                    }
                int inkN = 0, miss = 0, missLo = int.MaxValue, missHi = -1;
                for (int x = 0; x < bmp.Width; x++)
                {
                    if (!ink[x]) continue;
                    inkN++;
                    if (!blue[x]) { miss++; if (x < missLo) missLo = x; if (x > missHi) missHi = x; }
                }
                string tag = miss == 0 ? "  OK" : "  漏!";
                if (miss > 0) badRows++;
                Console.WriteLine("  行 y=" + y0 + " 文字宽=" + (kv.Value[1] - kv.Value[0]) +
                    "  墨迹列=" + inkN + "  没盖住=" + miss + (miss > 0 ? " 漏在 x=" + missLo + ".." + missHi : "") + tag);
            }
            Console.WriteLine("有漏的行数 = " + badRows + " / " + badRowCount);
        }

        // ---------- 3) 局部跨行选择：高亮边界必须落在字与字之间（不能切在字的一半） ----------
        int bi = raw.IndexOf("精准定位");
        b.SetSel(bi + 2, bi + 30);
        Pump(150);
        List<Rectangle> h2 = b.HighlightRects();
        Console.WriteLine();
        Console.WriteLine("局部选择（原文偏移 " + (bi + 2) + ".." + (bi + 30) + "），高亮块=" + h2.Count
            + "  选中文字「" + b.SelectedText + "」");
        using (Bitmap bmp = new Bitmap(b.Width, b.Height))
        {
            b.DrawToBitmap(bmp, new Rectangle(0, 0, b.Width, b.Height));
            bmp.Save(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_hl_part.png"), System.Drawing.Imaging.ImageFormat.Png);
            foreach (Rectangle r in h2)
            {
                int y0 = Math.Max(0, r.Top - 8), y1 = Math.Min(bmp.Height - 1, r.Bottom + 8);
                int gapL = -1, gapR = -1;
                for (int x = r.Left - 1; x >= 0; x--)
                {
                    bool ink = false;
                    for (int y = y0; y <= y1; y++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        if (!(c.B - c.R > 25 && c.B > 150) && (c.R + c.G + c.B) < 640) { ink = true; break; }
                    }
                    if (ink) { gapL = r.Left - 1 - x; break; }
                }
                for (int x = r.Right; x < bmp.Width; x++)
                {
                    bool ink = false;
                    for (int y = y0; y <= y1; y++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        if (!(c.B - c.R > 25 && c.B > 150) && (c.R + c.G + c.B) < 640) { ink = true; break; }
                    }
                    if (ink) { gapR = x - r.Right; break; }
                }
                Console.WriteLine("  高亮 x=" + r.Left + ".." + r.Right + " y=" + r.Top
                    + "  左边界到最近墨迹=" + gapL + "px  右边界到最近墨迹=" + gapR + "px"
                    + "  （只看数字：中文字形常常紧贴，0~2px 都算正常；看 _hl_part.png 最直观）"));
            }
        }
        // ---------- 4) 垂直方向：高亮要完整包住字形（含 g j p q y 这种下伸部） ----------
        rtb.ClearAll();
        Pump(150);
        f.Write(OutKind.Assistant, "\nHgjpqy 有下伸部，**粗体里也有 gjpqy**，还有 `code = gjpqy;` 行内代码。\n");
        Pump(500);
        List<MsgBlock> bs4 = (List<MsgBlock>)Get(rtb, "_blocks");
        MsgBlock b4 = bs4[bs4.Count - 1];
        b4.SetSel(0, b4.Body.Length);
        Pump(150);
        List<Rectangle> h4 = b4.HighlightRects();
        Console.WriteLine();
        Console.WriteLine("下伸部检查：整行选中后，高亮下边缘之下的墨迹像素数");
        using (Bitmap bmp = new Bitmap(b4.Width, b4.Height))
        {
            b4.DrawToBitmap(bmp, new Rectangle(0, 0, b4.Width, b4.Height));
            foreach (Rectangle r in h4)
            {
                int below = 0;
                for (int y = r.Bottom + 1; y <= Math.Min(bmp.Height - 1, r.Bottom + 8); y++)
                    for (int x = r.Left; x < r.Right; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        if (!(c.B - c.R > 25 && c.B > 150) && (c.R + c.G + c.B) < 640) below++;
                    }
                Console.WriteLine("  高亮 y=" + r.Top + ".." + r.Bottom + " (x=" + r.Left + ".." + r.Right
                    + ")  下方 8px 内的墨迹像素=" + below + (below == 0 ? "  OK" : "  漏!"));
            }
        }
        // ---------- 5) 诊断：标题行的"排版字体"和"绘制字体"是否一致 ----------
        rtb.ClearAll();
        Pump(150);
        f.Write(OutKind.Assistant, "\n## 大标题一行\n正文一行，普通字号，用来当参照。\n");
        Pump(400);
        List<MsgBlock> bs5 = (List<MsgBlock>)Get(rtb, "_blocks");
        MsgBlock b5 = bs5[bs5.Count - 1];
        Console.WriteLine();
        Console.WriteLine("标题/正文的行高与字形墨迹高度：");
        using (Bitmap bmp = new Bitmap(b5.Width, b5.Height))
        {
            b5.DrawToBitmap(bmp, new Rectangle(0, 0, b5.Width, b5.Height));
            IList ps5 = (IList)typeof(MsgBlock).GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b5);
            Type t5 = null;
            Dictionary<int, string> rowTxt = new Dictionary<int, string>();
            Dictionary<int, int> rowH = new Dictionary<int, int>();
            foreach (object p in ps5)
            {
                if (t5 == null) t5 = p.GetType();
                int st = (int)t5.GetField("Style").GetValue(p);
                if (st == 6) continue;
                Rectangle r = (Rectangle)t5.GetField("Rect").GetValue(p);
                string tx = (string)t5.GetField("Text").GetValue(p);
                if (!rowTxt.ContainsKey(r.Y)) { rowTxt[r.Y] = tx; rowH[r.Y] = r.Height; }
                else rowTxt[r.Y] = rowTxt[r.Y] + tx;
            }
            List<int> ys = new List<int>(rowTxt.Keys);
            ys.Sort();
            foreach (int y in ys)
            {
                int inkTop = -1, inkBot = -1;
                for (int yy = Math.Max(0, y - 4); yy <= Math.Min(bmp.Height - 1, y + 40); yy++)
                {
                    bool ink = false;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        Color c = bmp.GetPixel(x, yy);
                        if (c.R < 130 && c.G < 130 && c.B < 140) { ink = true; break; }
                    }
                    if (ink) { if (inkTop < 0) inkTop = yy; inkBot = yy; }
                }
                string head = rowTxt[y].Length > 14 ? rowTxt[y].Substring(0, 14) : rowTxt[y];
                Console.WriteLine("  行 y=" + y + " 片段高=" + rowH[y] + "px  墨迹 y=" + inkTop + ".." + inkBot
                    + " (高 " + (inkBot - inkTop + 1) + "px)  文字「" + head + "…」");
            }
            Console.WriteLine("  参照：正文体 f.Height=" + Theme.F(10.5f).Height + "  粗体 f.Height="
                + Theme.FB(10.5f).Height + "  一级标题(1.5倍粗体) f.Height="
                + Theme.Cache(Theme.FontName, 10.5f * 1.5f, FontStyle.Bold).Height);
        }
        f.Close();
    }
}
