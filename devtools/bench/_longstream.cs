using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 开发用：对比"空对话区"和"很长的对话区"下，逐 token 流式追加的耗时（找卡顿来源）
class LongStream
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

    static double Feed(MainForm f, int tokens, int delayMs)
    {
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < tokens; i++)
        {
            f.Write(OutKind.Assistant, "字");
            Pump(delayMs);
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    [STAThread]
    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        MainForm f = new MainForm(false);
        f.ShowInTaskbar = false;
        f.Show();
        f.WindowState = FormWindowState.Maximized;
        Pump(900);
        RichTextBox rtb = (RichTextBox)Get(f, "rtb");

        Console.WriteLine("对话区初始长度: " + rtb.TextLength);
        double t1 = Feed(f, 60, 20);
        Console.WriteLine("空对话区：60 个 token（每 20ms 一个）用了 " + t1.ToString("0") + " ms（理论 1200ms）");

        // 灌一个很长的对话区（模拟长会话）
        StringBuilder big = new StringBuilder();
        for (int i = 0; i < 4000; i++) big.Append("第 " + i + " 行：这是一段比较长的历史内容，用来把对话区撑大，测试重排开销。\n");
        rtb.Clear();
        rtb.AppendText(big.ToString());
        Pump(300);
        Console.WriteLine("灌长文本后长度: " + rtb.TextLength);
        double t2 = Feed(f, 60, 20);
        Console.WriteLine("长对话区：60 个 token（每 20ms 一个）用了 " + t2.ToString("0") + " ms（理论 1200ms）");
        Console.WriteLine(t2 > 2200 ? "→ 长文本下明显变慢（有卡顿）" : "→ 长文本下没有明显卡顿");

        f.Close();
    }
}
