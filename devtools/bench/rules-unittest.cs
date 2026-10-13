using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Reflection;
using System.Windows.Forms;

// 单元自测：项目规矩文件 + 验证命令识别 + 失败要点提取 + 系统提示注入
class RulesUnit
{
    static int pass, fail;

    static void Check(string name, bool ok, string detail)
    {
        if (ok) { pass++; Console.WriteLine("  [PASS] " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + "  -> " + detail); }
    }

    class FakeUI : IAgentUI
    {
        // 越权申请自测要能控制"用户点了哪个按钮"并数次数（默认仍是"允许"，不影响别的自测）
        internal static int Decision = 1;
        internal static int Calls = 0;
        internal static string LastTool = "";
        internal static StringBuilder Log = new StringBuilder();   // 断言界面上真的给了反馈
        public void Write(OutKind k, string t) { if (Log.Length < 20000) Log.Append(t); }
        public int ApproveOutside(string n, string[] p) { Calls++; LastTool = n; return Decision; }
        public int Approve(string n, string[] p) { Calls++; LastTool = n; return Decision; }
        public void Status(string t) { }
    }

    static string NewDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "rules-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        return d;
    }

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("== 1. 找规矩文件 ==");
        string d1 = NewDir();
        Check("没文件时返回 null", ProjectRules.Find(d1) == null, "");
        File.WriteAllText(Path.Combine(d1, "AGENTS.md"), "agents rules", Encoding.UTF8);
        Check("只有 AGENTS.md 也能找到", ProjectRules.Find(d1).EndsWith("AGENTS.md"), ProjectRules.Find(d1));
        File.WriteAllText(Path.Combine(d1, "AHZ.md"), "ahz rules", Encoding.UTF8);
        Check("AHZ.md 优先", ProjectRules.Find(d1).EndsWith("AHZ.md"), ProjectRules.Find(d1));

        Console.WriteLine("== 2. 验证命令：从规矩文件里读 ==");
        Check("中文冒号写法", ProjectRules.VerifyFromRules("验证命令：python -B run_tests.py") == "python -B run_tests.py", "");
        Check("英文冒号写法", ProjectRules.VerifyFromRules("验证命令: build.cmd") == "build.cmd", "");
        Check("markdown 列表项也认", ProjectRules.VerifyFromRules("- 验证命令: npm test") == "npm test", "");
        Check("verify: 也认", ProjectRules.VerifyFromRules("verify: make check") == "make check", "");
        Check("没写就返回 null", ProjectRules.VerifyFromRules("# 只有说明\n没有命令") == null, "");

        Console.WriteLine("== 3. 验证命令：自动识别 ==");
        string d2 = NewDir();
        Check("啥都没有就 null", ProjectRules.Detect(d2) == null, "");
        File.WriteAllText(Path.Combine(d2, "package.json"), "{ \"scripts\": { \"test\": \"jest\" } }", Encoding.UTF8);
        Check("认得 package.json", ProjectRules.Detect(d2) == "npm test --silent", ProjectRules.Detect(d2));
        File.WriteAllText(Path.Combine(d2, "build.cmd"), "echo hi", Encoding.UTF8);
        File.WriteAllText(Path.Combine(d2, "run_tests.py"), "print(1)", Encoding.UTF8);
        Check("run_tests.py 优先级最高", ProjectRules.Detect(d2) == "python -B run_tests.py", ProjectRules.Detect(d2));
        string d3 = NewDir();
        Directory.CreateDirectory(Path.Combine(d3, "tests"));
        File.WriteAllText(Path.Combine(d3, "tests", "test_x.py"), "def test_a(): pass", Encoding.UTF8);
        Check("认得 tests/ 目录", ProjectRules.Detect(d3) == "python -B -m unittest discover -s tests -q", ProjectRules.Detect(d3));
        string d4 = NewDir();
        File.WriteAllText(Path.Combine(d4, "Makefile"), "all:", Encoding.UTF8);
        Check("认得 Makefile", ProjectRules.Detect(d4) == "make", ProjectRules.Detect(d4));

        Console.WriteLine("== 4. 规矩文件太长会截断 ==");
        string d5 = NewDir();
        File.WriteAllText(Path.Combine(d5, "AHZ.md"), new string('x', 9000), Encoding.UTF8);
        string p;
        string t = ProjectRules.Read(d5, out p);
        Check("截断到 4000 字左右", t.Length < 4200 && t.IndexOf("已截断") >= 0, "len=" + t.Length);

        Console.WriteLine("== 5. 失败要点提取 ==");
        string fake = "共运行 12 个用例，失败 8 个\nFAIL: test_a (x.Y)\n一些无关的废话\nERROR: test_b\n"
            + "AssertionError: 1 != 2\n";
        string s = AgentRunner.FailureSummary(fake);
        Check("抓到了 FAIL/ERROR/失败数", s.IndexOf("FAIL: test_a") >= 0 && s.IndexOf("ERROR: test_b") >= 0
            && s.IndexOf("失败 8 个") >= 0, s.Replace("\n", " | "));
        Check("乱七八糟的没被抓成要点", s.IndexOf("无关的废话") < 0, "");
        Check("没有失败信息时返回空", AgentRunner.FailureSummary("全部通过\nALL TESTS PASSED") == "", "");

        Console.WriteLine("== 6. 判断模型自己跑过验证命令 ==");
        List<string> ran = new List<string>();
        ran.Add("python -B run_tests.py");
        Check("能认出自己跑过", AgentRunner.RanCommand(ran, "python -B run_tests.py"), "");
        ran.Clear();
        ran.Add("cd D:\\x && python -B run_tests.py 2>&1");
        Check("带前缀后缀也能认出", AgentRunner.RanCommand(ran, "python -B run_tests.py"), "");
        ran.Clear();
        ran.Add("dir");
        Check("没跑过就不认", !AgentRunner.RanCommand(ran, "python -B run_tests.py"), "");

        Console.WriteLine("== 7. 规矩进系统提示 ==");
        string d6 = NewDir();
        File.WriteAllText(Path.Combine(d6, "AHZ.md"),
            "# 规矩\n验证命令: python -B run_tests.py\n独特标记-ABCXYZ\n", Encoding.UTF8);
        AgentConfig cfg = AgentConfig.Load();
        cfg.Migrate();
        cfg.cwd = d6;
        cfg.auto_verify = true;
        AgentRunner r = new AgentRunner(cfg, new FakeUI());
        string sys = Json.Str(r.Messages[0], "content", "");
        Check("系统提示里有规矩内容", sys.IndexOf("独特标记-ABCXYZ") >= 0, "");
        Check("系统提示里提了验证命令", sys.IndexOf("python -B run_tests.py") >= 0, "");
        Check("系统提示里说明了会自动验证", sys.IndexOf("我会自动帮你在工作目录里跑一次项目验证命令") >= 0, "");
        cfg.auto_verify = false;
        AgentRunner r2 = new AgentRunner(cfg, new FakeUI());
        Check("关掉自动验证后不提验证", Json.Str(r2.Messages[0], "content", "").IndexOf("自动验证") < 0, "");

        Console.WriteLine("== 8. 编辑护栏：花括号配平（edit_file 落盘前挡住「改完才编不过」）==");
        // 直接数括号会被字符串/注释里的括号骗到，这里逐条钉住
        Check("配平的代码净差为 0", Tools.BraceBalance("class A { void B() { } }") == 0, "");
        Check("少一个右括号 -> +1", Tools.BraceBalance("class A { void B() { }") == 1, "");
        Check("多一个右括号 -> -1", Tools.BraceBalance("class A { } }") == -1, "");
        Check("普通字符串里的括号不算数", Tools.BraceBalance("x = \"{ }\";") == 0,
            "净差=" + Tools.BraceBalance("x = \"{ }\";"));
        Check("逐字字符串里的括号不算数", Tools.BraceBalance("x = @\"{ \"\" } }\";") == 0,
            "净差=" + Tools.BraceBalance("x = @\"{ \"\" } }\";"));
        Check("行注释里的括号不算数", Tools.BraceBalance("// { { {\nclass A { }") == 0,
            "净差=" + Tools.BraceBalance("// { { {\nclass A { }"));
        Check("块注释里的括号不算数", Tools.BraceBalance("/* } } } */ class A { }") == 0,
            "净差=" + Tools.BraceBalance("/* } } } */ class A { }"));
        Check("字符字面量里的括号不算数", Tools.BraceBalance("if (c == '{') { }") == 0,
            "净差=" + Tools.BraceBalance("if (c == '{') { }"));
        Check("转义引号不会把字符串提前截断", Tools.BraceBalance("s = \"a\\\"{ }\";") == 0,
            "净差=" + Tools.BraceBalance("s = \"a\\\"{ }\";"));

        // 真跑一遍 edit_file：少右括号必须被拒且文件不动
        string d7 = NewDir();
        string csFile = Path.Combine(d7, "T.cs");
        string original = "class T\n{\n    void A()\n    {\n    }\n}\n";
        File.WriteAllText(csFile, original, new UTF8Encoding(false));
        AgentConfig cfg7 = AgentConfig.Load();
        cfg7.Migrate();
        cfg7.cwd = d7;
        cfg7.auto_yes = true;
        ToolContext tctx = new ToolContext(d7, false, 30);
        Dictionary<string, object> eargs = new Dictionary<string, object>();
        eargs["path"] = csFile;
        eargs["old_string"] = "    void A()\n    {\n    }\n";
        eargs["new_string"] = "    void A()\n    {\n";            // 故意少一个 }
        string err = null;
        try { Tools.EditFile(tctx, eargs); }
        catch (ToolException ex) { err = ex.Message; }
        Check("少右括号的 edit 被拒绝", err != null && err.IndexOf("花括号") >= 0,
            err == null ? "居然写进去了" : err);
        Check("被拒后原文件一字未改", File.ReadAllText(csFile, Encoding.UTF8) == original,
            "内容变了：" + File.ReadAllText(csFile, Encoding.UTF8).Replace("\n", "\\n"));

        // 正常 edit 不受影响
        eargs["new_string"] = "    void A()\n    {\n        int z = 0;   // { 在注释里\n    }\n";
        object okMsg = null;
        try { okMsg = Tools.EditFile(tctx, eargs); }
        catch (ToolException ex) { okMsg = "被误拦：" + ex.Message; }
        Check("配平的正常 edit 照常放行（注释里的括号不误报）",
            Convert.ToString(okMsg).IndexOf("已修改") == 0
            && File.ReadAllText(csFile, Encoding.UTF8).IndexOf("int z = 0;") >= 0, Convert.ToString(okMsg));

        // 非 .cs 文件不该被这个护栏拦（比如改 md 里贴的代码片段）
        string mdFile = Path.Combine(d7, "notes.md");
        File.WriteAllText(mdFile, "说明\n", new UTF8Encoding(false));
        eargs["path"] = mdFile;
        eargs["old_string"] = "说明\n";
        eargs["new_string"] = "说明 { 半个括号\n";
        object mdMsg = null;
        try { mdMsg = Tools.EditFile(tctx, eargs); }
        catch (ToolException ex) { mdMsg = "被误拦：" + ex.Message; }
        Check("非 .cs 文件不受花括号护栏影响", Convert.ToString(mdMsg).IndexOf("已修改") == 0, Convert.ToString(mdMsg));

        Console.WriteLine("== 9. 快照持久化：两次编辑两个快照、恢复到第一个内容一致 ==");
        string d8 = NewDir();
        string target = Path.Combine(d8, "code.txt");
        // 第一次编辑
        File.WriteAllText(target, "版本一\n", new UTF8Encoding(false));
        List<FileChange> s1 = new List<FileChange>();
        FileChange f1 = new FileChange();
        f1.Path = target; f1.Rel = "code.txt"; f1.OldText = "版本零\n"; f1.NewText = "版本一\n";
        s1.Add(f1);
        string snap1 = SnapStore.Save(d8, s1, "第一次编辑");
        // 第二次编辑
        File.WriteAllText(target, "版本二\n", new UTF8Encoding(false));
        List<FileChange> s2 = new List<FileChange>();
        FileChange f2 = new FileChange();
        f2.Path = target; f2.Rel = "code.txt"; f2.OldText = "版本一\n"; f2.NewText = "版本二\n";
        s2.Add(f2);
        string snap2 = SnapStore.Save(d8, s2, "第二次编辑");

        Check("两次编辑产生两个快照文件", snap1 != null && snap2 != null && snap1 != snap2,
            "snap1=" + snap1 + " snap2=" + snap2);
        List<string> listed = SnapStore.List(d8);
        Check("列快照能列出两条", listed.Count == 2, "列出 " + listed.Count + " 条");
        Check("快照存在 .snapshots 目录里（不混进源码）",
            Directory.Exists(Path.Combine(d8, ".snapshots")), Path.Combine(d8, ".snapshots"));

        // 恢复到第一个快照：内容必须等于"第一次编辑之前"
        int okR, failR;
        string rmsg = SnapStore.Restore(d8, Path.GetFileNameWithoutExtension(snap1), out okR, out failR);
        string now = File.ReadAllText(target, Encoding.UTF8);
        Check("恢复第一个快照后内容与当时完全一致", now == "版本零\n",
            "恢复后=" + now.Replace("\n", "\\n") + "  返回=" + rmsg);
        Check("恢复报告了成功个数", okR == 1 && failR == 0, "ok=" + okR + " fail=" + failR);

        // 按前缀恢复（时间戳前缀），拿最近匹配的那个
        string rmsg2 = SnapStore.Restore(d8, Path.GetFileNameWithoutExtension(snap2), out okR, out failR);
        Check("按 id 恢复第二个快照 -> 版本一",
            File.ReadAllText(target, Encoding.UTF8) == "版本一\n", rmsg2 + " 内容=" + File.ReadAllText(target, Encoding.UTF8).Replace("\n", "\\n"));
        Check("恢复不存在的 id 会明确报错", SnapStore.Restore(d8, "不存在的快照", out okR, out failR).IndexOf("没找到") >= 0, "");

        // 保留策略：超过上限要砍最老的，不能像 .bak 一样越堆越多
        for (int k = 0; k < SnapStore.SNAP_KEEP + 5; k++)
        {
            List<FileChange> sk = new List<FileChange>();
            FileChange fk = new FileChange();
            fk.Path = target; fk.Rel = "code.txt"; fk.OldText = "x" + k + "\n"; fk.NewText = "";
            sk.Add(fk);
            SnapStore.Save(d8, sk, "压测 " + k);
        }
        int after = Directory.GetFiles(Path.Combine(d8, ".snapshots"), "*.json").Length;
        Check("快照数量被保留策略夹住（<= " + SnapStore.SNAP_KEEP + "）",
            after <= SnapStore.SNAP_KEEP, "实际 " + after + " 个");

        Console.WriteLine("== 10. tail_file：只看长日志的末尾（别整读）==");
        string d9 = NewDir();
        string logFile = Path.Combine(d9, "big.log");
        StringBuilder big = new StringBuilder();
        for (int i = 1; i <= 11000; i++) big.Append("第").Append(i).Append("行：日志内容 padding padding\n");
        File.WriteAllText(logFile, big.ToString(), new UTF8Encoding(false));
        ToolContext tctx9 = new ToolContext(d9, false, 30);
        Dictionary<string, object> targs = new Dictionary<string, object>();
        targs["path"] = logFile;
        string tail = Tools.TailFile(tctx9, targs);          // 默认 50 行
        string[] tl = tail.Replace("\r\n", "\n").Split('\n');
        // 第一行是标题、最后一行是"最后写入时间"，中间才是正文
        List<string> body = new List<string>();
        foreach (string ln in tl) if (ln.IndexOf('\t') >= 0) body.Add(ln);
        Check("默认只回末尾 50 行", body.Count == 50, "实际 " + body.Count + " 行");
        Check("带的是绝对行号（11000 行文件末尾从 10951 起）",
            body.Count > 0 && body[0].StartsWith("10951\t"), "首行=「" + (body.Count > 0 ? body[0] : "") + "」");
        Check("内容就是最后一行", body.Count > 0 && body[body.Count - 1].IndexOf("第11000行") >= 0,
            "末行=「" + (body.Count > 0 ? body[body.Count - 1] : "") + "」");
        Check("没有把整个文件塞进来", tail.Length < 8000, "长度=" + tail.Length);
        Check("带了最后写入时间（不用再 stat）", tail.IndexOf("最后写入时间") >= 0, "");
        targs["lines"] = 5;
        string tail5 = Tools.TailFile(tctx9, targs);
        int n5 = 0;
        foreach (string ln in tail5.Replace("\r\n", "\n").Split('\n')) if (ln.IndexOf('\t') >= 0) n5++;
        Check("lines=5 时只回 5 行", n5 == 5, "实际 " + n5);
        // 小文件（不足 N 行）不能崩，也不能漏
        string small = Path.Combine(d9, "small.log");
        File.WriteAllText(small, "只有一行\n", new UTF8Encoding(false));
        Dictionary<string, object> sargs = new Dictionary<string, object>();
        sargs["path"] = small;
        string tsmall = Tools.TailFile(tctx9, sargs);
        Check("行数不足时全部返回且不报错", tsmall.IndexOf("只有一行") >= 0, tsmall.Replace("\n", " | "));
        // 文件不存在要给出可读的错
        sargs["path"] = Path.Combine(d9, "nope.log");
        string terr = null;
        try { Tools.TailFile(tctx9, sargs); } catch (ToolException ex) { terr = ex.Message; }
        Check("文件不存在时报的是可读错误", terr != null && terr.IndexOf("不存在") >= 0, Convert.ToString(terr));

        Console.WriteLine("== 11. 任务清单：把模型写的计划抽出来、每步提醒、收尾对账 ==");
        List<AgentRunner.TaskItem> tasks = new List<AgentRunner.TaskItem>();
        string plan = "我打算这么做：\n"
            + "- [ ] 先读 AhzScribe.cs 找浮层代码\n"
            + "- [ ] 把宽度从 340 改成 220\n"
            + "- [x] 想清楚要改哪几处\n"
            + "然后开始动手。\n";
        Check("能解析出 3 步计划", AgentRunner.ParseTasks(plan, tasks) && tasks.Count == 3,
            "解析出 " + tasks.Count + " 步");
        Check("已完成的那步标成 Done", tasks.Count == 3 && tasks[2].Done && !tasks[0].Done, "");
        Check("完成数统计对", AgentRunner.TaskDoneCount(tasks) == 1, "完成 " + AgentRunner.TaskDoneCount(tasks));

        // 没变化的输出不该把清单清掉
        string again = "- [ ] 先读 AhzScribe.cs 找浮层代码\n- [ ] 把宽度从 340 改成 220\n- [x] 想清楚要改哪几处\n";
        Check("重复输出同一份计划不算变化", !AgentRunner.ParseTasks(again, tasks) && tasks.Count == 3, "");

        // 模型写完一步、把 x 打上
        string step2 = "- [x] 先读 AhzScribe.cs 找浮层代码\n- [ ] 把宽度从 340 改成 220\n- [x] 想清楚要改哪几处\n";
        Check("打了 x 之后算变化且完成数变 2",
            AgentRunner.ParseTasks(step2, tasks) && AgentRunner.TaskDoneCount(tasks) == 2,
            "完成 " + AgentRunner.TaskDoneCount(tasks));

        // 提醒里只列没做完的
        string nudge = AgentRunner.TaskNudge(tasks);
        Check("提醒里只剩 1 步待办", nudge != null && nudge.IndexOf("还剩 1 步") >= 0, Convert.ToString(nudge).Replace("\n", " | "));
        Check("提醒里不列已完成的步骤", nudge != null && nudge.IndexOf("先读 AhzScribe.cs") < 0, "");

        // 全做完就不再唠叨
        string allDone = "- [x] 先读 AhzScribe.cs 找浮层代码\n- [x] 把宽度从 340 改成 220\n- [x] 想清楚要改哪几处\n";
        AgentRunner.ParseTasks(allDone, tasks);
        Check("全做完时不再注入提醒", AgentRunner.TaskNudge(tasks) == null, Convert.ToString(AgentRunner.TaskNudge(tasks)));

        // 收尾对账
        List<AgentRunner.TaskItem> half = new List<AgentRunner.TaskItem>();
        AgentRunner.ParseTasks("- [x] 第一步\n- [ ] 第二步\n- [ ] 第三步\n", half);
        string sum = AgentRunner.TaskSummary(half);
        Check("没做完时收尾报出「计划 N 步完成 M 步」",
            sum != null && sum.IndexOf("计划了 3 步") >= 0 && sum.IndexOf("完成 1 步") >= 0, Convert.ToString(sum));
        Check("全做完时不报对账", AgentRunner.TaskSummary(tasks) == null, "");

        // 边界：正文里的方括号/代码块不该被当成计划
        List<AgentRunner.TaskItem> junk = new List<AgentRunner.TaskItem>();
        string noisy = "参数是 arr[ ] 这样传的。\n"
            + "```\n- [ ] 这段在代码块里\n```\n"
            + "数组下标 [x] 也不是计划\n"
            + "普通句子 [ ] 中间有括号\n";
        Check("正文里的方括号不会被误认成计划", !AgentRunner.ParseTasks(noisy, junk) && junk.Count == 0,
            "误认了 " + junk.Count + " 条");
        Check("空输出不产生计划", !AgentRunner.ParseTasks("", junk) && !AgentRunner.ParseTasks(null, junk), "");

        // 上限：列太多步要夹住
        StringBuilder many = new StringBuilder();
        for (int i = 1; i <= 40; i++) many.Append("- [ ] 第").Append(i).Append("步\n");
        List<AgentRunner.TaskItem> capped = new List<AgentRunner.TaskItem>();
        AgentRunner.ParseTasks(many.ToString(), capped);
        Check("计划条数被夹在 12 步以内", capped.Count == 12, "实际 " + capped.Count);

        // 序号写法也认
        List<AgentRunner.TaskItem> numbered = new List<AgentRunner.TaskItem>();
        Check("1. [ ] 这种序号写法也认",
            AgentRunner.ParseTasks("1. [ ] 第一步\n2. [x] 第二步\n", numbered) && numbered.Count == 2,
            "解析出 " + numbered.Count + " 步");

        Console.WriteLine("== 12. glob：按通配符找文件（以前只能一层层 list_dir）==");
        string d10 = NewDir();
        Directory.CreateDirectory(Path.Combine(d10, "src", "deep", "nested"));
        Directory.CreateDirectory(Path.Combine(d10, "docs"));
        File.WriteAllText(Path.Combine(d10, "a.cs"), "class A { }", Encoding.UTF8);
        File.WriteAllText(Path.Combine(d10, "README.md"), "# x", Encoding.UTF8);
        File.WriteAllText(Path.Combine(d10, "src", "b.cs"), "class B { }", Encoding.UTF8);
        File.WriteAllText(Path.Combine(d10, "src", "deep", "nested", "c.cs"), "class C { }", Encoding.UTF8);
        File.WriteAllText(Path.Combine(d10, "docs", "d.md"), "# y", Encoding.UTF8);
        ToolContext tctx10 = new ToolContext(d10, false, 30);
        Dictionary<string, object> gargs = new Dictionary<string, object>();

        gargs["pattern"] = "**/*.cs";
        string g1 = Tools.GlobTool(tctx10, gargs);
        Check("**/*.cs 能递归找到 3 个 cs（含根目录那个）",
            g1.IndexOf("a.cs") >= 0 && g1.IndexOf("b.cs") >= 0 && g1.IndexOf("c.cs") >= 0,
            g1.Replace("\n", " | "));
        Check("**/*.cs 不会带出 md", g1.IndexOf(".md") < 0, g1.Replace("\n", " | "));

        gargs["pattern"] = "*.cs";
        string g2 = Tools.GlobTool(tctx10, gargs);
        Check("*.cs 只匹配当前层（不跨目录）",
            g2.IndexOf("a.cs") >= 0 && g2.IndexOf("b.cs") < 0,
            g2.Replace("\n", " | "));

        gargs["pattern"] = "src/**/*.cs";
        string g3 = Tools.GlobTool(tctx10, gargs);
        Check("src/**/*.cs 只扫 src 下的（含深层）",
            g3.IndexOf("b.cs") >= 0 && g3.IndexOf("c.cs") >= 0 && g3.IndexOf("a.cs") < 0,
            g3.Replace("\n", " | "));

        gargs["pattern"] = "**/*.md";
        string g4 = Tools.GlobTool(tctx10, gargs);
        Check("**/*.md 找到两个 md", g4.IndexOf("README.md") >= 0 && g4.IndexOf("d.md") >= 0, g4.Replace("\n", " | "));

        gargs["pattern"] = "**/*.zzz";
        Check("没有任何匹配时给出可读提示", Tools.GlobTool(tctx10, gargs).IndexOf("没有匹配") >= 0, "");

        Console.WriteLine("== 13. outline：只列结构，不读正文 ==");
        string cs2 = Path.Combine(d10, "shape.cs");
        File.WriteAllText(cs2,
            "using System;\n"
            + "namespace N\n"
            + "{\n"
            + "    public class Foo\n"
            + "    {\n"
            + "        private int _n;\n"
            + "        public int Bar { get { return _n; } }\n"
            + "        public void Baz(int x)\n"
            + "        {\n"
            + "            if (x > 0) { _n = x; }\n"
            + "            for (int i = 0; i < 3; i++) { _n++; }\n"
            + "            Console.WriteLine(_n);\n"
            + "        }\n"
            + "    }\n"
            + "    interface IThing { void Go(); }\n"
            + "}\n", Encoding.UTF8);
        Dictionary<string, object> oargs = new Dictionary<string, object>();
        oargs["path"] = cs2;
        string o1 = Tools.OutlineTool(tctx10, oargs);
        Check("列到了 class Foo", o1.IndexOf("class Foo") >= 0, o1.Replace("\n", " | "));
        Check("列到了方法 Baz", o1.IndexOf("Baz") >= 0, o1.Replace("\n", " | "));
        Check("列到了属性 Bar", o1.IndexOf("Bar") >= 0, o1.Replace("\n", " | "));
        Check("列到了 interface IThing", o1.IndexOf("interface IThing") >= 0, o1.Replace("\n", " | "));
        Check("没有把 if / for / Console.WriteLine 当结构列出来",
            o1.IndexOf("if (x > 0)") < 0 && o1.IndexOf("for (int i") < 0 && o1.IndexOf("WriteLine") < 0,
            o1.Replace("\n", " | "));
        Check("带了行号（class Foo 在第 4 行）", o1.IndexOf("     4  ") >= 0 || o1.IndexOf("4  class Foo") >= 0,
            o1.Replace("\n", " | "));
        // 非 C 文件不该硬套结构识别
        oargs["path"] = Path.Combine(d10, "README.md");
        string o2 = Tools.OutlineTool(tctx10, oargs);
        Check("非 .cs 文件给出行数+前几行，不硬套结构", o2.IndexOf("不做结构识别") >= 0, o2.Replace("\n", " | "));
        // 不存在的文件
        oargs["path"] = Path.Combine(d10, "nope.cs");
        string oerr = null;
        try { Tools.OutlineTool(tctx10, oargs); } catch (ToolException ex) { oerr = ex.Message; }
        Check("outline 对不存在的文件报可读错误", oerr != null && oerr.IndexOf("不存在") >= 0, Convert.ToString(oerr));

        Console.WriteLine("== 14. write_file 体量护栏：整体覆盖前挡住「内容丢了一大半」==");
        // 判据本身（相对掉 30% 且绝对少 200 字符以上才拦）
        string big1000 = new string('x', 1000);
        Check("大文件砍掉 60% → 拦下", Tools.ShrinkReason(big1000, new string('x', 400)) != null,
            Convert.ToString(Tools.ShrinkReason(big1000, new string('x', 400))));
        Check("大文件只砍 10% → 放行（正常精简不该被拦）",
            Tools.ShrinkReason(big1000, new string('x', 900)) == null, "");
        Check("小文件（<200 字符）砍半 → 放行（误拦比漏拦更烦人）",
            Tools.ShrinkReason(new string('x', 120), new string('x', 40)) == null, "");
        Check("内容没变短 → 放行", Tools.ShrinkReason(big1000, big1000) == null, "");
        Check("内容变长 → 放行", Tools.ShrinkReason(big1000, big1000 + "y") == null, "");
        Check("清空整个大文件 → 拦下", Tools.ShrinkReason(big1000, "") != null, "");

        // 真跑一遍：拦下之后文件必须一字未动
        string d11 = NewDir();
        string bigFile = Path.Combine(d11, "big.txt");
        StringBuilder bb = new StringBuilder();
        for (int i = 0; i < 60; i++) bb.Append("第").Append(i).Append("行内容，用来把文件撑过 200 字符的阈值\n");
        string bigOrig = bb.ToString();
        File.WriteAllText(bigFile, bigOrig, new UTF8Encoding(false));
        ToolContext tctx11 = new ToolContext(d11, false, 30);
        AgentConfig cfg11 = AgentConfig.Load();
        cfg11.Migrate();
        cfg11.cwd = d11;
        Dictionary<string, object> wargs = new Dictionary<string, object>();
        wargs["path"] = bigFile;
        wargs["content"] = "只剩一行了\n";
        string werr = null;
        try { Tools.WriteFile(tctx11, wargs); }
        catch (ToolException ex) { werr = ex.Message; }
        Check("整体覆盖丢掉大半内容 → 被拒绝", werr != null && werr.IndexOf("拒绝整体覆盖") >= 0,
            Convert.ToString(werr));
        Check("被拒后原文件一字未改", File.ReadAllText(bigFile, Encoding.UTF8) == bigOrig, "");
        Check("报错里给了替代做法（edit_file / allow_shrink）",
            werr != null && werr.IndexOf("edit_file") >= 0 && werr.IndexOf("allow_shrink") >= 0,
            Convert.ToString(werr));

        // 显式 allow_shrink 就该放行（确实是用户要的精简）
        wargs["allow_shrink"] = true;
        string wok = null;
        try { wok = Tools.WriteFile(tctx11, wargs); }
        catch (ToolException ex) { wok = "被误拦：" + ex.Message; }
        Check("显式 allow_shrink=true → 放行", Convert.ToString(wok).IndexOf("覆盖写入") == 0, Convert.ToString(wok));
        Check("放行后内容确实换了", File.ReadAllText(bigFile, Encoding.UTF8) == "只剩一行了\n", "");

        // 新建文件不受这个护栏影响
        Dictionary<string, object> nargs = new Dictionary<string, object>();
        nargs["path"] = Path.Combine(d11, "brand-new.txt");
        nargs["content"] = "新文件内容\n";
        string nok = null;
        try { nok = Tools.WriteFile(tctx11, nargs); }
        catch (ToolException ex) { nok = "被误拦：" + ex.Message; }
        Check("新建文件不受体量护栏影响", Convert.ToString(nok).IndexOf("新建文件") == 0, Convert.ToString(nok));

        // 正常的小幅覆盖也不该被拦
        wargs["path"] = bigFile;
        wargs.Remove("allow_shrink");
        wargs["content"] = "只剩一行了，但加长一点点\n";
        string sok = null;
        try { sok = Tools.WriteFile(tctx11, wargs); }
        catch (ToolException ex) { sok = "被误拦：" + ex.Message; }
        Check("对已经很短的文件做覆盖 → 放行", Convert.ToString(sok).IndexOf("覆盖写入") == 0, Convert.ToString(sok));

        Console.WriteLine("== 15. 只读档：写类工具真被拦、读类照常 ==");
        // 先测判据本身
        Check("readonly 会被认成只读档", Perm.IsReadOnly("readonly") && !Perm.IsReadOnly("workspace") && !Perm.IsReadOnly("full"), "");
        Check("写类工具集合 = write_file/edit_file/run_shell",
            Tools.IsWriteTool("write_file") && Tools.IsWriteTool("edit_file") && Tools.IsWriteTool("run_shell"), "");
        Check("读类工具不算写类",
            !Tools.IsWriteTool("read_file") && !Tools.IsWriteTool("search_text") && !Tools.IsWriteTool("glob")
            && !Tools.IsWriteTool("outline") && !Tools.IsWriteTool("tail_file") && !Tools.IsWriteTool("list_dir"), "");

        // 走真实执行路径：ToolContext.ReadOnly 打开后，写类工具必须被挡
        string d12 = NewDir();
        AgentConfig cfg12 = AgentConfig.Load();
        cfg12.Migrate();
        cfg12.cwd = d12;
        cfg12.auto_yes = true;                    // 连"完全权限"都开着，看只读档是不是更硬
        cfg12.permission = "readonly";
        AgentRunner r12 = new AgentRunner(cfg12, new FakeUI());
        r12.Ctx = new ToolContext(d12, false, 30, true);
        string target12 = Path.Combine(d12, "victim.txt");
        File.WriteAllText(target12, "原内容，别动我\n", new UTF8Encoding(false));

        MethodInfo ex12 = typeof(AgentRunner).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        Check("拿得到 Execute 做端到端验证", ex12 != null, "反射没找到 Execute");

        Func<string, Dictionary<string, object>, string> run12 = delegate(string tool, Dictionary<string, object> aa)
        {
            object[] argv = new object[] { tool, aa, true, null, null };
            ex12.Invoke(r12, argv);
            string res = Convert.ToString(argv[3]);
            bool ok = argv[4] is bool && (bool)argv[4];
            return (ok ? "[ok] " : "[no] ") + res;
        };

        Dictionary<string, object> w12 = new Dictionary<string, object>();
        w12["path"] = target12;
        w12["content"] = "被改掉了\n";
        string wres12 = run12("write_file", w12);
        Check("只读档下 write_file 被拒绝", wres12.IndexOf("仅可查看") >= 0, wres12);
        Check("被拒后文件内容没变", File.ReadAllText(target12, Encoding.UTF8) == "原内容，别动我\n",
            File.ReadAllText(target12, Encoding.UTF8).Replace("\n", "\\n"));

        Dictionary<string, object> e12 = new Dictionary<string, object>();
        e12["path"] = target12;
        e12["old_string"] = "原内容";
        e12["new_string"] = "新内容";
        string eres12 = run12("edit_file", e12);
        Check("只读档下 edit_file 被拒绝", eres12.IndexOf("仅可查看") >= 0, eres12);
        Check("edit 被拒后文件还是没变", File.ReadAllText(target12, Encoding.UTF8) == "原内容，别动我\n", "");

        Dictionary<string, object> s12 = new Dictionary<string, object>();
        s12["command"] = "Write-Output hello";
        string sres12 = run12("run_shell", s12);
        Check("只读档下 run_shell 被拒绝（连无害命令也不行）", sres12.IndexOf("仅可查看") >= 0, sres12);

        Dictionary<string, object> rf12 = new Dictionary<string, object>();
        rf12["path"] = target12;
        string rres12 = run12("read_file", rf12);
        Check("只读档下 read_file 照常可用", rres12.IndexOf("仅可查看") < 0 && rres12.IndexOf("原内容") >= 0,
            rres12.Length > 120 ? rres12.Substring(0, 120) : rres12);

        string failed12 = null;
        try { Tools.GuardReadOnly(r12.Ctx, "write_file"); }
        catch (ToolException ex) { failed12 = ex.Message; }
        Check("闸门报错里写清了怎么解（去切权限档）",
            failed12 != null && failed12.IndexOf("权限档") >= 0, Convert.ToString(failed12));

        // 切回工作区档后必须立刻能写（不能一直卡着）
        r12.Ctx = new ToolContext(d12, false, 30, false);
        string w2res12 = run12("write_file", w12);
        Check("切回工作区档后 write_file 恢复可用", w2res12.IndexOf("仅可查看") < 0, w2res12);
        Check("恢复后内容真的写进去了", File.ReadAllText(target12, Encoding.UTF8) == "被改掉了\n", "");

        Console.WriteLine("== 16. 历史配对体检：压缩切歪不再弄坏会话（修 2026-10-12 真机 400）==");

        // 造一段工具密集的历史：assistant(tool_calls) → tool 结果，反复几轮，后面才出现 user
        List<Dictionary<string, object>> hist16 = new List<Dictionary<string, object>>();
        hist16.Add(AgentRunner.Msg("system", "base"));
        for (int i = 0; i < 6; i++)
        {
            hist16.Add(ToolCallMsg16("call_" + i, "read_file"));
            hist16.Add(ToolResultMsg16("call_" + i));
        }
        hist16.Add(AgentRunner.Msg("user", "新目标"));
        hist16.Add(AgentRunner.Msg("assistant", "好的"));

        int cut16 = AgentRunner.AlignKeepFrom(hist16, hist16.Count - 8);
        Check("切点被推到 user 消息上（不会落在 tool 结果中间）",
            Json.Str(hist16[cut16], "role", "") == "user",
            "落点 role=" + Json.Str(hist16[cut16], "role", "") + "（原始切点是 " + (hist16.Count - 8) + "）");

        int noUser16 = AgentRunner.AlignKeepFrom(hist16, hist16.Count - 1);
        Check("后半段没有 user 边界时返回 Count（调用方放弃这次压缩）",
            noUser16 == hist16.Count, "返回 " + noUser16 + " / 共 " + hist16.Count);

        // 复现真机那份坏历史：压缩后第一条就是孤立 tool 结果（它的 assistant 被切掉了）
        List<Dictionary<string, object>> broken16 = new List<Dictionary<string, object>>();
        broken16.Add(AgentRunner.Msg("system", "base"));
        broken16.Add(ToolResultMsg16("call_gone"));
        broken16.Add(ToolCallMsg16("call_ok", "edit_file"));
        broken16.Add(ToolResultMsg16("call_ok"));
        int removed16 = AgentRunner.DropOrphanTools(broken16);
        Check("孤立 tool 结果被摘掉", removed16 == 1 && broken16.Count == 3,
            "removed=" + removed16 + " 剩 " + broken16.Count);
        Check("配对的那条 tool 结果没被误删",
            Json.Str(broken16[2], "role", "") == "tool"
            && Json.Str(broken16[2], "tool_call_id", "") == "call_ok",
            Json.Str(broken16[2], "role", "") + "/" + Json.Str(broken16[2], "tool_call_id", ""));

        // 新切点压缩出来的历史必须干净
        List<Dictionary<string, object>> rebuilt16 = new List<Dictionary<string, object>>();
        rebuilt16.Add(hist16[0]);
        rebuilt16.Add(AgentRunner.Msg("system", "[早期对话摘要 · 自动生成]\n摘要正文"));
        for (int i = cut16; i < hist16.Count; i++) rebuilt16.Add(hist16[i]);
        Check("按新切点压缩出来的历史零孤立 tool 消息",
            AgentRunner.DropOrphanTools(rebuilt16) == 0, "还有孤立项");

        List<Dictionary<string, object>> healthy16 = new List<Dictionary<string, object>>();
        healthy16.Add(AgentRunner.Msg("system", "base"));
        healthy16.Add(ToolCallMsg16("call_h", "glob"));
        healthy16.Add(ToolResultMsg16("call_h"));
        Check("本来就健康的历史一条不删（体检不误伤）",
            AgentRunner.DropOrphanTools(healthy16) == 0 && healthy16.Count == 3,
            "剩 " + healthy16.Count);

        List<Dictionary<string, object>> halfBroken16 = new List<Dictionary<string, object>>();
        halfBroken16.Add(AgentRunner.Msg("system", "base"));
        halfBroken16.Add(AgentRunner.Msg("assistant", "我只说了句话，没调工具"));
        halfBroken16.Add(ToolResultMsg16("call_x"));
        Check("assistant 没声明 tool_calls 的 tool 结果也摘掉",
            AgentRunner.DropOrphanTools(halfBroken16) == 1 && halfBroken16.Count == 2,
            "剩 " + halfBroken16.Count);

        Console.WriteLine("== 17. 启动自检：低完整性 / 临时目录写不进要说清楚（不是 Key 不对）==");

        string d17 = NewDir();
        Check("能写的目录：TryWrite 返回 true", StartupCheck.TryWrite(d17), d17);
        Check("不存在的目录：TryWrite 返回 false",
            !StartupCheck.TryWrite(Path.Combine(d17, "definitely-missing")), "");
        Check("完整性级别认得出来（不崩、有名字）",
            StartupCheck.IntegrityName().Length > 0, "拿到的是 " + StartupCheck.IntegrityName());

        string pick17 = StartupCheck.PickTemp(d17, null);
        Check("PickTemp 造得出项目内 _build\\tmp",
            pick17 != null && Directory.Exists(pick17) && pick17.EndsWith("tmp"), Convert.ToString(pick17));
        Check("兜底目录真能写", pick17 != null && StartupCheck.TryWrite(pick17), Convert.ToString(pick17));

        Check("系统 TMP/TEMP 正常时不动它（返回 null）",
            StartupCheck.EnsureTemp(d17, null, d17, d17) == null, "不该兜底");

        string bad17 = Path.Combine(d17, "no-such-tmp");
        string fallback17 = StartupCheck.EnsureTemp(d17, null, bad17, bad17);
        Check("写不进时兜到 _build\\tmp", fallback17 != null && Directory.Exists(fallback17), Convert.ToString(fallback17));
        Check("兜底后进程 TMP/TEMP 真的改成它了",
            Environment.GetEnvironmentVariable("TMP") == fallback17
            && Environment.GetEnvironmentVariable("TEMP") == fallback17,
            Convert.ToString(Environment.GetEnvironmentVariable("TMP")));
        // 收尾：环境变量还回系统值，别影响后面/别的进程
        Environment.SetEnvironmentVariable("TMP", Path.GetTempPath(), EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TEMP", Path.GetTempPath(), EnvironmentVariableTarget.Process);

        Console.WriteLine("== 18. 越权申请：跑到工作目录外先问用户，而不是一刀切拒绝 ==");

        string d18 = NewDir();
        string outside18 = Path.Combine(Path.GetTempPath(),
            "outside-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");

        // 1) 用户允许 → 能解析出外面的路径，并且确实问过
        int asked18 = 0;
        string last18 = "";
        ToolContext c18 = new ToolContext(d18, false, 30);
        c18.AskOutside = delegate(string full) { asked18++; last18 = full; return 1; };
        string r18 = c18.Resolve(outside18);
        Check("允许后能解析工作目录外的路径",
            string.Equals(r18, outside18, StringComparison.OrdinalIgnoreCase), r18);
        Check("确实弹了询问", asked18 == 1 && last18.Length > 0, "问了 " + asked18 + " 次");

        // 2) 用户拒绝 → 仍然是拒绝，但报错里要说清"是用户没放行"，模型才不会反复重试
        ToolContext c18b = new ToolContext(d18, false, 30);
        c18b.AskOutside = delegate(string full) { return 0; };
        string err18 = null;
        try { c18b.Resolve(outside18); }
        catch (ToolException ex) { err18 = ex.Message; }
        Check("拒绝后依然挡住（没静默放行）",
            err18 != null && err18.IndexOf("路径超出工作目录") >= 0, Convert.ToString(err18));
        Check("报错里说清是用户没放行、别重试",
            err18 != null && err18.IndexOf("没放行") >= 0, Convert.ToString(err18));

        // 3) 选"本会话总是允许" → 这次放行，同一上下文后续不再问
        int asked18c = 0;
        ToolContext c18c = new ToolContext(d18, false, 30);
        c18c.AskOutside = delegate(string full) { asked18c++; return 2; };
        c18c.Resolve(outside18);
        c18c.Resolve(outside18);
        c18c.Resolve(Path.Combine(d18, "inside.txt"));
        Check("选「本会话总是允许」后不再重复问", asked18c == 1, "问了 " + asked18c + " 次");
        Check("本上下文的放行标志被置上", c18c.AllowOutside, "");

        // 4) 工作目录内的路径永远不打扰用户
        int asked18d = 0;
        ToolContext c18d = new ToolContext(d18, false, 30);
        c18d.AskOutside = delegate(string full) { asked18d++; return 1; };
        c18d.Resolve(Path.Combine(d18, "inside.txt"));
        Check("目录内路径不弹窗", asked18d == 0, "问了 " + asked18d + " 次");

        // 5) 没装申请通道（老的构造方式）→ 保持直接拒绝
        ToolContext c18e = new ToolContext(d18, false, 30);
        string err18e = null;
        try { c18e.Resolve(outside18); }
        catch (ToolException ex) { err18e = ex.Message; }
        Check("没装通道时仍是直接拒绝（不静默放行）",
            err18e != null && err18e.IndexOf("路径超出") >= 0, Convert.ToString(err18e));

        // 6) 端到端：走真实 Execute，看申请是不是真的接到了审批通道上
        File.WriteAllText(outside18, "越权读到的内容\n", new UTF8Encoding(false));
        AgentConfig cfg18 = AgentConfig.Load();
        cfg18.Migrate();
        cfg18.cwd = d18;
        cfg18.allow_outside = false;
        cfg18.auto_yes = true;                      // 连"完全权限"档都开着：越权还是得问
        AgentRunner runner18 = new AgentRunner(cfg18, new FakeUI());
        MethodInfo ex18m = typeof(AgentRunner).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        Func<Dictionary<string, object>, string> read18 = delegate(Dictionary<string, object> aa)
        {
            object[] argv = new object[] { "read_file", aa, true, null, null };
            ex18m.Invoke(runner18, argv);
            return Convert.ToString(argv[3]);
        };
        Dictionary<string, object> oa18 = new Dictionary<string, object>();
        oa18["path"] = outside18;

        FakeUI.Calls = 0;
        FakeUI.Decision = 1;
        string ok18 = read18(oa18);
        Check("用户批准时，端到端能读到工作目录外的文件",
            ok18.IndexOf("越权读到的内容") >= 0, ok18.Length > 120 ? ok18.Substring(0, 120) : ok18);
        Check("批准前确实走了审批通道（弹了一次）", FakeUI.Calls == 1, "弹了 " + FakeUI.Calls + " 次");
        Check("弹窗标题写清了是「工作目录外」的申请",
            FakeUI.LastTool.IndexOf("工作目录外") >= 0, FakeUI.LastTool);

        FakeUI.Calls = 0;
        FakeUI.Decision = 0;
        string no18 = read18(oa18);
        Check("用户拒绝时端到端读不到（内容没泄露）",
            no18.IndexOf("路径超出") >= 0 && no18.IndexOf("越权读到的内容") < 0,
            no18.Length > 120 ? no18.Substring(0, 120) : no18);
        Check("拒绝后同样弹了一次（不是默默失败）", FakeUI.Calls == 1, "弹了 " + FakeUI.Calls + " 次");
        FakeUI.Decision = 1;                        // 还原，免得影响别的自测
        try { File.Delete(outside18); } catch { }

        Console.WriteLine("== 19. 踩坑本：这次做错的，下次别再重来 ==");

        string d19 = NewDir();
        string lp19 = Lessons.PathOf(d19);
        Check("踩坑本落在 <工作目录>\\devtools\\lessons.json",
            lp19.EndsWith("devtools\\lessons.json"), lp19);

        Lessons.Add(d19, "未能写入输出文件,filelock", "build 报「未能写入输出文件」时不知道谁锁着",
            "先跑 devtools\\filelock.cmd 看占用进程，再决定等它退出还是走 .new");
        Check("新增后会真的落盘", File.Exists(lp19), lp19);
        Lessons.Add(d19, "未能写入输出文件,filelock", "重复记一条", "先跑 filelock.cmd 看占用进程");
        List<Lesson> l19 = Lessons.Load(d19);
        Check("同一组关键词只留一条（不重复堆积）", l19.Count == 1, "共 " + l19.Count + " 条");
        Check("重复出现时累加 hits（越常踩越不会被淘汰）", l19[0].hits == 1, "hits=" + l19[0].hits);

        Check("命中：用户消息里出现关键词就能查到",
            Lessons.Match(d19, "编译时又说未能写入输出文件了，怎么办", 3, false).Count == 1, "");
        Check("不命中：无关的话一条都不提醒（不刷屏）",
            Lessons.Match(d19, "今天天气不错", 3, false).Count == 0, "");
        string note19 = Lessons.Note(d19, "build 报未能写入输出文件怎么办", 3);
        Check("提醒里带上了坑的标题和处置",
            note19.IndexOf("未能写入输出文件") >= 0 && note19.IndexOf("filelock") >= 0, note19);

        // 自动记：同一个工具用同样的参数又失败了一次
        Dictionary<string, object> a19 = new Dictionary<string, object>();
        a19["path"] = "D:\\somewhere\\missing.txt";
        Check("能从参数里挑出认人用的特征值",
            Lessons.ValueTrigger(a19) == "D:\\somewhere\\missing.txt", Lessons.ValueTrigger(a19));
        Dictionary<string, object> a19b = new Dictionary<string, object>();
        a19b["path"] = ".";
        Check("挑不出特征值就不记（不造噪声）", Lessons.ValueTrigger(a19b) == "", "[" + Lessons.ValueTrigger(a19b) + "]");

        Lesson cap19 = Lessons.CaptureDuplicate(d19, "read_file", a19, "没找到文件：D:\\somewhere\\missing.txt");
        Check("重复失败会被记进踩坑本", cap19 != null && Lessons.Load(d19).Count == 2,
            "共 " + Lessons.Load(d19).Count + " 条");
        Check("记的坑写清了是哪个工具、什么错",
            cap19.title.IndexOf("read_file") >= 0 && cap19.title.IndexOf("没找到文件") >= 0, cap19.title);
        Check("下次同样的参数会提前提醒（跨会话）",
            Lessons.Warn(d19, Lessons.ArgsText(a19)).IndexOf("踩坑本提醒") >= 0, Lessons.Warn(d19, Lessons.ArgsText(a19)));
        Check("无关的调用不会被误提醒",
            Lessons.Warn(d19, Lessons.ArgsText(a19b)).Length == 0, Lessons.Warn(d19, Lessons.ArgsText(a19b)));
        Check("参数原文不做 JSON 转义（否则路径类关键词永远匹配不上）",
            Lessons.ArgsText(a19) == "D:\\somewhere\\missing.txt", Lessons.ArgsText(a19));

        for (int i = 0; i < Lessons.MaxItems + 5; i++)
            Lessons.Add(d19, "cap-trigger-" + i, "第 " + i + " 条占位", "x");
        Check("超过上限会自动瘦身（不会无限膨胀）",
            Lessons.Load(d19).Count == Lessons.MaxItems, "共 " + Lessons.Load(d19).Count + " 条");

        // /记住：用户显式教一条，别把这条当普通提问发给模型
        AgentConfig cfg19 = AgentConfig.Load();
        cfg19.Migrate();
        cfg19.cwd = d19;
        AgentRunner runner19 = new AgentRunner(cfg19, new FakeUI());
        MethodInfo h19 = typeof(AgentRunner).GetMethod("HandleLessonCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        FakeUI.Log.Length = 0;
        object handled19 = h19.Invoke(runner19, new object[] { "/记住 微信,自动回复 = 别硬来，直接说做不到" });
        Check("「/记住」被认出来（不当成普通提问发给模型）",
            handled19 is bool && (bool)handled19, Convert.ToString(handled19));
        Check("教的内容进了踩坑本",
            Lessons.Match(d19, "帮我在微信里自动回复消息", 3, false).Count == 1, "");
        Check("界面上给了「已记住」的反馈", FakeUI.Log.ToString().IndexOf("已记住") >= 0, FakeUI.Log.ToString());
        object plain19 = h19.Invoke(runner19, new object[] { "帮我看看 build.cmd 有没有问题" });
        Check("普通提问不会被命令吃掉", plain19 is bool && !(bool)plain19, Convert.ToString(plain19));

        Console.WriteLine("== 20. 后台任务：长活别再被前台 120 秒超时掐断 ==");

        string d20 = NewDir();
        ToolContext c20 = new ToolContext(d20, true, 30);

        BgJob j20 = Jobs.Start(d20, "Start-Sleep -Seconds 3; Write-Output 'MARKER-OK'; exit 7", "自测长任务");
        Check("起任务立刻返回任务号（不阻塞）", j20 != null && j20.id.Length > 0, j20 == null ? "null" : j20.id);
        string running20 = Jobs.Describe(j20, 5);
        Check("刚起来时状态是「运行中」", running20.IndexOf("运行中") >= 0, running20);

        bool done20 = false;
        int waited20 = 0;
        for (int i = 0; i < 40 && !done20; i++)
        {
            System.Threading.Thread.Sleep(500);
            waited20 += 500;
            done20 = j20.proc.HasExited;
        }
        Check("后台任务能自己跑到结束（不靠前台等）", done20, "等了 " + waited20 + " 毫秒");
        string st20 = Jobs.Describe(j20, 10);
        Check("退出码被记下来（7）", st20.IndexOf("退出码 7") >= 0, st20);
        Check("输出被收进日志（MARKER-OK）", st20.IndexOf("MARKER-OK") >= 0, st20);
        Check("日志落在工作目录的 _build\\jobs 里",
            File.Exists(j20.logPath) && j20.logPath.StartsWith(d20), j20.logPath);

        Dictionary<string, object> found20 = new Dictionary<string, object>();
        found20["task_id"] = j20.id.Substring(0, j20.id.IndexOf('-'));
        Check("短号（前缀）也能查到任务", Jobs.Status(c20, found20).IndexOf("退出码 7") >= 0, "");

        BgJob j20b = Jobs.Start(d20, "Start-Sleep -Seconds 120", "自测长驻");
        string kill20 = Jobs.Kill(j20b);
        System.Threading.Thread.Sleep(1200);
        Check("能按任务号结束还在跑的任务",
            kill20.IndexOf("已结束后台任务") >= 0 && Jobs.Describe(j20b, 1).IndexOf("手动终止") >= 0,
            kill20 + " / " + Jobs.Describe(j20b, 1));

        Dictionary<string, object> empty20 = new Dictionary<string, object>();
        string err20 = null;
        try { Jobs.Run(c20, empty20); }
        catch (ToolException ex) { err20 = ex.Message; }
        Check("命令为空会被挡下", err20 != null && err20.IndexOf("不能为空") >= 0, Convert.ToString(err20));

        Dictionary<string, object> miss20 = new Dictionary<string, object>();
        miss20["task_id"] = "99999-000000";
        string err20b = null;
        try { Jobs.Status(c20, miss20); }
        catch (ToolException ex) { err20b = ex.Message; }
        Check("查不存在的任务号会明确报错（不静默返回空）",
            err20b != null && err20b.IndexOf("没有这个任务号") >= 0, Convert.ToString(err20b));

        Check("只读档把 run_background 也当写类工具（和 run_shell 同级）",
            Tools.IsWriteTool("run_background") && Tools.IsWriteTool("run_shell"), "");

        // 危险命令在后台同样要被安全规则拦（Execute 里的筛查得覆盖新工具）
        AgentConfig cfg20 = AgentConfig.Load();
        cfg20.Migrate();
        cfg20.cwd = d20;
        cfg20.auto_yes = true;
        AgentRunner runner20 = new AgentRunner(cfg20, new FakeUI());
        MethodInfo ex20 = typeof(AgentRunner).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        Dictionary<string, object> bad20 = new Dictionary<string, object>();
        bad20["command"] = "format D: /q";
        object[] argv20 = new object[] { "run_background", bad20, true, null, null };
        ex20.Invoke(runner20, argv20);
        string badres20 = Convert.ToString(argv20[3]);
        Check("危险命令在后台同样被拦住", badres20.IndexOf("安全规则") >= 0, badres20);

        Console.WriteLine("== 21. 计划模式：只准调研、只准出计划，动手类工具一律拦住 ==");

        Check("写文件 / 跑命令在计划模式里被拦",
            Tools.IsPlanBlocked("write_file") && Tools.IsPlanBlocked("edit_file")
            && Tools.IsPlanBlocked("run_shell") && Tools.IsPlanBlocked("run_background"), "");
        Check("起程序 / 杀进程 / 停后台任务也算动手（不留后门）",
            Tools.IsPlanBlocked("start_program") && Tools.IsPlanBlocked("kill_process")
            && Tools.IsPlanBlocked("job_kill"), "");
        Check("纯调研类工具照常放行（不然没法出计划）",
            !Tools.IsPlanBlocked("read_file") && !Tools.IsPlanBlocked("list_dir")
            && !Tools.IsPlanBlocked("search_text") && !Tools.IsPlanBlocked("glob")
            && !Tools.IsPlanBlocked("outline") && !Tools.IsPlanBlocked("tail_file")
            && !Tools.IsPlanBlocked("job_status") && !Tools.IsPlanBlocked("list_processes"), "");

        string d21 = NewDir();
        string f21 = Path.Combine(d21, "plan.txt");
        File.WriteAllText(f21, "原内容\n", new UTF8Encoding(false));
        AgentConfig cfg21 = AgentConfig.Load();
        cfg21.Migrate();
        cfg21.cwd = d21;
        cfg21.auto_yes = true;              // 连"完全权限"都开着，计划模式也得拦得住
        cfg21.plan_mode = true;
        AgentRunner runner21 = new AgentRunner(cfg21, new FakeUI());
        Check("计划模式下系统提示写明了「只调研、别动手」",
            Convert.ToString(runner21.Messages[0]["content"]).IndexOf("【计划模式 · 已生效】") >= 0, "");

        MethodInfo ex21 = typeof(AgentRunner).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic);
        Func<string, Dictionary<string, object>, string> run21 = delegate(string tool, Dictionary<string, object> aa)
        {
            object[] argv = new object[] { tool, aa, true, null, null };
            ex21.Invoke(runner21, argv);
            return Convert.ToString(argv[3]);
        };
        Dictionary<string, object> w21 = new Dictionary<string, object>();
        w21["path"] = f21;
        w21["content"] = "被改掉了\n";
        string wr21 = run21("write_file", w21);
        Check("计划模式下 write_file 被拦，且说清了原因", wr21.IndexOf("计划模式") >= 0, wr21);
        Check("被拦之后文件一个字没动",
            File.ReadAllText(f21, Encoding.UTF8) == "原内容\n", File.ReadAllText(f21, Encoding.UTF8));
        Dictionary<string, object> s21 = new Dictionary<string, object>();
        s21["command"] = "Write-Output hi";
        string sr21 = run21("run_shell", s21);
        Check("计划模式下 run_shell 同样被拦（不能靠命令绕过去）",
            sr21.IndexOf("计划模式") >= 0, sr21);
        Dictionary<string, object> r21 = new Dictionary<string, object>();
        r21["path"] = f21;
        string rr21 = run21("read_file", r21);
        Check("调研类工具照常能用",
            rr21.IndexOf("原内容") >= 0 && rr21.IndexOf("计划模式") < 0,
            rr21.Length > 120 ? rr21.Substring(0, 120) : rr21);

        cfg21.plan_mode = false;
        AgentRunner runner21b = new AgentRunner(cfg21, new FakeUI());
        Check("关掉之后系统提示里不再有这一段",
            Convert.ToString(runner21b.Messages[0]["content"]).IndexOf("【计划模式 · 已生效】") < 0, "");

        Console.WriteLine("== 22. 交付记录导出：干了什么 / 改了哪些文件 / 跑了哪些验证 / 结论 ==");

        string d22 = NewDir();
        string f22 = Path.Combine(d22, "victim.cs");
        File.WriteAllText(f22, "第一行\n第二行\n第三行\n", new UTF8Encoding(false));

        AgentConfig cfg22 = AgentConfig.Load();
        cfg22.Migrate();
        cfg22.cwd = d22;
        AgentRunner runner22 = new AgentRunner(cfg22, new FakeUI());

        // 造一段真实形状的会话：用户要求 → 改文件 → 跑验证 → 结论
        runner22.Messages.Add(AgentRunner.Msg("user", "把 victim.cs 第二行改掉，然后跑一遍验证命令"));
        Dictionary<string, object> call22 = AgentRunner.Msg("assistant", "");
        List<object> calls22 = new List<object>();
        calls22.Add(ToolCall22("c1", "edit_file", "victim.cs"));
        calls22.Add(ToolCall22("c2", "run_shell", "cmd /c build.cmd"));
        call22["tool_calls"] = calls22;
        runner22.Messages.Add(call22);
        Dictionary<string, object> tr22 = AgentRunner.Msg("tool", "[edit_file 结果]\n已替换 1 处");
        tr22["tool_call_id"] = "c1";
        runner22.Messages.Add(tr22);
        Dictionary<string, object> tr22b = AgentRunner.Msg("tool", "[run_shell 结果]\n退出码 0\nBUILD_RESULT: UPDATED");
        tr22b["tool_call_id"] = "c2";
        runner22.Messages.Add(tr22b);
        runner22.Messages.Add(AgentRunner.Msg("assistant", "改完了：第二行换成了新内容，编译通过。"));
        // 框架注入的提醒不该混进"用户原话"
        runner22.Messages.Add(AgentRunner.Msg("user", "（系统提醒）你刚才只输出了计划"));
        Dictionary<string, object> finalMsg22 = AgentRunner.Msg("user", "再顺手确认一下没别的改动");
        runner22.Messages.Add(finalMsg22);

        // 落一份快照（框架每轮结束后就是这么做），好让导出能列出改动
        List<FileChange> fc22 = new List<FileChange>();
        FileChange one22 = new FileChange();
        one22.Rel = "victim.cs";
        one22.Path = f22;
        one22.OldText = "第一行\n第二行\n第三行\n";
        fc22.Add(one22);
        File.WriteAllText(f22, "第一行\n改过的第二行\n第三行\n", new UTF8Encoding(false));
        SnapStore.Save(d22, fc22, "已编辑 1 个文件");

        string doc22 = Deliver.Build(runner22, d22);
        Check("导出里有「用户原话」", doc22.IndexOf("把 victim.cs 第二行改掉") >= 0, "");
        Check("框架注入的系统提醒不算用户原话", doc22.IndexOf("你刚才只输出了计划") < 0, "");
        Check("导出里列出了改动的文件", doc22.IndexOf("`victim.cs`") >= 0, "");
        Check("改动带了增删行数", doc22.IndexOf("（+1 / −1 行）") >= 0,
            doc22.Substring(Math.Max(0, doc22.IndexOf("victim.cs") - 10), 40));
        Check("导出里有跑过的命令和它的结果",
            doc22.IndexOf("cmd /c build.cmd") >= 0 && doc22.IndexOf("退出码 0") >= 0, "");
        Check("导出里有结论", doc22.IndexOf("编译通过") >= 0, "");
        Check("系统提示词没被倒进导出里", doc22.IndexOf("你是「Ahz Scribe」") < 0, "");

        string path22 = Deliver.Export(runner22, d22);
        Check("导出落盘、文件名可辨认",
            File.Exists(path22) && Path.GetFileName(path22).StartsWith("交付记录-"), path22);
        Check("导出内容确实写进去了（不是空文件）",
            new FileInfo(path22).Length > 300, new FileInfo(path22).Length + " 字节");

        Console.WriteLine("== 23. 自升级闭环：等本进程退出 → 换 .new → 删 .new →（可选）重启 ==");

        string d23 = NewDir();
        Check("没有 .new 时列出来是空的", Upgrader.PendingNewFiles(d23).Count == 0, "");

        // 造一对：正式版 + .new（内容不同，替换完能验出来）
        string oldExe = Path.Combine(d23, "fake.exe");
        string newExe = oldExe + ".new";
        File.WriteAllText(oldExe, "旧版本", new UTF8Encoding(false));
        File.WriteAllText(newExe, "新版本内容", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(d23, "别的文件.txt"), "别动我", new UTF8Encoding(false));
        List<string> pend23 = Upgrader.PendingNewFiles(d23);
        Check("认得出待升级的 .new（且只认 .new）",
            pend23.Count == 1 && pend23[0] == "fake.exe.new", string.Join(",", pend23.ToArray()));

        // helper 脚本本身：必须用注入的 pid、必须带替换步骤；relaunch 给了才带 Start-Process
        string sc23 = Upgrader.ScriptFor(4242, d23, "");
        Check("脚本等的是被注入的那个 pid", sc23.IndexOf("Get-Process -Id 4242") >= 0, "");
        Check("脚本里有替换 + 删 .new 两步",
            sc23.IndexOf("Copy-Item") >= 0 && sc23.IndexOf("Remove-Item") >= 0, "");
        Check("不给重启目标时脚本里没有 Start-Process", sc23.IndexOf("Start-Process") < 0, "");
        Check("给了重启目标才带 Start-Process",
            Upgrader.ScriptFor(4242, d23, oldExe).IndexOf("Start-Process") >= 0, "");

        // 真跑一遍 helper：pid 用一个肯定不存在的号，它应该立刻开始替换
        string msg23;
        bool sched23 = Upgrader.ScheduleFor(999999, d23, "", out msg23);
        Check("能安排升级并返回排到的文件", sched23 && msg23.IndexOf("fake.exe.new") >= 0, msg23);
        bool replaced23 = false;
        for (int i = 0; i < 40 && !replaced23; i++)
        {
            System.Threading.Thread.Sleep(500);
            replaced23 = !File.Exists(newExe);
        }
        Check("helper 真的把新版换上去了（内容是新版的）",
            replaced23 && File.ReadAllText(oldExe, Encoding.UTF8) == "新版本内容",
            "旧 exe 现在是：" + (File.Exists(oldExe) ? File.ReadAllText(oldExe, Encoding.UTF8) : "（不见了）"));
        Check("换完把 .new 删掉了（不留“过期 .new”陷阱）", !File.Exists(newExe), "");
        Check("没有顺手动别的文件", File.ReadAllText(Path.Combine(d23, "别的文件.txt"), Encoding.UTF8) == "别动我", "");
        string ulog23 = Path.Combine(d23, "_build", "upgrade.log");
        Check("helper 过程写进了 upgrade.log（失败时查得到）",
            File.Exists(ulog23) && File.ReadAllText(ulog23).IndexOf("已替换") >= 0,
            File.Exists(ulog23) ? File.ReadAllText(ulog23) : "（没有日志）");

        Console.WriteLine("== 24. run_shell 收尾：超时要杀整棵树、句柄要释放 ==");

        string d24 = NewDir();
        ToolContext c24 = new ToolContext(d24, true, 3);        // 3 秒超时
        string marker24 = Path.Combine(d24, "child-done.txt");
        Dictionary<string, object> a24 = new Dictionary<string, object>();
        a24["command"] = "$arg = '-NoProfile','-Command','Start-Sleep -Seconds 8; New-Item -ItemType File -Path "
            + marker24 + " -Force | Out-Null'; Start-Process powershell.exe -ArgumentList $arg -WindowStyle Hidden | Out-Null;"
            + " Start-Sleep -Seconds 60";
        string err24 = null;
        try { Tools.RunShell(c24, a24); }
        catch (ToolException ex) { err24 = ex.Message; }
        Check("超时会明确报错（不是静默返回）",
            err24 != null && err24.IndexOf("超过") >= 0, Convert.ToString(err24));
        // 子进程本该在 8 秒后写出标记：只杀外壳的话它会出现
        System.Threading.Thread.Sleep(9000);
        Check("超时把子进程也一起杀了（子进程没能写出标记）",
            !File.Exists(marker24), File.Exists(marker24) ? "子进程活下来写出了标记" : "");

        Dictionary<string, object> ok24 = new Dictionary<string, object>();
        ok24["command"] = "Write-Output hi24";
        string r24 = Tools.RunShell(c24, ok24);
        Check("修完正常命令照样跑（退出码 + 输出都对）",
            r24.IndexOf("退出码 0") >= 0 && r24.IndexOf("hi24") >= 0, r24);
        string r24b = Tools.RunShell(c24, ok24);
        Check("连跑两次也没问题（Dispose 没影响后续调用）",
            r24b.IndexOf("hi24") >= 0, r24b);

        Console.WriteLine("== 25. 「本轮的改动」对话框：两栏不能重叠、不能被 DPI 放大两遍 ==");

        List<FileChange> cf25 = new List<FileChange>();
        FileChange f25a = new FileChange();
        f25a.Rel = "src\\AhzScribe.cs";
        f25a.Path = Path.Combine(d23, "victim.cs");
        f25a.OldText = "第一行\n旧的一行\n第三行\n";
        f25a.NewText = "第一行\n新的一行\n第三行\n";
        cf25.Add(f25a);
        FileChange f25b = new FileChange();
        f25b.Rel = "给Codex的信-2026-10-13.md";
        f25b.Path = Path.Combine(d23, "letter.md");
        f25b.OldText = "";
        f25b.NewText = "这是一封很长的信\n第二行\n第三行\n";
        cf25.Add(f25b);

        ChangesForm cf25form = new ChangesForm(cf25, 0);
        FieldInfo fl = typeof(ChangesForm).GetField("_list", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo fd = typeof(ChangesForm).GetField("_diff", BindingFlags.Instance | BindingFlags.NonPublic);
        Check("拿得到对话框里的列表和 diff 面板", fl != null && fd != null, "");
        Control list25 = (Control)fl.GetValue(cf25form);
        Control diff25 = (Control)fd.GetValue(cf25form);

        Check("列表和 diff 不重叠（列表右边 <= diff 左边）",
            list25.Right <= diff25.Left, "列表右=" + list25.Right + " diff左=" + diff25.Left);
        Check("列表左边距只放大一次（不是 1.5×1.5）",
            list25.Left == Theme.Px(16), "实际 " + list25.Left + "，期望 " + Theme.Px(16));
        Check("diff 没被顶出窗口右边（在容器内）",
            diff25.Right <= list25.Parent.ClientSize.Width, "diff右=" + diff25.Right + " 容器宽=" + list25.Parent.ClientSize.Width);
        Check("diff 比列表宽（正文才是主角）", diff25.Width > list25.Width,
            "diff=" + diff25.Width + " 列表=" + list25.Width);

        // 窗口变大后两栏要跟着重排，而且仍然不重叠
        cf25form.Size = new Size(cf25form.Width + Theme.Px(300), cf25form.Height + Theme.Px(120));
        cf25form.PerformLayout();
        System.Windows.Forms.Application.DoEvents();
        Check("窗口变大后仍然不重叠、且 diff 跟着变宽",
            list25.Right <= diff25.Left && diff25.Right <= list25.Parent.ClientSize.Width
            && diff25.Width > list25.Parent.ClientSize.Width / 2,
            "diff 宽=" + diff25.Width + " 容器宽=" + list25.Parent.ClientSize.Width);

        RichTextBox rtb25 = (RichTextBox)diff25;
        string t25 = rtb25.Text;
        Check("diff 里列出了两个文件和内容", t25.IndexOf("src\\AhzScribe.cs") >= 0 && t25.IndexOf("新的一行") >= 0, "");
        int plus25 = t25.IndexOf("+ 新的一行");
        int minus25 = t25.IndexOf("- 旧的一行");
        if (plus25 >= 0) { rtb25.Select(plus25, 1); }
        Color cplus25 = plus25 >= 0 ? rtb25.SelectionColor : Color.Empty;
        Color bgplus25 = plus25 >= 0 ? rtb25.SelectionBackColor : Color.Empty;
        if (minus25 >= 0) { rtb25.Select(minus25, 1); }
        Color cminus25 = minus25 >= 0 ? rtb25.SelectionColor : Color.Empty;
        Check("新增行是绿色、删除行是红色（一眼能分清）",
            cplus25 == Theme.Ok && cminus25 == Theme.Err, "加=" + cplus25 + " 删=" + cminus25);
        Check("新增行带淡绿底（像审查面板那样）", bgplus25 == Theme.OkSoft, Convert.ToString(bgplus25));
        cf25form.Dispose();

        Console.WriteLine("== 26. 提示词事实一致性：能力/边界必须如实写进系统提示 ==");
        // 为什么要这条：2026-10-13 AHZS 自己发现「提示词写着『只能碰工作目录』，
        // 但进程管理工具其实早就有了」——模型照着过时的自我描述办事，会自己劝退自己。
        // 改提示词光编译通过说明不了任何事，这里把「必须出现的事实」钉成回归点。

        AgentConfig cfg26 = AgentConfig.Load();
        cfg26.Migrate();
        cfg26.plan_mode = false;
        AgentRunner r26 = new AgentRunner(cfg26, new FakeUI());
        string prompt26 = Convert.ToString(r26.Messages[0]["content"]);

        string[] needTools = new string[] { "run_shell", "list_processes", "start_program", "kill_process",
            "run_background", "job_status" };
        string miss26 = "";
        foreach (string tn in needTools)
            if (prompt26.IndexOf(tn) < 0) miss26 += tn + " ";
        Check("四个「管理程序」的工具名都在系统提示里（模型得知道自己有什么牌）",
            miss26.Length == 0, "提示词里没提到：" + miss26);
        Check("写清了敏感操作要先经用户授权",
            prompt26.IndexOf("授权") >= 0, "");
        Check("写清了工作目录外的访问路径（走授权窗口，不是硬拒）",
            prompt26.IndexOf("工作目录外") >= 0 && prompt26.IndexOf("授权窗口") >= 0, "");

        // 计划模式那段的开关行为（section 21 已覆盖，这里只确认它不常驻）
        Check("没开计划模式时，提示词里不会出现计划模式那段",
            prompt26.IndexOf("【计划模式 · 已生效】") < 0, "");

        string[] busyTools = new string[] { "glob", "outline", "tail_file" };
        string miss26b = "";
        foreach (string tn in busyTools)
            if (prompt26.IndexOf(tn) < 0) miss26b += tn + " ";
        if (miss26b.Length > 0)
            Console.WriteLine("  · 提示：这些工具还没在系统提示里点名（模型只能从工具表看到）：" + miss26b);

        Console.WriteLine("== 27. 自我迭代闭环：申请单 → 批准/驳回 → 固定四步 → 回滚留痕 ==");

        string d27 = NewDir();
        Iteration it27 = new Iteration();
        it27.title = "自测申请";
        it27.change = "把 X 改成 Y";
        it27.decision = "已批准";
        it27.state = "待执行";
        Iterations.Add(d27, it27);
        Check("申请记录会落盘（devtools\\iterations.json）",
            File.Exists(Iterations.PathOf(d27)) && Iterations.Load(d27).Count == 1,
            "共 " + Iterations.Load(d27).Count + " 条");
        Check("「已批准且还没执行完」的那条找得出来",
            Iterations.LastApproved(d27) != null && Iterations.LastApproved(d27).id == it27.id, "");
        Iterations.MarkState(d27, it27.id, "已执行", "自测");
        Check("状态能改（待执行 → 已执行）", Iterations.Load(d27)[0].state == "已执行", Iterations.Load(d27)[0].state);
        Check("执行完就不再算「待执行的已批准」", Iterations.LastApproved(d27) == null, "");

        ToolContext c27 = new ToolContext(d27, false, 30);
        Dictionary<string, object> p27 = new Dictionary<string, object>();
        p27["title"] = "自测：加个没用的功能";
        p27["change"] = "改 src/AhzScribe.cs 加一行注释";

        Upgrader.ProposeConfirm = delegate(Iteration x) { return 0; };      // 模拟用户点「驳回」
        string res27 = Upgrader.ProposeTool(c27, p27);
        Check("驳回时明确让模型收手、别动手", res27.IndexOf("驳回") >= 0, res27);
        Check("驳回也留痕（decision=已驳回）",
            Iterations.Load(d27)[Iterations.Load(d27).Count - 1].decision == "已驳回", "");

        Upgrader.ProposeConfirm = delegate(Iteration x) { return 1; };      // 模拟用户点「批准执行」
        string res27b = Upgrader.ProposeTool(c27, p27);
        Check("批准后给出固定四步（改代码 → build → 自测 → 重启）+ 失败回滚",
            res27b.IndexOf("build.cmd") >= 0 && res27b.IndexOf("run-ui-tests.cmd") >= 0
            && res27b.IndexOf("restart_upgrade") >= 0 && res27b.IndexOf("rollback_self") >= 0, res27b);
        Check("批准的那条被记成「待执行」",
            Iterations.LastApproved(d27) != null && Iterations.LastApproved(d27).state == "待执行", "");
        Check("申请单正文格式齐全（五段 + 安全兜底）",
            ProposalForm.FormatText(Iterations.LastApproved(d27)).IndexOf("【潜在风险】") >= 0
            && ProposalForm.FormatText(Iterations.LastApproved(d27)).IndexOf("【安全兜底】") >= 0, "");

        // 回滚：文件必须恢复成改动前，且这次迭代记成已回滚
        string f27 = Path.Combine(d27, "roll.txt");
        File.WriteAllText(f27, "改动前的内容\n", new UTF8Encoding(false));
        List<FileChange> snap27 = new List<FileChange>();
        FileChange fc27 = new FileChange();
        fc27.Rel = "roll.txt";
        fc27.Path = f27;
        fc27.OldText = "改动前的内容\n";
        snap27.Add(fc27);
        File.WriteAllText(f27, "我改坏了\n", new UTF8Encoding(false));
        SnapStore.Save(d27, snap27, "已编辑 1 个文件");
        string rb27 = Upgrader.RollbackTool(c27, new Dictionary<string, object>());
        Check("回滚把文件恢复成改动前",
            File.ReadAllText(f27, Encoding.UTF8) == "改动前的内容\n",
            File.ReadAllText(f27, Encoding.UTF8).Replace("\n", "\\n"));
        Check("回滚把这次迭代记成「已回滚」",
            Iterations.Load(d27)[Iterations.Load(d27).Count - 1].state == "已回滚", "");
        Check("回滚结果里说清了「要重跑 build 才一致」", rb27.IndexOf("build") >= 0, rb27);

        string sc27 = Upgrader.ScriptFor(4242, d27, Path.Combine(d27, "fake.exe"));
        Check("升级脚本替换前先留上一版（.prev）", sc27.IndexOf(".prev") >= 0, "");
        Check("升级脚本带看门狗（新版起不来就回滚）",
            sc27.IndexOf("HasExited") >= 0 && sc27.IndexOf("回滚到上一版") >= 0, "");
        Upgrader.ProposeConfirm = null;      // 还原

        Console.WriteLine();
        Console.WriteLine("================ 规矩/验证 单元自测：通过 " + pass + " 项，失败 " + fail + " 项 ================");
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static Dictionary<string, object> ToolCallMsg16(string id, string name)
    {
        Dictionary<string, object> m = AgentRunner.Msg("assistant", "");
        List<object> calls = new List<object>();
        Dictionary<string, object> tc = new Dictionary<string, object>();
        tc["id"] = id;
        tc["name"] = name;
        tc["arguments"] = new Dictionary<string, object>();
        calls.Add(tc);
        m["tool_calls"] = calls;
        return m;
    }

    static Dictionary<string, object> ToolResultMsg16(string id)
    {
        Dictionary<string, object> m = AgentRunner.Msg("tool", "[read_file 结果]\n内容");
        m["tool_call_id"] = id;
        m["tool_name"] = "read_file";
        return m;
    }

    // 交付记录自测用：造一个工具调用条目（id / 名字 / 一个代表性参数）
    static Dictionary<string, object> ToolCall22(string id, string name, string argValue)
    {
        Dictionary<string, object> tc = new Dictionary<string, object>();
        tc["id"] = id;
        tc["name"] = name;
        Dictionary<string, object> args = new Dictionary<string, object>();
        if (name == "run_shell") args["command"] = argValue;
        else args["path"] = argValue;
        tc["arguments"] = args;
        return tc;
    }
}
