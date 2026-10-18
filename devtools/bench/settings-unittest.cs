using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 自测：设置窗口「模型」页——预设自动填地址 / 新增不覆盖 / 拉取合并 / 控件不许跑出可视区
class SettingsUnit
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

    static object Call(object t, string m, params object[] a)
    {
        MethodInfo mi = t.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.NonPublic);
        return mi.Invoke(t, a);
    }

    static void Pump(int ms)
    {
        DateTime end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    static Control Txt(object f, string field)
    {
        return (Control)Get(f, field);
    }

    static ListBox Lst(object f)
    {
        return (ListBox)Get(f, "_lstProviders");
    }

    static List<string> Ids(AgentConfig c)
    {
        List<string> l = new List<string>();
        foreach (Provider p in c.providers) l.Add(p.id);
        return l;
    }

    [STAThread]
    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.EnsureDpi();

        AgentConfig cfg = new AgentConfig();
        cfg.providers = new List<Provider>();
        cfg.providers.Add(Provider.DefaultOllama());
        Provider ds = new Provider();
        ds.name = "deepseek"; ds.type = "openai";
        ds.base_url = "https://api.deepseek.com/v1"; ds.api_key = "sk-x";
        ds.models = new List<string>(new string[] { "deepseek-flash" });
        cfg.providers.Add(ds);

        SettingsForm f = new SettingsForm(cfg);
        f.Show();
        Pump(600);
        AgentConfig c = (AgentConfig)Get(f, "_c");      // 窗体内部用的是 config 的深拷贝，断言都看这份

        Console.WriteLine("== 1. 四页所有控件都必须在可视区内（按钮被推出可视区 = 用户点不到）==");
        Panel[] pages = (Panel[])Get(f, "_pages");
        string[] names = new string[] { "通用设置", "模型", "运行日志", "关于" };
        for (int i = 0; i < pages.Length; i++)
        {
            Check("第 " + i + " 页不是空指针（漏建页面 = 点导航就崩）", pages[i] != null, "pages[" + i + "]=null");
            if (pages[i] == null) continue;
            Call(f, "ShowPage", i);
            Pump(150);
            Panel pg = pages[i];
            int outside = 0;
            string worst = "";
            foreach (Control k in pg.Controls)
            {
                bool inside = k.Left >= 0 && k.Top >= 0 && k.Right <= pg.ClientSize.Width && k.Bottom <= pg.ClientSize.Height;
                if (!inside) { outside++; if (worst.Length == 0) worst = k.GetType().Name + " (" + k.Left + "," + k.Top + ") " + k.Width + "x" + k.Height; }
            }
            Check(names[i] + "页 " + pg.Controls.Count + " 个控件全在可视区内", outside == 0,
                "越界 " + outside + " 个，例如 " + worst);
        }
        Call(f, "ShowPage", 1);
        Pump(150);

        // 导航必须和页面数量对得上（原来 icons 只有 3 个名字却有 4 个导航项，构造时直接越界崩）
        object[] nav = (object[])Get(f, "_nav");
        Check("导航项数 = 页面数 = 4", nav.Length == 4 && pages.Length == 4,
            "导航 " + nav.Length + " 项 / 页面 " + pages.Length + " 页");

        Console.WriteLine("== 1b. 运行日志页：显式排版 + 能读日志 ==");
        Call(f, "ShowPage", 2);                    // 导航顺序：0 通用设置 / 1 模型 / 2 运行日志 / 3 关于
        Pump(200);
        Panel lp = pages[2];
        UiDiff logBox = (UiDiff)Get(f, "_logBox");
        Check("日志页里装的确实是日志框（页面顺序没和「关于」页串）",
            logBox != null && lp.Controls.Contains(logBox)
            && !lp.Controls.Contains((Control)Get(f, "_aboutCard")),
            "日志框在页 2 = " + (logBox != null && lp.Controls.Contains(logBox))
            + " / 关于卡片在页 2 = " + lp.Controls.Contains((Control)Get(f, "_aboutCard")));
        Check("日志页真的切过去了（不是白页）", lp.Visible && logBox != null, "visible=" + lp.Visible);
        if (logBox != null)
        {
            Check("日志框撑满了页面宽度（留白对称，不是固定 820px）",
                logBox.Width >= lp.ClientSize.Width - 100, "框宽=" + logBox.Width + " 页宽=" + lp.ClientSize.Width);
            Check("日志框够高（≥ 150px，不是被 DPI 挤成一条）", logBox.Height >= 150, "框高=" + logBox.Height);
            Check("日志框底部在按钮上方（按钮不会被压住）",
                logBox.Bottom < lp.ClientSize.Height, "框底=" + logBox.Bottom + " 页高=" + lp.ClientSize.Height);
            Control[] btns = new Control[] { (Control)Get(f, "_btnLogRefresh"), (Control)Get(f, "_btnLogOpen"), (Control)Get(f, "_btnLogClear") };
            int badBtn = 0;
            string btnWorst = "";
            foreach (Control bc in btns)
            {
                bool ok = bc != null && bc.Left >= 0 && bc.Top >= 0
                    && bc.Right <= lp.ClientSize.Width && bc.Bottom <= lp.ClientSize.Height
                    && bc.Top > logBox.Bottom;
                if (!ok)
                {
                    badBtn++;
                    if (btnWorst.Length == 0) btnWorst = (bc == null ? "null" : (bc.Text + " (" + bc.Left + "," + bc.Top + ")"));
                }
            }
            Check("三个按钮都在日志框下方且没跑出可视区", badBtn == 0, "不对 " + badBtn + " 个 例如 " + btnWorst);
            // 先往日志里写一行"留痕"，再点刷新：页面必须能显示到这一行（证明页面真的接上了日志文件）
            string tag = "自测留痕-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Log.Audit(tag);
            // 点一下"刷新"：走真事件。刷新走 120ms 防抖，所以要**给它机会**——
            // 最多等 1.5 秒轮询，而不是只 Pump 一次（实测只等一次会偶发看不到，那是测试写法的问题，
            // 不是产品缺陷：定时器还没触发而已）。
            bool threw = false;
            try
            {
                Control rb = (Control)Get(f, "_btnLogRefresh");
                typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(rb, new object[] { EventArgs.Empty });     // 走真事件（UiButton 是自绘控件，没有 PerformClick）
            }
            catch { threw = true; }
            bool sawTag = false;
            for (int i = 0; i < 10 && !sawTag; i++)
            {
                Pump(150);
                if (logBox.Text.IndexOf(tag) >= 0) sawTag = true;
            }
            Check("点「刷新」显示的就是日志文件的最新内容（页面真的接上了日志文件）",
                !threw && sawTag, threw ? "抛异常" : "等了 1.5 秒也没看到 " + tag);
            // 防抖与"不动用户的滚动位置"（整改项⑨）：**文件没变时**刷新，文本框与选区都不许动。
            // 先把 _logShown 对齐到当前尾巴（这一步可能写入一次），再快照、再调一次 —— 那次必须什么都不做。
            Call(f, "LoadLogText");
            Pump(120);
            string tail1 = Log.ReadTail(64 * 1024);
            int sel0 = logBox.SelectionStart;
            string text0 = logBox.Text;
            Call(f, "LoadLogText");
            Pump(120);
            string tail2 = Log.ReadTail(64 * 1024);
            Check("内容没变时刷新不改动文本框（滚动位置不被顶走）",
                tail1 != tail2 || (logBox.Text == text0 && logBox.SelectionStart == sel0),
                "fileChanged=" + (tail1 != tail2) + " textChanged=" + (logBox.Text != text0)
                + " sel " + sel0 + "->" + logBox.SelectionStart);
            Check("日志很大时只显示末尾（带截断提示，不是整文件重灌）",
                Log.ReadTail(64 * 1024).Length <= 64 * 1024 + 200 && Log.ReadTail(64 * 1024).Length <= Log.Read().Length,
                "tail=" + Log.ReadTail(64 * 1024).Length + " full=" + Log.Read().Length);
        }
        Call(f, "ShowPage", 1);
        Pump(150);

        Console.WriteLine("== 1c. 通用设置页：卡片装得下内容，输入框不按「隐藏页的 200px」算 ==");
        Call(f, "ShowPage", 0);
        Pump(200);
        Panel gp = pages[0];
        RoundedPanel gc = (RoundedPanel)Get(f, "_generalCard");
        Control lastChk = (Control)Get(f, "_chkHideThink");
        FieldBox cwdField = (FieldBox)Get(f, "_cwdBox");
        Control pickBtn = (Control)Get(f, "_btnPickCwd");
        Check("卡片装得下最后一个复选框（第 5 行没被卡片下边缘切掉）",
            gc != null && lastChk != null && lastChk.Bottom + 8 <= gc.Height,
            "最后一行底=" + (lastChk == null ? -1 : lastChk.Bottom) + " 卡片高=" + (gc == null ? -1 : gc.Height));
        Check("工作目录输入框撑到「选择…」按钮左边（不是按隐藏页尺寸算出的短框）",
            cwdField != null && pickBtn != null && cwdField.Right <= pickBtn.Left + 1
            && cwdField.Width > gp.ClientSize.Width / 2,
            "框=" + (cwdField == null ? "null" : cwdField.Bounds.ToString())
            + " 按钮x=" + (pickBtn == null ? -1 : pickBtn.Left) + " 页宽=" + gp.ClientSize.Width);

        Console.WriteLine("== 1d. 同级控件不许互相压住（150% 下权限下拉压复选框的回归）==");
        UiDrop permCmb = (UiDrop)Get(f, "_cmbPerm");
        Control outsideChk = (Control)Get(f, "_chkOutside");
        Check("权限下拉与「允许访问工作目录外」复选框不重叠",
            permCmb != null && outsideChk != null && !permCmb.Bounds.IntersectsWith(outsideChk.Bounds),
            "下拉=" + (permCmb == null ? "null" : permCmb.Bounds.ToString())
            + " 复选框=" + (outsideChk == null ? "null" : outsideChk.Bounds.ToString()));
        Panel navPanel = (Panel)Get(f, "_navPanel");
        UiButton[] navBtns = (UiButton[])Get(f, "_nav");
        bool navIn = navPanel != null && navBtns != null;
        if (navIn)
            foreach (UiButton nb in navBtns)
                if (nb != null && !navPanel.ClientRectangle.Contains(nb.Bounds)) navIn = false;
        Check("左侧导航按钮都在自己的面板里（不再 405px 撑出 318px 父容器）",
            navIn, navPanel == null ? "没有 _navPanel" : ("父=" + navPanel.ClientSize + " 首个=" + (navBtns == null || navBtns.Length == 0 ? "null" : navBtns[0].Bounds.ToString())));
        Call(f, "ShowPage", 1);
        Pump(150);

        Console.WriteLine("== 2. 名称里写平台名 -> 自动填接口地址 ==");
        Lst(f).SelectedIndex = 1;                       // deepseek
        Pump(120);
        Dictionary<string, string> want = new Dictionary<string, string>();
        want["glm"] = "https://open.bigmodel.cn/api/paas/v4";
        want["智谱"] = "https://open.bigmodel.cn/api/paas/v4";
        want["kimi"] = "https://api.moonshot.cn/v1";
        want["通义"] = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        want["deepseek"] = "https://api.deepseek.com/v1";
        want["豆包"] = "https://ark.cn-beijing.volces.com/api/v3";
        int okAuto = 0;
        foreach (KeyValuePair<string, string> kv in want)
        {
            Txt(f, "_txtBase").Text = "";               // 模拟还没填地址
            Txt(f, "_txtName").Text = kv.Key;
            Pump(60);
            string got = Txt(f, "_txtBase").Text.Trim();
            if (got == kv.Value) okAuto++;
            else Console.WriteLine("    " + kv.Key + " 期望 " + kv.Value + "，实际 " + got);
        }
        Check("6 个平台名都能自动填对地址", okAuto == want.Count, okAuto + "/" + want.Count);

        Console.WriteLine("== 3. 手工填过的地址不能被自动覆盖 ==");
        Txt(f, "_txtName").Text = "glm";
        Pump(60);
        Txt(f, "_txtBase").Text = "https://my-own-proxy.example/v1";
        Pump(60);
        Txt(f, "_txtName").Text = "kimi";               // 改成别的平台
        Pump(60);
        Check("改名称不会覆盖我手填的地址", Txt(f, "_txtBase").Text == "https://my-own-proxy.example/v1",
            "现在=" + Txt(f, "_txtBase").Text);

        Console.WriteLine("== 4. 新增提供商不能覆盖已有的 ==");
        object psGlm = null;
        foreach (object ps in (System.Collections.IEnumerable)typeof(SettingsForm)
                     .GetField("Presets", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))
            if ((string)ps.GetType().GetField("name").GetValue(ps) == "智谱 GLM") psGlm = ps;
        Check("拿到「智谱 GLM」预设", psGlm != null, "psGlm=" + (psGlm == null ? "null" : "ok"));
        string deepBefore = c.providers[1].name + "|" + c.providers[1].base_url + "|" + string.Join(",", c.providers[1].models.ToArray());
        int nBefore = c.providers.Count;
        try { Call(f, "AddProviderFromPreset", psGlm); }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            Console.WriteLine("    调用出错: " + inner.GetType().FullName + ": " + inner.Message);
            Console.WriteLine(inner.StackTrace);
        }
        Pump(200);
        Check("提供商数量 +1", c.providers.Count == nBefore + 1, nBefore + " -> " + c.providers.Count);
        Provider np = c.providers[c.providers.Count - 1];
        Check("新提供商名称/地址/类型正确",
            np.name == "智谱 GLM" && np.base_url == "https://open.bigmodel.cn/api/paas/v4" && np.type == "openai",
            np.name + " / " + np.base_url + " / " + np.type);
        string deepAfter = c.providers[1].name + "|" + c.providers[1].base_url + "|" + string.Join(",", c.providers[1].models.ToArray());
        Check("原来的 deepseek 一点没变", deepBefore == deepAfter, deepBefore + " -> " + deepAfter);
        Check("列表里能看到新加的，并且选中它",
            Lst(f).Items.Count == c.providers.Count && Lst(f).SelectedIndex == Lst(f).Items.Count - 1,
            "items=" + Lst(f).Items.Count + " sel=" + Lst(f).SelectedIndex);
        Check("界面字段已经切成新提供商（不是还停在 deepseek 上）",
            Txt(f, "_txtName").Text == "智谱 GLM" && Txt(f, "_txtBase").Text == "https://open.bigmodel.cn/api/paas/v4",
            Txt(f, "_txtName").Text + " / " + Txt(f, "_txtBase").Text);

        Console.WriteLine("== 5. 空白新增：地址留空、名称不重复 ==");
        Call(f, "AddProviderFromPreset", new object[] { null });    // 注意：要传"一个 null 参数"，不是"没有参数"
        Pump(120);
        Provider np2 = c.providers[c.providers.Count - 1];
        Check("空白新增地址为空", np2.base_url == "", "base=" + np2.base_url);
        Call(f, "AddProviderFromPreset", psGlm);        // 再加一个同名预设
        Pump(120);
        Provider np3 = c.providers[c.providers.Count - 1];
        Check("重名会自动加序号", np3.name == "智谱 GLM 2", "name=" + np3.name);

        Console.WriteLine("== 6. 拉取模型是合并而不是覆盖（顺带把接口给的能力标签存下来）==");
        Lst(f).SelectedIndex = 1;
        Pump(120);
        Txt(f, "_txtModels").Text = "my-model-a\r\ndeepseek-flash";
        Pump(60);
        // 现在"拉取"拉回来的是带能力标签的清单（以前只有 id），合并时顺手存进 provider
        Provider sel6 = c.providers[1];
        List<ModelMeta> fetched = new List<ModelMeta>();
        foreach (string id6 in new string[] { "deepseek-flash", "deepseek-v4-pro", "deepseek-new" })
        {
            ModelMeta mi6 = new ModelMeta();
            mi6.id = id6;
            fetched.Add(mi6);
        }
        fetched[0].name = "DeepSeek-V4.1-Flash";
        fetched[0].vision = true;
        fetched[0].ctx = 1048576;
        fetched[0].efforts = new List<string>(new string[] { "low", "high", "max" });
        object hint = Call(f, "MergeModelList", sel6, fetched);
        Pump(60);
        List<string> after = new List<string>();
        foreach (string line in Txt(f, "_txtModels").Text.Replace("\r\n", "\n").Split('\n'))
            if (line.Trim().Length > 0) after.Add(line.Trim());
        Check("手写的模型还在", after.Contains("my-model-a"), string.Join(",", after.ToArray()));
        Check("新拉的补进来了", after.Contains("deepseek-v4-pro") && after.Contains("deepseek-new"), string.Join(",", after.ToArray()));
        Check("没有重复项", after.Count == new List<string>(new HashSet<string>(after)).Count, string.Join(",", after.ToArray()));
        Check("提示里说清了新增几个", Convert.ToString(hint).IndexOf("新增 2") >= 0, Convert.ToString(hint));
        Check("合并结果写回了 config（存得住）",
            c.providers[1].models.Contains("my-model-a") && c.providers[1].models.Contains("deepseek-new"),
            string.Join(",", c.providers[1].models.ToArray()));
        Check("接口给的能力标签也存进了这个提供商（显示名 / 能看图 / 档位）",
            sel6.Meta("deepseek-flash") != null
            && sel6.DisplayName("deepseek-flash") == "DeepSeek-V4.1-Flash"
            && sel6.CanSeeImages("deepseek-flash") == true
            && Effort.Supported(sel6, "deepseek-flash", ""),
            sel6.Meta("deepseek-flash") == null ? "没存下来" : sel6.DisplayName("deepseek-flash"));
        Check("提示语里报了「能看图几个、能调档几个」",
            Convert.ToString(hint).IndexOf("能看图 1 个") >= 0 && Convert.ToString(hint).IndexOf("能调档 1 个") >= 0,
            Convert.ToString(hint));

        Console.WriteLine("== 7. 其它不变量 ==");
        Check("每个提供商 id 唯一",
            new List<string>(new HashSet<string>(Ids(c))).Count == c.providers.Count, string.Join(",", Ids(c).ToArray()));
        Check("Ollama 本地还在（没被新增挤掉）",
            c.providers[0].type == "ollama" && c.providers[0].base_url == "http://127.0.0.1:11434", c.providers[0].name);
        Check("新提供商的默认地址不再是 deepseek（旧默认值是真凶之一）", new Provider().base_url == "", "base=" + new Provider().base_url);

        Console.WriteLine("== 8. 输入框里的文字必须待在框里、并且竖直居中 ==");
        List<FieldBox> boxes = new List<FieldBox>();
        CollectFields(f, boxes);
        int bad = 0, offCenter = 0;
        foreach (FieldBox fb in boxes)
        {
            if (fb.Inner == null) continue;
            int padL = Theme.Px(fb.Padding.Left), padR = Theme.Px(fb.Padding.Right);
            int padT = Theme.Px(fb.Padding.Top), padB = Theme.Px(fb.Padding.Bottom);
            bool inside = fb.Inner.Left >= padL - 1 && fb.Inner.Right <= fb.Width - padR + 1;
            int top = fb.Inner.Top - padT, bottom = fb.Height - padB - fb.Inner.Bottom;
            bool centered = Math.Abs(top - bottom) <= 4;
            if (!inside) { bad++; Console.WriteLine("    超出外框: box " + fb.Width + "x" + fb.Height + " inner " + fb.Inner.Width + "x" + fb.Inner.Height); }
            if (!centered) { offCenter++; Console.WriteLine("    竖直不居中: 上留=" + top + " 下留=" + bottom); }
        }
        Check("一共检查了 " + boxes.Count + " 个输入框，文字控件宽度都在框内", boxes.Count > 0 && bad == 0, "超出 " + bad + " 个");
        Check("文字竖直居中（上下留白差 ≤ 4px）", offCenter == 0, "不居中 " + offCenter + " 个");

        Console.WriteLine("== 9. TLS：默认就要打开 1.2+（否则智谱这类平台握手就失败）==");
        Program.EnsureTls();
        int proto = (int)System.Net.ServicePointManager.SecurityProtocol;
        Check("SecurityProtocol 含 TLS1.2（3072）", (proto & 3072) == 3072, "proto=" + proto);

        Console.WriteLine("== 10. API Key 落盘加密（DPAPI CurrentUser）==");
        string enc = Dpapi.Protect("sk-test-123");
        Check("密文带 dpapi-v1 前缀", Dpapi.IsProtected(enc), enc);
        Check("密文里看不到明文", enc.IndexOf("sk-test-123", StringComparison.Ordinal) < 0, enc);
        Check("当前用户能解回原 Key", Dpapi.Unprotect(enc) == "sk-test-123", "解回=" + Dpapi.Unprotect(enc));
        Check("老配置的明文 Key 仍能原样读", Dpapi.Unprotect("sk-old-plain") == "sk-old-plain", "旧明文读取失败");

        Console.WriteLine("== 11. 密码框 IME 合成缓冲隔离 ==");
        UiPasswordBox pw = new UiPasswordBox();
        pw.DebugCompose("ni");
        Check("合成中：密码内存还是空的（候选串不落盘）", pw.Text.Length == 0, "text=" + pw.Text);
        Check("合成中：候选串只在缓冲区", pw.DebugComposition == "ni", "comp=" + pw.DebugComposition);
        pw.DebugCommit("你");
        Check("提交后才进密码内存", pw.Text == "你", "text=" + pw.Text);
        Check("提交后合成缓冲清空", pw.DebugComposition.Length == 0, "comp=" + pw.DebugComposition);
        pw.Dispose();

        f.Close();
        Console.WriteLine();
        Console.WriteLine("================ 设置页自测：通过 " + pass + " 项，失败 " + fail + " 项 ================");
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static void CollectFields(Control root, List<FieldBox> into)
    {
        foreach (Control k in root.Controls)
        {
            FieldBox fb = k as FieldBox;
            if (fb != null) into.Add(fb);
            if (k.Controls.Count > 0) CollectFields(k, into);
        }
    }
}
