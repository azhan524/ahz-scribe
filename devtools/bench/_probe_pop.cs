using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 开发用：直接看自绘浮层弹出来之后，主窗口是不是被"失活"了（失活会自动关掉浮层，得先确认）
class ProbePop
{
    static object Get(object t, string f)
    {
        return t.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(t);
    }
    static object Call(object t, string m, params object[] a)
    {
        return t.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(t, a);
    }
    static void Pump(int ms)
    {
        DateTime end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    [STAThread]
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();
        MainForm f = new MainForm(false);
        f.WindowState = FormWindowState.Normal;
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.Show();
        Pump(600);
        f.Activate();
        Pump(300);

        int deact = 0;
        f.Deactivate += delegate { deact++; };
        ModelChip chip = (ModelChip)Get(f, "cmbModel");
        Console.WriteLine("chip 模型数 = " + chip.Items.Count + "  chip.Text=「" + chip.Text + "」");

        // 不走 MainForm 的 OnDeactivate 自动关闭，只造一个浮层看它弹不弹得出来
        string[] labels, groups; int cur;
        object[] args = new object[] { null, null, 0 };
        // BuildModelGroups 是 out 参数，这里直接手搓一份
        labels = new string[] { "a:latest", "b:latest", "deepseek-flash" };
        groups = new string[] { "本地", "本地", "云端" };
        cur = 0;

        ModelPopover pop = new ModelPopover(labels, groups, cur, 8);
        pop.ShowAbove(chip);
        Pump(400);
        Console.WriteLine("浮层 Visible=" + pop.Visible + "  Handle=" + pop.Handle + "  区域=" + pop.Bounds);
        Console.WriteLine("主窗口失活次数 = " + deact + "  主窗口 ActiveForm= " + (Form.ActiveForm == null ? "null" : Form.ActiveForm.GetType().Name));
        Console.WriteLine("ShowWithoutActivation 生效？" + (Form.ActiveForm != pop));
        Console.WriteLine("浮层行数 = " + pop.Pick.RowCount + " 标题=" + string.Join("/", pop.Pick.Headers.ToArray())
            + " 当前行=" + pop.Pick.CurrentRow + " 打勾=" + pop.Pick.CheckedCount);
        PopoverForm pf = pop;
        Console.WriteLine("浮层 CreateParams.ExStyle 含 WS_EX_TOOLWINDOW = "
            + ((pf.GetType().GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic) != null) ? "检查代码" : "?"));
        pop.Close();
        Pump(200);
        f.Close();
    }
}
