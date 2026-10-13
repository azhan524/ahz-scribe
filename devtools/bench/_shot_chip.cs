using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 开发用：把主窗口真开出来，截 chip + 浮层（强度页 / 模型列表页 / 换页动画）
//   用法: shotchip.exe 输出前缀
class ShotChip
{
    static object Get(object t, string f)
    {
        return t.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(t);
    }

    static void Call(object t, string m, params object[] a)
    {
        t.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(t, a);
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
        string pre = args.Length > 0 ? args[0] : "chip";
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();
        Console.WriteLine("DPI 缩放 = " + Theme.Dpi);

        MainForm f = new MainForm(false);
        AgentConfig cfg = (AgentConfig)Get(f, "config");
        cfg.providers.Clear();
        Provider po = Provider.DefaultOllama();
        po.models = new List<string>();
        for (int i = 1; i <= 12; i++) po.models.Add("azhan_v" + i + ":latest");
        cfg.providers.Add(po);
        Provider pd = new Provider();
        pd.name = "deepseek"; pd.type = "openai";
        pd.base_url = "https://api.deepseek.com/v1"; pd.api_key = "x";
        pd.models = new List<string>(new string[] { "deepseek-reasoner", "deepseek-flash" });
        cfg.providers.Add(pd);
        Provider pg = new Provider();
        pg.name = "智谱 GLM"; pg.type = "openai";
        pg.base_url = "https://open.bigmodel.cn/api/paas/v4"; pg.api_key = "x";
        pg.models = new List<string>(new string[] { "glm-4.5", "glm-4.6", "glm-4.7", "glm-5", "glm-5-turbo", "glm-5.1", "glm-5.2", "glm-5.3" });
        cfg.providers.Add(pg);
        cfg.provider = pd.id; cfg.model = "deepseek-reasoner"; cfg.effort = "medium";
        Call(f, "LoadModels");
        Pump(400);
        f.ShowInTaskbar = false;
        f.TopMost = true;                  // 保持在前台，否则 CopyFromScreen 抓到的是别的窗口
        f.WindowState = FormWindowState.Maximized;
        f.Show();
        Pump(1000);
        f.Activate();
        Pump(300);

        ModelChip chip = (ModelChip)Get(f, "cmbModel");
        Rectangle cr = chip.RectangleToScreen(chip.ClientRectangle);
        Console.WriteLine("chip 文字 = 「" + chip.Text + "」  chip 宽=" + chip.ClientSize.Width
            + " 需要=" + chip.PreferredWidth());
        UiButton send = (UiButton)Get(f, "btnSend");
        Console.WriteLine("chip 右=" + cr.Right + " 发送键左=" + send.RectangleToScreen(send.ClientRectangle).Left
            + " 重叠=" + (cr.Right > send.RectangleToScreen(send.ClientRectangle).Left));
        Shot(chip, 700, 60, 1455, 260, pre + "-composer.png");

        // 点开：第一页应该是「强度」
        Call(f, "ToggleModelPopover");
        Pump(700);
        ModelPopover pop = (ModelPopover)Get(f, "modelPop");
        if (pop == null) { Console.WriteLine("!! 浮层没弹出来"); f.Close(); return; }
        Console.WriteLine("浮层=" + pop.Bounds + " 页=" + pop.Page + " 卡片宽=" + pop.CardWidth
            + " 强度页宽=" + pop.EffortPage.Width + " 列表宽=" + pop.Pick.Width
            + " 档位=" + pop.EffortPage.Value + " 支持强度=" + pop.EffortPage.EffortSupported);

        // 以 chip 为锚，把"浮层 + chip"整块截进来
        int shotW = 1100, shotH = 700;
        Shot(chip, (shotW - chip.Width) / 2 + Theme.Px(16), shotH - Theme.Px(120), shotW, shotH, pre + "-strength.png");

        // 点模型名那行 → 换成模型列表（等动画走完）
        pop.ShowPage(ModelPopover.PageModels, true);
        Pump(700);
        Console.WriteLine("换页后：页=" + pop.Page + " 卡片宽=" + pop.CardWidth + " 动画中=" + pop.Animating
            + " 行数=" + pop.Pick.RowCount + " 标题=" + string.Join("/", pop.Pick.Headers.ToArray()));
        Shot(chip, (shotW - chip.Width) / 2 + Theme.Px(16), shotH - Theme.Px(120), shotW, shotH, pre + "-models.png");
        // 滚到最上面，确认「本地」组标题画出来了
        pop.Pick.ScrollBy(-100000);
        Pump(300);
        Console.WriteLine("滚到顶：ScrollTop=" + pop.Pick.ScrollTop + " 第一行顶=" + pop.Pick.FirstRowTop
            + "（组标题高 " + pop.Pick.HeaderH + "）");
        Shot(chip, (shotW - chip.Width) / 2 + Theme.Px(16), shotH - Theme.Px(120), shotW, shotH, pre + "-models-top.png");

        // 选一个模型 → 应该平滑换回强度页
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        FieldInfo fi = pop.Pick.GetType().GetField("Picked", BindingFlags.Instance | BindingFlags.NonPublic);
        Delegate d = (Delegate)fi.GetValue(pop.Pick);
        Console.WriteLine("选之前：页=" + pop.Page + " 卡片宽=" + pop.CardWidth + " 动画中=" + pop.Animating);
        pop.Pick.PickedIndex = 0;                  // 挑一个和当前不一样的（本地第一个）
        d.DynamicInvoke(new object[] { pop.Pick, EventArgs.Empty });
        Console.WriteLine("选之后立刻：页=" + pop.Page + " 卡片宽=" + pop.CardWidth + " 动画中=" + pop.Animating);
        Pump(700);
        Console.WriteLine("选完：页=" + pop.Page + " 卡片宽=" + pop.CardWidth
            + " 强度页模型名=" + pop.EffortPage.Title + " chip=「" + chip.Text + "」");
        Shot(chip, (shotW - chip.Width) / 2 + Theme.Px(16), shotH - Theme.Px(120), shotW, shotH, pre + "-back.png");

        // 拖到「高」
        pop.EffortPage.SetValue(2, true);
        Pump(400);
        Console.WriteLine("拖到高：档位=" + pop.EffortPage.Value + " config.effort=" + cfg.effort
            + " chip=「" + chip.Text + "」");
        Shot(chip, (shotW - chip.Width) / 2 + Theme.Px(16), shotH - Theme.Px(120), shotW, shotH, pre + "-high.png");

        // 换成一个不支持调强度的模型：不画滑块，但卡片要有档位 + 模型名 + 一句说明（不是空白）
        cfg.model = "glm-5.3";
        pop.Refresh(cfg.model, Effort.Index(cfg.effort), false);
        Pump(300);
        Console.WriteLine("不支持强度的模型：EffortSupported=" + pop.EffortPage.EffortSupported
            + " 标题=" + pop.EffortPage.Head + " 模型=" + pop.EffortPage.Title);
        Shot(chip, (shotW - chip.Width) / 2 + Theme.Px(16), shotH - Theme.Px(120), shotW, shotH, pre + "-noslider.png");

        // ---- 权限浮层 ----
        Call(f, "CloseModelPopover");
        Pump(300);
        // 截图前把窗口顶到最前（别的窗口可能盖在上面，CopyFromScreen 抓到的是别人的画面）
        f.TopMost = true;
        f.Activate();
        Pump(400);
        f.GetType().GetMethod("TogglePermPopover", BindingFlags.Instance | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null).Invoke(f, null);
        Pump(600);
        Pump(1200);                        // 多等一会，排除"DWM 还没合成"的截图假象
        object ppo = Get(f, "permPop");
        Console.WriteLine("权限浮层：" + (ppo == null ? "没弹出来" : Convert.ToString(((Form)ppo).Bounds)));
        Console.WriteLine("  窗口=" + f.Bounds + " client=" + f.RectangleToScreen(f.ClientRectangle)
            + " chip屏=" + ((UiDrop)Get(f, "permChip")).PointToScreen(new Point(0, 0))
            + " 浮层高=" + (ppo == null ? "-" : Convert.ToString(((Form)ppo).Height)));
        // 整窗口来一张（别的窗口可能盖着，这一张能直接看出浮层位置对不对）
        Rectangle wscr = f.Bounds;
        using (Bitmap bmp = new Bitmap(wscr.Width, wscr.Height))
        {
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(wscr.Left, wscr.Top, 0, 0, bmp.Size);
            bmp.Save(pre + "-perm-win.png", ImageFormat.Png);
        }
        Console.WriteLine("已保存 " + pre + "-perm-win.png");
        UiDrop pchip = (UiDrop)Get(f, "permChip");
        Rectangle pcr = pchip.RectangleToScreen(pchip.ClientRectangle);
        Rectangle pbr = ppo == null ? pcr : ((Form)ppo).Bounds;
        // 直接把"浮层 + chip"整块截出来（用屏幕坐标算，别再经过 Shot 的锚点换算）
        int cx = Math.Max(0, pbr.Left - Theme.Px(30));
        int cy = Math.Max(0, pbr.Top - Theme.Px(20));
        int cw = Math.Min(Screen.FromControl(pchip).Bounds.Width - cx, (pbr.Right + Theme.Px(30)) - cx);
        int chh = Math.Min(Screen.FromControl(pchip).Bounds.Height - cy, (pcr.Bottom + Theme.Px(30)) - cy);
        using (Bitmap bmp = new Bitmap(Math.Max(1, cw), Math.Max(1, chh)))
        {
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(cx, cy, 0, 0, bmp.Size);
            bmp.Save(pre + "-perm.png", ImageFormat.Png);
        }
        Console.WriteLine("已保存 " + pre + "-perm.png  " + cw + "x" + chh);
        f.Close();
    }
}
