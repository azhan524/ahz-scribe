using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// UI 观测（升级需求 P0-3，按"确定性断言优先"的思路重做）
//
//   为什么不用 shot.exe / CopyFromScreen：实测反复被骗 —— 抓到的可能是盖在上面的别的
//   窗口，也可能是 DWM 还没合成完（卡片看着"半透明"，DrawToBitmap 一验却是纯白）。
//   所以这里观测的是**真实控件树的几何**，不是像素。
//
//   用法：
//     uiobserve.exe tree [控件名过滤]        列出控件树：类型 名称 坐标 尺寸 可见 文字
//     uiobserve.exe inside                  断言：所有可见控件都在自己父控件范围内（越界=布局漏了）
//     uiobserve.exe shot <控件名> <出图路径>  把某个控件 DrawToBitmap 出图（单控件地面真相）
//     uiobserve.exe popovers                模型浮层 / 权限浮层的几何 + 相对 chip 的居中/间距
//
//   退出码：0 = 通过 / 1 = 有断言失败 / 2 = 用法错
class UiObserve
{
    static MainForm _form;
    static int _fail;

    static void Say(string s) { Console.WriteLine(s); }

    static void Pump(int ms)
    {
        DateTime end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    static object Get(object t, string f)
    {
        FieldInfo fi = t.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return fi == null ? null : fi.GetValue(t);
    }

    static void Call(object t, string m, params object[] a)
    {
        Type[] types = new Type[a.Length];
        for (int i = 0; i < a.Length; i++) types[i] = a[i] == null ? typeof(object) : a[i].GetType();
        MethodInfo mi = t.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic, null, types, null);
        if (mi != null) mi.Invoke(t, a);
    }

    static string TextOf(Control c)
    {
        try
        {
            string t = c.Text;
            if (t == null || t.Length == 0)
            {
                // 自绘控件：读它自己的字段（Label/Title/Head 之类）
                foreach (string fn in new string[] { "Label", "Title", "Head", "Value", "Icon" })
                {
                    object v = Get(c, fn);
                    if (v is string && ((string)v).Length > 0) { t = ((string)v); break; }
                }
            }
            if (t == null) t = "";
            t = t.Replace("\r", " ").Replace("\n", " ");
            return t.Length > 40 ? t.Substring(0, 40) + "…" : t;
        }
        catch { return ""; }
    }

