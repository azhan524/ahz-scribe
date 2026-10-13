using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 探针：把程序目录里真实的 sessions.json 第一个会话渲染出来，打印每个消息块的几何
// （必须在"程序目录"里运行，它会读那个目录的 config.json / sessions.json）
class ProbeSession
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
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        MainForm f = new MainForm(false);
        f.WindowState = FormWindowState.Maximized;
        f.Show();
        Pump(1800);

        TranscriptView rtb = (TranscriptView)Get(f, "rtb");
        rtb.FlushNow();
        Pump(400);

        Console.WriteLine("程序目录: " + AppDomain.CurrentDomain.BaseDirectory);
        Console.WriteLine("窗口: " + f.Width + "x" + f.Height + "  面板: " + rtb.ClientSize
            + "  hero.Visible=" + ((Control)Get(f, "heroPanel")).Visible);
        Console.WriteLine("内容高=" + rtb.AutoScrollMinSize.Height
            + "  当前滚动=" + (-rtb.AutoScrollPosition.Y)
            + "  需要滚动条=" + (rtb.AutoScrollMinSize.Height > rtb.ClientSize.Height));

        List<MsgBlock> bs = (List<MsgBlock>)Get(rtb, "_blocks");
        int y = 0;
        Console.WriteLine("块数=" + bs.Count);
        for (int i = 0; i < bs.Count; i++)
        {
            MsgBlock b = bs[i];
            int visTop = b.PointToScreen(Point.Empty).Y - rtb.PointToScreen(Point.Empty).Y;
            Console.WriteLine(string.Format("  #{0,-2} {1,-11} 高={2,-5} 宽={3,-5} 字={4,-6} 内容y={5,-6} 可视y={6}",
                i, b.Kind, b.Height, b.Width, b.Body.Length, y, visTop));
            y += b.Height + Theme.Px(6);
        }

        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save("real-session.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        Console.WriteLine("已保存 real-session.png");
        f.Close();
    }
}
