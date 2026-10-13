using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 开发用：量一下"流式写入 + 排版"的成本（长回答时最容易变慢的地方）
class Perf
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
        Console.OutputEncoding = Encoding.UTF8;
        Application.EnableVisualStyles();
        Form host = new Form();
        host.ClientSize = new Size(1200, 900);
        TranscriptView v = new TranscriptView();
        host.Controls.Add(v);
        v.SetBounds(0, 0, 1200, 900);
        host.Show();
        Application.DoEvents();
        Theme.EnsureDpi();
        v.SetFonts(Theme.F(10.5f), Theme.FB(10.5f), Theme.M(9.5f), Theme.F(9f));

        string unit = "这是一段用来测试排版成本的中文回答内容，包含 **加粗** 和 `行内代码` 以及 EnglishWords";

        // 场景 A：一条长回答，按批写入（每批约 40 字，等价于 120ms 攒一批）
        int batches = 300, per = 40;
        double sum = 0, max = 0;
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < batches; i++)
        {
            string chunk = unit.Substring(0, Math.Min(per, unit.Length));
            Stopwatch one = Stopwatch.StartNew();
            v.AddChunk(OutKind.Assistant, chunk + (i % 6 == 5 ? "\n" : ""));
            v.FlushNow();
            one.Stop();
            double ms = one.Elapsed.TotalMilliseconds;
            sum += ms; if (ms > max) max = ms;
        }
        sw.Stop();
        int len = v.AllText().Length;
        Console.WriteLine("场景A 一条长回答（" + len + " 字 / " + batches + " 批）：总 " + sw.ElapsedMilliseconds
            + "ms  每批平均 " + (sum / batches).ToString("0.00") + "ms  最慢 " + max.ToString("0.00") + "ms");

        // 场景 B：很多条消息 + 每次追加都要整体 Relayout
        v.ClearAll();
        Pump(200);
        for (int i = 0; i < 60; i++) { v.AddChunk(OutKind.Assistant, "第 " + i + " 条消息：" + unit); v.Break(); }
        v.FlushNow();
        Pump(200);
        double sum2 = 0, max2 = 0;
        for (int i = 0; i < 120; i++)
        {
            Stopwatch one = Stopwatch.StartNew();
            v.AddChunk(OutKind.Assistant, unit);
            v.FlushNow();
            one.Stop();
            sum2 += one.Elapsed.TotalMilliseconds; if (one.Elapsed.TotalMilliseconds > max2) max2 = one.Elapsed.TotalMilliseconds;
        }
        Console.WriteLine("场景B 60 条历史消息 + 持续流式：每批平均 " + (sum2 / 120).ToString("0.00")
            + "ms  最慢 " + max2.ToString("0.00") + "ms  块数=" + v.BlockCount);

        host.Close();
    }
}
