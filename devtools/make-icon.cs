using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 生成 src\app.ico：照界面里的 LogoMark 画 —— 圆角方块（品牌蓝渐变）+ 白色 spark。
// 多尺寸（16/24/32/48/64/128/256），32bpp BMP 条目（不依赖任何外部图片工具，随时可重生成）。
class MakeIcon
{
    static readonly int[] Sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };

    static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        List<Bitmap> frames = new List<Bitmap>();
        foreach (int s in Sizes) frames.Add(Frame(s));
        WriteIco(outPath, frames);
        foreach (Bitmap b in frames) b.Dispose();
        Console.WriteLine("已生成 " + outPath + "（" + Sizes.Length + " 个尺寸：" + string.Join("/", Array.ConvertAll(Sizes, delegate(int x) { return x.ToString(); })) + "）");
    }

    static PointF P(RectangleF r, float x, float y)
    {
        return new PointF(r.X + r.Width * x, r.Y + r.Height * y);
    }

    static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Max(2f, radius * 2f);
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Frame(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float pad = size * 0.025f;
            RectangleF r = size <= 20
                ? new RectangleF(0.5f, 0.5f, size - 1f, size - 1f)          // 小图标别留边，否则糊成一团
                : new RectangleF(pad, pad, size - pad * 2f, size - pad * 2f);
            using (GraphicsPath p = Round(r, size * (size <= 20 ? 0.28f : 0.22f)))
            using (LinearGradientBrush br = new LinearGradientBrush(r,
                Color.FromArgb(255, 92, 120, 255), Color.FromArgb(255, 56, 84, 235), 90f))
                g.FillPath(br, p);

            float s = size * (size <= 20 ? 0.72f : 0.56f);
            RectangleF sr = new RectangleF((size - s) / 2f, (size - s) / 2f, s, s);
            PointF[] pts = new PointF[] {
                P(sr, 0.50f, 0.04f), P(sr, 0.60f, 0.40f), P(sr, 0.96f, 0.50f), P(sr, 0.60f, 0.60f),
                P(sr, 0.50f, 0.96f), P(sr, 0.40f, 0.60f), P(sr, 0.04f, 0.50f), P(sr, 0.40f, 0.40f)
            };
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillPolygon(b, pts);
        }
        return bmp;
    }

    static void WriteIco(string path, List<Bitmap> frames)
    {
        List<byte[]> blobs = new List<byte[]>();
        // 256 那一帧用 PNG 存（ico 支持）：BMP 要 260KB，PNG 只要 ~20KB，exe 别为图标胖一圈
        foreach (Bitmap b in frames) blobs.Add(b.Width >= 256 ? PngBytes(b) : DibBytes(b));
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        using (FileStream fs = new FileStream(path, FileMode.Create))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)frames.Count);
            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                Bitmap b = frames[i];
                w.Write((byte)(b.Width >= 256 ? 0 : b.Width));
                w.Write((byte)(b.Height >= 256 ? 0 : b.Height));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);          // planes
                w.Write((short)32);         // bpp
                w.Write(blobs[i].Length);
                w.Write(offset);
                offset += blobs[i].Length;
            }
            foreach (byte[] blob in blobs) w.Write(blob);
        }
    }

    // ico 里的位图是 DIB：BITMAPINFOHEADER + 自下而上的 BGRA + 全 0 的 AND 掩码（透明交给 alpha）
    static byte[] PngBytes(Bitmap bmp)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static byte[] DibBytes(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write(40);
            bw.Write(w);
            bw.Write(h * 2);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(0);
            bw.Write(w * h * 4);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                }
            bw.Write(new byte[((w + 31) / 32) * 4 * h]);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
