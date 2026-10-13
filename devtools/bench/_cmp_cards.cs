using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 把参考图和我们的卡片并排拼一张（左=参考，右=我们的），三行：强度卡 / 模型卡 / 权限卡
class CmpCards
{
    static Bitmap Fit(Image src, int h)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * (double)h / src.Height));
        Bitmap b = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }
        return b;
    }

    static string[] refs = new string[]
    {
        @"C:\Users\Ahzhan\AppData\Local\Temp\codex-clipboard-7e7dd84a-89ab-4bf5-9451-6ea7cf0b115a.png",
        @"C:\Users\Ahzhan\AppData\Local\Temp\codex-clipboard-5ee5df3e-0fd7-4c23-b312-5d50fb650111.png",
        @"C:\Users\Ahzhan\AppData\Local\Temp\codex-clipboard-cfefa08d-8308-471b-b831-fe574d03df4f.png"
    };

    static string[] names = new string[] { "强度卡", "模型卡", "权限卡" };

    [STAThread]
    static void Main(string[] args)
    {
        string pre = args.Length > 0 ? args[0] : "devtools\\bench\\_build\\mine6";
        string[] mines = new string[] { pre + "-effort.png", pre + "-models.png", pre + "-perm.png" };
        int rowH = 300, gap = 26, labelH = 26;

        Bitmap[] r = new Bitmap[3], m = new Bitmap[3];
        int totalW = 0, totalH = gap;
        for (int i = 0; i < 3; i++)
        {
            r[i] = Fit(Image.FromFile(refs[i]), rowH);
            m[i] = Fit(Image.FromFile(mines[i]), rowH);
            totalW = Math.Max(totalW, r[i].Width + m[i].Width);
            totalH += labelH + rowH + gap;
        }
        totalW += gap * 3;

        using (Bitmap outBmp = new Bitmap(totalW, totalH))
        {
            using (Graphics g = Graphics.FromImage(outBmp))
            {
                g.Clear(Color.FromArgb(250, 250, 252));
                Font f = new Font("Microsoft YaHei UI", 11f);
                SolidBrush br = new SolidBrush(Color.FromArgb(90, 95, 105));
                Font fb = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
                SolidBrush br2 = new SolidBrush(Color.FromArgb(40, 45, 55));
                int y = gap;
                for (int i = 0; i < 3; i++)
                {
                    g.DrawString(names[i] + "   ·   左 = 参考图（Codex/DSH）    右 = 我们的", fb, br2, gap, y);
                    y += labelH;
                    g.DrawImage(r[i], gap, y);
                    g.DrawImage(m[i], gap * 2 + r[i].Width, y);
                    y += rowH + gap;
                }
                g.DrawString("注意：参考图是对方界面里裁出来的，我们的是 150% DPI 下 1:1 截的，",
                    f, br, gap, totalH - 22);
            }
            outBmp.Save("devtools\\bench\\_build\\cards-compare.png", ImageFormat.Png);
            Console.WriteLine("已保存 devtools\\bench\\_build\\cards-compare.png  " + outBmp.Width + "x" + outBmp.Height);
        }
    }
}
