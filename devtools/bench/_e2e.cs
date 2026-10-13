using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 端到端：用本机真实模型（Ollama）跑一整轮，检查新对话区在真实流式输出下的表现。
// 用法: e2e.exe [提示词] [最多等多少秒]
class E2E
{
    static int pass, fail;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) { pass++; Console.WriteLine("  [PASS] " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + "  -> " + detail); }
    }

    static object Get(object t, string f)
    {
        return t.GetType().GetField(f, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(t);
    }

    static void SetProp(object o, string name, object v)
    {
        o.GetType().GetProperty(name).SetValue(o, v, null);
    }

    static void Pump(int ms)
    {
        DateTime end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    static void Main(string[] args)
    {
        try { Body(args); }
        catch (Exception ex)
        {
            Console.WriteLine("EX " + ex.GetType().FullName + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            Environment.Exit(2);
        }
    }

    static void Body(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string prompt = args.Length > 0 ? args[0] : "用一句话回答：1+1 等于几？";
        int budget = args.Length > 1 ? int.Parse(args[1]) : 180;
        string wantProvider = args.Length > 2 ? args[2] : null;
        string wantModel = args.Length > 3 ? args[3] : null;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        MainForm f = new MainForm(false);
        f.ShowInTaskbar = false;
        f.TopMost = true;
        f.Show();
        f.WindowState = FormWindowState.Maximized;
        Pump(1500);

        TranscriptView rtb = (TranscriptView)Get(f, "rtb");
        TranscriptView trace = (TranscriptView)Get(f, "trace");
        TextBox input = (TextBox)Get(f, "txtInput");
        object cfg = Get(f, "config");
        rtb.ClearAll();
        trace.ClearAll();
        Pump(200);

        // 跑真实模型：工作目录挪到临时沙箱，自动批准工具，开思考 + 思考只进轨迹页
        string ws = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "e2e-ws");
        System.IO.Directory.CreateDirectory(ws);
        SetProp(cfg, "cwd", ws);
        SetProp(cfg, "auto_yes", true);
        SetProp(cfg, "think", true);
        SetProp(cfg, "hide_thinking", true);
        if (!string.IsNullOrEmpty(wantProvider)) SetProp(cfg, "provider", wantProvider);
        if (!string.IsNullOrEmpty(wantModel)) SetProp(cfg, "model", wantModel);
        if (!string.IsNullOrEmpty(wantProvider) || !string.IsNullOrEmpty(wantModel))
        {
            // 主窗体的 runner 是构造时按当时配置建好的，换了提供商得重建，否则还在连老的那个
            AgentRunner nr = new AgentRunner((AgentConfig)cfg, f);
            nr.SetWorkingDir(ws);
            f.GetType().GetField("runner", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f, nr);
        }
        Console.WriteLine("模型: " + GetProp(cfg, "provider") + " / " + GetProp(cfg, "model")
            + "  think=" + GetProp(cfg, "think") + "  hide_thinking=" + GetProp(cfg, "hide_thinking"));
        Console.WriteLine("提示词: " + prompt);

        input.Text = prompt;
        int fl0 = rtb.Flushes;
        DateTime t0 = DateTime.Now;
        f.GetType().GetMethod("SendMessage", BindingFlags.Instance | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null).Invoke(f, null);
        Pump(1200);
        bool heroShown = ((Control)Get(f, "heroPanel")).Visible;
        Check("发送后空状态大标题没有盖回来", !heroShown, "hero.Visible=" + heroShown);

        int spins = 0;
        while ((bool)Get(f, "running") && (DateTime.Now - t0).TotalSeconds < budget)
        {
            Pump(300); spins++;
        }
        bool stillRunning = (bool)Get(f, "running");
        double secs = (DateTime.Now - t0).TotalSeconds;
        Pump(1500);

        string all = rtb.AllText();
        int blocks = rtb.BlockCount;
        int flushes = rtb.Flushes - fl0;
        Console.WriteLine("用时 " + secs.ToString("0.0") + "s  对话块数=" + blocks + "  写入批次=" + flushes
            + "  对话字数=" + all.Length + "  轨迹字数=" + trace.AllText().Length);
        Console.WriteLine("---- 对话区内容 ----");
        Console.WriteLine(all.Length > 900 ? all.Substring(0, 900) + "…" : all);
        Console.WriteLine("---- 结束 ----");

        Check("整轮跑完（没被掐断）", !stillRunning, "stillRunning=" + stillRunning);
        Check("对话区有用户消息", all.IndexOf(prompt.Substring(0, Math.Min(6, prompt.Length))) >= 0, "len=" + all.Length);
        Check("对话区有回答（>=2 个块）", blocks >= 2, "blocks=" + blocks);
        Check("没有报错（连不上模型/异常）",
            all.IndexOf("出错") < 0 && all.IndexOf("连接不上") < 0 && all.IndexOf("还没有可用") < 0
            && all.IndexOf("×") < 0, Tail(all, 60));
        List<MsgBlock> bs = (List<MsgBlock>)Get(rtb, "_blocks");
        int thinkBlocks = 0;
        foreach (MsgBlock b in bs) if (b.Kind == OutKind.Thinking) thinkBlocks++;
        Check("思考内容没进对话区（hide_thinking=开）", thinkBlocks == 0, "chatThinkingBlocks=" + thinkBlocks);
        trace.FlushNow();
        Check("轨迹页拿得到内容（思考/步骤）", trace.AllText().Length > 0, "traceLen=" + trace.AllText().Length);

        f.Close();
        Console.WriteLine();
        Console.WriteLine("================ 真实模型端到端：通过 " + pass + " 项，失败 " + fail + " 项 ================");
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static object GetProp(object o, string name)
    {
        return o.GetType().GetProperty(name).GetValue(o, null);
    }

    static string Tail(string s, int n)
    {
        if (s == null) return "";
        return s.Length <= n ? s : s.Substring(s.Length - n).Replace("\n", "\\n");
    }
}
