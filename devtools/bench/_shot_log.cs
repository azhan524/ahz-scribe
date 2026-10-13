using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 把设置窗口四页各截一张图（重点看新做的「运行日志」页排版）
//   用法: shotlog.exe 前缀    → 生成 前缀-0.png … 前缀-3.png
class ShotLog
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
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string pre = args.Length > 0 ? args[0] : "settings";
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();

        AgentConfig cfg = new AgentConfig();
        cfg.providers = new List<Provider>();
        cfg.providers.Add(Provider.DefaultOllama());
        Provider ds = new Provider();
        ds.name = "deepseek"; ds.type = "openai";
        ds.base_url = "https://api.deepseek.com/v1"; ds.api_key = "sk-test";
        ds.models = new List<string>(new string[] { "deepseek-flash" });
        cfg.providers.Add(ds);

        SettingsForm f = new SettingsForm(cfg);
        f.Show();
        Pump(800);

        Panel[] pages = (Panel[])Get(f, "_pages");
        string[] names = new string[] { "通用设置", "模型", "运行日志", "关于" };
        MethodInfo showPage = f.GetType().GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic);
        Console.WriteLine("窗口 " + f.Width + "x" + f.Height);

        for (int i = 0; i < pages.Length; i++)
        {
            showPage.Invoke(f, new object[] { i });
            Pump(350);
            string path = pre + "-" + i + ".png";
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            string extra = "";
            Console.Write("  页面尺寸=" + pages[i].Width + "x" + pages[i].Height);
            if (i == 2)
            {
                TextBox lb = (TextBox)Get(f, "_logBox");
                Control r1 = (Control)Get(f, "_btnLogRefresh");
                Control r2 = (Control)Get(f, "_btnLogOpen");
                Control r3 = (Control)Get(f, "_btnLogClear");
                extra = "  日志框=" + lb.Bounds + " 刷新=" + r1.Bounds + " 打开=" + r2.Bounds + " 清空=" + r3.Bounds
                    + " 文字长度=" + lb.Text.Length;
            }
            Console.WriteLine("  " + names[i] + "页 -> " + path + "（控件 " + pages[i].Controls.Count + " 个）" + extra);
        }
        showPage.Invoke(f, new object[] { 2 });
        Pump(200);
        f.Close();
    }
}
