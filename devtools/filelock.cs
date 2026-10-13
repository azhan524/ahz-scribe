using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// 文件锁诊断 + 安全终止（对应升级需求 P0-1）
//
//   用法：
//     filelock.exe <文件路径>              只报告谁锁着（退出码 0=没锁 1=被锁）
//     filelock.exe <文件路径> --kill       先试着优雅关闭，超时再强杀
//     filelock.exe <文件路径> --kill --quiet
//
// 判定"被锁"的办法：独占打开一次。打不开就是有别的进程占着。
// 找出是谁：按文件名匹配进程（Windows 下没有公开 API 能查"谁开着这个句柄"，
// 按名字 + 主模块路径匹配对本项目足够用 —— 锁输出的基本都是自己的 exe）。
class FileLockCheck
{
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    const uint WM_CLOSE = 0x0010;

    static bool Locked(string path)
    {
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                return false;
        }
        catch (UnauthorizedAccessException) { return true; }   // 权限问题也当"打不开"
        catch (IOException) { return true; }                   // 正被占用
        catch { return false; }
    }

    // 候选进程：可执行文件名和 target 同名，或主模块路径就是 target
    static List<Process> Candidates(string path)
    {
        List<Process> hits = new List<Process>();
        string full = Path.GetFullPath(path);
        string baseName = Path.GetFileNameWithoutExtension(full);
        Process[] all;
        try { all = Process.GetProcesses(); } catch { return hits; }
        foreach (Process p in all)
        {
            try
            {
                string name = p.ProcessName;
                string modPath = null;
                try { modPath = p.MainModule == null ? null : p.MainModule.FileName; } catch { }
                bool byPath = modPath != null && string.Equals(modPath, full, StringComparison.OrdinalIgnoreCase);
                bool byName = string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase);
                if (byPath || byName) hits.Add(p);
                else p.Dispose();
            }
            catch { try { p.Dispose(); } catch { } }
        }
        return hits;
    }

    static string Describe(Process p)
    {
        string title = "";
        try { title = p.MainWindowTitle; } catch { }
        string started = "";
        try { started = p.StartTime.ToString("yyyy/MM/dd HH:mm:ss"); } catch { }
        return "  PID " + p.Id + "  " + p.ProcessName + ".exe"
            + (started.Length > 0 ? "  (启动于 " + started + ")" : "")
            + (title.Length > 0 ? "\n        窗口标题：" + title : "");
    }

    // 优雅关闭：先给它主窗口发 WM_CLOSE，等 timeoutMs；不行再 Kill
    static bool TryClose(Process p, int timeoutMs)
    {
        try
        {
            if (p.MainWindowHandle != IntPtr.Zero)
            {
                PostMessage(p.MainWindowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                if (p.WaitForExit(timeoutMs)) return true;
            }
        }
        catch { }
        try
        {
            p.Kill();
            p.WaitForExit(3000);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  [!] 强杀失败：" + ex.Message);
            return false;
        }
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0)
        {
            Console.WriteLine("用法: filelock.exe <文件路径> [--kill] [--quiet] [--wait 秒]");
            return 2;
        }
        string path = args[0];
        bool kill = false, quiet = false;
        int waitSec = 5;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--kill") kill = true;
            else if (args[i] == "--quiet") quiet = true;
            else if (args[i] == "--wait" && i + 1 < args.Length) int.TryParse(args[++i], out waitSec);
        }

        if (!File.Exists(path))
        {
            if (!quiet) Console.WriteLine("[i] 文件不存在：" + path + "（没锁的问题）");
            return 0;
        }

        if (!Locked(path))
        {
            if (!quiet) Console.WriteLine("[√] 没被锁：" + path);
            return 0;
        }

        Console.WriteLine("[X] 被锁住了：" + path);
        List<Process> hits = Candidates(path);
        if (hits.Count == 0)
        {
            Console.WriteLine("  [!] 按文件名没找到对应进程。可能是被别的程序（杀毒/索引器/编辑器）暂时占用，稍后重试。");
            return 1;
        }
        Console.WriteLine("  锁定它的进程：");
        foreach (Process p in hits) Console.WriteLine(Describe(p));

        if (!kill) return 1;

        Console.WriteLine("[*] 正在安全终止（先发 WM_CLOSE，最多等 " + waitSec + " 秒，再强杀）...");
        bool allOk = true;
        foreach (Process p in hits)
        {
            Console.WriteLine("  → 处理 PID " + p.Id + " " + p.ProcessName);
            if (!TryClose(p, waitSec * 1000)) allOk = false;
        }
        // 给系统一点时间释放句柄再复查
        for (int i = 0; i < 10; i++)
        {
            if (!Locked(path)) break;
            Thread.Sleep(200);
        }
        if (Locked(path))
        {
            Console.WriteLine("[!] 还是被锁着 —— 可能还有别的进程占着。");
            return 1;
        }
        Console.WriteLine("[√] 已解锁：" + path + (allOk ? "" : "（有的进程是强杀的）"));
        return 0;
    }
}
