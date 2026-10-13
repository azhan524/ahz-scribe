using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 开发用：把「设置 → 模型」这一页渲染成 PNG（不依赖真实桌面），顺便打印页面上每个按钮的位置和可见性
class ShotSettings
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

    static void DumpButtons(Control c, string indent)
    {
        foreach (Control k in c.Controls)
        {
            string txt = k.Text;
            bool interesting = k is Button || k.GetType().Name == "UiButton" || k.GetType().Name == "FieldBox";
            if (interesting && txt != null && txt.Length > 0)
                Console.WriteLine(string.Format("    {0}{1} \"{2}\" at ({3},{4}) {5}x{6} visible={7}",
                    indent, k.GetType().Name, txt, k.Left, k.Top, k.Width, k.Height, k.Visible));
            if (k.Controls.Count > 0) DumpButtons(k, indent + "  ");
        }
    }

    // 把一页里所有"直接子控件"列出来，标出哪些跑出可视区（页面高度之外）
    static void DumpOutside(Control page, string pageName)
    {
        Console.WriteLine("---- " + pageName + "  页面 " + page.Width + "x" + page.Height + " 可视区=" + page.ClientSize + " ----");
        int bad = 0;
        foreach (Control k in page.Controls)
        {
            try
            {
                bool inside = k.Top >= 0 && k.Left >= 0 && k.Bottom <= page.Height && k.Right <= page.Width;
                if (!inside) bad++;
                Console.WriteLine(string.Format("  {0} {1,-12} ({2},{3}) {4}x{5} visible={6}",
                    inside ? "OK  " : "越界", k.GetType().Name, k.Left, k.Top, k.Width, k.Height, k.Visible));
            }
            catch (Exception ex) { Console.WriteLine("  ??" + k.GetType().Name + " 读取失败: " + ex.GetType().Name); }
        }
        Console.WriteLine("  越界控件数: " + bad);
    }

    static void DumpFields(Control c, string indent)
    {
        foreach (Control k in c.Controls)
        {
            if (k.GetType().Name == "FieldBox")
            {
                FieldBox fb = (FieldBox)k;
                Control inner = fb.Inner;
                int padTop = Theme.Px(fb.Padding.Top), padBottom = Theme.Px(fb.Padding.Bottom);
                string t = inner is TextBox ? ((TextBox)inner).Text : "";
                if (t.Length > 22) t = t.Substring(0, 22) + "…";
                Console.WriteLine(string.Format(
                    "  {0,-4} box=({1},{2}) {3}x{4} | inner=({5},{6}) {7}x{8} | 上留={9} 下留={10} | 文字=\"{11}\"",
                    fb.Name.Length > 0 ? fb.Name : "字段", k.Left, k.Top, k.Width, k.Height,
                    inner.Left, inner.Top, inner.Width, inner.Height,
                    inner.Top - padTop, k.Height - padBottom - inner.Bottom, t));
            }
            if (k.Controls.Count > 0) DumpFields(k, indent + "  ");
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string outPath = args.Length > 0 ? args[0] : "settings.png";
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();

        AgentConfig cfg = new AgentConfig();
        cfg.providers = new List<Provider>();
        cfg.providers.Add(Provider.DefaultOllama());
        Provider ds = new Provider();
        ds.name = "deepseek"; ds.type = "openai";
        ds.base_url = "https://api.deepseek.com/v1"; ds.api_key = "sk-test";
        ds.models = new List<string>(new string[] { "deepseek-flash", "deepseek-v4-pro" });
        cfg.providers.Add(ds);
        Provider glm = new Provider();
        glm.name = "智谱 GLM"; glm.type = "openai";
        glm.base_url = "https://open.bigmodel.cn/api/paas/v4"; glm.api_key = "sk-test2";
        glm.models = new List<string>(new string[] { "glm-4.6", "glm-4-flash" });
        cfg.providers.Add(glm);      // 形态就是用户想要的：deepseek 和 glm 同时在

        SettingsForm f = new SettingsForm(cfg);
        f.Show();
        Pump(700);

        Panel page = ((Panel[])Get(f, "_pages"))[1];
        Console.WriteLine("模型页尺寸: " + page.Width + "x" + page.Height + "  滚动? " + page.AutoScroll);

        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(outPath, ImageFormat.Png);
        }
        Console.WriteLine("已保存 " + outPath + "  " + f.Width + "x" + f.Height);

        Console.WriteLine("页面上的按钮/输入框：");
        DumpButtons(page, "");
        Panel[] pages = (Panel[])Get(f, "_pages");
        string[] names = new string[] { "通用设置", "模型", "关于" };
        MethodInfo showPage = f.GetType().GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int i = 0; i < pages.Length; i++)
        {
            showPage.Invoke(f, new object[] { i });     // 先切到这一页（否则隐藏的页还没被撑开）
            Pump(150);
            DumpOutside(pages[i], names[i]);
        }
        showPage.Invoke(f, new object[] { 1 });
        Pump(150);

        // 每个输入框：框的位置 + 框内文字控件的实际位置（看有没有一个歪的）
        Console.WriteLine("---- 模型页输入框内部几何 ----");
        DumpFields(pages[1], "");
        f.Close();
    }
}
