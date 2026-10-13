using System;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 开发用：模拟"本地模型逐 token 流式输出"，一边喂一边连续抓帧，看哪里在跳
class StreamShot
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

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string outDir = args.Length > 0 ? args[0] : ".";
        MainForm f = new MainForm(false);
        f.ShowInTaskbar = false;
        f.Show();
        f.WindowState = FormWindowState.Maximized;
        Pump(900);

        // 后台线程：像本地模型那样每 ~95ms 吐一个 token
        string think = "好的，用户问我晚饭吃什么。我需要先看看工作目录里有什么文件，"
            + "根据项目规矩我只能访问工作目录内的文件，所以先调用 list_dir 看看。"
            + "然后我需要判断哪些文件跟晚饭相关，如果没有相关文件就基于常识回答，"
            + "并且要遵守项目规矩里说的不要修改源码。这里要注意不能编造文件内容，"
            + "应该先看再说，这是最基本的要求。";
        string answer = "结论：目录里只有源码，没有食谱文件，所以给你一个不依赖文件的建议——"
            + "天气转凉，晚饭建议吃点热汤热饭，比如番茄牛腩炖土豆配米饭，或者一碗汤面加个卤蛋。";
        Thread t = new Thread(delegate()
        {
            try
            {
                f.Write(OutKind.Thinking, "\n思考\n");
                foreach (char c in think) { f.Write(OutKind.Thinking, c.ToString()); Thread.Sleep(90); }
                f.Write(OutKind.ToolCall, "  ● list_dir(.)\n");
                f.Write(OutKind.ToolResult, "  └ ✓ 列了 12 项\n");
                foreach (char c in answer) { f.Write(OutKind.Assistant, c.ToString()); Thread.Sleep(90); }
            }
            catch (Exception ex) { Console.WriteLine("喂字出错: " + ex.Message); }
        });
        t.IsBackground = true;
        t.Start();

        // 边喂边抓帧
        for (int i = 0; i < 14; i++)
        {
            Pump(200);
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(Math.Max(0, f.Left), Math.Max(0, f.Top), 0, 0, new Size(f.Width, f.Height));
                bmp.Save(System.IO.Path.Combine(outDir, "s" + i.ToString("00") + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        Console.WriteLine("抓帧完成（喂字线程还在跑：" + t.IsAlive + "）");
        f.Close();
    }
}
