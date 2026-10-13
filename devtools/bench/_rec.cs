using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 开发用：盯着一个窗口连续抓帧，算"相邻两帧改了多少像素"。
// 用法: rec.exe <pid> <outDir> <seconds> [标题关键字]
// 输出 rec.csv：序号,毫秒,整体变化%,对话区上半段变化%,对话区底部变化%,平均亮度差
class Rec
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    static uint _wantPid; static string _filter = ""; static IntPtr _found = IntPtr.Zero;

    static bool Cb(IntPtr h, IntPtr l)
    {
        if (!IsWindowVisible(h)) return true;
        if (GetWindowTextLength(h) == 0) return true;
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != _wantPid) return true;
        if (_filter.Length > 0)
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.ToString().IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) return true;
        }
        _found = h; return false;
    }

    static int[] _prev; static int _pw, _ph;

    static void Diff(Bitmap b, out double all, out double top, out double bottom, out double mean)
    {
        Rectangle rc = new Rectangle(0, 0, b.Width, b.Height);
        BitmapData d = b.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] cur = new int[b.Width * b.Height];
        Marshal.Copy(d.Scan0, cur, 0, cur.Length);
        b.UnlockBits(d);
        if (_prev == null || _pw != b.Width || _ph != b.Height)
        {
            _prev = cur; _pw = b.Width; _ph = b.Height;
            all = top = bottom = mean = 0; return;
        }
        // 对话区大约从窗口 10% 高开始，到 82% 高结束；上半段 = 前 35%
        int y0 = (int)(b.Height * 0.10), y1 = (int)(b.Height * 0.82);
        int yTopEnd = y0 + (int)((y1 - y0) * 0.35);
        int yBot0 = y1 - (int)((y1 - y0) * 0.22);
        long chAll = 0, chTop = 0, chBot = 0, nAll = 0, nTop = 0, nBot = 0, sum = 0;
        for (int y = 0; y < b.Height; y++)
        {
            int row = y * b.Width;
            bool inTop = (y >= y0 && y < yTopEnd);
            bool inBot = (y >= yBot0 && y < y1);
            for (int x = 0; x < b.Width; x++)
            {
                int a = cur[row + x], p = _prev[row + x];
                int da = Math.Abs((a & 0xFF) - (p & 0xFF))
                    + Math.Abs(((a >> 8) & 0xFF) - ((p >> 8) & 0xFF))
                    + Math.Abs(((a >> 16) & 0xFF) - ((p >> 16) & 0xFF));
                sum += da;
                bool ch = (da > 72);
                nAll++;
                if (ch) chAll++;
                if (inTop) { nTop++; if (ch) chTop++; }
                if (inBot) { nBot++; if (ch) chBot++; }
            }
        }
        all = nAll > 0 ? chAll * 100.0 / nAll : 0;
        top = nTop > 0 ? chTop * 100.0 / nTop : 0;
        bottom = nBot > 0 ? chBot * 100.0 / nBot : 0;
        mean = nAll > 0 ? (double)sum / nAll : 0;
        _prev = cur;
    }

    static int Main(string[] args)
    {
        SetProcessDPIAware();
        if (args.Length < 3) { Console.WriteLine("用法: rec.exe <pid> <outDir> <seconds> [标题关键字]"); return 1; }
        _wantPid = uint.Parse(args[0]);
        string outDir = args[1];
        double seconds = double.Parse(args[2]);
        if (args.Length > 3) _filter = args[3];
        System.IO.Directory.CreateDirectory(outDir);
        EnumWindows(Cb, IntPtr.Zero);
        if (_found == IntPtr.Zero) { Console.WriteLine("该进程没有可见窗口"); return 2; }
        ShowWindow(_found, 9);
        SetForegroundWindow(_found);
        Thread.Sleep(600);
        RECT r; GetWindowRect(_found, out r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        Console.WriteLine("窗口 " + w + "x" + h + " @ " + r.Left + "," + r.Top);

        System.Collections.Generic.List<string> rows = new System.Collections.Generic.List<string>();
        DateTime t0 = DateTime.Now;
        int i = 0;
        double sumAll = 0, sumTop = 0, sumBot = 0, maxAll = 0; int fullRedraw = 0;
        while ((DateTime.Now - t0).TotalSeconds < seconds)
        {
            using (Bitmap bmp = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h));
                double a, tp, bt, mn;
                Diff(bmp, out a, out tp, out bt, out mn);
                rows.Add(i + "," + (int)(DateTime.Now - t0).TotalMilliseconds + "," +
                    a.ToString("0.0") + "," + tp.ToString("0.0") + "," + bt.ToString("0.0") + "," + mn.ToString("0.0"));
                sumAll += a; sumTop += tp; sumBot += bt;
                if (a > maxAll) maxAll = a;
                if (a > 50) fullRedraw++;
                if (i % 5 == 0)
                    bmp.Save(System.IO.Path.Combine(outDir, "f" + i.ToString("000") + ".png"), ImageFormat.Png);
            }
            i++;
            Thread.Sleep(60);
        }
        System.IO.File.WriteAllLines(System.IO.Path.Combine(outDir, "rec.csv"), rows.ToArray(), Encoding.UTF8);
        int n = rows.Count - 1;
        Console.WriteLine("帧数=" + n + "  平均整体变化=" + (sumAll / Math.Max(1, n)).ToString("0.0") + "%"
            + "  平均上半段变化=" + (sumTop / Math.Max(1, n)).ToString("0.0") + "%"
            + "  平均底部变化=" + (sumBot / Math.Max(1, n)).ToString("0.0") + "%"
            + "  最大整体变化=" + maxAll.ToString("0.0") + "%"
            + "  近乎整屏重画帧数=" + fullRedraw);
        return 0;
    }
}
