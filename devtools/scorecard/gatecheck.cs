using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// 记分卡的"护栏"维度：不调用模型，直接验闸门和止损逻辑
class GateCheck
{
    static int pass, fail;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) { pass++; Console.WriteLine("  [PASS] " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + "  -> " + detail); }
    }

    class FakeUI : IAgentUI
    {
        public int Answer = 1;
        public int Asked;
        public void Write(OutKind k, string t) { }
        public int Approve(string n, string[] p) { Asked++; return Answer; }
        public int ApproveOutside(string n, string[] p) { Asked++; return Answer; }
        public void Status(string t) { }
    }

    static string Exec(AgentRunner r, string name, Dictionary<string, object> args, bool autoYes, out bool ok)
    {
        MethodInfo mi = typeof(AgentRunner).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        object[] p = new object[] { name, args, autoYes, null, null };
        mi.Invoke(r, p);
        ok = (bool)p[4];
        return (string)p[3];
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Console.OutputEncoding = Encoding.UTF8;
        string root = AppDomain.CurrentDomain.BaseDirectory;
        AgentConfig cfg = AgentConfig.Load();
        cfg.Migrate();
        cfg.cwd = root;
        cfg.auto_yes = false;

        Console.WriteLine("== 护栏 ==");
        // 1) 拒绝 -> 绝不执行
        FakeUI u1 = new FakeUI();
        u1.Answer = 0;
        AgentRunner r1 = new AgentRunner(cfg, u1);
        Dictionary<string, object> a1 = new Dictionary<string, object>();
        a1["command"] = "echo gate";
        bool ok1;
        string s1 = Exec(r1, "run_shell", a1, false, out ok1);
        Check("拒绝后不执行", u1.Asked == 1 && !ok1 && s1.IndexOf("拒绝") >= 0, s1);

        // 2) 允许 -> 才执行
        FakeUI u2 = new FakeUI();
        AgentRunner r2 = new AgentRunner(cfg, u2);
        Dictionary<string, object> a2 = new Dictionary<string, object>();
        a2["command"] = "echo gate-ran";
        bool ok2;
        string s2 = Exec(r2, "run_shell", a2, false, out ok2);
        Check("允许后执行", u2.Asked == 1 && ok2 && s2.IndexOf("gate-ran") >= 0, s2.Trim());

        // 3) 完全权限 -> 不问直接执行
        FakeUI u3 = new FakeUI();
        AgentRunner r3 = new AgentRunner(cfg, u3);
        Dictionary<string, object> a3 = new Dictionary<string, object>();
        a3["command"] = "echo auto";
        bool ok3;
        Exec(r3, "run_shell", a3, true, out ok3);
        Check("完全权限不问直接跑", u3.Asked == 0 && ok3, "asked=" + u3.Asked);

        // 4) 只读工具不问
        FakeUI u4 = new FakeUI();
        AgentRunner r4 = new AgentRunner(cfg, u4);
        Dictionary<string, object> a4 = new Dictionary<string, object>();
        a4["path"] = ".";
        bool ok4;
        Exec(r4, "list_dir", a4, false, out ok4);
        Check("只读工具不问", u4.Asked == 0 && ok4, "asked=" + u4.Asked);

        // 5) 危险命令直接拦
        FakeUI u5 = new FakeUI();
        AgentRunner r5 = new AgentRunner(cfg, u5);
        Dictionary<string, object> a5 = new Dictionary<string, object>();
        a5["command"] = "format c: /q";
        bool ok5;
        string s5 = Exec(r5, "run_shell", a5, true, out ok5);
        Check("危险命令被拦", !ok5 && s5.IndexOf("安全规则") >= 0, s5);

        // 6) 工作目录外：**没人批准就必须读不到**。
        //    2026-10-12 起语义从"一堵墙"改成"要授权"：拒绝要拦住、明确批准才放行，
        //    所以这里两条都要测（只测"读不到"会漏掉"批准也没用"这种反向故障）。
        cfg.allow_outside = false;
        Dictionary<string, object> a6 = new Dictionary<string, object>();
        a6["path"] = @"C:\Windows\System32\drivers\etc\hosts";

        FakeUI u6 = new FakeUI();
        u6.Answer = 0;                                  // 模拟用户点「拒绝」
        AgentRunner r6 = new AgentRunner(cfg, u6);
        bool ok6;
        string s6 = Exec(r6, "read_file", a6, false, out ok6);
        Check("沙箱外路径在用户拒绝时读不到", !ok6 && s6.IndexOf("超出工作目录") >= 0, s6);

        FakeUI u6b = new FakeUI();
        u6b.Answer = 1;                                 // 模拟用户明确批准
        AgentRunner r6b = new AgentRunner(cfg, u6b);
        bool ok6b;
        Exec(r6b, "read_file", a6, false, out ok6b);
        Check("用户批准后越权访问才放行", ok6b, "被批准了但还是读不到");

        // 7) 重复失败护栏 / 卡住计数 / 泄漏识别（纯逻辑）
        Check("卡住计数：成功清零、失败累加",
            AgentRunner.NextStallCount(2, true) == 0 && AgentRunner.NextStallCount(1, false) == 2, "");
        Check("能识别工具语法泄漏",
            AgentRunner.LooksLikeToolMarkup("<|DSML|tool_calls>") && !AgentRunner.LooksLikeToolMarkup("正常中文回答 a<b"), "");

        Console.WriteLine("GATE: " + pass + "/" + (pass + fail));
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
