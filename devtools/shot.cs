using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// 开发用：按 PID（可选标题关键字）截取窗口。用法: shot.exe <pid> out.png [标题关键字]
class Shot
{
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    static uint _wantPid;
    static string _filter = "";
    static IntPtr _found = IntPtr.Zero;

    static bool Cb(IntPtr h, IntPtr l)
    {
        if (!IsWindowVisible(h)) return true;
        if (GetWindowTextLength(h) == 0) return true;
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid != _wantPid) return true;
        if (_filter.Length > 0)
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.ToString().IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) return true;
        }
        _found = h;
        return false;
    }

    static int Main(string[] args)
    {
        SetProcessDPIAware();
        if (args.Length < 2) { Console.WriteLine("用法: shot.exe <pid> out.png [标题关键字]"); return 1; }
        _wantPid = uint.Parse(args[0]);
        if (args.Length > 2) _filter = args[2];
        EnumWindows(Cb, IntPtr.Zero);
        if (_found == IntPtr.Zero) { Console.WriteLine("该进程没有可见窗口"); return 2; }
        SetForegroundWindow(_found);
        ShowWindow(_found, 9);                                  // SW_RESTORE
        SetWindowPos(_found, new IntPtr(-1), 0, 0, 0, 0, 0x0043); // HWND_TOPMOST
        System.Threading.Thread.Sleep(900);
        RECT r;
        if (!GetWindowRect(_found, out r)) { Console.WriteLine("拿不到窗口位置"); return 3; }
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        using (Bitmap bmp = new Bitmap(w, ht))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, ht));
            bmp.Save(args[1], ImageFormat.Png);
        }
        SetWindowPos(_found, new IntPtr(-2), 0, 0, 0, 0, 0x0043); // 还原 HWND_NOTOPMOST
        Console.WriteLine("saved " + args[1] + "  " + w + "x" + ht);
        return 0;
    }
}
