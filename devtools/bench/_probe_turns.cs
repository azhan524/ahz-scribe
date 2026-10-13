using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 探针：量"一轮任务"在对话区里长什么样（块间距 / 轮次分界），并出一张面板截图
//   用法: probeturns.exe [输出png]
class ProbeTurns
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
    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string outPath = args.Length > 0 ? args[0] : "_turns.png";
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();

        MainForm f = new MainForm(false);
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.Show();
        Pump(900);
        f.WindowState = FormWindowState.Maximized;
        Pump(600);
        f.GetType().GetMethod("ShowHero", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(f, new object[] { false });
        TranscriptView rtb = (TranscriptView)Get(f, "rtb");
        rtb.ClearAll();
        Pump(200);

        // 第一轮：和你截图里那轮一样
        f.Write(OutKind.User, "你好");
        f.Write(OutKind.Info, "· 已加载项目规矩 AGENTS.md\n");
        f.Write(OutKind.Info, "· 自动验证命令：cmd /c build.cmd（规矩文件里写的）\n");
        f.Write(OutKind.Assistant, "你好！我是 Ahz Scribe，可以帮你查看/修改这个项目的代码、跑编译和测试。有什么需要做的，直接说就行。");
        f.Write(OutKind.Stats, "· 检测到收工时 0 次编辑，拦截一次逼它动手（1/2）\n");
        f.Write(OutKind.Assistant, "我这边有个情况要先说清楚：这轮对话里我并没有收到任何具体的修改任务，所以我先查一下项目当前状态，然后再决定动不动手。");
        Pump(300);
        // 第二轮：提问 + 工具调用 + 回答
        f.Write(OutKind.User, "帮我看下 devtools 里那个大任务测试是怎么跑的");
        f.Write(OutKind.ToolCall, "  ● read_file(devtools/bigbench/run-big.ps1)\n");
        f.Write(OutKind.ToolResult, "  └ ✓ 已读取 5781 字符，7 个用例\n");
        f.Write(OutKind.Assistant, "跑法是先选 provider 再选模型，最后把用例名传进去。我建议先跑 S1 看基线。");
        Pump(600);

        List<MsgBlock> bs = (List<MsgBlock>)Get(rtb, "_blocks");
        Console.WriteLine("块数=" + bs.Count);
        int prevBottom = -1;
        for (int i = 0; i < bs.Count; i++)
        {
            MsgBlock b = bs[i];
            int gap = prevBottom < 0 ? -1 : b.Top - prevBottom;
            string head = b.Body.Replace("\n", " ").Replace("\r", "");
            if (head.Length > 16) head = head.Substring(0, 16);
            Console.WriteLine(string.Format("  [{0}] {1,-11} top={2,4} 高={3,3} 与上一块的间距={4,3}px  「{5}」",
                i, b.Kind.ToString(), b.Top, b.Height, gap, head));
            prevBottom = b.Top + b.Height;
        }
        Console.WriteLine("面板高=" + rtb.ClientSize.Height + "  内容高=" + rtb.AutoScrollMinSize.Height
            + "  正文宽=" + (rtb.ClientSize.Width - 30));

        using (Bitmap bmp = new Bitmap(rtb.ClientSize.Width, Math.Min(rtb.ClientSize.Height, 900)))
        {
            rtb.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("截图已保存: " + outPath);
        }
        f.Close();
    }
}
