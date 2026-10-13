using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 把输入条上那两张真实卡片截出来（强度卡 / 模型列表 / 权限卡），用来和参考图逐像素对比
//   用法: shotcards.exe 前缀 → 前缀-effort.png / 前缀-models.png / 前缀-perm.png
class ShotCards
{
    static object Get(object t, string f)
    {
        return t.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(t);
    }

    static void Pump(int ms)
    {
        DateTime end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    static void Call(object t, string m)
    {
        // 注意：同名重载会让 GetMethod(name) 抛 AmbiguousMatchException，这里明确要"无参那个"
        MethodInfo mi = t.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (mi != null) mi.Invoke(t, null);
    }

    // 当前打开着的浮层窗体（ModelPopover / PermPopover 都是 Form）
    static Form Popover(MainForm main, string kind)
    {
        Form found = null;
        foreach (Form w in Application.OpenForms)
            if (w != main && w.GetType().Name.IndexOf(kind) >= 0 && w.Visible) found = w;   // 取最后一个
        return found;
    }

    static void Shot(Form pop, string path, int extraPad)
    {
        if (pop == null) { Console.WriteLine("没找到浮层，跳过 " + path); return; }
        // 直接让窗口把自己画出来（不信屏幕：截屏会被别的窗口/桌面盖住）
        using (Bitmap bmp = new Bitmap(Math.Max(1, pop.Width), Math.Max(1, pop.Height)))
        {
            pop.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("已保存 " + path + "  " + pop.Width + "x" + pop.Height + "  位置=" + pop.Bounds);
    }

    [STAThread]
    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string pre = args.Length > 0 ? args[0] : "cards";
        AppDomain.CurrentDomain.UnhandledException += delegate(object s2, UnhandledExceptionEventArgs a2)
        {
            Exception ex2 = a2.ExceptionObject as Exception;
            Console.WriteLine("[未处理] " + (ex2 == null ? Convert.ToString(a2.ExceptionObject) : ex2.GetType().Name + ": " + ex2.Message));
        };
        Application.ThreadException += delegate(object s2, ThreadExceptionEventArgs a2)
        {
            Console.WriteLine("[界面线程] " + a2.Exception.GetType().Name + ": " + a2.Exception.Message);
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();

        MainForm f = new MainForm(false);
        AgentConfig cfg = (AgentConfig)Get(f, "config");
        cfg.providers.Clear();
        Provider po = Provider.DefaultOllama();
        po.models = new List<string>(new string[] { "qwen3.5:4b", "azhan_v10:latest", "qwen3:4b" });
        cfg.providers.Add(po);
        Provider pd = new Provider();
        pd.name = "deepseek"; pd.type = "openai";
        pd.base_url = "https://api.deepseek.com/v1"; pd.api_key = "x";
        // deepseek-reasoner 在白名单里 → 会显示滑块（要看滑杆那张卡）
        pd.models = new List<string>(new string[] { "deepseek-reasoner", "deepseek-v4-pro", "deepseek-chat" });
        cfg.providers.Add(pd);
        cfg.provider = pd.id; cfg.model = "deepseek-reasoner";
        cfg.effort = "high";
        f.ShowInTaskbar = false;
        f.WindowState = FormWindowState.Maximized;
        f.Show();
        Pump(1100);
        f.GetType().GetMethod("LoadModels", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f, null);
        Pump(400);

        // 1) 强度卡（滑块那页）
        Call(f, "ShowModelPopover");
        Pump(900);
        Form pop = Popover(f, "ModelPopover");
        Shot(pop, pre + "-effort.png", 10);

        // 2) 同一张浮层换到模型列表页
        if (pop != null)
        {
            MethodInfo sp = pop.GetType().GetMethod("ShowPage", new Type[] { typeof(int), typeof(bool) });
            if (sp != null) sp.Invoke(pop, new object[] { 1, false });
            Pump(700);
        }
        Shot(Popover(f, "ModelPopover"), pre + "-models.png", 10);

        // 3) 权限卡
        Call(f, "CloseModelPopover");
        Pump(300);
        try { Call(f, "TogglePermPopover"); }
        catch (TargetInvocationException te)
        {
            Exception in2 = te.InnerException == null ? te : te.InnerException;
            Console.WriteLine("权限浮层抛异常：" + in2.GetType().Name + ": " + in2.Message);
        }
        Pump(900);
        Shot(Popover(f, "PermPopover"), pre + "-perm.png", 10);

        Pump(200);
        f.Close();
    }
}
