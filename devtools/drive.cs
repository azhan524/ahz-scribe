using System;
using System.Runtime.InteropServices;
using System.Text;

// 开发用：往 Ahz Scribe 主窗口的多行输入框写文字并回车。用法: drive.exe "任务"
class Drive
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc cb, IntPtr l);

    delegate bool EnumProc(IntPtr h, IntPtr l);

    static IntPtr _edit = IntPtr.Zero;
    static IntPtr _target = IntPtr.Zero;
    static uint _wantPid;

    static bool Top(IntPtr h, IntPtr l)
    {
        if (!IsWindowVisible(h)) return true;
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid != _wantPid) return true;
        StringBuilder sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        if (sb.ToString().IndexOf("Ahz Scribe") < 0) return true;
        _target = h;
        return false;
    }

    static bool Child(IntPtr h, IntPtr l)
    {
        if (!IsWindowVisible(h)) return true;
        StringBuilder sb = new StringBuilder(64);
        GetClassName(h, sb, 64);
        string up = sb.ToString().ToUpperInvariant();
        if (up.Contains("EDIT") && !up.Contains("RICHEDIT") && (GetWindowLong(h, -16) & 0x0004) != 0)
        {
            _edit = h;
            return false;
        }
        return true;
    }

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("用法: drive.exe <pid> \"任务\""); return 1; }
        _wantPid = uint.Parse(args[0]);
        EnumWindows(Top, IntPtr.Zero);
        if (_target == IntPtr.Zero) { Console.WriteLine("找不到 Ahz Scribe 窗口"); return 2; }
        EnumChildWindows(_target, Child, IntPtr.Zero);
        if (_edit == IntPtr.Zero) { Console.WriteLine("找不到输入框"); return 3; }
        SendMessage(_edit, 0x000C, IntPtr.Zero, args[1]);   // WM_SETTEXT
        System.Threading.Thread.Sleep(300);
        PostMessage(_edit, 0x0100, (IntPtr)13, IntPtr.Zero); // Enter
        PostMessage(_edit, 0x0101, (IntPtr)13, IntPtr.Zero);
        Console.WriteLine("已投递: " + args[1]);
        return 0;
    }
}
