using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 开发用：不依赖屏幕采集，直接把主窗口画到 PNG 上（桌面被锁/远程断线时也能看图）
// 用法: shot2.exe <out.png>
class Shot2
{
    static object Get(object t, string f)
    {
        return t.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(t);
    }

    static void Pump(int ms)
    {
        DateTime end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string outPath = args.Length > 0 ? args[0] : "ui.png";
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        MainForm f = new MainForm(false);
        f.ShowInTaskbar = false;
        f.Show();
        Pump(900);
        f.WindowState = FormWindowState.Maximized;
        Pump(900);
        f.GetType().GetMethod("ShowHero", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(f, new object[] { false });
        TranscriptView rtb = (TranscriptView)Get(f, "rtb");
        rtb.ClearAll();
        Pump(200);

        f.Write(OutKind.User, "› 看一下 devtools 里那个大任务测试是怎么跑的，顺便告诉我怎么加一个用例\n");
        Pump(200);
        f.Write(OutKind.ToolCall, "  ● read_file(devtools/bigbench/run-big.ps1)\n");
        f.Write(OutKind.ToolResult, "  └ ✓ 已读取 5781 字符，7 个用例\n");
        f.Write(OutKind.ToolCall, "  ● list_dir(devtools/bigbench/template)\n");
        f.Write(OutKind.ToolResult, "  └ ✓ 列了 12 项\n");
        f.Write(OutKind.Assistant, "## 结论\n"
            + "框架已经追平，差距在模型：**云端 91.5 分，本地 60.5 分**。\n\n"
            + "## 两轮实测\n"
            + "| 题 | 云端 | 本地 | 用时 |\n"
            + "| --- | --- | --- | --- |\n"
            + "| S1 单文件修 bug | PASS | PASS | 8s / 344s |\n"
            + "| S4 大任务 | PASS | FAIL | 23s / 超时 |\n\n"
            + "## 本地模型的两个瓶颈\n"
            + "- **吞吐**：吐字 5–7 tok/s（云端 80–230）\n"
            + "- 行为：读完文件不动手，`edit_file` 一次都没调\n\n"
            + "## 下一步\n"
            + "1. 加「读了不动手」的催促\n"
            + "2. 本地默认关思考\n\n"
            + "```powershell\n"
            + "pwsh -File devtools\\scorecard\\run-scorecard.ps1 -Provider ollama-local -Model qwen3.5:4b -Think 0\n"
            + "```\n");
        f.Write(OutKind.Stats, "  ── 4 步 · 2 次工具 · 512 tok · 22.6 tok/s\n");
        Pump(700);

        // 「已编辑 N 个文件」卡片（带真实 diff 统计）
        try
        {
            Changes.Root = AppDomain.CurrentDomain.BaseDirectory;
            Changes.Now.Clear();
            FileChange c1 = new FileChange();
            c1.Path = "a"; c1.Rel = "src/AhzScribe.cs";
            c1.OldText = "line1\nline2\nline3\n"; c1.NewText = "line1\nLINE2\nline3\nline4\n";
            Changes.Now.Add(c1);
            FileChange c2 = new FileChange();
            c2.Path = "b"; c2.Rel = "devtools/bench/flicker-unittest.cs";
            c2.OldText = "x\ny\n"; c2.NewText = "x\nY\n";
            Changes.Now.Add(c2);
            int snap = Changes.Snapshot();
            f.Write(OutKind.Changes, "已编辑 2 个文件\nsrc/AhzScribe.cs\ndevtools/bench/flicker-unittest.cs\n#card#" + snap);
            Pump(600);
        }
        catch (Exception ex) { Console.WriteLine("卡片演示失败: " + ex.Message); }

        // 「查看变更」窗口（不弹模态，直接渲染出来看）
        try
        {
            List<FileChange> lst = Changes.Get(Changes.Snapshots.Count - 1);
            using (ChangesForm cf = new ChangesForm(lst, 0))
            {
                cf.Show();
                Pump(400);
                object dbox = cf.GetType().GetField("_diff", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cf);
                Console.WriteLine("  diff 框内容长度 = " + ((RichTextBox)dbox).TextLength
                    + "  可见=" + ((RichTextBox)dbox).Visible + "  位置=" + ((RichTextBox)dbox).Bounds);
                using (Bitmap b = new Bitmap(cf.Width, cf.Height))
                {
                    cf.DrawToBitmap(b, new Rectangle(0, 0, cf.Width, cf.Height));
                    b.Save(outPath.Replace(".png", "-diff.png"), ImageFormat.Png);
                }
                Console.WriteLine("已保存 diff 窗口图");
                cf.Close();
            }
        }
        catch (Exception ex) { Console.WriteLine("diff 窗口渲染失败: " + ex.Message); }

        // 选中 + 浮出的小条（复制 / 填到输入框）
        try
        {
            System.Collections.IEnumerable bl = (System.Collections.IEnumerable)
                rtb.GetType().GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rtb);
            List<MsgBlock> allb = new List<MsgBlock>();
            foreach (object o in bl) allb.Add((MsgBlock)o);
            MsgBlock target2 = null;
            foreach (MsgBlock m in allb) if (m.ShowBranch && m.CardRows.Count == 0) target2 = m;
            if (target2 != null)
            {
                target2.SetSel(0, Math.Min(30, target2.Body.Length));
                rtb.GetType().GetMethod("UpdateBar", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(rtb, null);
                Pump(300);
                SelBar sb = (SelBar)rtb.GetType().GetProperty("Bar", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(rtb, null);
                Console.WriteLine("  小条状态: visible=" + sb.Visible + " bounds=" + sb.Bounds
                    + " parent=" + (sb.Parent == null ? "null" : sb.Parent.GetType().Name)
                    + " 面板 ClientSize=" + rtb.ClientSize);
                Console.WriteLine("  小条 z-order 索引=" + rtb.Controls.GetChildIndex(sb) + "（0=最上层）  兄弟控件数=" + rtb.Controls.Count);
                using (Bitmap bb3 = new Bitmap(sb.Width, sb.Height))
                {
                    sb.DrawToBitmap(bb3, new Rectangle(0, 0, sb.Width, sb.Height));
                    bb3.Save(outPath.Replace(".png", "-baronly.png"), ImageFormat.Png);
                    Console.WriteLine("  小条单独图已保存，OnPaint 次数=" + sb.Paints);
                }
                using (Bitmap bb4 = new Bitmap(rtb.Width, rtb.Height))
                {
                    rtb.DrawToBitmap(bb4, new Rectangle(0, 0, rtb.Width, rtb.Height));
                    bb4.Save(outPath.Replace(".png", "-panel.png"), ImageFormat.Png);
                    int dark = 0;
                    for (int yy = Math.Max(0, sb.Top); yy < Math.Min(bb4.Height, sb.Bottom); yy++)
                        for (int xx = Math.Max(0, sb.Left); xx < Math.Min(bb4.Width, sb.Right); xx++)
                        {
                            Color c = bb4.GetPixel(xx, yy);
                            if (c.R < 60 && c.G < 62 && c.B < 70) dark++;
                        }
                    Console.WriteLine("  面板级截图里，小条区域内的深色像素数=" + dark + "（小条面积约 " + (sb.Width * sb.Height) + "）");
                }
                using (Bitmap bb2 = new Bitmap(sb.Width, sb.Height))
                {
                    sb.DrawToBitmap(bb2, new Rectangle(0, 0, sb.Width, sb.Height));
                    Color pc = bb2.GetPixel(sb.Width / 2, sb.Height / 2);
                    Console.WriteLine("  单独渲染小条，中心像素=" + pc.R + "," + pc.G + "," + pc.B);
                }
                using (Bitmap b = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                    b.Save(outPath.Replace(".png", "-selbar.png"), ImageFormat.Png);
                }
                Console.WriteLine("已保存 选中小条图");
            }
        }
        catch (Exception ex) { Console.WriteLine("小条演示失败: " + ex.Message); }

        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(outPath, ImageFormat.Png);
        }

        // 另外导一张：悬停气泡 / 已复制气泡 / 选中高亮 / 右键菜单
        System.Collections.IEnumerable blkList = (System.Collections.IEnumerable)
            rtb.GetType().GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rtb);
        List<MsgBlock> all = new List<MsgBlock>();
        foreach (object o in blkList) all.Add((MsgBlock)o);
        if (all.Count > 0)
        {
            MsgBlock target = null;
            foreach (MsgBlock m in all) if (m.ShowBranch) target = m;      // 助手回复那条
            if (target == null) target = all[all.Count - 1];
            target.SetSel(6, 26);                                          // 选中一段
            target.Tip = "复制回复"; target.TipAct = 0;                    // 悬停气泡
            target.Invalidate();
            Pump(300);
            string p2 = outPath.Replace(".png", "-ui.png");
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(p2, ImageFormat.Png);
            }
            Console.WriteLine("已保存 " + p2);
            ContextMenuStrip cm = rtb.ContextMenuStrip;
            if (cm != null)
            {
                cm.Show(f, new Point(f.Width / 3, f.Height / 3));
                Pump(400);
                using (Bitmap mb = new Bitmap(cm.Width, cm.Height))
                {
                    cm.DrawToBitmap(mb, new Rectangle(0, 0, cm.Width, cm.Height));
                    mb.Save(outPath.Replace(".png", "-menu.png"), ImageFormat.Png);
                }
                cm.Close();
                Console.WriteLine("已保存菜单图");
            }
        }
        Console.WriteLine("saved " + outPath + "  " + f.Width + "x" + f.Height
            + "  块数=" + rtb.BlockCount + "  hero=" + ((Control)Get(f, "heroPanel")).Visible);
        Console.WriteLine("  面板 ClientSize=" + rtb.ClientSize + "  AutoScrollMinSize=" + rtb.AutoScrollMinSize);
        System.Collections.IEnumerable bs = (System.Collections.IEnumerable)
            rtb.GetType().GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rtb);
        foreach (object b in bs)
        {
            int maxRight = 0;
            System.Collections.IEnumerable ps = (System.Collections.IEnumerable)
                b.GetType().GetField("_pieces", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(b);
            foreach (object p in ps)
            {
                Rectangle r = (Rectangle)p.GetType().GetField("Rect").GetValue(p);
                if (r.Right > maxRight) maxRight = r.Right;
            }
            Console.WriteLine("    块 " + b.GetType().GetField("Kind").GetValue(b) + "  宽=" + ((Control)b).Width
                + "  高=" + ((Control)b).Height + "  文字=" + ((MsgBlock)b).Body.Length + "  最右端=" + maxRight);
        }
        f.Close();
        IsolateBar(outPath);
    }

    // 隔离验证：单独把 SelBar 放进一个窗体，渲染出来看它到底画没画
    static void IsolateBar(string outPath)
    {
        Form host = new Form();
        host.ClientSize = new Size(400, 120);
        host.BackColor = Theme.Bg;
        SelBar bar = new SelBar();
        bar.SetBounds(20, 30, SelBar.PreferredW(), Theme.Px(32));
        bar.Visible = true;
        host.Controls.Add(bar);
        host.Show();
        Pump(400);
        bar.Invalidate();
        Pump(200);
        using (Bitmap b = new Bitmap(host.Width, host.Height))
        {
            host.DrawToBitmap(b, new Rectangle(0, 0, host.Width, host.Height));
            Color c1 = b.GetPixel(bar.Left + 8, bar.Top + 8);
            Color c2 = b.GetPixel(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2);
            Console.WriteLine("  隔离渲染: 左上像素=" + c1.R + "," + c1.G + "," + c1.B
                + "  中心像素=" + c2.R + "," + c2.G + "," + c2.B);
            Console.WriteLine("  小条 OnPaint 次数=" + bar.Paints + "  句柄已建=" + bar.IsHandleCreated);
            b.Save(outPath.Replace(".png", "-isolated.png"), ImageFormat.Png);
        }
        host.Close();
    }
}
