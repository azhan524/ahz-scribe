using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 把输入条上那张「模型 / 推理等级」卡片真开出来再截屏
// （菜单是独立的顶层窗口，DrawToBitmap 抓不到，只能用 CopyFromScreen）
//   用法: shoteffort.exe 前缀  → 前缀-menu.png / 前缀-slider.png
class ShotEffort
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

    // 以 anchor 控件为参照，往左上扩一块区域截屏
    static void Shot(Control anchor, int left, int top, int w, int h, string path)
    {
        Point o = anchor.PointToScreen(new Point(-left, -top));
        Rectangle scr = Screen.FromControl(anchor).Bounds;
        int x = Math.Max(scr.Left, o.X), y = Math.Max(scr.Top, o.Y);
        if (x + w > scr.Right) w = scr.Right - x;
        if (y + h > scr.Bottom) h = scr.Bottom - y;
        using (Bitmap bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h)))
        {
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, bmp.Size);
            bmp.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("已保存 " + path + "  " + w + "x" + h);
    }

    [STAThread]
    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string pre = args.Length > 0 ? args[0] : "effort";
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs a)
        {
            Exception ex = a.ExceptionObject as Exception;
            Console.WriteLine("[未处理异常] " + (ex == null ? Convert.ToString(a.ExceptionObject) : ex.GetType().Name + ": " + ex.Message));
        };
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs a)
        {
            Console.WriteLine("[界面线程异常] " + a.Exception.GetType().Name + ": " + a.Exception.Message);
        };
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();

        MainForm f = new MainForm(false);
        // 造一堆模型，复现"我模型很多"的场景（23 个）
        AgentConfig cfg = (AgentConfig)Get(f, "config");
        cfg.providers.Clear();
        Provider po = Provider.DefaultOllama();
        po.models = new List<string>();
        for (int i = 1; i <= 12; i++) po.models.Add("azhan_v" + i + ":latest");
        cfg.providers.Add(po);
        Provider pd = new Provider();
        pd.name = "deepseek"; pd.type = "openai";
        pd.base_url = "https://api.deepseek.com/v1"; pd.api_key = "x";
        pd.models = new List<string>(new string[] { "deepseek-flash", "deepseek-v4-pro" });
        cfg.providers.Add(pd);
        Provider pg = new Provider();
        pg.name = "智谱 GLM"; pg.type = "openai";
        pg.base_url = "https://open.bigmodel.cn/api/paas/v4"; pg.api_key = "x";
        pg.models = new List<string>(new string[] { "glm-4.5", "glm-4.6", "glm-4.7", "glm-5", "glm-5-turbo", "glm-5.1", "glm-5.2", "glm-5.3" });
        cfg.providers.Add(pg);
        cfg.provider = pg.id; cfg.model = "glm-5.3";
        f.GetType().GetMethod("LoadModels", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f, null);
        Pump(400);
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.WindowState = FormWindowState.Maximized;
        f.Show();
        Pump(1200);
        UiDrop chip = (UiDrop)Get(f, "cmbModel");
        Console.WriteLine("chip 文字 = 「" + chip.Text + "」  屏幕位置=" + chip.RectangleToScreen(chip.ClientRectangle));

        // 用 app 自己的那张卡片（走的就是点击时的同一条代码路径）
        ContextMenuStrip m = chip.MenuFactory();
        // 让滑杆停在「高」，好和参考图对照（只改显示，不动配置）
        if (m.Items.Count > 2 && m.Items[2] is ToolStripControlHost)
        {
            EffortSlider slv = ((ToolStripControlHost)m.Items[2]).Control as EffortSlider;
            if (slv != null) { slv.Value = 3; slv.Invalidate(); }
        }
        m.Show(chip, new Point(0, -m.Height - Theme.Px(6)));
        Pump(600);
        Console.WriteLine("卡片：行数=" + m.Items.Count);
        foreach (ToolStripItem it in m.Items)
        {
            ToolStripControlHost h = it as ToolStripControlHost;
            if (h == null) { Console.WriteLine("  非自绘行：" + it.Text); continue; }
            FieldInfo fl = h.Control.GetType().GetField("Label");
            FieldInfo fv2 = h.Control.GetType().GetField("Value");
            string lbl = fl == null ? "（非行控件：" + h.Control.GetType().Name + "）" : (string)fl.GetValue(h.Control);
            string val = fv2 == null ? "" : Convert.ToString(fv2.GetValue(h.Control));
            Console.WriteLine("  行「" + lbl + "」当前值=" + val + "  控件=" + h.Control.Width + "x" + h.Control.Height);
        }
        Rectangle mb = m.RectangleToScreen(m.ClientRectangle);
        Console.WriteLine("卡片屏幕区域 = " + mb);
        using (Bitmap bmp = new Bitmap(mb.Width + 40, mb.Height + 40))
        {
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(mb.Left - 20, mb.Top - 20, 0, 0, bmp.Size);
            bmp.Save(pre + "-card.png", ImageFormat.Png);
        }
        Console.WriteLine("已保存 " + pre + "-card.png");

        // 点「模型」→ 可滑动的选择卡片
        Raise(((ToolStripControlHost)m.Items[0]).Control);
        Pump(900);
        Shot(chip, 120, 470, 700, 520, pre + "-pick.png");
        SendKeys.SendWait("{ESC}");
        Pump(400);

        m.Close();
        Pump(200);

        // 选「高」，看输入条上的 chip 和底部状态
        f.GetType().GetMethod("SetEffort", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(f, new object[] { "high", false });
        Pump(400);
        Console.WriteLine("选完高之后 chip = 「" + chip.Text + "」");
        Shot(chip, 900, 40, 1640, 300, pre + "-chip.png");
        f.Close();
    }

    // 模拟点一下自绘行（它靠 Picked 事件开子菜单）
    static void Raise(Control row)
    {
        FieldInfo fi = row.GetType().GetField("Picked", BindingFlags.Instance | BindingFlags.NonPublic);
        Delegate d = fi == null ? null : (Delegate)fi.GetValue(row);
        if (d != null) d.DynamicInvoke(new object[] { row, EventArgs.Empty });
    }

}
