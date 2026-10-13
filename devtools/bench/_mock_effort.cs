using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

// 原型：把"模型 + 推理等级"选择器按本项目的 Theme 画出来，看实际效果
//   左 = 带滑杆的等级卡片（截图一）    右 = 「模型 / 推理等级」菜单（截图二）
class MockEffort : Control
{
    public MockEffort()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint, true);
        BackColor = Theme.Bg;
    }

    static void Round(Graphics g, Rectangle r, int rad, Color fill, Color border)
    {
        using (GraphicsPath p = Ui.Round(r, rad))
        {
            using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
            if (border != Color.Empty) using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
        }
    }

    static void Txt(Graphics g, string s, Font f, int x, int y, Color c)
    {
        TextRenderer.DrawText(g, s, f, new Point(x, y), c,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    // 一枚输入条（含左边的"选择强度"chip、右边的圆形发送键）
    static void Composer(Graphics g, int x, int y, int w, int h, string summary)
    {
        Round(g, new Rectangle(x, y, w, h), Theme.Px(16), Color.White, Theme.Line);
        int by = y + h - Theme.Px(44);
        // 左：选择强度 chip
        Font fc = Theme.F(9f);
        Txt(g, "选择强度", fc, x + Theme.Px(18), by + Theme.Px(8), Theme.FgMid);
        Txt(g, "⌄", fc, x + Theme.Px(18) + Ui.TextWidth("选择强度", fc) + Theme.Px(5), by + Theme.Px(7), Theme.FgFaint);
        // 圆形发送键
        int d = Theme.Px(38);
        Rectangle sr = new Rectangle(x + w - Theme.Px(16) - d, y + h - Theme.Px(50), d, d);
        using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, sr);
        using (Pen p = new Pen(Color.White, Theme.Dpi * 1.8f))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
            g.DrawLine(p, sr.Left + d / 2, sr.Bottom - Theme.Px(8), sr.Left + d / 2, sr.Top + Theme.Px(9));
            g.DrawLine(p, sr.Left + d / 2 - Theme.Px(5), sr.Top + Theme.Px(14), sr.Left + d / 2, sr.Top + Theme.Px(9));
            g.DrawLine(p, sr.Left + d / 2 + Theme.Px(5), sr.Top + Theme.Px(14), sr.Left + d / 2, sr.Top + Theme.Px(9));
        }
        // 麦克风
        int micX = sr.Left - Theme.Px(40);
        using (Pen p = new Pen(Theme.FgMid, Theme.Dpi * 1.6f))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
            g.DrawArc(p, micX - Theme.Px(5), by + Theme.Px(5), Theme.Px(10), Theme.Px(13), 0, 180);
            g.DrawLine(p, micX, by + Theme.Px(11), micX, by + Theme.Px(15));
            g.DrawLine(p, micX - Theme.Px(4), by + Theme.Px(18), micX + Theme.Px(4), by + Theme.Px(18));
        }
        // 中右：那行"模型 + 等级"摘要（截图二里带 ⌃ 的那行），右对齐到麦克风左边
        Font f = Theme.F(9.5f);
        int tw = Ui.TextWidth(summary, f);
        int sx = micX - Theme.Px(22) - tw - Theme.Px(16);
        Txt(g, summary, f, sx, by + Theme.Px(9), Theme.FgMid);
        Txt(g, "⌃", fc, sx + tw + Theme.Px(7), by + Theme.Px(7), Theme.FgFaint);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        int colW = Width / 2;

        // ================= 左：带滑杆的等级卡片 =================
        int cw = Math.Min(Theme.Px(360), colW - Theme.Px(60));
        int cx = Theme.Px(30) + (colW - Theme.Px(60) - cw) / 2;
        int ch = Theme.Px(150);
        int cy = Theme.Px(30);
        Round(g, new Rectangle(cx + Theme.Px(4), cy + Theme.Px(6), cw, ch), Theme.Px(18), Color.FromArgb(26, 20, 22, 30), Color.Empty);  // 淡淡一层阴影
        Round(g, new Rectangle(cx, cy, cw, ch), Theme.Px(18), Color.White, Theme.Line);
        string lvl = "高";
        Font fb = Theme.FB(10.5f);
        Txt(g, lvl, fb, cx + (cw - Ui.TextWidth(lvl, fb)) / 2, cy + Theme.Px(18), Theme.Accent);
        string sub = "DeepSeek-V4.1-Flash  ›";
        Font fs = Theme.F(8.5f);
        Txt(g, sub, fs, cx + (cw - Ui.TextWidth(sub, fs)) / 2, cy + Theme.Px(48), Theme.FgFaint);
        // 滑杆：底轨 + 蓝色已选区 + 端点小圆点 + 白色圆钮
        int tx = cx + Theme.Px(24), tw2 = cw - Theme.Px(48), th = Theme.Px(14), ty = cy + Theme.Px(88);
        Round(g, new Rectangle(tx, ty, tw2, th), th / 2, Color.FromArgb(232, 234, 240), Color.Empty);
        int fillW = (int)(tw2 * 0.78f);
        Round(g, new Rectangle(tx, ty, fillW, th), th / 2, Theme.Accent, Color.Empty);
        using (SolidBrush wb = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
            g.FillEllipse(wb, tx + Theme.Px(5), ty + th / 2 - Theme.Px(3), Theme.Px(6), Theme.Px(6));
        int kd = Theme.Px(30);
        Round(g, new Rectangle(tx + fillW - kd / 2, ty + th / 2 - kd / 2, kd, kd), kd / 2, Color.White, Color.FromArgb(226, 228, 236));
        Composer(g, Theme.Px(30), cy + ch + Theme.Px(40), colW - Theme.Px(60), Theme.Px(96),
            "DeepSeek-V4.1-Flash  Max");

        // ================= 右：「模型 / 推理等级」菜单 =================
        int rx0 = colW + Theme.Px(30);
        int mw = Math.Min(Theme.Px(360), colW - Theme.Px(60));
        int mx = rx0 + (colW - Theme.Px(60) - mw) / 2;
        int mh = Theme.Px(120);
        Rectangle mr = new Rectangle(mx, Theme.Px(30), mw, mh);
        Round(g, new Rectangle(mr.X + Theme.Px(4), mr.Y + Theme.Px(6), mw, mh), Theme.Px(16), Color.FromArgb(26, 20, 22, 30), Color.Empty);
        Round(g, mr, Theme.Px(16), Color.White, Theme.Line);
        string[] names = new string[] { "模型", "推理等级" };
        string[] vals = new string[] { "DeepSeek-V41-Flash", "Max" };
        for (int i = 0; i < 2; i++)
        {
            int ry = mr.Y + Theme.Px(16) + i * Theme.Px(44);
            if (i == 0) Round(g, new Rectangle(mr.X + Theme.Px(8), ry - Theme.Px(6), mw - Theme.Px(16), Theme.Px(40)), Theme.Px(10), Theme.Hover, Color.Empty);
            Txt(g, names[i], Theme.F(10f), mr.X + Theme.Px(20), ry, Theme.Fg);
            Font fv = Theme.F(9.5f);
            int vw = Ui.TextWidth(vals[i], fv);
            Txt(g, vals[i], fv, mr.Right - Theme.Px(38) - vw, ry + Theme.Px(2), Theme.FgDim);
            Txt(g, "›", Theme.F(10f), mr.Right - Theme.Px(26), ry, Theme.FgFaint);
        }
        Composer(g, rx0, mr.Bottom + Theme.Px(40), colW - Theme.Px(60), Theme.Px(96),
            "DeepSeek-V4.1-Flash  Max");

        Txt(g, "← 点「推理等级」展开成滑杆卡片", Theme.F(8.5f), Theme.Px(30), Height - Theme.Px(28), Theme.FgFaint);
        Txt(g, "→ 点「推理等级」的一项是二级菜单", Theme.F(8.5f), rx0, Height - Theme.Px(28), Theme.FgFaint);
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();
        string outPath = args.Length > 0 ? args[0] : "_mock_effort.png";
        MockEffort c = new MockEffort();
        c.Size = new Size(Theme.Px(760), Theme.Px(360));
        Form f = new Form();
        f.FormBorderStyle = FormBorderStyle.None;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.ClientSize = c.Size;
        f.Controls.Add(c);
        f.Show();
        DateTime end = DateTime.Now.AddMilliseconds(500);
        while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
        using (Bitmap bmp = new Bitmap(c.Width, c.Height))
        {
            c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
            bmp.Save(outPath, ImageFormat.Png);
        }
        Console.WriteLine("已保存 " + outPath + "  " + c.Width + "x" + c.Height);
        f.Close();
    }
}
