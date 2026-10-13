using System;
using System.Runtime.InteropServices;
using System.Text;

// 开发用：列出某个进程的所有顶层窗口（标题/类名/位置/是否可见）。用法: wins.exe <pid>
class Wins
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static uint want;
    static StringBuilder outp = new StringBuilder();

    static bool Cb(IntPtr h, IntPtr l)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid != want) return true;
        StringBuilder t = new StringBuilder(256);
        GetWindowText(h, t, 256);
        StringBuilder c = new StringBuilder(256);
        GetClassName(h, c, 256);
        RECT r;
        GetWindowRect(h, out r);
        outp.AppendLine(string.Format("visible={0} rect=({1},{2})-({3},{4}) class={5} title=[{6}]",
            IsWindowVisible(h) ? 1 : 0, r.L, r.T, r.R, r.B, c.ToString(), t.ToString()));
        return true;
    }

    static int Main(string[] a)
    {
        if (a.Length < 1) { Console.WriteLine("用法: wins.exe <pid>"); return 1; }
        want = uint.Parse(a[0]);
        EnumWindows(Cb, IntPtr.Zero);
        Console.Write(outp.ToString());
        return 0;
    }
}