    // 深度优先枚举（跳过不可见的内部容器，但保留"可见性"这一列给断言用）
    static void Walk(Control root, string filter, int depth, List<Control> into)
    {
        foreach (Control c in root.Controls)
        {
            if (filter == null || filter.Length == 0
                || c.GetType().Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || (c.Name != null && c.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                into.Add(c);
            if (depth < 12) Walk(c, filter, depth + 1, into);
        }
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string cmd = args.Length > 0 ? args[0] : "tree";
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();
        Say("DPI 缩放 = " + Theme.Dpi);

        _form = new MainForm(false);
        _form.ShowInTaskbar = false;
        _form.WindowState = FormWindowState.Maximized;
        _form.Show();
        Pump(1200);
        _form.Activate();
        Pump(400);
        Say("主窗口 = " + _form.Bounds + "  客户区 = " + _form.ClientSize);

        int code = 0;
        if (cmd == "tree") code = CmdTree(args);
        else if (cmd == "inside") code = CmdInside();
        else if (cmd == "shot") code = CmdShot(args);
        else if (cmd == "popovers") code = CmdPopovers();
        else { Say("用法: uiobserve.exe tree|inside|shot|popovers ..."); code = 2; }

        _form.Close();
        return code;
    }

    static int CmdTree(string[] args)
    {
        string filter = args.Length > 1 ? args[1] : "";
        List<Control> all = new List<Control>();
        Walk(_form, filter, 0, all);
        Say("控件 " + all.Count + " 个" + (filter.Length > 0 ? "（过滤：" + filter + "）" : "") + "：");
        foreach (Control c in all)
        {
            Rectangle r = c.RectangleToScreen(c.ClientRectangle);
            Say(string.Format("  {0,-20} {1,-16} 屏幕=({2},{3}) 尺寸={4}x{5} {6} {7}",
                c.GetType().Name, c.Name == null ? "" : c.Name, r.Left, r.Top, r.Width, r.Height,
                c.Visible ? "可见" : "隐藏", TextOf(c)));
        }
        return all.Count > 0 ? 0 : 1;
    }

    // 布局断言：可见子控件必须落在父控件内（这类"漂出去/被顶到可视区外"的 bug 出现过多次）
    static int CmdInside()
    {
        List<Control> all = new List<Control>();
        Walk(_form, "", 0, all);
        int checkedN = 0, bad = 0;
        foreach (Control c in all)
        {
            if (!c.Visible || c.Parent == null) continue;
            checkedN++;
            Rectangle pr = c.Parent.ClientRectangle;
            if (!pr.Contains(c.Bounds))
            {
                bad++;
                if (bad <= 12)
                    Say("  [越界] " + c.GetType().Name + " " + c.Name + " 自身=" + c.Bounds + " 父=" + pr);
            }
        }
        Say("检查了 " + checkedN + " 个可见控件，越界 " + bad + " 个");
        if (bad > 0) { _fail++; Say("  ✗ 有控件跑到父容器外面了"); return 1; }
        Say("  ✓ 所有可见控件都在父容器内");
        return 0;
    }

    static int CmdShot(string[] args)
    {
        if (args.Length < 3) { Say("用法: uiobserve.exe shot <控件名> <出图路径>"); return 2; }
        string want = args[1], outPath = args[2];
        List<Control> all = new List<Control>();
        Walk(_form, want, 0, all);
        Control target = null;
        foreach (Control c in all)
            if ((c.Name != null && c.Name == want) || c.GetType().Name == want) { target = c; break; }
        if (target == null) { Say("没找到控件：" + want); return 1; }
        if (target.Width <= 0 || target.Height <= 0) { Say("控件尺寸为 0，画不了：" + want); return 1; }
        using (Bitmap bmp = new Bitmap(target.Width, target.Height))
        {
            target.DrawToBitmap(bmp, new Rectangle(0, 0, target.Width, target.Height));
            bmp.Save(outPath, ImageFormat.Png);
            Say("已保存 " + outPath + "  " + bmp.Width + "x" + bmp.Height);
            // 采样几个点，机器可读地说明"到底画了没有"
            Say("  采样 左上=" + bmp.GetPixel(1, 1) + " 中心=" + bmp.GetPixel(bmp.Width / 2, bmp.Height / 2));
        }
        return 0;
    }

    // 浮层几何断言：水平居中于 chip、竖直在 chip 上方、不压住 chip
    static int CmdPopovers()
    {
        ModelChip mchip = (ModelChip)Get(_form, "cmbModel");
        UiDrop pchip = (UiDrop)Get(_form, "permChip");
        int bad = 0;

        Call(_form, "ToggleModelPopover");
        Pump(700);
        Form mp = (Form)Get(_form, "modelPop");
        bad += CheckPopover("模型浮层", mchip, mp);
        Call(_form, "CloseModelPopover");
        Pump(300);

        Call(_form, "TogglePermPopover");
        Pump(600);
        Form pp = (Form)Get(_form, "permPop");
        bad += CheckPopover("权限浮层", pchip, pp);
        Call(_form, "ClosePermPopover");
        Pump(200);

        Say(bad == 0 ? "  ✓ 浮层几何全部通过" : "  ✗ 有 " + bad + " 项浮层几何不对");
        return bad == 0 ? 0 : 1;
    }

    static int CheckPopover(string what, Control chip, Form pop)
    {
        if (pop == null) { Say("  [FAIL] " + what + " 没弹出来"); return 1; }
        Rectangle cr = chip.RectangleToScreen(chip.ClientRectangle);
        int pad = Theme.Px(((PopoverForm)pop).Pad);
        int cardCx = pop.Left + pop.Width / 2;
        int chipCx = cr.Left + cr.Width / 2;
        int cardBottom = pop.Bottom - pad;
        int centerOff = cardCx - chipCx;
        int gap = cr.Top - cardBottom;
        int shadow = pad - Theme.Px(2);
        bool ok = Math.Abs(centerOff) <= 2 && gap >= 0 && cardBottom <= cr.Top;
        Say(string.Format("  {0}：卡片 {1}x{2} 中心偏差={3}px 底↔chip顶={4}px（含阴影 {5}px）压住chip={6}  {7}",
            what, pop.Width - pad * 2, pop.Height - pad * 2, centerOff, gap, shadow,
            cardBottom > cr.Top, ok ? "✓" : "✗"));
        return ok ? 0 : 1;
    }
}
