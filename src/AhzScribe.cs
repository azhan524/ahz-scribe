// LocalAgent.cs —— 本地智能体（Windows 桌面程序）
// 编译：见同目录 build.cmd（用系统自带 csc.exe，无需安装任何东西）
// 语言级别固定为 C# 5（Windows 自带的 csc.exe 只支持到 C# 5），所以不要用字符串插值、?. 等新语法。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Ahz Scribe")]
[assembly: System.Reflection.AssemblyProduct("Ahz Scribe")]
[assembly: System.Reflection.AssemblyDescription("本地 / 云端模型通用桌面智能体")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

// ============================ 基础工具 ============================

static class Json
{
    public static readonly JavaScriptSerializer Ser = Create();

    static JavaScriptSerializer Create()
    {
        JavaScriptSerializer s = new JavaScriptSerializer();
        s.MaxJsonLength = int.MaxValue;
        s.RecursionLimit = 200;
        return s;
    }

    public static string Dump(object o) { return Ser.Serialize(o); }

    public static Dictionary<string, object> Obj(string text)
    {
        if (text == null) return null;
        return Ser.Deserialize<Dictionary<string, object>>(text);
    }

    public static Dictionary<string, object> Map(object o) { return o as Dictionary<string, object>; }

    // 注意：JSON 数组反序列化回来是 ArrayList，而程序内部构造的是 List<object>，
    // 两种都要认。只认 ArrayList 的话，内部拼的 tool_calls 会被静默丢掉，
    // 导致严格校验的接口（如 DeepSeek）报
    // "Messages with role 'tool' must be a response to a preceding message with 'tool_calls'"。
    public static object[] Arr(object o)
    {
        object[] arr = o as object[];
        if (arr != null) return arr;
        System.Collections.IList list = o as System.Collections.IList;
        if (list != null)
        {
            object[] result = new object[list.Count];
            list.CopyTo(result, 0);
            return result;
        }
        return null;
    }

    public static object Get(Dictionary<string, object> d, string key)
    {
        object v;
        if (d != null && d.TryGetValue(key, out v)) return v;
        return null;
    }

    public static string Str(Dictionary<string, object> d, string key, string def)
    {
        object v = Get(d, key);
        if (v == null) return def;
        return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static int Int(Dictionary<string, object> d, string key, int def)
    {
        object v = Get(d, key);
        if (v == null) return def;
        try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return def; }
    }

    public static long Long(Dictionary<string, object> d, string key, long def)
    {
        object v = Get(d, key);
        if (v == null) return def;
        try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return def; }
    }

    public static bool Bool(Dictionary<string, object> d, string key, bool def)
    {
        object v = Get(d, key);
        if (v is bool) return (bool)v;
        return def;
    }
}

static class Txt
{
    public static string Clip(string s, int limit)
    {
        if (s == null) return "";
        if (s.Length <= limit) return s;
        return s.Substring(0, limit) + "\n…（输出过长已截断，共 " + s.Length + " 字符）";
    }

    public static string FirstLine(string s)
    {
        if (s == null) return "";
        int i = s.IndexOf('\n');
        string line = i < 0 ? s : s.Substring(0, i);
        if (line.Length > 150) line = line.Substring(0, 150) + "…";
        return line;
    }

    public static int LineCount(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 1;
        foreach (char c in s) if (c == '\n') n++;
        return n;
    }
}

class LocalAgentException : Exception
{
    public LocalAgentException(string msg) : base(msg) { }
}

// ============================ 工具 ============================

class ToolException : Exception
{
    public ToolException(string msg) : base(msg) { }
}

class ToolContext
{
    public string Root;
    public bool AllowOutside;
    public int ShellTimeout;
    // 只读档（权限档「仅可查看」）：写类工具（write_file / edit_file / run_shell）直接拒绝。
    // 放在 ToolContext 上而不是散在 AgentRunner 里，是为了让"能不能写"只有一处判据 ——
    // 以前 readonly 和 workspace 行为完全一样，等于那档是装饰的。
    public bool ReadOnly;
    // 越权申请：路径跑到工作目录外时，不再一刀切拒绝，而是先问用户
    // （0 拒绝 / 1 允许这次 / 2 本会话总是允许）。由 AgentRunner.ResetContext 装上；
    // 没装（例如自测里直接 new ToolContext）就保持老行为：直接拒绝 —— 绝不会静默放行。
    public Func<string, int> AskOutside;

    public ToolContext(string root, bool allowOutside, int shellTimeout)
        : this(root, allowOutside, shellTimeout, false) { }

    public ToolContext(string root, bool allowOutside, int shellTimeout, bool readOnly)
    {
        Root = Path.GetFullPath(root);
        AllowOutside = allowOutside;
        ShellTimeout = shellTimeout;
        ReadOnly = readOnly;
    }

    public string Resolve(string raw)
    {
        if (raw == null || raw.Trim().Length == 0) throw new ToolException("路径不能为空");
        string p = raw.Trim().Trim('"');
        // 参数自动纠正：小模型经常把"工作目录的名字"当成路径（如工作目录 ...\local-agent，
        // 它就传 path="local-agent"）。这其实指当前目录，直接纠正掉，省一整轮试错。
        if (!Path.IsPathRooted(p))
        {
            string bare = p.TrimEnd('\\', '/');
            string rootName = Path.GetFileName(Root.TrimEnd('\\', '/'));
            if (bare.Length > 0 && rootName.Length > 0 &&
                (bare.Equals(rootName, StringComparison.OrdinalIgnoreCase) ||
                 bare.Equals("./" + rootName, StringComparison.OrdinalIgnoreCase) ||
                 bare.Equals(".\\" + rootName, StringComparison.OrdinalIgnoreCase)))
            {
                string probe = Path.Combine(Root, bare);
                if (!Directory.Exists(probe) && !File.Exists(probe)) p = ".";
            }
        }
        if (!Path.IsPathRooted(p)) p = Path.Combine(Root, p);
        string full = Path.GetFullPath(p);
        if (!AllowOutside)
        {
            string root = Root.TrimEnd('\\');
            string prefix = root + "\\";
            bool inside = full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                          full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            if (!inside)
            {
                int d = AskOutside == null ? 0 : AskOutside(full);
                if (d != 1 && d != 2)
                    throw new ToolException("路径超出工作目录：" + full + "\n（工作目录是 " + Root + "）"
                        + (AskOutside == null ? ""
                            : "\n（这次已经问过用户，没放行。不要反复重试同一条路径；"
                              + "要长期放行请在设置里勾选「允许访问工作目录外」。）"));
                if (d == 2) AllowOutside = true;      // 本上下文直接放行，后续不再问
            }
        }
        return full;
    }
}

delegate string ToolRun(ToolContext ctx, Dictionary<string, object> args);

class ToolDef
{
    public string Name;
    public string Desc;
    public bool NeedsApproval;
    public ToolRun Run;
    private readonly List<string> _names = new List<string>();
    private readonly List<string> _types = new List<string>();
    private readonly List<string> _descs = new List<string>();
    private readonly List<bool> _required = new List<bool>();

    public ToolDef(string name, string desc, bool needsApproval, ToolRun run)
    {
        Name = name; Desc = desc; NeedsApproval = needsApproval; Run = run;
    }

    public ToolDef Prop(string name, string type, string desc, bool required)
    {
        _names.Add(name); _types.Add(type); _descs.Add(desc); _required.Add(required);
        return this;
    }

    public Dictionary<string, object> Schema()
    {
        Dictionary<string, object> props = new Dictionary<string, object>();
        for (int i = 0; i < _names.Count; i++)
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            p["type"] = _types[i];
            p["description"] = _descs[i];
            props[_names[i]] = p;
        }
        List<object> req = new List<object>();
        for (int i = 0; i < _required.Count; i++) if (_required[i]) req.Add(_names[i]);
        Dictionary<string, object> parameters = new Dictionary<string, object>();
        parameters["type"] = "object";
        parameters["properties"] = props;
        parameters["required"] = req;
        Dictionary<string, object> fn = new Dictionary<string, object>();
        fn["name"] = Name;
        fn["description"] = Desc;
        fn["parameters"] = parameters;
        Dictionary<string, object> tool = new Dictionary<string, object>();
        tool["type"] = "function";
        tool["function"] = fn;
        return tool;
    }
}

static class A
{
    public static string Str(Dictionary<string, object> a, string k, string def)
    {
        object v = Json.Get(a, k);
        if (v == null) return def;
        return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static int Int(Dictionary<string, object> a, string k, int def)
    {
        object v = Json.Get(a, k);
        if (v == null) return def;
        try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return def; }
    }

    public static bool Bool(Dictionary<string, object> a, string k, bool def)
    {
        object v = Json.Get(a, k);
        if (v is bool) return (bool)v;
        return def;
    }
}

// 这一轮改了哪些文件：工具层记录，界面用它画「已编辑 N 个文件」卡片
// 模型"叫嚷却不调用"的识别：只说要调用工具、却把调用写成正文的句子。
// 这类文字既不该显示给用户，也不该让它一直重复（GLM 实测连刷十几条 [调用]）。
static class ToolNoise
{
    static readonly string[] ToolNames = new string[]
    { "list_dir", "read_file", "search_text", "tail_file", "write_file", "edit_file", "run_shell" };

    public static bool IsAnnounceLine(string line)
    {
        if (line == null) return false;
        string s = line.Trim();
        if (s.Length == 0) return false;
        if (s.Length <= 26)
        {
            if (s == "[调用]" || s == "[executing]" || s == "[tool_call]" || s == "[调用工具]") return true;
            if (s.StartsWith("Call now", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.StartsWith("真实调用")) return true;
            if (s.StartsWith("调用如下")) return true;
            if (s.StartsWith("（真实调用") || s.StartsWith("(真实调用")) return true;
            if (s.IndexOf("立即调用工具", StringComparison.Ordinal) >= 0) return true;
            if (s.StartsWith("不再解释") && s.IndexOf("调用", StringComparison.Ordinal) >= 0) return true;
        }
        foreach (string t in ToolNames)                       // 行首直接是"工具名("
            if (s.StartsWith(t + "(", StringComparison.Ordinal)) return true;
        return false;
    }

    public static int Count(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        foreach (string l in text.Replace("\r\n", "\n").Split('\n')) if (IsAnnounceLine(l)) n++;
        return n;
    }
}

class FileChange
{
    public string Path;        // 绝对路径
    public string Rel;         // 相对工作目录的显示路径
    public string OldText;
    public string NewText;
    public string DiffText;    // 懒算
    public int Added, Removed;
}

static class Changes
{
    public static string Root = "";
    public static readonly List<FileChange> Now = new List<FileChange>();
    public static readonly List<List<FileChange>> Snapshots = new List<List<FileChange>>();

    public static void Clear() { Now.Clear(); }

    public static void Record(string absPath, string oldText, string newText)
    {
        foreach (FileChange c in Now)
        {
            if (string.Equals(c.Path, absPath, StringComparison.OrdinalIgnoreCase))
            {
                c.NewText = newText;                 // 同一文件改多次：只留"最初 → 最新"
                c.DiffText = null; c.Added = 0; c.Removed = 0;
                return;
            }
        }
        FileChange fc = new FileChange();
        fc.Path = absPath;
        fc.Rel = Rel(absPath);
        fc.OldText = oldText == null ? "" : oldText;
        fc.NewText = newText == null ? "" : newText;
        Now.Add(fc);
    }

    public static string Rel(string abs)
    {
        try
        {
            if (!string.IsNullOrEmpty(Root) && abs.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
                return abs.Substring(Root.Length).TrimStart('\\', '/');
        }
        catch { }
        return abs;
    }

    // 存快照并返回编号：卡片上带这个编号，"查看变更"按编号取（不会串到别的轮次）
    public static int Snapshot()
    {
        Snapshots.Add(new List<FileChange>(Now));
        return Snapshots.Count - 1;
    }

    public static List<FileChange> Get(int idx)
    {
        if (idx < 0 || idx >= Snapshots.Count) return null;
        return Snapshots[idx];
    }

    // 把一个快照里的文件恢复成改动前的内容（新文件删掉）；返回成功/失败个数
    public static void RestoreAll(List<FileChange> list, out int ok, out int fail)
    {
        ok = 0; fail = 0;
        if (list == null) return;
        foreach (FileChange c in list)
        {
            try
            {
                if (c.OldText == null || c.OldText.Length == 0)
                {
                    if (File.Exists(c.Path)) File.Delete(c.Path);
                }
                else
                {
                    File.WriteAllText(c.Path, c.OldText, new UTF8Encoding(false));
                }
                ok++;
            }
            catch { fail++; }
        }
    }

    public static void EnsureDiff(FileChange c)
    {
        if (c.DiffText != null) return;
        Diff.Build(c);
    }
}

// 把 Changes 的快照落到磁盘（升级需求 P0-2）。
// 为什么不是新造一套：Changes 已经在每轮结束时攒好了"改前全文"，GUI 的「撤销」也走它；
// 这里只负责"存得住 + 能列出来 + 能恢复"，和 GUI 共用同一条 RestoreAll 路径，
// 不会出现两套快照互相打架。
//
//   .snapshots/<时间戳>-<序号>.json   一个快照一个文件
//   里面存：时间、说明、每个文件的 Rel 路径 + 改前全文
//   （只存"改前"，恢复就是把它们写回去；上限 SNAP_KEEP 个，超了删最老的）
static class SnapStore
{
    public const int SNAP_KEEP = 50;          // 只留最近这么多个快照
    const long SNAP_BUDGET = 80L * 1024 * 1024;   // 总量上限：快照目录最多 80MB

    public static string Dir(string root) { return Path.Combine(root, ".snapshots"); }

    // 存一个快照；成功返回文件名，失败返回 null（快照失败绝不能影响正常干活）
    public static string Save(string root, List<FileChange> files, string note)
    {
        if (files == null || files.Count == 0) return null;
        try
        {
            string dir = Dir(root);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string name = stamp + "-" + Guid.NewGuid().ToString("N").Substring(0, 4) + ".json";
            List<object> items = new List<object>();
            foreach (FileChange c in files)
            {
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["rel"] = c.Rel;
                o["path"] = c.Path;
                o["old"] = c.OldText == null ? "" : c.OldText;
                o["existed"] = !(c.OldText == null || c.OldText.Length == 0);
                items.Add(o);
            }
            Dictionary<string, object> doc = new Dictionary<string, object>();
            doc["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            doc["note"] = note == null ? "" : note;
            doc["files"] = items;
            File.WriteAllText(Path.Combine(dir, name), Json.Dump(doc), new UTF8Encoding(false));
            Prune(dir);
            return name;
        }
        catch (Exception ex) { Log.Swallow(ex, "存快照"); return null; }
    }

    // 列出快照（新→旧）：时间 / 文件名 / 说明 / 文件数
    public static List<string> List(string root)
    {
        List<string> outLines = new List<string>();
        try
        {
            string dir = Dir(root);
            if (!Directory.Exists(dir)) return outLines;
            string[] files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files);
            Array.Reverse(files);
            foreach (string f in files)
            {
                string time = Path.GetFileName(f), note = "", n = "?";
                try
                {
                    Dictionary<string, object> doc = Json.Obj(File.ReadAllText(f, Encoding.UTF8));
                    if (doc != null)
                    {
                        time = Json.Str(doc, "time", time);
                        note = Json.Str(doc, "note", "");
                        object[] arr = Json.Arr(Json.Get(doc, "files"));
                        n = Convert.ToString(arr == null ? 0 : arr.Length);
                    }
                }
                catch { }
                outLines.Add(Path.GetFileNameWithoutExtension(f) + "  " + time + "  " + n + " 个文件"
                    + (note.Length > 0 ? "  " + note : ""));
            }
        }
        catch (Exception ex) { Log.Swallow(ex, "列快照"); }
        return outLines;
    }

    // 恢复某个快照（按文件名前缀或全名匹配，取最新匹配的那个）
    public static string Restore(string root, string id, out int ok, out int fail)
    {
        ok = 0; fail = 0;
        try
        {
            string dir = Dir(root);
            if (!Directory.Exists(dir)) return "没有 .snapshots 目录，还没存过快照。";
            string[] files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files);
            Array.Reverse(files);
            string hit = null;
            foreach (string f in files)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(f), id, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileNameWithoutExtension(f).StartsWith(id, StringComparison.OrdinalIgnoreCase))
                { hit = f; break; }
            }
            if (hit == null) return "没找到快照：" + id + "（先跑 --snapshots 看有哪些）";
            Dictionary<string, object> doc = Json.Obj(File.ReadAllText(hit, Encoding.UTF8));
            object[] arr = Json.Arr(Json.Get(doc, "files"));
            if (arr == null || arr.Length == 0) return "这个快照里没有文件。";
            List<FileChange> list = new List<FileChange>();
            foreach (object it in arr)
            {
                Dictionary<string, object> o = Json.Map(it);
                if (o == null) continue;
                FileChange c = new FileChange();
                c.Path = Json.Str(o, "path", "");
                c.Rel = Json.Str(o, "rel", c.Path);
                c.OldText = Json.Str(o, "old", "");
                c.NewText = "";
                if (c.Path.Length > 0) list.Add(c);
            }
            Changes.RestoreAll(list, out ok, out fail);
            return "已从快照 " + Path.GetFileNameWithoutExtension(hit) + " 恢复 " + ok + " 个文件"
                + (fail > 0 ? "（" + fail + " 个失败）" : "") + "。";
        }
        catch (Exception ex) { return "恢复失败：" + ex.Message; }
    }

    // 保留策略：先按个数砍，再按总量砍（不砍就会像目录里那 9 个 .bak 一样越堆越多）
    static void Prune(string dir)
    {
        try
        {
            string[] files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files);
            // 个数：从最老的开始删
            int over = files.Length - SNAP_KEEP;
            for (int i = 0; i < over; i++) { try { File.Delete(files[i]); } catch { } }
            // 总量
            files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files);
            long total = 0;
            foreach (string f in files) { try { total += new FileInfo(f).Length; } catch { } }
            for (int i = 0; i < files.Length && total > SNAP_BUDGET; i++)
            {
                try { total -= new FileInfo(files[i]).Length; File.Delete(files[i]); } catch { }
            }
        }
        catch (Exception ex) { Log.Swallow(ex, "清理旧快照"); }
    }
}

// 行级 diff（够用就行：先掐掉公共前后缀，中间小段做 LCS，太大就整体替换）
static class Diff
{
    public static void Build(FileChange c)
    {
        string[] a = Split(c.OldText), b = Split(c.NewText);
        StringBuilder sb = new StringBuilder();
        c.Added = 0; c.Removed = 0;
        if (c.OldText.Length == 0 && c.NewText.Length > 0)
        {
            int head = Math.Min(b.Length, 40);
            for (int i = 0; i < head; i++) { sb.Append("+ ").Append(b[i]).Append('\n'); c.Added++; }
            if (b.Length > head) sb.Append("… （还有 ").Append(b.Length - head).Append(" 行，新文件）\n");
            c.DiffText = sb.ToString();
            return;
        }
        int s = 0;
        while (s < a.Length && s < b.Length && a[s] == b[s]) s++;
        int ea = a.Length, eb = b.Length;
        while (ea > s && eb > s && a[ea - 1] == b[eb - 1]) { ea--; eb--; }
        int ctx = Math.Min(3, s);
        for (int i = s - ctx; i < s; i++) sb.Append("  ").Append(a[i]).Append('\n');
        int na = ea - s, nb = eb - s;
        if (na == 0 && nb == 0)
        {
            c.DiffText = "（内容没变）\n";
            return;
        }
        if ((long)na * nb <= 400000)
        {
            // LCS
            int[,] dp = new int[na + 1, nb + 1];
            for (int i = na - 1; i >= 0; i--)
                for (int j = nb - 1; j >= 0; j--)
                    dp[i, j] = a[s + i] == b[s + j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            int x = 0, y = 0;
            while (x < na && y < nb)
            {
                if (a[s + x] == b[s + y]) { sb.Append("  ").Append(a[s + x]).Append('\n'); x++; y++; }
                else if (dp[x + 1, y] >= dp[x, y + 1]) { sb.Append("- ").Append(a[s + x]).Append('\n'); c.Removed++; x++; }
                else { sb.Append("+ ").Append(b[s + y]).Append('\n'); c.Added++; y++; }
            }
            while (x < na) { sb.Append("- ").Append(a[s + x]).Append('\n'); c.Removed++; x++; }
            while (y < nb) { sb.Append("+ ").Append(b[s + y]).Append('\n'); c.Added++; y++; }
        }
        else
        {
            sb.Append("（这段改动太大，按整体替换显示）\n");
            for (int i = s; i < s + Math.Min(na, 20); i++) { sb.Append("- ").Append(a[i]).Append('\n'); c.Removed++; }
            if (na > 20) sb.Append("- …（还有 ").Append(na - 20).Append(" 行）\n");
            for (int i = s; i < s + Math.Min(nb, 20); i++) { sb.Append("+ ").Append(b[i]).Append('\n'); c.Added++; }
            if (nb > 20) sb.Append("+ …（还有 ").Append(nb - 20).Append(" 行）\n");
        }
        int tailCtx = Math.Min(3, a.Length - ea);
        for (int i = 0; i < tailCtx; i++) sb.Append("  ").Append(a[ea + i]).Append('\n');
        c.DiffText = sb.ToString();
    }

    static string[] Split(string s)
    {
        if (s == null || s.Length == 0) return new string[0];
        string[] l = s.Replace("\r\n", "\n").Split('\n');
        if (l.Length > 0 && l[l.Length - 1].Length == 0)
        {
            string[] t = new string[l.Length - 1];      // 结尾那个空行不算"新增一行"
            Array.Copy(l, t, t.Length);
            return t;
        }
        return l;
    }
}

static class Tools
{
    public static readonly HashSet<string> IgnoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "__pycache__", ".venv", "venv", "env", "dist", "build",
        ".idea", ".vscode", ".next", "target", ".mypy_cache", ".pytest_cache", "site-packages"
    };

    public static List<ToolDef> All()
    {
        List<ToolDef> list = new List<ToolDef>();

        list.Add(new ToolDef("list_dir", "列出一个目录下的文件和子目录（含大小）。想了解项目结构时先用它。", false, ListDir)
            .Prop("path", "string", "目录路径，相对工作目录或绝对路径，默认 .", true));

        list.Add(new ToolDef("read_file", "按行读取文本文件内容（带行号）。默认读前 200 行，可用 start_line/end_line 继续读。", false, ReadFile)
            .Prop("path", "string", "文件路径", true)
            .Prop("start_line", "integer", "起始行号，从 1 开始，默认 1", false)
            .Prop("end_line", "integer", "结束行号，默认 200 行以内", false));

        list.Add(new ToolDef("search_text", "在目录里按正则搜索文本，返回 文件:行号: 内容。找函数、变量、报错信息时用。", false, SearchText)
            .Prop("pattern", "string", "正则表达式，例如 def main 或 TODO", true)
            .Prop("path", "string", "搜索起点，默认工作目录", false)
            .Prop("max_results", "integer", "最多返回多少条，默认 60", false));

        list.Add(new ToolDef("tail_file", "只看文件末尾若干行（默认 50，带行号）。日志/长文件用这个，别用 read_file 整读。", false, TailFile)
            .Prop("path", "string", "文件路径", true)
            .Prop("lines", "integer", "末尾多少行，默认 50", false));

        list.Add(new ToolDef("glob", "按通配符找文件（支持 ** 递归），按最近修改排序。找\"哪个文件在哪\"用这个，别一层层 list_dir。", false, GlobTool)
            .Prop("pattern", "string", "通配符，例如 **/*.cs 或 src/*.cmd", true)
            .Prop("path", "string", "搜索起点，默认工作目录", false)
            .Prop("max_results", "integer", "最多返回多少个，默认 100", false));

        list.Add(new ToolDef("outline", "只列文件里的类/方法/属性签名和行号（不读正文）。长文件想先看结构、再决定读哪段时用。", false, OutlineTool)
            .Prop("path", "string", "文件路径", true)
            .Prop("max_items", "integer", "最多列多少项，默认 120", false));

        list.Add(new ToolDef("write_file", "新建文件或整体覆盖写入（需要用户确认）。小改动请优先用 edit_file。整体覆盖会先做体量检查：新内容比原文少 30% 以上会被拒绝。", true, WriteFile)
            .Prop("path", "string", "文件路径", true)
            .Prop("content", "string", "完整文件内容", true)
            .Prop("allow_shrink", "boolean", "确实要大幅精简/重写时才设 true，默认 false", false));

        list.Add(new ToolDef("edit_file", "把文件里的 old_string 精确替换成 new_string（需要用户确认）。old_string 必须与原文完全一致且在文件中唯一。", true, EditFile)
            .Prop("path", "string", "文件路径", true)
            .Prop("old_string", "string", "被替换的原文本（要带足上下文，保证唯一）", true)
            .Prop("new_string", "string", "替换后的新文本", true)
            .Prop("replace_all", "boolean", "是否替换全部匹配，默认 false", false));

        list.Add(new ToolDef("run_shell", "在 PowerShell 里执行一条命令并返回输出（需要用户确认）。用来跑测试、看 git 状态等。", true, RunShell)
            .Prop("command", "string", "要执行的 PowerShell 命令", true)
            .Prop("timeout_sec", "integer", "超时秒数，默认 120", false));

        // 长活（编译 / 测试 / 记分卡 / 起服务）用后台跑：前台 run_shell 默认 120 秒一到会被掐断，
        // 记分卡那种十几分钟的任务就是这么被杀在半路的（history.csv 一直落不了盘）。
        list.Add(new ToolDef("run_background", "把一条命令放到后台跑，立刻返回任务号（不阻塞）。跑测试、编译、记分卡、启动服务这类长任务用它；短命令仍用 run_shell。用 job_status 看进度。", true, Jobs.Run)
            .Prop("command", "string", "要执行的 PowerShell 命令", true)
            .Prop("name", "string", "给任务起个短名字，便于辨认（可省略）", false));

        list.Add(new ToolDef("job_status", "看后台任务的进度：运行中还是已结束、退出码、跑了多久、末尾输出。传 task_id 看单个，不传列出全部。", false, Jobs.Status)
            .Prop("task_id", "string", "任务号（run_background 返回的）；不传就列出全部任务", false)
            .Prop("lines", "integer", "顺便返回末尾多少行输出，默认 30", false));

        list.Add(new ToolDef("job_kill", "结束一个还在跑的后台任务（按任务号）。", true, Jobs.KillTool)
            .Prop("task_id", "string", "任务号", true));

        // 改完自己、跑过 build.cmd 之后用它收尾：把 .new 换上去并重启（以前这步必须人工退出 + 双击 升级.cmd）
        list.Add(new ToolDef("restart_upgrade", "把编译出来的 .new 换上去并重启程序（会结束当前这一轮）。自己改完自己、且已经跑过 build.cmd 之后用它收尾；没有 .new 时它只会告诉你先去 build。", true, Upgrader.RestartTool)
            .Prop("reason", "string", "为什么现在重启升级（一句话，会记进日志）", false));

        // 自我迭代闭环：发现自己能力不够 → 提申请单 → 用户批准/驳回 → 按固定四步执行 → 失败可回滚
        list.Add(new ToolDef("propose_upgrade", "发现自己的能力、代码或工具不够用时，向用户提一份自我迭代改造申请（会弹标准申请单让用户批准或驳回）。别直接报错收工，也别自己闷头改。", false, Upgrader.ProposeTool)
            .Prop("title", "string", "一句话标题，例如「给 read_file 加二进制文件识别」", true)
            .Prop("change", "string", "改造内容：具体改哪个文件、怎么改", true)
            .Prop("scope", "string", "影响范围：会动到哪些功能/文件", false)
            .Prop("risk", "string", "潜在风险：最坏会怎样、怎么兜底", false)
            .Prop("expect", "string", "预期效果：改完能多做什么", false)
            .Prop("verify", "string", "怎么验证：跑什么命令、看什么现象", false));

        list.Add(new ToolDef("rollback_self", "把文件回滚到改动前（用最近一条快照），并把这次自我迭代记成「已回滚」。改造失败、验证不过、或新版起不来时用它。", false, Upgrader.RollbackTool)
            .Prop("reason", "string", "为什么回滚（一句话）", false));

        list.Add(new ToolDef("list_processes", "列出当前正在运行的进程（PID / 名称 / 内存占用），可用 name 过滤。查\"哪个程序在跑、占多少内存\"先用它。", false, ListProcesses)
            .Prop("name", "string", "只列名称包含这个词的进程，默认全部", false));

        list.Add(new ToolDef("start_program", "启动一个程序（敏感操作，需要用户确认）。可以传程序名或完整路径，也能带参数。", true, StartProgram)
            .Prop("path", "string", "程序路径或命令，如 notepad、D:\\xx\\app.exe 或一个网址", true)
            .Prop("args", "string", "命令行参数，可选", false)
            .Prop("working_dir", "string", "工作目录，相对路径基于工作区，可选", false));

        list.Add(new ToolDef("kill_process", "结束一个或一组进程（敏感操作，需要用户确认）。按 pid 结束单个，按 name 结束所有同名进程；先 list_processes 确认再杀。", true, KillProcess)
            .Prop("pid", "integer", "要结束的进程 ID", false)
            .Prop("name", "string", "进程名（不含 .exe），会结束所有同名进程", false));

        return list;
    }

    // ---------- 具体实现 ----------

    public static string ListDir(ToolContext ctx, Dictionary<string, object> a)
    {
        string path = ctx.Resolve(A.Str(a, "path", "."));
        if (File.Exists(path))
        {
            FileInfo fi = new FileInfo(path);
            return path + " 是文件，大小 " + fi.Length + " 字节";
        }
        if (!Directory.Exists(path))
        {
            // 报错时把上一级的真实内容带上，避免小模型一直拿项目名当路径
            string hint = "";
            try
            {
                string parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    string[] kids = Directory.GetFileSystemEntries(parent);
                    int n = Math.Min(kids.Length, 12);
                    List<string> names = new List<string>();
                    for (int i = 0; i < n; i++) names.Add(Path.GetFileName(kids[i]));
                    hint = "。它的上一级 " + parent + " 下现有：" + string.Join("、", names.ToArray())
                         + "（想看当前工作目录，请把 path 传 \".\"）";
                }
                else if (Directory.Exists(ctx.Root))
                {
                    hint = "。当前工作目录是 " + ctx.Root + "，想看当前目录请把 path 传 \".\"";
                }
            }
            catch { }
            throw new ToolException("路径不存在：" + path + hint);
        }
        string[] dirs = Directory.GetDirectories(path);
        string[] files = Directory.GetFiles(path);
        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        int total = dirs.Length + files.Length;
        StringBuilder sb = new StringBuilder();
        sb.Append(path).Append(" 共 ").Append(total).Append(" 项：\n");
        if (total == 0) sb.Append("（空目录）\n");
        int shown = 0;
        foreach (string d in dirs)
        {
            if (shown++ >= 300) break;
            sb.Append("[DIR ] ").Append(Path.GetFileName(d)).Append("/\n");
        }
        foreach (string f in files)
        {
            if (shown++ >= 300) break;
            long len = 0;
            try { len = new FileInfo(f).Length; } catch { }
            sb.Append("[FILE] ").Append(Path.GetFileName(f)).Append("  (").Append(len).Append(" 字节)\n");
        }
        if (total > 300) sb.Append("…还有 ").Append(total - 300).Append(" 项未列出\n");
        return sb.ToString();
    }

    public static string ReadFile(ToolContext ctx, Dictionary<string, object> a)
    {
        string path = ctx.Resolve(A.Str(a, "path", ""));
        if (Directory.Exists(path)) throw new ToolException(path + " 是目录，请用 list_dir");
        if (!File.Exists(path)) throw new ToolException("文件不存在：" + path);
        byte[] raw = File.ReadAllBytes(path);
        int probe = Math.Min(raw.Length, 4096);
        for (int i = 0; i < probe; i++)
            if (raw[i] == 0) throw new ToolException(path + " 看起来是二进制文件，无法按文本读取");
        string text = Encoding.UTF8.GetString(raw);
        if (text.Length == 0) return path + " 是空文件";
        string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        int total = lines.Length;
        int start = Math.Max(1, A.Int(a, "start_line", 1));
        int endArg = A.Int(a, "end_line", 0);
        int end = (endArg >= start) ? Math.Min(endArg, total) : Math.Min(total, start + 199);
        StringBuilder sb = new StringBuilder();
        sb.Append(path).Append("（共 ").Append(total).Append(" 行，本次显示 ").Append(start).Append("-").Append(end).Append("）\n");
        if (end < total) sb.Append("（后面还有 ").Append(total - end).Append(" 行，可再用 read_file 指定 start_line 继续读）\n");
        for (int i = start - 1; i < end && i < total; i++)
            sb.Append((i + 1).ToString().PadLeft(5)).Append("| ").Append(lines[i]).Append('\n');
        return Txt.Clip(sb.ToString(), 6000);
    }

    static IEnumerable<string> Walk(string root)
    {
        Stack<string> stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subs;
            string[] fs;
            try { subs = Directory.GetDirectories(dir); } catch { subs = new string[0]; }
            try { fs = Directory.GetFiles(dir); } catch { fs = new string[0]; }
            foreach (string f in fs) yield return f;
            foreach (string d in subs)
            {
                if (IgnoredDirs.Contains(Path.GetFileName(d))) continue;
                stack.Push(d);
            }
        }
    }

    // ---------- 按通配符找文件 ----------
    // 以前没有这个工具，模型找文件只能一层层 list_dir 或拿 search_text 猜内容 —— 又慢又费上下文。
    // 支持 ** 递归；结果按修改时间倒序（最近动过的更可能是你要找的）。
    // 会跳过构建产物/快照/隐藏目录：不然 _build\tmp 里的自测垃圾会把真正的结果淹掉（实测）。
    static readonly string[] NoisyDirs = new string[]
    { ".snapshots", "_build", ".git", "bin", "obj", "node_modules", ".vs", "packages", "__pycache__", ".idea" };

    // 判断某个路径相对 basePath 的**相对部分**里是否含噪声目录。
    // 注意不能对整条绝对路径判断：搜索起点本身可能就在 _build 里（自测就是这么跑的），
    // 那就该老老实实搜它 —— "跳过噪声"只对起点之下的目录生效。
    static bool Noisy(string fullPath, string basePath)
    {
        string rel = Rel(basePath, fullPath).Replace('\\', '/');
        foreach (string d in NoisyDirs)
        {
            if (rel.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase)) return true;
            if (rel.IndexOf("/" + d + "/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    public static string GlobTool(ToolContext ctx, Dictionary<string, object> a)
    {
        string pattern = A.Str(a, "pattern", "").Trim();
        if (pattern.Length == 0) throw new ToolException("pattern 不能为空，例如 **/*.cs");
        string basePath = ctx.Resolve(A.Str(a, "path", "."));
        if (!Directory.Exists(basePath)) throw new ToolException("目录不存在：" + basePath);
        int limit = A.Int(a, "max_results", 100);
        if (limit < 1) limit = 100;
        if (limit > 500) limit = 500;

        Regex re = GlobToRegex(pattern);
        List<FileInfo> hits = new List<FileInfo>();
        int scanned = 0, skipped = 0;
        foreach (string f in Walk(basePath))
        {
            if (hits.Count >= 4000) break;         // 先收着，最后再截断排序
            scanned++;
            if (Noisy(f, basePath)) { skipped++; continue; }
            string rel = Rel(basePath, f);
            if (re.IsMatch(rel.Replace('\\', '/')))
            {
                try { hits.Add(new FileInfo(f)); } catch { }
            }
        }
        if (hits.Count == 0)
            return "在 " + basePath + " 下没有匹配 “" + pattern + "” 的文件（扫了 " + scanned
                + " 个文件，其中 " + skipped + " 个在构建产物/快照目录里被跳过）。";

        hits.Sort(delegate(FileInfo x, FileInfo y) { return y.LastWriteTime.CompareTo(x.LastWriteTime); });
        StringBuilder sb = new StringBuilder();
        sb.Append("匹配 “").Append(pattern).Append("”，共 ").Append(hits.Count).Append(" 个（按最近修改排序）：\n");
        int n = Math.Min(hits.Count, limit);
        for (int i = 0; i < n; i++)
        {
            FileInfo fi = hits[i];
            sb.Append(fi.LastWriteTime.ToString("MM-dd HH:mm")).Append("  ")
              .Append(fi.Length.ToString().PadLeft(8)).Append("  ")
              .Append(Rel(basePath, fi.FullName)).Append('\n');
        }
        if (hits.Count > n) sb.Append("…（还有 ").Append(hits.Count - n).Append(" 个，可用 max_results 放大）\n");
        return Txt.Clip(sb.ToString(), 6000);
    }

    // 通配符 -> 正则：** 跨目录，* 不跨目录，? 单字符
    public static Regex GlobToRegex(string pattern)
    {
        string p = pattern.Replace('\\', '/');
        StringBuilder sb = new StringBuilder("^");
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '*')
            {
                bool dbl = (i + 1 < p.Length && p[i + 1] == '*');
                if (dbl)
                {
                    i++;
                    // "**/" 连斜杠一起吃掉，这样 **/*.cs 也能匹配根目录下的 a.cs
                    if (i + 1 < p.Length && p[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                    else sb.Append(".*");
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append("$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }

    static string Rel(string basePath, string full)
    {
        try
        {
            string b = basePath.TrimEnd('\\', '/');
            if (full.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                return full.Substring(b.Length).TrimStart('\\', '/').Replace('\\', '/');
        }
        catch { }
        return full.Replace('\\', '/');
    }

    // ---------- 只列结构（类/方法/属性 + 行号）----------
    // 主文件 1 万多行，整读一遍很贵。先看结构、再决定读哪段，能省掉大量无用上下文。
    public static string OutlineTool(ToolContext ctx, Dictionary<string, object> a)
    {
        string path = ctx.Resolve(A.Str(a, "path", ""));
        if (!File.Exists(path)) throw new ToolException("文件不存在：" + path);
        int maxItems = A.Int(a, "max_items", 120);
        if (maxItems < 1) maxItems = 120;

        string[] lines;
        try { lines = File.ReadAllLines(path, Encoding.UTF8); }
        catch (Exception ex) { throw new ToolException("读不了这个文件：" + ex.Message); }

        // 只对 C 系语言做结构识别；别的文件给出"行数 + 前几行"就够
        bool cs = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".java", StringComparison.OrdinalIgnoreCase);
        if (!cs)
        {
            StringBuilder sb0 = new StringBuilder();
            sb0.Append(Path.GetFileName(path)).Append(" 共 ").Append(lines.Length).Append(" 行（不做结构识别）。\n");
            int head = Math.Min(lines.Length, 15);
            for (int i = 0; i < head; i++) sb0.Append(i + 1).Append('\t').Append(lines[i]).Append('\n');
            if (lines.Length > head) sb0.Append("… 用 read_file 按行范围读剩下的。\n");
            return sb0.ToString();
        }

        List<string> outLines = new List<string>();
        int depth = 0;
        for (int i = 0; i < lines.Length && outLines.Count < maxItems; i++)
        {
            string raw = lines[i];
            string t = raw.Trim();
            // 注释行不参与声明识别，但注释里的括号不该影响深度（BraceBalance 已经会跳注释）
            string decl = Declaration(t, depth);
            if (decl != null)
                outLines.Add((i + 1).ToString().PadLeft(6) + "  " + new string(' ', Math.Min(depth, 8) * 2) + decl);
            // 用 BraceBalance 那套（跳字符串/注释）更新深度，避免被字面量里的括号带偏
            depth += BraceBalance(raw);
            if (depth < 0) depth = 0;
        }

        StringBuilder sb = new StringBuilder();
        sb.Append(Path.GetFileName(path)).Append(" 共 ").Append(lines.Length).Append(" 行，");
        sb.Append(outLines.Count).Append(" 项结构：\n");
        foreach (string l in outLines) sb.Append(l).Append('\n');
        if (outLines.Count >= maxItems) sb.Append("…（已达 max_items 上限）\n");
        return Txt.Clip(sb.ToString(), 6000);
    }

    // 一行是不是"结构声明"。判据（按可靠性排序）：
    //   1) 命名空间层（depth 0）的 namespace/class/struct/interface/enum/delegate —— 直接认
    //   2) 类型体层（depth 1..2）的：带 ( 且不是控制流/调用 → 方法；带 { get / => → 属性
    //   3) 方法体里（depth 3+）一律不认 —— 这是上一版误报 Console.WriteLine 的原因
    // 注意"大括号换行"是 C# 主流写法，所以不能靠"这一行以 { 结尾"来判断。
    static string Declaration(string t, int depth)
    {
        if (t.Length == 0) return null;
        if (t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith("*") || t.StartsWith("#")) return null;

        bool typeKeyword = StartsWithAny(t, new string[] { "namespace ", "class ", "struct ", "interface ",
            "enum ", "delegate ", "public class ", "internal class ", "private class ", "sealed class ",
            "abstract class ", "static class ", "partial class ", "public struct ", "public interface ",
            "public enum ", "internal struct ", "internal interface ", "internal enum " });
        if (typeKeyword) return Clip(t);

        // 类型体之外（free function / 命名空间体）也允许
        if (depth > 2) return null;

        bool control = StartsWithAny(t, new string[] { "if ", "for ", "foreach ", "while ", "switch ",
            "catch ", "return ", "using ", "else", "do ", "lock ", "try", "throw ", "goto ", "case ",
            "break", "continue", "yield ", "await ", "fixed ", "checked ", "unchecked " });
        if (control) return null;

        // 属性：`public int Bar { get { return _n; } }` / `public int Bar => 1;`
        if (t.IndexOf("=>") > 0) return Clip(t);
        int brace = t.IndexOf('{');
        if (brace > 0 && t.IndexOf("get", brace, StringComparison.Ordinal) >= 0) return Clip(t);
        if (brace > 0 && t.IndexOf("set", brace, StringComparison.Ordinal) >= 0) return Clip(t);

        // 方法：这一行有 "(" 且有 ")"，并且不是赋值/调用
        int lp = t.IndexOf('(');
        if (lp <= 0) return null;
        int rp = t.IndexOf(')', lp);
        if (rp < 0) return null;
        if (t.IndexOf('=') >= 0 && t.IndexOf('=') < lp) return null;   // 形如 x = Foo(...)
        // 下一个非空字符应该是 { 或 => 或 : （构造器初始化列表/基类构造）
        string rest = t.Substring(rp + 1).Trim();
        bool bodyNext = rest.Length == 0 || rest.StartsWith("{") || rest.StartsWith("=>")
            || rest.StartsWith(":") || rest.StartsWith("where ");
        if (!bodyNext) return null;
        return Clip(t);
    }

    static bool StartsWithAny(string s, string[] prefixes)
    {
        foreach (string p in prefixes) if (s.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    static string Clip(string t)
    {
        string sig = t.TrimEnd('{', ' ', '\t', '\r', '\n');
        if (sig.Length > 110) sig = sig.Substring(0, 110) + "…";
        return sig;
    }

    // 只读文件末尾 N 行（带真实行号）。日志上到上万行时，整读既慢又挤爆上下文。
    public static string TailFile(ToolContext ctx, Dictionary<string, object> a)
    {
        string path = ctx.Resolve(A.Str(a, "path", ""));
        if (!File.Exists(path))
        {
            // 报错时列出同目录下名字相近的文件，省得小模型一直猜路径
            string hint = "";
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string[] all = Directory.GetFiles(dir, "*.txt");
                    if (all.Length > 0)
                    {
                        List<string> names = new List<string>();
                        for (int i = 0; i < all.Length && i < 8; i++) names.Add(Path.GetFileName(all[i]));
                        hint = "\n同目录下的 txt：" + string.Join("、", names.ToArray());
                    }
                }
            }
            catch { }
            throw new ToolException("文件不存在：" + path + hint);
        }

        int want = A.Int(a, "lines", 50);
        if (want < 1) want = 50;
        if (want > 2000) want = 2000;

        FileInfo fi = new FileInfo(path);
        long size = fi.Length;
        int take = (int)Math.Min(size, 128 * 1024);
        List<string> lines = new List<string>();
        long begin = 0;
        // 从尾部往前扩，直到凑够 want 行（或到达文件头 / 2MB 上限）
        while (true)
        {
            begin = Math.Max(0, size - take);
            lines = ReadTailLines(path, begin, take);
            if (lines.Count >= want + 1 || begin == 0 || take >= 2 * 1024 * 1024) break;
            take *= 4;
        }

        // 尾部这一段一共有多少行（用来算绝对行号）
        long firstLineNo;
        if (begin == 0) firstLineNo = 1;
        else
        {
            // begin 落在某一行中间：这一行算"部分行"，绝对行号得从头数或按换行数估。
            // 估法：把从头到 begin 的换行数数一遍（只对超长文件做的代价，实测够快）
            firstLineNo = CountNewlines(path, begin) + 2;
        }

        int from = Math.Max(0, lines.Count - want);
        StringBuilder sb = new StringBuilder();
        sb.Append(Path.GetFileName(path)).Append(" 共 ").Append(size).Append(" 字节，末尾 ")
          .Append(lines.Count - from).Append(" 行：\n");
        for (int i = from; i < lines.Count; i++)
            sb.Append(firstLineNo + i).Append('\t').Append(lines[i]).Append('\n');
        sb.Append("（最后写入时间 ").Append(fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")).Append("）\n");
        return Txt.Clip(sb.ToString(), 8000);
    }

    static List<string> ReadTailLines(string path, long begin, int take)
    {
        List<string> lines = new List<string>();
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(begin, SeekOrigin.Begin);
            byte[] buf = new byte[take];
            int n = fs.Read(buf, 0, take);
            string text = Encoding.UTF8.GetString(buf, 0, n);
            // begin 不在行首时，首个不完整片段丢掉
            if (begin > 0)
            {
                int nl = text.IndexOf('\n');
                if (nl >= 0) text = text.Substring(nl + 1);
                else text = "";
            }
            string[] parts = text.Replace("\r\n", "\n").Split('\n');
            foreach (string p in parts) lines.Add(p);
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }

    // 数 0..end 之间有多少个 '\n'（块读，不整文件载入）
    static long CountNewlines(string path, long end)
    {
        long count = 0;
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            byte[] buf = new byte[64 * 1024];
            long read = 0;
            while (read < end)
            {
                int want = (int)Math.Min(buf.Length, end - read);
                int n = fs.Read(buf, 0, want);
                if (n <= 0) break;
                for (int i = 0; i < n; i++) if (buf[i] == (byte)'\n') count++;
                read += n;
            }
        }
        return count;
    }

    public static string SearchText(ToolContext ctx, Dictionary<string, object> a)
    {
        string basePath = ctx.Resolve(A.Str(a, "path", "."));
        string pattern = A.Str(a, "pattern", "");
        if (pattern.Length == 0) throw new ToolException("pattern 不能为空");
        Regex re;
        try { re = new Regex(pattern, RegexOptions.IgnoreCase); }
        catch (ArgumentException ex) { throw new ToolException("正则表达式写错了：" + ex.Message); }
        int limit = Math.Max(1, Math.Min(A.Int(a, "max_results", 60), 200));
        List<string> hits = new List<string>();
        int scanned = 0;
        IEnumerable<string> files = File.Exists(basePath) ? (IEnumerable<string>)new string[] { basePath } : Walk(basePath);
        foreach (string f in files)
        {
            if (hits.Count >= limit || scanned > 4000) break;
            try
            {
                FileInfo fi = new FileInfo(f);
                if (fi.Length > 1000000) continue;
                scanned++;
                string[] lines = File.ReadAllLines(f, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!re.IsMatch(lines[i])) continue;
                    string s = lines[i].Trim();
                    if (s.Length > 200) s = s.Substring(0, 200);
                    hits.Add(f + ":" + (i + 1) + ": " + s);
                    if (hits.Count >= limit) break;
                }
            }
            catch { }
        }
        if (hits.Count == 0)
            return "在 " + basePath + " 下没有找到匹配 “" + pattern + "” 的内容（扫描了 " + scanned + " 个文件）";
        StringBuilder sb = new StringBuilder();
        sb.Append("匹配 “").Append(pattern).Append("”，共 ").Append(hits.Count).Append(" 条（扫描 ").Append(scanned).Append(" 个文件）：\n");
        foreach (string h in hits) sb.Append(h).Append('\n');
        if (hits.Count >= limit) sb.Append("（已达上限 ").Append(limit).Append(" 条）\n");
        return Txt.Clip(sb.ToString(), 6000);
    }


    // ---------- 只读档闸门（权限档「仅可查看」）----------
    // 放在这里而不是 UI 层：GATE 是硬约束，跟用户点没点"允许"无关。
    // 以前 readonly 和 workspace 行为完全一样（都走审批），那档等于装饰 —— 现在它真的拦。
    static readonly string[] WriteTools = new string[] { "write_file", "edit_file", "run_shell", "run_background", "restart_upgrade" };

    public static bool IsWriteTool(string name)
    {
        foreach (string w in WriteTools) if (w == name) return true;
        return false;
    }

    // 计划模式要拦下的工具：除了写文件/跑命令，起程序、杀进程、结束后台任务同样是"动手"
    static readonly string[] PlanBlockedExtra = new string[] { "start_program", "kill_process", "job_kill", "restart_upgrade" };

    public static bool IsPlanBlocked(string name)
    {
        if (IsWriteTool(name)) return true;
        foreach (string w in PlanBlockedExtra) if (w == name) return true;
        return false;
    }

    public static void GuardReadOnly(ToolContext ctx, string name)
    {
        if (ctx == null || !ctx.ReadOnly) return;
        if (!IsWriteTool(name)) return;
        throw new ToolException("当前权限档是「仅可查看」，不能执行 " + name + "。\n"
            + "读类操作（list_dir / read_file / search_text / glob / outline / tail_file）照常可用。\n"
            + "需要改动的话，请让用户在输入条左下角把权限档切成「工作区内修改」或「完全权限」。");
    }

    public static string WriteFile(ToolContext ctx, Dictionary<string, object> a)
    {
        string path = ctx.Resolve(A.Str(a, "path", ""));
        string content = A.Str(a, "content", "");
        bool existed = File.Exists(path);
        string old = "";
        if (existed) { try { old = File.ReadAllText(path, Encoding.UTF8); } catch { old = ""; } }

        // 体量护栏（升级需求追加项）：write_file 是**整体覆盖**，没有任何长度限制。
        // 本项目主文件 466KB / 1.2 万行，让模型逐字吐一遍既贵又几乎必然丢内容，
        // 而且丢完不可逆。所以：新内容比原文短一大截时直接拒绝，除非显式 allow_shrink。
        bool allowShrink = A.Bool(a, "allow_shrink", false);
        if (existed && !allowShrink && old.Length > 0)
        {
            string why = ShrinkReason(old, content);
            if (why != null)
                throw new ToolException("拒绝整体覆盖 " + Path.GetFileName(path) + "：" + why + "\n"
                    + "原文 " + old.Length + " 字符 / " + Txt.LineCount(old) + " 行；"
                    + "这次只给了 " + content.Length + " 字符 / " + Txt.LineCount(content) + " 行。\n"
                    + "整体覆盖会把丢掉的部分永久删掉。二选一：\n"
                    + "  ① 用 edit_file 只改要改的那几处（推荐，也省 token）；\n"
                    + "  ② 确实要整篇重写，就把内容补全后再调用，或显式传 allow_shrink=true。");
        }

        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        Changes.Record(path, old, content);
        return (existed ? "覆盖写入 " : "新建文件 ") + path + "（" + content.Length + " 字符）";
    }

    // 返回"这次覆盖看着像内容丢了"的原因；没问题返回 null。
    // 两条判据：相对掉了 30% 以上，**并且**绝对少了 200 字符以上 ——
    // 只用相对判据的话，改一个 50 字符的小文件会频繁误拦。
    public static string ShrinkReason(string oldText, string newText)
    {
        if (oldText == null) return null;
        int o = oldText.Length, n = newText == null ? 0 : newText.Length;
        if (o < 200) return null;                     // 小文件随便覆盖，误拦比漏拦更烦人
        if (n >= o) return null;                      // 没变短
        double dropPct = (o - n) * 100.0 / o;
        if (dropPct < 30.0) return null;              // 掉得不多，可能是正常精简
        if (o - n < 200) return null;                 // 绝对量太小，不算"丢内容"
        return "新内容比原文少了 " + ((int)Math.Round(dropPct)) + "%（"
            + (o - n) + " 字符）—— 像是没写完或者复制中途断了";
    }

    public static string EditFile(ToolContext ctx, Dictionary<string, object> a)
    {
        string path = ctx.Resolve(A.Str(a, "path", ""));
        if (!File.Exists(path)) throw new ToolException("文件不存在：" + path);
        string oldS = A.Str(a, "old_string", "");
        string newS = A.Str(a, "new_string", "");
        bool all = A.Bool(a, "replace_all", false);
        string text = File.ReadAllText(path, Encoding.UTF8);
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(oldS, idx, StringComparison.Ordinal)) >= 0) { count++; idx += oldS.Length; }
        if (count == 0) throw new ToolException("没找到 old_string。请先用 read_file 看清原文，注意空格、缩进和换行要完全一致。");
        if (count > 1 && !all) throw new ToolException("old_string 在文件里出现 " + count + " 次，不唯一。请给出更长的上下文，或把 replace_all 设为 true。");
        string result;
        if (all) result = text.Replace(oldS, newS);
        else
        {
            int p = text.IndexOf(oldS, StringComparison.Ordinal);
            result = text.Substring(0, p) + newS + text.Substring(p + oldS.Length);
        }
        // 落盘前的护栏：C# 源码花括号配平。缺一个 } 是最常见的"改完才在编不过"，
        // 在这里挡住比等 build 报一堆错强。只拦"改完变不平衡"，改动前本来就不平的放行。
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            int before = BraceBalance(text), after = BraceBalance(result);
            if (after != 0 && before == 0)
                throw new ToolException("这次改动会让 " + Path.GetFileName(path) + " 的花括号配不平（差 "
                    + after + " 个，正数=缺右括号，负数=多右括号）。已拒绝写入，文件没动。\n"
                    + "请检查 new_string 里的 { 和 } 是不是成对。");
        }
        File.WriteAllText(path, result, new UTF8Encoding(false));
        Changes.Record(path, text, result);
        return "已修改 " + path + "（替换 " + (all ? count : 1) + " 处）";
    }

    // 花括号净差（{ 记 +1，} 记 -1）。必须跳过字符串和注释 —— 直接数会把
    // "……{" 这类字面量、以及注释里的括号一起算进去，平白误报（C# 5 没有插值字符串，
    // 但仍要处理 @"逐字串"、'c'、// 和 /* */）。
    public static int BraceBalance(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            // 行注释
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                i += 2;
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            // 块注释
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i++;
                continue;
            }
            // 逐字字符串 @"..."（里面 "" 表示一个引号）
            if (c == '@' && i + 1 < s.Length && s[i + 1] == '"')
            {
                i += 2;
                while (i < s.Length)
                {
                    if (s[i] == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                        break;
                    }
                    i++;
                }
                continue;
            }
            // 普通字符串 / 字符字面量
            if (c == '"' || c == '\'')
            {
                char q = c;
                i++;
                while (i < s.Length && s[i] != q)
                {
                    if (s[i] == '\\') i++;
                    i++;
                }
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}') depth--;
        }
        return depth;
    }

    public static string FindShell()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string ps = Path.Combine(win, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(ps)) return ps;
        string cmd = Path.Combine(win, @"System32\cmd.exe");
        return cmd;
    }

    static readonly string[] Dangerous = new string[]
    {
        @"\bformat\s+[a-z]:",
        @"\bdiskpart\b",
        @"\brm\s+-rf\s+/(\s|$)",
        @"\bdel\s+/[sf]\b.*\\\*",
        @"\b(shutdown|Restart-Computer|Stop-Computer)\b",
        @"\breg\s+delete\b",
        @"\bvssadmin\b.*\bdelete\b",
    };

    public static string ScreenCommand(string command)
    {
        foreach (string pat in Dangerous)
            if (Regex.IsMatch(command, pat, RegexOptions.IgnoreCase)) return pat;
        return null;
    }

    // 杀整棵进程树：只 Kill 外壳的话，它启动的子进程会活下来继续跑（自测里踩到过）
    public static void KillTree(Process p)
    {
        if (p == null) return;
        try
        {
            int pid = p.Id;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
                psi.Arguments = "/T /F /PID " + pid;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process t = Process.Start(psi);
                if (t != null)
                {
                    try { t.WaitForExit(5000); } catch { }
                    try { t.Dispose(); } catch { }
                }
            }
            catch { }
            try { p.Kill(); } catch { }        // taskkill 够不到时的兜底
        }
        catch { }
    }

    public static string RunShell(ToolContext ctx, Dictionary<string, object> a)
    {
        string command = A.Str(a, "command", "");
        if (command.Trim().Length == 0) throw new ToolException("命令不能为空");
        int timeout = A.Int(a, "timeout_sec", 0);
        if (timeout <= 0) timeout = ctx.ShellTimeout;

        string shell = FindShell();
        bool usePs = shell.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase);
        string arguments;
        if (usePs)
        {
            // 用 -EncodedCommand 传脚本：不用写临时文件（某些环境下 %TEMP% 不可写），
            // 也彻底避开引号/换行转义问题
            string script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n"
                + "$ProgressPreference='SilentlyContinue'\n" + command + "\n";
            arguments = "-NoProfile -NonInteractive -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }
        else
        {
            arguments = "/c " + command;
        }

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = shell;
        psi.Arguments = arguments;
        psi.WorkingDirectory = ctx.Root;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        Process p = new Process();
        p.StartInfo = psi;
        StringBuilder so = new StringBuilder();
        StringBuilder se = new StringBuilder();
        p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) se.AppendLine(e.Data); };
        int exitCode = -1;
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeout * 1000))
            {
                KillTree(p);
                throw new ToolException("命令超过 " + timeout + " 秒还没结束，已连同它启动的子进程一起终止"
                    + "（以前只杀外壳，子进程会活下来继续跑）。可以加 timeout_sec 参数，或换更快的命令。");
            }
            p.WaitForExit();      // 等异步输出收干净
            exitCode = p.ExitCode;   // 必须在 Dispose 之前取：Dispose 之后再读会抛 ObjectDisposedException
        }
        finally
        {
            // 一定要 Dispose：不释放进程句柄，已退出的子进程会在系统里留一堆"0 线程的死记录"，
            // 之前 list_processes / filelock / tasklist 全被这些残影骗过（2026-10-12 真机踩到）。
            try { p.Dispose(); } catch { }
        }
        StringBuilder outSb = new StringBuilder();
        outSb.Append("退出码 ").Append(exitCode).Append('\n');
        string o = so.ToString().TrimEnd();
        string err = se.ToString().TrimEnd();
        if (o.Length > 0) outSb.Append(o).Append('\n');
        if (err.Length > 0) outSb.Append("[stderr]\n").Append(err).Append('\n');
        if (o.Length == 0 && err.Length == 0) outSb.Append("（没有任何输出）\n");
        return Txt.Clip(outSb.ToString(), 6000);
    }

    // ---------- 程序管理：列进程 / 启动 / 结束 ----------

    public static string ListProcesses(ToolContext ctx, Dictionary<string, object> a)
    {
        string filter = A.Str(a, "name", "").Trim();
        Process[] all = Process.GetProcesses();
        List<Process> hits = new List<Process>();
        for (int i = 0; i < all.Length; i++)
        {
            if (filter.Length == 0 || all[i].ProcessName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                hits.Add(all[i]);
        }
        hits.Sort(delegate(Process x, Process y) { return string.Compare(x.ProcessName, y.ProcessName, StringComparison.OrdinalIgnoreCase); });
        if (hits.Count == 0)
        {
            return filter.Length == 0 ? "没有列出任何进程。" : "没有找到名称包含 \"" + filter + "\" 的进程。";
        }
        StringBuilder sb = new StringBuilder();
        sb.Append("共 ").Append(hits.Count).Append(" 个进程：\n");
        for (int i = 0; i < hits.Count && i < 300; i++)
        {
            Process p = hits[i];
            long mem = 0;
            try { mem = p.WorkingSet64; } catch { }
            sb.Append("PID ").Append(p.Id.ToString().PadLeft(6))
              .Append("  ").Append(p.ProcessName.PadRight(24))
              .Append(mem > 0 ? (mem / 1024 / 1024).ToString() + " MB" : "无法读取")
              .Append('\n');
        }
        if (hits.Count > 300) sb.Append("（只显示前 300 个，共 ").Append(hits.Count).Append("，可用 name 参数过滤）\n");
        return Txt.Clip(sb.ToString(), 6000);
    }

    public static string StartProgram(ToolContext ctx, Dictionary<string, object> a)
    {
        string path = A.Str(a, "path", "").Trim();
        if (path.Length == 0) throw new ToolException("path 不能为空");
        string args = A.Str(a, "args", "");
        string workDir = A.Str(a, "working_dir", "");
        if (workDir.Length > 0) workDir = ctx.Resolve(workDir);

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = path;
        if (args.Trim().Length > 0) psi.Arguments = args;
        psi.UseShellExecute = true;   // 支持程序名（走 PATH）、网址、文档，也让 GUI 程序正常弹窗
        if (workDir.Length > 0 && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;
        try
        {
            Process p = Process.Start(psi);
            string pname = p != null ? p.ProcessName : "";
            return "已启动：" + path + (args.Trim().Length > 0 ? " " + args : "")
                + (pname.Length > 0 ? "（进程名 " + pname + "）" : "") + "\n";
        }
        catch (Exception ex)
        {
            throw new ToolException("启动失败：" + ex.Message + "（检查路径是否正确；程序名需要能在 PATH 里找到）");
        }
    }

    public static string KillProcess(ToolContext ctx, Dictionary<string, object> a)
    {
        int pid = A.Int(a, "pid", 0);
        string name = A.Str(a, "name", "").Trim();
        if (pid <= 0 && name.Length == 0)
            throw new ToolException("必须给 pid 或 name 之一（建议先 list_processes 确认目标）");

        int ok = 0, fail = 0;
        StringBuilder sb = new StringBuilder();

        if (pid > 0)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                string pname = p.ProcessName;
                if (IsCriticalSelf(pname)) throw new ToolException("拒绝结束关键系统进程或自身（" + pname + "）");
                p.Kill();
                p.WaitForExit(5000);
                ok++;
                sb.Append("已结束 PID ").Append(pid).Append("（").Append(pname).Append("）\n");
            }
            catch (ArgumentException)
            {
                fail++;
                sb.Append("PID ").Append(pid).Append(" 不存在\n");
            }
            catch (ToolException) { throw; }
            catch (Exception ex)
            {
                fail++;
                sb.Append("PID ").Append(pid).Append(" 结束失败：").Append(ex.Message).Append('\n');
            }
        }
        else
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (IsCriticalSelf(name)) throw new ToolException("拒绝结束关键系统进程或自身（" + name + "）");
            Process[] targets = Process.GetProcessesByName(name);
            if (targets.Length == 0) throw new ToolException("没有找到名为 \"" + name + "\" 的进程");
            for (int i = 0; i < targets.Length; i++)
            {
                try
                {
                    targets[i].Kill();
                    targets[i].WaitForExit(5000);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    sb.Append("PID ").Append(targets[i].Id).Append(" 结束失败：").Append(ex.Message).Append('\n');
                }
            }
            sb.Append("已结束 ").Append(ok).Append(" 个 \"").Append(name).Append("\" 进程");
            if (fail > 0) sb.Append("，").Append(fail).Append(" 个失败");
            sb.Append('\n');
            return sb.ToString();
        }
        return sb.ToString();
    }

    static bool IsCriticalSelf(string pname)
    {
        if (pname == null) return false;
        HashSet<string> guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csrss", "wininit", "winlogon", "services", "lsass", "smss", "explorer",
            "AhzScribe", "ahzscribe-cli"
        };
        return guard.Contains(pname);
    }
}

// ============================ 插件：给 agent 加工具 ============================
// 插件就是程序目录 plugins\ 下的一个 json 文件，里面声明若干工具。
// 支持两种类型：
//   shell —— 跑一条 PowerShell 命令（始终需要用户确认，且过一遍危险命令拦截）
//   http  —— 请求一个网址（默认也需要确认，插件可写 "approval": false 关掉）
// 参数用 {{参数名}} 占位：shell 里会被安全转义成 PowerShell 字符串，http 里会做 URL 编码。

class PluginParam
{
    public string name { get; set; }
    public string type { get; set; }
    public string description { get; set; }
    public bool required { get; set; }
}

class PluginToolDef
{
    public string name { get; set; }
    public string description { get; set; }
    public string type { get; set; }        // shell / http，默认 shell
    public string command { get; set; }     // type=shell：PowerShell 命令
    public string method { get; set; }      // type=http：GET / POST
    public string url { get; set; }         // type=http：网址
    public string body { get; set; }        // type=http：POST 正文
    public Dictionary<string, string> headers { get; set; }
    public bool? approval { get; set; }     // 默认 true；shell 类型强制需要确认
    public int timeout_sec { get; set; }
    public int max_chars { get; set; }
    public List<PluginParam> parameters { get; set; }
}

class PluginFile
{
    public string name { get; set; }
    public string version { get; set; }
    public string description { get; set; }
    public List<PluginToolDef> tools { get; set; }
    public List<PluginPrompt> prompts { get; set; }   // 插件提供的"技能"：一段要求，会追加到系统提示里
}

class PluginPrompt
{
    public string name { get; set; }
    public string content { get; set; }
}

class MarketEntry
{
    public string name { get; set; }
    public string description { get; set; }
    public string url { get; set; }
}

class MarketFile
{
    public string name { get; set; }
    public List<MarketEntry> plugins { get; set; }
}

class PluginInfo
{
    public string Path;
    public PluginFile File;
    public string Error;

    public string DisplayName
    {
        get
        {
            if (File != null && !string.IsNullOrEmpty(File.name)) return File.name;
            return System.IO.Path.GetFileNameWithoutExtension(Path);
        }
    }

    public List<PluginToolDef> Tools
    {
        get { return File != null && File.tools != null ? File.tools : new List<PluginToolDef>(); }
    }
}

static class Plugins
{
    public static string Dir
    {
        get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins"); }
    }

    public static string EnsureDir()
    {
        try { if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir); }
        catch { }
        return Dir;
    }

    public static List<PluginInfo> Load()
    {
        List<PluginInfo> list = new List<PluginInfo>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            string[] files = Directory.GetFiles(Dir, "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                PluginInfo info = new PluginInfo();
                info.Path = f;
                try
                {
                    PluginFile pf = Json.Ser.Deserialize<PluginFile>(File.ReadAllText(f, Encoding.UTF8));
                    if (pf == null) info.Error = "文件是空的，或者不是合法的 JSON";
                    else
                    {
                        if (pf.tools == null) pf.tools = new List<PluginToolDef>();
                        if (pf.prompts == null) pf.prompts = new List<PluginPrompt>();
                        info.File = pf;
                    }
                }
                catch (Exception ex) { info.Error = ex.Message; }
                list.Add(info);
            }
        }
        catch { }
        return list;
    }

    public static bool IsEnabled(AgentConfig config, string path)
    {
        if (config == null || config.disabled_plugins == null) return true;
        string key = System.IO.Path.GetFileName(path);
        foreach (string s in config.disabled_plugins)
            if (string.Equals(s, key, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static void SetEnabled(AgentConfig config, string path, bool on)
    {
        if (config.disabled_plugins == null) config.disabled_plugins = new List<string>();
        string key = System.IO.Path.GetFileName(path);
        for (int i = config.disabled_plugins.Count - 1; i >= 0; i--)
            if (string.Equals(config.disabled_plugins[i], key, StringComparison.OrdinalIgnoreCase))
                config.disabled_plugins.RemoveAt(i);
        if (!on) config.disabled_plugins.Add(key);
    }

    public static int EnabledToolCount(AgentConfig config, string path)
    {
        foreach (PluginInfo info in Load())
            if (string.Equals(info.Path, path, StringComparison.OrdinalIgnoreCase))
                return IsEnabled(config, path) ? info.Tools.Count : 0;
        return 0;
    }

    // 把启用的插件工具变成 agent 能调用的工具；名字和内置工具撞了就跳过（不覆盖内置）
    public static List<ToolDef> ToolDefs(AgentConfig config, HashSet<string> taken)
    {
        List<ToolDef> result = new List<ToolDef>();
        foreach (PluginInfo info in Load())
        {
            if (info.File == null || !IsEnabled(config, info.Path)) continue;
            foreach (PluginToolDef t in info.Tools)
            {
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                string name = t.name.Trim();
                if (taken.Contains(name)) continue;
                string kind = string.IsNullOrEmpty(t.type) ? "shell" : t.type.Trim().ToLowerInvariant();
                bool shell = kind != "http";
                string desc = string.IsNullOrEmpty(t.description) ? "插件工具（来自 " + info.DisplayName + "）"
                                                                 : t.description + "（插件：" + info.DisplayName + "）";
                PluginToolDef captured = t;
                bool needsApproval = shell || !(t.approval.HasValue && t.approval.Value == false);
                ToolDef def = new ToolDef(name, desc, needsApproval,
                    delegate(ToolContext ctx, Dictionary<string, object> args) { return Run(ctx, captured, shell, args); });
                if (t.parameters != null)
                    foreach (PluginParam p in t.parameters)
                    {
                        if (p == null || string.IsNullOrEmpty(p.name)) continue;
                        string ty = string.IsNullOrEmpty(p.type) ? "string" : p.type;
                        def.Prop(p.name, ty, p.description == null ? "" : p.description, p.required);
                    }
                taken.Add(name);
                result.Add(def);
            }
        }
        return result;
    }

    static string Run(ToolContext ctx, PluginToolDef t, bool shell, Dictionary<string, object> args)
    {
        if (t.parameters != null)
            foreach (PluginParam p in t.parameters)
                if (p != null && p.required && (args == null || !args.ContainsKey(p.name)))
                    throw new ToolException("缺少必填参数 " + p.name);

        if (shell)
        {
            string cmd = Substitute(t.command, args, true);
            if (cmd.Trim().Length == 0) throw new ToolException("插件工具没有配置 command");
            string hit = Tools.ScreenCommand(cmd);
            if (hit != null) throw new ToolException("这条命令被安全规则拦下了（" + hit + "）。");
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["command"] = cmd;
            a["timeout_sec"] = t.timeout_sec > 0 ? t.timeout_sec : 0;
            return Clip(t.max_chars, Tools.RunShell(ctx, a));
        }

        string url = Substitute(t.url, args, false);
        if (string.IsNullOrEmpty(url)) throw new ToolException("插件工具没有配置 url");
        int timeout = (t.timeout_sec > 0 ? t.timeout_sec : 20) * 1000;
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = string.IsNullOrEmpty(t.method) ? "GET" : t.method.Trim().ToUpperInvariant();
        req.Timeout = timeout;
        req.UserAgent = "AhzScribe/" + Program.Version + " (local agent)";
        if (t.headers != null)
            foreach (KeyValuePair<string, string> kv in t.headers)
            {
                try { req.Headers[kv.Key] = Substitute(kv.Value, args, false); }
                catch { }
            }
        if (req.Method != "GET" && req.Method != "HEAD" && !string.IsNullOrEmpty(t.body))
        {
            byte[] data = Encoding.UTF8.GetBytes(Substitute(t.body, args, false));
            req.ContentType = "application/json; charset=utf-8";
            req.ContentLength = data.Length;
            using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
        }
        string text;
        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            text = sr.ReadToEnd();
        return Clip(t.max_chars, StripHtml(text));
    }

    static string Substitute(string template, Dictionary<string, object> args, bool forShell)
    {
        if (string.IsNullOrEmpty(template)) return "";
        string s = template;
        if (args != null)
            foreach (KeyValuePair<string, object> kv in args)
            {
                string v = kv.Value == null ? "" : Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
                string rep = forShell ? ("'" + v.Replace("'", "''") + "'") : Uri.EscapeDataString(v);
                s = s.Replace("{{" + kv.Key + "}}", rep);
            }
        return s;
    }

    static string StripHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOf('<') < 0) return s;
        s = Regex.Replace(s, "(?is)<script.*?</script>", " ");
        s = Regex.Replace(s, "(?is)<style.*?</style>", " ");
        s = Regex.Replace(s, "(?s)<[^>]+>", " ");
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"");
        s = Regex.Replace(s, "[ \t\r\f\v]+", " ");
        s = Regex.Replace(s, "\n{3,}", "\n\n");
        return s.Trim();
    }

    static string Clip(int maxChars, string s)
    {
        int m = maxChars > 0 ? maxChars : 6000;
        if (s == null) return "";
        if (s.Length <= m) return s;
        return s.Substring(0, m) + "\n…（内容较长，已截断到 " + m + " 字符）";
    }

    // ---------- 插件提供的"技能"（一段会被追加到系统提示里的要求）----------

    public static string PromptText(AgentConfig config)
    {
        StringBuilder sb = new StringBuilder();
        foreach (PluginInfo info in Load())
        {
            if (info.File == null || !IsEnabled(config, info.Path)) continue;
            if (info.File.prompts == null) continue;
            foreach (PluginPrompt p in info.File.prompts)
            {
                if (p == null || string.IsNullOrEmpty(p.content)) continue;
                sb.Append("- ");
                if (!string.IsNullOrEmpty(p.name)) sb.Append("【" + p.name.Trim() + "】");
                sb.Append(p.content.Trim());
                sb.Append("\n");
            }
        }
        return sb.ToString();
    }

    public static int CountPrompts(PluginInfo info)
    {
        if (info == null || info.File == null || info.File.prompts == null) return 0;
        int n = 0;
        foreach (PluginPrompt p in info.File.prompts) if (p != null && !string.IsNullOrEmpty(p.content)) n++;
        return n;
    }

    // ---------- 安装 / 市场 ----------

    public static string SanitizeFileName(string name)
    {
        string s = name == null ? "" : name.Trim();
        char[] bad = System.IO.Path.GetInvalidFileNameChars();
        foreach (char c in bad) s = s.Replace(c, '_');
        s = s.Replace("/", "_").Replace("\\", "_").Replace("..", "_");
        if (s.Length > 60) s = s.Substring(0, 60);
        s = s.Trim().Trim('.', ' ');
        if (s.Length == 0) s = "plugin";
        return s + ".json";
    }

    // 支持三种写法：http(s) 直链 / owner/repo 或 github 链接 / 本地 json 文件
    public static string ResolveUrl(string input, out string err)
    {
        err = null;
        string s = input == null ? "" : input.Trim().Trim('"');
        if (s.Length == 0) { err = "地址是空的"; return null; }
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (s.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                string[] gp = s.Substring("https://github.com/".Length).Trim('/').Split('/');
                if (gp.Length >= 2)
                    return "https://raw.githubusercontent.com/" + gp[0] + "/" + gp[1] + "/HEAD/plugin.json";
                err = "GitHub 链接至少要写到 作者/仓库";
                return null;
            }
            return s;
        }
        if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            return ResolveUrl("https://" + s, out err);
        if (File.Exists(s)) return s;
        if (File.Exists(System.IO.Path.Combine(Dir, s))) return System.IO.Path.Combine(Dir, s);
        if (s.IndexOf('/') > 0 && s.IndexOf(' ') < 0 && s.IndexOf(':') < 0)
        {
            string[] parts = s.Split('/');
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                return "https://raw.githubusercontent.com/" + parts[0] + "/" + parts[1] + "/HEAD/plugin.json";
        }
        err = "看不懂这个地址：填 http(s) 直链、GitHub 的 作者/仓库，或者 plugins\\ 里已有的 json 文件名 / 本地文件路径";
        return null;
    }

    public static string DownloadText(string url, int timeoutSec)
    {
        if (File.Exists(url)) return File.ReadAllText(url, Encoding.UTF8);
        string local = System.IO.Path.Combine(Dir, url);
        if (File.Exists(local)) return File.ReadAllText(local, Encoding.UTF8);
        Program.EnsureTls();
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = (timeoutSec > 0 ? timeoutSec : 20) * 1000;
        req.UserAgent = "AhzScribe/" + Program.Version;
        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }

    // 校验一段插件 json，合格才返回解析结果
    public static PluginFile Parse(string text, out string err)
    {
        err = null;
        if (string.IsNullOrEmpty(text)) { err = "内容是空的"; return null; }
        PluginFile pf = null;
        try { pf = Json.Ser.Deserialize<PluginFile>(text); }
        catch (Exception ex) { err = "不是合法的 JSON（" + ex.Message + "）"; return null; }
        if (pf == null) { err = "内容是空的"; return null; }
        if (string.IsNullOrEmpty(pf.name)) { err = "缺少 name（插件的名字）"; return null; }
        if (pf.tools == null) pf.tools = new List<PluginToolDef>();
        if (pf.prompts == null) pf.prompts = new List<PluginPrompt>();
        if (pf.tools.Count == 0 && pf.prompts.Count == 0) { err = "里面既没有 tools 也没有 prompts，装进去也没用"; return null; }
        foreach (PluginToolDef t in pf.tools)
        {
            if (t == null || string.IsNullOrEmpty(t.name)) { err = "有工具没有写 name"; return null; }
            string kind = string.IsNullOrEmpty(t.type) ? "shell" : t.type.Trim().ToLowerInvariant();
            if (kind != "shell" && kind != "http") { err = "工具 " + t.name + " 的 type 只能是 shell 或 http"; return null; }
            if (kind == "shell" && string.IsNullOrEmpty(t.command)) { err = "工具 " + t.name + " 是 shell 类型但没写 command"; return null; }
            if (kind == "http" && string.IsNullOrEmpty(t.url)) { err = "工具 " + t.name + " 是 http 类型但没写 url"; return null; }
        }
        foreach (PluginPrompt p in pf.prompts)
            if (p == null || string.IsNullOrEmpty(p.content)) { err = "prompts 里有条目没写 content"; return null; }
        return pf;
    }

    public static string SourceOf(AgentConfig cfg, string path)
    {
        if (cfg == null || cfg.plugin_sources == null) return "";
        string key = System.IO.Path.GetFileName(path);
        string v;
        return cfg.plugin_sources.TryGetValue(key, out v) ? v : "";
    }

    // 真正落地：写文件 + 记来源
    public static bool Install(string text, AgentConfig cfg, string sourceUrl, bool overwrite, out string msg, out string installedPath)
    {
        msg = null;
        installedPath = null;
        string err;
        PluginFile pf = Parse(text, out err);
        if (pf == null) { msg = "插件没通过检查：" + err; return false; }
        string file = SanitizeFileName(pf.name);
        string path = System.IO.Path.Combine(EnsureDir(), file);
        if (File.Exists(path) && !overwrite) { msg = "已经有个同名插件了（" + file + "）。要替换请勾上「覆盖已存在的插件」。"; return false; }
        try { File.WriteAllText(path, text, new UTF8Encoding(false)); }
        catch (Exception ex) { msg = "写入失败：" + ex.Message; return false; }
        if (cfg != null)
        {
            if (cfg.plugin_sources == null) cfg.plugin_sources = new Dictionary<string, string>();
            cfg.plugin_sources[file] = (string.IsNullOrEmpty(sourceUrl) ? "本地文件" : sourceUrl)
                + "   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }
        installedPath = path;
        msg = "已安装：" + pf.name + "  →  " + file;
        return true;
    }

    // 从地址下载并安装（地址可以是直链、owner/repo、本地文件）
    public static bool InstallFromUrl(string input, AgentConfig cfg, bool overwrite, out string msg, out PluginFile parsed, out string usedUrl, out string installedPath)
    {
        msg = null; parsed = null; usedUrl = null; installedPath = null;
        string err;
        string url = ResolveUrl(input, out err);
        if (url == null) { msg = err; return false; }
        usedUrl = url;
        string text;
        try { text = DownloadText(url, 20); }
        catch (Exception ex) { msg = "下载失败：" + ex.Message; return false; }
        PluginFile pf = Parse(text, out err);
        if (pf == null) { msg = "下载到的内容不是合格插件：" + err; return false; }
        parsed = pf;
        return Install(text, cfg, url, overwrite, out msg, out installedPath);
    }

    public static string MarketPath
    {
        get { return System.IO.Path.Combine(EnsureDir(), "market.json"); }
    }

    public static List<MarketEntry> LoadMarket(AgentConfig cfg, out string err, out string source)
    {
        err = null;
        source = null;
        string text = null;
        if (cfg != null && !string.IsNullOrEmpty(cfg.plugin_market_url))
        {
            source = cfg.plugin_market_url;
            try { text = DownloadText(cfg.plugin_market_url, 20); }
            catch (Exception ex) { err = "市场地址拉取失败：" + ex.Message; return new List<MarketEntry>(); }
        }
        else if (File.Exists(MarketPath))
        {
            source = MarketPath;
            try { text = File.ReadAllText(MarketPath, Encoding.UTF8); }
            catch (Exception ex) { err = "读本地市场文件失败：" + ex.Message; return new List<MarketEntry>(); }
        }
        else
        {
            err = "还没有市场索引：可以填一个市场地址，或者点「新建示例市场文件」。";
            return new List<MarketEntry>();
        }
        try
        {
            MarketFile mf = Json.Ser.Deserialize<MarketFile>(text);
            if (mf == null || mf.plugins == null) { err = "市场文件的格式不对（要有 plugins 数组）"; return new List<MarketEntry>(); }
            return mf.plugins;
        }
        catch (Exception ex) { err = "市场文件不是合法 JSON：" + ex.Message; return new List<MarketEntry>(); }
    }

    public const string SampleMarketJson = @"{
  ""name"": ""我的插件市场"",
  ""plugins"": [
    { ""name"": ""示例：小工具"", ""description"": ""插件目录里自带的示例（url 用相对路径）"", ""url"": ""示例-小工具.json"" },
    { ""name"": ""网页抓取（示例条目）"", ""description"": ""把 url 换成你自己的 json 直链，或者 GitHub 的 作者/仓库"", ""url"": ""你的名字/你的插件仓库"" }
  ]
}";

    public static string WriteSampleMarket()
    {
        EnsureDir();
        if (!File.Exists(MarketPath)) File.WriteAllText(MarketPath, SampleMarketJson, new UTF8Encoding(false));
        return MarketPath;
    }

    public const string SampleFileName = "示例-小工具.json";

    public const string SampleJson = @"{
  ""name"": ""示例：小工具"",
  ""version"": ""1.0"",
  ""description"": ""演示插件格式，可以直接改，也可以删掉。拿走不谢。"",
  ""prompts"": [
    { ""name"": ""回答风格"", ""content"": ""回答尽量先给结论，再给一句理由；涉及数字时不要自己估算。"" }
  ],
  ""tools"": [
    {
      ""name"": ""now_time"",
      ""description"": ""查询当前日期和时间。"",
      ""type"": ""shell"",
      ""command"": ""Get-Date -Format 'yyyy-MM-dd HH:mm:ss dddd'"",
      ""parameters"": []
    },
    {
      ""name"": ""line_count"",
      ""description"": ""统计某个文本文件有多少行。"",
      ""type"": ""shell"",
      ""command"": ""(Get-Content -LiteralPath {{path}} | Measure-Object -Line).Lines"",
      ""parameters"": [
        { ""name"": ""path"", ""type"": ""string"", ""description"": ""文件路径（相对工作目录）"", ""required"": true }
      ]
    }
  ]
}";

    public static string WriteSample()
    {
        EnsureDir();
        string f = System.IO.Path.Combine(Dir, SampleFileName);
        if (!File.Exists(f)) File.WriteAllText(f, SampleJson, new UTF8Encoding(false));
        return f;
    }
}

// ============================ Ollama 客户端 ============================

class OllamaClient
{
    public string Host;
    private HttpWebRequest _current;
    private readonly object _lock = new object();

    public OllamaClient(string host)
    {
        if (host == null || host.Trim().Length == 0) host = "127.0.0.1:11434";
        host = host.Trim();
        if (!host.StartsWith("http")) host = "http://" + host;
        Host = host.TrimEnd('/');
    }

    public void Cancel()
    {
        lock (_lock) { if (_current != null) { try { _current.Abort(); } catch { } } }
    }

    HttpWebRequest Make(string path, string method, string json)
    {
        Program.EnsureTls();
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Host + path);
        req.Method = method;
        req.Proxy = null;
        req.Timeout = 900000;
        req.ReadWriteTimeout = 900000;
        req.KeepAlive = false;
        if (json != null)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            req.ContentType = "application/json";
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
        }
        lock (_lock) { _current = req; }
        return req;
    }

    static string ReadBody(WebResponse resp)
    {
        using (Stream s = resp.GetResponseStream())
        using (StreamReader r = new StreamReader(s, Encoding.UTF8))
            return r.ReadToEnd();
    }

    public Dictionary<string, object> Show(string model)
    {
        Dictionary<string, object> payload = new Dictionary<string, object>();
        payload["model"] = model;
        Dictionary<string, object> result = PostJson("/api/show", payload);
        if (result == null) throw new LocalAgentException("读不到模型信息：" + model);
        return result;
    }

    public Dictionary<string, object> PostJson(string path, Dictionary<string, object> payload)
    {
        HttpWebRequest req = Make(path, "POST", Json.Dump(payload));
        try
        {
            using (WebResponse resp = req.GetResponse())
                return Json.Obj(ReadBody(resp));
        }
        catch (WebException ex) { throw new LocalAgentException(Describe(ex)); }
        finally { lock (_lock) { _current = null; } }
    }

    public List<Dictionary<string, object>> Tags()
    {
        List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
        HttpWebRequest req = Make("/api/tags", "GET", null);
        try
        {
            using (WebResponse resp = req.GetResponse())
            {
                Dictionary<string, object> o = Json.Obj(ReadBody(resp));
                object[] arr = Json.Arr(Json.Get(o, "models"));
                if (arr != null)
                    foreach (object it in arr)
                    {
                        Dictionary<string, object> m = Json.Map(it);
                        if (m != null) list.Add(m);
                    }
            }
        }
        catch (WebException ex) { throw new LocalAgentException(Describe(ex)); }
        finally { lock (_lock) { _current = null; } }
        return list;
    }

    public Dictionary<string, object> ChatStream(Dictionary<string, object> payload, Action<Dictionary<string, object>> onChunk)
    {
        HttpWebRequest req = Make("/api/chat", "POST", Json.Dump(payload));
        Dictionary<string, object> last = null;
        try
        {
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    Dictionary<string, object> chunk;
                    try { chunk = Json.Obj(line); }
                    catch { continue; }
                    if (chunk == null) continue;
                    if (onChunk != null) onChunk(chunk);
                    if (Json.Bool(chunk, "done", false)) last = chunk;
                }
            }
        }
        catch (WebException ex)
        {
            if (ex.Status == WebExceptionStatus.RequestCanceled) throw new LocalAgentException("__CANCELLED__");
            throw new LocalAgentException(Describe(ex));
        }
        finally { lock (_lock) { _current = null; } }
        return last;
    }

    static string Describe(WebException ex)
    {
        try
        {
            if (ex.Response != null)
            {
                string body = ReadBody(ex.Response);
                string msg = null;
                try
                {
                    Dictionary<string, object> o = Json.Obj(body);
                    if (o != null) msg = Json.Str(o, "error", null);
                }
                catch { }
                return "Ollama 返回错误：" + (msg != null ? msg : body);
            }
        }
        catch { }
        return "连不上 Ollama（" + ex.Message + "）\n提示：确认 Ollama 正在运行（托盘里有图标，或执行 ollama serve）。";
    }
}

// ============================ 模型调用层 ============================
// 中立消息约定（AgentRunner 只认这一套，各家协议在发送前转换）：
//   assistant : { role:"assistant", content, tool_calls:[ {id, name, arguments:{...}} ] }
//   tool      : { role:"tool", content, tool_call_id, tool_name }

interface ILlm
{
    string Kind { get; }
    Dictionary<string, object> Stream(
        List<Dictionary<string, object>> messages, List<object> tools, bool think, string effort, int numCtx,
        Action<string> onThinking, Action<string> onContent, Action<Dictionary<string, object>> onToolCall);
    void Cancel();
    Dictionary<string, object> Probe();     // 能力探测，OpenAI 兼容接口返回 null
    List<string> ListModels();
    void Close();
}

// ---------- 本地 Ollama ----------

// 权限档（Codex 那三档）。界面上是「仅可查看 / 工作区内修改 / 完全权限」，
// 存进 config.permission，auto_yes 跟着它同步（老配置只有 auto_yes，按它迁移）。
static class Perm
{
    public const string Readonly = "readonly";
    public const string Workspace = "workspace";
    public const string Full = "full";

    public static readonly string[] Values = new string[] { Readonly, Workspace, Full };
    public static readonly string[] Labels = new string[] { "仅可查看", "工作区内修改", "完全权限" };
    public static readonly string[] Icons2 = new string[] { "eye", "folder", "warn" };

    public static int Index(string v)
    {
        for (int i = 0; i < Values.Length; i++) if (Values[i] == v) return i;
        return 1;                      // 认不出的按「工作区内修改」
    }

    public static string Label(string v) { return Labels[Index(v)]; }
    public static string Icon(string v) { return Icons2[Index(v)]; }
    public static string ValueAt(int i) { return Values[Math.Max(0, Math.Min(Values.Length - 1, i))]; }

    // 完全权限 = 不再逐条问（原来那个 auto_yes）
    public static bool AutoYes(string v) { return v == Full; }

    // 只读档：写类工具（write_file / edit_file / run_shell）一律拒绝。
    // 以前 readonly 和 workspace 行为完全一样，那档是装饰的 —— 现在真的生效。
    public static bool IsReadOnly(string v) { return v == Readonly; }
}

// 推理等级：界面上是 自动/低/中/高，落到请求参数时各家叫法不一样，集中在这里翻译
static class Effort
{
    public static readonly string[] Levels = new string[] { "low", "medium", "high" };

    public static bool IsSet(string e) { return e == "low" || e == "medium" || e == "high"; }

    public static string Label(string e)
    {
        if (e == "low") return "低";
        if (e == "high") return "高";
        return "中";        // medium，以及没设过（老配置里的 auto / 空）都按默认的「中」显示
    }

    // 没设过等级的（老配置里的 auto）算默认档「中」
    public static string Normalize(string e) { return IsSet(e) ? e : "medium"; }

    public static int Index(string e)
    {
        string n = Normalize(e);
        for (int i = 0; i < Levels.Length; i++) if (Levels[i] == n) return i;
        return 1;
    }

    // Ollama：think 既可以是 bool，也可以是 "low|medium|high"；没选等级就保持原来的 bool
    public static object OllamaThink(bool think, string e)
    {
        if (IsSet(e)) return e;
        return think;
    }

    // OpenAI 兼容（DeepSeek / 智谱等）：只有选了等级才带 reasoning_effort，没选就一个字段都不加
    public static string OpenAiEffort(string e)
    {
        return IsSet(e) ? e : null;
    }

    // 这个模型能不能调档（决定界面上显不显示滑块）：
    //   本地 Ollama：能力探测给出的 thinking.values 里要有 low/medium/high（只有 true/false 只是"能开关思考"）
    //   OpenAI 兼容：没有统一探测接口，只认白名单；认不出的宁可不显示滑块，也不给假档位
    public static bool Supported(string providerType, string model, string thinkingValues)
    {
        if (providerType == "ollama")
        {
            if (string.IsNullOrEmpty(thinkingValues)) return false;
            string[] parts = thinkingValues.Split('/');
            foreach (string s in parts)
                if (s == "low" || s == "medium" || s == "high") return true;
            return false;
        }
        return CloudSupportsEffort(model);
    }

    public static bool CloudSupportsEffort(string model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        string m = model.ToLowerInvariant();
        if (m.IndexOf("deepseek-reasoner") >= 0) return true;
        if (m.IndexOf("deepseek-r") == 0) return true;
        if (m.IndexOf("gpt-5") >= 0) return true;
        if (m.IndexOf("gpt-oss") >= 0) return true;
        if (m.IndexOf("o1") == 0 || m.IndexOf("o3") == 0 || m.IndexOf("o4") == 0) return true;
        return false;
    }

    // 只给自测用：把"能力探测 + 白名单"两条路一起走一遍
    public static bool TestEffortCapability(string providerType, string model, string thinkingValues)
    {
        return Supported(providerType, model, thinkingValues);
    }
}

class OllamaLlm : ILlm
{
    private readonly OllamaClient _client;
    private readonly string _model;

    public OllamaLlm(string baseUrl, string model)
    {
        _client = new OllamaClient(baseUrl);
        _model = model;
    }

    public string Kind { get { return "ollama"; } }

    public void Cancel() { _client.Cancel(); }

    public void Close() { }

    static List<object> ConvertMessages(List<Dictionary<string, object>> msgs)
    {
        List<object> list = new List<object>();
        foreach (Dictionary<string, object> m in msgs)
        {
            string role = Json.Str(m, "role", "user");
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["role"] = role;
            o["content"] = Json.Str(m, "content", "");
            object[] calls = Json.Arr(Json.Get(m, "tool_calls"));
            if (calls != null && calls.Length > 0)
            {
                List<object> outCalls = new List<object>();
                foreach (object c in calls)
                {
                    Dictionary<string, object> cm = Json.Map(c);
                    if (cm == null) continue;
                    Dictionary<string, object> fn = new Dictionary<string, object>();
                    fn["name"] = Json.Str(cm, "name", "");
                    fn["arguments"] = Json.Get(cm, "arguments") ?? new Dictionary<string, object>();
                    Dictionary<string, object> call = new Dictionary<string, object>();
                    call["function"] = fn;
                    outCalls.Add(call);
                }
                o["tool_calls"] = outCalls;
            }
            list.Add(o);
        }
        return list;
    }

    public Dictionary<string, object> Stream(
        List<Dictionary<string, object>> messages, List<object> tools, bool think, string effort, int numCtx,
        Action<string> onThinking, Action<string> onContent, Action<Dictionary<string, object>> onToolCall)
    {
        Dictionary<string, object> payload = new Dictionary<string, object>();
        payload["model"] = _model;
        payload["messages"] = ConvertMessages(messages);
        // 空工具列表别发出去（Ollama 会报错）；这一轮不给工具时 tools 是 null
        if (tools != null && tools.Count > 0) payload["tools"] = tools;
        payload["stream"] = true;
        payload["think"] = Effort.OllamaThink(think, effort);
        Dictionary<string, object> opts = new Dictionary<string, object>();
        opts["num_ctx"] = numCtx;
        payload["options"] = opts;

        return _client.ChatStream(payload, delegate(Dictionary<string, object> chunk)
        {
            Dictionary<string, object> msg = Json.Map(Json.Get(chunk, "message"));
            if (msg == null) return;
            string th = Json.Str(msg, "thinking", null);
            if (!string.IsNullOrEmpty(th) && onThinking != null) onThinking(th);
            string ct = Json.Str(msg, "content", null);
            if (!string.IsNullOrEmpty(ct) && onContent != null) onContent(ct);
            object[] calls = Json.Arr(Json.Get(msg, "tool_calls"));
            if (calls != null && onToolCall != null)
                foreach (object c in calls)
                {
                    Dictionary<string, object> cm = Json.Map(c);
                    if (cm == null) continue;
                    Dictionary<string, object> fn = Json.Map(Json.Get(cm, "function"));
                    Dictionary<string, object> args = Json.Map(Json.Get(fn, "arguments"));
                    if (args == null)
                    {
                        string raw = Json.Str(fn, "arguments", "");
                        if (raw.Trim().Length > 0) { try { args = Json.Obj(raw); } catch { } }
                    }
                    if (args == null) args = new Dictionary<string, object>();
                    Dictionary<string, object> call = new Dictionary<string, object>();
                    call["id"] = Json.Str(cm, "id", "call_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    call["name"] = Json.Str(fn, "name", "?");
                    call["arguments"] = args;
                    onToolCall(call);
                }
        });
    }

    public Dictionary<string, object> Probe() { return _client.Show(_model); }

    public List<string> ListModels()
    {
        List<string> names = new List<string>();
        foreach (Dictionary<string, object> m in _client.Tags()) names.Add(Json.Str(m, "name", "?"));
        return names;
    }
}

// ---------- OpenAI 兼容接口（DeepSeek / OpenAI / 通义 / Kimi / 智谱 …） ----------

class OpenAiLlm : ILlm
{
    private readonly Provider _p;
    private readonly string _model;
    private HttpWebRequest _current;
    private readonly object _lock = new object();

    public OpenAiLlm(Provider p, string model)
    {
        _p = p;
        _model = model;
    }

    public string Kind { get { return "openai"; } }

    public void Cancel() { lock (_lock) { if (_current != null) { try { _current.Abort(); } catch { } } } }

    public void Close() { }

    public string Root()
    {
        string b = (_p.base_url ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) b = "https://api.openai.com/v1";
        return b;
    }

    HttpWebRequest Make(string url, string method, string json)
    {
        Program.EnsureTls();
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Proxy = null;
        req.Timeout = 900000;
        req.ReadWriteTimeout = 900000;
        req.KeepAlive = false;
        req.Accept = "application/json";
        if (!string.IsNullOrEmpty(_p.api_key)) req.Headers["Authorization"] = "Bearer " + _p.api_key.Trim();
        if (json != null)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            req.ContentType = "application/json";
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
        }
        lock (_lock) { _current = req; }
        return req;
    }

    static string ReadAll(WebResponse resp)
    {
        using (Stream s = resp.GetResponseStream())
        using (StreamReader r = new StreamReader(s, Encoding.UTF8))
            return r.ReadToEnd();
    }

    static string Describe(WebException ex)
    {
        try
        {
            if (ex.Response != null)
            {
                string body = ReadAll(ex.Response);
                string msg = null;
                try
                {
                    Dictionary<string, object> o = Json.Obj(body);
                    Dictionary<string, object> err = Json.Map(Json.Get(o, "error"));
                    if (err != null) msg = Json.Str(err, "message", null);
                    if (msg == null) msg = Json.Str(o, "message", null);
                }
                catch { }
                return "接口返回错误：" + (msg != null ? msg : body);
            }
        }
        catch { }
        return "连不上接口（" + ex.Message + "）\n提示：检查接口地址、API Key 和网络。";
    }

    static List<object> ConvertMessages(List<Dictionary<string, object>> msgs)
    {
        List<object> list = new List<object>();
        foreach (Dictionary<string, object> m in msgs)
        {
            string role = Json.Str(m, "role", "user");
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["role"] = role;
            string content = Json.Str(m, "content", "");
            object[] calls = Json.Arr(Json.Get(m, "tool_calls"));
            if (role == "tool")
            {
                o["content"] = content;
                o["tool_call_id"] = Json.Str(m, "tool_call_id", "");
            }
            else if (calls != null && calls.Length > 0)
            {
                List<object> outCalls = new List<object>();
                foreach (object c in calls)
                {
                    Dictionary<string, object> cm = Json.Map(c);
                    if (cm == null) continue;
                    Dictionary<string, object> fn = new Dictionary<string, object>();
                    fn["name"] = Json.Str(cm, "name", "");
                    fn["arguments"] = Json.Dump(Json.Get(cm, "arguments") ?? new Dictionary<string, object>());
                    Dictionary<string, object> call = new Dictionary<string, object>();
                    call["id"] = Json.Str(cm, "id", "call_0");
                    call["type"] = "function";
                    call["function"] = fn;
                    outCalls.Add(call);
                }
                o["tool_calls"] = outCalls;
                o["content"] = content.Length > 0 ? content : null;
            }
            else
            {
                o["content"] = content;
            }
            list.Add(o);
        }
        return list;
    }

    public Dictionary<string, object> Stream(
        List<Dictionary<string, object>> messages, List<object> tools, bool think, string effort, int numCtx,
        Action<string> onThinking, Action<string> onContent, Action<Dictionary<string, object>> onToolCall)
    {
        Dictionary<string, object> payload = new Dictionary<string, object>();
        payload["model"] = _model;
        payload["messages"] = ConvertMessages(messages);
        if (tools != null && tools.Count > 0)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }
        payload["stream"] = true;
        string eff = Effort.OpenAiEffort(effort);
        if (eff != null) payload["reasoning_effort"] = eff;

        Dictionary<int, Dictionary<string, object>> pending = new Dictionary<int, Dictionary<string, object>>();
        Dictionary<string, object> stats = null;
        HttpWebRequest req = Make(Root() + "/chat/completions", "POST", Json.Dump(payload));
        try
        {
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    if (!line.StartsWith("data:")) continue;
                    string data = line.Substring(5).Trim();
                    if (data == "[DONE]") break;
                    Dictionary<string, object> chunk;
                    try { chunk = Json.Obj(data); }
                    catch { continue; }
                    Dictionary<string, object> usage = Json.Map(Json.Get(chunk, "usage"));
                    if (usage != null) stats = usage;
                    object[] choices = Json.Arr(Json.Get(chunk, "choices"));
                    if (choices == null || choices.Length == 0) continue;
                    Dictionary<string, object> ch = Json.Map(choices[0]);
                    if (ch == null) continue;
                    Dictionary<string, object> delta = Json.Map(Json.Get(ch, "delta"));
                    if (delta == null) continue;
                    string th = Json.Str(delta, "reasoning_content", null);
                    if (string.IsNullOrEmpty(th)) th = Json.Str(delta, "reasoning", null);
                    if (!string.IsNullOrEmpty(th) && onThinking != null) onThinking(th);
                    string ct = Json.Str(delta, "content", null);
                    if (!string.IsNullOrEmpty(ct) && onContent != null) onContent(ct);
                    object[] tcs = Json.Arr(Json.Get(delta, "tool_calls"));
                    if (tcs != null)
                        foreach (object t in tcs)
                        {
                            Dictionary<string, object> tc = Json.Map(t);
                            if (tc == null) continue;
                            int idx = Json.Int(tc, "index", 0);
                            Dictionary<string, object> acc;
                            if (!pending.TryGetValue(idx, out acc))
                            {
                                acc = new Dictionary<string, object>();
                                acc["id"] = "";
                                acc["name"] = "";
                                acc["args"] = "";
                                pending[idx] = acc;
                            }
                            string id = Json.Str(tc, "id", null);
                            if (!string.IsNullOrEmpty(id)) acc["id"] = id;
                            Dictionary<string, object> fn = Json.Map(Json.Get(tc, "function"));
                            if (fn != null)
                            {
                                string nm = Json.Str(fn, "name", null);
                                if (!string.IsNullOrEmpty(nm) && Convert.ToString(acc["name"]).Length == 0) acc["name"] = nm;
                                string ar = Json.Str(fn, "arguments", null);
                                if (!string.IsNullOrEmpty(ar)) acc["args"] = Convert.ToString(acc["args"]) + ar;
                            }
                        }
                }
            }
        }
        catch (WebException ex)
        {
            if (ex.Status == WebExceptionStatus.RequestCanceled) throw new LocalAgentException("__CANCELLED__");
            throw new LocalAgentException(Describe(ex));
        }
        finally { lock (_lock) { _current = null; } }

        foreach (KeyValuePair<int, Dictionary<string, object>> kv in pending)
        {
            Dictionary<string, object> acc = kv.Value;
            string argsText = Convert.ToString(acc["args"]);
            Dictionary<string, object> args;
            try { args = argsText.Trim().Length > 0 ? Json.Obj(argsText) : new Dictionary<string, object>(); }
            catch { args = new Dictionary<string, object>(); }
            if (args == null) args = new Dictionary<string, object>();
            Dictionary<string, object> call = new Dictionary<string, object>();
            string id = Convert.ToString(acc["id"]);
            call["id"] = id.Length > 0 ? id : "call_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            call["name"] = Convert.ToString(acc["name"]);
            call["arguments"] = args;
            if (onToolCall != null && call["name"].ToString().Length > 0) onToolCall(call);
        }
        return stats;
    }

    public Dictionary<string, object> Probe() { return null; }

    public List<string> ListModels()
    {
        List<string> names = new List<string>();
        HttpWebRequest req = Make(Root() + "/models", "GET", null);
        try
        {
            using (WebResponse resp = req.GetResponse())
            {
                Dictionary<string, object> o = Json.Obj(ReadAll(resp));
                object[] data = Json.Arr(Json.Get(o, "data"));
                if (data != null)
                    foreach (object d in data)
                    {
                        Dictionary<string, object> dm = Json.Map(d);
                        if (dm == null) continue;
                        string id = Json.Str(dm, "id", null);
                        if (!string.IsNullOrEmpty(id)) names.Add(id);
                    }
            }
        }
        catch (WebException ex) { throw new LocalAgentException(Describe(ex)); }
        finally { lock (_lock) { _current = null; } }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }
}

static class LlmFactory
{
    public static ILlm Create(Provider p, string model)
    {
        if (p != null && p.type == "ollama") return new OllamaLlm(p.base_url, model);
        return new OpenAiLlm(p, model);
    }
}

// ============================ 会话配置 ============================

class Provider
{
    public string id { get; set; }
    public string name { get; set; }
    public string type { get; set; }          // ollama = 本地 Ollama；openai = OpenAI 兼容接口
    public string base_url { get; set; }
    public string api_key { get; set; }
    public List<string> models { get; set; }

    public Provider()
    {
        id = "p" + Guid.NewGuid().ToString("N").Substring(0, 6);
        name = "新提供商";
        type = "openai";
        base_url = "";          // 别默认成某一家：新提供商应该由预设或用户自己填
        api_key = "";
        models = new List<string>();
    }

    public static Provider DefaultOllama()
    {
        Provider p = new Provider();
        p.id = "ollama-local";
        p.name = "Ollama 本地";
        p.type = "ollama";
        p.base_url = "http://127.0.0.1:11434";
        p.api_key = "";
        p.models = new List<string>();
        return p;
    }

    public override string ToString()
    {
        return name + "（" + (type == "ollama" ? "本地" : "云端") + "）";
    }
}

class AgentConfig
{
    public List<Provider> providers { get; set; }
    public string provider { get; set; }
    public string model { get; set; }
    public string cwd { get; set; }
    public int num_ctx { get; set; }
    public bool think { get; set; }
    public string effort { get; set; }           // 推理等级：auto / low / medium / high（auto = 老行为，不带这个参数）
    public bool auto_yes { get; set; }
    // 权限档：readonly = 只读（都先问） / workspace = 工作区内修改（默认，都先问） / full = 完全权限（不再问）
    public string permission { get; set; }
    // 计划模式：只调研、只出计划，不动文件（框架层会拦下所有动手类工具，见 Tools.IsPlanBlocked）
    public bool plan_mode { get; set; }
    public int max_steps { get; set; }
    public int shell_timeout { get; set; }
    public bool allow_outside { get; set; }
    public bool auto_verify { get; set; }        // 改完文件后自动跑项目测试，把结果回灌给模型
    public bool follow_output { get; set; }      // 流式输出时自动滚到底（关掉 = 画面完全不动）
    public bool hide_thinking { get; set; }      // 思考内容只进"轨迹"页，对话区只留一行摘要
    public List<string> disabled_plugins { get; set; }   // 被停用的插件文件名
    public Dictionary<string, string> plugin_sources { get; set; }   // 文件名 -> 安装来源
    public string plugin_market_url { get; set; }        // 插件市场索引地址（留空则用 plugins\market.json）

    public AgentConfig()
    {
        providers = new List<Provider>();
        providers.Add(Provider.DefaultOllama());
        provider = providers[0].id;
        model = "qwen3.5:4b";
        cwd = Environment.CurrentDirectory;
        num_ctx = 8192;
        think = true;          // 默认让模型思考（输入条上不再放开关，要关就去「设置 → 通用设置」）
        effort = "auto";
        auto_yes = false;
        permission = "workspace";
        max_steps = 28;
        shell_timeout = 120;
        allow_outside = false;
        auto_verify = true;
        follow_output = true;
        hide_thinking = true;
        disabled_plugins = new List<string>();
        plugin_sources = new Dictionary<string, string>();
        plugin_market_url = "";
    }

    public void Migrate()
    {
        if (providers == null || providers.Count == 0)
        {
            providers = new List<Provider>();
            providers.Add(Provider.DefaultOllama());
            provider = providers[0].id;
        }
        if (provider == null || provider.Length == 0) provider = providers[0].id;
        if (model == null || model.Length == 0) model = "qwen3.5:4b";
        if (cwd == null || cwd.Length == 0) cwd = Environment.CurrentDirectory;
        if (disabled_plugins == null) disabled_plugins = new List<string>();
        if (plugin_sources == null) plugin_sources = new Dictionary<string, string>();
        if (plugin_market_url == null) plugin_market_url = "";
        // 单轮工具步数：默认 28（老默认 12 在多文件改+跑测试这类任务上实测有一半概率被掐断）。
        // 这里只补"没填"的情况，不动用户显式设过的值。
        if (max_steps <= 0) max_steps = 28;
        if (effort == null || effort.Length == 0) effort = "auto";     // 老配置文件没有这一项
        if (effort != "low" && effort != "medium" && effort != "high") effort = "auto";
        // 权限档：老配置里只有 auto_yes，按它迁移。之后以 permission 为准，auto_yes 跟着它同步。
        if (permission != "readonly" && permission != "workspace" && permission != "full")
            permission = auto_yes ? "full" : "workspace";
        auto_yes = permission == "full";
    }

    public Provider Current()
    {
        Migrate();
        foreach (Provider p in providers) if (p.id == provider) return p;
        provider = providers[0].id;
        return providers[0];
    }

    // 按 id 找提供商（找不到返回 null；模型浮层要按它判断"本地 / 云端"）
    public Provider Find(string id)
    {
        foreach (Provider p in providers) if (p.id == id) return p;
        return null;
    }

    public string Display { get { return Current().name + " / " + model; } }

    public static string FilePath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); }
    }

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AgentConfig c = Json.Ser.Deserialize<AgentConfig>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (c != null) { c.Migrate(); return c; }
            }
        }
        catch { }
        return new AgentConfig();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, Json.Ser.Serialize(this), new UTF8Encoding(false)); }
        catch { }
    }
}

// ============================ 界面接口 ============================

enum OutKind { User, Thinking, Assistant, ToolCall, ToolResult, Error, Info, Stats, Changes }

interface IAgentUI
{
    void Write(OutKind kind, string text);
    int Approve(string toolName, string[] preview);   // 0 拒绝 / 1 允许 / 2 本会话总是允许
    // 越权访问（工作目录外）专用：默认必须是「拒绝」—— 无人值守时（空输入/EOF）绝不能自动放行提权。
    int ApproveOutside(string toolName, string[] preview);
    void Status(string text);
}

// ============================ Agent 核心 ============================

class StepResult
{
    public string Content = "";
    public List<Dictionary<string, object>> Calls = new List<Dictionary<string, object>>();
    public Dictionary<string, object> Stats;
    public long Tokens;      // 归一化后的生成 token 数（本地取 eval_count，云端取 completion_tokens）
    public double Seconds;   // 对应耗时（本地取 eval_duration，云端用实测墙钟时间）
}

// ============================ 项目规矩文件（AGENTS.md / AHZ.md） ============================
// 在工作目录里放一个 AGENTS.md（通用名字，Codex / DeepSeek Harness 也读这个；老名字 AHZ.md 仍兼容，
// 两个都在时 AHZ.md 优先），内容会追加进系统提示；顺便可以声明验证命令：
//   验证命令: python -B run_tests.py
// 没有声明时，会在常见项目里自动认一个安全的验证命令。

static class ProjectRules
{
    public const int MaxChars = 4000;

    public static string Find(string cwd)
    {
        try
        {
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return null;
            string a = Path.Combine(cwd, "AHZ.md");
            if (File.Exists(a)) return a;
            string b = Path.Combine(cwd, "AGENTS.md");
            if (File.Exists(b)) return b;
        }
        catch { }
        return null;
    }

    public static string Read(string cwd, out string path)
    {
        path = null;
        string f = Find(cwd);
        if (f == null) return "";
        try
        {
            string t = File.ReadAllText(f, Encoding.UTF8).Trim();
            if (t.Length == 0) return "";
            path = f;
            if (t.Length > MaxChars) t = t.Substring(0, MaxChars) + "\n…（规矩文件太长，已截断）";
            return t;
        }
        catch { return ""; }
    }

    public static string VerifyFromRules(string rulesText)
    {
        if (string.IsNullOrEmpty(rulesText)) return null;
        string[] lines = rulesText.Replace("\r\n", "\n").Split('\n');
        foreach (string raw in lines)
        {
            string line = raw.Trim().TrimStart('-', '*', '>', '#', ' ').Trim();
            if (line.StartsWith("验证命令", StringComparison.Ordinal))
            {
                int i = line.IndexOfAny(new char[] { ':', '：' });
                if (i >= 0)
                {
                    string c = line.Substring(i + 1).Trim();
                    if (c.Length > 0) return c;
                }
            }
            else if (line.StartsWith("verify:", StringComparison.OrdinalIgnoreCase))
            {
                string c = line.Substring(7).Trim();
                if (c.Length > 0) return c;
            }
        }
        return null;
    }

    // 自动识别：只认这几种常见写法，都是安全命令
    public static string Detect(string cwd)
    {
        try
        {
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd)) return null;
            if (File.Exists(Path.Combine(cwd, "run_tests.py"))) return "python -B run_tests.py";
            string tests = Path.Combine(cwd, "tests");
            if (Directory.Exists(tests) && Directory.GetFiles(tests, "test_*.py").Length > 0)
                return "python -B -m unittest discover -s tests -q";
            if (Directory.GetFiles(cwd, "test_*.py").Length > 0 || Directory.GetFiles(cwd, "*_test.py").Length > 0)
                return "python -B -m unittest discover -q";
            string pkg = Path.Combine(cwd, "package.json");
            if (File.Exists(pkg) && File.ReadAllText(pkg, Encoding.UTF8).IndexOf("\"test\"", StringComparison.Ordinal) >= 0)
                return "npm test --silent";
            if (File.Exists(Path.Combine(cwd, "build.cmd"))) return "build.cmd";
            if (File.Exists(Path.Combine(cwd, "Makefile"))) return "make";
        }
        catch { }
        return null;
    }

    public static string Resolve(string cwd, string rulesText, out string source)
    {
        string fromRules = VerifyFromRules(rulesText);
        if (!string.IsNullOrEmpty(fromRules)) { source = "规矩文件里写的"; return fromRules; }
        string d = Detect(cwd);
        source = d == null ? null : "自动识别";
        return d;
    }
}

class AgentRunner
{
    public ILlm Llm;
    public readonly AgentConfig Config;
    public readonly IAgentUI UI;
    public readonly List<ToolDef> ToolList = new List<ToolDef>();
    public readonly Dictionary<string, ToolDef> ToolMap = new Dictionary<string, ToolDef>(StringComparer.Ordinal);
    public readonly List<Dictionary<string, object>> Messages = new List<Dictionary<string, object>>();
    public readonly HashSet<string> AlwaysAllow = new HashSet<string>(StringComparer.Ordinal);
    public ToolContext Ctx;
    public volatile bool Cancelled;
    public bool CanDisableThink;
    public List<string> Capabilities = new List<string>();
    public string ThinkingValues = "";

    public AgentRunner(AgentConfig config, IAgentUI ui)
    {
        Config = config;
        UI = ui;
        ResetContext();
        ReloadTools();                     // 里面会刷新系统提示（含项目规矩）
        RebuildLlm();
        Messages.Add(Msg("system", SystemPrompt()));
    }

    // 本会话是否已经放行「工作目录外」（用户在越权弹窗里选过"本会话总是允许"）
    bool _outsideGranted;
    string _currentTool = "";              // 正在执行的工具名：越权弹窗要显示是谁在申请

    // 重建工具上下文：权限档、越权放行、申请通道都在这一处装好。
    // 以前三处各自 new ToolContext(...)，加新东西容易漏（越权申请就漏过一遍）。
    public void ResetContext()
    {
        Ctx = new ToolContext(Config.cwd, Config.allow_outside || _outsideGranted,
            Config.shell_timeout, Perm.IsReadOnly(Config.permission));
        Ctx.AskOutside = AskOutsidePath;
    }

    // 工具想访问工作目录外的路径：走和写操作同一条审批通道（0 拒绝 / 1 允许这次 / 2 本会话总是允许）
    int AskOutsidePath(string full)
    {
        string tool = _currentTool.Length > 0 ? _currentTool : "工具";
        int d;
        try
        {
            d = UI.ApproveOutside(tool + "（工作目录外）", new string[]
            {
                full,
                "工作目录：" + Ctx.Root,
                "这是工作目录之外的路径，批准后才会访问"
            });
        }
        catch (Exception ex) { Log.Swallow(ex, "越权申请"); return 0; }
        try
        {
            Log.Audit("越权申请 · " + (d == 0 ? "拒绝" : (d == 2 ? "本会话总是允许" : "允许这次"))
                + " · " + tool + " · " + full);
        }
        catch { }
        if (d == 2) _outsideGranted = true;
        return d;
    }

    // 重新装载工具表：内置工具 + 启用的插件工具（插件窗口里改完开关就调它）
    public void ReloadTools()
    {
        ToolList.Clear();
        ToolMap.Clear();
        ToolList.AddRange(Tools.All());
        HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (ToolDef t in ToolList) taken.Add(t.Name);
        try { ToolList.AddRange(Plugins.ToolDefs(Config, taken)); }
        catch { }
        foreach (ToolDef t in ToolList) ToolMap[t.Name] = t;
        RefreshSystemPrompt();      // 插件里的"技能"文字也要跟着刷新
    }

    // ---------- 失败自动升级（本地模型顶不住时自动找云端模型接手）----------
    bool _escalated;
    int _failStreak;
    string _escProvider;
    string _escModel;

    void EscalateIfPossible()
    {
        try
        {
            if (Config.providers == null) return;
            Provider cloud = null;
            foreach (Provider p in Config.providers)
            {
                if (p != null && p.type == "openai" && !string.IsNullOrEmpty(p.api_key)) { cloud = p; break; }
            }
            if (cloud == null) return;
            if (cloud.id == Config.provider) return;              // 已经就是云端，没必要升
            if (cloud.models == null || cloud.models.Count == 0) return;
            _escProvider = Config.provider;
            _escModel = Config.model;
            Config.provider = cloud.id;
            Config.model = cloud.models[0];
            RebuildLlm();
            _escalated = true;
            UI.Write(OutKind.Error, "  ⚡ 当前模型连续失败 " + _failStreak + " 次，已自动升级到 "
                + cloud.name + " / " + cloud.models[0] + " 继续本轮（本轮结束自动切回）\n");
        }
        catch { }
    }

    void RestoreAfterEscalation()
    {
        if (!_escalated) return;
        try
        {
            Config.provider = _escProvider;
            Config.model = _escModel;
            RebuildLlm();
        }
        catch { }
        _escalated = false;
        _failStreak = 0;
    }

    // ---------- 长会话上下文压缩：把早期多轮对话摘要成一段，避免越聊越糊涂 ----------
    int _compactedUpTo;
    const int CompactTrigger = 24;   // 消息超过这个数就压缩
    const int KeepRecent = 8;        // 保留最近这么多条原文
    const string SummaryMark = "[早期对话摘要";   // 压缩摘要的标记（RefreshSystemPrompt 靠它认人，别删）

    void CompactHistoryIfNeeded()
    {
        try
        {
            int total = Messages.Count;
            if (total <= CompactTrigger) return;
            // 切点必须落在 user 消息上：按条数硬切会把 assistant(tool_calls) 丢掉、只留 tool 结果，
            // 严格校验的接口（DeepSeek）会直接 400：
            // "Messages with role 'tool' must be a response to a preceding message with 'tool_calls'"
            int keepFrom = AlignKeepFrom(Messages, total - KeepRecent);
            if (keepFrom <= 1 || keepFrom >= total) return;
            if (_compactedUpTo >= keepFrom) return;
            StringBuilder src = new StringBuilder();
            for (int i = 1; i < keepFrom; i++)
            {
                Dictionary<string, object> m = Messages[i];
                string role = Json.Str(m, "role", "");
                if (role == "system") continue;
                string c = Json.Str(m, "content", "");
                if (c.Length > 800) c = c.Substring(0, 800) + "…";
                if (c.Length == 0) continue;
                src.Append(role == "user" ? "用户：" : (role == "tool" ? "工具结果：" : "助手："));
                src.Append(c).Append("\n");
            }
            if (src.Length < 200) { _compactedUpTo = keepFrom; return; }
            List<Dictionary<string, object>> msgs = new List<Dictionary<string, object>>();
            msgs.Add(Msg("system", "你是对话压缩器。把下面的历史压缩成简洁中文摘要，必须保留："
                + "用户的原始目标、已确认的事实与数字、看过或改过的文件路径、未完成事项。"
                + "删掉寒暄和重复内容，不要编造，控制在 400 字以内。只输出摘要正文。"));
            msgs.Add(Msg("user", src.ToString()));
            StringBuilder sb = new StringBuilder();
            UI.Write(OutKind.Stats, "  ⟳ 对话较长（" + total + " 条），正在压缩早期上下文…\n");
            Llm.Stream(msgs, null, false, "auto", Config.num_ctx, null,
                delegate(string ct) { sb.Append(ct); }, null);
            string summary = sb.ToString().Trim();
            if (summary.Length < 20) return;
            List<Dictionary<string, object>> rebuilt = new List<Dictionary<string, object>>();
            rebuilt.Add(Messages[0]);
            rebuilt.Add(Msg("system", SummaryMark + " · 自动生成]\n" + summary
                + "\n（以上是更早的对话；需要细节时用工具重新查看文件，不要凭记忆编造。）"));
            for (int i = keepFrom; i < total; i++) rebuilt.Add(Messages[i]);
            Messages.Clear();
            Messages.AddRange(rebuilt);
            _compactedUpTo = Messages.Count;
            UI.Write(OutKind.Stats, "  ⟳ 已压缩为摘要，保留最近 " + KeepRecent + " 条原文\n");
        }
        catch (Exception ex) { Log.Swallow(ex, "压缩历史"); }
    }

    // 压缩切点对齐：从 keepFrom 往后找第一条 user 消息。找不到（后半段全是工具链）就返回 Count，
    // 调用方据此放弃这次压缩 —— 宁可这次不压，也不能切出「孤立 tool 消息」把会话弄坏。
    internal static int AlignKeepFrom(List<Dictionary<string, object>> msgs, int keepFrom)
    {
        int i = keepFrom;
        while (i < msgs.Count && Json.Str(msgs[i], "role", "") != "user") i++;
        return i;
    }

    // 发请求前的体检：丢掉没有配对 tool_calls 的 tool 消息。压缩切歪、旧会话、手工改过的会话都能自愈。
    internal static int DropOrphanTools(List<Dictionary<string, object>> msgs)
    {
        int removed = 0;
        int i = 1;
        while (i < msgs.Count)
        {
            if (Json.Str(msgs[i], "role", "") != "tool") { i++; continue; }
            string id = Json.Str(msgs[i], "tool_call_id", "");
            bool paired = false;
            // 往前找最近一条非 tool 消息：配对的前提是它是带 tool_calls 的 assistant
            for (int j = i - 1; j >= 0; j--)
            {
                string r = Json.Str(msgs[j], "role", "");
                if (r == "tool") continue;
                if (r == "assistant" && HasToolCall(msgs[j], id)) paired = true;
                break;
            }
            if (paired) { i++; continue; }
            msgs.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    static bool HasToolCall(Dictionary<string, object> msg, string id)
    {
        object[] calls = Json.Arr(Json.Get(msg, "tool_calls"));
        if (calls == null || calls.Length == 0) return false;
        if (id.Length == 0) return true;   // 老数据没带 tool_call_id：只要求"有这么个调用"
        for (int i = 0; i < calls.Length; i++)
        {
            Dictionary<string, object> tc = Json.Map(calls[i]);
            if (tc != null && Json.Str(tc, "id", "") == id) return true;
        }
        return false;
    }

    public void RebuildLlm()
    {
        if (Llm != null) { try { Llm.Close(); } catch { } }
        Llm = LlmFactory.Create(Config.Current(), Config.model);
    }

    public void Cancel()
    {
        Cancelled = true;
        if (Llm != null) Llm.Cancel();
    }

    public static Dictionary<string, object> Msg(string role, string content)
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["role"] = role;
        m["content"] = content;
        if (role != "system") m["ts"] = NowUnix();     // 存个时间戳，对话区每条下面要显示"16:59"
        return m;
    }

    public static long NowUnix()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    string SystemPrompt()
    {
        string s = "你是「Ahz Scribe」，一个跑在用户 Windows 电脑上的编程智能体。\n"
            + "你通过调用工具来完成任务：查看目录、读文件、搜索代码、改文件、执行命令。\n\n"
            + "工作准则：\n"
            + "1. 先看再动手：需要了解文件内容或项目结构时，必须调用工具，不要凭空猜测或编造。\n"
            + "2. 修改文件前，先用 read_file 读它，确认要改的原文确实存在、且与 old_string 完全一致。\n"
            + "3. 小改动用 edit_file，只有新建文件或整体重写才用 write_file。\n"
            + "4. 工具报错时先读懂报错再决定下一步，不要重复同样的失败调用。\n"
            + "5. 汇报用中文，简洁说明「做了什么 + 结果如何」，不要复述工具原始输出。\n"
            + "6. 涉及数字、文件名、行号时必须以工具返回的为准，不要自己重新估算或改写。\n"
            + "7. 任务完成后直接给结论，不要再调用工具。\n"
            + "8. 多步任务先列出 2-4 步计划再动手（一行一步，写成 `- [ ] 待办` / `- [x] 已完成`），"
            + "每步结束就把对应的那行改成 `- [x]`；系统每步会把你还剩的步骤发给你，照着做，不要重列一遍。\n"
            + "9. 路径参数约定：相对路径基于工作目录；要看当前目录就传 \".\"，不要传工作目录的文件夹名。\n\n"
            + "回答风格：\n"
            + "· 先给结论/结果，再给必要的理由和细节，别铺垫、别寒暄。\n"
            + "· 用短句和列表；多项数据对比用 Markdown 表格；内容多时用 ## 小标题分段。\n"
            + "· 只写事实：数字、文件名、行号以工具返回为准；不确定就直说「不确定」，绝不编。\n"
            + "· 不要写「我感觉 / 我学到了 / 像被上了一课」这类自我感想和角色扮演，用户要的是结果。\n"
            + "· 不要把工具调用写成正文（例如 run_shell(...) 这样的纯文本）；要执行就真的调用工具。\n"
            + "· 活没干完别说「已完成」；做不到就说清卡在哪一步、还差什么。\n\n"
            + "环境：Windows，命令是 PowerShell 语法；今天 " + DateTime.Now.ToString("yyyy-MM-dd")
            + "；工作目录 " + Ctx.Root + "。\n"
             + "你可以管理这台电脑的程序：run_shell 执行命令、list_processes 列进程、start_program 启动程序、kill_process 结束进程。\n"
             + "长任务（编译、跑测试、记分卡、起服务）用 run_background 丢到后台、拿任务号，再用 job_status 看进度；"
             + "别用 run_shell 在前台干等（默认 120 秒会被掐断）。\n"
             + "遇到你自己做不到的事（缺工具、缺能力、代码有缺陷、性能不够）：不要直接报错收工 —— 先用 propose_upgrade 提一份改造申请"
             + "（标题 / 改造内容 / 影响范围 / 风险 / 预期效果 / 怎么验证），由用户批准或驳回；"
             + "批准之后按固定顺序做完：改代码 → run_background 跑 build.cmd → run_background 跑 devtools\\bench\\run-ui-tests.cmd → restart_upgrade 重启；"
             + "任何一步失败就用 rollback_self 回滚到改动前，不许跳过验证。\n"
             + "写文件、执行命令、启动/结束程序这些敏感操作，执行前会先请你（用户）授权，批准后才执行 —— 和 codex/DSH 一样。\n"
             + "文件路径默认限定在工作目录内；需要访问工作目录外的文件时，直接调用工具申请 —— "
             + "系统会弹授权窗口问用户，批准了才放行；没人批准（用户拒绝、或无人值守）就仍然读不到，"
             + "不要当成已经能访问。用户可以在设置里勾「允许访问工作目录外」，勾上之后这类访问直接放行、不再问。";
        // 插件提供的"技能"：启用的插件声明的要求，追加到系统提示里
        string extra = Plugins.PromptText(Config);
        if (extra.Length > 0)
            s += "\n\n插件提供的技能（用户装的插件里声明的要求，按需遵守）：\n" + extra;

        // 计划模式：这一轮只准调研、只准出计划（框架层同时会拦下动手类工具，见 Execute）
        if (Config.plan_mode)
            s += "\n\n【计划模式 · 已生效】\n"
              + "这一轮你只能调研：读文件、看目录、搜代码、查进程都可以；"
              + "write_file / edit_file / run_shell / run_background / start_program / kill_process / job_kill 一律会被系统拦下。\n"
              + "功夫花在看清现状上，最后交一份计划：分几步、每步动哪个文件或跑什么命令、怎么验证、有什么风险。"
              + "不要动手，也不要说自己已经改过了。\n";

        // 工作目录里的项目规矩文件
        string rulesPath;
        string rules = ProjectRules.Read(Ctx.Root, out rulesPath);
        if (rulesPath != null)
            s += "\n\n项目规矩（取自 " + Path.GetFileName(rulesPath) + "，优先遵守）：\n" + rules;

        if (Config.auto_verify)
            s += "\n\n验证：你改完文件、准备收尾时，我会自动帮你在工作目录里跑一次项目验证命令，并把结果给你。"
               + "如果验证失败，请根据失败信息继续修，不用再问用户。";
        return s;
    }

    // ---------- 本轮任务清单（让 agent "看得见"自己计划了什么）----------
    // 背景：系统提示一直要求"多步任务先列出 2-4 步计划"，但代码里没有任何地方存这个计划，
    // 模型每步都得从对话历史里重读一遍 —— 本地小模型的主要死法「只读不写 / 读完就收工」
    // 就是这么来的：它忘了自己还剩几步没做。
    // 这里把计划抽出来记在会话里，每步以"剩余步骤"的形式注入上下文，收尾时做对齐检查。
    public class TaskItem
    {
        public string Text;
        public bool Done;
    }

    readonly List<TaskItem> _tasks = new List<TaskItem>();
    const int TaskMax = 12;          // 计划最多记这么多步（防止模型列 50 条刷屏）

    // 从模型输出里解析 "- [ ] xxx" / "- [x] xxx"；返回这次是否有变化
    // 只认行首列表项，避免把正文里的 "[ ]" 当计划
    public static bool ParseTasks(string text, List<TaskItem> into)
    {
        if (text == null || into == null) return false;
        List<TaskItem> parsed = new List<TaskItem>();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        bool inFence = false;                 // ``` 代码块里的示例不算计划
        foreach (string raw in lines)
        {
            string l = raw.Trim();
            if (l.StartsWith("```") || l.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            if (l.Length < 6) continue;
            // 允许 "- [ ] " / "* [x] " / "1. [ ] " 这几种写法
            int b = l.IndexOf('[');
            if (b < 0 || b + 2 >= l.Length) continue;
            if (l[b + 2] != ']') continue;
            char mark = char.ToLowerInvariant(l[b + 1]);
            if (mark != ' ' && mark != 'x') continue;
            // "[" 之前只能是列表符号/序号/空白
            string head = l.Substring(0, b).Trim().TrimEnd('.', ')', '、');
            bool headOk = head.Length == 0;
            if (!headOk)
            {
                if (head == "-" || head == "*" || head == "+") headOk = true;
                else
                {
                    int n;
                    headOk = int.TryParse(head, out n);
                }
            }
            if (!headOk) continue;
            string body = l.Substring(b + 3).Trim();
            if (body.Length == 0) continue;
            if (body.Length > 120) body = body.Substring(0, 120) + "…";
            TaskItem it = new TaskItem();
            it.Text = body;
            it.Done = mark == 'x';
            parsed.Add(it);
            if (parsed.Count >= TaskMax) break;
        }
        if (parsed.Count == 0) return false;          // 这段输出没有计划，别把已有清单清掉
        // 和现有清单一致就不动（避免每步都判"变了"）
        if (parsed.Count == into.Count)
        {
            bool same = true;
            for (int i = 0; i < parsed.Count; i++)
                if (parsed[i].Done != into[i].Done || parsed[i].Text != into[i].Text) { same = false; break; }
            if (same) return false;
        }
        into.Clear();
        into.AddRange(parsed);
        return true;
    }

    public static int TaskDoneCount(List<TaskItem> list)
    {
        int n = 0;
        if (list == null) return 0;
        foreach (TaskItem t in list) if (t.Done) n++;
        return n;
    }

    // 每步注入的"剩余步骤"。全是已完成 / 没有清单时返回 null（不浪费上下文）。
    public static string TaskNudge(List<TaskItem> list)
    {
        if (list == null || list.Count == 0) return null;
        StringBuilder sb = new StringBuilder();
        int undone = 0;
        foreach (TaskItem t in list)
        {
            if (t.Done) continue;
            undone++;
            sb.Append("· ").Append(t.Text).Append('\n');
        }
        if (undone == 0) return null;      // 全做完了就别再唠叨
        return "（系统提醒）你这一轮的计划还剩 " + undone + " 步没标记完成：\n" + sb.ToString()
            + "做完一步就把那一行改成 - [x]。都做完了直接给结论；如果某一步确定做不了，"
            + "把那一行删掉并说明原因，别留着它当摆设。";
    }

    // 收尾时的对齐检查：说了几步、实际做完几步
    public static string TaskSummary(List<TaskItem> list)
    {
        if (list == null || list.Count == 0) return null;
        int done = TaskDoneCount(list);
        if (done == list.Count) return null;         // 全做完就不用提
        StringBuilder sb = new StringBuilder();
        sb.Append("计划了 ").Append(list.Count).Append(" 步，完成 ").Append(done).Append(" 步。没做完的：");
        int n = 0;
        foreach (TaskItem t in list)
        {
            if (t.Done) continue;
            if (n++ > 0) sb.Append('、');
            sb.Append(t.Text);
            if (n >= 4 && done + n < list.Count) { sb.Append(" 等"); break; }
        }
        return sb.ToString();
    }

    // 把系统提示刷新成最新（插件装/卸、启停之后调用），顺便把历史里多出来的 system 消息并掉
    public void RefreshSystemPrompt()
    {
        try
        {
            int i = 0;
            // 只删「重复的基础提示词」：早期对话摘要也是 system 身份，但它是压缩后唯一的上下文，删了就等于白压
            while (i < Messages.Count && Json.Str(Messages[i], "role", "") == "system"
                   && !Json.Str(Messages[i], "content", "").StartsWith(SummaryMark)) i++;
            if (i > 1) Messages.RemoveRange(1, i - 1);
            Dictionary<string, object> sys = Msg("system", SystemPrompt());
            if (Messages.Count > 0 && Json.Str(Messages[0], "role", "") == "system") Messages[0] = sys;
            else Messages.Insert(0, sys);
        }
        catch { }
    }

    public void ProbeModel()
    {
        Capabilities.Clear();
        CanDisableThink = false;
        ThinkingValues = "";
        Provider p = Config.Current();
        if (p.type != "ollama")
        {
            // OpenAI 兼容接口没有统一的能力探测接口：按“支持工具调用”处理，
            // 并且不发送任何思考相关参数
            Capabilities.Add("tools");
            CanDisableThink = true;
            return;
        }
        Dictionary<string, object> info = Llm.Probe();
        if (info == null) return;
        object[] caps = Json.Arr(Json.Get(info, "capabilities"));
        if (caps != null) foreach (object c in caps) Capabilities.Add(Convert.ToString(c));
        Dictionary<string, object> thinking = Json.Map(Json.Get(info, "thinking"));
        if (thinking != null)
        {
            object[] vals = Json.Arr(Json.Get(thinking, "values"));
            if (vals != null)
            {
                List<string> parts = new List<string>();
                foreach (object v in vals)
                {
                    parts.Add(Convert.ToString(v).ToLowerInvariant());
                    if (v is bool && !(bool)v) CanDisableThink = true;
                }
                ThinkingValues = string.Join("/", parts.ToArray());
            }
        }
    }

    public string Warning()
    {
        if (!Capabilities.Contains("tools"))
            return "模型 " + Config.model + " 不支持工具调用（能力：" + string.Join(", ", Capabilities.ToArray())
                + "），请换一个会调用工具的模型，例如 qwen3.5 系列。";
        if (Config.think && ThinkingValues == "true")
            return "模型 " + Config.model + " 关不掉思考（thinking.values=[true]），思考内容会混进回答里。";
        return null;
    }

    public void SetWorkingDir(string dir)
    {
        Config.cwd = Path.GetFullPath(dir);
        Config.Save();
        ResetContext();
        Messages[0] = Msg("system", SystemPrompt());
    }

    // 聊天命令（不进模型）：/计划 [开|关]
    bool HandlePlanCommand(string text)
    {
        string t = (text == null ? "" : text.Trim());
        if (!t.StartsWith("/计划")) return false;
        string body = t.Substring(3).Trim().ToLowerInvariant();
        if (body.Length == 0)
        {
            UI.Write(OutKind.Info, "\n  计划模式：" + (Config.plan_mode ? "开（只调研、只出计划，不改文件）" : "关（可以直接动手）") + "\n");
            UI.Write(OutKind.Info, "  切换：/计划 开   或   /计划 关\n\n");
            return true;
        }
        bool on = (body == "开" || body == "on" || body == "1" || body == "true");
        bool off = (body == "关" || body == "off" || body == "0" || body == "false");
        if (!on && !off)
        {
            UI.Write(OutKind.Info, "  用法：/计划 开 或 /计划 关（不带参数看当前状态）\n");
            return true;
        }
        Config.plan_mode = on;
        Config.Save();
        RefreshSystemPrompt();      // 立刻生效，不用等下一轮重建
        UI.Write(OutKind.Info, "  计划模式已" + (on ? "打开：这一轮它只调研、只出计划" : "关掉：可以动手改文件了") + "\n");
        return true;
    }

    // 聊天命令（不进模型）：/回滚 —— 把文件恢复到改动前（最近一条快照），并把迭代记成已回滚
    bool HandleRollbackCommand(string text)
    {
        string t = (text == null ? "" : text.Trim());
        if (!t.StartsWith("/回滚")) return false;
        string msg = Upgrader.RollbackTool(Ctx, new Dictionary<string, object>());
        UI.Write(OutKind.Info, "  " + msg + "\n");
        return true;
    }

    // 聊天命令（不进模型）：/升级 —— 把 .new 换上去并重启（等价于菜单里那项）
    bool HandleUpgradeCommand(string text)
    {
        string t = (text == null ? "" : text.Trim());
        if (!t.StartsWith("/升级")) return false;
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        List<string> pending = Upgrader.PendingNewFiles(exeDir);
        if (pending.Count == 0)
        {
            UI.Write(OutKind.Info, "  没有待升级的 .new。先跑 cmd /c build.cmd（窗口版在跑时会自动出 .new）。\n");
            return true;
        }
        string msg;
#if CLI_BUILD
        bool ok = Upgrader.Schedule(exeDir, false, out msg);      // CLI：替换完不自动拉窗口，免得莫名弹窗
#else
        bool ok = Upgrader.Schedule(exeDir, true, out msg);
#endif
        UI.Write(ok ? OutKind.Info : OutKind.Error, "  " + msg + "\n");
        if (ok)
        {
            UI.Write(OutKind.Info, "  程序马上退出完成替换" + (Upgrader.RequestClose != null ? "，替换完自动启动。\n" : "。\n"));
            try { Console.Out.Flush(); } catch { }
            if (Upgrader.RequestClose != null) Upgrader.RequestClose();
            else Environment.Exit(0);
        }
        return true;
    }

    // 聊天命令（不进模型）：/导出 —— 把这一轮交付落成 md（干了什么/改了哪些文件/跑了哪些验证/结论）
    bool HandleExportCommand(string text)
    {
        string t = (text == null ? "" : text.Trim());
        if (!t.StartsWith("/导出")) return false;
        try
        {
            string path = Deliver.Export(this, Ctx.Root);
            UI.Write(OutKind.Info, "  已导出交付记录：" + path + "\n");
        }
        catch (Exception ex)
        {
            Log.Swallow(ex, "导出交付记录");
            UI.Write(OutKind.Error, "  导出失败：" + ex.Message + "\n");
        }
        return true;
    }

    // 聊天命令（不进模型）：/记住 关键词1,关键词2 = 处置说明    /记住（列出现有条目）
    bool HandleLessonCommand(string text)
    {
        string t = (text == null ? "" : text.Trim());
        if (!t.StartsWith("/记住")) return false;
        string body = t.Substring(3).Trim();
        if (body.Length == 0)
        {
            List<Lesson> list = Lessons.Load(Ctx.Root);
            UI.Write(OutKind.Info, "\n  踩坑本（" + list.Count + " 条）：" + Lessons.PathOf(Ctx.Root) + "\n");
            foreach (Lesson l in list)
                UI.Write(OutKind.Info, "  · " + l.title + "   [关键词 " + l.triggers + "]\n");
            UI.Write(OutKind.Info, "  教它一条：/记住 关键词1,关键词2 = 处置说明\n\n");
            return true;
        }
        int eq = body.IndexOf('=');
        if (eq <= 0)
        {
            UI.Write(OutKind.Info, "  格式：/记住 关键词1,关键词2 = 处置说明（关键词用逗号分隔，命中就会出现提醒）\n");
            return true;
        }
        string keys = body.Substring(0, eq).Trim();
        string fix = body.Substring(eq + 1).Trim();
        if (keys.Length == 0 || fix.Length == 0)
        {
            UI.Write(OutKind.Info, "  关键词和处置说明都要写，例如：/记住 微信,自动回复 = 我做不到，别硬来\n");
            return true;
        }
        Lessons.Add(Ctx.Root, keys, "用户教过：" + Txt.Clip(fix, 80), fix);
        UI.Write(OutKind.Info, "  ✓ 已记住：关键词 [" + keys + "] → " + fix + "\n");
        return true;
    }

    // ---------- 一轮对话 ----------

    public void RunTurn(string userText)
    {
        if (userText == null || userText.Trim().Length == 0) return;
        if (HandlePlanCommand(userText)) return;        // /计划 开|关：本地切换，不发给模型
        if (HandleExportCommand(userText)) return;      // /导出：把交付落成 md，也不发给模型
        if (HandleUpgradeCommand(userText)) return;     // /升级：换 .new 并重启
        if (HandleRollbackCommand(userText)) return;    // /回滚：恢复改动前的文件（快照）
        if (HandleLessonCommand(userText)) return;      // /记住：教一条就够，不发给模型
        UI.Write(OutKind.User, "› " + userText + "\n");
        Messages.Add(Msg("user", userText));
        // 踩坑本命中就捎一条提醒（只提醒、不拦；没命中什么都不加）
        List<Lesson> lessonHits = Lessons.Match(Ctx.Root, userText, 3, true);
        if (lessonHits.Count > 0)
        {
            Messages.Add(Msg("user", Lessons.NoteText(lessonHits)));
            UI.Write(OutKind.Stats, "  ⟳ 踩坑本命中 " + lessonHits.Count + " 条，已并入上下文\n");
        }
        Changes.Root = Ctx.Root;      // 卡片上显示相对路径
        Changes.Clear();              // 这一轮改了哪些文件，从零开始记

        int steps = 0, toolCount = 0;
        long genTokens = 0;
        double genSeconds = 0;
        bool autoYes = Config.auto_yes;
        bool finished = false;      // 模型自己收尾（不再要工具）
        int stall = 0;              // 连续几轮"所有工具调用都失败"
        int stallStopped = 0;       // 判定卡住时，卡了几轮
        int announceStreak = 0;     // 连续几步在"叫嚷要调用"却没真调用
        bool noisyStopped = false;  // 因为"只说不做"收尾
        List<string> doneActs = new List<string>();   // 做成的操作（给兜底汇报用）
        List<string> failActs = new List<string>();   // 最近的失败（同上）
        List<string> ranCmds = new List<string>();    // 这一轮跑过的命令（判断要不要自动验证）
        int editedFiles = 0;                          // 改过几个文件
        int verifyRounds = 0;                         // 自动验证跑了几次
        int idleReads = 0;                            // 连续几步只读不写（催它动手用）
        int nudges = 0;                               // "别再读了"催过几次（封顶 2 次）

        // 项目规矩 + 自动验证命令（每轮对话重新读一次规矩文件，改完立刻生效）
        string rulesPath;
        string rulesText = ProjectRules.Read(Ctx.Root, out rulesPath);
        if (rulesPath != null)
        {
            UI.Write(OutKind.Info, "  · 已加载项目规矩 " + Path.GetFileName(rulesPath) + "\n");
            RefreshSystemPrompt();
        }
        string vSource = null;
        string vcmd = null;
        if (Config.auto_verify)
            vcmd = ProjectRules.Resolve(Ctx.Root, rulesText, out vSource);
        if (vcmd != null) UI.Write(OutKind.Info, "  · 自动验证命令：" + vcmd + "（" + vSource + "）\n");

        for (int stepNo = 1; stepNo <= Config.max_steps; stepNo++)
        {
            if (Cancelled) break;
            steps++;
            // 每步把"还剩几步"注入上下文：模型不用自己记计划，忘了就会被提醒（见 _tasks 那段注释）
            string tn = TaskNudge(_tasks);
            if (tn != null) Messages.Add(Msg("user", tn));
            UI.Status("等待模型…");
            StepResult res = Step();
            genTokens += res.Tokens;
            genSeconds += res.Seconds;
            // 模型自己写的 "- [ ] / - [x]" 就是它的计划，抽出来记着（每步注入剩余步骤用）
            if (ParseTasks(res.Content, _tasks))
                UI.Write(OutKind.Info, "  · 任务清单更新：" + TaskDoneCount(_tasks) + "/" + _tasks.Count + " 完成\n");
            Dictionary<string, object> assistant = Msg("assistant", res.Content);
            if (res.Calls.Count > 0)
            {
                List<object> calls = new List<object>();
                foreach (Dictionary<string, object> c in res.Calls) calls.Add(c);
                assistant["tool_calls"] = calls;
            }
            Messages.Add(assistant);
            if (res.Calls.Count == 0)
            {
                // 收工拦截：任务大概率要求改代码（有验证命令），但这一轮一次文件都没改过——
                // 本地小模型经常读完就输出"计划"收工。拦一次，把计划逼成动作。
                if (vcmd != null && editedFiles == 0 && nudges == 0 && stepNo < Config.max_steps && !Cancelled)
                {
                    nudges++;
                    UI.Write(OutKind.Info, "  · 检测到收工时 0 次编辑，拦截一次逼它动手（" + nudges + "/2）\n");
                    Messages.Add(Msg("user", "（系统提醒）你刚才只输出了计划/分析，但没有改任何文件。"
                        + "这轮任务是要改代码的。现在直接调用 edit_file 或 write_file 动手改，"
                        + "改完运行验证命令确认，不要只描述打算怎么做。"));
                    continue;
                }
                // 模型想收尾：如果这一轮真改过文件、而且它自己没跑过验证，就替它跑一次
                bool canVerify = vcmd != null && editedFiles > 0 && verifyRounds < 2 && !RanCommand(ranCmds, vcmd);
                if (canVerify && !Cancelled)
                {
                    verifyRounds++;
                    editedFiles = 0;
                    string vout;
                    bool vok;
                    RunAutoVerify(vcmd, out vout, out vok);
                    if (vok)
                    {
                        finished = true;
                        break;
                    }
                    Messages.Add(Msg("user", "（这是系统自动跑的验证命令 `" + vcmd + "` 的输出，失败了）\n"
                        + FailureSummary(vout) + vout
                        + "\n\n请按上面的失败信息继续修：**只改真正失败的那几处**，不要再动别的代码；"
                        + "改完不用再问我，我会再验证一次。"));
                    continue;
                }
                finished = true;
                break;
            }

            bool anyOk = false;
            foreach (Dictionary<string, object> call in res.Calls)
            {
                if (Cancelled) break;
                string name = Json.Str(call, "name", "?");
                string callId = Json.Str(call, "id", "");
                Dictionary<string, object> args = Json.Map(Json.Get(call, "arguments"));
                if (args == null)
                {
                    string raw = Json.Str(call, "arguments", "");
                    if (raw.Trim().Length > 0) { try { args = Json.Obj(raw); } catch { } }
                }
                if (args == null) args = new Dictionary<string, object>();

                string shown = FormatArgs(name, args);
                UI.Write(OutKind.ToolCall, "  ● " + name + "(" + shown + ")\n");
                // 同参数 + 同工具已经失败过：不再执行，直接把纠正提示回灌给模型
                string sig = name + "|" + Json.Dump(args);
                string prevFail;
                if (_failedSig.TryGetValue(sig, out prevFail))
                {
                    string dupMsg = "你已经用完全相同的参数调用过 " + name + " 并且失败了（" + prevFail + "）。"
                        + "不要重复同样的调用：换参数（要看当前工作目录请把 path 传 \".\"）、换工具，"
                        + "或者直接告诉用户你卡在哪里。";
                    UI.Write(OutKind.ToolResult, "  └ ⚠ 重复调用已拦截（同工具同参数刚失败过）\n");
                    Lessons.CaptureDuplicate(Ctx.Root, name, args, prevFail);   // 记进踩坑本：重启后也还记得
                    _failStreak++; if (_failStreak >= 2 && !_escalated) EscalateIfPossible();
                    toolCount++;
                    Dictionary<string, object> dupMsgObj = Msg("tool", "[" + name + " 结果]\n" + dupMsg);
                    dupMsgObj["tool_call_id"] = callId;
                    dupMsgObj["tool_name"] = name;
                    Messages.Add(dupMsgObj);
                    continue;
                }

                string result;
                bool ok;
                Execute(name, args, autoYes, out result, out ok);
                if (ok) _failedSig.Remove(sig);
                else _failedSig[sig] = Txt.FirstLine(result);
                // 跨会话的坑：这个参数组合以前失败过，就把话捎在结果里
                string lessonWarn = Lessons.Warn(Ctx.Root, Lessons.ArgsText(args));
                if (lessonWarn.Length > 0) result += lessonWarn;
                // 失败自动升级：本地小模型连续失败，就临时换云端模型继续（本回合有效）                 if (ok) _failStreak = 0; else _failStreak++;                 if (!ok && _failStreak >= 2 && !_escalated) EscalateIfPossible();
                if (ok) anyOk = true;
                if (ok)
                {
                    if (doneActs.Count < 8) doneActs.Add(name + "(" + shown + ")");
                }
                else
                {
                    if (failActs.Count < 4) failActs.Add(name + "：" + Txt.FirstLine(result));
                }
                if (name == "run_shell") ranCmds.Add(A.Str(args, "command", ""));
                if (ok && (name == "write_file" || name == "edit_file")) editedFiles++;
                toolCount++;                int extra = Txt.LineCount(result) - 1;
                UI.Write(OutKind.ToolResult, "  └ " + (ok ? "✓ " : "✗ ") + Txt.FirstLine(result)
                    + (extra > 0 ? "  …+" + extra + " 行" : "") + "\n");
                Dictionary<string, object> toolMsg = Msg("tool", "[" + name + " 结果]\n" + result);
                toolMsg["tool_call_id"] = callId;
                toolMsg["tool_name"] = name;
                Messages.Add(toolMsg);
            }

            stall = NextStallCount(stall, anyOk);
            if (stall >= StallLimit) { stallStopped = stall; break; }

            // 只读不写催促：这一步成功的全是读类工具（list_dir/read_file/search_text）且没改过文件，
            // 连续 3 步就注入一次提醒。本地小模型最常见的死法就是"读完→输出计划→收工"。
            if (StepOnlyRead(res.Calls, editedFiles))
            {
                idleReads++;
                if (idleReads >= 3 && nudges < 2 && stepNo < Config.max_steps)
                {
                    nudges++;
                    idleReads = 0;
                    UI.Write(OutKind.Info, "  · 连续 " + 3 + " 步只读没写，注入提醒（第 " + nudges + "/2 次）\n");
                    Messages.Add(Msg("user", "（系统提醒）你已经连续读了几个文件但一个都没改过。"
                        + "现在直接动手：用 edit_file（或 write_file）把该改的地方改掉，"
                        + "改完再跑验证命令。不要再继续读文件或复述计划，除非你确实还缺关键信息。"));
                }
            }
            else idleReads = 0;

            // 只说不做：这一步的正文里满是"真实调用/［调用］"这类宣告 → 连续两步硬提醒，第三步直接收尾
            // （GLM 实测能把 28 步预算全烧在这种自言自语上）
            if (ToolNoise.Count(res.Content) >= 2)
            {
                announceStreak++;
                if (announceStreak >= 2 && nudges < 2 && steps < Config.max_steps)
                {
                    nudges++;
                    UI.Write(OutKind.Info, "  · 连续 " + announceStreak + " 步只在文字里说要调用、没有真调用，注入提醒（第 " + nudges + "/2 次）\n");
                    Messages.Add(Msg("user", "（系统提醒）你在正文里写了「要调用工具」的文字，但没有真的发起工具调用——写文字不算调用。"
                        + "现在二选一：① 立刻发起真实工具调用把活干完；② 判断已经无法推进就直接给结论、说明卡在哪。"
                        + "不要再输出「真实调用如下」这类句子。"));
                }
                if (announceStreak >= 3) { noisyStopped = true; break; }
            }
            else announceStreak = 0;
        }

        // 停下来的两种情况：步数用完 / 连续多轮一点进展都没有。
        // 不管哪种，都再给模型一轮"不给工具"的机会，让它把已经做了什么、还剩什么讲清楚，
        // 而不是让用户只看到一句"先停下来"。
        bool capped = !finished && !Cancelled && steps >= Config.max_steps;
        bool stalled = !finished && !Cancelled && stallStopped >= StallLimit;
        if (capped || stalled || noisyStopped)
        {
            UI.Write(OutKind.Error, capped
                ? "  ⚠ 本轮工具步数用完了（" + Config.max_steps + " 步），先停下来。\n"
                : (noisyStopped
                    ? "  ⚠ 连续 " + announceStreak + " 步只在文字里说「要调用」，没有真的动手，先停下来。\n"
                    : "  ⚠ 连续 " + stallStopped + " 轮的工具调用全失败了，判断为卡住了，先停下来。\n"));
            if (!Cancelled)
            {
                UI.Status("正在整理结论…");
                UI.Write(OutKind.Info, "  · 这一轮不再给工具，让它先把阶段性结论讲清楚…\n");
                StepResult fin = Step(false);
                genTokens += fin.Tokens;
                genSeconds += fin.Seconds;
                string txt = fin.Content == null ? "" : fin.Content.Trim();
                if (txt.Length < 2)
                {
                    // 模型没给出总结（本地小模型常见）：这里自己写一份，别让用户只看到"停下来了"
                    txt = "（模型这次没给出总结，下面是本地的记录）\n"
                        + "· 本轮共 " + steps + " 步、" + toolCount + " 次工具调用\n"
                        + (doneActs.Count > 0 ? "· 做成的操作：" + string.Join("；", doneActs.ToArray()) + "\n" : "· 没有成功的工具调用\n")
                        + (failActs.Count > 0 ? "· 最近的失败：" + string.Join("；", failActs.ToArray()) + "\n" : "")
                        + (capped ? "· 停下来是因为单轮工具步数用完了（" + Config.max_steps + " 步）"
                                  : (noisyStopped ? "· 停下来是因为它一直在文字里说「要调用工具」，实际没发起调用"
                                                  : "· 停下来是因为连续 " + stallStopped + " 轮工具调用都失败"));
                    UI.Write(OutKind.Info, "\n" + txt + "\n");
                }
                Messages.Add(Msg("assistant", txt));
            }
        }

        double tps = genSeconds > 0 ? genTokens / genSeconds : 0;
        // 收尾对齐检查：计划了几步、做完几步（做不完也要说清，别让清单当摆设）
        // 计划模式下例外：这时候的清单本来就不该被执行，报"没做完"是假警告。
        string taskSum = TaskSummary(_tasks);
        if (taskSum != null && !Cancelled && !Config.plan_mode) UI.Write(OutKind.Error, "\n  ⚠ " + taskSum + "\n");
        // 这一轮改过的文件 → 对话区来一张「已编辑 N 个文件」卡片（可以点开看 diff）
        if (Changes.Now.Count > 0)
        {
            int snap = Changes.Snapshot();
            // 顺手落盘一份（重启后还能恢复；快照本身失败不影响正常干活）
            SnapStore.Save(Ctx.Root, Changes.Get(snap),
                "已编辑 " + Changes.Now.Count + " 个文件");
            StringBuilder cb = new StringBuilder();
            cb.Append("已编辑 ").Append(Changes.Now.Count).Append(" 个文件\n");
            foreach (FileChange fc in Changes.Now) cb.Append(fc.Rel).Append('\n');
            cb.Append("#card#").Append(snap);
            UI.Write(OutKind.Changes, cb.ToString());
        }
        UI.Write(OutKind.Stats, "\n  ── " + steps + " 步 · " + toolCount + " 次工具 · "
            + genTokens + " tok · " + tps.ToString("0.0") + " tok/s\n\n");
        CompactHistoryIfNeeded();
        RestoreAfterEscalation();
        UI.Status("就绪");
    }

    // 重复调用护栏：同一个工具 + 完全相同的参数，失败过就不再重复执行（小模型很容易原地打转）
    readonly Dictionary<string, string> _failedSig = new Dictionary<string, string>();

    // 这一步是不是"只读不写"：没有成功执行任何写类工具（edit_file/write_file/run_shell 都算动手）
    // 用来识别"读完→输出计划→收工"的本地小模型死法，注入催促。
    public static bool StepOnlyRead(List<Dictionary<string, object>> calls, int editedSoFar)
    {
        if (calls == null || calls.Count == 0) return false;
        foreach (Dictionary<string, object> c in calls)
        {
            string n = c == null ? "" : Json.Str(c, "name", "");
            if (n == "edit_file" || n == "write_file" || n == "run_shell") return false;
        }
        return editedSoFar == 0;
    }

    // 卡住判定：连续 StallLimit 轮里"一个工具调用都没成功"就停下（先给模型几次换策略的机会）
    public const int StallLimit = 3;

    // 这一轮里模型自己跑过同样的验证命令了吗（跑过就不再自动跑）
    public static bool RanCommand(List<string> ran, string vcmd)
    {
        if (ran == null || string.IsNullOrEmpty(vcmd)) return false;
        string a = vcmd.Replace("\"", "").Replace("'", "").Replace("  ", " ").Trim().ToLowerInvariant();
        string key = a;
        int sp = a.IndexOf(' ');
        if (sp > 0) key = a.Substring(sp + 1).Trim();     // 去掉解释器名，比如 "python -B run_tests.py" -> "-b run_tests.py"
        foreach (string c0 in ran)
        {
            string c = (c0 == null ? "" : c0).Replace("\"", "").Replace("'", "").Replace("  ", " ").Trim().ToLowerInvariant();
            if (c.Length == 0) continue;
            if (c.IndexOf(a) >= 0) return true;
            if (key.Length > 3 && c.IndexOf(key) >= 0) return true;
        }
        return false;
    }

    // 自动验证：走同一个 run_shell（同样的危险命令拦截、同样的超时），结果也写进轨迹
    void RunAutoVerify(string cmd, out string output, out bool ok)
    {
        UI.Status("自动验证中…");
        UI.Write(OutKind.ToolCall, "  ● 自动验证(" + cmd + ")\n");
        Dictionary<string, object> args = new Dictionary<string, object>();
        args["command"] = cmd;
        string result;
        Execute("run_shell", args, true, out result, out ok);     // true = 不用问（命令来自规矩文件或白名单识别）
        output = Txt.Clip(result, 3000);
        int extra = Txt.LineCount(output) - 1;
        UI.Write(OutKind.ToolResult, "  └ " + (ok ? "✓ " : "✗ ") + Txt.FirstLine(output)
            + (extra > 0 ? "  …+" + extra + " 行" : "") + "\n");
    }

    // 把验证输出里的"失败要点"挑出来放最前面：小模型面对一大坨输出经常抓不住重点
    public static string FailureSummary(string output)
    {
        if (string.IsNullOrEmpty(output)) return "";
        List<string> hits = new List<string>();
        string[] lines = output.Replace("\r\n", "\n").Split('\n');
        foreach (string raw in lines)
        {
            string l = raw.Trim();
            if (l.Length == 0) continue;
            bool hit = l.StartsWith("FAIL:", StringComparison.Ordinal)
                || l.StartsWith("ERROR:", StringComparison.Ordinal)
                || l.StartsWith("AssertionError", StringComparison.Ordinal)
                || (l.IndexOf("个用例，失败", StringComparison.Ordinal) >= 0)
                || (l.IndexOf("失败", StringComparison.Ordinal) >= 0 && l.IndexOf("个", StringComparison.Ordinal) >= 0 && l.Length < 80);
            if (hit) hits.Add(l);
            if (hits.Count >= 15) break;
        }
        if (hits.Count == 0) return "";
        return "失败要点（共 " + hits.Count + " 条）：\n" + string.Join("\n", hits.ToArray()) + "\n原始输出（截断）：\n";
    }

    public static int NextStallCount(int current, bool anyToolSucceeded)
    {
        return anyToolSucceeded ? 0 : current + 1;
    }

    // 有些模型（DeepSeek 系列）偶尔把"原生工具调用语法"直接写进正文，形如 <|DSML|tool_calls>…。
    // 这类内容不该当成回答显示给用户。
    public static bool LooksLikeToolMarkup(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.IndexOf("DSML", StringComparison.Ordinal) >= 0) return true;
        if (s.IndexOf("<function_calls", StringComparison.Ordinal) >= 0) return true;
        if (s.IndexOf("invoke name=", StringComparison.Ordinal) >= 0) return true;
        if (s.IndexOf("<|", StringComparison.Ordinal) >= 0 && s.IndexOf("tool_calls", StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    void Execute(string name, Dictionary<string, object> args, bool autoYes, out string result, out bool ok)
    {
        ok = false;
        _currentTool = name;                 // 越权弹窗要显示是谁在申请
        ToolDef def;
        if (!ToolMap.TryGetValue(name, out def))
        {
            result = "不存在名为 " + name + " 的工具。可用工具：" + string.Join(", ", new List<string>(ToolMap.Keys).ToArray());
            return;
        }
        if (name == "run_shell" || name == "run_background")
        {
            string hit = Tools.ScreenCommand(A.Str(args, "command", ""));
            if (hit != null)
            {
                result = "这条命令被安全规则拦下了（" + hit + "）。请换一种安全的做法。";
                return;
            }
        }
        // 只读档：写类工具直接拒绝（在审批之前 —— 这是硬约束，不是"要不要问用户"）
        try { Tools.GuardReadOnly(Ctx, name); }
        catch (ToolException ex) { result = "× " + ex.Message; return; }

        // 计划模式：只准调研，不准动手 —— 先让它把"打算怎么做"讲清楚，用户批准后再放开
        if (Config.plan_mode && Tools.IsPlanBlocked(name))
        {
            result = "现在处于「计划模式」，只能调研，不能执行 " + name + "。\n"
                + "请继续读代码、搜文本、看目录，最后交一份计划（分几步、每步动哪个文件、怎么验证、有什么风险）。\n"
                + "用户批准后会关掉「计划模式」（输入框下方那个 chip，或发 /计划 关），那时再动手。";
            return;
        }

        if (def.NeedsApproval && !autoYes && !AlwaysAllow.Contains(name))
        {
            int decision = UI.Approve(name, Preview(name, args));
            try
            {
                Log.Audit("审批·" + (decision == 0 ? "拒绝" : (decision == 2 ? "本会话总是允许" : "允许"))
                    + " · 工具 " + name + " · " + string.Join(" / ", Preview(name, args)));
            }
            catch { }
            if (decision == 2) { AlwaysAllow.Add(name); decision = 1; }
            if (decision == 0)
            {
                result = "用户拒绝了这个操作。请换一个思路，或先向用户解释你打算做什么。";
                return;
            }
        }
        try
        {
            result = def.Run(Ctx, args);
            ok = true;
        }
        catch (ToolException ex) { result = "工具报错：" + ex.Message; }
        catch (Exception ex) { result = "工具执行异常：" + ex.GetType().Name + ": " + ex.Message; }
    }

    public static string FormatArgs(string name, Dictionary<string, object> args)
    {
        string s;
        if (name == "run_shell") s = "$ " + A.Str(args, "command", "");
        else if (name == "search_text") s = A.Str(args, "pattern", "") + "  →  " + A.Str(args, "path", ".");
        else if (name == "write_file") s = A.Str(args, "path", "") + "  (" + A.Str(args, "content", "").Length + " 字符)";
        else if (name == "edit_file") s = A.Str(args, "path", "");
        else if (name == "list_dir" || name == "read_file") s = A.Str(args, "path", ".");
        else s = Json.Dump(args);
        s = s.Replace("\r", " ").Replace("\n", " ⏎ ");
        if (s.Length > 130) s = s.Substring(0, 130) + "…";
        return s;
    }

    public static string[] Preview(string name, Dictionary<string, object> args)    {
        List<string> lines = new List<string>();
        if (name == "run_shell")
        {
            string cmd = A.Str(args, "command", "");
            if (cmd.Length > 500) cmd = cmd.Substring(0, 500) + "…";
            lines.Add("$ " + cmd);
        }
        else if (name == "write_file")
        {
            string content = A.Str(args, "content", "");
            string head = content.Length > 400 ? content.Substring(0, 400) + "\n…" : content;
            lines.Add("写入 " + A.Str(args, "path", "") + "（" + content.Length + " 字符）");
            lines.Add("");
            lines.Add(head);
        }
        else if (name == "edit_file")
        {
            string oldS = A.Str(args, "old_string", "");
            string newS = A.Str(args, "new_string", "");
            if (oldS.Length > 300) oldS = oldS.Substring(0, 300) + "…";
            if (newS.Length > 300) newS = newS.Substring(0, 300) + "…";
            lines.Add("修改 " + A.Str(args, "path", ""));
            lines.Add("");
            lines.Add("- " + oldS);
            lines.Add("+ " + newS);
        }
        else lines.Add(Json.Dump(args));
        return lines.ToArray();
    }

    StepResult Step()
    {
        StepResult res = new StepResult();
        return Step(true);
    }

    // allowTools=false：这一轮不给模型任何工具，用来逼它直接给结论
    StepResult Step(bool allowTools)
    {
        StepResult res = new StepResult();
        List<object> schemas = null;
        if (allowTools)
        {
            schemas = new List<object>();
            foreach (ToolDef t in ToolList) schemas.Add(t.Schema());
        }

        bool thinkingStarted = false;
        bool contentStarted = false;
        bool contentDecided = false;   // 前 24 个字符攒着看，确认不是"工具语法泄漏"再往外显示
        bool markup = false;

        Stopwatch clock = Stopwatch.StartNew();
        // 发请求前体检：坏会话（孤立 tool 消息）在这里自愈，否则每次请求都会被接口拒掉
        DropOrphanTools(Messages);
        Dictionary<string, object> last = Llm.Stream(Messages, schemas, Config.think, Config.effort, Config.num_ctx,
            delegate(string th)
            {
                if (!thinkingStarted)
                {
                    UI.Write(OutKind.Thinking, "\n  🧠 思考\n  ");
                    thinkingStarted = true;
                    UI.Status("思考中…");
                }
                UI.Write(OutKind.Thinking, th);
            },
            delegate(string ct)
            {
                res.Content = res.Content + ct;
                if (markup) return;
                if (!contentDecided && res.Content.Length < 24) return;
                if (!contentDecided)
                {
                    contentDecided = true;
                    if (LooksLikeToolMarkup(res.Content)) { markup = true; return; }
                    if (thinkingStarted) UI.Write(OutKind.Info, "\n");
                    UI.Write(OutKind.Assistant, "\n● ");
                    contentStarted = true;
                    UI.Status("生成中…");
                    UI.Write(OutKind.Assistant, res.Content);   // 把攒着的一段补上
                    return;
                }
                UI.Write(OutKind.Assistant, ct);
            },
            delegate(Dictionary<string, object> call)
            {
                res.Calls.Add(call);
            });

        res.Stats = last;
        clock.Stop();
        long evalCount = last == null ? -1 : Json.Long(last, "eval_count", -1);
        if (evalCount >= 0)
        {
            res.Tokens = evalCount;
            res.Seconds = last == null ? 0 : Json.Long(last, "eval_duration", 0) / 1e9;
        }
        else
        {
            // OpenAI 兼容接口：token 数在 usage.completion_tokens，耗时只能用墙钟时间
            res.Tokens = last == null ? 0 : Json.Long(last, "completion_tokens", 0);
            res.Seconds = clock.Elapsed.TotalSeconds;
        }
        if (!contentDecided && res.Content.Length > 0)
        {
            // 总长没到判断阈值：这时候再判一次
            if (LooksLikeToolMarkup(res.Content)) markup = true;
            else
            {
                if (thinkingStarted) UI.Write(OutKind.Info, "\n");
                UI.Write(OutKind.Assistant, "\n● ");
                contentStarted = true;
                UI.Write(OutKind.Assistant, res.Content);
            }
        }
        if (markup)
        {
            // 模型把工具调用写进了正文：不进对话记录，也不显示给用户
            res.Content = "";
            UI.Write(OutKind.Info, "\n  · （模型把工具调用写成了正文，已忽略）\n");
        }
        if (contentStarted) UI.Write(OutKind.Info, "\n");
        return res;
    }

    // ---------- 上下文管理 ----------

    int ApproxTokens()
    {
        int chars = 0;
        foreach (Dictionary<string, object> m in Messages) chars += (Json.Str(m, "content", "") ?? "").Length;
        return chars / 2 + 200;
    }

    public void TrimContext()
    {
        int budget = (int)(Config.num_ctx * 0.75);
        if (ApproxTokens() <= budget) return;
        for (int i = 1; i < Messages.Count - 6 && i < Messages.Count; i++)
        {
            Dictionary<string, object> m = Messages[i];
            if (Json.Str(m, "role", "") != "tool") continue;
            string c = Json.Str(m, "content", "");
            if (c.Length > 600) m["content"] = c.Substring(0, 600) + "\n…（上下文压缩，已省略后续内容）";
        }
        if (ApproxTokens() <= budget)
        {
            UI.Write(OutKind.Info, "  · 已压缩较早的工具输出以腾出上下文\n");
            return;
        }
        while (Messages.Count > 6 && ApproxTokens() > budget) Messages.RemoveRange(1, 2);
        UI.Write(OutKind.Info, "  · 上下文接近上限，已丢弃最早的几轮对话\n");
    }
}

// ============================ 控制台界面（CLI 版用） ============================

class ConsoleUI : IAgentUI
{
    public void Write(OutKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ConsoleColor old = Console.ForegroundColor;
        switch (kind)
        {
            case OutKind.User: Console.ForegroundColor = ConsoleColor.Cyan; break;
            case OutKind.Thinking: Console.ForegroundColor = ConsoleColor.DarkGray; break;
            case OutKind.Assistant: Console.ForegroundColor = ConsoleColor.White; break;
            case OutKind.ToolCall: Console.ForegroundColor = ConsoleColor.Yellow; break;
            case OutKind.ToolResult: Console.ForegroundColor = ConsoleColor.DarkGray; break;
            case OutKind.Error: Console.ForegroundColor = ConsoleColor.Red; break;
            case OutKind.Stats: Console.ForegroundColor = ConsoleColor.DarkGray; break;
            default: Console.ForegroundColor = ConsoleColor.Gray; break;
        }
        Console.Write(text);
        Console.ForegroundColor = old;
    }

    public int Approve(string toolName, string[] preview)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ⚠ 需要你确认：" + toolName);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (string line in preview) Console.WriteLine("  " + line);
        Console.ResetColor();
        while (true)
        {
            Console.Write("  执行？[y]允许  [n]拒绝  [a]本会话总是允许：");
            string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (answer == "y" || answer == "yes" || answer == "是" || answer.Length == 0) return 1;
            if (answer == "n" || answer == "no" || answer == "否") return 0;
            if (answer == "a" || answer == "all") return 2;
            Console.WriteLine("  请输入 y / n / a");
        }
    }

    // 越权访问（工作目录外）：无人值守时**默认拒绝** —— 空输入 / EOF 不算批准，
    // 不然脚本跑起来模型就能自己申请、自己被批，等于没有闸门。
    public int ApproveOutside(string toolName, string[] preview)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ⚠ 越权访问请求：" + toolName);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (string line in preview) Console.WriteLine("  " + line);
        Console.ResetColor();
        while (true)
        {
            Console.Write("  批准访问工作目录外？（默认 n）[y]允许  [n]拒绝  [a]本会话总是允许：");
            string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (answer == "y" || answer == "yes" || answer == "是") return 1;
            if (answer == "a" || answer == "all") return 2;
            if (answer == "n" || answer == "no" || answer == "否" || answer.Length == 0) return 0;
            Console.WriteLine("  请输入 y / n / a");
        }
    }

    public void Status(string text)
    {
        Console.Title = "Local Agent — " + text;
    }
}

// ============================ 主题 / 图标 / 自绘控件（对齐 DSH 的浅色观感） ============================

// 配色：白底 + 浅灰侧栏 + 一点点蓝（DeepSeek 蓝）。所有界面颜色都从这里取，改一处全局生效。
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(255, 255, 255);
    public static readonly Color Side = Color.FromArgb(249, 250, 251);
    public static readonly Color Card = Color.FromArgb(249, 250, 251);
    public static readonly Color Field = Color.FromArgb(255, 255, 255);
    public static readonly Color Chip = Color.FromArgb(243, 244, 246);
    public static readonly Color ChipHover = Color.FromArgb(233, 235, 239);
    public static readonly Color Hover = Color.FromArgb(241, 242, 244);
    public static readonly Color Sel = Color.FromArgb(236, 238, 240);
    public static readonly Color Line = Color.FromArgb(231, 232, 234);
    public static readonly Color LineSoft = Color.FromArgb(240, 240, 242);
    public static readonly Color Fg = Color.FromArgb(15, 17, 21);
    public static readonly Color FgMid = Color.FromArgb(88, 93, 100);
    public static readonly Color FgDim = Color.FromArgb(138, 143, 152);
    public static readonly Color FgFaint = Color.FromArgb(170, 175, 182);
    public static readonly Color Accent = Color.FromArgb(77, 107, 254);
    public static readonly Color AccentHover = Color.FromArgb(60, 88, 232);
    public static readonly Color AccentIdle = Color.FromArgb(187, 200, 251);
    public static readonly Color AccentSoft = Color.FromArgb(238, 241, 254);
    public static readonly Color SelFill = Color.FromArgb(110, 77, 107, 254);   // 拖选高亮
    public static readonly Color Band = Color.FromArgb(245, 246, 248);
    public static readonly Color Warn = Color.FromArgb(180, 83, 9);
    public static readonly Color Ok = Color.FromArgb(5, 150, 105);
    public static readonly Color Err = Color.FromArgb(220, 38, 38);
    // diff 面板用的淡底：加行淡绿、删行淡红（跟 Codex 那种审查面板一个观感）
    public static readonly Color OkSoft = Color.FromArgb(236, 253, 245);
    public static readonly Color ErrSoft = Color.FromArgb(254, 242, 242);
    public static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);

    public const string FontName = "Microsoft YaHei UI";
    public const string MonoName = "Consolas";

    // 屏幕缩放（150% 的屏幕上是 1.5）。程序是 DPI 感知的，所以逻辑尺寸要自己乘上去，
    // 否则整块界面在大屏上会小得可怜。
    public static float Dpi = 1f;

    // 屏幕缩放算一次就够了；对话框也可能是被单独打开的（没有主窗口），所以自己兜底
    public static void EnsureDpi()
    {
        if (Dpi > 1.01f) return;
        try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Dpi = g.DpiX / 96f; }
        catch { }
        if (Dpi < 0.5f || Dpi > 4f) Dpi = 1f;
    }

    public static int Px(float logical) { return (int)Math.Round(logical * Dpi); }

    // 程序图标：exe 里嵌了 src\app.ico（build.cmd 的 /win32icon），窗口/对话框都挂上它，
    // 免得标题栏和任务栏还顶着系统默认图标。
    static Icon _appIcon;
    public static Icon AppIcon()
    {
        if (_appIcon != null) return _appIcon;
        try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { }
        return _appIcon;
    }
    public static float Pxf(float logical) { return logical * Dpi; }

    // 固定尺寸的对话框：建完界面后把绝对坐标整体放大一次。
    // 不用 Form.Scale()，因为它会顺手改 Padding 和字体，和自绘控件里的 Px() 打架。
    public static void ScaleBounds(Control root, float f)
    {
        if (f <= 1.01f) return;
        root.SuspendLayout();
        foreach (Control c in root.Controls)
        {
            if (c.Dock == DockStyle.None)
                c.Bounds = new Rectangle(
                    (int)Math.Round(c.Left * f), (int)Math.Round(c.Top * f),
                    (int)Math.Round(c.Width * f), (int)Math.Round(c.Height * f));
            else if (c.Dock == DockStyle.Left || c.Dock == DockStyle.Right)
                c.Width = (int)Math.Round(c.Width * f);
            else if (c.Dock == DockStyle.Top || c.Dock == DockStyle.Bottom)
                c.Height = (int)Math.Round(c.Height * f);
            // 注意：FieldBox 会自己按容器尺寸摆内部输入框（Place()），这里再递归放大一次就会
            // 被放大两次（1.5×1.5）—— 输入框比外框还宽、文字还偏下，看着就是"歪了/跑出框外"。
            if (c.HasChildren && !(c is FieldBox)) ScaleBounds(c, f);
        }
        root.ResumeLayout(false);
    }

    // 字号用「磅」写：磅本身就和屏幕 DPI 挂钩（150% 的屏幕上一个 9.5 磅的字自然大 1.5 倍），
    // 所以这里不要再乘 Dpi，否则字会比界面大一圈。
    public static Font F(float size) { return new Font(FontName, size); }
    public static Font FB(float size) { return new Font(FontName, size, FontStyle.Bold); }
    public static Font M(float size) { return new Font(MonoName, size); }
    public static Font MI(float size) { return new Font(MonoName, size, FontStyle.Italic); }
    public static Font MB(float size) { return new Font(MonoName, size, FontStyle.Bold); }

    // 共享字体缓存：标题/表格头这类字体要在每个消息块里用到，但不能每块都 new 一个（GDI 句柄会爆）
    static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();
    public static Font Cache(string family, float size, FontStyle style)
    {
        string k = family + "|" + size.ToString("0.##") + "|" + (int)style;
        Font f;
        if (FontCache.TryGetValue(k, out f)) return f;
        f = new Font(family, size, style);
        FontCache[k] = f;
        return f;
    }

    public static Color Mix(Color a, Color b, float t)
    {
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}

// 所有图标都手绘：不依赖字体里有没有对应字形，避免出现"豆腐块"
static class Icons
{
    static float PX(Rectangle r, float f) { return r.Left + r.Width * f; }
    static float PY(Rectangle r, float f) { return r.Top + r.Height * f; }
    static PointF Pt(Rectangle r, float x, float y) { return new PointF(PX(r, x), PY(r, y)); }

    public static void Draw(Graphics g, string name, Rectangle r, Color color, float sw)
    {
        if (name == null || name.Length == 0) return;
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (Pen p = new Pen(color, sw * Theme.Dpi))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            switch (name)
            {
                case "spark":
                    DrawSpark(g, r, color);
                    break;

                case "chat":
                    using (GraphicsPath bp = Ui.Round(new Rectangle((int)PX(r, 0.12f), (int)PY(r, 0.16f),
                        (int)(r.Width * 0.76f), (int)(r.Height * 0.56f)), (int)(r.Width * 0.22f)))
                        g.DrawPath(p, bp);
                    g.DrawLines(p, new PointF[] { Pt(r, 0.34f, 0.68f), Pt(r, 0.30f, 0.86f), Pt(r, 0.50f, 0.70f) });
                    break;

                case "folder":
                    g.DrawLines(p, new PointF[] {
                        Pt(r, 0.82f, 0.74f), Pt(r, 0.14f, 0.74f), Pt(r, 0.14f, 0.28f),
                        Pt(r, 0.38f, 0.28f), Pt(r, 0.46f, 0.40f), Pt(r, 0.82f, 0.40f), Pt(r, 0.82f, 0.74f) });
                    break;

                case "gear":
                    g.DrawEllipse(p, PX(r, 0.20f), PY(r, 0.20f), r.Width * 0.60f, r.Height * 0.60f);
                    g.DrawEllipse(p, PX(r, 0.41f), PY(r, 0.41f), r.Width * 0.18f, r.Height * 0.18f);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = Math.PI * 2 * i / 8.0;
                        float cx = PX(r, 0.5f), cy = PY(r, 0.5f);
                        g.DrawLine(p, cx + (float)Math.Cos(a) * r.Width * 0.31f, cy + (float)Math.Sin(a) * r.Height * 0.31f,
                                      cx + (float)Math.Cos(a) * r.Width * 0.46f, cy + (float)Math.Sin(a) * r.Height * 0.46f);
                    }
                    break;

                case "search":
                    g.DrawEllipse(p, PX(r, 0.14f), PY(r, 0.14f), r.Width * 0.52f, r.Height * 0.52f);
                    g.DrawLine(p, PX(r, 0.60f), PY(r, 0.60f), PX(r, 0.86f), PY(r, 0.86f));
                    break;

                case "sort":
                    g.DrawLine(p, PX(r, 0.16f), PY(r, 0.34f), PX(r, 0.62f), PY(r, 0.34f));
                    g.DrawLine(p, PX(r, 0.16f), PY(r, 0.62f), PX(r, 0.46f), PY(r, 0.62f));
                    g.DrawLine(p, PX(r, 0.76f), PY(r, 0.24f), PX(r, 0.76f), PY(r, 0.80f));
                    g.DrawLines(p, new PointF[] { Pt(r, 0.64f, 0.68f), Pt(r, 0.76f, 0.80f), Pt(r, 0.88f, 0.68f) });
                    break;

                case "plus":
                    g.DrawLine(p, PX(r, 0.5f), PY(r, 0.18f), PX(r, 0.5f), PY(r, 0.82f));
                    g.DrawLine(p, PX(r, 0.18f), PY(r, 0.5f), PX(r, 0.82f), PY(r, 0.5f));
                    break;

                case "send":
                    g.DrawLine(p, PX(r, 0.5f), PY(r, 0.80f), PX(r, 0.5f), PY(r, 0.20f));
                    g.DrawLines(p, new PointF[] { Pt(r, 0.28f, 0.44f), Pt(r, 0.5f, 0.20f), Pt(r, 0.72f, 0.44f) });
                    break;

                case "stop":
                    using (GraphicsPath sp2 = Ui.Round(new Rectangle((int)PX(r, 0.28f), (int)PY(r, 0.28f),
                        (int)(r.Width * 0.44f), (int)(r.Height * 0.44f)), 2))
                    using (SolidBrush sb = new SolidBrush(color)) g.FillPath(sb, sp2);
                    break;

                case "lock":
                    using (GraphicsPath lp = Ui.Round(new Rectangle((int)PX(r, 0.22f), (int)PY(r, 0.44f),
                        (int)(r.Width * 0.56f), (int)(r.Height * 0.40f)), 2))
                        g.DrawPath(p, lp);
                    g.DrawArc(p, PX(r, 0.34f), PY(r, 0.18f), r.Width * 0.32f, r.Height * 0.44f, 180, 180);
                    break;

                case "chevronDown":
                    g.DrawLines(p, new PointF[] { Pt(r, 0.28f, 0.42f), Pt(r, 0.5f, 0.62f), Pt(r, 0.72f, 0.42f) });
                    break;

                case "chevronRight":
                    g.DrawLines(p, new PointF[] { Pt(r, 0.40f, 0.26f), Pt(r, 0.62f, 0.5f), Pt(r, 0.40f, 0.74f) });
                    break;

                case "check":
                    g.DrawLines(p, new PointF[] { Pt(r, 0.20f, 0.52f), Pt(r, 0.42f, 0.74f), Pt(r, 0.80f, 0.28f) });
                    break;

                case "close":
                    g.DrawLine(p, PX(r, 0.26f), PY(r, 0.26f), PX(r, 0.74f), PY(r, 0.74f));
                    g.DrawLine(p, PX(r, 0.74f), PY(r, 0.26f), PX(r, 0.26f), PY(r, 0.74f));
                    break;

                case "min":
                    g.DrawLine(p, PX(r, 0.22f), PY(r, 0.52f), PX(r, 0.78f), PY(r, 0.52f));
                    break;

                case "max":
                    g.DrawRectangle(p, PX(r, 0.24f), PY(r, 0.24f), r.Width * 0.52f, r.Height * 0.52f);
                    break;

                case "restore":
                    g.DrawRectangle(p, PX(r, 0.20f), PY(r, 0.32f), r.Width * 0.46f, r.Height * 0.46f);
                    g.DrawLines(p, new PointF[] {
                        Pt(r, 0.34f, 0.26f), Pt(r, 0.80f, 0.26f), Pt(r, 0.80f, 0.66f) });
                    break;

                case "refresh":
                    g.DrawArc(p, PX(r, 0.18f), PY(r, 0.18f), r.Width * 0.64f, r.Height * 0.64f, 40, 280);
                    g.DrawLines(p, new PointF[] { Pt(r, 0.62f, 0.08f), Pt(r, 0.88f, 0.28f), Pt(r, 0.58f, 0.34f) });
                    break;

                case "info":
                    g.DrawEllipse(p, PX(r, 0.14f), PY(r, 0.14f), r.Width * 0.72f, r.Height * 0.72f);
                    g.DrawLine(p, PX(r, 0.5f), PY(r, 0.44f), PX(r, 0.5f), PY(r, 0.70f));
                    using (SolidBrush ib = new SolidBrush(color))
                        g.FillEllipse(ib, PX(r, 0.45f), PY(r, 0.28f), r.Width * 0.10f, r.Height * 0.10f);
                    break;

                case "list":
                    // 三条"日志条目"：小点 + 横线
                    {
                        using (SolidBrush lb = new SolidBrush(color))
                            for (int i = 0; i < 3; i++)
                            {
                                float ly = 0.26f + i * 0.24f;
                                g.FillEllipse(lb, PX(r, 0.16f), PY(r, ly - 0.05f), r.Width * 0.10f, r.Height * 0.10f);
                                g.DrawLine(p, PX(r, 0.38f), PY(r, ly), PX(r, 0.84f), PY(r, ly));
                            }
                    }
                    break;

                case "pen":
                    g.DrawLine(p, PX(r, 0.24f), PY(r, 0.80f), PX(r, 0.76f), PY(r, 0.22f));
                    g.DrawLines(p, new PointF[] { Pt(r, 0.62f, 0.14f), Pt(r, 0.86f, 0.38f), Pt(r, 0.74f, 0.46f) });
                    g.DrawLine(p, PX(r, 0.24f), PY(r, 0.80f), PX(r, 0.40f), PY(r, 0.68f));
                    break;

                case "dots":
                    using (SolidBrush db = new SolidBrush(color))
                        for (int i = 0; i < 3; i++)
                            g.FillEllipse(db, PX(r, 0.18f + i * 0.28f), PY(r, 0.44f), r.Width * 0.12f, r.Height * 0.12f);
                    break;

                case "plug":
                    // 插头：两个插脚 + 插头身 + 线
                    using (GraphicsPath pl = Ui.Round(new Rectangle((int)PX(r, 0.26f), (int)PY(r, 0.38f),
                        (int)(r.Width * 0.48f), (int)(r.Height * 0.38f)), 3))
                        g.DrawPath(p, pl);
                    g.DrawLine(p, PX(r, 0.38f), PY(r, 0.38f), PX(r, 0.38f), PY(r, 0.16f));
                    g.DrawLine(p, PX(r, 0.62f), PY(r, 0.38f), PX(r, 0.62f), PY(r, 0.16f));
                    g.DrawLine(p, PX(r, 0.5f), PY(r, 0.76f), PX(r, 0.5f), PY(r, 0.92f));
                    break;

                case "eye":
                    // 眼睛（只读）
                    g.DrawArc(p, PX(r, 0.10f), PY(r, 0.28f), r.Width * 0.80f, r.Height * 0.44f, 180, 180);
                    g.DrawArc(p, PX(r, 0.10f), PY(r, 0.28f), r.Width * 0.80f, r.Height * 0.44f, 0, 180);
                    g.DrawEllipse(p, PX(r, 0.42f), PY(r, 0.40f), r.Width * 0.16f, r.Height * 0.20f);
                    break;

                case "warn":
                    // 警告三角 + 感叹号（完全权限）
                    g.DrawLines(p, new PointF[] {
                        Pt(r, 0.50f, 0.14f), Pt(r, 0.90f, 0.84f), Pt(r, 0.10f, 0.84f), Pt(r, 0.50f, 0.14f) });
                    g.DrawLine(p, PX(r, 0.50f), PY(r, 0.40f), PX(r, 0.50f), PY(r, 0.60f));
                    using (SolidBrush wmb = new SolidBrush(color))
                        g.FillEllipse(wmb, PX(r, 0.45f), PY(r, 0.68f), r.Width * 0.10f, r.Height * 0.10f);
                    break;

                case "copy":
                    // 两张叠起来的纸（复制）
                    using (GraphicsPath c1 = Ui.Round(new Rectangle((int)PX(r, 0.30f), (int)PY(r, 0.10f),
                        (int)(r.Width * 0.60f), (int)(r.Height * 0.60f)), (int)(r.Width * 0.14f)))
                        g.DrawPath(p, c1);
                    using (GraphicsPath c2 = Ui.Round(new Rectangle((int)PX(r, 0.10f), (int)PY(r, 0.30f),
                        (int)(r.Width * 0.60f), (int)(r.Height * 0.60f)), (int)(r.Width * 0.14f)))
                        g.DrawPath(p, c2);
                    break;

                case "branch":
                    // 分叉：一条竖线，两处斜着分出去，末端各一个点（= 从这条分支出去）
                    g.DrawLine(p, PX(r, 0.30f), PY(r, 0.10f), PX(r, 0.30f), PY(r, 0.90f));
                    g.DrawLines(p, new PointF[] { Pt(r, 0.30f, 0.36f), Pt(r, 0.56f, 0.20f), Pt(r, 0.68f, 0.20f) });
                    g.DrawLines(p, new PointF[] { Pt(r, 0.30f, 0.64f), Pt(r, 0.56f, 0.80f), Pt(r, 0.68f, 0.80f) });
                    using (SolidBrush bb = new SolidBrush(color))
                    {
                        int d = Math.Max(4, (int)(r.Width * 0.26f));
                        g.FillEllipse(bb, PX(r, 0.68f), PY(r, 0.20f) - d / 2, d, d);
                        g.FillEllipse(bb, PX(r, 0.68f), PY(r, 0.80f) - d / 2, d, d);
                    }
                    break;
            }
        }
        g.SmoothingMode = old;
    }

    public static void DrawSpark(Graphics g, Rectangle r, Color color)
    {
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PointF[] pts = new PointF[] {
            Pt(r, 0.50f, 0.04f), Pt(r, 0.60f, 0.40f), Pt(r, 0.96f, 0.50f), Pt(r, 0.60f, 0.60f),
            Pt(r, 0.50f, 0.96f), Pt(r, 0.40f, 0.60f), Pt(r, 0.04f, 0.50f), Pt(r, 0.40f, 0.40f)
        };
        using (SolidBrush b = new SolidBrush(color)) g.FillPolygon(b, pts);
        g.SmoothingMode = old;
    }
}

static class Ui
{
    public static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        if (d > r.Width) d = Math.Max(2, r.Width);
        if (d > r.Height) d = Math.Max(2, r.Height);
        GraphicsPath path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static int TextWidth(string text, Font font)
    {
        if (text == null || text.Length == 0) return 0;
        return TextRenderer.MeasureText(text, font, new Size(6000, 200),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    }

    // 给控件设一个稳定的背景，避免自绘控件在重绘时闪
    public static void Bg(Control c, Color color)
    {
        c.BackColor = color;
    }
}

// 带圆角、可选边框和投影的面板
class RoundedPanel : Panel
{
    public int Radius = 12;
    public Color Fill = Color.White;
    public Color BorderColor = Color.FromArgb(231, 232, 234);
    public bool DrawBorder = true;
    public bool Shadow;
    public int Inset = 4;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    public Rectangle CardRect
    {
        get
        {
            int i = Shadow ? Theme.Px(Inset) : 0;
            return new Rectangle(i, i, Math.Max(2, Width - i * 2 - 1), Math.Max(2, Height - i * 2 - 1));
        }
    }

    // 投影留白（已经按屏幕缩放换算成像素）
    public int InsetPx { get { return Shadow ? Theme.Px(Inset) : 0; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        int inset = Theme.Px(Inset);
        int radius = Theme.Px(Radius);
        if (Shadow)
        {
            for (int i = inset; i >= 1; i--)
            {
                Rectangle sr = new Rectangle(inset - i, inset - i + 1, Width - (inset - i) * 2 - 1, Height - (inset - i) * 2 - 1);
                if (sr.Width <= 2 || sr.Height <= 2) continue;
                using (GraphicsPath sp = Ui.Round(sr, radius + i))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(7, 24, 30, 48))) g.FillPath(sb, sp);
            }
        }
        Rectangle r = CardRect;
        using (GraphicsPath path = Ui.Round(r, radius))
        {
            if (Fill.A > 0) using (SolidBrush b = new SolidBrush(Fill)) g.FillPath(b, path);
            if (DrawBorder && BorderColor.A > 0)
                using (Pen p = new Pen(BorderColor, Theme.Pxf(1f))) g.DrawPath(p, path);
        }
        base.OnPaint(e);
    }
}

// 通用按钮：圆角 / 可选图标 / 可选箭头 / 悬停态，全部自绘
class UiButton : Control, IButtonControl
{
    private DialogResult _dialogResult = DialogResult.None;

    public DialogResult DialogResult
    {
        get { return _dialogResult; }
        set { _dialogResult = value; }
    }

    public void NotifyDefault(bool value) { }

    public void PerformClick() { OnClick(EventArgs.Empty); }

    public string Icon;
    public int IconSize = 15;
    public int IconGap = 7;
    public int Radius = 9;
    public Color FillColor = Theme.Chip;
    public Color HoverColor = Theme.ChipHover;
    public Color BorderColor = Theme.Clear;
    public Color TextColor = Theme.Fg;
    public Color IconColor = Theme.Clear;
    public Color HoverTextColor = Theme.Clear;   // 悬停时的文字/图标颜色（比如关闭键变白）
    public ContentAlignment Align = ContentAlignment.MiddleCenter;
    public bool ShowCaret;
    public bool CaretFlip;                 // 展开时把箭头转 180°（Codex 风格的选择器 chip）
    public bool Active;
    public Color ActiveFill = Theme.AccentSoft;
    public Color ActiveText = Theme.Accent;
    public float IconWeight = 1.5f;
    private bool _hover, _down;

    public UiButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        TabStop = false;
        Font = Theme.F(9f);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    public void SetIcon(string name) { Icon = name; Invalidate(); }

    public int PreferredWidth()
    {
        int iconSize = Theme.Px(IconSize), gap = Theme.Px(IconGap);
        int total = Theme.Px(Padding.Left) + Theme.Px(Padding.Right) + Ui.TextWidth(Text, Font);
        if (Icon != null) total += iconSize + (Text != null && Text.Length > 0 ? gap : 0);
        if (ShowCaret) total += Theme.Px(16);
        return total;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color parentBg = Parent != null ? Parent.BackColor : BackColor;
        using (SolidBrush bg = new SolidBrush(BackColor.A > 0 ? BackColor : parentBg)) g.FillRectangle(bg, ClientRectangle);

        Color fill = FillColor;
        if (!Enabled) fill = FillColor.A > 0 ? Theme.Mix(FillColor, Theme.Bg, 0.5f) : Theme.Clear;
        else if (Active) fill = ActiveFill;
        else if (_hover) fill = _down ? Theme.Mix(HoverColor, Color.Black, 0.06f) : HoverColor;
        Color fg = !Enabled ? Theme.FgFaint : (Active ? ActiveText : TextColor);
        if (Enabled && _hover && HoverTextColor.A > 0) fg = HoverTextColor;

        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Ui.Round(r, Theme.Px(Radius)))
        {
            if (fill.A > 0) using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
            if (BorderColor.A > 0 && Enabled)
                using (Pen p = new Pen(BorderColor, Theme.Pxf(1f))) g.DrawPath(p, path);
        }

        string txt = Text == null ? "" : Text;
        int tw = Ui.TextWidth(txt, Font);
        int iconSize = Theme.Px(IconSize);
        int gap = Theme.Px(IconGap);
        int iconW = Icon == null ? 0 : iconSize;
        int caretW = ShowCaret ? Theme.Px(16) : 0;
        int total = iconW + (tw > 0 ? ((iconW > 0 ? gap : 0) + tw) : 0) + caretW;
        int x;
        if (Align == ContentAlignment.MiddleLeft) x = Theme.Px(Padding.Left);
        else if (Align == ContentAlignment.MiddleRight) x = Width - Theme.Px(Padding.Right) - total;
        else x = (Width - total) / 2;

        if (Icon != null)
        {
            Rectangle ir = new Rectangle(x, (Height - iconSize) / 2, iconSize, iconSize);
            Icons.Draw(g, Icon, ir, IconColor.A > 0 ? IconColor : fg, IconWeight);
            x += iconSize + (tw > 0 ? gap : 0);
        }
        if (tw > 0)
        {
            Rectangle tr = new Rectangle(x, 0, tw + 2, Height);
            TextRenderer.DrawText(g, txt, Font, tr, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
        if (ShowCaret)
        {
            if (!CaretFlip)
                Icons.Draw(g, "chevronDown",
                    new Rectangle(Width - Theme.Px(Padding.Right) - Theme.Px(11), Height / 2 - Theme.Px(5), Theme.Px(11), Theme.Px(10)),
                    fg, 1.4f);
            else
            {
                // 箭头是自绘的，不想为"翻转"再写一条 DrawLine：直接把画布绕箭头中心转 180°
                System.Drawing.Drawing2D.Matrix old = g.Transform;
                g.TranslateTransform(Width - Theme.Px(Padding.Right) - Theme.Px(5.5f), Height / 2);
                g.RotateTransform(180f);
                g.TranslateTransform(-(Width - Theme.Px(Padding.Right) - Theme.Px(5.5f)), -Height / 2);
                Icons.Draw(g, "chevronDown",
                    new Rectangle(Width - Theme.Px(Padding.Right) - Theme.Px(11), Height / 2 - Theme.Px(5), Theme.Px(11), Theme.Px(10)),
                    fg, 1.4f);
                g.Transform = old;
            }
        }
    }
}

// 只有图标的方形按钮（顶栏/侧栏用）
class UiIconButton : UiButton
{
    public UiIconButton(string icon)
    {
        Icon = icon;
        IconSize = 16;
        Radius = 8;
        FillColor = Theme.Clear;
        HoverColor = Theme.Hover;
        TextColor = Theme.FgMid;
        IconColor = Theme.FgMid;
        Padding = new Padding(0);
    }
}

// 下拉 / 菜单按钮：点开一个浅色菜单
class UiDrop : UiButton
{
    public List<object> Items = new List<object>();
    private object _sel;
    public event EventHandler SelectedIndexChanged;
    public event EventHandler ItemChosen;
    public bool KeepText;          // true：只发 ItemChosen，不改按钮上的文字（菜单按钮）
    public string SelectedMark = "check";

    public UiDrop()
    {
        ShowCaret = true;
        Align = ContentAlignment.MiddleLeft;
        Padding = new Padding(10, 0, 12, 0);
        Radius = 9;
        Font = Theme.F(9f);
    }

    public object SelectedItem
    {
        get { return _sel; }
        set { _sel = value; if (!KeepText) Text = value == null ? "" : Convert.ToString(value); Invalidate(); }
    }

    public int SelectedIndex
    {
        get { return _sel == null ? -1 : Items.IndexOf(_sel); }
        set
        {
            if (value < 0 || value >= Items.Count) { _sel = null; if (!KeepText) Text = ""; Invalidate(); return; }
            SelectedItem = Items[value];
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }
    }

    public void AddRange(object[] items) { foreach (object o in items) Items.Add(o); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        ShowMenu();
    }

    public void ShowMenu()
    {
        if (Items.Count == 0) return;
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.ShowImageMargin = false;
        menu.BackColor = Color.White;
        menu.ForeColor = Theme.Fg;
        menu.Font = Font;
        menu.Padding = new Padding(4);
        menu.Renderer = new MenuTheme();
        foreach (object it in Items)
        {
            object captured = it;
            bool isSel = !KeepText && _sel != null && object.Equals(_sel, captured);
            ToolStripMenuItem mi = new ToolStripMenuItem(Convert.ToString(captured));
            mi.Padding = new Padding(Theme.Px(6), Theme.Px(4), Theme.Px(6), Theme.Px(4));
            if (isSel) { mi.Font = Theme.FB(Font.Size); mi.ForeColor = Theme.Accent; }
            mi.Click += delegate(object s, EventArgs a)
            {
                if (KeepText)
                {
                    if (ItemChosen != null) ItemChosen(captured, EventArgs.Empty);
                }
                else
                {
                    SelectedItem = captured;
                    if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                }
            };
            menu.Items.Add(mi);
        }
        menu.Show(this, new Point(0, Height + 6));
    }
}

class MenuTheme : ToolStripProfessionalRenderer
{
    public MenuTheme() : base(new MenuColors()) { RoundedEdges = false; }
}

class MenuColors : ProfessionalColorTable
{
    public override Color MenuItemSelected { get { return Theme.Hover; } }
    public override Color MenuItemBorder { get { return Theme.Hover; } }
    public override Color ToolStripDropDownBackground { get { return Color.White; } }
    public override Color MenuBorder { get { return Theme.Line; } }
    public override Color ImageMarginGradientBegin { get { return Color.White; } }
    public override Color ImageMarginGradientMiddle { get { return Color.White; } }
    public override Color ImageMarginGradientEnd { get { return Color.White; } }
    public override Color SeparatorDark { get { return Theme.LineSoft; } }
    public override Color SeparatorLight { get { return Color.White; } }
}
// 强度面板（Codex 的"推理强度"那张卡片）：顶部大号蓝字档位、下面一行「模型名 ›」、最下面一根滑杆。
// 点「模型名 ›」那行可以切到模型列表（浮层会平滑地换成模型列表那张卡）。
class EffortSlider : Control
{
    public int Value;                 // 0..2，对应 Effort.Levels（低/中/高）
    public string Title = "";         // 中间那行模型名
    public bool EffortSupported = true;   // 这个模型支不支持调强度（不支持就只留模型名，不画滑块）
    public string Head = "";          // 卡片顶部那行小标题（"选择强度"）
    public Color Chip = Theme.Chip;   // "模型名"胶囊底色（浮层卡片是白底，要自己给）
    public event EventHandler ValueChanged;     // 档位真的变了就发
    public event EventHandler ModelClicked;     // 点了「模型名 ›」

    public EffortSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Font = Theme.F(9f);
        Size = new Size(Theme.Px(232), Theme.Px(112));
    }

    int HeadTop { get { return Theme.Px(12); } }
    int TrackL { get { return Theme.Px(22); } }
    int TrackW { get { return Math.Max(Theme.Px(60), Width - Theme.Px(44)); } }
    int TrackY { get { return Theme.Px(106); } }
    int TrackH { get { return Theme.Px(13); } }
    int LevelTop { get { return Theme.Px(36); } }
    int TitleTop { get { return Theme.Px(78); } }
    int TitleH { get { return Theme.Px(22); } }

    // 中间那行「模型名 ›」的胶囊区域（点它 = 换模型）
    public Rectangle TitleRect
    {
        get
        {
            Font f = Theme.F(9f);
            int tw = Ui.TextWidth(Title, f) + Theme.Px(26);
            if (tw > Width - Theme.Px(24)) tw = Width - Theme.Px(24);
            return new Rectangle((Width - tw) / 2, TitleTop, tw, TitleH);
        }
    }

    int LevelCount { get { return Math.Max(2, Effort.Levels.Length); } }

    int IndexAt(int x)
    {
        float f = (float)(x - TrackL) / TrackW;
        if (f < 0) f = 0;
        if (f > 1) f = 1;
        return (int)Math.Round(f * (LevelCount - 1));
    }

    public void SetValue(int v, bool fire)
    {
        if (v < 0) v = 0;
        if (v > LevelCount - 1) v = LevelCount - 1;
        if (v == Value) return;
        Value = v;
        Invalidate();
        if (fire && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

        // 顶部小标题（"选择强度"）
        if (Head != null && Head.Length > 0)
        {
            Font fh = Theme.F(10f);      // 参考图（Codex）那行小标题比正文略大、中灰
            TextRenderer.DrawText(g, Head, fh, new Point(Theme.Px(14), HeadTop), Theme.FgDim,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        // 当前档位（强调蓝、加粗、居中）。不支持调强度的模型也照常显示当前档位文字，
        // 只是下面不画滑块 —— 否则整张卡片会是一片空白（用户报的 bug）。
        string lvl = Effort.Label(Effort.Levels[Value]);
        Font fb = Theme.FB(17f);
        TextRenderer.DrawText(g, lvl, fb,
            new Point((Width - Ui.TextWidth(lvl, fb)) / 2, LevelTop), Theme.Accent,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        // 中间：「模型名 ›」。参考图（Codex）这里是纯灰文字，不是胶囊底 —— 胶囊会看成一枚按钮
        Rectangle tr = TitleRect;
        Font ft = Theme.F(9f);
        string tt = Title == null ? "" : Title;
        int maxTw = tr.Width - Theme.Px(24);
        while (tt.Length > 1 && Ui.TextWidth(tt, ft) > maxTw) tt = tt.Substring(0, tt.Length - 1);   // 太长的模型名裁尾巴
        TextRenderer.DrawText(g, tt, ft, new Point(tr.Left + Theme.Px(10), tr.Top + (tr.Height - ft.Height) / 2),
            Theme.FgDim, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        Icons.Draw(g, "chevronRight",
            new Rectangle(tr.Right - Theme.Px(15), tr.Top + tr.Height / 2 - Theme.Px(5), Theme.Px(9), Theme.Px(10)),
            Theme.FgFaint, 1.2f);

        // 不支持调强度：不画滑块（不要假档位），但把话说明白
        if (!EffortSupported)
        {
            Font fn = Theme.F(8.5f);
            string note = "这个模型不支持调强度";
            TextRenderer.DrawText(g, note, fn,
                new Point((Width - Ui.TextWidth(note, fn)) / 2, TrackY + Theme.Px(2)), Theme.FgFaint,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return;
        }

        // 底部：轨道 + 已填段 + 圆钮（左端一个小圆点 = 最低档）
        using (GraphicsPath tp = Ui.Round(new Rectangle(TrackL, TrackY, TrackW, TrackH), TrackH / 2))
        using (SolidBrush tb = new SolidBrush(Theme.LineSoft)) g.FillPath(tb, tp);
        int knobD = Theme.Px(30);
        int knobCx = TrackL + (Value * TrackW) / (LevelCount - 1);
        int fillW = Math.Max(TrackH, knobCx - TrackL);
        using (GraphicsPath fp = Ui.Round(new Rectangle(TrackL, TrackY, fillW, TrackH), TrackH / 2))
        using (SolidBrush fb2 = new SolidBrush(Theme.Accent)) g.FillPath(fb2, fp);
        using (SolidBrush wb = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            g.FillEllipse(wb, TrackL + Theme.Px(5), TrackY + TrackH / 2 - Theme.Px(3), Theme.Px(6), Theme.Px(6));
        Rectangle knob = new Rectangle(knobCx - knobD / 2, TrackY + TrackH / 2 - knobD / 2, knobD, knobD);
        using (SolidBrush kb = new SolidBrush(Color.White)) g.FillEllipse(kb, knob);
        using (Pen kp = new Pen(Theme.Line, 1f)) g.DrawEllipse(kp, knob);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (TitleRect.Contains(e.Location)) return;              // 点模型名那行留给 OnMouseUp 处理
        if (EffortSupported && e.Y > TrackY - Theme.Px(14)) SetValue(IndexAt(e.X), true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left) return;
        if (EffortSupported && e.Y > TrackY - Theme.Px(14)) SetValue(IndexAt(e.X), true);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (TitleRect.Contains(e.Location) && ModelClicked != null) ModelClicked(this, EventArgs.Empty);
    }

    // ←/→ 微调（浮层把键盘消息转过来）
    protected override bool IsInputKey(Keys keyData)
    {
        if (keyData == Keys.Left || keyData == Keys.Right) return true;
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) { SetValue(Value - 1, true); e.Handled = true; }
        else if (e.KeyCode == Keys.Right) { SetValue(Value + 1, true); e.Handled = true; }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetValue(Value - (e.Delta / 120), true);
    }
}

// 「模型 / 推理等级」卡片里的一行：左边名字、右边当前值、末尾一个 ›
// （WinForms 的菜单项只要带子菜单就不画 ShortcutKeyDisplayString，实测确认，所以这行自己画）
class CardRowCtl : Control
{
    public string Label = "";
    public string Value = "";
    public bool Arrow = true;          // 末尾那个 ›（滑杆那一行不需要）
    public event EventHandler Picked;
    private bool _hover;

    public CardRowCtl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Font = Theme.F(9.5f);
        Size = new Size(Theme.Px(340), Theme.Px(36));
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        if (_hover && Picked != null)
        {
            using (GraphicsPath hp = Ui.Round(new Rectangle(Theme.Px(4), Theme.Px(2),
                Width - Theme.Px(8), Height - Theme.Px(4)), Theme.Px(8)))
            using (SolidBrush hb = new SolidBrush(Theme.Hover)) g.FillPath(hb, hp);
        }
        TextRenderer.DrawText(g, Label, Font, new Point(Theme.Px(14), (Height - Font.Height) / 2),
            Theme.Fg, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        if (Value != null && Value.Length > 0)
        {
            Font fv = Theme.F(9f);
            int vw = Ui.TextWidth(Value, fv);
            TextRenderer.DrawText(g, Value, fv,
                new Point(Width - Theme.Px(34) - vw, (Height - fv.Height) / 2), Theme.FgDim,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        if (Arrow)
            Icons.Draw(g, "chevronRight",
                new Rectangle(Width - Theme.Px(22), Height / 2 - Theme.Px(6), Theme.Px(12), Theme.Px(12)),
                Theme.FgFaint, 1.3f);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (Picked != null) Picked(this, EventArgs.Empty);
    }
}

// 「选择模型」浮层里的列表：整块自己画（行、组标题、滚动条都是画的，不用子控件）。
// 为什么不用 Panel.AutoScroll + 一堆子控件：滚动位置一变，那套自动滚动会把子控件的
// Top 改成"内容坐标 + 滚动量"，再手工滚回顶时列表就整片空白了（实测过）。
class PickItem
{
    public string Label;
    public bool Header;
    public int Y;
    public int H;
}

class ModelPickCtl : Control
{
    public int PickedIndex = -1;
    public event EventHandler Picked;

    private readonly List<PickItem> _items = new List<PickItem>();
    private readonly List<int> _rowItemIdx = new List<int>();     // 第 n 行 -> _items 下标
    private readonly int _rowH;
    private readonly int _headH;
    private readonly int _cur;
    private int _scrollTop;
    private int _contentH;
    private readonly int _maxRows;    // 列表区最多显示几行（多了就滚）
    private Rectangle _listRect;      // 列表区（相对本控件）
    private int _hoverRow = -1;
    private bool _dragBar;
    private int _dragGrab;

    public ModelPickCtl(string[] labels, int cur, int maxRows)
        : this(labels, cur, maxRows, Theme.Px(36), null, 0) { }

    // groups：和 labels 等长，给了就按分组渲染（组下标 0 = 组里第一项，前面留一个组标题）
    // headH：顶部留白（0 = 不要"选择模型"那行标题）
    public ModelPickCtl(string[] labels, int cur, int maxRows, int headH, string[] groups, int groupsFirstRow)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        _rowH = Theme.Px(36);
        _headH = headH;
        _maxRows = maxRows;
        _cur = cur;

        int y = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (groups != null && (i == 0 || groups[i] != groups[i - 1]))
            {
                PickItem h = new PickItem();
                h.Label = groups[i];
                h.Header = true;
                h.Y = y;
                h.H = Theme.Px(26);
                _items.Add(h);
                y += h.H;
            }
            PickItem it = new PickItem();
            it.Label = labels[i];
            it.Y = y;
            it.H = _rowH;
            _rowItemIdx.Add(_items.Count);
            _items.Add(it);
            y += _rowH;
        }
        _contentH = y + Theme.Px(6);
        Size = new Size(Theme.Px(340), _headH + ListH(maxRows, labels.Length));
        _listRect = new Rectangle(0, _headH, Width, ListH(maxRows, labels.Length));
        // 当前模型的自动滚动放在 SetListHeight 里做：构造时还没跟浮层接上，Scrolled 还没人听
    }

    // 卡片宽度（浮层要按 Codex 那种"小卡片"来，不能一路铺满）
    public void SetCardWidth(int w)
    {
        if (w < Theme.Px(140)) w = Theme.Px(140);
        Width = w;
        Invalidate();
    }

    // 列表区（不含顶部那行标题）：最多 maxRows 行，再加上分组标题要的高度
    int ListH(int maxRows, int labelCount)
    {
        int rows = Math.Max(1, Math.Min(maxRows, Math.Max(1, labelCount)));
        int h = rows * _rowH + Theme.Px(2);
        foreach (PickItem it in _items) if (it.Header) h += it.H;
        return h;
    }

    // 注意：这里不要覆写 OnResize 去"按内容撑高自己"。
    // 高度只由构造函数（maxRows）和 SetListHeight（浮层按屏幕截断）决定；
    // 之前那版会在 OnResize 里按"全部行"重算高度，直接把 maxRows 顶掉，列表就不再滚动了。

    // 由浮层显式给定列表区：控件本身高度固定，超出的部分自己滚（不靠 WinForms 的 AutoScroll，
    // 那套东西会改子控件坐标，滚回顶部时列表会整片空白）
    public void SetListHeight(int h)
    {
        if (h < Theme.Px(40)) h = Theme.Px(40);
        _listRect = new Rectangle(0, _headH, Width, h);
        Size = new Size(Width, _headH + h);
        ScrollToCurrent();       // 列表区定下来才知道能滚多远，这时再把当前模型滚进视野
        ClampScroll();
        Invalidate();
    }

    int ViewTop { get { return _listRect.Top; } }
    public int ViewH { get { return Math.Max(Theme.Px(20), _listRect.Height - Theme.Px(4)); } }
    int MaxScroll { get { return Math.Max(0, _contentH - ViewH); } }

    void ClampScroll()
    {
        if (_scrollTop > MaxScroll) _scrollTop = MaxScroll;
        if (_scrollTop < 0) _scrollTop = 0;
    }

    // 行区域宽度（右边留出滚动条的地方）
    int RowW { get { return Math.Max(Theme.Px(60), Width - Theme.Px(14)); } }

    int RowTopInView(int rowIdx)
    {
        int itemIdx = _rowItemIdx[rowIdx];
        return ViewTop + _items[itemIdx].Y - _scrollTop;
    }

    int RowAtY(int y)
    {
        for (int i = 0; i < _rowItemIdx.Count; i++)
        {
            PickItem it = _items[_rowItemIdx[i]];
            int top = ViewTop + it.Y - _scrollTop;
            if (y >= top && y < top + it.H) return i;
        }
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        if (_headH > 0)
        {
            Font ft = Theme.F(10f);      // 和强度卡那行小标题统一（参考图里这行比正文略大、中灰）
            TextRenderer.DrawText(g, "选择模型", ft, new Point(Theme.Px(14), Theme.Px(10)), Theme.FgDim,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        Font fh = Theme.F(8.5f);
        Font fr = Theme.F(9.5f);
        // 行和组标题都画在列表区里，超出列表区的部分裁掉
        Region old = g.Clip;
        g.SetClip(new Rectangle(0, ViewTop, Width, ViewH));
        foreach (PickItem it in _items)
        {
            int y = ViewTop + it.Y - _scrollTop;
            if (y + it.H < ViewTop || y > ViewTop + ViewH) continue;
            if (it.Header)
            {
                TextRenderer.DrawText(g, it.Label, fh, new Point(Theme.Px(20), y + (it.H - fh.Height) / 2),
                    Theme.FgFaint, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                continue;
            }
            int rowIdx = RowIndexOfItem(it);
            bool isCur = rowIdx == _cur;
            Color fill = isCur ? Theme.Sel : (rowIdx == _hoverRow ? Theme.Hover : Color.Empty);
            if (fill.A > 0)
                using (GraphicsPath hp = Ui.Round(new Rectangle(Theme.Px(4), y + Theme.Px(1),
                    RowW - Theme.Px(6), it.H - Theme.Px(2)), Theme.Px(10)))
                using (SolidBrush hb = new SolidBrush(fill)) g.FillPath(hb, hp);
            TextRenderer.DrawText(g, it.Label, fr, new Point(Theme.Px(16), y + (it.H - fr.Height) / 2),
                Theme.Fg, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (isCur)
                Icons.Draw(g, "check", new Rectangle(RowW - Theme.Px(30), y + it.H / 2 - Theme.Px(8),
                    Theme.Px(16), Theme.Px(16)), Theme.Fg, 1.7f);      // 参考图里这个勾是深色，不是蓝色
        }
        g.Clip = old;
        DrawScrollBar(g);
    }

    int RowIndexOfItem(PickItem it)
    {
        return _rowItemIdx.IndexOf(_items.IndexOf(it));
    }

    // 自绘滚动条（只有内容超长才画）：轨道 + 可拖的滑块
    void DrawScrollBar(Graphics g)
    {
        if (MaxScroll <= 0) return;
        int barW = Theme.Px(6);
        int trackX = Width - barW - Theme.Px(4);
        int trackY = ViewTop + Theme.Px(2);
        int trackH = Math.Max(Theme.Px(20), ViewH - Theme.Px(4));
        using (GraphicsPath tp = Ui.Round(new Rectangle(trackX, trackY, barW, trackH), barW / 2))
        using (SolidBrush tb = new SolidBrush(Theme.LineSoft)) g.FillPath(tb, tp);
        int thumbH = Math.Max(Theme.Px(28), trackH * ViewH / Math.Max(1, _contentH));
        int thumbY = trackY + (trackH - thumbH) * _scrollTop / Math.Max(1, MaxScroll);
        using (GraphicsPath hp = Ui.Round(new Rectangle(trackX, thumbY, barW, thumbH), barW / 2))
        using (SolidBrush hb = new SolidBrush(_dragBar ? Theme.FgDim : Theme.FgFaint)) g.FillPath(hb, hp);
    }

    void SetScroll(int y)
    {
        if (y < 0) y = 0;
        if (y > MaxScroll) y = MaxScroll;
        if (y == _scrollTop) return;
        _scrollTop = y;
        Invalidate();
        if (Scrolled != null) Scrolled(this, EventArgs.Empty);
    }

    public void ScrollBy(int dy) { SetScroll(_scrollTop + dy); }

    // 打开浮层时把当前模型滚进视野
    public void ScrollToCurrent()
    {
        if (_cur < 0 || _cur >= _rowItemIdx.Count) return;
        SetScroll(_items[_rowItemIdx[_cur]].Y);      // 当前项顶到列表区最上面（SetScroll 会夹边界）
    }

    // ←/→ 微调由浮层转过来（浮层顶部那个档位面板要用）
    public event EventHandler Scrolled;

    public void HoverRow(int rowIdx)
    {
        if (rowIdx == _hoverRow) return;
        _hoverRow = rowIdx;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragBar)
        {
            int trackH = Math.Max(Theme.Px(20), ViewH - Theme.Px(4));
            int thumbH = Math.Max(Theme.Px(28), trackH * ViewH / Math.Max(1, _contentH));
            int travel = Math.Max(1, trackH - thumbH);
            SetScroll((e.Y - Theme.Px(2) - ViewTop - _dragGrab) * MaxScroll / travel);
            return;
        }
        int r = RowAtY(e.Y);
        if (r != _hoverRow) { _hoverRow = r; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverRow != -1) { _hoverRow = -1; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (MaxScroll > 0 && e.X >= Width - Theme.Px(14))
        {
            _dragBar = true;
            _dragGrab = Theme.Px(14);      // 抓滑块时相对滑块顶部的偏移（随手抓都能拖）
            OnMouseMove(e);
            return;
        }
        int r = RowAtY(e.Y);
        if (r < 0) return;
        PickedIndex = r;
        if (Picked != null) Picked(this, EventArgs.Empty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragBar = false;
        Invalidate();
    }

    // 滚轮：浮层和列表都要处理（滚轮消息有时落在浮层那张卡片上）
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-(e.Delta / 120) * _rowH * 3);
    }

    // ↑/↓ 换一行（浮层把键盘消息转过来）
    protected override bool IsInputKey(Keys keyData)
    {
        if (keyData == Keys.Up || keyData == Keys.Down) return true;
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) return;
        int r = _hoverRow < 0 ? _cur : _hoverRow;
        r += (e.KeyCode == Keys.Down ? 1 : -1);
        if (r < 0) r = 0;
        if (r >= RowCount) r = RowCount - 1;
        HoverRow(r);
        int top = RowTopInView(r);
        if (top < ViewTop) SetScroll(_scrollTop - (ViewTop - top));
        else if (top + _rowH > ViewTop + ViewH) SetScroll(_scrollTop + (top + _rowH - ViewTop - ViewH));
        e.Handled = true;
    }

    // ---- 只给自测用的只读快照（devtools\bench\flicker-unittest.cs 要检查列表状态）----
    public int ListHeight { get { return _headH + ListH(_maxRows, _rowItemIdx.Count); } }
    public int RowCount { get { return _rowItemIdx.Count; } }
    public int CurrentRow { get { return _cur; } }
    public int CheckedCount { get { int n = 0; for (int i = 0; i < RowCount; i++) if (i == _cur) n++; return n; } }
    public int ScrollTop { get { return _scrollTop; } }
    public int ContentH { get { return _contentH; } }
    public int RowH2 { get { return _rowH; } }
    public int HeaderH { get { foreach (PickItem it in _items) if (it.Header) return it.H; return 0; } }
    public int FirstRowTop { get { return RowTopInView(0); } }
    public bool CurrentInView
    {
        get
        {
            if (_cur < 0 || _cur >= _rowItemIdx.Count) return false;
            int top = RowTopInView(_cur);
            // 允许边界上"露一部分"也算看得见（滚一格就能看全）
            return top >= ViewTop - _rowH && top + _rowH <= ViewTop + ViewH + _rowH;
        }
    }
    public List<string> Headers
    {
        get
        {
            List<string> hs = new List<string>();
            foreach (PickItem it in _items) if (it.Header) hs.Add(it.Label);
            return hs;
        }
    }
}

// 自绘浮层：白底 + 圆角 16 + 1px 描边 + 柔和阴影。
// ============================ 输入条上的模型浮层（自绘，不用 ContextMenuStrip） ============================

// 自绘浮层卡片：白底 + 圆角 16 + 1px 描边 + 柔和阴影。
//   ShowWithoutActivation：弹出来不抢输入框焦点（否则正在打的字会掉）
//   卡片矩形由 CardRect 决定；换页/变宽时把它按动画宽度算出来的值传进来（见 ModelPopover.AnimateTo）
class PopoverForm : Form
{
    [DllImport("user32.dll")]
    static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);

    const uint RDW_INVALIDATE = 0x0001;
    const uint RDW_ERASE = 0x0004;
    const uint RDW_UPDATENOW = 0x0100;
    const uint RDW_ALLCHILDREN = 0x0080;
    const uint RDW_FRAME = 0x0400;

    public int Pad = 16;              // 阴影留白（不含它的话圆角边会被切掉）
    public int Gap;                   // 卡片与 chip 之间的间距（逻辑像素，默认 6）
    public int CardW;                 // 卡片宽（换页动画中会变）
    public int CardH;                 // 卡片高（换页动画中会变）
    public bool CardGrowsLeft;        // 卡片是往左边长（右边缘钉住）
    public int ShowTick;              // 弹出来的时刻（TickCount），刚弹出时忽略"失活"事件

    public PopoverForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // 不要设 TopMost：主窗口置顶时它会被压住，而且置顶窗口会牵动前台的激活状态，
        // 实测会让"点 chip 弹浮层"偶发失效。靠 Show()/BringToFront() 保持在自己的 owner 之上就够了。
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.F(9.5f);
        Gap = Theme.Px(6);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    // 卡片（白底圆角矩形）在窗体里的位置：默认右边缘留 Pad，往左长
    public Rectangle CardRect
    {
        get
        {
            int p = Theme.Px(Pad);
            int w = CardW > 0 ? CardW : Width - p * 2;
            int h = CardH > 0 ? CardH : Height - p * 2;
            int x = CardGrowsLeft ? Width - p - w : p;
            return new Rectangle(x, p, w, h);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle card = CardRect;
        // 柔和阴影：几层递减透明度的圆角矩形（投影在下方，观感更像卡片）
        for (int i = 6; i >= 1; i--)
        {
            int a = 8 + (7 - i) * 2;
            Rectangle sr = new Rectangle(card.X - i, card.Y - i + Theme.Px(3), card.Width + i * 2, card.Height + i * 2);
            using (GraphicsPath sp = Ui.Round(sr, Theme.Px(16) + i))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(a, 15, 17, 21))) g.FillPath(sb, sp);
        }
        using (GraphicsPath cp = Ui.Round(card, Theme.Px(16)))
        {
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, cp);
            using (Pen pen = new Pen(Theme.Line, 1f)) g.DrawPath(pen, cp);
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    // 把窗口本身裁成卡片形状（连阴影一起）。这样窗口里就没有"透明边"了 ——
    // 不激活的窗口，透明边不会被重新合成，挪位置后会留下"上一处"的旧像素，
    // 看着就像卡片是半透明的（实测踩过，这是最根治的一种改法）。
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyCardRegion();
    }

    void ApplyCardRegion()
    {
        try
        {
            if (Width <= 0 || Height <= 0) return;
            int p = Theme.Px(Pad);
            Rectangle card = CardRect;
            int grow = p - Theme.Px(2);          // 把阴影也圈进来
            Rectangle outer = new Rectangle(
                Math.Max(0, card.X - grow), Math.Max(0, card.Y - grow),
                Math.Min(Width, card.Width + grow * 2), Math.Min(Height, card.Height + grow * 2));
            using (GraphicsPath gp = Ui.Round(outer, Theme.Px(16) + grow))
                Region = new Region(gp);
        }
        catch (Exception ex) { Log.Swallow(ex, "浮层裁剪"); }
    }

    // 真正显示：先摆好位置再 Show()，然后强制整块重画一次。
    // 为什么需要最后这一步：这窗口是"不激活"的（WS_EX_NOACTIVATE），Windows 不会给它合成
    // 底下那块区域的背景，移动后旧像素/底下内容会透出来，卡片看着像半透明的（实测踩过）。
    public void ShowAt(Point loc)
    {
        Location = loc;
        Show();
        BringToFront();
        ApplyCardRegion();
        ForceRepaint();
        // 让 DWM 把这一块合成上去：不激活的窗口是自己画的，紧接着重画/截屏会看到"还没合成"的空块
        Application.DoEvents();
    }

    // 主窗口被别的窗口盖住过之后，把卡片重新提到前面（在 owner 之上）
    public void Raise() { BringToFront(); ForceRepaint(); }

    public void ForceRepaint()
    {
        try
        {
            if (!IsHandleCreated) return;
            Invalidate(true);
            Update();
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW | RDW_ALLCHILDREN | RDW_FRAME);
        }
        catch (Exception ex) { Log.Swallow(ex, "浮层重画"); }
    }

    // 摆在 chip 上方：水平以 chip 为中心（挂在 chip 上的感觉），竖直紧贴它（间距 Gap）。
    // 上面放不下就翻到下面；两边都放不下就用"地方大的那一边"，并夹进窗口/屏幕。
    public void ShowAbove(Control anchor)
    {
        ShowAbove(anchor, null);
    }

    // 换页会改窗口尺寸，改完要重新贴回 chip（否则会盖住 chip / 偏出中心）
    private Control _anchor;

    public void Reanchor()
    {
        if (_anchor == null || !Visible) return;
        ShowAbove(_anchor, _anchor);
    }

    public void ShowAbove(Control anchor, Control centerOn)
    {
        _anchor = anchor;
        Control c0 = centerOn == null ? anchor : centerOn;
        Point a = c0.PointToScreen(new Point(0, 0));
        Rectangle win = anchor.FindForm() == null ? Screen.FromControl(anchor).WorkingArea
            : anchor.FindForm().RectangleToScreen(anchor.FindForm().ClientRectangle);
        // 可用的竖直范围（窗口 ∩ 屏幕）
        int top = Math.Max(win.Top, Screen.FromControl(anchor).WorkingArea.Top);
        int bottom = Math.Min(win.Bottom, Screen.FromControl(anchor).WorkingArea.Bottom);
        int roomAbove = a.Y - Gap - top;                            // chip 上方还有多少
        int roomBelow = bottom - (a.Y + anchor.Height + Gap);       // chip 下方还有多少
        // 优先上方；上方放不下且下方更宽裕就翻到下面
        bool above = Height <= roomAbove || roomAbove >= roomBelow;
        int y = above ? a.Y - Height - Gap : a.Y + anchor.Height + Gap;
        if (above && y < top) y = top;                              // 夹进可视范围
        if (!above && y + Height > bottom) y = Math.Max(top, bottom - Height);
        if (y + Height > bottom) y = Math.Max(top, bottom - Height);
        if (y < top) y = top;

        Rectangle scr = Screen.FromControl(anchor).WorkingArea;
        int x = a.X + c0.Width / 2 - Width / 2;                     // 居中对齐 chip
        if (x + Width > win.Right) x = win.Right - Width;
        if (x + Width > scr.Right) x = scr.Right - Width;
        if (x < win.Left) x = win.Left;
        if (x < scr.Left) x = scr.Left;
        ShowAt(new Point(x, y));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80;      // WS_EX_TOOLWINDOW：不占 Alt+Tab
            return cp;
        }
    }
}

// 输入条右下角的模型 chip（Codex 风格）：文字「厂商 / 模型 · 档位」+ 自绘箭头，点开自绘浮层
class ModelChip : UiButton
{
    public event EventHandler ChipClicked;
    public event EventHandler SelectedIndexChanged;
    public bool Expanded;
    // 模型列表 + 当前选中的那一个。原来挂在 UiDrop 上，换成自绘 chip 后这一套跟着搬过来
    public List<object> Items = new List<object>();
    private object _sel;

    public ModelChip()
    {
        ShowCaret = true;
        CaretFlip = true;
        Align = ContentAlignment.MiddleLeft;
        FillColor = Theme.Chip;
        HoverColor = Theme.ChipHover;
        TextColor = Theme.Fg;
        Radius = 18;
        Font = Theme.F(9f);
        Padding = new Padding(14, 0, 14, 0);
    }

    public object SelectedItem
    {
        get { return _sel; }
        set { _sel = value; Invalidate(); }
    }

    public int SelectedIndex
    {
        get { return _sel == null ? -1 : Items.IndexOf(_sel); }
        set
        {
            if (value < 0 || value >= Items.Count) { _sel = null; Invalidate(); return; }
            if (Items[value] == _sel) return;
            _sel = Items[value];
            Invalidate();
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }
    }

    public void AddRange(object[] items) { foreach (object o in items) Items.Add(o); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (ChipClicked != null) ChipClicked(this, EventArgs.Empty);
    }
}

// 权限浮层：一列「仅可查看 / 工作区内修改 / 完全权限」，每行一个自绘图标，当前档右侧打勾。
// 整块自己画（不用 ContextMenuStrip），观感和模型浮层一致。
class PermMenu : Control
{
    public int MenuW;                 // 卡片宽（含左右各 6 的内边距）
    public int Current;               // 当前档（0/1/2）
    public event EventHandler RowPicked;
    private int _hover = -1;

    int RowH { get { return Theme.Px(30); } }      // 参考图（Codex）那三行比我们原来松一点
    int PadY { get { return Theme.Px(6); } }
    int PadX { get { return Theme.Px(6); } }

    public PermMenu(int cur)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Current = cur;
        Font = Theme.F(9.5f);
        int textW = 0;
        Font f = Theme.F(9.5f);
        for (int i = 0; i < Perm.Labels.Length; i++)
        {
            int w = Ui.TextWidth(Perm.Labels[i], f);
            if (w > textW) textW = w;
        }
        // 左右内边距 + 图标 + 图标与文字间距 + ✓ 的位置 + 右边距（比原来收窄一档）
        MenuW = textW + Theme.Px(10) + Theme.Px(16) + Theme.Px(8) + Theme.Px(24) + Theme.Px(10) + PadX * 2;
        Size = new Size(MenuW, Perm.Labels.Length * RowH + PadY * 2);
    }

    public Rectangle RowRect(int i)
    {
        return new Rectangle(PadX, PadY + i * RowH, Width - PadX * 2, RowH);
    }

    int RowAt(Point p)
    {
        for (int i = 0; i < Perm.Labels.Length; i++) if (RowRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        Font f = Theme.F(10f);
        for (int i = 0; i < Perm.Labels.Length; i++)
        {
            Rectangle r = RowRect(i);
            if (i == _hover)
                using (GraphicsPath hp = Ui.Round(r, Theme.Px(10)))
                using (SolidBrush hb = new SolidBrush(Theme.Hover)) g.FillPath(hb, hp);
            int ic = Theme.Px(16);
            Icons.Draw(g, Perm.Icons2[i], new Rectangle(r.Left + Theme.Px(9), r.Top + (r.Height - ic) / 2, ic, ic),
                Theme.FgMid, 1.5f);
            TextRenderer.DrawText(g, Perm.Labels[i], f,
                new Rectangle(r.Left + Theme.Px(32), r.Top, r.Width - Theme.Px(58), r.Height), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            if (i == Current)
                Icons.Draw(g, "check", new Rectangle(r.Right - Theme.Px(24), r.Top + r.Height / 2 - Theme.Px(8),
                    Theme.Px(15), Theme.Px(15)), Theme.Fg, 1.7f);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = RowAt(e.Location);
        if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int h = RowAt(e.Location);
        if (h < 0 || h == Current) return;      // 点当前档就不折腾了
        Current = h;
        Invalidate();
        if (RowPicked != null) RowPicked(this, EventArgs.Empty);
    }
}

// 权限浮层（卡片壳子），位置算法和模型浮层一致
class PermPopover : PopoverForm
{
    public int PickedIndex = -1;
    public event EventHandler Chosen;
    private readonly PermMenu _menu;

    public PermPopover(int cur)
    {
        _menu = new PermMenu(cur);
        _menu.Dock = DockStyle.Fill;
        _menu.RowPicked += delegate
        {
            PickedIndex = _menu.Current;
            if (Chosen != null) Chosen(this, EventArgs.Empty);
        };
        int p = Theme.Px(Pad);
        CardGrowsLeft = true;
        CardW = _menu.MenuW;
        CardH = _menu.Height;
        SetClientSizeCore(_menu.MenuW + p * 2, _menu.Height + p * 2);
        _menu.SetBounds(p, p, _menu.MenuW, _menu.Height);
        Controls.Add(_menu);
    }

    public PermMenu MenuCtl { get { return _menu; } }

    // 摆位就用 PopoverForm.ShowAbove：水平以 chip 为中心、竖直紧贴（见那边的注释）
}

// 输入条上那个浮层（Codex 那套）：先弹"推理强度"小卡片，点模型名那行平滑换成"模型列表"卡片，
// 选完模型再平滑换回来。
class ModelPopover : PopoverForm
{
    public const int PageStrength = 0;
    public const int PageModels = 1;
    public event EventHandler Chosen;      // 模型被选中
    public event EventHandler EffortChanged;   // 档位被调了（Low/Mid/High，不是索引）
    public int Page;                       // 当前是哪一页
    public bool Animating;                 // 正在做换页动画

    private readonly ModelPickCtl _pick;
    private readonly EffortSlider _eff;
    // 两页各自想要的卡片宽度。注意别去读控件当前的 Width —— LayoutPage 会把它们撑到卡片宽，
    // 读回来就变成"当前页的宽度"，换回去时算不出目标宽度（实测踩过）。
    private readonly int _wStrength;
    private readonly int _wList;
    private readonly int _hStrength;
    private readonly int _hList;
    private int _fromPage, _toPage;
    private int _fromW, _toW, _fromH, _toH, _animMs;
    private System.Windows.Forms.Timer _anim;
    private System.Windows.Forms.Timer _settle;

    public ModelPopover(string[] labels, string[] groups, int cur, int maxRows,
        string modelName, int effortIdx, bool effortSupported, string effortLevel)
    {
        int p = Theme.Px(Pad);
        // ---- 模型列表页 ----
        // headH = 36：顶部留出「选择模型」那行标题（参考图有，之前被换成只有分组标题了）
        _pick = new ModelPickCtl(labels, cur, maxRows, Theme.Px(36), groups, 0);
        _wList = Theme.Px(220);                  // Codex 那种窄卡片（原来是 340，铺得太宽）
        _pick.SetCardWidth(_wList);
        _pick.Picked += delegate(object s, EventArgs e)
        {
            if (Chosen != null) Chosen(this, EventArgs.Empty);
        };
        MaxH = Screen.FromPoint(Cursor.Position).WorkingArea.Height - Theme.Px(60);
        _hList = Math.Min(_pick.ListHeight, MaxH);
        _pick.SetListHeight(_hList);
        _pick.Scrolled += delegate { _pick.Invalidate(); };
        _pick.Location = new Point(0, 0);        // Location 由 LayoutPage 统一摆
        _pick.Visible = false;
        Controls.Add(_pick);

        // ---- 强度页 ----
        _eff = new EffortSlider();
        _eff.Head = "选择强度";
        _wStrength = Theme.Px(204);
        _hStrength = Theme.Px(136);
        _eff.Title = modelName;
        _eff.EffortSupported = effortSupported;
        _eff.Chip = Theme.Chip;
        _eff.Value = effortIdx;
        _eff.Size = new Size(_wStrength, _hStrength);
        _eff.ValueChanged += delegate
        {
            if (EffortChanged != null) EffortChanged(this, EventArgs.Empty);
        };
        _eff.ModelClicked += delegate { ShowPage(PageModels, true); };
        _eff.Location = new Point(0, 0);
        _eff.Visible = false;
        Controls.Add(_eff);

        // 窗体按"当前那页"的尺寸来；换页时整体动画（宽度和高度一起变）
        CardGrowsLeft = true;                    // 卡片右边缘钉住 chip，往左长
        Page = PageStrength;
        ApplyCard(_wStrength, _hStrength);
        _pick.Visible = false;
        _eff.Visible = true;
    }

    // 只给自测用（devtools\bench\flicker-unittest.cs 要检查列表状态）
    public ModelPickCtl Pick { get { return _pick; } }
    public EffortSlider EffortPage { get { return _eff; } }
    public int PickedIndex { get { return _pick.PickedIndex; } }
    public int MaxH;
    int MaxCardW { get { return Width - Theme.Px(Pad) * 2; } }
    public int CardWidth { get { return CardW; } }

    // 把某一页按给定卡片尺寸摆好（内容靠右，和卡片左边缘一起动）
    void LayoutPage(int page, int cardW, int cardH)
    {
        int p = Theme.Px(Pad);
        int x = CardGrowsLeft ? Width - p - cardW : p;
        Control c = page == PageModels ? (Control)_pick : (Control)_eff;
        c.SetBounds(x, p, cardW, cardH);
    }

    // 换页：从卡片当前尺寸动画到目标尺寸（卡片从右往左长/收、高度也跟着变），结束再切子控件。
    //   immediate=true 时不做动画（自测里要断言换页结果，动画是异步的）
    public void ShowPage(int page, bool animate) { ShowPage(page, animate, false); }

    public void ShowPage(int page, bool animate, bool immediate)
    {
        if (Animating || page == Page) return;
        int targetW = page == PageModels ? _wList : _wStrength;
        int targetH = page == PageModels ? _hListUsed : _hStrength;
        _fromPage = Page;
        _toPage = page;
        _fromW = CardW;
        _toW = targetW;
        _fromH = CardH;
        _toH = targetH;
        Control from = _fromPage == PageModels ? (Control)_pick : (Control)_eff;
        Control to = page == PageModels ? (Control)_pick : (Control)_eff;
        to.Visible = false;
        // 还没显示出来（或自测要求直接换）就别做动画了，直接落位
        if (immediate || !animate || Environment.TickCount - ShowTick < 30)
        {
            from.Visible = false;
            Page = page;
            ApplyCard(targetW, targetH);
            to.Visible = true;
            to.Invalidate();
            Invalidate();
            Reanchor();          // 卡片尺寸变了，重新贴回 chip
            return;
        }
        Animating = true;
        _animMs = 130;
        _animStart = Environment.TickCount;
        if (_anim == null)
        {
            _anim = new System.Windows.Forms.Timer();
            _anim.Interval = 15;
            _anim.Tick += delegate { AnimStep(); };
        }
        _anim.Start();
    }

    int _animStart;

    // 把窗体尺寸和当前页摆到给定的卡片尺寸上（始终保持右边缘钉住 chip）
    void ApplyCard(int w, int h)
    {
        int p = Theme.Px(Pad);
        CardW = w;
        CardH = h;
        SetClientSizeCore(w + p * 2, h + p * 2);
        LayoutPage(Page, w, h);
    }

    void AnimStep()
    {
        float f = (float)(Environment.TickCount - _animStart) / _animMs;
        if (f > 1f) f = 1f;
        float e = 1f - (1f - f) * (1f - f);          // ease-out：起步快、收尾稳
        int w = _fromW + (int)Math.Round((_toW - _fromW) * e);
        int h = _fromH + (int)Math.Round((_toH - _fromH) * e);
        int p = Theme.Px(Pad);
        CardW = w;
        CardH = h;
        SetClientSizeCore(w + p * 2, h + p * 2);
        // 旧页跟着卡片一起动（内容靠右，看着就是"卡片把新页抽出来"）
        LayoutPage(_fromPage == PageModels ? PageModels : PageStrength, w, h);
        if (f >= 1f)
        {
            _anim.Stop();
            Animating = false;
            Control from = _fromPage == PageModels ? (Control)_pick : (Control)_eff;
            Control to = _toPage == PageModels ? (Control)_pick : (Control)_eff;
            from.Visible = false;
            Page = _toPage;
            ApplyCard(_toW, _toH);
            to.Visible = true;
            to.Invalidate();
            Reanchor();          // 卡片尺寸变了，重新贴回 chip
        }
        Invalidate();
    }

    // 强度页的档位改了：更新显示（真正落配置由 MainForm 的 EffortChanged 处理）
    public void RefreshEffort(string level)
    {
        _eff.Value = Effort.Index(level);
        _eff.Invalidate();
    }

    // 换了模型之后刷新强度页（模型名、档位、这个模型支不支持调强度）
    public void Refresh(string modelName, int effortIdx, bool effortSupported)
    {
        _eff.Title = modelName;
        _eff.Value = effortIdx;
        _eff.EffortSupported = effortSupported;
        _eff.Invalidate();
    }

    // 打开浮层：模型列表页要按"chip 上方还有多少地方"截断（不够就在里面滚），所以这里是自己的入口。
    // 水平居中、竖直紧贴 chip 的算法在基类 PopoverForm.ShowAbove 里。
    public void ShowOver(Control anchor)
    {
        Point a = anchor.PointToScreen(new Point(0, 0));
        Rectangle scr = Screen.FromControl(anchor).WorkingArea;
        int p = Theme.Px(Pad);
        // 高度按"chip 上方还有多少地方"截断：列表页放不下就在里面滚，强度页矮，基本不会截
        int room = Math.Max(Theme.Px(80), a.Y - scr.Top - Gap - p * 2);
        if (room < _hList)
        {
            _hListUsed = room;
            _pick.SetListHeight(room);
        }
        else _hListUsed = _hList;
        if (Page == PageModels) ApplyCard(_wList, _hListUsed);
        else ApplyCard(_wStrength, _hStrength);
        // 先只显示当前这一页（另一页留到换页时再露）
        _pick.Visible = Page == PageModels;
        _eff.Visible = Page == PageStrength;
        ShowAbove(anchor, anchor);
        ShowTick = Environment.TickCount;
        // 刚显示出来时控件的可见性还要等一帧才生效，这里再补一次，免得"第一页空白"
        if (_settle == null)
        {
            _settle = new System.Windows.Forms.Timer();
            _settle.Interval = 30;
            _settle.Tick += delegate
            {
                _settle.Stop();
                if (!Visible) return;
                Control c = Page == PageModels ? (Control)_pick : (Control)_eff;
                if (!c.Visible) c.Visible = true;
                c.Invalidate();
                Invalidate();
            };
        }
        _settle.Start();
    }

    int _hListUsed;      // 实际可用的列表高度（被屏幕截断过）

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Page == PageModels) _pick.ScrollBy(-(e.Delta / 120) * Theme.Px(36) * 3);
    }

    // ←/→ 微调强度、↑/↓ 选模型（浮层不抢焦点，键盘消息由 MainForm 转过来）
    public void HandleKey(Keys key)
    {
        if (Page == PageStrength)
        {
            if (key == Keys.Left) _eff.SetValue(_eff.Value - 1, true);
            else if (key == Keys.Right) _eff.SetValue(_eff.Value + 1, true);
        }
        else
        {
            if (key == Keys.Up) _pick.ScrollBy(-Theme.Px(36));
            else if (key == Keys.Down) _pick.ScrollBy(Theme.Px(36));
        }
    }

    // 只给自测用：走一遍"点模型名那行"的真实换页路径。
    // 用 immediate 换页：动画是异步的，自测要马上断言结果。
    public static bool TestClickModelRow(ModelPopover pop, EffortSlider row)
    {
        if (pop == null || row == null) return false;
        pop.ShowPage(ModelPopover.PageModels, true, true);
        return pop.Page == ModelPopover.PageModels;
    }
}
// 应用标志：黑色圆角方块 + 白色四角星
class LogoMark : Control
{
    public int Tile = 0;      // 0 = 填满控件
    public LogoMark()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        Rectangle r = ClientRectangle;
        using (GraphicsPath p = Ui.Round(r, Math.Max(Theme.Px(6), r.Width / 4)))
        using (SolidBrush b = new SolidBrush(Theme.Fg)) g.FillPath(b, p);
        int pad = Math.Max(Theme.Px(3), r.Width / 4);
        Icons.DrawSpark(g, new Rectangle(r.X + pad, r.Y + pad, r.Width - pad * 2, r.Height - pad * 2), Color.White);
    }
}

// 小黑胶囊标签（LOGO 旁边的 LOCAL 之类）
class PillLabel : Control
{
    public Color Fill = Theme.Fg;
    public Color TextColor = Color.White;
    public int Radius = 5;
    public PillLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = Theme.FB(7.5f);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(Radius)))
        using (SolidBrush b = new SolidBrush(Fill)) g.FillPath(b, p);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, TextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// 小圆点（状态指示）
class Dot : Control
{
    public Color DotColor = Theme.Ok;
    public Dot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, ClientRectangle);
        using (SolidBrush b = new SolidBrush(DotColor))
        {
            int d = Math.Min(Width, Height);
            e.Graphics.FillEllipse(b, (Width - d) / 2, (Height - d) / 2, d, d);
        }
    }
}
// 侧栏里的一行：项目（分组标题）或会话
class SessionRowView : Control
{
    public bool IsProject;
    public string SessionId = "";
    public string TimeText = "";
    public bool Selected;
    private bool _hover;
    private Font _fTitle, _fTime;

    public SessionRowView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Side;
        Cursor = Cursors.Hand;
        // 字体建一次就好：每次重绘都 new Font 会把 GDI 句柄耗光
        _fTitle = Theme.F(9.5f);
        _fTime = Theme.F(8.4f);
        Font = _fTitle;
        Height = Theme.Px(32);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_fTitle != null) _fTitle.Dispose();
            if (_fTime != null) _fTime.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

        Color fill = Selected ? Theme.Sel : (_hover ? Theme.Hover : Theme.Clear);
        if (fill.A > 0)
        {
            using (GraphicsPath p = Ui.Round(new Rectangle(Theme.Px(10), Theme.Px(1), Width - Theme.Px(20), Height - Theme.Px(2)), Theme.Px(8)))
            using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
        }

        int iconSize = 15;
        int x = Theme.Px(IsProject ? 16 : 26);
        iconSize = Theme.Px(iconSize);
        Rectangle ir = new Rectangle(x, (Height - iconSize) / 2, iconSize, iconSize);
        Icons.Draw(g, IsProject ? "folder" : "chat", ir, IsProject ? Theme.FgDim : Theme.FgFaint, 1.4f);

        int textX = x + iconSize + Theme.Px(8);
        int timeW = 0;
        if (!IsProject && TimeText != null && TimeText.Length > 0)
        {
            int pad = Theme.Px(10);
            timeW = Ui.TextWidth(TimeText, _fTime) + pad;
            Rectangle tr = new Rectangle(Width - Theme.Px(18) - timeW + pad, 0, timeW - pad, Height);
            TextRenderer.DrawText(g, TimeText, _fTime, tr, Theme.FgFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        Rectangle cr = new Rectangle(textX, 0, Math.Max(10, Width - textX - Theme.Px(16) - timeW), Height);
        TextRenderer.DrawText(g, Text, _fTitle, cr, IsProject ? Theme.FgDim : Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

// 侧栏会话列表（可滚动）
class SidebarList : Panel
{
    public SidebarList()
    {
        AutoScroll = true;
        BackColor = Theme.Side;
        TabStop = false;
    }

    public void SetRows(List<Control> rows)
    {
        SuspendLayout();
        Controls.Clear();
        int y = 0;
        foreach (Control c in rows)
        {
            c.Left = 0;
            c.Top = y;
            c.Width = RowWidth;
            Controls.Add(c);
            y += c.Height + Theme.Px(2);
        }
        AutoScrollMinSize = new Size(0, y + 6);
        ResumeLayout();
    }

    int RowWidth
    {
        get
        {
            int w = ClientSize.Width;
            if (VerticalScroll.Visible) w -= SystemInformation.VerticalScrollBarWidth;
            return Math.Max(120, w);
        }
    }

    void Fit()
    {
        foreach (Control c in Controls) c.Width = RowWidth;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Fit();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // 让滚轮在子控件上也能滚动列表
        int y = -AutoScrollPosition.Y - e.Delta;
        AutoScrollPosition = new Point(0, Math.Max(0, y));
    }
}

// 对话 / 轨迹 两个页签
class TabsBar : Control
{
    public string[] Tabs = new string[] { "对话", "轨迹" };
    public int SelectedIndex;
    public event EventHandler SelectedIndexChanged;
    private int _hot = -1;

    public TabsBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);   // 焦点留在对话区上，Ctrl+C 才归它管
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
    }

    int TabX(int i)
    {
        int x = Theme.Px(22);
        using (Font f = Theme.F(10f))
        {
            for (int k = 0; k < i; k++) x += Ui.TextWidth(Tabs[k], f) + Theme.Px(34);
        }
        return x;
    }

    int HitTest(Point p)
    {
        using (Font f = Theme.F(10f))
        {
            for (int i = 0; i < Tabs.Length; i++)
            {
                int x = TabX(i);
                int w = Ui.TextWidth(Tabs[i], f) + Theme.Px(34);
                if (p.X >= x - Theme.Px(8) && p.X <= x + w - Theme.Px(8)) return i;
            }
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hot) { _hot = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h >= 0 && h != SelectedIndex)
        {
            SelectedIndex = h;
            Invalidate();
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        for (int i = 0; i < Tabs.Length; i++)
        {
            bool sel = i == SelectedIndex;
            Font f = sel ? Theme.FB(10f) : Theme.F(10f);
            Color c = sel ? Theme.Fg : (_hot == i ? Theme.FgMid : Theme.FgDim);
            int x = TabX(i);
            int w = Ui.TextWidth(Tabs[i], f);
            Rectangle tr = new Rectangle(x, 0, w + 4, Height - Theme.Px(6));
            TextRenderer.DrawText(g, Tabs[i], f, tr, c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (sel)
            {
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                using (GraphicsPath p = Ui.Round(new Rectangle(x, Height - Theme.Px(6), w, Theme.Px(3)), Theme.Px(2)))
                    g.FillPath(b, p);
            }
            f.Dispose();
        }
        using (Pen p = new Pen(Theme.LineSoft, Theme.Pxf(1f))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
    }
}

// 带提示语的输入框：提示语由输入框自己画（位置和光标完全一致），
// 而不是另外叠一个 Label —— 那样光标会比提示语高出一截，看着像个小竖线。
// ============================ 对话区：一条消息一个块（学 Codex 的做法） ============================
// 不用一整块 RichTextBox 渲染整段对话——那样每来一个字都要把整块重排、整屏上移，就会"一顶一闪"。
// 这里改成"一条消息一个控件 + 可滚动面板 + 双缓冲"：
//   · 流式输出只重画最后那一条（老内容一动不动）
//   · 自动跟随 = 移动子控件（不重画文字）
//   · 用户往上翻时完全不打扰

class MsgBlock : Control
{
    public OutKind Kind = OutKind.Assistant;
    public readonly StringBuilder Raw = new StringBuilder();
    private int _pads;
    public int Paints = 0;        // 自测用：这一条消息被重画了几次
    public int Rebuilds = 0;      // 自测用：这一条消息被重新排版了几次
    private bool _building;

    // 排版结果
    class Piece
    {
        public string Text;
        public int Style;      // 0 普通 1 粗体 2 行内代码 3 代码块
        public int SrcLen;     // 这一段在原文里占了多少字符（粗体含两对 **，行内代码含两个 `）
        public int SrcAt;      // 这一段的文字在原文里的起始偏移（用来做"点哪儿选哪儿"）
        public Rectangle Rect;
        // 绘制/量宽用的字体。标题这类"按行指定字体"的片段,Style 仍是 0,
        // 只看 Style 就会用正文字号画——排版按大字号、绘制按小字号,行高白占、高亮也会错位
        public Font Font;
    }
    private readonly List<Piece> _pieces = new List<Piece>();
    private int _contentH;

    // 增量排版用的"定稿点"：流式追加时，前面已经排好的部分不会变，只有尾巴要重排。
    // 不做这个的话，一段 12000 字的回答每来一批都要把全篇重新量一遍（实测每批 50ms，最慢 180ms）。
    private int _laidChars;        // 已定稿的字符数（原文偏移）
    private int _laidPieces;       // 已定稿的片段数
    private int _laidX, _laidY;    // 从定稿点继续排版时的起点
    private bool _laidBlank, _laidMidLine;
    private int _laidWrap;         // 定稿时的正文宽度（换了宽度就得全量重排）
    private bool _laidInCode;      // 定稿时是不是停在 ``` 代码块里

    // ---------- 每条下面的操作行：复制 / 分支到新聊天 / 时间 ----------
    private DateTime? _stamp;
    public DateTime? Stamp          // 这条消息的时间（老会话没有就显示空）
    {
        get { return _stamp; }
        set { _stamp = value; }
    }
    public bool ShowCopy, ShowBranch;
    public int HoverAct = -1;
    public string Tip;             // 悬停气泡文字（"复制回复" / "分支到新聊天"）
    public int TipAct = -1;
    internal string Flash;         // 点完之后的气泡（"已复制"），1 秒后自动消失
    internal int FlashAct = -1;
    public int MsgIdx = -1;        // 对应 runner.Messages 里的第几条（-1 = 不确定，分支时按末尾算）
    private int _actY, _actH;

    internal const int ActCopy = 0, ActBranch = 1;

    Rectangle CopyRect() { return new Rectangle(_pads, _actY + Theme.Px(2), Theme.Px(17), Theme.Px(17)); }
    Rectangle BranchRect() { return new Rectangle(_pads + Theme.Px(28), _actY + Theme.Px(2), Theme.Px(17), Theme.Px(17)); }
    internal Rectangle CopyIconRect() { return CopyRect(); }
    internal Rectangle BranchIconRect() { return BranchRect(); }

    internal void SetFlash(string text, int act)
    {
        Flash = text; FlashAct = act;
        Invalidate();
    }

    // ---------- 「已编辑 N 个文件」卡片 ----------
    internal List<Rectangle> CardRows = new List<Rectangle>();
    internal Rectangle CardHeaderRect, CardListRect, CardLinkRect, CardUndoRect;
    internal List<string> CardFiles = new List<string>();
    internal string CardTitle = "已编辑 0 个文件";
    internal int CardIndex = -1, CardHover = -1;
    public bool CardUndone;        // 已经撤销过这一轮的改动

    void BuildCard()
    {
        CardRows.Clear();
        CardFiles.Clear();
        string[] lines = Raw.ToString().Replace("\r\n", "\n").Split('\n');
        foreach (string raw in lines)
        {
            string ln = raw.Trim();
            if (ln.Length == 0) continue;
            if (ln.StartsWith("#card#"))
            {
                int v;
                if (int.TryParse(ln.Substring(6).Trim(), out v)) CardIndex = v;
                continue;
            }
            if (CardFiles.Count == 0 && ln.StartsWith("已编辑")) { CardTitle = ln; continue; }
            CardFiles.Add(ln);
        }
        int w = Math.Max(Theme.Px(120), Width - _pads * 2);
        int headH = Theme.Px(52), rowH = Theme.Px(34);
        int y = Theme.Px(3);
        CardHeaderRect = new Rectangle(_pads, y, w, headH);
        CardLinkRect = new Rectangle(_pads + w - Theme.Px(12) - Theme.Px(92), y + Theme.Px(13), Theme.Px(92), Theme.Px(26));
        CardUndoRect = new Rectangle(CardLinkRect.Left - Theme.Px(8) - Theme.Px(62), y + Theme.Px(17), Theme.Px(62), Theme.Px(20));
        y += headH + Theme.Px(8);
        int top = y;
        for (int i = 0; i < CardFiles.Count; i++) { CardRows.Add(new Rectangle(_pads, y, w, rowH)); y += rowH; }
        CardListRect = new Rectangle(_pads, top, w, Math.Max(0, y - top));
        _contentH = y + Theme.Px(2);
        _actH = 0; _actY = _contentH;
        Height = _contentH + Theme.Px(4);
        Invalidate();
    }

    // 点在卡片上：-1 = "查看变更"，>=0 = 第几个文件，-2 = 没点到
    internal int CardHit(Point pt)
    {
        if (CardLinkRect.Contains(pt)) return -1;
        if (!CardUndone && CardUndoRect.Contains(pt)) return -3;
        for (int i = 0; i < CardRows.Count; i++) if (CardRows[i].Contains(pt)) return i;
        return -2;
    }

    // 点在哪个按钮上（不在操作行就返回 -1）
    public int ActionAt(Point pt)
    {
        if (_actH <= 0 || !ShowCopy) return -1;
        if (pt.Y < _actY || pt.Y > _actY + _actH) return -1;
        if (CopyRect().Contains(pt)) return ActCopy;
        if (ShowBranch && BranchRect().Contains(pt)) return ActBranch;
        return -1;
    }

    public MsgBlock(OutKind kind, Font norm, Font bold, Font mono, Font small)
    {
        Kind = kind;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Bg;
        _pads = Theme.Px(12);
        _fontNorm = norm; _fontBold = bold; _fontMono = mono; _fontSmall = small;
        Font = norm;
        Rebuild();
    }

    public string Body { get { return Raw.ToString(); } }

    // ---------- 选中（拖选 / Ctrl+C 复制）----------

    public int SelStart = -1;
    public int SelEnd = -1;
    public bool HasSel { get { return SelStart >= 0 && SelEnd > SelStart; } }

    public void SetSel(int a, int b)
    {
        int len = Raw.Length;
        if (a < 0) a = 0; if (a > len) a = len;
        if (b < 0) b = 0; if (b > len) b = len;
        if (a > b) { int t = a; a = b; b = t; }
        if (SelStart == a && SelEnd == b) return;
        SelStart = a; SelEnd = b;
        Invalidate();
    }

    public void ClearSel()
    {
        if (SelStart == -1 && SelEnd == -1) return;
        SelStart = -1; SelEnd = -1;
        Invalidate();
    }

    public string SelectedText
    {
        get
        {
            if (!HasSel) return "";
            string t = Raw.ToString();
            int a = Math.Max(0, Math.Min(SelStart, t.Length));
            int b = Math.Max(a, Math.Min(SelEnd, t.Length));
            return t.Substring(a, b - a);
        }
    }

    // 双击选一个词（中文按标点/空白断）
    public void SelectWordAt(int idx)
    {
        string t = Raw.ToString();
        if (idx < 0) idx = 0; if (idx > t.Length) idx = t.Length;
        int a = idx, b = idx;
        while (a > 0 && !IsWordBreak(t[a - 1])) a--;
        while (b < t.Length && !IsWordBreak(t[b])) b++;
        if (a == b && idx > 0) { a--; }
        SetSel(a, b);
    }

    static bool IsWordBreak(char c)
    {
        if (char.IsWhiteSpace(c)) return true;
        string p = "，。、；：！？“”‘’（）《》【】,.!?;:\"'()[]{}<>|/\\-=+*`";
        return p.IndexOf(c) >= 0;
    }

    // 点(x,y) → 字符偏移（找不到就当点的行首/行尾）
    public int CharAt(Point pt)
    {
        if (_pieces.Count == 0) return 0;
        int rowStart = -1;
        for (int i = 0; i < _pieces.Count; i++)
        {
            Rectangle r = _pieces[i].Rect;
            if (pt.Y >= r.Top - Theme.Px(4) && pt.Y <= r.Bottom)
            {
                if (rowStart < 0 || _pieces[i].Rect.Y != _pieces[i - 1].Rect.Y) rowStart = i;
                // 同一行的片段：找到 x 落在哪个片段里
                if (pt.X <= r.Right) return _pieces[i].SrcAt + CharInPiece(i, pt.X);
            }
        }
        // 没落在任何片段里：在最后一行之后就当末尾，否则当这一行的末尾
        if (rowStart >= 0)
        {
            int last = rowStart;
            while (last + 1 < _pieces.Count && _pieces[last + 1].Rect.Y == _pieces[rowStart].Rect.Y) last++;
            return _pieces[last].SrcAt + _pieces[last].Text.Length;
        }
        Rectangle f0 = _pieces[0].Rect;
        if (pt.Y < f0.Top) return 0;
        Piece pl = _pieces[_pieces.Count - 1];
        return pl.SrcAt + pl.Text.Length;
    }

    // 片段内 x → 片段内第几个字符（二分量宽度，够精确）
    int CharInPiece(int i, int x)
    {
        Piece p = _pieces[i];
        if (x <= p.Rect.Left) return 0;
        if (x >= p.Rect.Right) return p.Text.Length;
        Font f = FontOf(p);
        int lo = 0, hi = p.Text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            int w = TextRenderer.MeasureText(p.Text.Substring(0, mid), f, new Size(4000, 100),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (p.Rect.Left + w <= x) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public void Append(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        Raw.Append(s);
        Rebuild();
    }

    // ---------- 排版：把文本切成"行内片段"，算出每个片段的位置 ----------
    void Rebuild()
    {
        // 设 Height 会触发 OnResize 又回到 Rebuild，这里加个闸门，避免自己递归自己
        if (_building || _fontNorm == null) return;
        _building = true;
        try { RebuildCore(); }
        finally { _building = false; }
    }

    // 这条消息的主体字体：工具/思考用等宽，统计/提示用小号，其余用正文体
    Font BaseFont()
    {
        if (Kind == OutKind.ToolCall || Kind == OutKind.ToolResult || Kind == OutKind.Thinking)
            return _fontMono == null ? _fontNorm : _fontMono;
        if (Kind == OutKind.Stats || Kind == OutKind.Info)
            return _fontSmall == null ? _fontNorm : _fontSmall;
        return _fontNorm;
    }

    // 全量重排：字体变了 / 宽度变了 / 文本被改短了才需要
    void ResetLayout()
    {
        _pieces.Clear();
        _laidChars = 0; _laidPieces = 0;
        _laidX = _pads; _laidY = Theme.Px(3);
        _laidBlank = false; _laidMidLine = false; _laidWrap = 0;
        _laidInCode = false;
    }

    // 这一段里没有"还没配对的" ** 或 ` 时，后面再追加文字也不会改变它的样式/位置
    static bool PlainChunk(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            // # 标题、| 表格、` 代码块围栏 — 这些"行首含义"会随后面补上来的字符改变，
            // 所以含这些字符的段不能当成定稿点（否则增量排版和全量重排会不一致）
            if (c == '*' || c == '`' || c == '#' || c == '|') return false;
        }
        return true;
    }

    void RebuildCore()
    {
        Rebuilds++;
        if (Kind == OutKind.Changes) { BuildCard(); return; }     // 卡片有自己的一套排版
        string text = Raw.ToString();
        int wrapW = Math.Max(Theme.Px(80), Width - _pads * 2);
        int lineH = BaseFont().Height + Theme.Px(3);

        if (text.Length < _laidChars || _laidWrap != wrapW) ResetLayout();
        if (_pieces.Count > _laidPieces) _pieces.RemoveRange(_laidPieces, _pieces.Count - _laidPieces);

        int x = _laidX, y = _laidY;
        bool lastBlank = _laidBlank;
        bool midLine = _laidMidLine;         // 上次定稿停在行中间：这次接着这一行往下排
        int baseChars = _laidChars;
        string rest = text.Substring(baseChars);
        int pos = 0;
        bool inCode = _laidInCode;           // 上次停在 ``` 代码块里

        while (pos < rest.Length)
        {
            int nl = rest.IndexOf('\n', pos);
            int lineEnd = nl < 0 ? rest.Length : nl;
            string line = rest.Substring(pos, lineEnd - pos);
            if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);
            int lineAbs = baseChars + pos;
            int nextPos = nl < 0 ? -1 : nl + 1;
            bool isTable = false;

            if (line.Length == 0)
            {
                if (midLine) { x = _pads; y += lineH + Theme.Px(2); lastBlank = false; }
                else { if (y > 0) y += (int)(lineH * 0.55f); lastBlank = true; }
            }
            else
            {
                string trimmed = line.TrimStart();
                if (midLine && pos == 0)
                {
                    // 续排上一次定稿留下的半行：它是普通正文的一部分，不能再按行首去识别标题/列表/表格
                    LayLine(line, lineAbs, null, 0, null, null, lineH, wrapW, 0, ref x, ref y);
                    x = _pads; y += lineH + Theme.Px(2);
                }
                else if (trimmed.StartsWith("```"))
                {
                    // 代码块围栏：本身不画字，只留一点间距
                    if (inCode) { inCode = false; y += Theme.Px(2); }
                    else { inCode = true; if (y > 0) y += Theme.Px(4); }
                    lastBlank = false;
                    x = _pads; y += lineH + Theme.Px(2);
                }
                else if (inCode)
                {
                    // 代码块内容：等宽、浅底、保留原样
                    LayLine(line, lineAbs, _fontMono, 0, null, null, lineH, wrapW, 3, ref x, ref y);
                    x = _pads; y += lineH + Theme.Px(2);
                }
                else if (Kind == OutKind.Assistant && ToolNoise.IsAnnounceLine(line))
                {
                    // 模型把"要调用工具"写成了正文：不显示（否则对话区会被十几条 [调用] 刷屏）
                    x = _pads; y += Theme.Px(3);
                    lastBlank = false;
                }
                else
                {
                    int hPrefix;
                    int lvl = HeadingLevel(line, out hPrefix);
                    if (lvl > 0)
                    {
                        Font hf = HeadingFont(lvl);
                        int hh = hf.Height + Theme.Px(3);
                        if (y > 0) y += Theme.Px(lvl == 1 ? 10 : 7);
                        string body = line.Substring(hPrefix).TrimStart();
                        LayLine(body, lineAbs + (line.Length - body.Length), hf, 0, null, null, hh, wrapW, 0, ref x, ref y);
                        x = _pads; y += hh + Theme.Px(4);
                    }
                    else if (IsTableRow(line))
                    {
                        nextPos = LayTable(rest, pos, lineAbs, lineH, wrapW, ref x, ref y);   // 整张表一次排完
                        isTable = true;      // 表格要整块重排，别在它中间定稿
                    }
                    else
                    {
                        int hang = 0;
                        string marker = null;
                        string body = ListBody(line, out marker);
                        if (marker == null) body = line; else hang = Theme.Px(20);
                        if (lastBlank && y > 0) y += (int)(lineH * 0.15f);
                        lastBlank = false;
                        LayLine(body, lineAbs + (line.Length - body.Length), null, hang, marker, _fontBold, lineH, wrapW, 0, ref x, ref y);
                        x = _pads; y += lineH + Theme.Px(2);
                    }
                }
            }
            midLine = false;
            if (nextPos < 0) break;                  // 最后一行还没换行：这一行不算定稿
            pos = nextPos;                           // LayTable 可能一次吃掉好几行
            if (isTable) continue;                   // 表格：定稿点留在表格开头，下次整张重排
            // 整行定稿（行首状态）
            _laidChars = baseChars + pos;
            _laidPieces = _pieces.Count;
            _laidX = _pads; _laidY = y;
            _laidBlank = lastBlank; _laidMidLine = false; _laidWrap = wrapW;
            _laidInCode = inCode;
        }

        _contentH = Math.Max(1, y);
        _actH = (ShowCopy || ShowBranch) ? Theme.Px(22) : 0;
        _actY = _contentH + Theme.Px(2);
        Height = _contentH + Theme.Px(4) + _actH;
        _laidWrap = wrapW;
        Invalidate();
    }

    // ---------- 块级 Markdown：标题 / 列表 / 代码块 / 表格 ----------

    // "## 标题" → 返回 # 的个数（1..6），prefixLen = 前导空格 + # 的长度；不是标题返回 0
    static int HeadingLevel(string line, out int prefixLen)
    {
        prefixLen = 0;
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        int n = 0;
        while (i + n < line.Length && line[i + n] == '#') n++;
        if (n < 1 || n > 6) return 0;
        if (i + n >= line.Length || line[i + n] != ' ') return 0;   // Markdown 要求 # 后面有空格
        prefixLen = i + n;
        return n;
    }

    Font HeadingFont(int lvl)
    {
        float size = _fontBold != null ? _fontBold.Size : 10.5f;
        float f = lvl == 1 ? 1.5f : (lvl == 2 ? 1.28f : 1.13f);
        return Theme.Cache(Theme.FontName, size * f, FontStyle.Bold);
    }

    Font MonoBoldFont()
    {
        float size = _fontMono != null ? _fontMono.Size : 9.5f;
        return Theme.Cache(Theme.MonoName, size, FontStyle.Bold);
    }

    // "- 项目" / "1. 项目" → 返回正文，marker 里给出要画的标记
    static string ListBody(string line, out string marker)
    {
        marker = null;
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        if (i >= line.Length) return line;
        string t = line.Substring(i);
        if (t.Length >= 2 && (t[0] == '-' || t[0] == '*' || t[0] == '+' || t[0] == '•') && t[1] == ' ')
        {
            marker = "•";
            return t.Substring(2);
        }
        int d = 0;
        while (d < t.Length && char.IsDigit(t[d])) d++;
        if (d > 0 && d + 1 < t.Length && (t[d] == '.' || t[d] == ')' || t[d] == '、') && t[d + 1] == ' ')
        {
            marker = t.Substring(0, d + 1);
            return t.Substring(d + 2);
        }
        return line;
    }

    static bool IsTableRow(string line)
    {
        int a = line.IndexOf('|');
        return a >= 0 && line.LastIndexOf('|') > a;
    }

    static bool IsTableRule(List<string> cells)
    {
        bool any = false;
        foreach (string c in cells)
        {
            string s = c.Trim();
            if (s.Length == 0) continue;
            any = true;
            foreach (char ch in s) if (ch != '-' && ch != ':' && ch != ' ') return false;
        }
        return any;
    }

    static List<string> TableCells(string line)
    {
        List<string> cells = new List<string>();
        string[] raw = line.Split('|');
        for (int k = 0; k < raw.Length; k++)
        {
            if (k == 0 && raw[k].Trim().Length == 0) continue;                 // 开头的空段（外框）
            if (k == raw.Length - 1 && raw[k].Trim().Length == 0) continue;     // 结尾的空段
            cells.Add(raw[k].Trim());
        }
        return cells;
    }

    static int Measure(string s, Font f)
    {
        if (s == null || s.Length == 0) return 0;
        return TextRenderer.MeasureText(s, f, new Size(4000, 100),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    }

    // 排一行（可带行首标记 + 悬挂缩进 + 指定字体）；顺带把安全的段推进成定稿点
    void LayLine(string line, int lineAbs, Font forceFont, int hang, string marker, Font markerFont,
                 int lineH, int wrapW, int style, ref int x, ref int y)
    {
        int left = _pads + hang;
        if (marker != null && marker.Length > 0)
        {
            Font mf = markerFont != null ? markerFont : (forceFont != null ? forceFont : BaseFont());
            Piece mp = new Piece();
            mp.Text = marker;
            mp.Style = 4;
            mp.SrcLen = 0;
            mp.SrcAt = lineAbs;
            mp.Font = mf;
            // 标记右对齐到悬挂缩进处：这样 "•" 和 "10." 的文字起始位置一致，不会忽左忽右
            int mw = Math.Max(1, Measure(marker, mf));
            mp.Rect = new Rectangle(Math.Max(_pads, _pads + hang - mw - Theme.Px(6)), y, mw, lineH + Theme.Px(2));
            _pieces.Add(mp);
        }
        x = left;
        // 续排（上次定稿停在行中间）时，调用方给的 x 已经排到一半了，不能退回行首
        if (_laidMidLine && _laidChars == lineAbs) x = _laidX;
        int partAt = 0;
        foreach (Piece p in (style == 3 ? CodeParts(line) : SplitInline(line)))
        {
            Font f = forceFont != null ? forceFont : FontFor(p.Style);
            string seg = p.Text;
            int markerLen = p.Style == 1 ? 2 : (p.Style == 2 ? 1 : 0);
            // inPart 是"这一段内部"已经排掉的字符数：必须每段归零，
            // 否则第二段起的 SrcAt 会把前面所有段的长度重复加一遍（选中高亮就会错位、盖不住字）
            int inPart = 0;
            while (seg.Length > 0)
            {
                int room = wrapW - (x - _pads);
                if (room < Theme.Px(24) && x > left)
                {
                    x = left;
                    y += lineH + Theme.Px(2);
                    room = wrapW - (x - _pads);
                }
                int avail = Math.Max(Theme.Px(24), room);
                int fit = Fitting(seg, f, avail);
                string head = seg.Substring(0, fit);
                Piece one = new Piece();
                one.Text = head;
                one.Style = style > 0 ? style : p.Style;
                one.SrcLen = head.Length;
                one.Font = f;
                // ** / ` 这些标记字符只在段首出现一次，折行后的续块不能再加
                one.SrcAt = lineAbs + partAt + (inPart == 0 ? markerLen : 0) + inPart;
                one.Rect = new Rectangle(x, y, Math.Max(1, Measure(head, f)), lineH + Theme.Px(2));
                _pieces.Add(one);
                inPart += head.Length;
                x += one.Rect.Width;
                seg = seg.Substring(fit);
                if (seg.Length > 0) { x = left; y += lineH + Theme.Px(2); }
            }
            if (p.Style == 2) x += Theme.Px(3);
            partAt += p.SrcLen;
            // 只有"普通正文行"才允许行中定稿：标题/列表/代码行都有"行首含义"，
            // 从行中间续排会丢掉这个上下文（字号、悬挂缩进、等宽），必须整行重排
            if (style == 0 && hang == 0 && marker == null && forceFont == null && p.Text.Length > 0 && PlainChunk(p.Text))
            {
                _laidChars = lineAbs + partAt;
                _laidPieces = _pieces.Count;
                _laidX = x; _laidY = y;
                _laidBlank = false; _laidMidLine = true; _laidWrap = wrapW;
            }
        }
    }

    // 代码块里的内容不做行内解析（原样等宽）
    static List<Piece> CodeParts(string line)
    {
        List<Piece> l = new List<Piece>();
        Piece p = new Piece();
        p.Text = line.Length == 0 ? " " : line;
        p.Style = 3;
        p.SrcLen = line.Length;
        l.Add(p);
        return l;
    }

    // 排一整张表格（连续多行）；返回下一行的起点（-1 = 到末尾了）
    int LayTable(string rest, int pos, int lineAbs, int lineH, int wrapW, ref int x, ref int y)
    {
        List<List<string>> rows = new List<List<string>>();
        List<int> rowAbs = new List<int>();
        List<int> rowNext = new List<int>();
        int p2 = pos;
        while (p2 < rest.Length)
        {
            int nl2 = rest.IndexOf('\n', p2);
            int end2 = nl2 < 0 ? rest.Length : nl2;
            string l2 = rest.Substring(p2, end2 - p2);
            if (l2.Length > 0 && l2[l2.Length - 1] == '\r') l2 = l2.Substring(0, l2.Length - 1);
            if (!IsTableRow(l2)) break;
            rows.Add(TableCells(l2));
            rowAbs.Add(lineAbs + (p2 - pos));
            rowNext.Add(nl2 < 0 ? -1 : nl2 + 1);
            p2 = nl2 < 0 ? rest.Length : nl2 + 1;
        }
        if (rows.Count == 0) return -1;
        int cols = 0;
        foreach (List<string> r in rows) if (r.Count > cols) cols = r.Count;
        Font fm = _fontMono != null ? _fontMono : BaseFont();
        Font fh = MonoBoldFont();
        int[] colW = new int[cols];
        for (int k = 0; k < rows.Count; k++)
        {
            if (IsTableRule(rows[k])) continue;
            for (int j = 0; j < rows[k].Count; j++)
            {
                int w = Measure(rows[k][j], fm);
                if (w > colW[j]) colW[j] = w;
            }
        }
        int pad = Theme.Px(18), total = 0;
        for (int j = 0; j < cols; j++) { colW[j] += pad; total += colW[j]; }
        if (total > wrapW) return -1;                       // 太宽：退回普通文本折行
        int[] colX = new int[cols];
        int cx = _pads;
        for (int j = 0; j < cols; j++) { colX[j] = cx; cx += colW[j]; }
        for (int k = 0; k < rows.Count; k++)
        {
            if (IsTableRule(rows[k]))
            {
                Piece sp = new Piece();
                sp.Text = "";
                sp.Style = 6;
                sp.SrcLen = 0;
                sp.SrcAt = rowAbs[k];
                sp.Rect = new Rectangle(_pads, y + lineH / 2, total, Math.Max(1, Theme.Px(1)));
                _pieces.Add(sp);
            }
            else
            {
                for (int j = 0; j < rows[k].Count; j++)
                {
                    if (rows[k][j].Length == 0) continue;
                    Piece cp = new Piece();
                    cp.Text = rows[k][j];
                    cp.Style = k == 0 ? 5 : 4;
                    cp.SrcLen = 0;
                    cp.SrcAt = rowAbs[k];
                    cp.Font = k == 0 ? fh : fm;
                    cp.Rect = new Rectangle(colX[j], y, colW[j], lineH + Theme.Px(2));
                    _pieces.Add(cp);
                }
            }
            y += lineH + Theme.Px(2);
        }
        x = _pads;
        return rowNext[rowNext.Count - 1];
    }

    // 一行放得下多少字符（按宽度切，尽量在空格处断）
    int Fitting(string s, Font f, int avail)
    {
        if (s.Length == 0) return 0;
        // 注意：不能直接把一整行长文本丢给 MeasureText —— 文本超出测量框时它会给一个被截断的错宽度
        // （实测 2700 字的一行会被量成 1032px，于是片段宽度溢出到框外）。
        // 这里先按"最窄的字也不会小于 4px"估出上限，再在这段里二分。
        int lo = 0, hi;
        int guess = Math.Max(1, avail / 4 + 2);
        while (true)
        {
            hi = Math.Min(s.Length, guess);
            int w = TextRenderer.MeasureText(s.Substring(0, hi), f, new Size(4000, 100),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (w <= avail)
            {
                lo = hi;
                if (hi >= s.Length) return s.Length;
                guess = hi * 2;
                continue;
            }
            break;
        }
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            int mw = TextRenderer.MeasureText(s.Substring(0, mid), f, new Size(4000, 100),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (mw <= avail) lo = mid; else hi = mid - 1;
        }
        // 尽量断在空格/标点后
        int cut = lo;
        for (int i = lo - 1; i > lo - 12 && i > 0; i--)
        {
            char c = s[i - 1];
            if (c == ' ' || c == '，' || c == '。' || c == ',' || c == '.' || c == '、' || c == '：' || c == ':') { cut = i; break; }
        }
        return Math.Max(1, cut);
    }

    List<Piece> SplitInline(string line)
    {
        List<Piece> parts = new List<Piece>();
        int i = 0;
        StringBuilder buf = new StringBuilder();
        while (i < line.Length)
        {
            if (line[i] == '`')
            {
                int j = line.IndexOf('`', i + 1);
                if (j > i)
                {
                    if (buf.Length > 0) { parts.Add(Make(buf.ToString(), 0)); buf.Length = 0; }
                    parts.Add(Make(line.Substring(i + 1, j - i - 1), 2));
                    i = j + 1;
                    continue;
                }
            }
            if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '*')
            {
                int j = line.IndexOf("**", i + 2);
                if (j > i)
                {
                    if (buf.Length > 0) { parts.Add(Make(buf.ToString(), 0)); buf.Length = 0; }
                    parts.Add(Make(line.Substring(i + 2, j - i - 2), 1));
                    i = j + 2;
                    continue;
                }
            }
            buf.Append(line[i]);
            i++;
        }
        if (buf.Length > 0) parts.Add(Make(buf.ToString(), 0));
        if (parts.Count == 0) parts.Add(Make(line, 0));
        return parts;
    }

    static Piece Make(string text, int style)
    {
        Piece p = new Piece();
        p.Text = text;
        p.Style = style;
        // 粗体在原文里多出两对 **（4 个字符），行内代码多出两个 `
        p.SrcLen = style == 1 ? text.Length + 4 : (style == 2 ? text.Length + 2 : text.Length);
        return p;
    }

    Font FontFor(int style)
    {
        if (style == 1) return _fontBold == null ? _fontNorm : _fontBold;
        if (style == 2 || style == 3 || style == 4) return _fontMono == null ? _fontNorm : _fontMono;
        if (style == 5) return MonoBoldFont();
        return BaseFont();
    }

    // 片段真正该用的字体：排版时定下来的那个（标题就不是正文字号）
    Font FontOf(Piece p)
    {
        return p.Font != null ? p.Font : FontFor(p.Style);
    }

    // 自测用：第 i 个片段绘制时用的字体
    internal Font FontAt(int i)
    {
        return (i >= 0 && i < _pieces.Count) ? FontOf(_pieces[i]) : null;
    }

    internal int PieceCount { get { return _pieces.Count; } }

    private Font _fontNorm, _fontBold, _fontMono, _fontSmall;
    public void SetFonts(Font norm, Font bold, Font mono, Font small)
    {
        _fontNorm = norm; _fontBold = bold; _fontMono = mono; _fontSmall = small;
        Font = norm;
        ResetLayout();          // 字号变了，之前的定稿点全部作废
        Rebuild();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_fontNorm != null) Rebuild();
    }

    // 子控件默认会把滚轮消息吞掉（鼠标压在文字上就滚不动），这里转给滚动面板
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x020A && Parent != null && Parent.IsHandleCreated)   // WM_MOUSEWHEEL
        {
            Native.SendMsg(Parent.Handle, m.Msg, m.WParam, m.LParam);
            return;
        }
        base.WndProc(ref m);
    }

    // 鼠标事件转给对话区：拖选要在整块面板上做
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Kind == OutKind.Changes)
        {
            int hit = CardHit(e.Location);
            if (hit >= 0 || hit == -1)
            {
                TranscriptView tv1 = Parent as TranscriptView;
                if (tv1 != null) tv1.ChildCardAction(this, hit);
            }
            return;                       // 卡片不参与拖选
        }
        int act = ActionAt(e.Location);
        if (act >= 0)
        {
            TranscriptView tv0 = Parent as TranscriptView;
            if (tv0 != null) tv0.ChildAction(this, act);
            return;                       // 点按钮不进入拖选
        }
        TranscriptView tv = Parent as TranscriptView;
        if (tv != null) tv.ChildMouseDown(this, e);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Kind == OutKind.Changes)
        {
            int hit = CardHit(e.Location);
            int hov = hit >= 0 ? hit : -1;
            if (hov != CardHover)
            {
                CardHover = hov;
                Cursor = (hit >= 0 || hit == -1) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            return;
        }
        HoverAt(e.Location);
        TranscriptView tv = Parent as TranscriptView;
        if (tv != null) tv.ChildMouseMove(this, e);
        base.OnMouseMove(e);
    }

    // 悬停状态（鼠标事件和自测都走这里）
    internal void HoverAt(Point pt)
    {
        int act = ActionAt(pt);
        if (act != HoverAct)
        {
            HoverAct = act;
            Cursor = act >= 0 ? Cursors.Hand : Cursors.Default;
            Tip = act == ActCopy ? "复制回复" : (act == ActBranch ? "分支到新聊天" : null);
            TipAct = act;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (HoverAct != -1 || Tip != null)
        {
            HoverAct = -1; Tip = null; TipAct = -1; Cursor = Cursors.Default;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        TranscriptView tv = Parent as TranscriptView;
        if (tv != null) tv.ChildMouseUp(this, e);
        base.OnMouseUp(e);
    }

    // ---------- 画 ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        Paints++;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fg = Theme.Fg;
        Color back = Theme.Bg;
        switch (Kind)
        {
            case OutKind.User: back = Theme.Band; fg = Theme.Fg; break;
            case OutKind.Thinking: fg = Theme.FgDim; break;
            case OutKind.ToolCall: fg = Theme.FgMid; break;
            case OutKind.ToolResult: fg = Theme.FgFaint; break;
            case OutKind.Error: fg = Theme.Err; break;
            case OutKind.Info: fg = Theme.FgDim; break;
            case OutKind.Stats: fg = Theme.FgFaint; break;
        }
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        if (Kind == OutKind.User)
        {
            using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, Width - 1, _contentH), Theme.Px(10)))
            using (SolidBrush b = new SolidBrush(back)) g.FillPath(b, p);
        }
        if (Kind == OutKind.Changes) { DrawCard(g); return; }

        // 分三趟画：先底（代码块底/行内代码底/表格线）→ 再选中高亮 → 最后文字。
        // 高亮必须在"所有底色之上、文字之下"，否则合并后的高亮会被后面的代码底色盖住。
        foreach (Piece p in _pieces)
        {
            if (p.Style == 6)
                using (SolidBrush lb = new SolidBrush(Theme.LineSoft)) g.FillRectangle(lb, p.Rect);
            else if (p.Style == 3)
                using (SolidBrush cb = new SolidBrush(Theme.Card))
                    g.FillRectangle(cb, _pads - Theme.Px(6), p.Rect.Y + Theme.Px(1),
                        Math.Max(Theme.Px(20), Width - 2 * _pads + Theme.Px(12)), p.Rect.Height - Theme.Px(2));
            else if (p.Style == 2)
                using (GraphicsPath cp = Ui.Round(new Rectangle(p.Rect.X - Theme.Px(2), p.Rect.Y + Theme.Px(1),
                        p.Rect.Width + Theme.Px(4), p.Rect.Height - Theme.Px(2)), Theme.Px(4)))
                using (SolidBrush cb = new SolidBrush(Theme.Chip)) g.FillPath(cb, cp);
        }
        if (HasSel)
            foreach (Rectangle hr in HighlightRects())
                using (SolidBrush sb = new SolidBrush(Theme.SelFill)) g.FillRectangle(sb, hr);
        foreach (Piece p in _pieces)
        {
            if (p.Style == 6) continue;
            Font f = FontOf(p);
            TextRenderer.DrawText(g, p.Text, f, new Point(p.Rect.X, p.Rect.Y + Theme.Px(1)),
                (p.Style == 2 || p.Style == 3 || p.Style == 4 || p.Style == 5) ? Theme.Fg : fg,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        // 操作行：复制 / 分支 / 时间
        if (_actH > 0)
        {
            Rectangle cr = CopyRect();
            Color cc = HoverAct == ActCopy ? Theme.FgMid : Theme.FgFaint;
            if (HoverAct == ActCopy) DrawActHover(g, cr);
            Icons.Draw(g, "copy", cr, cc, 1.15f);
            if (ShowBranch)
            {
                Rectangle br = BranchRect();
                Color bc = HoverAct == ActBranch ? Theme.FgMid : Theme.FgFaint;
                if (HoverAct == ActBranch) DrawActHover(g, br);
                Icons.Draw(g, "branch", br, bc, 1.15f);
            }
            if (Stamp.HasValue)
            {
                Font f2 = _fontSmall != null ? _fontSmall : _fontNorm;
                string ts = Stamp.Value.ToString("HH:mm");
                int tw = Ui.TextWidth(ts, f2);
                TextRenderer.DrawText(g, ts, f2, new Point(Math.Max(_pads, Width - _pads - tw - Theme.Px(2)), _actY + Theme.Px(4)),
                    Theme.FgFaint, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }

            // 深色小气泡：悬停时给按钮名，点完给"已复制"
            string bubble = Flash != null && Flash.Length > 0 ? Flash : Tip;
            int bAct = Flash != null && Flash.Length > 0 ? FlashAct : TipAct;
            if (bubble != null && bubble.Length > 0)
                DrawBubble(g, bubble, bAct == ActBranch ? BranchRect() : CopyRect());
        }
    }

    // 按钮上方的深色提示气泡（和 Codex 一样）
    // 画「已编辑 N 个文件 + 文件列表」两张卡
    void DrawCard(Graphics g)
    {
        // 标题卡
        using (GraphicsPath hp = Ui.Round(CardHeaderRect, Theme.Px(12)))
        {
            using (SolidBrush hb = new SolidBrush(Theme.Card)) g.FillPath(hb, hp);
            using (Pen pp = new Pen(Theme.Line, 1f)) g.DrawPath(pp, hp);
        }
        Rectangle ic = new Rectangle(CardHeaderRect.Left + Theme.Px(14), CardHeaderRect.Top + Theme.Px(14), Theme.Px(17), Theme.Px(17));
        using (GraphicsPath ip = Ui.Round(new Rectangle(ic.Left - Theme.Px(2), ic.Top - Theme.Px(2), ic.Width + Theme.Px(4), ic.Height + Theme.Px(4)), Theme.Px(5)))
        using (SolidBrush ib = new SolidBrush(Color.White)) g.FillPath(ib, ip);
        Icons.Draw(g, "pen", ic, Theme.FgMid, 1.2f);
        Font ft = _fontBold != null ? _fontBold : _fontNorm;
        TextRenderer.DrawText(g, CardTitle, ft,
            new Point(ic.Right + Theme.Px(10), CardHeaderRect.Top + Theme.Px(13)), Theme.Fg,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        // 右侧「查看变更 ↗」
        Font fl = _fontSmall != null ? _fontSmall : _fontNorm;
        // 「撤销」：把这一轮改的文件恢复原样
        if (!CardUndone)
        {
            int uw = Ui.TextWidth("撤销 ↺", fl);
            TextRenderer.DrawText(g, "撤销 ↺", fl,
                new Point(CardUndoRect.Right - uw, CardHeaderRect.Top + Theme.Px(19)), Theme.FgMid,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
        else
        {
            TextRenderer.DrawText(g, "已撤销", fl,
                new Point(CardUndoRect.Left, CardHeaderRect.Top + Theme.Px(19)), Theme.FgFaint,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
        // 「查看变更」做成圆角胶囊按钮（和 Codex 一样）
        using (GraphicsPath bp = Ui.Round(CardLinkRect, Theme.Px(13)))
        {
            using (SolidBrush bb = new SolidBrush(Color.White)) g.FillPath(bb, bp);
            using (Pen pp2 = new Pen(Theme.Line, 1f)) g.DrawPath(pp2, bp);
        }
        int lw = Ui.TextWidth("查看变更 ↗", fl);
        TextRenderer.DrawText(g, "查看变更 ↗", fl,
            new Point(CardLinkRect.Left + (CardLinkRect.Width - lw) / 2, CardHeaderRect.Top + Theme.Px(19)), Theme.Fg,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        // 文件列表卡
        if (CardRows.Count == 0) return;
        using (GraphicsPath lp = Ui.Round(CardListRect, Theme.Px(12)))
        {
            using (SolidBrush lb = new SolidBrush(Color.White)) g.FillPath(lb, lp);
            using (Pen pp = new Pen(Theme.Line, 1f)) g.DrawPath(pp, lp);
        }
        List<FileChange> info = Changes.Get(CardIndex);
        Font fm = _fontMono != null ? _fontMono : _fontNorm;
        for (int i = 0; i < CardRows.Count; i++)
        {
            Rectangle r = CardRows[i];
            if (i == CardHover)
                using (SolidBrush hb = new SolidBrush(Theme.Hover)) g.FillRectangle(hb, new Rectangle(r.Left + 1, r.Top, r.Width - 2, r.Height));
            string path = i < CardFiles.Count ? CardFiles[i] : "";
            TextRenderer.DrawText(g, path, fm, new Point(r.Left + Theme.Px(14), r.Top + Theme.Px(8)), Theme.FgMid,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis);
            if (info != null && i < info.Count)
            {
                Changes.EnsureDiff(info[i]);
                string plus = "+" + info[i].Added, minus = "−" + info[i].Removed;
                int mw = Ui.TextWidth(minus, fl), pw = Ui.TextWidth(plus, fl);
                TextRenderer.DrawText(g, minus, fl, new Point(r.Right - Theme.Px(14) - mw, r.Top + Theme.Px(11)), Theme.Err,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, plus, fl, new Point(r.Right - Theme.Px(14) - mw - Theme.Px(10) - pw, r.Top + Theme.Px(11)), Theme.Ok,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
            if (i < CardRows.Count - 1)
                using (Pen sp = new Pen(Theme.LineSoft, 1f)) g.DrawLine(sp, r.Left + Theme.Px(12), r.Bottom - 1, r.Right - Theme.Px(12), r.Bottom - 1);
        }
    }

    void DrawBubble(Graphics g, string text, Rectangle icon)
    {
        Font f = _fontSmall != null ? _fontSmall : _fontNorm;
        int tw = Ui.TextWidth(text, f);
        int bw = tw + Theme.Px(22);
        int bh = f.Height + Theme.Px(12);
        int bx = Math.Max(Theme.Px(2), icon.Left - Theme.Px(9));
        if (bx + bw > Width - Theme.Px(2)) bx = Math.Max(Theme.Px(2), Width - Theme.Px(2) - bw);
        int by = icon.Top - bh - Theme.Px(4);
        if (by < Theme.Px(2)) by = icon.Top + icon.Height + Theme.Px(3);      // 顶上没地方就放下面
        Rectangle br = new Rectangle(bx, by, bw, bh);
        using (GraphicsPath bp = Ui.Round(br, Math.Min(Theme.Px(9), bh / 2)))
        using (SolidBrush bb = new SolidBrush(Color.FromArgb(33, 35, 40))) g.FillPath(bb, bp);
        TextRenderer.DrawText(g, text, f, new Point(br.Left + Theme.Px(11), br.Top + Theme.Px(6)), Color.White,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    // 整条消息的高亮矩形：同一"视觉行"上的选中部分要合成连续一段
    // （否则粗体/行内代码处会留下空隙，看着就是"没完全覆盖字体"）
    internal List<Rectangle> HighlightRects()
    {
        List<Rectangle> all = new List<Rectangle>();
        if (!HasSel) return all;
        foreach (Piece p in _pieces)
        {
            if (p.Style == 6 || p.Text.Length == 0) continue;
            Font f = FontOf(p);
            int a = Math.Max(0, SelStart - p.SrcAt);
            int b = Math.Min(p.Text.Length, SelEnd - p.SrcAt);
            if (b <= a) continue;
            int x1 = p.Rect.Left + Measure(p.Text.Substring(0, a), f);
            int x2 = p.Rect.Left + Measure(p.Text.Substring(0, b), f);
            int top = p.Rect.Y + Theme.Px(1);
            int hh = f.Height + Theme.Px(1);
            bool merged = false;
            for (int i = all.Count - 1; i >= 0 && i >= all.Count - 4; i--)
            {
                Rectangle r = all[i];
                if (r.Top != top) continue;
                int l = Math.Min(r.Left, x1), rr = Math.Max(r.Right, x2);
                all[i] = new Rectangle(l, top, Math.Max(1, rr - l), Math.Max(r.Height, hh));
                merged = true;
                break;
            }
            if (!merged) all.Add(new Rectangle(x1, top, Math.Max(1, x2 - x1), hh));
        }
        return all;
    }

    void DrawActHover(Graphics g, Rectangle icon)
    {
        Rectangle r = new Rectangle(icon.Left - Theme.Px(4), icon.Top - Theme.Px(3),
            icon.Width + Theme.Px(8), icon.Height + Theme.Px(6));
        using (GraphicsPath hp = Ui.Round(r, Theme.Px(6)))
        using (SolidBrush hb = new SolidBrush(Theme.Hover)) g.FillPath(hb, hp);
    }
}

// 选中文字后浮出来的小操作条（像 Codex 那个"添加到对话"）：复制 / 填到输入框
class SelBar : Control
{
    public int Hover = -1;
    public int Paints = 0;      // 自测用
    public Action<int> OnAct;

    static readonly string[] Labels = new string[] { "复制", "填到输入框" };

    public SelBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Theme.Bg;
        Visible = false;
    }

    int BtnW(int i)
    {
        return Ui.TextWidth(Labels[i], Theme.F(9f)) + Theme.Px(20);
    }

    // 整条小操作条的高度：文字高度 + 上下各 6（以前写死 32，150% 下文字贴着上下边，看着"框太大/不贴合"）
    public static int PreferredH()
    {
        return Theme.F(9f).Height + Theme.Px(12);
    }

    public Rectangle Btn(int i)
    {
        int x = Theme.Px(5);
        if (i == 0) return new Rectangle(x, Theme.Px(5), BtnW(0), Height - Theme.Px(10));
        x += BtnW(0) + Theme.Px(4);
        return new Rectangle(x, Theme.Px(5), BtnW(1), Height - Theme.Px(10));
    }

    public static int PreferredW()
    {
        SelBar b = new SelBar();
        int w = b.BtnW(0) + b.BtnW(1) + Theme.Px(14);
        b.Dispose();
        return w;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Paints++;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(9)))
        using (SolidBrush b = new SolidBrush(Color.FromArgb(33, 35, 40))) g.FillPath(b, p);
        Font f = Theme.F(9f);
        for (int i = 0; i < Labels.Length; i++)
        {
            Rectangle r = Btn(i);
            if (Hover == i)
                using (GraphicsPath hp = Ui.Round(r, Theme.Px(6)))
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillPath(hb, hp);
            // 竖直居中按"按钮自己的高度"算，别用固定偏移 —— 换 DPI 就不会贴边
            TextRenderer.DrawText(g, Labels[i], f, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
        using (Pen sp = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
            g.DrawLine(sp, Btn(1).Left - Theme.Px(2), Theme.Px(8), Btn(1).Left - Theme.Px(2), Height - Theme.Px(8));
    }

    int Hit(Point pt)
    {
        for (int i = 0; i < Labels.Length; i++) if (Btn(i).Contains(pt)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Hit(e.Location);
        if (h != Hover) { Hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (Hover != -1) { Hover = -1; Cursor = Cursors.Default; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int h = Hit(e.Location);
        if (h >= 0 && OnAct != null) OnAct(h);
        base.OnMouseDown(e);
    }
}

// 可滚动的对话区：一堆消息块 + 双缓冲 + 自动跟随
class TranscriptView : Panel
{
    private readonly List<MsgBlock> _blocks = new List<MsgBlock>();
    private MsgBlock _last;
    private OutKind _lastKind;
    private bool _hasLast;
    private bool _follow = true;
    private Font _norm, _bold, _mono, _small;
    public int Repaints = 0;      // 自测用：整个视图重画次数
    private MsgBlock _hot;        // 右键点到的那一条
    private ContextMenuStrip _menu;
    private bool _autoFollow = true;    // 配置项 follow_output 的镜像（主窗体同步过来）
    private MsgBlock _selA, _selB;      // 拖选的两个端点
    private int _selAi, _selBi;
    private bool _dragging;
    private SelBar _bar;                // 选中后浮出来的"复制 / 填到输入框"小条
    public Action<int> OnBarAct;        // 0=复制 1=填到输入框
    public Action<int> OnCopied;        // 复制完回调（主窗体拿去显示"已复制 N 字"）
    public Action<MsgBlock, int> OnBlockAction;   // 点了块上的"复制/分支"按钮
    public Action<MsgBlock, int> OnCardAction;    // 点了"改动卡片"（-1=查看变更，>=0=第几个文件）

    internal void ChildCardAction(MsgBlock b, int idx)
    {
        if (OnCardAction != null) OnCardAction(b, idx);
    }

    internal SelBar Bar { get { return _bar; } }

    private MsgBlock _flashBlock;
    private System.Windows.Forms.Timer _flashTimer;

    // 点完按钮后在那一条上浮出深色气泡（"已复制"），1.1 秒后自动消失
    public void ShowFlash(MsgBlock b, string text, int act)
    {
        if (b == null) return;
        if (_flashBlock != null && _flashBlock != b) _flashBlock.SetFlash(null, -1);
        _flashBlock = b;
        b.SetFlash(text, act);
        if (_flashTimer == null)
        {
            _flashTimer = new System.Windows.Forms.Timer();
            _flashTimer.Interval = 1100;
            _flashTimer.Tick += delegate(object s2, EventArgs e2)
            {
                _flashTimer.Stop();
                if (_flashBlock != null) { _flashBlock.SetFlash(null, -1); _flashBlock = null; }
            };
        }
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    internal void ChildAction(MsgBlock b, int act)
    {
        if (OnBlockAction != null) OnBlockAction(b, act);
    }

    public bool AutoFollow
    {
        get { return _autoFollow; }
        set { _autoFollow = value; }
    }

    public TranscriptView()
    {
        AutoScroll = true;
        BackColor = Theme.Bg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        DoubleBuffered = true;
        TabStop = true;          // 要能拿到焦点，Ctrl+C 才收得到
        BuildMenu();
        _bar = new SelBar();
        _bar.OnAct = delegate(int i) { if (OnBarAct != null) OnBarAct(i); };
        Controls.Add(_bar);
    }

    // 让小操作条跟着选区走（拖动中不显示，松手才浮出来——和 Codex 一样）
    void UpdateBar()
    {
        if (_bar == null) return;
        if (_dragging || !HasSelection) { _bar.Visible = false; return; }
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue;
        foreach (MsgBlock b in _blocks)
        {
            if (!b.HasSel) continue;
            List<Rectangle> hl = b.HighlightRects();
            if (hl.Count == 0) continue;
            int bx = int.MaxValue, by = int.MaxValue, ex = int.MinValue;
            foreach (Rectangle r in hl)
            {
                if (r.Left < bx) bx = r.Left;
                if (r.Top < by) by = r.Top;
                if (r.Right > ex) ex = r.Right;
            }
            bx += b.Left; by += b.Top; ex += b.Left;
            if (bx < minX) minX = bx;
            if (by < minY) minY = by;
            if (ex > maxX) maxX = ex;
        }
        if (minX == int.MaxValue) { _bar.Visible = false; return; }
        int w = SelBar.PreferredW();
        int bh = SelBar.PreferredH();
        // 水平方向：贴着选区中间，看起来才像"挂在选中的字上"（以前是左对齐，常常偏出去一大截）
        int x = minX + (maxX - minX - w) / 2;
        x = Math.Max(Theme.Px(4), Math.Min(x, Math.Max(Theme.Px(4), ClientSize.Width - w - Theme.Px(4))));
        int y = minY - bh - Theme.Px(6);
        if (y < Theme.Px(4)) y = minY + Theme.Px(26);
        _bar.SetBounds(x, y, w, bh);
        _bar.Visible = y + bh <= ClientSize.Height;
        _bar.BringToFront();
    }

    // ---------- 选中与复制 ----------

    public bool HasSelection
    {
        get
        {
            foreach (MsgBlock b in _blocks) if (b.HasSel) return true;
            return false;
        }
    }

    public void SelectAll()
    {
        if (_blocks.Count == 0) return;
        _selA = _blocks[0]; _selAi = 0;
        _selB = _blocks[_blocks.Count - 1]; _selBi = _selB.Body.Length;
        ApplySel();
    }

    public void ClearSelection()
    {
        _selA = null; _selB = null;
        foreach (MsgBlock b in _blocks) b.ClearSel();
        UpdateBar();
    }

    // 把选中的文字拼起来（块之间补一个换行，免得两句话粘一起）
    public string BuildSelectionText()
    {
        StringBuilder sb = new StringBuilder();
        foreach (MsgBlock b in _blocks)
        {
            if (!b.HasSel) continue;
            string t = b.SelectedText;
            if (t.Length == 0) continue;
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
            sb.Append(t);
        }
        return sb.ToString();
    }

    public void CopySelection()
    {
        string t = BuildSelectionText();
        if (t.Length == 0) return;
        try { Clipboard.SetText(t); } catch { return; }
        if (OnCopied != null) OnCopied(t.Length);
    }

    void ApplySel()
    {
        int ia = _selA == null ? -1 : _blocks.IndexOf(_selA);
        int ib = _selB == null ? -1 : _blocks.IndexOf(_selB);
        if (ia < 0 || ib < 0) { foreach (MsgBlock b in _blocks) b.ClearSel(); return; }
        MsgBlock s = _selA, t = _selB;
        int si = _selAi, ti = _selBi;
        if (ia > ib || (ia == ib && _selAi > _selBi))
        {
            s = _selB; si = _selBi; t = _selA; ti = _selAi;
            ia = _blocks.IndexOf(s); ib = _blocks.IndexOf(t);
        }
        for (int k = 0; k < _blocks.Count; k++)
        {
            MsgBlock b = _blocks[k];
            int len = b.Body.Length;
            if (k < ia || k > ib) b.ClearSel();
            else if (ia == ib) b.SetSel(si, ti);
            else if (k == ia) b.SetSel(si, len);
            else if (k == ib) b.SetSel(0, ti);
            else b.SetSel(0, len);
        }
        UpdateBar();
    }

    Point LocalIn(MsgBlock b, Point pt)
    {
        return new Point(pt.X - b.Left, pt.Y - b.Top);
    }

    // 鼠标事件有可能落在消息块上（它是子控件），所以块会把事件转上来，统一在这里处理
    internal void ChildMouseDown(MsgBlock b, MouseEventArgs e) { MouseDownAt(b, new Point(e.X + b.Left, e.Y + b.Top), e); }
    internal void ChildMouseMove(MsgBlock b, MouseEventArgs e) { MouseMoveAt(new Point(e.X + b.Left, e.Y + b.Top), e); }
    internal void ChildMouseUp(MsgBlock b, MouseEventArgs e) { _dragging = false; UpdateBar(); }

    protected override void OnMouseDown(MouseEventArgs e) { MouseDownAt(HitBlock(e.Location), e.Location, e); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { MouseMoveAt(e.Location, e); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; UpdateBar(); base.OnMouseUp(e); }

    void MouseDownAt(MsgBlock b, Point pt, MouseEventArgs e)
    {
        _hot = b;
        if (e.Button != MouseButtons.Left) return;
        Focus();                                      // 拿到焦点才能收 Ctrl+C
        if (b == null) { ClearSelection(); return; }
        int idx = b.CharAt(LocalIn(b, pt));
        if (e.Clicks >= 2)
        {
            b.SelectWordAt(idx);
            _selA = b; _selB = b; _selAi = b.SelStart; _selBi = b.SelEnd;
        }
        else
        {
            _selA = b; _selAi = idx; _selB = b; _selBi = idx;
            ApplySel();
        }
        _dragging = true;
    }

    void MouseMoveAt(Point pt, MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left || _selA == null) return;
        if (_bar != null) _bar.Visible = false;        // 拖动中先藏起来
        MsgBlock b = HitBlock(pt);
        if (b == null) return;
        _selB = b;
        _selBi = b.CharAt(LocalIn(b, pt));
        ApplySel();
        // 拖到上下边缘时自动滚（长对话里选一大段）
        if (pt.Y < Theme.Px(22)) { try { AutoScrollPosition = new Point(0, -AutoScrollPosition.Y - Theme.Px(22)); } catch { } }
        else if (pt.Y > ClientSize.Height - Theme.Px(22)) { try { AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + Theme.Px(22)); } catch { } }
    }

    protected override bool IsInputKey(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C)) return true;
        if (keyData == (Keys.Control | Keys.A)) return true;
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C) { CopySelection(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { ClearSelection(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    public void SetFonts(Font norm, Font bold, Font mono, Font small)
    {
        _norm = norm; _bold = bold; _mono = mono; _small = small;
        foreach (MsgBlock b in _blocks) b.SetFonts(norm, bold, mono, small);
        Relayout();
    }

    // 右键菜单：没法像 RichTextBox 那样拖选文字，就给出"复制这条 / 复制全部"
    void BuildMenu()
    {
        _menu = new ContextMenuStrip();
        _menu.Renderer = new MenuSkin();
        _menu.Font = Theme.F(9.5f);
        _menu.ShowImageMargin = true;
        // 试过自己切圆角：WinForms 的下拉会强制自己的方角 Region，改不动。
        // 所以保留系统阴影（至少不像一块平板），里面用白底 + 圆角悬停 + 图标 + 快捷键做得干净些。
        _menu.DropShadowEnabled = true;
        _menu.Padding = new Padding(Theme.Px(4), Theme.Px(5), Theme.Px(4), Theme.Px(5));
        _menu.Opening += delegate(object s2, System.ComponentModel.CancelEventArgs e2)
        {
            if (_menu.Items.Count >= 1) _menu.Items[0].Enabled = HasSelection;
        };
        ToolStripMenuItem it;
        it = (ToolStripMenuItem)_menu.Items.Add("复制选中", MenuIcon("copy"), delegate(object s2, EventArgs e2) { CopySelection(); });
        it.ShortcutKeyDisplayString = "Ctrl+C";
        it = (ToolStripMenuItem)_menu.Items.Add("全选", MenuIcon("check"), delegate(object s2, EventArgs e2) { SelectAll(); });
        it.ShortcutKeyDisplayString = "Ctrl+A";
        it = (ToolStripMenuItem)_menu.Items.Add("复制全部对话", MenuIcon("chat"), delegate(object s2, EventArgs e2)
        {
            string t = AllText();
            if (t.Length > 0) { try { Clipboard.SetText(t); } catch { } if (OnCopied != null) OnCopied(t.Length); }
        });
        _menu.Items.Add(new ToolStripSeparator());
        it = (ToolStripMenuItem)_menu.Items.Add("复制这条消息", MenuIcon("pen"), delegate(object s2, EventArgs e2)
        {
            if (_hot == null) return;
            string t = _hot.Body;
            if (t.Length > 0) { try { Clipboard.SetText(t); } catch { } if (OnCopied != null) OnCopied(t.Length); }
        });
        foreach (ToolStripItem o in _menu.Items)
        {
            ToolStripMenuItem mi2 = o as ToolStripMenuItem;
            if (mi2 != null) mi2.Padding = new Padding(Theme.Px(4), Theme.Px(4), Theme.Px(10), Theme.Px(4));
        }
        _menu.Resize += delegate { SkinMenuCorners(); };
        _menu.Opened += delegate { SkinMenuCorners(); };      // 弹出来之后尺寸才定，这时再切一次圆角
        ContextMenuStrip = _menu;
        SkinMenuCorners();
    }

    // 菜单外框切成圆角（否则还是 WinForms 那个直角灰框）
    void SkinMenuCorners()
    {
        if (_menu == null || _menu.Width <= 1 || _menu.Height <= 1) return;
        try
        {
            using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, _menu.Width, _menu.Height), Theme.Px(10)))
                _menu.Region = new Region(p);
        }
        catch { }
    }

    // 菜单里的小图标（按菜单尺寸画一张透明位图）
    static Image MenuIcon(string name)
    {
        int s = Theme.Px(15);
        Bitmap bmp = new Bitmap(s, s);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Icons.Draw(g, name, new Rectangle(0, 0, s, s), Theme.FgMid, 1.25f);
        }
        return bmp;
    }

    public bool Follow
    {
        get { return _follow; }
        set { _follow = value; }
    }

    public int BlockCount { get { return _blocks.Count; } }

    // 有没有内容（含还没刷出去的那一批）
    public bool HasContent { get { return _blocks.Count > 0 || _pend.Count > 0; } }

    public void ClearAll()
    {
        if (_timer != null) _timer.Stop();
        _pend.Clear();
        _selA = null; _selB = null; _dragging = false;
        foreach (MsgBlock b in _blocks) { Controls.Remove(b); b.Dispose(); }
        _blocks.Clear();
        _last = null;
        _hasLast = false;
        _hot = null;
        _follow = true;                         // 清空 = 回到"跟着最新"的状态
        AutoScrollMinSize = new Size(0, 0);
        AutoScrollPosition = new Point(0, 0);   // 清空就得回到顶部，否则下一轮排版会带着旧偏移
        if (_bar != null) _bar.Visible = false;
        Invalidate();
    }

    public MsgBlock LastBlock { get { return _last; } }

    // ---------- 攒批：120ms 一刷。不然每个 token 都要把这一条消息重新排版一次 ----------
    class Pend
    {
        public OutKind Kind;
        public StringBuilder Text;
        public bool BreakOnly;
    }
    private readonly List<Pend> _pend = new List<Pend>();
    private System.Windows.Forms.Timer _timer;
    public int Flushes = 0;      // 自测用：真正写入的批次数

    public void AddChunk(OutKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Pend p = new Pend();
        p.Kind = kind;
        p.Text = new StringBuilder(text);
        _pend.Add(p);
        EnsureTimer();
    }

    // 换一条（下一次写入会新开一块）
    public void Break()
    {
        Pend p = new Pend();
        p.BreakOnly = true;
        _pend.Add(p);
        EnsureTimer();
    }

    void EnsureTimer()
    {
        if (_timer == null)
        {
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 120;
            _timer.Tick += delegate { FlushNow(); };
        }
        if (!_timer.Enabled) _timer.Start();
    }

    // 把攒着的文字一次写进视图（测试和"立即显示"都可以直接调）
    public void FlushNow()
    {
        if (_timer != null) _timer.Stop();
        if (_pend.Count == 0) return;
        Flushes++;
        List<Pend> items = new List<Pend>(_pend);
        _pend.Clear();
        // 同一批里同一种消息先合并成一段再写：800 个 token 只排版一次，不是 800 次
        List<Pend> merged = new List<Pend>();
        foreach (Pend p in items)
        {
            Pend m = merged.Count > 0 ? merged[merged.Count - 1] : null;
            if (m != null && !m.BreakOnly && !p.BreakOnly && m.Kind == p.Kind)
                m.Text.Append(p.Text);
            else
                merged.Add(p);
        }
        bool follow = _follow && _autoFollow;
        bool touched = false;
        foreach (Pend p in merged)
        {
            if (p.BreakOnly) { _last = null; _hasLast = false; continue; }
            WriteNow(p.Kind, p.Text.ToString());
            touched = true;
        }
        if (!touched) return;
        Relayout();
        if (follow) ScrollToEnd();
    }

    // 追加一段文字：同一种 kind 就接到最后一块上
    void WriteNow(OutKind kind, string text)
    {
        if (!_hasLast || _lastKind != kind)
        {
            // 新起一条消息：开头的空行没必要（块与块之间本来就有间距）
            text = text.TrimStart('\n', '\r');
            if (text.Length == 0) return;
            _last = new MsgBlock(kind, _norm, _bold, _mono, _small);
            _last.Width = BodyWidth;
            _last.Stamp = DateTime.Now;
            _last.ShowCopy = (kind == OutKind.Assistant || kind == OutKind.User);
            _last.ShowBranch = (kind == OutKind.Assistant);
            MsgBlock nb = _last;
            nb.MouseDown += delegate(object s2, MouseEventArgs me) { _hot = nb; };
            nb.ContextMenuStrip = _menu;
            _blocks.Add(_last);
            Controls.Add(_last);
            _lastKind = kind;
            _hasLast = true;
            CapBlocks();
        }
        _last.Append(text);
    }

    void CapBlocks()
    {
        while (_blocks.Count > 400)
        {
            MsgBlock b = _blocks[0];
            _blocks.RemoveAt(0);
            Controls.Remove(b);
            b.Dispose();
        }
    }

    int BodyWidth
    {
        get
        {
            // 不管滚动条在不在，正文宽度都保持一致：
            // 否则"内容长到出滚动条"的那一瞬间客户区变窄，所有老消息会跟着重新折行重画一次
            int w = ClientSize.Width + (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0)
                - SystemInformation.VerticalScrollBarWidth - Theme.Px(8);
            return Math.Max(Theme.Px(160), w);
        }
    }

    void Relayout()
    {
        // 先把滚动位置夹回合法范围：窗口变大 / 内容变短时，面板会自己把偏移夹住（子控件也跟着挪），
        // 这里补一次，保证下面用的 off 和真实的滚动位置一致，不会又把内容推偏。
        int maxPrev = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
        if (-AutoScrollPosition.Y > maxPrev) AutoScrollPosition = new Point(0, maxPrev);

        int w = BodyWidth;
        int y = 0;
        // 关键：AutoScroll 面板里，子控件的 Top 是"客户区坐标"，而 y 是"内容坐标"。
        // 不补上这个滚动偏移，面板滚一段就把所有消息块整体推下去一段，
        // 面板又会把"子控件的范围"算进可滚动区 —— 越滚越大，内容就被顶到中间去了。
        int off = AutoScrollPosition.Y;      // 已经往下滚了多少（负数）
        bool first = true;
        foreach (MsgBlock b in _blocks)
        {
            if (b.Width != w) b.Width = w;
            b.Left = 0;
            if (!first) y += GapBefore(b);
            first = false;
            b.Top = y + off;
            y += b.Height;
        }
        AutoScrollMinSize = new Size(0, y + Theme.Px(8));
    }

    // 块前面留多少空隙。同一轮任务内部紧凑（9px），
    // 轮到"用户又提了新问题"就直接拉开一大截 —— 这样一眼就能看出哪几段是同一轮干的活。
    int GapBefore(MsgBlock b)
    {
        return b.Kind == OutKind.User ? Theme.Px(26) : Theme.Px(6);
    }

    public void ScrollToEnd()
    {
        try
        {
            if (AutoScrollMinSize.Height <= ClientSize.Height) return;
            AutoScrollPosition = new Point(0, AutoScrollMinSize.Height);
        }
        catch { }
    }

    // 视野是不是已经在最底下
    public bool AtEnd
    {
        get
        {
            int viewH = ClientSize.Height;
            if (AutoScrollMinSize.Height <= viewH) return true;
            return (-AutoScrollPosition.Y) >= AutoScrollMinSize.Height - viewH - Theme.Px(28);
        }
    }

    // 滚轮：自己滚（不依赖系统往父窗口冒泡），并且"滚上去就停止自动跟随，滚回来再跟上"
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!AutoScroll) { base.OnMouseWheel(e); return; }
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines < 1) lines = 3;
        int step = Theme.Px(18) * lines;
        int max = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
        int target = (-AutoScrollPosition.Y) - (e.Delta * step / 120);
        if (target < 0) target = 0;
        if (target > max) target = max;
        AutoScrollPosition = new Point(0, target);
        if (_autoFollow) _follow = AtEnd;
        UpdateBar();          // 滚动时选区跟着走，小条也要跟着挪
    }

    // 找鼠标点在哪一条消息上（消息块的 Bounds 就是客户区坐标，滚过之后框架会跟着挪，直接比就行）
    MsgBlock HitBlock(Point pt)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--)
        {
            Rectangle r = _blocks[i].Bounds;
            if (r.Contains(pt))
                return _blocks[i];
        }
        return null;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
        UpdateBar();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Repaints++;
        using (SolidBrush bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, ClientRectangle);
    }

    // 自测用：所有消息块加起来被重画了几次（流式输出时应该只有最后一条在动）
    public int BlockPaints()
    {
        int n = 0;
        foreach (MsgBlock b in _blocks) n += b.Paints;
        return n;
    }

    public int LastPaints() { return _last == null ? 0 : _last.Paints; }

    // 全部文字（给"复制全部对话"用）
    public string AllText()
    {
        StringBuilder sb = new StringBuilder();
        foreach (MsgBlock b in _blocks) sb.Append(b.Body);
        return sb.ToString();
    }
}

// 右键菜单的皮肤：白底、细边、悬停圆角高亮，别用 WinForms 那个灰扑扑的默认样式
class MenuSkin : ToolStripProfessionalRenderer
{
    class Skin : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color ToolStripDropDownBackground { get { return Color.White; } }
        public override Color ImageMarginGradientBegin { get { return Color.White; } }
        public override Color ImageMarginGradientMiddle { get { return Color.White; } }
        public override Color ImageMarginGradientEnd { get { return Color.White; } }
        public override Color SeparatorDark { get { return Theme.LineSoft; } }
        public override Color SeparatorLight { get { return Theme.LineSoft; } }
    }

    public MenuSkin() : base(new Skin()) { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (SolidBrush b = new SolidBrush(Color.White)) e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using (Pen p = new Pen(Theme.Line, 1f)) e.Graphics.DrawRectangle(p, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using (SolidBrush b = new SolidBrush(Color.White)) e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        Rectangle r = new Rectangle(Theme.Px(4), 1, e.Item.Width - Theme.Px(8), e.Item.Height - 2);
        using (GraphicsPath p = Ui.Round(r, Theme.Px(6)))
        using (SolidBrush b = new SolidBrush(Theme.Hover)) e.Graphics.FillPath(b, p);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Fg : Theme.FgFaint;
        e.TextFont = Theme.F(9.5f);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using (Pen p = new Pen(Theme.LineSoft, 1f))
            e.Graphics.DrawLine(p, Theme.Px(10), e.Item.Height / 2, e.Item.Width - Theme.Px(10), e.Item.Height / 2);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.FgMid;
        base.OnRenderArrow(e);
    }
}

// 「查看变更」窗口：左边选文件，右边看带颜色的 diff
class ChangesForm : Form
{
    private ListBox _list;
    private RichTextBox _diff;
    private List<FileChange> _files;

    public ChangesForm(List<FileChange> files, int start)
    {
        _files = files;
        Theme.EnsureDpi();
        AutoScaleMode = AutoScaleMode.None;
        Text = "本轮的改动（" + files.Count + " 个文件）";
        Icon = Theme.AppIcon();
        Size = new Size(Theme.Px(900), Theme.Px(580));
        MinimumSize = new Size(Theme.Px(680), Theme.Px(420));
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        ShowInTaskbar = false;

        Panel head = new Panel();
        head.Dock = DockStyle.Top;
        head.Height = Theme.Px(46);
        head.BackColor = Theme.Bg;
        Label t = new Label();
        t.Text = "这一轮改了 " + files.Count + " 个文件";
        t.Font = Theme.FB(11f);
        t.ForeColor = Theme.Fg;
        t.AutoSize = true;
        t.Location = new Point(Theme.Px(20), Theme.Px(14));
        head.Controls.Add(t);
        Controls.Add(head);

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = Theme.Px(58);
        bottom.BackColor = Theme.Bg;
        UiButton close = new UiButton();
        close.Text = "关闭";
        close.FillColor = Theme.Accent;
        close.HoverColor = Theme.AccentHover;
        close.TextColor = Color.White;
        close.Radius = 9;
        close.Font = Theme.F(9.5f);
        close.Size = new Size(Theme.Px(88), Theme.Px(34));
        close.Click += delegate { Close(); };
        bottom.Controls.Add(close);
        UiButton copy = new UiButton();
        copy.Text = "复制这个 diff";
        copy.FillColor = Color.White;
        copy.BorderColor = Theme.Line;
        copy.HoverColor = Theme.Hover;
        copy.Radius = 9;
        copy.Font = Theme.F(9.5f);
        copy.Size = new Size(Theme.Px(126), Theme.Px(34));
        copy.Click += delegate
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            Changes.EnsureDiff(_files[i]);
            try { Clipboard.SetText(_files[i].DiffText); } catch { }
        };
        bottom.Controls.Add(copy);
        bottom.Layout += delegate
        {
            copy.Location = new Point(bottom.ClientSize.Width - Theme.Px(20) - copy.Width - Theme.Px(10) - close.Width, Theme.Px(12));
            close.Location = new Point(bottom.ClientSize.Width - Theme.Px(20) - close.Width, Theme.Px(12));
        };
        Controls.Add(bottom);

        Panel body = new Panel();
        body.Dock = DockStyle.Fill;
        body.BackColor = Theme.Bg;
        Controls.Add(body);
        body.BringToFront();

        _list = new ListBox();
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Color.White;
        _list.ForeColor = Theme.Fg;
        _list.Font = Theme.F(9.5f);
        _list.IntegralHeight = false;
        _list.SelectedIndexChanged += delegate { ShowDiff(_list.SelectedIndex); };
        body.Controls.Add(_list);

        _diff = new RichTextBox();
        _diff.ReadOnly = true;
        _diff.WordWrap = false;
        _diff.BorderStyle = BorderStyle.None;
        _diff.BackColor = Color.White;
        _diff.ForeColor = Theme.Fg;
        _diff.Font = Theme.M(9.5f);
        _diff.ScrollBars = RichTextBoxScrollBars.Both;
        body.Controls.Add(_diff);
        body.Resize += delegate { LayoutBody(); };

        for (int i = 0; i < files.Count; i++)
        {
            Changes.EnsureDiff(files[i]);
            _list.Items.Add(files[i].Rel + "   +" + files[i].Added + " −" + files[i].Removed);
        }
        _list.SelectedIndex = (start >= 0 && start < files.Count) ? start : 0;
        LayoutBody();
        ActiveControl = _list;
    }

    // 左右两栏按容器实际尺寸算：左边列表占 32%（有上下限），剩下全给 diff。
    // 注意：这里尺寸都已经用 Theme.Px 换过算了，构造函数末尾**绝不能再调 ScaleBounds** ——
    // 那个函数会递归再乘一次 Dpi，1.5×1.5=2.25 之后 diff 面板会被顶出窗口右边、文字被切
    // （2026-10-13 用户截图里"查看变更有 bug"就是这么来的）。
    void LayoutBody()
    {
        if (_list == null || _diff == null || _list.Parent == null) return;
        Panel b = (Panel)_list.Parent;
        int pad = Theme.Px(16), gap = Theme.Px(14);
        int h = Math.Max(Theme.Px(80), b.ClientSize.Height - pad * 2);
        int listW = Math.Max(Theme.Px(180), (int)Math.Round(b.ClientSize.Width * 0.32));
        int maxListW = b.ClientSize.Width - pad * 2 - gap - Theme.Px(240);
        if (listW > maxListW) listW = Math.Max(Theme.Px(120), maxListW);
        _list.SetBounds(pad, pad, listW, h);
        int dx = pad + listW + gap;
        _diff.SetBounds(dx, pad, Math.Max(Theme.Px(160), b.ClientSize.Width - dx - pad), h);
    }

    void ShowDiff(int i)
    {
        if (i < 0 || i >= _files.Count) return;
        FileChange c = _files[i];
        Changes.EnsureDiff(c);
        _diff.Clear();
        AppendLine(c.Rel + "   +" + c.Added + " −" + c.Removed + "   （下面 + 绿 = 新增，− 红 = 删除）",
            Theme.Fg, true, Theme.LineSoft);
        AppendLine("", Theme.Fg, false, Color.White);
        string[] lines = c.DiffText.Replace("\r\n", "\n").Split('\n');
        for (int k = 0; k < lines.Length; k++)
        {
            string ln = lines[k];
            Color col = Theme.FgMid, bg = Color.White;
            if (ln.StartsWith("+ ")) { col = Theme.Ok; bg = Theme.OkSoft; }
            else if (ln.StartsWith("- ")) { col = Theme.Err; bg = Theme.ErrSoft; }
            AppendLine(ln, col, false, bg);
        }
        _diff.SelectionStart = 0;
        _diff.ScrollToCaret();
    }

    void AppendLine(string s, Color c, bool bold, Color bg)
    {
        _diff.SelectionStart = _diff.TextLength;
        _diff.SelectionLength = 0;
        _diff.SelectionColor = c;
        _diff.SelectionBackColor = bg;
        _diff.SelectionFont = bold ? Theme.MB(9.8f) : Theme.M(9.5f);
        _diff.AppendText(s + "\n");
    }
}

class HintTextBox : TextBox
{
    public string Hint = "";

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x000F && TextLength == 0 && Hint != null && Hint.Length > 0)   // WM_PAINT
        {
            using (Graphics g = CreateGraphics())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                TextRenderer.DrawText(g, Hint, Font, new Point(1, 1), Theme.FgFaint,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }
}

// 圆角输入框：外面是画的圆角边框，里面塞一个无边框文本框
class FieldBox : RoundedPanel
{
    public Control Inner;
    private bool _multi;

    public FieldBox(bool multiline)
    {
        _multi = multiline;
        Radius = 10;
        Fill = Color.White;
        BorderColor = Theme.Line;
        BackColor = Theme.Bg;
        Padding = new Padding(11, 2, 11, 2);
        if (multiline)
        {
            TextBox rt = new TextBox();
            rt.BorderStyle = BorderStyle.None;
            rt.BackColor = Color.White;
            rt.ForeColor = Theme.Fg;
            rt.Multiline = true;
            rt.AcceptsReturn = true;
            rt.Font = Theme.F(9.5f);
            rt.ScrollBars = ScrollBars.Vertical;
            rt.WordWrap = false;
            Inner = rt;
        }
        else
        {
            TextBox t = new TextBox();
            t.BorderStyle = BorderStyle.None;
            t.BackColor = Color.White;
            t.ForeColor = Theme.Fg;
            t.Font = Theme.F(9.5f);
            Inner = t;
        }
        Controls.Add(Inner);
        Inner.GotFocus += delegate { BorderColor = Theme.Accent; Invalidate(); };
        Inner.LostFocus += delegate { BorderColor = Theme.Line; Invalidate(); };
    }

    bool _placing;

    // 让外部（比如 DPI 放大完之后）能主动重摆一次内部控件
    public void Reposition() { Place(); }

    void Place()
    {
        if (_placing) return;           // Place() 会触发子控件布局，加个闸门免得自己递归自己
        _placing = true;
        try { PlaceCore(); }
        finally { _placing = false; }
    }

    void PlaceCore()
    {
        TextBox tb = Inner as TextBox;
        int pl = Theme.Px(Padding.Left), pr = Theme.Px(Padding.Right);
        int pt = Theme.Px(Padding.Top), pb = Theme.Px(Padding.Bottom);
        int h = _multi
            ? Math.Max(10, Height - pt - pb)
            : (tb != null ? tb.PreferredHeight : Math.Max(18, Inner.Height));
        Inner.SetBounds(pl, Math.Max(0, (Height - h) / 2 - 1), Math.Max(10, Width - pl - pr), h);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Inner != null) Place();     // 布局一变就跟着重摆，避免"外框变了、里面还是老位置"
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Place();
    }

}

static class Native
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    const int SM_CXSIZEFRAME = 32;
    const int SM_CXPADDEDBORDER = 92;

    // 不可见窗口边框的厚度（最大化时要把客户区缩回来这么多）
    public static int FrameThickness
    {
        get { return GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER); }
    }

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // 把消息原样转给别的窗口（消息块把滚轮转给滚动面板要用）
    public static void SendMsg(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try { if (hWnd != IntPtr.Zero) SendMessage(hWnd, msg, wParam, lParam); }
        catch { }
    }
    // Windows 11：圆角窗口 + 白色标题栏（否则系统强调色会给标题栏染上一层偏绿的渐变，很脏）
    public static void RoundWindow(IntPtr hwnd)
    {
        try
        {
            int pref = 2;      // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            int lightTitle = 0;               // 不用暗色标题栏
            DwmSetWindowAttribute(hwnd, 20, ref lightTitle, sizeof(int));
            int caption = 0x00FFFFFF;         // COLORREF(BGR) 白色
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
            int captionText = 0x0015110F;
            DwmSetWindowAttribute(hwnd, 36, ref captionText, sizeof(int));
        }
        catch { }
    }
}

// ============================ 审批对话框（GUI 用） ============================

class ApprovalForm : Form
{
    public int Decision = 0;

    public ApprovalForm(string toolName, string[] preview)
        : this(toolName, preview, false) { }

    // safeDefault = true：默认按钮变成「拒绝」（越权访问那种要人明确点头的场景用）
    public ApprovalForm(string toolName, string[] preview, bool safeDefault)
    {
        Text = "需要确认";
        Icon = Theme.AppIcon();
        Size = new Size(760, 520);
        MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        Panel head = new Panel();
        head.Dock = DockStyle.Top;
        head.Height = 78;
        head.BackColor = Theme.Bg;

        LogoMark mark = new LogoMark();
        mark.BackColor = Theme.Bg;
        mark.SetBounds(24, 24, 26, 26);
        head.Controls.Add(mark);

        Label title = new Label();
        title.Text = "即将执行 · " + toolName;
        title.Font = Theme.FB(13f);
        title.ForeColor = Theme.Fg;
        title.AutoSize = true;
        title.Location = new Point(60, 22);
        head.Controls.Add(title);

        Label sub = new Label();
        sub.Text = "这个操作会改动你的文件或执行命令，确认后才会继续。";
        sub.ForeColor = Theme.FgDim;
        sub.AutoSize = true;
        sub.Location = new Point(62, 48);
        head.Controls.Add(sub);

        RoundedPanel box = new RoundedPanel();
        box.Dock = DockStyle.Fill;
        box.Radius = 12;
        box.Fill = Theme.Card;
        box.BorderColor = Theme.Line;
        box.BackColor = Theme.Bg;
        box.Padding = new Padding(14, 12, 14, 12);

        RichTextBox body = new RichTextBox();
        body.Dock = DockStyle.Fill;
        body.BorderStyle = BorderStyle.None;
        body.ReadOnly = true;
        body.WordWrap = false;
        body.ScrollBars = RichTextBoxScrollBars.Both;
        body.BackColor = Theme.Card;
        body.ForeColor = Theme.Fg;
        body.Font = Theme.M(10f);
        body.Text = string.Join("\n", preview);
        box.Controls.Add(body);

        Panel bodyWrap = new Panel();
        bodyWrap.Dock = DockStyle.Fill;
        bodyWrap.BackColor = Theme.Bg;
        bodyWrap.Padding = new Padding(20, 0, 20, 0);
        bodyWrap.Controls.Add(box);

        Panel buttons = new Panel();
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = 66;
        buttons.BackColor = Theme.Bg;

        UiButton yes = new UiButton();
        yes.Text = "允许";
        yes.FillColor = Theme.Accent;
        yes.HoverColor = Theme.AccentHover;
        yes.TextColor = Color.White;
        yes.Radius = 10;
        yes.Font = Theme.F(9.5f);
        yes.Size = new Size(104, 36);
        yes.Click += delegate { Decision = 1; Close(); };

        UiButton always = new UiButton();
        always.Text = "本会话总是允许";
        always.Radius = 10;
        always.BorderColor = Theme.Line;
        always.FillColor = Color.White;
        always.HoverColor = Theme.Hover;
        always.Font = Theme.F(9.5f);
        always.Size = new Size(146, 36);
        always.Click += delegate { Decision = 2; Close(); };

        UiButton no = new UiButton();
        no.Text = "拒绝";
        no.Radius = 10;
        no.BorderColor = Theme.Line;
        no.FillColor = Color.White;
        no.HoverColor = Theme.Hover;
        no.Font = Theme.F(9.5f);
        no.Size = new Size(88, 36);
        no.Click += delegate { Decision = 0; Close(); };

        buttons.Controls.Add(always);
        buttons.Controls.Add(yes);
        buttons.Controls.Add(no);
        buttons.Layout += delegate
        {
            int right = buttons.ClientSize.Width - 20;
            no.Location = new Point(right - no.Width, 14);
            yes.Location = new Point(no.Left - 10 - yes.Width, 14);
            always.Location = new Point(yes.Left - 10 - always.Width, 14);
        };

        Label hint = new Label();
        hint.Text = safeDefault ? "Enter 拒绝 · 要放行请点「允许」或「本会话总是允许」" : "Enter 允许 · Esc 拒绝";
        hint.ForeColor = Theme.FgFaint;
        hint.AutoSize = true;
        hint.Location = new Point(24, 24);
        buttons.Controls.Add(hint);

        Controls.Add(bodyWrap);
        Controls.Add(buttons);
        Controls.Add(head);

        AcceptButton = safeDefault ? no : yes;
        KeyPreview = true;
        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Decision = 0; Close(); }
        };
        if (Theme.Dpi > 1.01f && Size.Width < Theme.Px(760))
        {
            Theme.ScaleBounds(this, Theme.Dpi);
            Size = new Size(Theme.Px(760), Theme.Px(520));
            MinimumSize = new Size(Theme.Px(600), Theme.Px(400));
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundWindow(Handle);
    }
}

// ============================ 主窗口 ============================

// ============================ 自我迭代「改造申请单」（GUI 用） ============================
// 智能体发现自己能力不够时，用它把方案摆到用户面前：改造内容 / 影响范围 / 风险 / 预期效果 / 怎么验证。
// 用户点「批准执行」或「驳回」；批准后框架要求它按固定四步走（改代码 → build → 自测 → 升级重启），
// 任何一步失败就回滚（源码走改动前快照，二进制走上一版 exe）。
class ProposalForm : Form
{
    public int Decision = 0;      // 1 = 批准执行 / 0 = 驳回

    public ProposalForm(string title, string body)
    {
        Theme.EnsureDpi();
        AutoScaleMode = AutoScaleMode.None;
        Text = "自我迭代改造申请";
        Icon = Theme.AppIcon();
        Size = new Size(Theme.Px(860), Theme.Px(640));
        MinimumSize = new Size(Theme.Px(660), Theme.Px(480));
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        Panel head = new Panel();
        head.Dock = DockStyle.Top;
        head.Height = Theme.Px(78);
        head.BackColor = Theme.Bg;
        Label t = new Label();
        t.Text = "智能体申请改造自己";
        t.Font = Theme.FB(13.5f);
        t.ForeColor = Theme.Fg;
        t.AutoSize = true;
        t.Location = new Point(Theme.Px(22), Theme.Px(16));
        head.Controls.Add(t);
        Label sub = new Label();
        sub.Text = title;
        sub.ForeColor = Theme.FgMid;
        sub.AutoSize = true;
        sub.Location = new Point(Theme.Px(24), Theme.Px(46));
        head.Controls.Add(sub);
        Controls.Add(head);

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = Theme.Px(64);
        bottom.BackColor = Theme.Bg;
        UiButton ok = new UiButton();
        ok.Text = "批准执行";
        ok.FillColor = Theme.Accent;
        ok.HoverColor = Theme.AccentHover;
        ok.TextColor = Color.White;
        ok.Radius = 9;
        ok.Font = Theme.F(9.5f);
        ok.Size = new Size(Theme.Px(118), Theme.Px(36));
        ok.Click += delegate { Decision = 1; Close(); };
        bottom.Controls.Add(ok);
        UiButton no = new UiButton();
        no.Text = "驳回";
        no.FillColor = Color.White;
        no.BorderColor = Theme.Line;
        no.HoverColor = Theme.Hover;
        no.Radius = 9;
        no.Font = Theme.F(9.5f);
        no.Size = new Size(Theme.Px(88), Theme.Px(36));
        no.Click += delegate { Decision = 0; Close(); };
        bottom.Controls.Add(no);
        Label hint = new Label();
        hint.Text = "Enter 驳回 · 批准后：① 改代码 ② build ③ 跑自测 ④ 自动升级重启；任一步失败自动回滚";
        hint.ForeColor = Theme.FgFaint;
        hint.AutoSize = true;
        hint.Location = new Point(Theme.Px(22), Theme.Px(24));
        bottom.Controls.Add(hint);
        bottom.Layout += delegate
        {
            no.Location = new Point(bottom.ClientSize.Width - Theme.Px(20) - no.Width, Theme.Px(14));
            ok.Location = new Point(no.Left - Theme.Px(10) - ok.Width, Theme.Px(14));
        };
        Controls.Add(bottom);

        Panel wrap = new Panel();
        wrap.Dock = DockStyle.Fill;
        wrap.BackColor = Theme.Bg;
        TextBox tb = new TextBox();
        tb.Multiline = true;
        tb.ReadOnly = true;
        tb.ScrollBars = ScrollBars.Vertical;
        tb.WordWrap = true;
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.BackColor = Color.White;
        tb.ForeColor = Theme.Fg;
        tb.Font = Theme.F(9.5f);
        tb.Text = body;
        wrap.Controls.Add(tb);
        wrap.Layout += delegate
        {
            tb.SetBounds(Theme.Px(22), Theme.Px(6),
                Math.Max(Theme.Px(160), wrap.ClientSize.Width - Theme.Px(44)),
                Math.Max(Theme.Px(120), wrap.ClientSize.Height - Theme.Px(20)));
        };
        Controls.Add(wrap);
        wrap.BringToFront();

        AcceptButton = no;                       // 默认按钮是「驳回」：按 Enter 不会批准自我改造
        KeyPreview = true;
        KeyDown += delegate(object s2, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { Decision = 0; Close(); } };
    }

    // 申请单正文（统一格式，五个必填段落）
    public static string FormatText(Iteration it)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("【改造内容】\n").Append(it.change).Append("\n\n");
        sb.Append("【影响范围】\n").Append(it.scope).Append("\n\n");
        sb.Append("【潜在风险】\n").Append(it.risk).Append("\n\n");
        sb.Append("【预期效果】\n").Append(it.expect).Append("\n\n");
        sb.Append("【怎么验证】\n").Append(it.verify).Append("\n\n");
        sb.Append("【安全兜底】\n改动前的文件快照会留在 .snapshots\\，升级前的 exe 会留成 .prev；");
        sb.Append("验证不过或新版起不来，自动回滚到升级前的状态。全过程记进 devtools\\iterations.json 与日志。\n");
        return sb.ToString();
    }
}

class Session
{
    public string id { get; set; }
    public string title { get; set; }
    public string cwd { get; set; }
    public string model { get; set; }
    public string provider { get; set; }
    public string updated { get; set; }
    public List<Dictionary<string, object>> messages { get; set; }
}

class SessionStore
{
    public List<Session> sessions { get; set; }

    public SessionStore() { sessions = new List<Session>(); }

    public static string FilePath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sessions.json"); }
    }

    public static SessionStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                SessionStore s = Json.Ser.Deserialize<SessionStore>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (s != null && s.sessions != null) return s;
            }
        }
        catch { }
        return new SessionStore();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, Json.Ser.Serialize(this), new UTF8Encoding(false)); }
        catch { }
    }
}
// ---------- 统一错误日志：出问题时有据可查（以前 50 处 catch{} 把异常吞了，什么都看不见）----------
static class Log
{
    static readonly object _lock = new object();

    public static string FilePath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ahz错误日志.txt"); }
    }

    public static void Crash(Exception ex, string where)
    {
        if (ex == null) return;
        try
        {
            lock (_lock)
            {
                File.AppendAllText(FilePath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + where + " → "
                    + ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace + "\r\n\r\n",
                    Encoding.UTF8);
            }
        }
        catch { }
    }

    // 用于替换 catch { }：吞掉异常可以，但必须留痕
    public static void Swallow(Exception ex, string where) { Crash(ex, "已忽略 · " + where); }

    // 读取日志内容（设置里的"运行日志"页用；界面本身在 SettingsForm.BuildLogPage 里建）
    public static string Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return "（暂无日志：没有审批记录、没有异常）";
            string s = File.ReadAllText(FilePath, Encoding.UTF8);
            return s.Length > 0 ? s : "（日志为空）";
        }
        catch (Exception ex) { return "（读取日志失败：" + ex.Message + "）"; }
    }

    // 敏感操作留痕：审批决定、远程执行、升权等，供事后溯源（设置里可查看）
    public static void Audit(string text)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(FilePath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text + "\r\n",
                    Encoding.UTF8);
            }
        }
        catch { }
    }
}

// ---------- 启动自检：是不是被低完整性启动的？临时目录还能写吗？----------
// 背景（2026-10-12 真机踩到）：Ahz Scribe 若是从别的沙箱窗口里被拉起来的，整个进程连同
// 它调用的 csc 都继承成 Low 完整性级别，写 TEMP / 写 .exe 一律"拒绝访问"，
// 现象特别像"API Key 不对"。本程序没有任何降权代码 —— 令牌是继承来的，
// 所以只能在启动时自己看一眼、把话说清楚，并顺手把 TMP/TEMP 兜到能写的地方。
static class StartupCheck
{
    public static string Warning = "";

    const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    struct SidAndAttributes { public IntPtr Sid; public int Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    struct TokenMandatoryLabel { public SidAndAttributes Label; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr tokenHandle, int cls, IntPtr info, int len, out int ret);
    [DllImport("advapi32.dll")]
    static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);
    [DllImport("advapi32.dll")]
    static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    // 当前进程令牌的完整性级别 RID（0x1000 低 / 0x2000 中 / 0x3000 高）；取不到返回 -1
    public static int IntegrityRid()
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            IntPtr token = System.Security.Principal.WindowsIdentity.GetCurrent().Token;
            if (token == IntPtr.Zero) return -1;
            int len;
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out len);
            if (len <= 0) return -1;
            buf = Marshal.AllocHGlobal(len);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out len)) return -1;
            TokenMandatoryLabel tml = (TokenMandatoryLabel)Marshal.PtrToStructure(buf, typeof(TokenMandatoryLabel));
            if (tml.Label.Sid == IntPtr.Zero) return -1;
            IntPtr pCount = GetSidSubAuthorityCount(tml.Label.Sid);
            if (pCount == IntPtr.Zero) return -1;
            int count = Marshal.ReadByte(pCount);
            if (count <= 0) return -1;
            IntPtr pRid = GetSidSubAuthority(tml.Label.Sid, count - 1);
            if (pRid == IntPtr.Zero) return -1;
            return Marshal.ReadInt32(pRid);
        }
        catch { return -1; }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    public static string IntegrityName()
    {
        int rid = IntegrityRid();
        if (rid < 0) return "未知";
        if (rid < 0x1000) return "不受信任";
        if (rid < 0x2000) return "低";
        if (rid < 0x3000) return "中";
        if (rid < 0x4000) return "高";
        if (rid < 0x5000) return "系统";
        return "受保护";
    }

    // 真写一个文件再删掉：比看 ACL 靠谱（ACL 允许也可能被别的策略拦住）
    public static bool TryWrite(string dir)
    {
        try
        {
            if (dir == null || dir.Trim().Length == 0 || !Directory.Exists(dir)) return false;
            string f = Path.Combine(dir, "ahz-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            File.WriteAllText(f, "probe", Encoding.UTF8);
            File.Delete(f);
            return true;
        }
        catch { return false; }
    }

    // 找一个真能写的临时目录：先 exe 目录下 _build\tmp，再工作目录下同名目录；都不行返回 null
    public static string PickTemp(string exeDir, string cwd)
    {
        List<string> cands = new List<string>();
        if (exeDir != null && exeDir.Trim().Length > 0) cands.Add(Path.Combine(exeDir, "_build", "tmp"));
        if (cwd != null && cwd.Trim().Length > 0) cands.Add(Path.Combine(cwd, "_build", "tmp"));
        foreach (string d in cands)
        {
            try { Directory.CreateDirectory(d); } catch { }
            if (TryWrite(d)) return d;
        }
        return null;
    }

    // 系统 TMP/TEMP 写不进，就把本进程的 TMP/TEMP 兜到项目内（子进程会继承，csc 才工作得起来）。
    // 返回兜到的目录；null = 不用兜（或连项目内也写不进）。curTmp/curTemp 显式传，方便自测。
    public static string EnsureTemp(string exeDir, string cwd, string curTmp, string curTemp)
    {
        try
        {
            if (TryWrite(curTmp) && TryWrite(curTemp)) return null;
            string dir = PickTemp(exeDir, cwd);
            if (dir == null) return null;
            Environment.SetEnvironmentVariable("TMP", dir, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("TEMP", dir, EnvironmentVariableTarget.Process);
            return dir;
        }
        catch (Exception ex) { Log.Swallow(ex, "兜临时目录"); return null; }
    }

    // 返回给人看的告警（空串 = 一切正常），并写进日志
    public static string Run(string exeDir, string cwd)
    {
        string w = "";
        try
        {
            string integ = IntegrityName();
            string tmpDir = EnsureTemp(exeDir, cwd,
                Environment.GetEnvironmentVariable("TMP"), Environment.GetEnvironmentVariable("TEMP"));
            if (integ == "低" || integ == "不受信任")
                w += "⚠ 这次是以「" + integ + "完整性」运行的：写系统临时目录、写工作目录之外的 .exe 都可能报「拒绝访问」，"
                   + "这不是 API Key 的问题。常见原因：程序所在目录被打了 Low 完整性标签（在这个目录里生成的 exe 都会低完整性启动），"
                   + "或者它是从别的沙箱窗口里被拉起来的。处理：管理员身份的 cmd 里执行 "
                   + "icacls \"<程序所在目录>\" /setintegritylevel (OI)(CI)Medium /T，或者把程序放到没被标记的目录再运行。";
            if (tmpDir != null)
                w += (w.Length > 0 ? "\n" : "") + "⚠ 系统临时目录写不进，已把 TMP/TEMP 兜到 " + tmpDir + "。";
            if (w.Length > 0) Log.Audit("启动自检 · " + w.Replace("\r", " ").Replace("\n", "  "));
        }
        catch (Exception ex) { Log.Swallow(ex, "启动自检"); }
        Warning = w;
        return w;
    }
}

// ---------- 踩坑本：在同一台机器上做错过的招，下次别再重来一遍 ----------
// 设计原则（避免"越学越脏"）：
//   · 只记确定性事实：重复失败的调用、工具报错原文、用户显式教的。不做模型自我总结。
//   · 只按关键词命中才提醒，上限 60 条、每条一行，绝不把上下文撑爆。
//   · 全部落在 <工作目录>\devtools\lessons.json：人可读、可手改、可删。
class Lesson
{
    public string id = "";
    public string triggers = "";   // 逗号分隔的关键词（用来"认人"）
    public string title = "";      // 一句话：错在哪
    public string fix = "";        // 该怎么处置
    public string added = "";
    public int hits;
}

class LessonStore
{
    public List<Lesson> items = new List<Lesson>();
}

// ---------- 自我迭代记录：每一次「申请—批准—执行—验证—回滚」都留痕 ----------
// 放在 <工作目录>\devtools\iterations.json：人可读、可手改，也是"这轮改造到底做了什么"的唯一权威记录。
class Iteration
{
    public string id = "";
    public string time = "";
    public string title = "";
    public string change = "";     // 改造内容
    public string scope = "";      // 影响范围
    public string risk = "";       // 潜在风险
    public string expect = "";     // 预期效果
    public string verify = "";     // 怎么验证
    public string decision = "";   // 已批准 / 已驳回
    public string state = "";      // 待执行 / 已执行 / 已回滚 / 执行失败
    public string note = "";
}

class IterationStore
{
    public List<Iteration> items = new List<Iteration>();
}

static class Iterations
{
    public const int MaxItems = 200;

    public static string PathOf(string root) { return Path.Combine(Path.Combine(root, "devtools"), "iterations.json"); }

    public static List<Iteration> Load(string root)
    {
        try
        {
            string f = PathOf(root);
            if (!File.Exists(f)) return new List<Iteration>();
            IterationStore st = Json.Ser.Deserialize<IterationStore>(File.ReadAllText(f, Encoding.UTF8));
            if (st == null || st.items == null) return new List<Iteration>();
            return st.items;
        }
        catch (Exception ex) { Log.Swallow(ex, "读自迭代记录"); return new List<Iteration>(); }
    }

    public static void Save(string root, List<Iteration> list)
    {
        try
        {
            while (list.Count > MaxItems) list.RemoveAt(0);
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(root)));
            IterationStore st = new IterationStore();
            st.items = list;
            File.WriteAllText(PathOf(root), Json.Ser.Serialize(st), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Swallow(ex, "写自迭代记录"); }
    }

    public static Iteration Add(string root, Iteration it)
    {
        if (it == null) return null;
        if (it.id.Length == 0) it.id = "it-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (it.time.Length == 0) it.time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        List<Iteration> list = Load(root);
        list.Add(it);
        Save(root, list);
        Log.Audit("自迭代·" + it.decision + " · " + it.id + " · " + it.title);
        return it;
    }

    // 拿到最近一条"已批准但还没执行完"的申请（回滚时要认它）
    public static Iteration LastApproved(string root)
    {
        List<Iteration> list = Load(root);
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i] != null && list[i].decision == "已批准" && list[i].state != "已回滚" && list[i].state != "已执行")
                return list[i];
        return null;
    }

    public static void MarkState(string root, string id, string state, string note)
    {
        List<Iteration> list = Load(root);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] == null || list[i].id != id) continue;
            list[i].state = state;
            if (note != null && note.Length > 0) list[i].note = note;
            break;
        }
        Save(root, list);
        Log.Audit("自迭代·" + state + " · " + id + (note != null && note.Length > 0 ? " · " + note : ""));
    }
}

static class Lessons
{
    public const int MaxItems = 60;

    public static string PathOf(string root)
    {
        return Path.Combine(Path.Combine(root, "devtools"), "lessons.json");
    }

    public static List<Lesson> Load(string root)
    {
        try
        {
            string f = PathOf(root);
            if (!File.Exists(f)) return new List<Lesson>();
            LessonStore st = Json.Ser.Deserialize<LessonStore>(File.ReadAllText(f, Encoding.UTF8));
            if (st == null || st.items == null) return new List<Lesson>();
            return st.items;
        }
        catch (Exception ex) { Log.Swallow(ex, "读踩坑本"); return new List<Lesson>(); }
    }

    public static void Save(string root, List<Lesson> list)
    {
        try
        {
            // 超上限：先淘汰最没用的（命中次数最少的），不是简单砍尾巴
            while (list.Count > MaxItems)
            {
                int victim = 0;
                for (int i = 1; i < list.Count; i++)
                    if (list[i] != null && (list[victim] == null || list[i].hits < list[victim].hits)) victim = i;
                list.RemoveAt(victim);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(root)));
            LessonStore st = new LessonStore();
            st.items = list;
            File.WriteAllText(PathOf(root), Json.Ser.Serialize(st), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Swallow(ex, "写踩坑本"); }
    }

    // 记一条。同一个关键词组合只留一条，重复出现就累加 hits（越常踩越不会被淘汰）
    public static Lesson Add(string root, string triggers, string title, string fix)
    {
        string t = triggers == null ? "" : triggers.Trim();
        if (t.Length == 0) return null;
        List<Lesson> list = Load(root);
        string id = t.ToLowerInvariant();
        Lesson hit = null;
        foreach (Lesson l in list) if (l != null && l.id == id) { hit = l; break; }
        if (hit == null)
        {
            hit = new Lesson();
            hit.id = id;
            hit.triggers = t;
            hit.title = Txt.Clip(title == null ? "" : title, 160);
            hit.fix = Txt.Clip(fix == null ? "" : fix, 300);
            hit.added = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            list.Add(hit);
            Log.Audit("踩坑本·新增 · [" + t + "] " + hit.title);
        }
        else
        {
            hit.hits++;
            if (fix != null && fix.Trim().Length > 0) hit.fix = Txt.Clip(fix, 300);
        }
        Save(root, list);
        return hit;
    }

    // 工具参数里挑"能用来认人"的值：第一个够长的字符串（路径 / 命令 / 关键词）。
    // 挑不出来就不记 —— 没有特征的坑记了也没法在下一次命中，只会变成噪声。
    public static string ValueTrigger(Dictionary<string, object> args)
    {
        if (args == null) return "";
        foreach (KeyValuePair<string, object> kv in args)
        {
            string v = kv.Value == null ? "" : Convert.ToString(kv.Value,
                System.Globalization.CultureInfo.InvariantCulture);
            if (v == null) continue;
            v = v.Trim();
            if (v.Length < 3) continue;
            return v.Length > 60 ? v.Substring(0, 60) : v;
        }
        return "";
    }

    // 工具参数拼成一段纯文本（**不做 JSON 转义**）：路径里的反斜杠要原样保留，
    // 否则 JSON 转义成 \\ 之后，关键词永远匹配不上（自测里真踩到过）。
    public static string ArgsText(Dictionary<string, object> args)
    {
        if (args == null) return "";
        StringBuilder sb = new StringBuilder();
        foreach (KeyValuePair<string, object> kv in args)
        {
            if (kv.Value == null) continue;
            string v = Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (v == null || v.Trim().Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(v);
        }
        return sb.ToString();
    }

    // 重复失败被拦下时调用：这是"这次做错了"最可靠的信号
    public static Lesson CaptureDuplicate(string root, string toolName, Dictionary<string, object> args, string prevFail)
    {
        string tk = ValueTrigger(args);
        if (tk.Length == 0) return null;
        return Add(root, tk,
            toolName + " 用同样的参数失败过：" + Txt.FirstLine(prevFail == null ? "" : prevFail),
            "别原样重试：换参数、换工具，或先把原文/现状读清楚再动手");
    }

    public static List<Lesson> Match(string root, string text, int max, bool bump)
    {
        List<Lesson> hitList = new List<Lesson>();
        if (text == null || text.Trim().Length == 0) return hitList;
        List<Lesson> all = Load(root);
        string low = text.ToLowerInvariant();
        foreach (Lesson l in all)
        {
            if (l == null || l.triggers == null) continue;
            string[] parts = l.triggers.Split(',');
            bool hit = false;
            foreach (string p in parts)
            {
                string k = p.Trim().ToLowerInvariant();
                if (k.Length >= 2 && low.IndexOf(k) >= 0) { hit = true; break; }
            }
            if (!hit) continue;
            if (bump) l.hits++;
            hitList.Add(l);
            if (hitList.Count >= max) break;
        }
        if (bump && hitList.Count > 0) Save(root, all);
        return hitList;
    }

    // 一轮开始时：命中的坑拼成一条提醒（没命中返回 ""，绝不刷屏）
    public static string Note(string root, string userText, int max)
    {
        try
        {
            List<Lesson> hits = Match(root, userText, max, true);
            return NoteText(hits);
        }
        catch (Exception ex) { Log.Swallow(ex, "查踩坑本"); return ""; }
    }

    public static string NoteText(List<Lesson> hits)
    {
        if (hits == null || hits.Count == 0) return "";
        StringBuilder sb = new StringBuilder();
        sb.Append("（系统提醒 · 踩坑本命中）以前在这台机器上踩过下面这些坑，先照着处置，别再重来：\n");
        foreach (Lesson l in hits)
        {
            sb.Append("- ").Append(l.title);
            if (l.fix != null && l.fix.Length > 0) sb.Append(" —— ").Append(l.fix);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // 工具调用前：这个参数以前失败过（跨会话），把话捎在结果里，别让它再撞一次
    public static string Warn(string root, string argsText)
    {
        try
        {
            List<Lesson> hits = Match(root, argsText, 1, false);   // 不写盘：这里可能每步都调到
            if (hits.Count == 0) return "";
            Lesson l = hits[0];
            return "\n（踩坑本提醒：这个调用以前失败过 —— " + l.title
                + (l.fix != null && l.fix.Length > 0 ? "；处置：" + l.fix : "") + "）";
        }
        catch (Exception ex) { Log.Swallow(ex, "查踩坑本"); return ""; }
    }
}

// ---------- 后台任务：长活别再被前台的超时掐断 ----------
// 以前只有 run_shell：命令跑多久就阻塞多久，默认 120 秒一到连进程一起杀。
// 记分卡那种十几分钟的活就是这么死在半路的（history.csv 一直落不了盘，根子在这）。
// 现在：run_background 起任务立刻拿到任务号 → job_status 看进度 → job_kill 收尾。
// 输出落 <工作目录>\_build\jobs\<任务号>.log，任务本体随程序进程存活。
class BgJob
{
    public string id = "";
    public string name = "";
    public string command = "";
    public string logPath = "";
    public Process proc;
    public DateTime started = DateTime.Now;
    public int exitCode = int.MinValue;
    public bool killed;
}

static class Jobs
{
    const int KeepLogs = 20;              // 只留最近这么多份任务输出，别把目录撑爆
    static readonly List<BgJob> _all = new List<BgJob>();
    static readonly object _lock = new object();
    static int _seq;

    public static string DirOf(string root)
    {
        return Path.Combine(Path.Combine(root, "_build"), "jobs");
    }

    public static List<BgJob> All()
    {
        lock (_lock) return new List<BgJob>(_all);
    }

    // 任务号支持前缀匹配：传 "3" 也能找到 "3-153012"（短号更省 token），传全号更精确
    public static BgJob Find(string id)
    {
        if (id == null) return null;
        string want = id.Trim();
        if (want.Length == 0) return null;
        lock (_lock)
        {
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                BgJob j = _all[i];
                if (j.id == want || j.id.StartsWith(want + "-", StringComparison.OrdinalIgnoreCase)) return j;
            }
        }
        return null;
    }

    public static BgJob Start(string root, string command, string name)
    {
        string dir = DirOf(root);
        Directory.CreateDirectory(dir);
        PruneLogs(dir);
        BgJob j = new BgJob();
        lock (_lock)
        {
            _seq++;
            j.id = _seq.ToString() + "-" + DateTime.Now.ToString("HHmmss");
        }
        j.name = (name == null || name.Trim().Length == 0) ? Txt.FirstLine(command) : name.Trim();
        j.command = command;
        j.logPath = Path.Combine(dir, j.id + ".log");
        j.started = DateTime.Now;

        string shell = Tools.FindShell();
        bool usePs = shell.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase);
        string arguments;
        if (usePs)
        {
            string script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n"
                + "$ProgressPreference='SilentlyContinue'\n" + command + "\n";
            arguments = "-NoProfile -NonInteractive -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }
        else arguments = "/c " + command;

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = shell;
        psi.Arguments = arguments;
        psi.WorkingDirectory = root;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        StreamWriter log = new StreamWriter(j.logPath, false, new UTF8Encoding(false));
        log.AutoFlush = true;
        Process p = new Process();
        p.StartInfo = psi;
        p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
        { if (e.Data != null) { lock (_lock) { try { log.WriteLine(e.Data); } catch { } } } };
        p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
        { if (e.Data != null) { lock (_lock) { try { log.WriteLine("[stderr] " + e.Data); } catch { } } } };
        p.EnableRaisingEvents = true;
        p.Exited += delegate(object s, EventArgs e)
        {
            try { p.WaitForExit(); } catch { }          // 等异步输出收干净，再关日志
            try { j.exitCode = p.ExitCode; } catch { }
            lock (_lock) { try { log.Flush(); log.Close(); } catch { } }
        };
        j.proc = p;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (_lock) _all.Add(j);
        Log.Audit("后台任务·启动 · " + j.id + " · " + Txt.Clip(j.command, 120));
        return j;
    }

    // 日志目录别无限长：只留最近 KeepLogs 份
    static void PruneLogs(string dir)
    {
        try
        {
            List<string> files = new List<string>(Directory.GetFiles(dir, "*.log"));
            if (files.Count < KeepLogs) return;
            files.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i <= files.Count - KeepLogs; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
        catch (Exception ex) { Log.Swallow(ex, "清理任务日志"); }
    }

    // ---------- 三个工具入口 ----------
    public static string Run(ToolContext ctx, Dictionary<string, object> a)
    {
        string command = A.Str(a, "command", "");
        if (command.Trim().Length == 0) throw new ToolException("命令不能为空");
        BgJob j = Start(ctx.Root, command, A.Str(a, "name", ""));
        return "已在后台启动 · 任务号 " + j.id + "（" + j.name + "）\n输出写到 " + j.logPath
            + "\n用 job_status 看进度（task_id \"" + j.id + "\"），要停就 job_kill。";
    }

    public static string Status(ToolContext ctx, Dictionary<string, object> a)
    {
        string id = A.Str(a, "task_id", "").Trim();
        int lines = A.Int(a, "lines", 30);
        if (id.Length > 0)
        {
            BgJob j = Find(id);
            if (j == null) throw new ToolException("没有这个任务号：" + id + "（不带参数调 job_status 看全部任务）");
            return Describe(j, lines);
        }
        List<BgJob> all = All();
        if (all.Count == 0) return "还没有后台任务。用 run_background 起一个。";
        StringBuilder sb = new StringBuilder();
        sb.Append("后台任务（" + all.Count + " 个，新的在前）：\n");
        for (int i = all.Count - 1; i >= 0; i--)
        {
            BgJob j = all[i];
            sb.Append("  · ").Append(j.id).Append("  ").Append(StateOf(j)).Append("  ")
              .Append(Txt.Clip(j.name, 60)).Append('\n');
        }
        return sb.ToString();
    }

    public static string KillTool(ToolContext ctx, Dictionary<string, object> a)
    {
        BgJob j = Find(A.Str(a, "task_id", ""));
        if (j == null) throw new ToolException("没有这个任务号：" + A.Str(a, "task_id", ""));
        return Kill(j);
    }

    public static string Kill(BgJob j)
    {
        if (j == null) return "任务不存在。";
        try
        {
            if (!j.proc.HasExited)
            {
                j.killed = true;
                j.proc.Kill();
                Log.Audit("后台任务·终止 · " + j.id + " · " + Txt.Clip(j.command, 120));
                return "已结束后台任务 " + j.id + "（" + j.name + "）";
            }
        }
        catch (Exception ex) { return "结束失败：" + ex.Message; }
        return "任务 " + j.id + " 早就结束了（" + StateOf(j) + "）";
    }

    static string StateOf(BgJob j)
    {
        try
        {
            if (!j.proc.HasExited)
                return "运行中（已跑 " + (int)(DateTime.Now - j.started).TotalSeconds + " 秒）";
        }
        catch { }
        if (j.killed) return "已结束（被手动终止）";
        return "已结束（退出码 " + j.exitCode + "）";
    }

    public static string Describe(BgJob j, int lines)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("任务 ").Append(j.id).Append("（").Append(j.name).Append("）\n");
        sb.Append("  状态：").Append(StateOf(j)).Append('\n');
        sb.Append("  命令：").Append(Txt.Clip(j.command, 200)).Append('\n');
        sb.Append("  输出：").Append(j.logPath).Append('\n');
        if (lines > 0)
            sb.Append("  末尾 ").Append(lines).Append(" 行：\n").Append(ReadTail(j.logPath, lines));
        return sb.ToString();
    }

    // 只读日志尾部：文件再大也只读最后一个块，别把整份日志吞进内存
    static string ReadTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "（还没有输出）\n";
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long take = Math.Min(fs.Length, 65536);
                fs.Seek(-take, SeekOrigin.End);
                byte[] buf = new byte[take];
                int n = fs.Read(buf, 0, (int)take);
                string text = Encoding.UTF8.GetString(buf, 0, n).Replace("\r\n", "\n");
                string[] all = text.Split('\n');
                int from = Math.Max(0, all.Length - lines - 1);   // 头一行可能是被截断的半行，丢掉
                StringBuilder sb = new StringBuilder();
                for (int i = from; i < all.Length; i++)
                    if (all[i].Length > 0 || i < all.Length - 1) sb.Append(all[i]).Append('\n');
                return sb.ToString();
            }
        }
        catch (Exception ex) { return "（读输出失败：" + ex.Message + "）\n"; }
    }
}

// ---------- 交付记录导出：干了什么 / 改了哪些文件 / 跑了哪些验证 / 结论 ----------
// 全部取自真实数据（会话消息 + 每轮快照 + 工具调用记录），不调模型、不做二次总结 ——
// 导出这类文档最怕"再编一遍"，那还不如不算。
static class Deliver
{
    public static string FileName()
    {
        return "交付记录-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".md";
    }

    static DateTime FromUnix(long ts)
    {
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime();
    }

    // 工具结果的第一行往往是 "[run_shell 结果]" 这种表头，跳掉它取真正的内容（自测里真踩到过）
    static string ResultHead(string content, int maxLines)
    {
        string[] lines = (content == null ? "" : content).Replace("\r\n", "\n").Split('\n');
        StringBuilder sb = new StringBuilder();
        int taken = 0;
        for (int i = 0; i < lines.Length && taken < maxLines; i++)
        {
            string l = lines[i].Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith("[") && l.EndsWith("结果]")) continue;
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append(l);
            taken++;
        }
        return sb.Length == 0 ? "（没有输出）" : sb.ToString();
    }

    public static string Build(AgentRunner r, string root)
    {
        List<string> asks = new List<string>();
        List<string> conclusions = new List<string>();
        List<string> flow = new List<string>();
        List<string> runCmd = new List<string>();
        List<string> runRes = new List<string>();
        Dictionary<string, int> runIdx = new Dictionary<string, int>();
        int toolCount = 0;
        long firstTs = 0;

        for (int i = 1; i < r.Messages.Count; i++)
        {
            Dictionary<string, object> m = r.Messages[i];
            string role = Json.Str(m, "role", "");
            string content = Json.Str(m, "content", "");
            if (role == "user")
            {
                string c = content.Trim();
                // 框架自己注入的提醒不算"用户原话"
                if (c.Length == 0 || c.StartsWith("（系统提醒") || c.StartsWith("（踩坑本")) continue;
                asks.Add(c);
                if (firstTs == 0)
                {
                    object ts = Json.Get(m, "ts");
                    if (ts != null) { try { firstTs = Convert.ToInt64(ts, System.Globalization.CultureInfo.InvariantCulture); } catch { } }
                }
                continue;
            }
            if (role == "assistant")
            {
                object[] calls = Json.Arr(Json.Get(m, "tool_calls"));
                if (calls != null)
                {
                    for (int k = 0; k < calls.Length; k++)
                    {
                        Dictionary<string, object> call = Json.Map(calls[k]);
                        if (call == null) continue;
                        string name = Json.Str(call, "name", "?");
                        Dictionary<string, object> args = Json.Map(Json.Get(call, "arguments"));
                        if (args == null) args = new Dictionary<string, object>();
                        toolCount++;
                        if (flow.Count < 200) flow.Add("- `" + name + "` " + Txt.Clip(AgentRunner.FormatArgs(name, args), 160));
                        if (name == "run_shell" || name == "run_background")
                        {
                            string id = Json.Str(call, "id", "");
                            if (id.Length == 0) id = "#" + runCmd.Count;
                            runCmd.Add(A.Str(args, "command", ""));
                            runRes.Add("（没看到结果）");
                            runIdx[id] = runCmd.Count - 1;
                        }
                    }
                }
                if (content.Trim().Length > 0) conclusions.Add(content.Trim());
                continue;
            }
            if (role == "tool")
            {
                string id = Json.Str(m, "tool_call_id", "");
                int idx;
                if (id.Length > 0 && runIdx.TryGetValue(id, out idx) && idx >= 0 && idx < runRes.Count)
                    runRes[idx] = ResultHead(content, 2);
            }
        }

        StringBuilder sb = new StringBuilder();
        sb.Append("# 交付记录：").Append(asks.Count > 0 ? Txt.Clip(Txt.FirstLine(asks[0]), 60) : "（本会话还没提过要求）").Append('\n');
        sb.Append('\n');
        sb.Append("- 导出时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');
        string providerName = r.Config.provider;
        try
        {
            Provider pv = r.Config.Current();
            if (pv != null && !string.IsNullOrEmpty(pv.name)) providerName = pv.name;
        }
        catch { }
        sb.Append("- 模型：").Append(r.Config.model).Append("（").Append(providerName).Append("）\n");
        sb.Append("- 工作目录：").Append(root).Append('\n');
        sb.Append("- 规模：").Append(asks.Count).Append(" 条要求 · ").Append(toolCount).Append(" 次工具调用\n\n");

        sb.Append("## 一、要我做的事（用户原话）\n\n");
        if (asks.Count == 0) sb.Append("- （无）\n");
        for (int i = 0; i < asks.Count && i < 20; i++)
            sb.Append(i + 1).Append(". ").Append(Txt.Clip(asks[i], 500)).Append('\n');
        if (asks.Count > 20) sb.Append("- …（还有 ").Append(asks.Count - 20).Append(" 条）\n");
        sb.Append('\n');

        sb.Append("## 二、改动了哪些文件\n\n");
        AppendChanges(sb, root, firstTs);

        sb.Append("## 三、跑过的命令 / 验证\n\n");
        if (runCmd.Count == 0) sb.Append("- （没跑过命令）\n");
        for (int i = 0; i < runCmd.Count; i++)
            sb.Append("- `").Append(Txt.Clip(runCmd[i], 200)).Append("` → ").Append(Txt.Clip(runRes[i], 160)).Append('\n');
        sb.Append('\n');

        sb.Append("## 四、结论（智能体最后说的话）\n\n");
        if (conclusions.Count == 0) sb.Append("（没有文字结论）\n");
        else
        {
            int from = Math.Max(0, conclusions.Count - 2);
            for (int i = from; i < conclusions.Count; i++)
                sb.Append(Txt.Clip(conclusions[i], 3000)).Append("\n\n");
        }

        if (flow.Count > 0)
        {
            sb.Append("<details><summary>附：工具调用流水（").Append(toolCount).Append(" 次，点开看）</summary>\n\n");
            foreach (string f in flow) sb.Append(f).Append('\n');
            if (toolCount > flow.Count) sb.Append("- …（还有 ").Append(toolCount - flow.Count).Append(" 次）\n");
            sb.Append("\n</details>\n");
        }
        return sb.ToString();
    }

    // 改动清单：从 .snapshots 里取本会话期间的每轮快照（框架每轮自动落的那份）
    static void AppendChanges(StringBuilder sb, string root, long firstTs)
    {
        try
        {
            string dir = SnapStore.Dir(root);
            if (!Directory.Exists(dir)) { sb.Append("- （没有改动：`.snapshots` 目录还不存在）\n\n"); return; }
            List<string> files = new List<string>(Directory.GetFiles(dir, "*.json"));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            DateTime since = firstTs > 0 ? FromUnix(firstTs).AddMinutes(-2) : DateTime.MinValue;
            bool any = false;
            for (int i = 0; i < files.Count; i++)
            {
                Dictionary<string, object> doc = null;
                try { doc = Json.Obj(File.ReadAllText(files[i], Encoding.UTF8)); }
                catch { }
                if (doc == null) continue;
                string time = Json.Str(doc, "time", "");
                DateTime t;
                if (DateTime.TryParse(time, out t) && t < since) continue;      // 不属于本会话的旧快照，跳过
                object[] items = Json.Arr(Json.Get(doc, "files"));
                if (items == null || items.Length == 0) continue;
                any = true;
                sb.Append("### ").Append(time).Append(" · ").Append(Json.Str(doc, "note", "")).Append('\n');
                for (int k = 0; k < items.Length; k++)
                {
                    Dictionary<string, object> f = Json.Map(items[k]);
                    if (f == null) continue;
                    string path = Json.Str(f, "path", "");
                    string rel = Json.Str(f, "rel", "");
                    string oldText = Json.Str(f, "old", "");
                    string now = "";
                    bool exists = false;
                    try { exists = File.Exists(path); if (exists) now = File.ReadAllText(path, Encoding.UTF8); }
                    catch { }
                    FileChange c = new FileChange();
                    c.Path = path; c.Rel = rel; c.OldText = oldText; c.NewText = now;
                    Diff.Build(c);
                    sb.Append("- `").Append(rel).Append("` — ")
                      .Append(oldText.Length == 0 ? "新增" : (exists ? "修改" : "已删除"))
                      .Append("（+").Append(c.Added).Append(" / −").Append(c.Removed).Append(" 行）\n");
                    if (c.DiffText != null && c.DiffText.Trim().Length > 0 && c.DiffText.Length <= 3000
                        && c.DiffText.IndexOf("（内容没变）") < 0)
                        sb.Append("```diff\n").Append(c.DiffText.TrimEnd()).Append("\n```\n");
                }
                sb.Append('\n');
            }
            if (!any) sb.Append("- （本会话没有改动过文件）\n\n");
        }
        catch (Exception ex) { Log.Swallow(ex, "导出·读快照"); sb.Append("- （读快照失败：" + ex.Message + "）\n\n"); }
    }

    public static string Export(AgentRunner r, string root)
    {
        string text = Build(r, root);
        string path = Path.Combine(root, FileName());
        File.WriteAllText(path, text, new UTF8Encoding(false));
        Log.Audit("导出交付记录 · " + path + " · " + text.Length + " 字符");
        return path;
    }
}

// ---------- 自升级闭环：把 .new 换上去并自动重启 ----------
// 以前：编译出 .new 之后必须人工退出程序、双击「升级.cmd」—— 智能体每改一次自己就要人接手一次。
// 现在：起一个隐藏的 PowerShell helper，等本进程退出 → 替换正式版 → 删掉 .new → 再把新版拉起来。
// helper 的过程写进 <程序目录>\_build\upgrade.log，失败时能查。
static class Upgrader
{
    public static Action RequestClose;      // 由窗口版装上：走正常关闭（会保存会话、不会再问一次）

    public static List<string> PendingNewFiles(string exeDir)
    {
        List<string> outList = new List<string>();
        try
        {
            string[] files = Directory.GetFiles(exeDir, "*.new");
            foreach (string f in files) outList.Add(Path.GetFileName(f));
        }
        catch (Exception ex) { Log.Swallow(ex, "查 .new"); }
        return outList;
    }

    // 生成 helper 脚本（pid 可注入，自测里用得上）
    internal static string ScriptFor(int pid, string exeDir, string relaunchExe)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("$ErrorActionPreference = 'SilentlyContinue'\n");
        sb.Append("$dir = '").Append(exeDir.Replace("'", "''")).Append("'\n");
        // 日志目录可能不存在（自测/临时目录），先建；写日志失败也不能影响替换本身
        sb.Append("$logDir = Join-Path $dir '_build'\n");
        sb.Append("New-Item -ItemType Directory -Force -Path $logDir | Out-Null\n");
        sb.Append("$log = Join-Path $logDir 'upgrade.log'\n");
        sb.Append("function Say($m) { try { ('[' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '] ' + $m) | Out-File -FilePath $log -Append -Encoding UTF8 } catch { } }\n");
        sb.Append("Say 'helper 启动，等 PID ").Append(pid).Append(" 退出'\n");
        sb.Append("for ($i = 0; $i -lt 240; $i++) { if (-not (Get-Process -Id ").Append(pid)
          .Append(" -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 500 }\n");
        sb.Append("if (Get-Process -Id ").Append(pid)
          .Append(" -ErrorAction SilentlyContinue) { Say '等超时了，程序还活着，放弃替换'; exit 1 }\n");
        sb.Append("Start-Sleep -Seconds 2\n");                                 // 等文件锁真的释放
        sb.Append("Get-ChildItem -LiteralPath $dir -Filter '*.new' | ForEach-Object {\n");
        sb.Append("  $dst = Join-Path $dir ($_.Name.Substring(0, $_.Name.Length - 4))\n");
        sb.Append("  try {\n");
        sb.Append("    if (Test-Path -LiteralPath $dst) { Copy-Item -LiteralPath $dst -Destination ($dst + '.prev') -Force -ErrorAction SilentlyContinue }\n");
        sb.Append("    Copy-Item -LiteralPath $_.FullName -Destination $dst -Force -ErrorAction Stop\n");
        sb.Append("    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue\n");
        sb.Append("    Say ('已替换 ' + $_.Name + ' → ' + [IO.Path]::GetFileName($dst) + '（上一版留成 .prev）')\n");
        sb.Append("  } catch { Say ('替换失败 ' + $_.Name + '：' + $_.Exception.Message) }\n");
        sb.Append("}\n");
        if (relaunchExe != null && relaunchExe.Length > 0)
        {
            sb.Append("Start-Sleep -Seconds 1\n");
            sb.Append("try {\n");
            sb.Append("  $p = Start-Process -FilePath '").Append(relaunchExe.Replace("'", "''")).Append("' -PassThru\n");
            sb.Append("  Say ('已启动新版，PID ' + $p.Id)\n");
            // 看门狗：新版起来 10 秒内自己退出 = 起不来 → 回滚到 .prev 再拉一次
            sb.Append("  Start-Sleep -Seconds 10\n");
            sb.Append("  if ($p.HasExited) {\n");
            sb.Append("    Say ('新版启动后 10 秒内退出了（退出码 ' + $p.ExitCode + '），判定为起不来，回滚')\n");
            sb.Append("    $prev = '").Append(relaunchExe.Replace("'", "''")).Append(".prev'\n");
            sb.Append("    if (Test-Path -LiteralPath $prev) {\n");
            sb.Append("      Copy-Item -LiteralPath $prev -Destination '").Append(relaunchExe.Replace("'", "''"))
              .Append("' -Force -ErrorAction Stop\n");
            sb.Append("      Start-Process -FilePath '").Append(relaunchExe.Replace("'", "''")).Append("'\n");
            sb.Append("      Say '已回滚到上一版并重新启动'\n");
            sb.Append("    } else { Say '没有 .prev 可回滚，请人工检查' }\n");
            sb.Append("  }\n");
            sb.Append("} catch { Say ('启动失败：' + $_.Exception.Message) }\n");
        }
        return sb.ToString();
    }

    static string SelPow()
    {
        string shell = Tools.FindShell();
        if (shell.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)) return shell;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\WindowsPowerShell\v1.0\powershell.exe");
    }

    // 起 helper。relaunch=true 时替换完把窗口版拉起来（CLI 触发的就不拉，免得莫名弹个窗）
    public static bool ScheduleFor(int pid, string exeDir, string relaunchExe, out string message)
    {
        message = "";
        List<string> pending = PendingNewFiles(exeDir);
        if (pending.Count == 0) { message = "没有待升级的 .new"; return false; }
        try
        {
            string script = ScriptFor(pid, exeDir, relaunchExe);
            string args = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = SelPow();
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = exeDir;
            Process.Start(psi);
            Log.Audit("重启并升级 · 等退出后替换 " + string.Join("、", pending.ToArray())
                + (relaunchExe.Length > 0 ? "（替换完自动启动）" : "（不自动启动）"));
            message = "已安排：" + string.Join("、", pending.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            Log.Swallow(ex, "安排升级");
            message = "安排升级失败：" + ex.Message;
            return false;
        }
    }

    public static bool Schedule(string exeDir, bool relaunch, out string message)
    {
        string exe = Path.Combine(exeDir, "AhzScribe.exe");
        return ScheduleFor(Process.GetCurrentProcess().Id, exeDir,
            (relaunch && File.Exists(exe)) ? exe : "", out message);
    }

    // 智能体调得到的那一个：改完自己、跑过 build.cmd 之后用它收尾
    public static string RestartTool(ToolContext ctx, Dictionary<string, object> a)
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        List<string> pending = PendingNewFiles(exeDir);
        if (pending.Count == 0)
            return "没有待升级的 .new：先跑一遍 cmd /c build.cmd（窗口版在跑时它会自动产出 .new），然后再调我。";
        string why = A.Str(a, "reason", "");
        if (why.Length > 0) Log.Audit("重启并升级（智能体请求）· " + why);
        string msg;
        if (!Schedule(exeDir, true, out msg)) return "安排升级失败：" + msg;
        // 这次自我迭代就算"执行完"了（重启之后由用户/下一轮看效果）
        try
        {
            Iteration last = Iterations.LastApproved(ctx.Root);
            if (last != null) Iterations.MarkState(ctx.Root, last.id, "已执行", "已替换并重启");
        }
        catch (Exception ex) { Log.Swallow(ex, "标记迭代已执行"); }
        if (RequestClose != null) RequestClose();
        return "已安排：" + string.Join("、", pending.ToArray())
            + "\n程序马上退出完成替换，替换完自动启动（这一轮的最后回复可能来不及保存）。";
    }

    // ---------- 自我迭代：申请（弹标准申请单）→ 批准/驳回 → 固定四步执行 ----------
    public static Func<Iteration, int> ProposeConfirm;   // 由窗口版装上；CLI 走控制台兜底

    static int ConsolePropose(Iteration it)
    {
        try
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ⚠ 智能体申请改造自己：" + it.title);
            Console.ResetColor();
            Console.WriteLine(ProposalForm.FormatText(it));
            while (true)
            {
                Console.Write("  批准这次自我改造？[y]批准  [n]驳回（默认 n）：");
                string ans = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (ans == "y" || ans == "yes" || ans == "是") return 1;
                if (ans == "n" || ans == "no" || ans == "否" || ans.Length == 0) return 0;
            }
        }
        catch { return 0; }
    }

    public static string ProposeTool(ToolContext ctx, Dictionary<string, object> a)
    {
        Iteration it = new Iteration();
        it.title = A.Str(a, "title", "").Trim();
        it.change = A.Str(a, "change", "").Trim();
        it.scope = A.Str(a, "scope", "").Trim();
        it.risk = A.Str(a, "risk", "").Trim();
        it.expect = A.Str(a, "expect", "").Trim();
        it.verify = A.Str(a, "verify", "").Trim();
        if (it.title.Length == 0 || it.change.Length == 0)
            return "申请单至少要写 title（一句话标题）和 change（改什么）；建议把 scope / risk / expect / verify 一起写全。";
        int d = 0;
        if (ProposeConfirm != null) { try { d = ProposeConfirm(it); } catch (Exception ex) { Log.Swallow(ex, "改造申请弹窗"); } }
        else d = ConsolePropose(it);
        it.decision = d == 1 ? "已批准" : "已驳回";
        it.state = d == 1 ? "待执行" : "未执行";
        Iterations.Add(ctx.Root, it);
        if (d != 1)
            return "用户**驳回了**这次改造申请（编号 " + it.id + "）。别动手改代码；说清你的替代方案，"
                + "或者拆成一个更小、更好验证的改造再申请。";
        return "用户**批准了**这次改造（编号 " + it.id + "，已记进 devtools\\iterations.json）。"
            + "按这个固定顺序做完，一步都不许跳：\n"
            + "1) 用 edit_file 小步改代码（别整文件重写）；\n"
            + "2) 用 run_background 跑 `cmd /c build.cmd`，用 job_status 等结果：退出码 0 = 两个正式版都更新，3 = 窗口版被占用但已出 .new（也算成功），其它 = 失败；\n"
            + "3) 用 run_background 跑 `devtools\\bench\\run-ui-tests.cmd`，必须全绿；\n"
            + "4) 调 restart_upgrade 收尾（框架会自动退出、替换、重启，并把这次迭代记成「已执行」）。\n"
            + "任何一步失败：调 rollback_self 回滚到改动前，并说清卡在哪一步。";
    }

    // 回滚：把文件恢复成"改动前"（用最近一条快照），并把迭代记录标成已回滚。
    // 故意不设审批、也不受只读档拦截：这是**恢复**动作，拦它等于把用户锁在坏状态里。
    public static string RollbackTool(ToolContext ctx, Dictionary<string, object> a)
    {
        string reason = A.Str(a, "reason", "").Trim();
        try
        {
            string dir = SnapStore.Dir(ctx.Root);
            if (!Directory.Exists(dir)) return "没有 .snapshots 目录，没有可回滚的快照。";
            List<string> files = new List<string>(Directory.GetFiles(dir, "*.json"));
            if (files.Count == 0) return "没有可回滚的快照。";
            files.Sort(StringComparer.OrdinalIgnoreCase);
            string id = Path.GetFileNameWithoutExtension(files[files.Count - 1]);
            int okR, failR;
            string msg = SnapStore.Restore(ctx.Root, id, out okR, out failR);
            Iteration last = Iterations.LastApproved(ctx.Root);
            if (last != null) Iterations.MarkState(ctx.Root, last.id, "已回滚", reason);
            Log.Audit("自迭代·回滚 · 快照 " + id + (reason.Length > 0 ? " · " + reason : ""));
            return "已回滚到最近一条快照 " + id + "：" + msg + "\n（恢复 " + okR + " 个文件，失败 " + failR + " 个"
                + (reason.Length > 0 ? "；原因：" + reason : "") + "）\n"
                + "提醒：如果已经 build 出新版，回滚源码后需要再跑一次 build，才能让二进制和源码一致。";
        }
        catch (Exception ex)
        {
            Log.Swallow(ex, "自迭代回滚");
            return "回滚失败：" + ex.Message;
        }
    }
}
class MainForm : Form, IAgentUI
{
    // ---------- 数据 ----------
    private AgentConfig config;
    private AgentRunner runner;
    private volatile bool running;
    private bool suppressModelChange;
    private SessionStore sessionStore;
    private string currentSessionId = "";
    private bool sortDesc = true;
    private string filter = "";
    private float _dpi = 1f;
    private int stepNo = 0;
    private int turnNo = 0;
    private readonly bool _openSettingsOnStart;
    private readonly ToolTip tips = new ToolTip();

    // ---------- 控件 ----------
    private Panel side, main, contentHost, composerHost, statusBar, heroPanel;
    private UiDrop mApp, mEdit, mView, mHelp;
    private LogoMark mark, heroMark, avatar;
    private Label lblLogo, lblUser, lblSec, lblTitle, lblBadge, lblStatus, lblStats;
    private Label lblFoot, heroTitle, heroSub;
    private PillLabel pill;
    private UiButton btnNew, btnPlugins, hNew, hRefresh, hDir, hSettings;
    private UiButton btnMin, btnMax, btnClose;
    private UiIconButton btnSearch, btnSort, btnNewWs, btnUserDir;
    private Dot dot, dotStatus;
    private FieldBox searchBox;
    private TextBox txtSearch;
    private bool searchOpen;
    private SidebarList list;
    private TabsBar tabs;
    // 对话区：一条消息一个控件（学 Codex），流式输出只重画最后那一条
    private TranscriptView rtb;
    // 轨迹页用的是同一套渲染：思考正文 + 工具步骤在这里流式输出，同样不会整块重排
    private TranscriptView trace;
    private float zoom = 1f;
    private RoundedPanel inputCard;
    private HintTextBox txtInput;
    private const string PlaceholderText = "发消息或创建任务，/ 调用指令，@ 文件或对话";
    private UiButton btnSend;
    private UiDrop permChip, modeChip, btnPlus, planChip;
    private ModelChip cmbModel;
    private ModelPopover modelPop;                  // 打开着的模型浮层（没开就是 null）
    private PermPopover permPop;                    // 打开着的权限浮层

    // chip 上要显示的档位。第三步接上自绘滑块后改成读 config.effort
    private const string ChipEffort = "medium";
    private List<UiButton> heroChips = new List<UiButton>();
    private string[] heroTexts = new string[] { "总结这个项目", "找出所有 TODO", "解释一段代码", "写个批处理脚本" };

    // 设置窗口要用到的数据载体（主界面隐藏，只当数据源）
    private TextBox txtCwd;
    private CheckBox chkThink, chkAutoYes;
    private UiDrop cmbCtx;

    // ---------- 字体 ----------

    public MainForm(bool openSettingsOnStart)
    {
        _openSettingsOnStart = openSettingsOnStart;
        config = AgentConfig.Load();
        config.Migrate();
        BuildUi();
        LoadModels();
        ApplyModelInfo();
        LoadSessions();

        // 全局异常捕获：任何未处理异常都写进 Ahz错误日志.txt，不再"静默失灵"
        try
        {
            try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch { }
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            { Log.Crash(e.Exception, "UI 线程未处理异常"); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            { Log.Crash(e.ExceptionObject as Exception, "进程未处理异常"); };
        }
        catch (Exception ex) { Log.Swallow(ex, "安装全局异常捕获"); }

        // 智能体请求「重启升级」时走正常关闭流程（会保存会话），别直接 Environment.Exit
        Upgrader.RequestClose = delegate { try { BeginInvoke(new MethodInvoker(Close)); } catch { } };

        // 自我迭代申请单：智能体调 propose_upgrade 时，回 UI 线程弹标准申请单（默认按钮是「驳回」）
        Upgrader.ProposeConfirm = delegate(Iteration it)
        {
            object r = null;
            try
            {
                r = Invoke(new Func<int>(delegate
                {
                    using (ProposalForm dlg = new ProposalForm(it.title, ProposalForm.FormatText(it)))
                    {
                        dlg.ShowDialog(this);
                        return dlg.Decision;
                    }
                }));
            }
            catch (Exception ex) { Log.Swallow(ex, "改造申请弹窗"); return 0; }
            return r == null ? 0 : (int)r;
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 启动自检的告警：状态栏放一句，鼠标停上去看全文（全文已经写进 Ahz错误日志.txt）
        if (StartupCheck.Warning.Length > 0)
        {
            lblStatus.Text = "⚠ " + Txt.FirstLine(StartupCheck.Warning);
            lblStatus.ForeColor = Theme.Warn;
            tips.SetToolTip(lblStatus, StartupCheck.Warning);
        }
        if (_openSettingsOnStart)
            BeginInvoke(new MethodInvoker(delegate { OpenSettings(); }));
    }

    // ============================ 界面搭建 ============================

    void BuildUi()
    {
        // 关掉 WinForms 自动缩放：所有尺寸都按逻辑像素写，再用屏幕 DPI 换算成物理像素
        _dpi = DpiFactor();
        Theme.Dpi = _dpi;
        AutoScaleMode = AutoScaleMode.None;
        Text = "Ahz Scribe";
        Icon = Theme.AppIcon();
        Size = new Size(Theme.Px(1180), Theme.Px(780));
        MinimumSize = new Size(Theme.Px(920), Theme.Px(620));
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        DoubleBuffered = true;

        BuildTopStrip();
        BuildSidebar();
        BuildMain();

        Controls.Add(main);
        Controls.Add(side);

        BuildTranscriptFonts();

        // 只在设置窗口里出现的控件（主界面逻辑仍然会读写它们）
        txtCwd = new TextBox();
        txtCwd.Text = config.cwd;
        chkThink = new CheckBox();
        chkThink.Checked = config.think;
        chkAutoYes = new CheckBox();
        chkAutoYes.Checked = config.auto_yes;
        cmbCtx = new UiDrop();
        cmbCtx.AddRange(new object[] { "2048", "4096", "8192", "16384", "32768" });
        cmbCtx.SelectedItem = config.num_ctx.ToString();
        cmbCtx.SelectedIndexChanged += delegate
        {
            int v;
            if (int.TryParse(Convert.ToString(cmbCtx.SelectedItem), out v)) { config.num_ctx = v; config.Save(); }
        };

        SetPermMode(config.auto_yes, false);

        runner = new AgentRunner(config, this);
        LayoutUi();
        SyncWindowButtons();
        Resize += delegate { LayoutUi(); SyncWindowButtons(); };
    }

    static float DpiFactor()
    {
        try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
        catch { return 1f; }
    }

    void BuildTranscriptFonts()
    {
        ApplyTranscriptFonts();
    }

    // 对话区 / 轨迹页的字号（视图菜单里的放大/缩小作用在这里）
    void ApplyTranscriptFonts()
    {
        if (rtb == null) return;
        rtb.SetFonts(Theme.F(10.5f * zoom), Theme.FB(10.5f * zoom),
            Theme.M(9.5f * zoom), Theme.F(9f * zoom));
        if (trace != null)
            trace.SetFonts(Theme.M(9.6f * zoom), Theme.MB(9.6f * zoom),
                Theme.M(9.6f * zoom), Theme.F(9f * zoom));
    }

    // ---------- 顶部菜单条 ----------

    void BuildTopStrip()
    {
        // 注意：这一条不能做成子 Panel。窗口拖动靠 WM_NCHITTEST 返回 HTCAPTION，
        // 而鼠标压在子控件上时消息是发给子控件的，父窗口的 WndProc 根本收不到，
        // 结果就是"看起来是标题栏，但拖不动"。所以按钮直接挂在窗体上，条带背景由窗体自己画。

        mApp = MenuButton("应用");
        mApp.Items.Add("新建会话");
        mApp.Items.Add("选择工作目录…");
        mApp.Items.Add("插件…");
        mApp.Items.Add("导出交付记录…");
        mApp.Items.Add("重启并升级（用 .new）…");
        mApp.Items.Add("回滚这次改动（用快照）…");
        mApp.Items.Add("设置…");
        mApp.Items.Add("退出");

        mEdit = MenuButton("编辑");
        mEdit.Items.Add("复制全部对话");
        mEdit.Items.Add("清空当前对话");

        mView = MenuButton("视图");
        mView.Items.Add("放大字号");
        mView.Items.Add("缩小字号");
        mView.Items.Add("重置字号");
        mView.Items.Add("显示对话");
        mView.Items.Add("显示轨迹");

        mHelp = MenuButton("帮助");
        mHelp.Items.Add("使用说明");
        mHelp.Items.Add("关于 Ahz Scribe");

        Controls.Add(mApp);
        Controls.Add(mEdit);
        Controls.Add(mView);
        Controls.Add(mHelp);

        // 自绘的最小化 / 最大化 / 关闭（系统标题栏已经被我们干掉了）
        btnMin = WindowButton("min", false);
        btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
        tips.SetToolTip(btnMin, "最小化");
        Controls.Add(btnMin);

        btnMax = WindowButton("max", false);
        btnMax.Click += delegate
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
        };
        tips.SetToolTip(btnMax, "最大化 / 还原");
        Controls.Add(btnMax);

        btnClose = WindowButton("close", true);
        btnClose.Click += delegate { Close(); };
        tips.SetToolTip(btnClose, "关闭");
        Controls.Add(btnClose);
    }

    UiButton WindowButton(string icon, bool close)
    {
        UiButton b = new UiButton();
        b.Icon = icon;
        b.IconSize = 11;
        b.IconWeight = 1.3f;
        b.Text = "";
        b.Radius = 0;
        b.FillColor = Theme.Clear;
        b.HoverColor = close ? Color.FromArgb(232, 17, 35) : Theme.Hover;
        b.IconColor = Theme.Clear;
        b.TextColor = Theme.FgMid;
        if (close) b.HoverTextColor = Color.White;
        return b;
    }

    UiDrop MenuButton(string text)
    {
        UiDrop b = new UiDrop();
        b.KeepText = true;
        b.Text = text;
        b.FillColor = Theme.Clear;
        b.HoverColor = Theme.Hover;
        b.TextColor = Theme.FgMid;
        b.Radius = 7;
        b.Font = Theme.F(9.5f);
        b.Padding = new Padding(10, 0, 8, 0);
        b.ItemChosen += delegate(object s, EventArgs e) { MenuAction(Convert.ToString(s)); };
        return b;
    }

    // ---------- 左侧栏 ----------

    void BuildSidebar()
    {
        side = new Panel();
        side.BackColor = Theme.Side;
        side.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.Line, 1f))
                e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height);
        };

        mark = new LogoMark();
        mark.BackColor = Theme.Side;
        side.Controls.Add(mark);

        lblLogo = new Label();
        lblLogo.Text = "Ahz Scribe";
        lblLogo.Font = Theme.FB(12.5f);
        lblLogo.ForeColor = Theme.Fg;
        lblLogo.AutoSize = true;
        lblLogo.BackColor = Theme.Side;
        side.Controls.Add(lblLogo);

        pill = new PillLabel();
        pill.Text = "LOCAL";
        pill.BackColor = Theme.Side;
        side.Controls.Add(pill);
        pill.BringToFront();

        btnNew = new UiButton();
        btnNew.Text = "新会话";
        btnNew.Icon = "plus";
        btnNew.IconSize = 15;
        btnNew.IconGap = 8;
        btnNew.FillColor = Color.White;
        btnNew.HoverColor = Theme.Hover;
        btnNew.BorderColor = Theme.Line;
        btnNew.Radius = 10;
        btnNew.Font = Theme.F(10f);
        btnNew.Click += delegate { NewSession(); };
        side.Controls.Add(btnNew);

        // 「插件」：就在新会话下面（原来放设置的位置）
        btnPlugins = new UiButton();
        btnPlugins.Text = "插件";
        btnPlugins.Icon = "plug";
        btnPlugins.IconSize = 16;
        btnPlugins.Align = ContentAlignment.MiddleLeft;
        btnPlugins.Padding = new Padding(12, 0, 0, 0);
        btnPlugins.FillColor = Theme.Clear;
        btnPlugins.HoverColor = Theme.Hover;
        btnPlugins.TextColor = Theme.Fg;
        btnPlugins.Font = Theme.F(9.5f);
        btnPlugins.Click += delegate { OpenPlugins(); };
        tips.SetToolTip(btnPlugins, "插件：给 agent 加工具");
        side.Controls.Add(btnPlugins);

        lblSec = new Label();
        lblSec.Text = "工作区";
        lblSec.ForeColor = Theme.FgDim;
        lblSec.BackColor = Theme.Side;
        lblSec.Font = Theme.F(9f);
        lblSec.AutoSize = true;
        side.Controls.Add(lblSec);

        btnSearch = new UiIconButton("search");
        btnSearch.IconSize = 15;
        btnSearch.BackColor = Theme.Side;
        btnSearch.Click += delegate { ToggleSearch(); };
        tips.SetToolTip(btnSearch, "搜索会话");
        side.Controls.Add(btnSearch);

        btnSort = new UiIconButton("sort");
        btnSort.IconSize = 15;
        btnSort.BackColor = Theme.Side;
        btnSort.Click += delegate { sortDesc = !sortDesc; RefreshSessionList(); };
        tips.SetToolTip(btnSort, "切换排序方式");
        side.Controls.Add(btnSort);

        btnNewWs = new UiIconButton("plus");
        btnNewWs.IconSize = 15;
        btnNewWs.BackColor = Theme.Side;
        btnNewWs.Click += delegate { NewSession(); };
        tips.SetToolTip(btnNewWs, "新建会话");
        side.Controls.Add(btnNewWs);

        searchBox = new FieldBox(false);
        searchBox.Visible = false;
        txtSearch = (TextBox)searchBox.Inner;
        txtSearch.TextChanged += delegate { filter = txtSearch.Text; RefreshSessionList(); };
        txtSearch.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) CloseSearch();
        };
        side.Controls.Add(searchBox);

        list = new SidebarList();
        list.BackColor = Theme.Side;
        side.Controls.Add(list);

        avatar = new LogoMark();
        avatar.BackColor = Theme.Side;
        side.Controls.Add(avatar);

        lblUser = new Label();
        lblUser.Text = Environment.UserName;
        lblUser.ForeColor = Theme.FgMid;
        lblUser.BackColor = Theme.Side;
        lblUser.Font = Theme.F(9.5f);
        lblUser.AutoSize = false;
        lblUser.TextAlign = ContentAlignment.MiddleLeft;
        lblUser.AutoEllipsis = true;
        side.Controls.Add(lblUser);

        btnUserDir = new UiIconButton("folder");
        btnUserDir.IconSize = 15;
        btnUserDir.BackColor = Theme.Side;
        btnUserDir.Click += delegate { PickFolder(); };
        tips.SetToolTip(btnUserDir, "选择工作目录");
        side.Controls.Add(btnUserDir);

        dot = new Dot();
        dot.DotColor = Theme.Ok;
        dot.BackColor = Theme.Side;
        side.Controls.Add(dot);

        lblFoot = new Label();
        lblFoot.Text = ProjectName(config.cwd) + "  ·  " + config.cwd;
        lblFoot.ForeColor = Theme.FgFaint;
        lblFoot.BackColor = Theme.Side;
        lblFoot.Font = Theme.F(8.2f);
        lblFoot.AutoSize = false;
        lblFoot.TextAlign = ContentAlignment.MiddleLeft;
        lblFoot.AutoEllipsis = true;
        lblFoot.Cursor = Cursors.Hand;
        lblFoot.Click += delegate { PickFolder(); };
        side.Controls.Add(lblFoot);
    }

    // ---------- 右侧主区 ----------

    void BuildMain()
    {
        main = new Panel();
        main.BackColor = Theme.Bg;

        lblTitle = new Label();
        lblTitle.Text = "新会话";
        lblTitle.Font = Theme.FB(12.5f);
        lblTitle.ForeColor = Theme.Fg;
        lblTitle.AutoSize = true;
        lblTitle.BackColor = Theme.Bg;
        main.Controls.Add(lblTitle);

        modeChip = new UiDrop();
        modeChip.Icon = "lock";
        modeChip.IconSize = 13;
        modeChip.IconGap = 6;
        modeChip.FillColor = Theme.Clear;
        modeChip.HoverColor = Theme.Hover;
        modeChip.TextColor = Theme.FgDim;
        modeChip.Radius = 8;
        modeChip.Font = Theme.F(9f);
        modeChip.Padding = new Padding(8, 0, 10, 0);
        // 顶栏这个也走自绘的三档浮层（和输入条那个同一套）
        modeChip.KeepText = true;
        modeChip.Text = Perm.Label(config.permission);
        modeChip.Click += delegate { TogglePermPopover(modeChip); };
        main.Controls.Add(modeChip);

        lblBadge = new Label();
        lblBadge.Text = "";
        lblBadge.Font = Theme.F(8.5f);
        lblBadge.ForeColor = Theme.FgFaint;
        lblBadge.BackColor = Theme.Bg;
        lblBadge.AutoSize = true;
        lblBadge.TextChanged += delegate { LayoutUi(); };
        main.Controls.Add(lblBadge);

        hNew = new UiIconButton("plus");
        hNew.BackColor = Theme.Bg;
        hNew.Click += delegate { NewSession(); };
        tips.SetToolTip(hNew, "新建会话");
        main.Controls.Add(hNew);

        hRefresh = new UiIconButton("refresh");
        hRefresh.BackColor = Theme.Bg;
        hRefresh.Click += delegate { LoadModels(); ApplyModelInfo(); };
        tips.SetToolTip(hRefresh, "重新加载模型列表");
        main.Controls.Add(hRefresh);

        hDir = new UiIconButton("folder");
        hDir.BackColor = Theme.Bg;
        hDir.Click += delegate { PickFolder(); };
        tips.SetToolTip(hDir, "选择工作目录");
        main.Controls.Add(hDir);

        hSettings = new UiIconButton("gear");
        hSettings.BackColor = Theme.Bg;
        hSettings.Click += delegate { OpenSettings(); };
        tips.SetToolTip(hSettings, "设置");
        main.Controls.Add(hSettings);

        tabs = new TabsBar();
        tabs.BackColor = Theme.Bg;
        tabs.SelectedIndexChanged += delegate { SwitchTab(); };
        main.Controls.Add(tabs);

        contentHost = new Panel();
        contentHost.BackColor = Theme.Bg;
        main.Controls.Add(contentHost);

        rtb = new TranscriptView();
        rtb.AutoFollow = config.follow_output;
        rtb.OnCopied = delegate(int n) { lblStatus.Text = "已复制 " + n + " 个字"; };
        rtb.OnBlockAction = delegate(MsgBlock b, int act) { BlockAction(b, act); };
        rtb.OnCardAction = delegate(MsgBlock b, int idx) { OpenChanges(b, idx); };
        rtb.OnBarAct = delegate(int i) { BarAction(i); };
        contentHost.Controls.Add(rtb);

        trace = new TranscriptView();
        trace.Visible = false;
        contentHost.Controls.Add(trace);

        BuildHero();
        contentHost.Controls.Add(heroPanel);
        heroPanel.BringToFront();

        BuildComposer();

        statusBar = new Panel();
        statusBar.BackColor = Theme.Bg;
        statusBar.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.LineSoft, 1f))
                e.Graphics.DrawLine(p, 0, 0, statusBar.Width, 0);
        };

        dotStatus = new Dot();
        dotStatus.DotColor = Theme.Ok;
        dotStatus.BackColor = Theme.Bg;
        statusBar.Controls.Add(dotStatus);

        lblStatus = new Label();
        lblStatus.Text = "就绪";
        lblStatus.ForeColor = Theme.FgDim;
        lblStatus.BackColor = Theme.Bg;
        lblStatus.Font = Theme.F(8.6f);
        lblStatus.AutoSize = true;
        statusBar.Controls.Add(lblStatus);

        lblStats = new Label();
        lblStats.Text = "";
        lblStats.ForeColor = Theme.FgFaint;
        lblStats.BackColor = Theme.Bg;
        lblStats.Font = Theme.F(8.6f);
        lblStats.AutoSize = true;
        lblStats.TextChanged += delegate { LayoutUi(); };
        statusBar.Controls.Add(lblStats);

        main.Controls.Add(statusBar);
    }

    void BuildHero()
    {
        heroPanel = new Panel();
        heroPanel.BackColor = Theme.Bg;

        heroMark = new LogoMark();
        heroMark.BackColor = Theme.Bg;
        heroPanel.Controls.Add(heroMark);

        heroTitle = new Label();
        heroTitle.Text = "我们应该在 Ahz Scribe 中做些什么？";
        heroTitle.Font = Theme.F(19f);
        heroTitle.ForeColor = Theme.Fg;
        heroTitle.AutoSize = false;
        heroTitle.TextAlign = ContentAlignment.MiddleCenter;
        heroTitle.BackColor = Theme.Bg;
        heroPanel.Controls.Add(heroTitle);

        heroSub = new Label();
        heroSub.Text = "输入任务后回车；写文件和执行命令会先征求你的同意";
        heroSub.Font = Theme.F(9.5f);
        heroSub.ForeColor = Theme.FgDim;
        heroSub.AutoSize = false;
        heroSub.TextAlign = ContentAlignment.MiddleCenter;
        heroSub.BackColor = Theme.Bg;
        heroPanel.Controls.Add(heroSub);

        for (int i = 0; i < heroTexts.Length; i++)
        {
            string t = heroTexts[i];
            UiButton b = new UiButton();
            b.Text = t;
            b.Radius = 15;
            b.Font = Theme.F(9f);
            b.FillColor = Color.White;
            b.HoverColor = Theme.Side;
            b.BorderColor = Theme.Line;
            b.Padding = new Padding(14, 0, 14, 0);
            b.Click += delegate
            {
                txtInput.Text = t;
                txtInput.SelectionStart = txtInput.TextLength;
                txtInput.Focus();
                UpdateSendButton();
            };
            heroPanel.Controls.Add(b);
            heroChips.Add(b);
        }
    }

    void BuildComposer()
    {
        composerHost = new Panel();
        composerHost.BackColor = Theme.Bg;
        main.Controls.Add(composerHost);

        inputCard = new RoundedPanel();
        inputCard.Radius = 16;
        inputCard.Shadow = true;
        inputCard.Inset = 5;
        inputCard.Fill = Color.White;
        inputCard.BorderColor = Theme.Line;
        inputCard.BackColor = Theme.Bg;
        composerHost.Controls.Add(inputCard);

        txtInput = new HintTextBox();
        txtInput.Hint = PlaceholderText;
        txtInput.Multiline = true;
        txtInput.BorderStyle = BorderStyle.None;
        txtInput.BackColor = Color.White;
        txtInput.ForeColor = Theme.Fg;
        txtInput.Font = Theme.F(10.5f);
        txtInput.ScrollBars = ScrollBars.None;
        txtInput.TextChanged += delegate { UpdateSendButton(); };
        txtInput.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                SendMessage();
            }
        };
        inputCard.Controls.Add(txtInput);

        btnPlus = new UiDrop();
        btnPlus.KeepText = true;
        btnPlus.ShowCaret = false;
        btnPlus.Icon = "plus";
        btnPlus.IconSize = 16;
        btnPlus.FillColor = Theme.Chip;
        btnPlus.HoverColor = Theme.ChipHover;
        btnPlus.IconColor = Theme.FgMid;
        btnPlus.Radius = 9;
        btnPlus.Text = "";
        btnPlus.Items.Add("选择工作目录…");
        btnPlus.Items.Add("清空当前对话");
        btnPlus.Items.Add("设置…");
        btnPlus.ItemChosen += delegate(object s, EventArgs e) { MenuAction(Convert.ToString(s)); };
        tips.SetToolTip(btnPlus, "更多");
        inputCard.Controls.Add(btnPlus);

        permChip = new UiDrop();
        permChip.Icon = "lock";
        permChip.IconSize = 13;
        permChip.IconGap = 6;
        permChip.FillColor = Theme.Chip;
        permChip.HoverColor = Theme.ChipHover;
        permChip.TextColor = Theme.FgMid;
        permChip.Radius = 9;
        permChip.Font = Theme.F(9f);
        permChip.Padding = new Padding(10, 0, 10, 0);
        permChip.ShowCaret = true;
        permChip.KeepText = true;                  // 文字自己刷，走 UpdatePermChip()
        permChip.Text = Perm.Label(config.permission);
        permChip.Click += delegate { TogglePermPopover(); };   // 自绘一列三档（Codex 那样）
        inputCard.Controls.Add(permChip);

        // 计划模式开关：开着只调研、只出计划，框架层会拦下动手类工具
        planChip = new UiDrop();
        planChip.Icon = "eye";
        planChip.IconSize = 13;
        planChip.IconGap = 6;
        planChip.FillColor = Theme.Chip;
        planChip.HoverColor = Theme.ChipHover;
        planChip.TextColor = Theme.FgDim;
        planChip.Radius = 9;
        planChip.Font = Theme.F(9f);
        planChip.Padding = new Padding(10, 0, 10, 0);
        planChip.KeepText = true;
        planChip.Click += delegate { TogglePlanMode(); };
        tips.SetToolTip(planChip, "计划模式：只调研、只出计划，不改文件（点一下切换）");
        inputCard.Controls.Add(planChip);
        UpdatePlanChip();

        cmbModel = new ModelChip();
        cmbModel.SelectedIndexChanged += delegate { OnModelChanged(); };
        cmbModel.ChipClicked += delegate { ToggleModelPopover(); };    // 点它开自绘浮层（不再用 ContextMenuStrip）
        inputCard.Controls.Add(cmbModel);

        btnSend = new UiButton();
        btnSend.Icon = "send";
        btnSend.IconSize = 18;
        btnSend.IconColor = Color.White;
        btnSend.IconWeight = 1.9f;
        btnSend.Text = "";
        btnSend.Radius = 20;
        btnSend.FillColor = Theme.AccentIdle;
        btnSend.HoverColor = Theme.Accent;
        btnSend.BorderColor = Theme.Clear;
        btnSend.Click += delegate
        {
            if (running)
            {
                if (runner != null) runner.Cancel();
                lblStatus.Text = "正在停止…";
            }
            else SendMessage();
        };
        inputCard.Controls.Add(btnSend);
    }

    // ---------- 布局 ----------

    // 逻辑像素 -> 物理像素
    int D(float v) { return Theme.Px(v); }

    void LayoutUi()
    {
        if (side == null || ClientSize.Width < 200) return;
        int W = ClientSize.Width, H = ClientSize.Height;
        side.SetBounds(0, D(40), D(288), H - D(40));
        main.SetBounds(D(288), D(40), W - D(288), H - D(40));

        int mx = D(16);
        UiDrop[] menus = new UiDrop[] { mApp, mEdit, mView, mHelp };
        foreach (UiDrop b in menus)
        {
            b.SetBounds(mx, D(7), b.PreferredWidth() + D(6), D(26));
            mx = b.Right + D(2);
        }

        // 右上角三个窗口按钮（贴边，和 Windows 11 一致）
        int wbW = D(46), wbH = D(40);
        btnClose.SetBounds(W - wbW, 0, wbW, wbH);
        btnMax.SetBounds(W - wbW * 2, 0, wbW, wbH);
        btnMin.SetBounds(W - wbW * 3, 0, wbW, wbH);

        // ---- 侧栏 ----
        mark.SetBounds(D(18), D(16), D(26), D(26));
        lblLogo.Location = new Point(D(52), D(19));
        int pw = Ui.TextWidth(pill.Text, pill.Font) + D(14);
        pill.SetBounds(D(52) + Ui.TextWidth(lblLogo.Text, lblLogo.Font) + D(10), D(22), pw, D(17));
        btnNew.SetBounds(D(14), D(62), D(260), D(40));
        btnPlugins.SetBounds(D(14), D(112), D(260), D(36));

        lblSec.Location = new Point(D(20), D(166));
        btnNewWs.SetBounds(D(248), D(158), D(28), D(26));
        btnSort.SetBounds(D(218), D(158), D(28), D(26));
        btnSearch.SetBounds(D(188), D(158), D(28), D(26));
        searchBox.SetBounds(D(14), D(156), D(260), D(30));

        int listTop = D(196);
        int listBottom = side.Height - D(96);
        list.SetBounds(0, listTop, D(288), Math.Max(D(40), listBottom - listTop));

        avatar.SetBounds(D(16), side.Height - D(56), D(24), D(24));
        lblUser.SetBounds(D(48), side.Height - D(54), D(288) - D(48) - D(46), D(20));
        btnUserDir.SetBounds(D(248), side.Height - D(56), D(28), D(26));
        dot.SetBounds(D(18), side.Height - D(27), D(7), D(7));
        lblFoot.SetBounds(D(32), side.Height - D(32), D(288) - D(48), D(18));

        // ---- 顶栏 ----
        int hy = D(13);
        lblTitle.Location = new Point(D(24), hy + D(1));
        modeChip.SetBounds(lblTitle.Right + D(12), hy,
            D(30) + Ui.TextWidth(modeChip.Text, modeChip.Font) + D(20), D(28));

        int rx = main.Width - D(18);
        hSettings.SetBounds(rx - D(28), hy, D(28), D(28)); rx -= D(34);
        hDir.SetBounds(rx - D(28), hy, D(28), D(28)); rx -= D(34);
        hRefresh.SetBounds(rx - D(28), hy, D(28), D(28)); rx -= D(34);
        hNew.SetBounds(rx - D(28), hy, D(28), D(28)); rx -= D(40);
        lblBadge.Location = new Point(Math.Max(D(24), rx - lblBadge.Width), hy + D(6));

        tabs.SetBounds(0, D(54), main.Width, D(38));
        int composerH = D(150);
        int statusH = D(28);
        contentHost.SetBounds(0, D(92), main.Width, Math.Max(D(60), main.Height - D(92) - composerH - statusH));
        composerHost.SetBounds(0, main.Height - composerH - statusH, main.Width, composerH);
        statusBar.SetBounds(0, main.Height - statusH, main.Width, statusH);

        int colW = Math.Min(D(1040), Math.Max(D(320), contentHost.Width - D(80)));
        int colX = (contentHost.Width - colW) / 2;
        rtb.SetBounds(colX, D(8), colW, Math.Max(D(30), contentHost.Height - D(16)));
        trace.SetBounds(colX, D(8), colW, Math.Max(D(30), contentHost.Height - D(16)));
        heroPanel.SetBounds(0, 0, contentHost.Width, contentHost.Height);

        int heroW = Math.Min(D(760), Math.Max(D(320), heroPanel.Width - D(60)));
        int heroX = (heroPanel.Width - heroW) / 2;
        int heroY = Math.Max(D(16), heroPanel.Height / 3 - D(40));
        heroMark.SetBounds(heroX + heroW / 2 - D(23), heroY, D(46), D(46));
        heroTitle.SetBounds(heroX, heroY + D(64), heroW, D(36));
        heroSub.SetBounds(heroX, heroY + D(104), heroW, D(24));
        int chipsY = heroY + D(148);
        int totalW = 0;
        for (int i = 0; i < heroChips.Count; i++)
        {
            heroChips[i].Width = heroChips[i].PreferredWidth();
            totalW += heroChips[i].Width + (i > 0 ? D(8) : 0);
        }
        int cx = heroX + Math.Max(0, (heroW - totalW) / 2);
        for (int i = 0; i < heroChips.Count; i++)
        {
            heroChips[i].SetBounds(cx, chipsY, heroChips[i].Width, D(30));
            cx += heroChips[i].Width + D(8);
        }

        // ---- 输入卡 ----
        int cardW = Math.Min(D(930), Math.Max(D(360), composerHost.Width - D(80)));
        int inset = inputCard.InsetPx;
        inputCard.SetBounds((composerHost.Width - cardW) / 2, D(10), cardW, composerH - D(20));
        int cardH = inputCard.Height;
        int ix = inset + D(16), iy = inset + D(12);
        int rowH = D(30);
        int rowY = cardH - inset - D(12) - rowH;
        txtInput.SetBounds(ix, iy - D(2), cardW - inset * 2 - D(32), Math.Max(D(24), rowY - iy - D(6)));

        btnPlus.SetBounds(ix, rowY, D(32), rowH);
        permChip.SetBounds(ix + D(38), rowY, permChip.PreferredWidth() + D(8), rowH);
        planChip.SetBounds(ix + D(38) + permChip.Width + D(6), rowY, planChip.PreferredWidth() + D(8), rowH);
        int rx2 = cardW - inset - D(14);
        btnSend.SetBounds(rx2 - D(38), rowY - D(5), D(38), D(38));
        rx2 -= D(48);
        cmbModel.SetBounds(rx2 - cmbModel.PreferredWidth() - D(8), rowY, cmbModel.PreferredWidth() + D(8), rowH);

        // ---- 状态条 ----
        dotStatus.SetBounds(D(18), D(11), D(7), D(7));
        lblStatus.Location = new Point(D(32), D(6));
        lblStats.Location = new Point(Math.Max(D(200), statusBar.Width - D(18) - lblStats.Width), D(6));
    }

    // ============================ 菜单动作 ============================

    void MenuAction(string text)
    {
        switch (text)
        {
            case "新建会话": NewSession(); break;
            case "选择工作目录…": PickFolder(); break;
            case "插件…": OpenPlugins(); break;
        case "导出交付记录…": ExportDelivery(); break;
        case "重启并升级（用 .new）…": ConfirmUpgrade(); break;
        case "回滚这次改动（用快照）…": ConfirmRollback(); break;
            case "设置…": OpenSettings(); break;
            case "退出": Close(); break;
            case "复制全部对话":
                {
                    string all = rtb.AllText();
                    if (all.Length > 0) { try { Clipboard.SetText(all); } catch { } }
                    break;
                }
            case "清空当前对话": ClearCurrentChat(true); break;
            case "放大字号": AdjustZoom(1f); break;
            case "缩小字号": AdjustZoom(-1f); break;
            case "重置字号": AdjustZoom(0f); break;
            case "显示对话": tabs.SelectedIndex = 0; SwitchTab(); break;
            case "显示轨迹": tabs.SelectedIndex = 1; SwitchTab(); break;
            case "关于 Ahz Scribe": OpenSettings(); break;
            case "使用说明":
                MessageBox.Show(this,
                    "· 在底部输入框写任务，Enter 发送，Shift+Enter 换行\n"
                    + "· 写文件 / 改文件 / 执行命令前会弹窗征求同意\n"
                    + "· 顶部「对话 / 轨迹」可以在正文和工具步骤之间切换\n"
                    + "· 底部左侧可以切换「标准模式 / 完全权限」\n"
                    + "· 模型和提供商在「设置」里管理（本地 Ollama 或云端接口）",
                    "Ahz Scribe 使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
        }
    }

    void SwitchTab()
    {
        bool traceMode = tabs.SelectedIndex == 1;
        trace.Visible = traceMode;
        rtb.Visible = !traceMode;
        // 对话区已经有东西了就不能再让空状态大标题盖上来（它会整块挡住对话区 = 一次"闪屏"）
        heroPanel.Visible = !traceMode && !HasMessages() && !rtb.HasContent;
        if (traceMode) trace.BringToFront();
        else if (heroPanel.Visible) heroPanel.BringToFront();
        // 切页时只在"本来就在跟最新"的情况下滚到底：用户往上翻历史时不会被拽走
        if (traceMode) { if (trace.Follow) trace.ScrollToEnd(); }
        else if (rtb.Follow) rtb.ScrollToEnd();
    }

    bool HasMessages()
    {
        // 实时状态优先：会话对象（sessions.json 里那份）只在回合结束时才更新，
        // 光看它会把手头正在进行的对话判成"空"，于是空状态大标题又盖回对话区。
        if (runner != null && runner.Messages != null && runner.Messages.Count > 1) return true;
        Session s = FindSession(currentSessionId);
        return s != null && s.messages != null && s.messages.Count > 0;
    }

    void ToggleSearch()
    {
        if (searchOpen) { CloseSearch(); return; }
        searchOpen = true;
        btnSearch.Active = true;
        searchBox.Visible = true;
        searchBox.BringToFront();
        txtSearch.Focus();
    }

    void CloseSearch()
    {
        searchOpen = false;
        btnSearch.Active = false;
        searchBox.Visible = false;
        txtSearch.Text = "";
        filter = "";
        RefreshSessionList();
    }

    // 权限档改了：落盘 + 三个地方（顶栏 / 输入条 / 设置页）一起刷新
    void SetPermMode(bool autoYes, bool save)
    {
        SetPerm(autoYes ? Perm.Full : Perm.Workspace, save);
    }

    void SetPerm(string level, bool save)
    {
        config.permission = level;
        config.auto_yes = Perm.AutoYes(level);     // 老字段跟着同步，别处的判断不用全改
        if (save) config.Save();
        UpdatePermChip();
        UpdatePlanChip();
        if (chkAutoYes != null) chkAutoYes.Checked = config.auto_yes;
        // 只读档立刻生效：重建 ToolContext，别等下一轮（用户切了档就该马上管用）
        try
        {
            if (runner != null)
            {
                runner.ResetContext();       // 越权申请通道也在里面装好，别只重建 Ctx 漏掉它
            }
        }
        catch (Exception ex) { Log.Swallow(ex, "切换权限档"); }
        if (Perm.IsReadOnly(level))
            lblStatus.Text = "权限档：仅可查看（写文件和执行命令会被拒绝）";
        dotStatus.DotColor = config.auto_yes ? Theme.Warn : Theme.Ok;
        dotStatus.Invalidate();
    }

    // 输入条 / 顶栏上权限 chip 的文字
    void UpdatePermChip()
    {
        string txt = Perm.Label(config.permission);
        if (permChip != null && permChip.Text != txt)
        {
            permChip.Text = txt;
            permChip.Invalidate();
            LayoutUi();
        }
        if (modeChip != null && Convert.ToString(modeChip.SelectedItem) != txt)
        {
            modeChip.SelectedItem = txt;
            modeChip.Invalidate();
        }
    }

    // ---------------- 权限浮层（自绘，一列三档） ----------------

    // 输入条上「计划模式」chip 的文字/配色（开着时高亮，免得用户忘了自己还开着）
    void UpdatePlanChip()
    {
        if (planChip == null) return;
        planChip.Text = config.plan_mode ? "计划模式" : "直接动手";
        planChip.TextColor = config.plan_mode ? Theme.Accent : Theme.FgDim;
        planChip.FillColor = config.plan_mode ? Theme.AccentSoft : Theme.Chip;
        planChip.Invalidate();
    }

    void TogglePlanMode()
    {
        config.plan_mode = !config.plan_mode;
        config.Save();
        UpdatePlanChip();
        try { if (runner != null) runner.RefreshSystemPrompt(); }
        catch (Exception ex) { Log.Swallow(ex, "切换计划模式"); }
        lblStatus.Text = config.plan_mode
            ? "计划模式已开：只调研、只出计划，不改文件（再点一下关掉）"
            : "计划模式已关：它可以动手改文件了";
        dotStatus.DotColor = config.plan_mode ? Theme.Accent : (config.auto_yes ? Theme.Warn : Theme.Ok);
        dotStatus.Invalidate();
    }

    // 「文件」菜单里的导出交付记录
    void ExportDelivery()
    {
        if (runner == null) { lblStatus.Text = "还没有会话可以导出"; return; }
        try
        {
            string path = Deliver.Export(runner, config.cwd);
            lblStatus.Text = "已导出交付记录：" + path;
            MessageBox.Show(this,
                "已导出到：\n" + path + "\n\n内容：用户要求 / 改动的文件（带行数差）/ 跑过的命令与结果 / 结论。",
                "导出交付记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Swallow(ex, "导出交付记录");
            MessageBox.Show(this, "导出失败：\n" + ex.Message, "导出交付记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 「文件」菜单里的重启并升级：退出 → 换 .new → 自动启动
    void ConfirmUpgrade()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        List<string> pending = Upgrader.PendingNewFiles(exeDir);
        if (pending.Count == 0)
        {
            MessageBox.Show(this,
                "没有待升级的 .new。\n\n先跑一遍  cmd /c build.cmd —— 窗口版正在运行时它会自动产出 .new，再来点这里。",
                "重启并升级", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this,
            "将做三件事：\n1) 退出当前程序（会保存会话）\n2) 用这些文件替换正式版：" + string.Join("、", pending.ToArray())
            + "\n3) 替换完自动重新启动\n\n现在继续？",
            "重启并升级", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        string msg;
        if (Upgrader.Schedule(exeDir, true, out msg)) Close();
        else MessageBox.Show(this, msg, "重启并升级", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // 「文件」菜单里的回滚：把文件恢复到改动前（最近一条快照）
    void ConfirmRollback()
    {
        if (runner == null) { lblStatus.Text = "还没有会话可以回滚"; return; }
        if (MessageBox.Show(this,
            "把工作目录里的文件恢复到**改动前**（用最近一条 .snapshots 快照）？\n\n"
            + "适合场景：刚改完发现不对、或自我改造验证没过。\n回滚之后如果已经 build 出新版，"
            + "要再跑一次 build 才能让二进制和源码一致。",
            "回滚这次改动", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        string msg = Upgrader.RollbackTool(runner.Ctx, new Dictionary<string, object>());
        lblStatus.Text = "回滚完成";
        MessageBox.Show(this, msg, "回滚这次改动", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // 输入条上那个权限 chip 的点击入口（顶栏那个走 TogglePermPopover(Control)）
    void TogglePermPopover() { TogglePermPopover(permChip); }

    void TogglePermPopover(Control anchor)
    {
        if (permPop != null && permPop.Visible) { ClosePermPopover(); return; }
        PermPopover p = new PermPopover(Perm.Index(config.permission));
        p.Chosen += delegate
        {
            string lv = Perm.ValueAt(p.PickedIndex);
            ClosePermPopover();
            SetPerm(lv, true);
            Log.Audit("权限档 → " + Perm.Label(lv));
        };
        permPop = p;
        p.ShowAbove(anchor == null ? (Control)permChip : anchor);
        p.ShowTick = Environment.TickCount;
    }

    void ClosePermPopover()
    {
        PermPopover p = permPop;
        permPop = null;
        if (p == null) return;
        try { p.Hide(); p.Dispose(); }
        catch (Exception ex) { Log.Swallow(ex, "关闭权限浮层"); }
    }

    // ============================ 模型 ============================

    // ---------- 输入条上那张「模型 / 推理等级」卡片 ----------

    static ContextMenuStrip SkinMenu()
    {
        ContextMenuStrip m = new ContextMenuStrip();
        m.ShowImageMargin = false;
        m.BackColor = Color.White;
        m.ForeColor = Theme.Fg;
        m.Font = Theme.F(9.5f);
        m.Padding = new Padding(Theme.Px(4), Theme.Px(5), Theme.Px(4), Theme.Px(5));
        m.DropShadowEnabled = true;
        m.Renderer = new MenuSkin();
        return m;
    }


    // 等级改了：落盘 + 刷新输入条上的文字和底部状态
    void SetEffort(string e, bool save)
    {
        if (config.effort == e && save) return;
        config.effort = e;
        // 选了具体等级就意味着要思考：顺手把思考打开，免得出现"选了高却没思考"这种怪状态
        if (Effort.IsSet(e) && !config.think)
        {
            config.think = true;
            if (chkThink != null) chkThink.Checked = true;
        }
        if (save) config.Save();
        UpdateModelChip();
        ApplyModelInfo();
        if (modelPop != null) modelPop.RefreshEffort(config.effort);
        // 别再往对话区写一行了：一直拖滑杆能把对话区刷屏（用户实测刷了十几行）。
        // 这种"设置类"动作进审计日志，要看就去「设置 → 运行日志」。
        if (save) Log.Audit("推理强度 → " + Effort.Label(config.effort)
            + (Effort.IsSet(config.effort)
                ? "（请求参数：" + EffortRequestText(config.effort) + "）"
                : "（这个模型不支持，不额外传参数）"));
    }

    // 档位落到请求里到底是什么（写进审计日志，好验证"真的生效了"）
    string EffortRequestText(string e)
    {
        Provider p = config.Current();
        if (p != null && p.type == "ollama") return "think=" + e;
        return "reasoning_effort=" + e;
    }

    // 当前模型能不能调推理强度（见 Effort.Supported）
    bool EffortSupported()
    {
        Provider p = config.Current();
        if (p == null) return false;
        string tv = (p.type == "ollama" && runner != null) ? runner.ThinkingValues : "";
        return Effort.Supported(p.type, config.model, tv);
    }

    // 输入条上那个 chip 的文字：「厂商 / 模型 · 档位」（Codex 风格）
    void UpdateModelChip()
    {
        if (cmbModel == null || cmbModel.SelectedItem == null) return;
        string t = Convert.ToString(cmbModel.SelectedItem) + " · " + Effort.Label(config.effort);
        if (cmbModel.Text == t) return;
        cmbModel.Text = t;
        cmbModel.Invalidate();
        LayoutUi();          // 文字变长了，chip 宽度要重算
    }

    void LoadModels()
    {
        suppressModelChange = true;
        cmbModel.Items.Clear();
        Provider cur = config.Current();
        int selected = -1;
        foreach (Provider p in config.providers)
        {
            List<string> models = p.models;
            if (models == null) models = new List<string>();
            if (models.Count == 0 && p.id == cur.id && !string.IsNullOrEmpty(config.model))
                models = new List<string>(new string[] { config.model });
            foreach (string m in models)
            {
                ModelItem item = new ModelItem();
                item.ProviderId = p.id;
                item.ProviderName = p.name;
                item.Model = m;
                cmbModel.Items.Add(item);
                if (p.id == cur.id && m == config.model) selected = cmbModel.Items.Count - 1;
            }
        }
        if (cmbModel.Items.Count == 0)
            Write(OutKind.Error, "! 还没有可用的模型，点「设置」添加提供商和模型。\n");
        if (selected < 0 && cmbModel.Items.Count > 0) selected = 0;
        cmbModel.SelectedIndex = selected;
        suppressModelChange = false;
        UpdateModelChip();          // chip 上要带上当前的推理等级
        LayoutUi();
    }

    void OnModelChanged()
    {
        if (suppressModelChange) return;
        ModelItem item = cmbModel.SelectedItem as ModelItem;
        if (item == null) return;
        if (item.ProviderId == config.provider && item.Model == config.model) return;
        config.provider = item.ProviderId;
        config.model = item.Model;
        config.Save();
        runner.RebuildLlm();
        ApplyModelInfo();
        Write(OutKind.Info, "· 已切换模型：" + item.ToString() + "\n");
    }

    void ApplyModelInfo()
    {
        UpdateModelChip();        // 输入条上那个 chip 始终显示"模型 · 等级"
        Provider p = config.Current();
        try
        {
            runner.ProbeModel();
        }
        catch (Exception ex)
        {
            // 后端连不上（比如 Ollama 没启动）不应该让程序打不开，只提示一下
            Write(OutKind.Error, "× 连接不上「" + p.name + "」：" + ex.Message + "\n");
            lblStats.Text = p.name + " / " + config.model + " · 连接失败";
            if (lblBadge != null) lblBadge.Text = p.name + " · " + config.model;
            return;
        }
        string warn = runner.Warning();
        if (warn != null) Write(OutKind.Error, "! " + warn + "\n");
        // 输入条上不再有"思考"开关，这里就说清当前到底是什么状态（开关在「设置 → 通用设置」）
        string thinkTxt = !config.think ? "已关" : (runner.CanDisableThink ? "默认开" : "关不掉");
        string toolTxt = runner.Capabilities.Contains("tools") ? "支持" : "不支持";
        lblStats.Text = p.name + " / " + config.model + " · 思考" + thinkTxt
            + " · 工具" + toolTxt + " · ctx " + config.num_ctx;
        if (Effort.IsSet(config.effort)) lblStats.Text += " · 推理" + Effort.Label(config.effort);
        if (lblBadge != null) lblBadge.Text = p.name + " · " + config.model;
    }

    void UpdateSendButton()
    {
        if (btnSend == null) return;
        if (running)
        {
            btnSend.Icon = "stop";
            btnSend.IconSize = 15;
            btnSend.FillColor = Theme.Fg;
            btnSend.HoverColor = Color.Black;
        }
        else
        {
            btnSend.Icon = "send";
            btnSend.IconSize = 18;
            bool has = txtInput != null && txtInput.Text.Trim().Length > 0;
            btnSend.FillColor = has ? Theme.Accent : Theme.AccentIdle;
            btnSend.HoverColor = has ? Theme.AccentHover : Theme.Accent;
        }
        btnSend.Invalidate();
    }

    // ============================ 会话 ============================

    void LoadSessions()
    {
        sessionStore = SessionStore.Load();
        RefreshSessionList();
        if (sessionStore.sessions.Count > 0) LoadSessionById(sessionStore.sessions[0].id);
        else { currentSessionId = ""; ShowHero(true); }
    }

    static string ProjectName(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "（未知项目）";
        string[] parts = cwd.TrimEnd('\\').Split('\\');
        return parts.Length > 0 && parts[parts.Length - 1].Length > 0 ? parts[parts.Length - 1] : cwd;
    }

    static string RelTime(string updated)
    {
        if (string.IsNullOrEmpty(updated)) return "";
        DateTime t;
        if (!DateTime.TryParse(updated, out t)) return updated;
        TimeSpan d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalMinutes < 60) return ((int)d.TotalMinutes) + "分钟";
        if (d.TotalHours < 24) return ((int)d.TotalHours) + "小时";
        if (d.TotalDays < 30) return ((int)d.TotalDays) + "天";
        return t.ToString("M月d日");
    }

    Session FindSession(string id)
    {
        if (sessionStore == null || id == null) return null;
        foreach (Session s in sessionStore.sessions) if (s.id == id) return s;
        return null;
    }

    void SaveStore()
    {
        if (sessionStore == null) return;
        while (sessionStore.sessions.Count > 200) sessionStore.sessions.RemoveAt(sessionStore.sessions.Count - 1);
        sessionStore.Save();
    }

    void NewSession()
    {
        SaveCurrentSession();
        Session s = new Session();
        s.id = Guid.NewGuid().ToString("N").Substring(0, 12);
        s.title = "新会话";
        s.cwd = config.cwd;
        s.provider = config.provider;
        s.model = config.model;
        s.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        s.messages = new List<Dictionary<string, object>>();
        if (sessionStore == null) sessionStore = new SessionStore();
        sessionStore.sessions.Insert(0, s);
        currentSessionId = s.id;
        runner = new AgentRunner(config, this);
        rtb.ClearAll();
        trace.ClearAll();
        stepNo = 0;
        tabs.SelectedIndex = 0;
        SwitchTab();
        ShowHero(true);
        SaveStore();
        RefreshSessionList();
    }

    void SaveCurrentSession()
    {
        Session s = FindSession(currentSessionId);
        if (s == null || runner == null) return;
        s.messages = new List<Dictionary<string, object>>(runner.Messages);
        s.cwd = config.cwd;
        s.provider = config.provider;
        s.model = config.model;
        s.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        foreach (Dictionary<string, object> m in s.messages)
        {
            if (Json.Str(m, "role", "") != "user") continue;
            string txt = Json.Str(m, "content", "");
            if (txt.Length > 0) { s.title = txt.Length > 22 ? txt.Substring(0, 22) + "…" : txt; break; }
        }
        SaveStore();
    }

    void LoadSessionById(string id)
    {
        Session s = FindSession(id);
        if (s == null) return;
        if (!string.IsNullOrEmpty(currentSessionId) && currentSessionId != id) SaveCurrentSession();
        currentSessionId = id;
        if (!string.IsNullOrEmpty(s.cwd) && Directory.Exists(s.cwd)) config.cwd = s.cwd;
        if (!string.IsNullOrEmpty(s.provider)) config.provider = s.provider;
        if (!string.IsNullOrEmpty(s.model)) config.model = s.model;
        runner = new AgentRunner(config, this);
        runner.Messages.Clear();
        runner.Messages.Add(AgentRunner.Msg("system", ""));
        if (s.messages != null) foreach (Dictionary<string, object> m in s.messages) runner.Messages.Add(m);
        runner.RefreshSystemPrompt();      // 用当前的系统提示（含插件技能）覆盖历史里存的那份，顺便去掉重复的 system
        rtb.ClearAll();
        trace.ClearAll();
        stepNo = 0;
        RenderHistory();
        ShowHero(s.messages == null || s.messages.Count == 0);
        if (txtCwd != null) txtCwd.Text = config.cwd;
        lblFoot.Text = ProjectName(config.cwd) + "  ·  " + config.cwd;
        LoadModels();
        ApplyModelInfo();
        RefreshSessionList();
    }

    void RenderHistory()
    {
        for (int i = 1; i < runner.Messages.Count; i++)
        {
            Dictionary<string, object> m = runner.Messages[i];
            string role = Json.Str(m, "role", "");
            string content = Json.Str(m, "content", "");
            if (role == "user")
            {
                Write(OutKind.User, "› " + content + "\n");
            }
            else if (role == "assistant")
            {
                object[] calls = Json.Arr(Json.Get(m, "tool_calls"));
                if (calls != null)
                    foreach (object c in calls)
                    {
                        Dictionary<string, object> cm = Json.Map(c);
                        if (cm != null) Write(OutKind.ToolCall, "  ● " + Json.Str(cm, "name", "?") + "\n");
                    }
                if (content.Length > 0) Write(OutKind.Assistant, "\n● " + content + "\n");
            }
            else if (role == "tool")
            {
                Write(OutKind.ToolResult, "  └ " + Txt.FirstLine(content) + "\n");
            }
            // 历史里的每一条也要能"分支到新聊天"，并且带上当时的时间
            rtb.FlushNow();
            MsgBlock hb = rtb.LastBlock;
            if (hb != null)
            {
                hb.MsgIdx = i;
                long ts = Json.Long(m, "ts", 0);
                hb.Stamp = ts > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime() : (DateTime?)null;
            }
        }
        rtb.FlushNow();      // 历史不要等攒批：切会话立刻就能看到
        rtb.Follow = true;
        rtb.ScrollToEnd();   // 打开一个会话先看到最新的几条（跟聊天软件一致）
    }

    // 消息块上的两个按钮：复制这条 / 从这条分支到新会话
    void BlockAction(MsgBlock b, int act)
    {
        if (b == null) return;
        if (act == MsgBlock.ActCopy)
        {
            string t = b.Body;
            if (t.Length == 0) return;
            bool ok = false;
            try { Clipboard.SetText(t); ok = true; } catch { }
            // 不管成功失败都给个反馈：剪贴板被别的程序占着时也能知道
            lblStatus.Text = ok ? ("已复制 " + t.Length + " 个字") : "复制失败（剪贴板被别的程序占用了）";
            rtb.ShowFlash(b, ok ? "已复制" : "复制失败", MsgBlock.ActCopy);   // 按钮上方也冒一个气泡
            return;
        }
        if (act == MsgBlock.ActBranch) ForkFromBlock(b);
    }

    // 以这一条为结尾，复制出一个新会话（原来的会话不动）
    // 选中小条上的两个动作：复制 / 填到输入框
    void BarAction(int i)
    {
        string sel = rtb.BuildSelectionText();
        if (sel.Trim().Length == 0) return;
        if (i == 0) { rtb.CopySelection(); return; }
        string add = sel.Trim();
        if (add.Length > 4000) add = add.Substring(0, 4000) + "\n…（太长已截断）";
        string cur = txtInput.Text;
        txtInput.Text = (cur.Trim().Length > 0 ? cur.TrimEnd() + "\n\n" : "") + add + "\n";
        txtInput.SelectionStart = txtInput.TextLength;
        txtInput.Focus();
        rtb.ClearSelection();
        lblStatus.Text = "已把选中的 " + add.Length + " 个字填到输入框，可以直接发给我";
    }

    void OpenChanges(MsgBlock b, int fileIdx)
    {
        if (b == null) return;
        if (fileIdx == -3) { UndoChanges(b); return; }
        List<FileChange> list = Changes.Get(b.CardIndex);
        if (list == null || list.Count == 0)
        {
            lblStatus.Text = "这一轮的改动记录已不在内存里（程序重启后不保留）";
            return;
        }
        using (ChangesForm dlg = new ChangesForm(list, fileIdx)) dlg.ShowDialog(this);
    }

    // 「撤销」：把这一轮改动过的文件恢复成改动前的内容（新文件则删掉）
    void UndoChanges(MsgBlock b)
    {
        List<FileChange> list = Changes.Get(b.CardIndex);
        if (list == null || list.Count == 0) { lblStatus.Text = "这一轮的改动记录已不在内存里"; return; }
        if (MessageBox.Show(this, "把这一轮改的 " + list.Count + " 个文件恢复原样？\n\n"
                + "新文件会被删除，改动过的文件会写回改动前的内容。", "撤销这一轮的改动",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        int ok, fail;
        Changes.RestoreAll(list, out ok, out fail);
        b.CardUndone = true;
        b.Invalidate();
        lblStatus.Text = fail == 0
            ? ("已撤销 " + ok + " 个文件的改动（模型不知道这件事，下轮它会看到旧内容）")
            : ("撤销 " + ok + " 个成功，" + fail + " 个失败（可能被占用）");
        Write(OutKind.Info, "· 用户撤销了上一轮的 " + ok + " 个文件改动" + (fail > 0 ? "（" + fail + " 个失败）" : "") + "\n");
    }

    void ForkFromBlock(MsgBlock b)
    {
        if (runner == null || b == null) return;
        if (running)
        {
            lblStatus.Text = "正在生成，等这一轮结束再分支";
            return;
        }
        int upTo = b.MsgIdx >= 0 ? b.MsgIdx : runner.Messages.Count - 1;
        if (upTo < 0 || upTo >= runner.Messages.Count) upTo = runner.Messages.Count - 1;
        if (upTo <= 0) { lblStatus.Text = "这条还没有可分支的内容"; return; }
        SaveCurrentSession();
        Session src = FindSession(currentSessionId);
        string baseTitle = src != null && src.title != null && src.title.Length > 0 ? src.title : "新会话";
        Session ns = new Session();
        ns.id = Guid.NewGuid().ToString("N").Substring(0, 12);
        ns.title = baseTitle + " · 分支";
        ns.cwd = config.cwd;
        ns.provider = config.provider;
        ns.model = config.model;
        ns.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        ns.messages = new List<Dictionary<string, object>>();
        for (int i = 0; i <= upTo; i++) ns.messages.Add(runner.Messages[i]);
        if (sessionStore == null) sessionStore = new SessionStore();
        sessionStore.sessions.Insert(0, ns);
        SaveStore();
        LoadSessionById(ns.id);
        Write(OutKind.Info, "· 已从「" + baseTitle + "」分支出新会话，保留到这里为止的 " + (ns.messages.Count - 1) + " 条消息\n");
        lblStatus.Text = "已分支到新会话";
    }

    void ShowHero(bool show)
    {
        if (heroPanel == null) return;
        // 只要已经有过用户消息，空状态就绝对不能再显示（否则会盖住对话区）
        bool hasUser = false;
        if (runner != null)
        {
            for (int i = 0; i < runner.Messages.Count; i++)
            {
                if (Json.Str(runner.Messages[i], "role", "") == "user") { hasUser = true; break; }
            }
        }
        heroPanel.Visible = show && !hasUser && tabs.SelectedIndex == 0;
        if (heroPanel.Visible) heroPanel.BringToFront();
        RefreshSessionList();
    }

    void ClearCurrentChat(bool unused)
    {
        runner = new AgentRunner(config, this);
        rtb.ClearAll();
        trace.ClearAll();
        stepNo = 0;
        ShowHero(true);
    }

    void PickFolder()
    {
        FolderBrowserDialog dlg = new FolderBrowserDialog();
        dlg.Description = "选择工作目录（agent 只能在这里面读写）";
        if (Directory.Exists(config.cwd)) dlg.SelectedPath = config.cwd;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        config.cwd = dlg.SelectedPath;
        config.Save();
        if (txtCwd != null) txtCwd.Text = config.cwd;
        runner.SetWorkingDir(config.cwd);
        NewSession();
    }

    void AdjustZoom(float delta)
    {
        if (delta == 0f) zoom = 1f;
        else zoom = Math.Max(0.7f, Math.Min(1.9f, zoom + delta * 0.1f));
        ApplyTranscriptFonts();
        rtb.ScrollToEnd();
    }

    void RefreshSessionList()
    {
        if (list == null || sessionStore == null) return;

        List<Session> items = new List<Session>(sessionStore.sessions);
        items.Sort(delegate(Session a, Session b)
        {
            string x = a.updated == null ? "" : a.updated;
            string y = b.updated == null ? "" : b.updated;
            return sortDesc ? string.CompareOrdinal(y, x) : string.CompareOrdinal(x, y);
        });

        string f = (filter == null ? "" : filter.Trim().ToLowerInvariant());
        string lastProject = null;
        List<Control> rows = new List<Control>();
        foreach (Session s in items)
        {
            string proj = ProjectName(s.cwd);
            if (f.Length > 0
                && (s.title == null || s.title.ToLowerInvariant().IndexOf(f) < 0)
                && proj.ToLowerInvariant().IndexOf(f) < 0) continue;
            if (proj != lastProject)
            {
                SessionRowView pr = new SessionRowView();
                pr.IsProject = true;
                pr.Text = proj;
                pr.Height = Theme.Px(30);
                rows.Add(pr);
                lastProject = proj;
            }
            SessionRowView r = new SessionRowView();
            r.IsProject = false;
            r.SessionId = s.id;
            r.Text = string.IsNullOrEmpty(s.title) ? "新会话" : s.title;
            r.TimeText = RelTime(s.updated);
            r.Selected = s.id == currentSessionId;
            r.Click += delegate(object sender, EventArgs e)
            {
                SessionRowView row = (SessionRowView)sender;
                if (row.SessionId == currentSessionId) return;
                LoadSessionById(row.SessionId);
            };
            rows.Add(r);
        }
        list.SetRows(rows);

        Session cur = FindSession(currentSessionId);
        lblTitle.Text = cur != null && !string.IsNullOrEmpty(cur.title) ? cur.title : "新会话";
        LayoutUi();
    }

    void EnsureSession()
    {
        if (sessionStore == null) sessionStore = new SessionStore();
        if (FindSession(currentSessionId) != null) return;
        Session s = new Session();
        s.id = Guid.NewGuid().ToString("N").Substring(0, 12);
        s.title = "新会话";
        s.cwd = config.cwd;
        s.provider = config.provider;
        s.model = config.model;
        s.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        s.messages = new List<Dictionary<string, object>>();
        sessionStore.sessions.Insert(0, s);
        currentSessionId = s.id;
    }

    // 插件窗口：里面改完开关，这里马上把 agent 的工具表重新装一遍
    void OpenPlugins()
    {
        int before = CountPluginTools();
        using (PluginsForm dlg = new PluginsForm(config, delegate { runner.ReloadTools(); }))
            dlg.ShowDialog(this);
        config.Save();
        runner.ReloadTools();
        int after = CountPluginTools();
        List<PluginInfo> all = Plugins.Load();
        int on = 0;
        foreach (PluginInfo p in all) if (Plugins.IsEnabled(config, p.Path)) on++;
        Write(OutKind.Info, "· 插件已更新：共 " + all.Count + " 个（启用 " + on + " 个）· 可用插件工具 " + after + " 个"
            + (before != after ? "（之前 " + before + "）" : "") + "\n");
    }

    int CountPluginTools()
    {
        HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
        return Plugins.ToolDefs(config, taken).Count;
    }

    void OpenSettings()
    {
        using (SettingsForm dlg = new SettingsForm(config, delegate(string lv) { SetPerm(lv, true); }))
        {
            dlg.Icon = Theme.AppIcon();          // 设置窗口也挂程序图标，别只剩它顶默认图标
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            config = dlg.Result;
            config.Save();
            rtb.AutoFollow = config.follow_output;
            suppressModelChange = true;
            txtCwd.Text = config.cwd;
            chkThink.Checked = config.think;
            chkAutoYes.Checked = config.auto_yes;
            cmbCtx.SelectedItem = config.num_ctx.ToString();
            if (cmbCtx.SelectedIndex < 0) cmbCtx.SelectedItem = "8192";
            suppressModelChange = false;
            SetPerm(config.permission, false);        // 权限档：顶栏 / 输入条跟着设置页一起变
            runner = new AgentRunner(config, this);
            rtb.ClearAll();
            trace.ClearAll();
            lblFoot.Text = ProjectName(config.cwd) + "  ·  " + config.cwd;
            LoadModels();
            ApplyModelInfo();
            Write(OutKind.Info, "· 设置已保存\n");
        }
    }

    // ============================ 输出 ============================

    static string Sym(string s)
    {
        return s.Replace("✗", "×").Replace("✓", "√").Replace("⚠", "!").Replace("🧠 ", "").Replace("🧠", "");
    }

    public void Write(OutKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (InvokeRequired) { SafeWrite(kind, text); return; }
        string t = Sym(text);
        switch (kind)
        {
            case OutKind.User:
                {
                    string body = t.Trim();
                    if (body.StartsWith("› ")) body = body.Substring(2);
                    rtb.Break();
                    rtb.AddChunk(OutKind.User, "你\n" + body + "\n");
                    rtb.FlushNow();
                    if (rtb.LastBlock != null && runner != null)
                        rtb.LastBlock.MsgIdx = runner.Messages.Count - 1;   // 这句是第几条，分支时要按它切
                    break;
                }
            case OutKind.Assistant:
                {
                    string body = t;
                    if (body.StartsWith("\n● ")) body = "\n" + body.Substring(3);
                    else if (body.StartsWith("● ")) body = body.Substring(2);
                    // 分片式流式输出：同一种 kind 会自动接在同一条消息上，只有这一条在重画
                    rtb.AddChunk(OutKind.Assistant, body);
                    break;
                }
            case OutKind.Thinking:
                {
                    string body = t.Trim();
                    if (body.Length == 0) break;
                    if (body == "思考")
                    {
                        trace.Break();
                        trace.AddChunk(OutKind.Thinking, "思考\n");
                        if (!config.hide_thinking)
                        {
                            rtb.Break();
                            rtb.AddChunk(OutKind.Thinking, "思考\n");
                        }
                        break;
                    }
                    trace.AddChunk(OutKind.Thinking, body);
                    // 关掉"思考显示"后，思考正文只进轨迹页：对话区不会被长思考一直往上顶
                    if (!config.hide_thinking) rtb.AddChunk(OutKind.Thinking, body);
                    break;
                }
            case OutKind.ToolCall:
                {
                    string body = t.Trim();
                    if (body.StartsWith("● ")) body = body.Substring(2);
                    stepNo++;
                    rtb.Break();
                    rtb.AddChunk(OutKind.ToolCall, "• " + body + "\n");
                    trace.Break();
                    trace.AddChunk(OutKind.ToolCall, stepNo + ". " + body + "\n");
                    break;
                }
            case OutKind.ToolResult:
                {
                    string body = t.Trim();
                    if (body.StartsWith("└ ")) body = body.Substring(2);
                    rtb.Break();
                    rtb.AddChunk(OutKind.ToolResult, body + "\n");
                    trace.Break();
                    trace.AddChunk(OutKind.ToolResult, "    " + body + "\n");
                    break;
                }
            case OutKind.Error:
                {
                    string body = t.Trim();
                    if (body.StartsWith("× ") || body.StartsWith("! ")) body = body.Substring(2);
                    rtb.Break();
                    rtb.AddChunk(OutKind.Error, body + "\n");
                    trace.Break();
                    trace.AddChunk(OutKind.Error, body + "\n");
                    break;
                }
            case OutKind.Stats:
                {
                    string body = t.Trim();
                    if (body.Length == 0) break;
                    rtb.Break();
                    rtb.AddChunk(OutKind.Stats, body + "\n");
                    trace.Break();
                    trace.AddChunk(OutKind.Stats, body + "\n");
                    break;
                }
            case OutKind.Changes:
                {
                    // 「已编辑 N 个文件」卡片（正文保留原样，卡片自己解析）
                    string body = t.Replace("\r\n", "\n").Trim();
                    if (body.Length == 0) break;
                    rtb.Break();
                    rtb.AddChunk(OutKind.Changes, body);
                    break;
                }
            default:
                {
                    string body = t.Trim();
                    if (body.Length == 0) break;
                    rtb.Break();
                    rtb.AddChunk(OutKind.Info, body + "\n");
                    break;
                }
        }
    }

    void SafeUi(MethodInvoker action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(action);
        }
        catch { }
    }

    void SafeWrite(OutKind kind, string text)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new MethodInvoker(delegate { Write(kind, text); }));
        }
        catch { }
    }

    public int Approve(string toolName, string[] preview)
    {
        if (InvokeRequired)
        {
            object result = null;
            try
            {
                result = Invoke(new Func<int>(delegate { return Approve(toolName, preview); }));
            }
            catch { return 0; }
            return result == null ? 0 : (int)result;
        }
        using (ApprovalForm dlg = new ApprovalForm(toolName, preview))
        {
            dlg.ShowDialog(this);
            return dlg.Decision;
        }
    }

    public void Status(string text)
    {
        if (InvokeRequired) { SafeUi(delegate { lblStatus.Text = text; }); return; }
        lblStatus.Text = text;
        UpdatePlanChip();      // /计划 开|关 是在 AgentRunner 里切的，chip 得跟着刷新
        dotStatus.DotColor = running ? Theme.Accent : Theme.Ok;
        dotStatus.Invalidate();
    }

    // 越权访问：同一个弹窗，但默认按钮是「拒绝」（按 Enter 不会放行）
    public int ApproveOutside(string toolName, string[] preview)
    {
        if (InvokeRequired)
        {
            object result = null;
            try { result = Invoke(new Func<int>(delegate { return ApproveOutside(toolName, preview); })); }
            catch { return 0; }
            return result == null ? 0 : (int)result;
        }
        using (ApprovalForm dlg = new ApprovalForm(toolName, preview, true))
        {
            dlg.ShowDialog(this);
            return dlg.Decision;
        }
    }

    // ============================ 发送 ============================

    void SendMessage()
    {
        if (running) return;
        string text = txtInput.Text.Trim();
        if (text.Length == 0) return;
        txtInput.Clear();
        tabs.SelectedIndex = 0;
        SwitchTab();
        ShowHero(false);      // 顺序很重要：SwitchTab 会按"有没有内容"重算空状态，收尾再收一次才稳
        // 发新消息 = 用户想看回答：重新打开自动跟随（之前往上翻历史时它被关掉了）
        if (config.follow_output) rtb.Follow = true;
        rtb.ScrollToEnd();
        EnsureSession();
        running = true;
        turnNo++;
        UpdateSendButton();
        dotStatus.DotColor = Theme.Accent;
        dotStatus.Invalidate();
        trace.Break();
        trace.AddChunk(OutKind.Info, "· 第 " + turnNo + " 轮\n");
        runner.Cancelled = false;
        Thread t = new Thread(delegate()
        {
            try { runner.RunTurn(text); }
            catch (LocalAgentException ex)
            {
                if (ex.Message != "__CANCELLED__") SafeWrite(OutKind.Error, "\n× " + ex.Message + "\n");
                else SafeWrite(OutKind.Error, "\n! 已中断本轮\n");
            }
            catch (Exception ex) { SafeWrite(OutKind.Error, "\n× 出错：" + ex.Message + "\n"); }
            finally
            {
                running = false;
                try { SaveCurrentSession(); } catch { }
                SafeUi(delegate
                {
                    UpdateSendButton();
                    RefreshSessionList();
                    lblStatus.Text = "就绪";
                    dotStatus.DotColor = Theme.Ok;
                    dotStatus.Invalidate();
                });
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // ============================ 窗口事件 ============================

    // ---- 自绘标题栏：干掉系统标题栏（那条"图标 + Ahz Scribe — 本地智能体"），
    //      但仍然保留窗口边框，所以拖动、缩放、贴边分屏、圆角和阴影都还在 ----

    const int WM_NCCALCSIZE = 0x0083;
    const int WM_NCHITTEST = 0x0084;
    const int WM_ACTIVATEAPP = 0x001C;
    const int HTCLIENT = 1;
    const int HTCAPTION = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct NcRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct NcCalcSizeParams { public NcRect rgrc0, rgrc1, rgrc2; public IntPtr lppos; }

    protected override void WndProc(ref Message m)
    {
        try
        {
            WndProcCore(ref m);
        }
        catch
        {
            // 自绘标题栏出任何意外都不能把窗口拖死：退回系统默认处理
            base.WndProc(ref m);
        }
    }

    void WndProcCore(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // 注意：这里不能先调 base.WndProc —— 那一步会把标题栏的高度算进客户区里，
            // 结果就是"自绘标题栏没生效、系统标题栏还在"。直接返回 0 = 客户区铺满整个窗口。
            if (WindowState == FormWindowState.Maximized)
            {
                // 最大化时窗口比屏幕大出一圈看不见的边框，把客户区缩回来，否则内容会跑到屏幕外
                NcCalcSizeParams p = (NcCalcSizeParams)m.GetLParam(typeof(NcCalcSizeParams));
                int f = Native.FrameThickness;
                p.rgrc0.Left += f;
                p.rgrc0.Top += f;
                p.rgrc0.Right -= f;
                p.rgrc0.Bottom -= f;
                Marshal.StructureToPtr(p, m.LParam, false);
            }
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_NCHITTEST && WindowState != FormWindowState.Minimized)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                int lp = m.LParam.ToInt32();
                Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int code = HitTestFrame(pt);
                if (code != 0) m.Result = (IntPtr)code;
            }
            return;
        }

        if (m.Msg == WM_ACTIVATEAPP && m.WParam == IntPtr.Zero)
        {
            // 整个程序失去前台（用户切到别的程序去了）：把浮层收掉
            CloseModelPopover();
            ClosePermPopover();
            base.WndProc(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    // 返回 0 = 不特殊处理（走默认的客户区）
    int HitTestFrame(Point pt)
    {
        if (WindowState != FormWindowState.Maximized)
        {
            int e = D(6);
            bool left = pt.X < e, right = pt.X > Width - e;
            bool top = pt.Y < e, bottom = pt.Y > Height - e;
            if (top && left) return 13;      // HTTOPLEFT
            if (top && right) return 14;     // HTTOPRIGHT
            if (bottom && left) return 16;   // HTBOTTOMLEFT
            if (bottom && right) return 17;  // HTBOTTOMRIGHT
            if (left) return 10;             // HTLEFT
            if (right) return 11;            // HTRIGHT
            if (top) return 12;              // HTTOP
            if (bottom) return 15;           // HTBOTTOM
        }

        // 顶部这一条当标题栏用：拖动、双击最大化、贴边分屏都由系统处理
        if (pt.Y >= 0 && pt.Y <= D(40))
        {
            foreach (Control c in Controls)
                if (c.Visible && c.Bounds.Contains(pt)) return 0;   // 按钮/面板自己处理点击
            return HTCAPTION;
        }
        return 0;
    }

    void SyncWindowButtons()
    {
        if (btnMax == null) return;
        btnMax.Icon = WindowState == FormWindowState.Maximized ? "restore" : "max";
        btnMax.Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundWindow(Handle);   // Win11：圆角 + 白色标题栏
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // 顶部这一条就是自绘标题栏，只画一条和下面内容区的分隔线
        using (Pen p = new Pen(Theme.LineSoft, Theme.Pxf(1f)))
            e.Graphics.DrawLine(p, 0, D(40) - 1, ClientSize.Width, D(40) - 1);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            if (runner != null) runner.Cancel();
            SaveCurrentSession();
        }
        catch { }
        base.OnFormClosing(e);
    }

    // ---------------- 输入条上的模型浮层（自绘，不用 ContextMenuStrip） ----------------

    void ToggleModelPopover()
    {
        if (modelPop != null && modelPop.Visible) { CloseModelPopover(); return; }
        ShowModelPopover();
    }

    void CloseModelPopover()
    {
        ModelPopover p = modelPop;
        modelPop = null;
        if (cmbModel != null) { cmbModel.Expanded = false; cmbModel.Invalidate(); }
        if (p != null)
        {
            try { p.Hide(); p.Dispose(); }
            catch (Exception ex) { Log.Swallow(ex, "关闭模型浮层"); }
        }
    }

    // 把当前配置里的模型拍平成"列表 + 分组"两份（本地/云端分开）
    void BuildModelGroups(out string[] labels, out string[] groups, out int cur)
    {
        List<string> ls = new List<string>();
        List<string> gs = new List<string>();
        cur = -1;
        Provider curP = config.Current();
        for (int i = 0; i < cmbModel.Items.Count; i++)
        {
            ModelItem it = cmbModel.Items[i] as ModelItem;
            if (it == null) continue;
            string g = "云端";
            Provider p = config.Find(it.ProviderId);
            if (p != null && p.type == "ollama") g = "本地";
            ls.Add(it.Model);
            gs.Add(g);
            if (it.ProviderId == curP.id && it.Model == config.model) cur = ls.Count - 1;
        }
        labels = ls.ToArray();
        groups = gs.ToArray();
    }

    void ShowModelPopover()
    {
        string[] labels, groups;
        int cur;
        BuildModelGroups(out labels, out groups, out cur);
        bool effortOk = EffortSupported();
        ModelPopover p = new ModelPopover(labels, groups, cur, 8,
            config.model, Effort.Index(config.effort), effortOk, Effort.Levels[Effort.Index(config.effort)]);
        p.Chosen += delegate(object s, EventArgs e)
        {
            int idx = p.PickedIndex;
            if (idx < 0 || idx == cmbModel.SelectedIndex) { CloseModelPopover(); return; }
            // 选模型时浮层不关：平滑换成"强度"那张卡（Codex 就是这个手感）
            cmbModel.SelectedIndex = idx;          // 走原来的换模型逻辑
            p.Refresh(config.model, Effort.Index(config.effort), EffortSupported());
            p.ShowPage(ModelPopover.PageStrength, true);
        };
        p.EffortChanged += delegate
        {
            SetEffort(Effort.Levels[p.EffortPage.Value], true);
        };
        modelPop = p;
        cmbModel.Expanded = true;
        cmbModel.Invalidate();
        // 浮层自己不抢焦点（Show() 会顺手激活新窗口，让主窗口"失活"）。所以：
        //   1. 临时压住"失活就关"的判断，等真正弹出来再放开；
        //   2. 放开之后不靠 Deactivate 收浮层（用户点浮层照样会激活它、主窗口失活），
        //      "点窗口别处就收"靠 MainForm.OnMouseDown（见那里）。
        p.ShowOver(cmbModel);
        p.ShowTick = Environment.TickCount;
    }


    // 浮层开着时按 Esc / 点窗口别处都关掉。
    // 注意：不要拿 OnDeactivate 来收浮层 —— Show() 自己就会让主窗口"失活"，
    // Windows 那边的时序也不稳（实测偶发"刚弹出来就被自己收掉"）。改成：
    //   · 主窗口上按下鼠标 → 收（OnMouseDown）
    //   · 整个程序失去前台（用户切到别的程序）→ 收（WM_ACTIVATEAPP）
    //   · Esc / 窗口移动缩放 → 收
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // 点在 chip 自己身上的那次交给 chip 的 Click 去 toggle，别在这里先收掉
        if (permChip != null && permChip.Visible && permChip.Bounds.Contains(e.Location)) return;
        if (modeChip != null && modeChip.Visible && modeChip.Bounds.Contains(e.Location)) return;
        if (cmbModel != null && cmbModel.Visible && cmbModel.Bounds.Contains(e.Location)) return;
        CloseModelPopover();
        ClosePermPopover();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        CloseModelPopover();      // 窗口一移动，浮层的绝对位置就过期了
        ClosePermPopover();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        CloseModelPopover();      // 窗口尺寸一变，浮层的绝对位置也过期了
        ClosePermPopover();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (permPop != null && permPop.Visible)
        {
            if (keyData == Keys.Escape) { ClosePermPopover(); return true; }
            if (keyData == Keys.Up || keyData == Keys.Down)
            {
                int step = keyData == Keys.Down ? 1 : -1;
                int idx = Perm.Index(config.permission) + step;
                if (idx >= 0 && idx < Perm.Values.Length) SetPerm(Perm.ValueAt(idx), true);
                return true;
            }
        }
        if (modelPop != null && modelPop.Visible)
        {
            if (keyData == Keys.Escape)
            {
                CloseModelPopover();
                return true;
            }
            // 浮层不抢焦点，←/→ 和 ↑/↓ 落不到它身上，这里转过去
            if (keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down)
            {
                modelPop.HandleKey(keyData);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

// ============================ 设置窗口（左侧导航 + 右侧内容） ============================

class ModelItem
{
    public string ProviderId;
    public string ProviderName;
    public string Model;
    public override string ToString() { return ProviderName + " / " + Model; }
}

// ============================ 插件窗口 ============================

// 安装插件：从地址装 / 从市场索引一键装
class PluginInstallForm : Form
{
    private readonly AgentConfig _c;
    private readonly Action<string> _onInstalled;
    private FieldBox _addrBox, _marketUrlBox;
    private TextBox _txtAddr, _txtMarketUrl;
    private CheckBox _chkOverwrite;
    private RichTextBox _preview;
    private ListBox _market;
    private Label _lblMsg, _lblMarketSrc, _lblPreviewHead, _lblMarketHead;
    private RoundedPanel _previewCard, _marketCard;
    private UiButton _btnFetch, _btnInstall, _btnSaveUrl, _btnReloadMarket, _btnSampleMarket, _btnInstallSel, _btnClose;
    private string _pendingText, _pendingUrl;
    private List<MarketEntry> _entries = new List<MarketEntry>();
    private Panel _body;

    public PluginInstallForm(AgentConfig config, Action<string> onInstalled)
    {
        _c = config;
        _onInstalled = onInstalled;
        Theme.EnsureDpi();
        AutoScaleMode = AutoScaleMode.None;
        Text = "Ahz Scribe — 安装插件";
        Size = new Size(840, 660);
        MinimumSize = new Size(700, 560);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        Build();
        if (Theme.Dpi > 1.01f)
        {
            Theme.ScaleBounds(this, Theme.Dpi);
            Size = new Size(Theme.Px(840), Theme.Px(660));
            MinimumSize = new Size(Theme.Px(700), Theme.Px(560));
        }
        ReloadMarket();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundWindow(Handle);
    }

    void Build()
    {
        _body = new Panel();
        _body.Dock = DockStyle.Fill;
        _body.BackColor = Theme.Bg;
        Controls.Add(_body);

        Label h1 = new Label();
        h1.Text = "从地址安装";
        h1.Font = Theme.FB(12f);
        h1.ForeColor = Theme.Fg;
        h1.BackColor = Theme.Bg;
        h1.AutoSize = true;
        h1.Location = new Point(26, 18);
        _body.Controls.Add(h1);

        Label hint = new Label();
        hint.Text = "可以填：json 直链 / GitHub 的 作者/仓库（会去找仓库根目录的 plugin.json）/ 本地 json 路径";
        hint.ForeColor = Theme.FgDim;
        hint.BackColor = Theme.Bg;
        hint.Font = Theme.F(8.6f);
        hint.AutoSize = true;
        hint.Location = new Point(28, 44);
        _body.Controls.Add(hint);

        _addrBox = new FieldBox(false);
        _addrBox.BackColor = Theme.Bg;
        _addrBox.SetBounds(26, 70, 520, 34);
        _body.Controls.Add(_addrBox);
        _txtAddr = (TextBox)_addrBox.Inner;

        _btnFetch = MakeButton("获取", 556, 70, 92, 34);
        _btnFetch.Click += delegate { FetchPreview(); };
        _body.Controls.Add(_btnFetch);

        _btnInstall = MakeButton("安装", 656, 70, 92, 34);
        _btnInstall.FillColor = Theme.Accent;
        _btnInstall.HoverColor = Theme.AccentHover;
        _btnInstall.TextColor = Color.White;
        _btnInstall.BorderColor = Theme.Clear;
        _btnInstall.Click += delegate { DoInstall(); };
        _body.Controls.Add(_btnInstall);

        _chkOverwrite = new CheckBox();
        _chkOverwrite.Text = "覆盖已存在的插件";
        _chkOverwrite.AutoSize = true;
        _chkOverwrite.ForeColor = Theme.FgMid;
        _chkOverwrite.BackColor = Theme.Bg;
        _chkOverwrite.Location = new Point(556, 112);
        _body.Controls.Add(_chkOverwrite);

        _lblPreviewHead = new Label();
        _lblPreviewHead.Text = "预览";
        _lblPreviewHead.ForeColor = Theme.Fg;
        _lblPreviewHead.BackColor = Theme.Bg;
        _lblPreviewHead.Font = Theme.FB(10f);
        _lblPreviewHead.AutoSize = true;
        _lblPreviewHead.Location = new Point(26, 112);
        _body.Controls.Add(_lblPreviewHead);

        _previewCard = new RoundedPanel();
        _previewCard.Radius = 12;
        _previewCard.Fill = Theme.Card;
        _previewCard.BorderColor = Theme.Line;
        _previewCard.BackColor = Theme.Bg;
        _previewCard.Padding = new Padding(14, 12, 14, 12);
        _previewCard.SetBounds(26, 140, 722, 180);
        _body.Controls.Add(_previewCard);

        _preview = new RichTextBox();
        _preview.Dock = DockStyle.Fill;
        _preview.BorderStyle = BorderStyle.None;
        _preview.ReadOnly = true;
        _preview.WordWrap = true;
        _preview.ScrollBars = RichTextBoxScrollBars.Vertical;
        _preview.BackColor = Theme.Card;
        _preview.ForeColor = Theme.FgMid;
        _preview.Font = Theme.F(9.5f);
        _preview.Text = "还没有获取内容。填好地址点「获取」，这里会显示这个插件声明了什么。";
        _previewCard.Controls.Add(_preview);

        Label warn = new Label();
        warn.Text = "⚠ 插件能让 agent 跑 PowerShell 命令、发网络请求。只装你信任的来源，装完先看一眼上面的预览。";
        warn.ForeColor = Theme.Warn;
        warn.BackColor = Theme.Bg;
        warn.Font = Theme.F(8.6f);
        warn.AutoSize = true;
        warn.Location = new Point(28, 328);
        _body.Controls.Add(warn);

        _lblMarketHead = new Label();
        _lblMarketHead.Text = "插件市场";
        _lblMarketHead.ForeColor = Theme.Fg;
        _lblMarketHead.BackColor = Theme.Bg;
        _lblMarketHead.Font = Theme.FB(12f);
        _lblMarketHead.AutoSize = true;
        _lblMarketHead.Location = new Point(26, 362);
        _body.Controls.Add(_lblMarketHead);

        _lblMarketSrc = new Label();
        _lblMarketSrc.ForeColor = Theme.FgFaint;
        _lblMarketSrc.BackColor = Theme.Bg;
        _lblMarketSrc.Font = Theme.F(8.4f);
        _lblMarketSrc.AutoSize = false;
        _lblMarketSrc.SetBounds(120, 368, 628, 18);
        _body.Controls.Add(_lblMarketSrc);

        _marketUrlBox = new FieldBox(false);
        _marketUrlBox.BackColor = Theme.Bg;
        _marketUrlBox.SetBounds(26, 394, 470, 34);
        _body.Controls.Add(_marketUrlBox);
        _txtMarketUrl = (TextBox)_marketUrlBox.Inner;
        _txtMarketUrl.Text = _c.plugin_market_url == null ? "" : _c.plugin_market_url;

        _btnSaveUrl = MakeButton("保存地址", 506, 394, 110, 34);
        _btnSaveUrl.Click += delegate
        {
            _c.plugin_market_url = _txtMarketUrl.Text.Trim();
            _c.Save();
            ReloadMarket();
            SetMsg("市场地址已保存" + (_c.plugin_market_url.Length == 0 ? "（留空则用 plugins\\market.json）" : ""));
        };
        _body.Controls.Add(_btnSaveUrl);

        _btnReloadMarket = MakeButton("刷新列表", 626, 394, 122, 34);
        _btnReloadMarket.Click += delegate { ReloadMarket(); };
        _body.Controls.Add(_btnReloadMarket);

        _marketCard = new RoundedPanel();
        _marketCard.Radius = 12;
        _marketCard.Fill = Color.White;
        _marketCard.BorderColor = Theme.Line;
        _marketCard.BackColor = Theme.Bg;
        _marketCard.Padding = new Padding(5);
        _marketCard.SetBounds(26, 438, 722, 130);
        _body.Controls.Add(_marketCard);

        _market = new ListBox();
        _market.Dock = DockStyle.Fill;
        _market.BorderStyle = BorderStyle.None;
        _market.BackColor = Color.White;
        _market.ForeColor = Theme.Fg;
        _market.Font = Theme.F(9.5f);
        _market.IntegralHeight = false;
        _marketCard.Controls.Add(_market);

        _btnInstallSel = MakeButton("安装选中的", 26, 578, 130, 34);
        _btnInstallSel.Click += delegate { InstallSelected(); };
        _body.Controls.Add(_btnInstallSel);

        _btnSampleMarket = MakeButton("新建示例市场文件", 164, 578, 170, 34);
        _btnSampleMarket.Click += delegate
        {
            string p = Plugins.WriteSampleMarket();
            ReloadMarket();
            SetMsg("已生成示例市场文件：" + p);
        };
        _body.Controls.Add(_btnSampleMarket);

        _lblMsg = new Label();
        _lblMsg.ForeColor = Theme.FgDim;
        _lblMsg.BackColor = Theme.Bg;
        _lblMsg.Font = Theme.F(8.8f);
        _lblMsg.AutoSize = false;
        _lblMsg.SetBounds(344, 582, 404, 26);
        _body.Controls.Add(_lblMsg);

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 58;
        bottom.BackColor = Theme.Bg;
        bottom.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.LineSoft, 1f))
                e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0);
        };

        _btnClose = new UiButton();
        _btnClose.Text = "关闭";
        _btnClose.BorderColor = Theme.Line;
        _btnClose.FillColor = Color.White;
        _btnClose.HoverColor = Theme.Hover;
        _btnClose.Radius = 10;
        _btnClose.Font = Theme.F(9.5f);
        _btnClose.Size = new Size(96, 36);
        _btnClose.Click += delegate { Close(); };
        bottom.Controls.Add(_btnClose);
        bottom.Layout += delegate
        {
            _btnClose.Location = new Point(bottom.ClientSize.Width - 22 - _btnClose.Width, 11);
        };
        Controls.Add(bottom);

        _body.Resize += delegate { LayoutBody(); };
    }

    void LayoutBody()
    {
        int w = _body.ClientSize.Width;
        int m = Theme.Px(26);
        int inner = Math.Max(Theme.Px(360), w - m * 2);
        _addrBox.SetBounds(m, Theme.Px(70), Math.Max(Theme.Px(200), inner - Theme.Px(202)), Theme.Px(34));
        _btnFetch.SetBounds(m + inner - Theme.Px(192), Theme.Px(70), Theme.Px(92), Theme.Px(34));
        _btnInstall.SetBounds(m + inner - Theme.Px(92), Theme.Px(70), Theme.Px(92), Theme.Px(34));
        _chkOverwrite.Location = new Point(m + inner - Theme.Px(192), Theme.Px(112));
        _previewCard.SetBounds(m, Theme.Px(140), inner, Theme.Px(180));
        _lblMarketSrc.SetBounds(Theme.Px(120), Theme.Px(368), Math.Max(Theme.Px(120), w - Theme.Px(146)), Theme.Px(18));
        _marketUrlBox.SetBounds(m, Theme.Px(394), Math.Max(Theme.Px(160), inner - Theme.Px(252)), Theme.Px(34));
        _btnSaveUrl.SetBounds(m + inner - Theme.Px(242), Theme.Px(394), Theme.Px(110), Theme.Px(34));
        _btnReloadMarket.SetBounds(m + inner - Theme.Px(122), Theme.Px(394), Theme.Px(122), Theme.Px(34));
        _marketCard.SetBounds(m, Theme.Px(438), inner, Theme.Px(130));
        _lblMsg.SetBounds(Theme.Px(344), Theme.Px(582), Math.Max(Theme.Px(120), w - Theme.Px(370)), Theme.Px(26));
    }

    UiButton MakeButton(string text, int x, int y, int w, int h)
    {
        UiButton b = new UiButton();
        b.Text = text;
        b.BorderColor = Theme.Line;
        b.FillColor = Color.White;
        b.HoverColor = Theme.Hover;
        b.Radius = 9;
        b.Font = Theme.F(9f);
        b.SetBounds(x, y, w, h);
        return b;
    }

    void SetMsg(string s) { _lblMsg.Text = s; }

    void FetchPreview()
    {
        string err;
        string url = Plugins.ResolveUrl(_txtAddr.Text, out err);
        if (url == null) { SetMsg(err); _pendingText = null; _pendingUrl = null; return; }
        SetMsg("正在获取 " + url + " …");
        Application.DoEvents();
        string text;
        try { text = Plugins.DownloadText(url, 20); }
        catch (Exception ex) { SetMsg("下载失败：" + ex.Message); _pendingText = null; _pendingUrl = null; return; }
        PluginFile pf = Plugins.Parse(text, out err);
        if (pf == null)
        {
            _pendingText = null; _pendingUrl = null;
            SetMsg("内容不合格：" + err);
            _preview.Text = "这个地址拿到的内容不是合格插件：\n" + err;
            return;
        }
        _pendingText = text;
        _pendingUrl = url;
        _preview.Text = Describe(pf, url);
        SetMsg("获取成功，检查无误就点「安装」");
    }

    void DoInstall()
    {
        if (_pendingText == null) { SetMsg("先点「获取」，看清楚内容再安装"); return; }
        string msg, path;
        if (!Plugins.Install(_pendingText, _c, _pendingUrl, _chkOverwrite.Checked, out msg, out path))
        {
            SetMsg(msg);
            return;
        }
        _c.Save();
        SetMsg(msg);
        _pendingText = null;
        if (_onInstalled != null) _onInstalled(path);
        MessageBox.Show(this, msg + "\n\n可以在「插件」窗口里启用/停用它。", "安装完成",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ReloadMarket()
    {
        string err, src;
        _entries = Plugins.LoadMarket(_c, out err, out src);
        _market.Items.Clear();
        foreach (MarketEntry e in _entries)
            _market.Items.Add((e.name == null ? "(没写名字)" : e.name) + "    " + (e.description == null ? "" : e.description));
        if (_market.Items.Count > 0) _market.SelectedIndex = 0;
        _lblMarketSrc.Text = src == null ? "" : ("来源：" + src);
        SetMsg(err == null ? ("市场里有 " + _entries.Count + " 个插件") : err);
    }

    void InstallSelected()
    {
        int i = _market.SelectedIndex;
        if (i < 0 || i >= _entries.Count) { SetMsg("先选一个市场条目"); return; }
        MarketEntry e = _entries[i];
        if (string.IsNullOrEmpty(e.url)) { SetMsg("这个条目没写 url"); return; }
        _txtAddr.Text = e.url;
        FetchPreview();
        if (_pendingText == null) return;
        if (MessageBox.Show(this, "要安装「" + (e.name == null ? e.url : e.name) + "」吗？\n\n插件能让 agent 跑命令，只装你信任的来源。",
                "安装插件", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        DoInstall();
    }

    string Describe(PluginFile pf, string url)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("名称：" + pf.name + "\n");
        if (!string.IsNullOrEmpty(pf.version)) sb.Append("版本：" + pf.version + "\n");
        if (!string.IsNullOrEmpty(pf.description)) sb.Append("说明：" + pf.description + "\n");
        sb.Append("来源：" + url + "\n\n");
        int nt = pf.tools == null ? 0 : pf.tools.Count;
        int np = pf.prompts == null ? 0 : pf.prompts.Count;
        sb.Append("工具 " + nt + " 个，技能 " + np + " 条\n\n");
        if (pf.tools != null)
            foreach (PluginToolDef t in pf.tools)
            {
                string kind = string.IsNullOrEmpty(t.type) ? "shell" : t.type.Trim().ToLowerInvariant();
                bool need = (kind != "http") || !(t.approval.HasValue && t.approval.Value == false);
                sb.Append("• " + t.name + "  [" + (kind == "http" ? "http 请求" : "PowerShell 命令") + (need ? "，会问你" : "，免确认") + "]\n");
                if (!string.IsNullOrEmpty(t.description)) sb.Append("    " + t.description + "\n");
                sb.Append("    " + (kind == "http" ? (string.IsNullOrEmpty(t.method) ? "GET" : t.method.ToUpperInvariant()) + " " + t.url : "$ " + t.command) + "\n");
            }
        if (pf.prompts != null)
            foreach (PluginPrompt p in pf.prompts)
                sb.Append("• 技能" + (string.IsNullOrEmpty(p.name) ? "" : "【" + p.name + "】") + "：" + p.content + "\n");
        return sb.ToString();
    }
}

class PluginsForm : Form
{
    private readonly AgentConfig _c;
    private readonly MethodInvoker _onChanged;
    private ListBox _list;
    private Label _lblName, _lblMeta, _lblDesc, _lblPath, _lblToolsHead;
    private CheckBox _chkEnabled;
    private RichTextBox _detail;
    private RoundedPanel _card;
    private UiButton _btnReload, _btnOpen, _btnSample, _btnClose, _btnInstall;
    private List<PluginInfo> _plugins = new List<PluginInfo>();
    private PluginInfo _cur;
    private bool _loading;
    private string _selectAfterReload;

    public PluginsForm(AgentConfig config, MethodInvoker onChanged)
    {
        _c = config;
        _onChanged = onChanged;

        Theme.EnsureDpi();          // 必须在建控件之前算好缩放，否则字体和行高对不上
        AutoScaleMode = AutoScaleMode.None;
        Text = "Ahz Scribe — 插件";
        Size = new Size(900, 620);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.Sizable;

        BuildChrome();
        if (Theme.Dpi > 1.01f)
        {
            Theme.ScaleBounds(this, Theme.Dpi);
            Size = new Size(Theme.Px(900), Theme.Px(620));
            MinimumSize = new Size(Theme.Px(760), Theme.Px(520));
        }
        Reload();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundWindow(Handle);
    }

    void BuildChrome()
    {
        Panel nav = new Panel();
        nav.Dock = DockStyle.Left;
        nav.Width = 250;
        nav.BackColor = Theme.Side;
        nav.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.Line, 1f))
                e.Graphics.DrawLine(p, nav.Width - 1, 0, nav.Width - 1, nav.Height);
        };

        LogoMark logo = new LogoMark();
        logo.BackColor = Theme.Side;
        logo.SetBounds(20, 20, 24, 24);
        nav.Controls.Add(logo);

        Label title = new Label();
        title.Text = "插件";
        title.Font = Theme.FB(12.5f);
        title.ForeColor = Theme.Fg;
        title.BackColor = Theme.Side;
        title.AutoSize = true;
        title.Location = new Point(54, 22);
        nav.Controls.Add(title);

        Label hint = new Label();
        hint.Text = "给 agent 加工具";
        hint.ForeColor = Theme.FgFaint;
        hint.BackColor = Theme.Side;
        hint.Font = Theme.F(8.5f);
        hint.AutoSize = true;
        hint.Location = new Point(56, 46);
        nav.Controls.Add(hint);

        RoundedPanel listCard = new RoundedPanel();
        listCard.Radius = 10;
        listCard.Fill = Color.White;
        listCard.BorderColor = Theme.Line;
        listCard.BackColor = Theme.Side;
        listCard.Padding = new Padding(5);
        listCard.SetBounds(14, 76, 222, 400);
        listCard.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        nav.Controls.Add(listCard);

        _list = new ListBox();
        _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Color.White;
        _list.ForeColor = Theme.Fg;
        _list.Font = Theme.F(9.5f);
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = Theme.Px(38);
        _list.IntegralHeight = false;
        _list.DrawItem += delegate(object s, DrawItemEventArgs e) { DrawPluginRow(e); };
        _list.SelectedIndexChanged += delegate { SelectPlugin(); };
        listCard.Controls.Add(_list);

        _btnReload = NavButton("重新加载");
        _btnReload.SetBounds(14, 486, 106, 32);
        _btnReload.Click += delegate { Reload(); Changed(); };
        nav.Controls.Add(_btnReload);

        _btnOpen = NavButton("打开目录");
        _btnOpen.SetBounds(126, 486, 110, 32);
        _btnOpen.Click += delegate
        {
            Plugins.EnsureDir();
            try { Process.Start("explorer.exe", Plugins.Dir); } catch { }
        };
        nav.Controls.Add(_btnOpen);

        _btnSample = NavButton("新建示例插件");
        _btnSample.SetBounds(14, 524, 222, 32);
        _btnSample.Click += delegate
        {
            string f = Plugins.WriteSample();
            Reload();
            for (int i = 0; i < _plugins.Count; i++)
                if (string.Equals(_plugins[i].Path, f, StringComparison.OrdinalIgnoreCase)) _list.SelectedIndex = i;
            Changed();
        };
        nav.Controls.Add(_btnSample);
        // 装插件：主按钮，放在列表下面
        _btnInstall = NavButton("＋ 安装插件");
        _btnInstall.FillColor = Theme.AccentSoft;
        _btnInstall.HoverColor = Color.FromArgb(226, 232, 254);
        _btnInstall.TextColor = Theme.Accent;
        _btnInstall.BorderColor = Theme.Clear;
        _btnInstall.Font = Theme.F(9.5f);
        _btnInstall.Click += delegate
        {
            using (PluginInstallForm dlg = new PluginInstallForm(_c,
                delegate(string installedPath) { _selectAfterReload = installedPath; }))
                dlg.ShowDialog(this);
            Reload();
            if (_selectAfterReload != null)
            {
                for (int i = 0; i < _plugins.Count; i++)
                    if (string.Equals(_plugins[i].Path, _selectAfterReload, StringComparison.OrdinalIgnoreCase))
                        _list.SelectedIndex = i;
                _selectAfterReload = null;
            }
            Changed();
        };
        nav.Controls.Add(_btnInstall);
        // 按钮要盖在列表卡片之上（卡片比设计高度高时不会把按钮压住）
        _btnReload.BringToFront();
        _btnOpen.BringToFront();
        _btnSample.BringToFront();
        _btnInstall.BringToFront();
        // 不用 Anchor：缩放 + 锚点会互相打架（算出来的位置会跑到窗口外面），统一在 Resize 里排
        nav.Resize += delegate
        {
            int m = Theme.Px(14), bh = Theme.Px(32), gap = Theme.Px(8);
            int w2 = Math.Max(Theme.Px(70), (nav.ClientSize.Width - m * 2 - gap) / 2);
            int bottomY = nav.ClientSize.Height - m;
            _btnSample.SetBounds(m, bottomY - bh, nav.ClientSize.Width - m * 2, bh);
            _btnReload.SetBounds(m, _btnSample.Top - gap - bh, w2, bh);
            _btnOpen.SetBounds(m + w2 + gap, _btnReload.Top, Math.Max(Theme.Px(70), nav.ClientSize.Width - m * 2 - w2 - gap), bh);
            _btnInstall.SetBounds(m, _btnReload.Top - gap - bh, nav.ClientSize.Width - m * 2, bh);
        };

        Panel body = new Panel();
        body.Dock = DockStyle.Fill;
        body.BackColor = Theme.Bg;
        body.Padding = new Padding(26, 22, 26, 0);

        _lblName = new Label();
        _lblName.Text = "（没有插件）";
        _lblName.Font = Theme.FB(13f);
        _lblName.ForeColor = Theme.Fg;
        _lblName.BackColor = Theme.Bg;
        _lblName.AutoSize = true;
        _lblName.Location = new Point(26, 22);
        body.Controls.Add(_lblName);

        _lblMeta = new Label();
        _lblMeta.ForeColor = Theme.FgDim;
        _lblMeta.BackColor = Theme.Bg;
        _lblMeta.Font = Theme.F(9f);
        _lblMeta.AutoSize = true;
        _lblMeta.Location = new Point(28, 50);
        body.Controls.Add(_lblMeta);

        _chkEnabled = new CheckBox();
        _chkEnabled.Text = "启用这个插件（启用的插件，它的工具会出现在模型的工具列表里）";
        _chkEnabled.AutoSize = true;
        _chkEnabled.ForeColor = Theme.FgMid;
        _chkEnabled.BackColor = Theme.Bg;
        _chkEnabled.Location = new Point(26, 76);
        _chkEnabled.CheckedChanged += delegate
        {
            if (_loading || _cur == null) return;
            Plugins.SetEnabled(_c, _cur.Path, _chkEnabled.Checked);
            _c.Save();
            _list.Invalidate();
            Changed();
        };
        body.Controls.Add(_chkEnabled);

        _lblDesc = new Label();
        _lblDesc.ForeColor = Theme.FgMid;
        _lblDesc.BackColor = Theme.Bg;
        _lblDesc.AutoSize = false;
        _lblDesc.SetBounds(26, 108, 560, 40);
        body.Controls.Add(_lblDesc);

        _lblPath = new Label();
        _lblPath.ForeColor = Theme.FgFaint;
        _lblPath.BackColor = Theme.Bg;
        _lblPath.Font = Theme.F(8.4f);
        _lblPath.AutoSize = false;
        _lblPath.SetBounds(26, 150, 560, 20);
        body.Controls.Add(_lblPath);

        _lblToolsHead = new Label();
        _lblToolsHead.Text = "工具";
        _lblToolsHead.ForeColor = Theme.Fg;
        _lblToolsHead.BackColor = Theme.Bg;
        _lblToolsHead.Font = Theme.FB(10f);
        _lblToolsHead.AutoSize = true;
        _lblToolsHead.Location = new Point(26, 182);
        body.Controls.Add(_lblToolsHead);

        _card = new RoundedPanel();
        _card.Radius = 12;
        _card.Fill = Theme.Card;
        _card.BorderColor = Theme.Line;
        _card.BackColor = Theme.Bg;
        _card.Padding = new Padding(14, 12, 14, 12);
        _card.SetBounds(26, 210, 600, 300);
        body.Controls.Add(_card);

        _detail = new RichTextBox();
        _detail.Dock = DockStyle.Fill;
        _detail.BorderStyle = BorderStyle.None;
        _detail.ReadOnly = true;
        _detail.WordWrap = true;
        _detail.ScrollBars = RichTextBoxScrollBars.Vertical;
        _detail.BackColor = Theme.Card;
        _detail.ForeColor = Theme.FgMid;
        _detail.Font = Theme.F(9.5f);
        _card.Controls.Add(_detail);

        Label foot = new Label();
        foot.Text = "插件工具默认每次都要你点「允许」才会执行（shell 类型始终需要确认）。插件文件放在程序目录的 plugins\\ 里，一个 json 就是一个插件。";
        foot.ForeColor = Theme.FgFaint;
        foot.BackColor = Theme.Bg;
        foot.Font = Theme.F(8.4f);
        foot.AutoSize = false;
        foot.SetBounds(26, 522, 600, 40);
        body.Controls.Add(foot);
        body.Resize += delegate
        {
            int m = Theme.Px(26);
            int w = Math.Max(Theme.Px(260), body.ClientSize.Width - m * 2);
            _lblDesc.SetBounds(m, Theme.Px(108), w, Theme.Px(40));
            _lblPath.SetBounds(m, Theme.Px(150), w, Theme.Px(20));
            _card.SetBounds(m, Theme.Px(210), w, Math.Max(Theme.Px(120), body.ClientSize.Height - Theme.Px(210) - Theme.Px(74)));
            foot.SetBounds(m, body.ClientSize.Height - Theme.Px(52), w, Theme.Px(40));
        };

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 60;
        bottom.BackColor = Theme.Bg;
        bottom.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.LineSoft, 1f))
                e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0);
        };

        _btnClose = new UiButton();
        _btnClose.Text = "关闭";
        _btnClose.FillColor = Theme.Accent;
        _btnClose.HoverColor = Theme.AccentHover;
        _btnClose.TextColor = Color.White;
        _btnClose.Radius = 10;
        _btnClose.Font = Theme.F(9.5f);
        _btnClose.Size = new Size(96, 36);
        _btnClose.Click += delegate { Close(); };
        bottom.Controls.Add(_btnClose);
        bottom.Layout += delegate
        {
            _btnClose.Location = new Point(bottom.ClientSize.Width - 22 - _btnClose.Width, 12);
        };

        Controls.Add(body);
        Controls.Add(bottom);
        Controls.Add(nav);
        AcceptButton = _btnClose;
    }

    static UiButton NavButton(string text)
    {
        UiButton b = new UiButton();
        b.Text = text;
        b.BorderColor = Theme.Line;
        b.FillColor = Color.White;
        b.HoverColor = Theme.Hover;
        b.Radius = 9;
        b.Font = Theme.F(9f);
        return b;
    }

    void Changed()
    {
        if (_onChanged != null) _onChanged();
    }

    void Reload()
    {
        _loading = true;
        _plugins = Plugins.Load();
        _list.Items.Clear();
        foreach (PluginInfo p in _plugins) _list.Items.Add(p.DisplayName);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _loading = false;
        SelectPlugin();
    }

    void SelectPlugin()
    {
        int i = _list.SelectedIndex;
        _cur = (i >= 0 && i < _plugins.Count) ? _plugins[i] : null;
        _loading = true;
        if (_cur == null)
        {
            _lblName.Text = _plugins.Count == 0 ? "（没有插件）" : "（选一个插件）";
            _lblMeta.Text = "";
            _lblDesc.Text = _plugins.Count == 0
                ? "把插件放到程序目录的 plugins\\ 里（一个 json 一个插件），或者点左下角「新建示例插件」看格式。"
                : "";
            _lblPath.Text = Plugins.Dir;
            _chkEnabled.Enabled = false;
            _chkEnabled.Checked = false;
            _lblToolsHead.Text = "工具";
            _detail.Text = "";
        }
        else
        {
            _chkEnabled.Enabled = true;
            _chkEnabled.Checked = Plugins.IsEnabled(_c, _cur.Path);
            _lblName.Text = _cur.DisplayName;
            string ver = _cur.File != null && !string.IsNullOrEmpty(_cur.File.version) ? _cur.File.version : "未标版本";
            int np = Plugins.CountPrompts(_cur);
            if (_cur.Error != null)
                _lblMeta.Text = "读取失败：" + _cur.Error;
            else
                _lblMeta.Text = "版本 " + ver + "  ·  工具 " + _cur.Tools.Count + " 个"
                    + (np > 0 ? "  ·  技能 " + np + " 条" : "")
                    + "  ·  " + (Plugins.IsEnabled(_c, _cur.Path) ? "已启用" : "已停用");
            _lblDesc.Text = _cur.File != null && !string.IsNullOrEmpty(_cur.File.description) ? _cur.File.description : "";
            string src = Plugins.SourceOf(_c, _cur.Path);
            _lblPath.Text = "文件：" + _cur.Path + (src.Length > 0 ? "        来源：" + src : "");
            _lblToolsHead.Text = "工具（" + _cur.Tools.Count + "）";
            _detail.Text = DescribeTools(_cur);
        }
        _loading = false;
    }

    string DescribeTools(PluginInfo p)
    {
        StringBuilder sb = new StringBuilder();
        if (p.Error != null) { sb.Append("这个插件文件没读成功：\n" + p.Error + "\n"); return sb.ToString(); }
        foreach (PluginToolDef t in p.Tools)
        {
            string kind = string.IsNullOrEmpty(t.type) ? "shell" : t.type.Trim().ToLowerInvariant();
            bool needApproval = (kind != "http") || !(t.approval.HasValue && t.approval.Value == false);
            sb.Append("• " + t.name + "   [" + (kind == "http" ? "http 请求" : "PowerShell 命令")
                + (needApproval ? "，需要确认" : "，免确认") + "]\n");
            sb.Append("    " + (string.IsNullOrEmpty(t.description) ? "（没有写说明）" : t.description) + "\n");
            if (kind == "http") sb.Append("    " + (string.IsNullOrEmpty(t.method) ? "GET" : t.method.ToUpperInvariant()) + " " + t.url + "\n");
            else sb.Append("    $ " + t.command + "\n");
            if (t.parameters != null && t.parameters.Count > 0)
            {
                sb.Append("    参数：");
                List<string> ps = new List<string>();
                foreach (PluginParam q in t.parameters)
                    ps.Add(q.name + (q.required ? "（必填）" : ""));
                sb.Append(string.Join("、", ps.ToArray()) + "\n");
            }
            sb.Append("\n");
        }
        if (p.Tools.Count == 0) sb.Append("这个插件里没有声明任何工具。\n");
        if (Plugins.CountPrompts(p) > 0)
        {
            sb.Append("技能（启用后会追加到系统提示里）：\n");
            foreach (PluginPrompt pr in p.File.prompts)
            {
                if (pr == null || string.IsNullOrEmpty(pr.content)) continue;
                sb.Append("• " + (string.IsNullOrEmpty(pr.name) ? "（没写名字）" : pr.name) + "：" + pr.content.Trim() + "\n");
            }
        }
        return sb.ToString();
    }

    void DrawPluginRow(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _plugins.Count) return;
        PluginInfo p = _plugins[e.Index];
        bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (SolidBrush bg = new SolidBrush(sel ? Theme.Sel : Color.White)) e.Graphics.FillRectangle(bg, e.Bounds);
        bool on = Plugins.IsEnabled(_c, p.Path);
        int pad = Theme.Px(12);
        Rectangle nr = new Rectangle(e.Bounds.X + pad, e.Bounds.Y + Theme.Px(3), e.Bounds.Width - pad * 2, Theme.Px(20));
        TextRenderer.DrawText(e.Graphics, p.DisplayName, Theme.F(9.5f), nr, p.Error != null ? Theme.Err : Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        int np = Plugins.CountPrompts(p);
        string sub = p.Error != null ? "读取失败"
            : (on ? p.Tools.Count + " 个工具" + (np > 0 ? " · " + np + " 条技能" : "") : "已停用");
        Rectangle sr = new Rectangle(e.Bounds.X + pad, e.Bounds.Y + Theme.Px(21), e.Bounds.Width - pad * 2, Theme.Px(15));
        TextRenderer.DrawText(e.Graphics, sub, Theme.F(8.2f), sr, p.Error != null ? Theme.Err : Theme.FgFaint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

class SettingsForm : Form
{
    public AgentConfig Result;
    private readonly AgentConfig _c;
    private Panel _content;
    private Panel[] _pages;
    private UiButton[] _nav;
    private int _page;

    private ListBox _lstProviders;
    private TextBox _txtName;
    private ComboBox _cmbType;
    private TextBox _txtBase;
    private TextBox _txtKey;
    private TextBox _txtModels;
    private CheckBox _chkShowKey;
    private Label _lblHint;
    private bool _loading;

    private TextBox _txtCwd;
    private UiDrop _cmbCtx;
    private NumericUpDown _numSteps;
    private NumericUpDown _numTimeout;
    private CheckBox _chkThink;
    private UiDrop _cmbPerm;
    private Action<string> _onPermChanged;    // 设置页改权限档时通知主窗口刷新 chip
    private CheckBox _chkOutside;
    private CheckBox _chkVerify;
    private CheckBox _chkHideThink;
    private Panel _navPanel;
    private Label _lblSec;
    private UiButton _btnAddProvider, _btnDelProvider, _btnFetch;
    private RoundedPanel _lstCard;
    private FieldBox _modelsBox;
    private string _autoBase = "";      // 上一次"自动填进去"的接口地址（用户手改过就不再覆盖）
    private FieldBox _cwdBox;
    private UiButton _btnPickCwd;
    private RoundedPanel _generalCard, _aboutCard;
    // 运行日志页（位置一律由 LayoutLogPage 按页面尺寸算，不用 Anchor）
    private Panel _logPage;
    private Label _logHead, _logTip;
    private TextBox _logBox;
    private UiButton _btnLogRefresh, _btnLogOpen, _btnLogClear;

    public SettingsForm(AgentConfig config)
        : this(config, null)
    {
    }

    public SettingsForm(AgentConfig config, Action<string> onPermChanged)
    {
        _onPermChanged = onPermChanged;
        Result = config;
        _c = Json.Ser.Deserialize<AgentConfig>(Json.Ser.Serialize(config));
        _c.Migrate();
        Theme.EnsureDpi();          // 同上：先算缩放，再建控件
        AutoScaleMode = AutoScaleMode.None;

        Text = "Ahz Scribe — 设置";
        Size = new Size(1000, 660);
        MinimumSize = new Size(820, 540);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        BuildChrome();
        BuildGeneralPage();
        BuildModelPage();
        BuildAboutPage();
        ShowPage(1);
        LoadProviders();
        LoadGeneral();
        ApplyDpiScale();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundWindow(Handle);
    }

    // 和主窗口一样：按屏幕 DPI 等比放大，否则 150% 缩放下字挤在一起
    void ApplyDpiScale()
    {
        Theme.EnsureDpi();
        if (Theme.Dpi <= 1.01f) return;
        Theme.ScaleBounds(this, Theme.Dpi);
        Size = new Size(Theme.Px(1000), Theme.Px(660));
        MinimumSize = new Size(Theme.Px(820), Theme.Px(540));
        LayoutModelPage();     // 按放大后的页面尺寸重新摆一遍（否则底部按钮会被推到可视区外）
        LayoutOtherPages();
        LayoutLogPage();
        RepositionFields(this);   // 输入框内部文字也重摆一遍（防"比框宽 / 偏下"）
    }

    // 递归把所有 FieldBox 的内部输入框重摆一次
    static void RepositionFields(Control root)
    {
        foreach (Control c in root.Controls)
        {
            FieldBox fb = c as FieldBox;
            if (fb != null) fb.Reposition();
            if (c.Controls.Count > 0) RepositionFields(c);
        }
    }

    // ---------- 骨架 ----------

    void BuildChrome()
    {
        _navPanel = new Panel();
        _navPanel.Dock = DockStyle.Left;
        _navPanel.Width = 214;
        _navPanel.BackColor = Theme.Side;
        _navPanel.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.Line, 1f))
                e.Graphics.DrawLine(p, _navPanel.Width - 1, 0, _navPanel.Width - 1, _navPanel.Height);
        };

        LogoMark logo = new LogoMark();
        logo.BackColor = Theme.Side;
        logo.SetBounds(20, 22, 24, 24);
        _navPanel.Controls.Add(logo);

        Label title = new Label();
        title.Text = "设置";
        title.Font = Theme.FB(12.5f);
        title.ForeColor = Theme.Fg;
        title.AutoSize = true;
        title.BackColor = Theme.Side;
        title.Location = new Point(54, 24);
        _navPanel.Controls.Add(title);

        string[] names = new string[] { "通用设置", "模型", "运行日志", "关于" };
        string[] icons = new string[] { "gear", "spark", "list", "info" };
        _nav = new UiButton[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            UiButton b = new UiButton();
            b.Text = names[i];
            b.Tag = i;
            b.Icon = icons[i];
            b.IconSize = 16;
            b.IconGap = 9;
            b.Align = ContentAlignment.MiddleLeft;
            b.Padding = new Padding(12, 0, 0, 0);
            b.FillColor = Theme.Clear;
            b.HoverColor = Theme.Hover;
            b.TextColor = Theme.FgMid;
            b.ActiveFill = Theme.AccentSoft;
            b.ActiveText = Theme.Accent;
            b.Radius = 9;
            b.Font = Theme.F(9.5f);
            b.SetBounds(16, 74 + i * 42, 182, 36);
            b.Click += delegate(object s, EventArgs e) { ShowPage((int)((UiButton)s).Tag); };
            _navPanel.Controls.Add(b);
            _nav[i] = b;
        }

        _lblSec = new Label();
        _lblSec.Text = "改完记得点右下角「保存」";
        _lblSec.ForeColor = Theme.FgFaint;
        _lblSec.BackColor = Theme.Side;
        _lblSec.Font = Theme.F(8.2f);
        _lblSec.AutoSize = false;
        _lblSec.SetBounds(20, 74 + names.Length * 42 + 16, 182, 40);
        _navPanel.Controls.Add(_lblSec);

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = 64;
        bottom.BackColor = Theme.Bg;
        bottom.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.LineSoft, 1f))
                e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0);
        };

        UiButton ok = new UiButton();
        ok.Text = "保存";
        ok.FillColor = Theme.Accent;
        ok.HoverColor = Theme.AccentHover;
        ok.TextColor = Color.White;
        ok.Radius = 10;
        ok.Font = Theme.F(9.5f);
        ok.Size = new Size(96, 36);
        ok.Click += delegate { Save(); };

        UiButton cancel = new UiButton();
        cancel.Text = "取消";
        cancel.BorderColor = Theme.Line;
        cancel.FillColor = Color.White;
        cancel.HoverColor = Theme.Hover;
        cancel.Radius = 10;
        cancel.Font = Theme.F(9.5f);
        cancel.Size = new Size(88, 36);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);
        bottom.Layout += delegate
        {
            cancel.Location = new Point(bottom.ClientSize.Width - 22 - cancel.Width, 14);
            ok.Location = new Point(cancel.Left - 10 - ok.Width, 14);
        };

        _content = new Panel();
        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Bg;

        Controls.Add(_content);
        Controls.Add(bottom);
        Controls.Add(_navPanel);

        _pages = new Panel[4];
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i] = new Panel();
            _pages[i].Dock = DockStyle.Fill;
            _pages[i].BackColor = Theme.Bg;
            _pages[i].Visible = false;
            _content.Controls.Add(_pages[i]);
        }
        // 页面顺序必须和导航一致：0 通用设置 / 1 模型 / 2 运行日志 / 3 关于
        BuildLogPage(_pages[2]);
        // 隐藏的页从来没被撑开过（ClientSize 还停在 200x100），按它算尺寸会把卡片/输入框算小、文字被切。
        // 模型页和日志页自带这种"一改尺寸就重排"的钩子，这里给另外两页也挂上。
        _pages[0].Resize += delegate { LayoutOtherPages(); };
        _pages[3].Resize += delegate { LayoutOtherPages(); };
        AcceptButton = ok;
        CancelButton = cancel;
    }

    void ShowPage(int index)
    {
        _page = index;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
        if (index == 2) LayoutLogPage();      // 日志页按当前尺寸摆一遍（隐藏页没被撑开过，切过来才算准）
        for (int i = 0; i < _nav.Length; i++)
        {
            _nav[i].Active = i == index;
            _nav[i].Invalidate();
        }
    }

    // ---------- 小工具 ----------

    UiButton Btn(string text, int x, int y, int w, int h)
    {
        UiButton b = new UiButton();
        b.Text = text;
        b.BorderColor = Theme.Line;
        b.FillColor = Color.White;
        b.HoverColor = Theme.Hover;
        b.Radius = 9;
        b.Font = Theme.F(9f);
        b.SetBounds(x, y, w, h);
        return b;
    }

    Label Label2(string text, int x, int y)
    {
        Label l = new Label();
        l.Text = text;
        l.Location = new Point(x, y);
        l.AutoSize = true;
        l.ForeColor = Theme.FgMid;
        l.BackColor = Theme.Bg;
        return l;
    }

    Label LabelHead(string text, int x, int y)
    {
        Label l = new Label();
        l.Text = text;
        l.Location = new Point(x, y);
        l.AutoSize = true;
        l.ForeColor = Theme.Fg;
        l.BackColor = Theme.Bg;
        l.Font = Theme.FB(11f);
        return l;
    }

    FieldBox MakeField(Panel p, int x, int y, int w, int h, bool multiline)
    {
        FieldBox f = new FieldBox(multiline);
        f.SetBounds(x, y, w, h);
        f.BackColor = p.BackColor;
        p.Controls.Add(f);
        return f;
    }

    static RoundedPanel Card(Panel p, int x, int y, int w, int h)
    {
        RoundedPanel c = new RoundedPanel();
        c.Radius = 12;
        c.Fill = Color.White;
        c.BorderColor = Theme.Line;
        c.BackColor = p.BackColor;
        c.SetBounds(x, y, w, h);
        p.Controls.Add(c);
        return c;
    }

    // ---------- 通用设置 ----------

    void BuildGeneralPage()
    {
        Panel p = _pages[0];

        p.Controls.Add(LabelHead("通用设置", 28, 18));
        Label tip = Label2("工作目录、上下文和审批相关的默认值。改完点「保存」。", 28, 46);
        tip.ForeColor = Theme.FgDim;
        p.Controls.Add(tip);

        p.Controls.Add(Label2("工作目录", 28, 96));
        FieldBox cwd = MakeField(p, 120, 90, 560, 34, false);
        _cwdBox = cwd;
        _txtCwd = (TextBox)cwd.Inner;
        UiButton pick = Btn("选择…", 692, 90, 84, 34);
        _btnPickCwd = pick;
        pick.Click += delegate
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            if (Directory.Exists(_txtCwd.Text)) dlg.SelectedPath = _txtCwd.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtCwd.Text = dlg.SelectedPath;
        };
        p.Controls.Add(pick);
        Label cwdHint = Label2("agent 只能在这个目录里读写文件", 120, 128);
        cwdHint.ForeColor = Theme.FgFaint;
        cwdHint.Font = Theme.F(8.5f);
        p.Controls.Add(cwdHint);

        p.Controls.Add(Label2("上下文 num_ctx", 28, 168));
        FieldBox ctx = MakeField(p, 164, 162, 130, 34, false);
        ctx.Controls.Remove(ctx.Inner);
        _cmbCtx = new UiDrop();
        _cmbCtx.Text = "8192";
        _cmbCtx.FillColor = Color.White;
        _cmbCtx.HoverColor = Theme.Hover;
        _cmbCtx.BorderColor = Theme.Clear;
        _cmbCtx.Radius = 8;
        _cmbCtx.Font = Theme.F(9f);
        _cmbCtx.AddRange(new object[] { "2048", "4096", "8192", "16384", "32768" });
        _cmbCtx.SetBounds(4, 3, 122, 28);
        ctx.Controls.Add(_cmbCtx);
        Label ctxHint = Label2("本地模型越大越慢；云端模型由服务商决定", 306, 169);
        ctxHint.ForeColor = Theme.FgFaint;
        ctxHint.Font = Theme.F(8.5f);
        p.Controls.Add(ctxHint);

        p.Controls.Add(Label2("单轮最多工具步数", 28, 216));
        FieldBox steps = MakeField(p, 176, 210, 92, 34, false);
        steps.Controls.Remove(steps.Inner);
        _numSteps = new NumericUpDown();
        _numSteps.Minimum = 1;
        _numSteps.Maximum = 200;
        _numSteps.BorderStyle = BorderStyle.None;
        _numSteps.BackColor = Color.White;
        _numSteps.ForeColor = Theme.Fg;
        _numSteps.Font = Theme.F(9.5f);
        _numSteps.SetBounds(10, 8, 72, 22);
        steps.Controls.Add(_numSteps);

        p.Controls.Add(Label2("命令超时（秒）", 300, 216));
        FieldBox timeout = MakeField(p, 424, 210, 100, 34, false);
        timeout.Controls.Remove(timeout.Inner);
        _numTimeout = new NumericUpDown();
        _numTimeout.Minimum = 5;
        _numTimeout.Maximum = 3600;
        _numTimeout.BorderStyle = BorderStyle.None;
        _numTimeout.BackColor = Color.White;
        _numTimeout.ForeColor = Theme.Fg;
        _numTimeout.Font = Theme.F(9.5f);
        _numTimeout.SetBounds(10, 8, 80, 22);
        timeout.Controls.Add(_numTimeout);

        RoundedPanel card = Card(p, 24, 264, Math.Max(400, p.Width - 48), 182);
        _generalCard = card;
        Label ch = new Label();
        ch.Text = "审批与安全";
        ch.Font = Theme.FB(10f);
        ch.ForeColor = Theme.Fg;
        ch.BackColor = Color.White;
        ch.AutoSize = true;
        ch.Location = new Point(20, 18);
        card.Controls.Add(ch);

        _chkThink = Check2(card, "默认打开思考模式（本地小模型开着明显变慢）", 20, 52);
        // 权限档：三档（仅可查看 / 工作区内修改 / 完全权限），和输入条上那个浮层是同一套取值
        Label pl = new Label();
        pl.Text = "权限档";
        pl.Font = Theme.F(9f);
        pl.ForeColor = Theme.FgMid;
        pl.BackColor = Color.White;
        pl.AutoSize = true;
        pl.Location = new Point(20, 85);
        card.Controls.Add(pl);
        _cmbPerm = new UiDrop();
        _cmbPerm.Font = Theme.F(9f);
        _cmbPerm.Radius = 8;
        _cmbPerm.Padding = new Padding(10, 0, 10, 0);
        for (int i = 0; i < Perm.Labels.Length; i++) _cmbPerm.Items.Add(Perm.Labels[i]);
        _cmbPerm.SelectedItem = Perm.Label(_c.permission);
        _cmbPerm.SelectedIndexChanged += delegate
        {
            string lv = Perm.ValueAt(_cmbPerm.SelectedIndex);
            _c.permission = lv;
            _c.auto_yes = Perm.AutoYes(lv);
            if (_onPermChanged != null) _onPermChanged(lv);
        };
        _cmbPerm.SetBounds(90, 80, 150, 26);
        card.Controls.Add(_cmbPerm);
        _chkOutside = Check2(card, "允许访问工作目录以外的路径（关闭沙箱）", 20, 112);
        _chkVerify = Check2(card, "改完文件后自动跑一次项目验证（测试/编译），失败自动回灌给模型", 20, 142);
        _chkHideThink = Check2(card, "思考过程只进「轨迹」页（对话区不被长思考一直往上顶，推荐开）", 20, 172);
    }

    CheckBox Check2(Control parent, string text, int x, int y)
    {
        CheckBox c = new CheckBox();
        c.Text = text;
        c.Location = new Point(x, y);
        c.AutoSize = true;
        c.ForeColor = Theme.FgMid;
        c.BackColor = parent.BackColor;
        parent.Controls.Add(c);
        return c;
    }

    void LoadGeneral()
    {
        _txtCwd.Text = _c.cwd;
        _cmbCtx.SelectedItem = _c.num_ctx.ToString();
        if (_cmbCtx.SelectedIndex < 0) _cmbCtx.SelectedItem = "8192";
        _numSteps.Value = Math.Max(1, Math.Min(200, _c.max_steps));
        _numTimeout.Value = Math.Max(5, Math.Min(3600, _c.shell_timeout));
        _chkThink.Checked = _c.think;
        _cmbPerm.SelectedItem = Perm.Label(_c.permission);
        _chkOutside.Checked = _c.allow_outside;
        _chkVerify.Checked = _c.auto_verify;
        _chkHideThink.Checked = _c.hide_thinking;
    }

    // ---------- 模型 ----------

    // ---------- 常见平台的接口地址预设：名称（或模型名）里出现关键字就自动填 ----------

    class Preset
    {
        public string keys;         // 关键字，| 分隔，小写
        public string name;         // 建议的提供商名称
        public string type;         // openai / ollama
        public string baseUrl;      // 接口地址
        public string note;         // 申请 Key 的地方（提示用）
    }

    // 这些地址都用"GET {base}/models 返回 401/403（说明接口真实存在，只差 Key）"验过；
    // 验不过的（比如已下线的旧接口）不放进来，宁缺毋滥。
    static readonly Preset[] Presets = new Preset[]
    {
        new Preset { keys = "deepseek|深度求索", name = "DeepSeek", type = "openai",
            baseUrl = "https://api.deepseek.com/v1", note = "platform.deepseek.com" },
        new Preset { keys = "glm|智谱|zhipu|bigmodel|chatglm", name = "智谱 GLM", type = "openai",
            baseUrl = "https://open.bigmodel.cn/api/paas/v4", note = "bigmodel.cn" },
        new Preset { keys = "kimi|moonshot|月之暗面", name = "Kimi（Moonshot）", type = "openai",
            baseUrl = "https://api.moonshot.cn/v1", note = "platform.moonshot.cn" },
        new Preset { keys = "qwen|通义|dashscope|百炼|alibaba", name = "通义千问（DashScope）", type = "openai",
            baseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1", note = "bailian.console.aliyun.com" },
        new Preset { keys = "doubao|豆包|方舟|volces", name = "豆包（火山方舟）", type = "openai",
            baseUrl = "https://ark.cn-beijing.volces.com/api/v3", note = "console.volcengine.com/ark" },
        new Preset { keys = "hunyuan|混元|腾讯", name = "腾讯混元", type = "openai",
            baseUrl = "https://api.hunyuan.cloud.tencent.com/v1", note = "cloud.tencent.com" },
        new Preset { keys = "qianfan|千帆|文心|ernie|baidu|百度", name = "百度千帆", type = "openai",
            baseUrl = "https://qianfan.baidubce.com/v2", note = "console.bce.baidu.com/qianfan" },
        new Preset { keys = "spark|星火|讯飞", name = "讯飞星火", type = "openai",
            baseUrl = "https://spark-api-open.xf-yun.com/v1", note = "console.xfyun.cn" },
        new Preset { keys = "siliconflow|硅基流动|硅基", name = "硅基流动", type = "openai",
            baseUrl = "https://api.siliconflow.cn/v1", note = "cloud.siliconflow.cn" },
        new Preset { keys = "openrouter", name = "OpenRouter", type = "openai",
            baseUrl = "https://openrouter.ai/api/v1", note = "openrouter.ai" },
        new Preset { keys = "minimax|海螺", name = "MiniMax", type = "openai",
            baseUrl = "https://api.minimax.chat/v1", note = "platform.minimaxi.com" },
        new Preset { keys = "step|阶跃", name = "阶跃星辰", type = "openai",
            baseUrl = "https://api.stepfun.com/v1", note = "platform.stepfun.com" },
        new Preset { keys = "openai|gpt|o1|o3", name = "OpenAI", type = "openai",
            baseUrl = "https://api.openai.com/v1", note = "platform.openai.com" },
        new Preset { keys = "grok|xai|x.ai", name = "xAI Grok", type = "openai",
            baseUrl = "https://api.x.ai/v1", note = "console.x.ai" },
        new Preset { keys = "groq", name = "Groq", type = "openai",
            baseUrl = "https://api.groq.com/openai/v1", note = "console.groq.com" },
        new Preset { keys = "mistral", name = "Mistral", type = "openai",
            baseUrl = "https://api.mistral.ai/v1", note = "console.mistral.ai" },
        new Preset { keys = "ollama|本地|localhost", name = "Ollama 本地", type = "ollama",
            baseUrl = "http://127.0.0.1:11434", note = "本地服务" },
    };

    // 名称或模型名里出现关键字就返回对应预设
    static Preset MatchPreset(string text)
    {
        if (text == null) return null;
        string t = text.Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        foreach (Preset ps in Presets)
        {
            string[] ks = ps.keys.Split('|');
            foreach (string k in ks)
                if (k.Length > 0 && t.IndexOf(k, StringComparison.Ordinal) >= 0) return ps;
        }
        return null;
    }

    // 重名就加个序号，别让两个提供商叫一样的名字
    string UniqueName(string want)
    {
        string n = want;
        int i = 2;
        while (true)
        {
            bool hit = false;
            foreach (Provider p in _c.providers) if (p.name == n) { hit = true; break; }
            if (!hit) return n;
            n = want + " " + i;
            i++;
        }
    }

    void BuildModelPage()
    {
        Panel p = _pages[1];

        p.Controls.Add(LabelHead("模型提供商", 24, 18));
        Label hint2 = new Label();
        hint2.Text = "填好接口地址和 API Key 就能用云端模型（名称里写 deepseek / glm / kimi / 通义 会自动填接口地址）。\n"
            + "要加新平台点左下角「＋ 添加提供商」（带常见平台预设）；在右边改「名称」只会改当前选中的那一个。";
        hint2.AutoSize = false;
        hint2.SetBounds(24, 44, 720, 42);
        hint2.ForeColor = Theme.FgDim;
        p.Controls.Add(hint2);

        RoundedPanel listCard = Card(p, 24, 92, 240, 396);
        _lstCard = listCard;
        listCard.Padding = new Padding(5);
        _lstProviders = new ListBox();
        _lstProviders.Dock = DockStyle.Fill;
        _lstProviders.BorderStyle = BorderStyle.None;
        _lstProviders.BackColor = Color.White;
        _lstProviders.ForeColor = Theme.Fg;
        _lstProviders.Font = Theme.F(9.5f);
        _lstProviders.DrawMode = DrawMode.OwnerDrawFixed;
        _lstProviders.ItemHeight = 30;
        _lstProviders.IntegralHeight = false;
        _lstProviders.DrawItem += delegate(object s, DrawItemEventArgs e)
        {
            Provider pv = e.Index >= 0 && e.Index < _lstProviders.Items.Count ? _lstProviders.Items[e.Index] as Provider : null;
            Color bg = Color.White;
            if (pv != null && (e.State & DrawItemState.Selected) == DrawItemState.Selected) bg = Theme.Sel;
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            if (pv == null) return;
            Rectangle tr = new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, pv.name, Theme.F(9.5f), tr, Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            string sub = pv.type == "ollama" ? "本地" : "云端";
            TextRenderer.DrawText(e.Graphics, sub, Theme.F(8.2f), tr, Theme.FgFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
        _lstProviders.SelectedIndexChanged += delegate { LoadProviderFields(); };
        listCard.Controls.Add(_lstProviders);

        // 新增：点一下弹出"预设 / 空白"，选预设就直接建好一个（接口地址自动填）
        _btnAddProvider = Btn("＋ 添加提供商", 24, 496, 156, 34);
        _btnAddProvider.Click += delegate { ShowAddProviderMenu(_btnAddProvider); };
        p.Controls.Add(_btnAddProvider);

        _btnDelProvider = Btn("删除", 188, 496, 76, 34);
        _btnDelProvider.Click += delegate
        {
            Provider sel = _lstProviders.SelectedItem as Provider;
            if (sel == null) return;
            if (_c.providers.Count <= 1) { MessageBox.Show(this, "至少要保留一个提供商。", "Ahz Scribe"); return; }
            if (MessageBox.Show(this, "确定删除「" + sel.name + "」？", "Ahz Scribe", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _c.providers.Remove(sel);
            RefreshProviderList();
            _lblHint.Text = "已删除「" + sel.name + "」";
        };
        p.Controls.Add(_btnDelProvider);

        int lx = 272, fx = 380;
        p.Controls.Add(Label2("名称", lx, 88));
        FieldBox nameB = MakeField(p, fx, 80, 370, 34, false);      // 四行输入框统一 370 宽，看着才齐
        _txtName = (TextBox)nameB.Inner;
        _txtName.TextChanged += delegate
        {
            StoreProviderFields();
            ApplyPresetToFields();          // 名称里出现 glm / kimi / 通义 … 就自动把接口地址填上
        };

        p.Controls.Add(Label2("类型", lx, 136));
        FieldBox typeB = MakeField(p, fx, 128, 370, 34, false);
        typeB.Controls.Remove(typeB.Inner);
        _cmbType = new ComboBox();
        _cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbType.FlatStyle = FlatStyle.Flat;
        _cmbType.BackColor = Color.White;
        _cmbType.ForeColor = Theme.Fg;
        _cmbType.Font = Theme.F(9.5f);
        _cmbType.Items.AddRange(new object[] { "本地 Ollama", "OpenAI 兼容接口（DeepSeek / OpenAI / Kimi / 智谱 …）" });
        _cmbType.SetBounds(8, 5, 344, 24);
        _cmbType.SelectedIndexChanged += delegate { StoreProviderFields(); };
        typeB.Controls.Add(_cmbType);

        p.Controls.Add(Label2("接口地址", lx, 184));
        FieldBox baseB = MakeField(p, fx, 176, 370, 34, false);
        _txtBase = (TextBox)baseB.Inner;
        _txtBase.TextChanged += delegate { StoreProviderFields(); };
        Label bh = Label2("例如 https://api.deepseek.com/v1 或 http://127.0.0.1:11434", fx, 214);
        bh.ForeColor = Theme.FgFaint;
        bh.Font = Theme.F(8.5f);
        p.Controls.Add(bh);

        p.Controls.Add(Label2("API Key", lx, 246));
        FieldBox keyB = MakeField(p, fx, 238, 370, 34, false);      // 智谱的 Key 很长，这里给满剩下的宽度
        _txtKey = (TextBox)keyB.Inner;
        _txtKey.UseSystemPasswordChar = true;
        _txtKey.TextChanged += delegate
        {
            StoreProviderFields();
            // 填了 Key、有地址、但模型列表还是空的：提示一下可以一键拉取
            if (_txtModels != null && _txtModels.Text.Trim().Length == 0
                && _txtBase.Text.Trim().Length > 0 && _txtKey.Text.Trim().Length > 0)
                _lblHint.Text = "点「从接口拉取模型」就能拿到这个 Key 能用的模型";
        };
        _chkShowKey = new CheckBox();
        _chkShowKey.Text = "显示";
        // 「显示」挪到"API Key"标签和输入框之间：这样密钥框能吃到整行剩下的宽度（长 Key 才看得全）
        _chkShowKey.Location = new Point(lx + 62, 246);
        _chkShowKey.AutoSize = true;
        _chkShowKey.ForeColor = Theme.FgMid;
        _chkShowKey.CheckedChanged += delegate { _txtKey.UseSystemPasswordChar = !_chkShowKey.Checked; };
        p.Controls.Add(_chkShowKey);

        p.Controls.Add(Label2("模型列表（每行一个）", lx, 286));
        FieldBox modelsB = MakeField(p, fx, 306, 370, 172, true);
        _modelsBox = modelsB;
        _txtModels = (TextBox)modelsB.Inner;
        _txtModels.Multiline = true;
        _txtModels.ScrollBars = ScrollBars.Vertical;
        _txtModels.WordWrap = false;
        _txtModels.TextChanged += delegate
        {
            StoreProviderFields();
            // 模型名里出现 glm / 通义 … 也顺手把接口地址填上
            if (_txtModels.Focused) ApplyPresetToFields();
        };

        _btnFetch = Btn("从接口拉取模型", fx, 486, 148, 34);
        _btnFetch.Click += delegate { FetchModels(); };
        p.Controls.Add(_btnFetch);

        // 提示语放在按钮下面一整行：原来贴在按钮右边，长一点就被挤出屏幕外（"格式乱了"）
        _lblHint = Label2("", fx, 526);
        _lblHint.AutoSize = false;
        _lblHint.TextAlign = ContentAlignment.MiddleLeft;
        _lblHint.SetBounds(fx, 526, 540, 22);
        _lblHint.ForeColor = Theme.FgDim;
        p.Controls.Add(_lblHint);

        // 页面一改尺寸就重排：按钮按"当前页面高度"算位置，绝不再被推到可视区外
        p.Resize += delegate { LayoutModelPage(); };
        LayoutModelPage();
    }

    // ---------- 模型页排版（所有位置都按页面当前像素算）----------

    // 通用设置页 / 关于页：这两页原来靠 Anchor 拉伸，DPI 放大后会被拉出可视区，这里统一改成显式排版
    void LayoutOtherPages()
    {
        Panel pg = _pages[0];
        if (pg != null && _cwdBox != null)
        {
            int pageW = pg.ClientSize.Width;
            int pickW = Theme.Px(84);
            int left = Theme.Px(120);
            _btnPickCwd.SetBounds(pageW - Theme.Px(24) - pickW, Theme.Px(90), pickW, Theme.Px(34));
            _cwdBox.SetBounds(left, Theme.Px(90), Math.Max(Theme.Px(160), _btnPickCwd.Left - Theme.Px(12) - left), Theme.Px(34));
            if (_generalCard != null)
            {
                // 卡片高度按里面最后一个复选框算，别写死：写死的话 DPI 放大后第 5 行会被卡片下边缘切掉
                int cardTop = Theme.Px(264);
                int cardH = Theme.Px(182);
                if (_chkHideThink != null) cardH = Math.Max(cardH, _chkHideThink.Bottom + Theme.Px(18));
                _generalCard.SetBounds(Theme.Px(24), cardTop, Math.Max(Theme.Px(300), pageW - Theme.Px(48)), cardH);
            }
        }
        Panel pa = _pages[3];
        if (pa != null && _aboutCard != null)
            _aboutCard.SetBounds(Theme.Px(28), Theme.Px(92), Math.Max(Theme.Px(300), pa.ClientSize.Width - Theme.Px(56)), Theme.Px(250));
    }

    // ---------- 运行日志页 ----------

    void BuildLogPage(Panel p)
    {
        _logPage = p;

        _logHead = LabelHead("运行日志", 28, 18);
        p.Controls.Add(_logHead);
        _logTip = Label2("记录审批决定、敏感操作与异常，可追溯 agent 每一步（文件：Ahz错误日志.txt）", 28, 46);
        _logTip.ForeColor = Theme.FgDim;
        p.Controls.Add(_logTip);

        _logBox = new TextBox();
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Both;
        _logBox.WordWrap = false;
        _logBox.Font = new Font("Consolas", 9.5f);
        _logBox.BackColor = Color.White;
        _logBox.ForeColor = Theme.Fg;
        _logBox.BorderStyle = BorderStyle.FixedSingle;
        // 位置/大小交给 LayoutLogPage：Anchor 在 DPI 放大后会把控件推出可视区
        p.Controls.Add(_logBox);

        _btnLogRefresh = Btn("刷新", 0, 0, 88, 34);
        _btnLogRefresh.Click += delegate { LoadLogText(); };
        p.Controls.Add(_btnLogRefresh);

        _btnLogOpen = Btn("打开日志文件", 0, 0, 140, 34);
        _btnLogOpen.Click += delegate
        {
            try { System.Diagnostics.Process.Start("notepad.exe", Log.FilePath); }
            catch (Exception ex) { Log.Swallow(ex, "打开日志文件"); }
        };
        p.Controls.Add(_btnLogOpen);

        _btnLogClear = Btn("清空", 0, 0, 88, 34);
        _btnLogClear.Click += delegate
        {
            try { if (File.Exists(Log.FilePath)) File.Delete(Log.FilePath); }
            catch (Exception ex) { Log.Swallow(ex, "清空日志"); }
            LoadLogText();
        };
        p.Controls.Add(_btnLogClear);

        p.Resize += delegate { LayoutLogPage(); };
        LayoutLogPage();
        LoadLogText();
    }

    void LoadLogText()
    {
        if (_logBox == null) return;
        _logBox.Text = Log.Read();
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    // 位置全按页面实际尺寸算：日志框撑满（底部给按钮留一条），按钮排在框下面
    void LayoutLogPage()
    {
        if (_logPage == null || _logBox == null || _btnLogRefresh == null) return;
        int pageW = _logPage.ClientSize.Width;
        int pageH = _logPage.ClientSize.Height;
        int pad = Theme.Px(28);
        int top = Theme.Px(92);
        int btnH = Theme.Px(34);
        int gap = Theme.Px(12);
        int boxH = Math.Max(Theme.Px(120), pageH - top - btnH - gap - Theme.Px(24));
        _logBox.SetBounds(pad, top, Math.Max(Theme.Px(200), pageW - pad * 2), boxH);

        int by = top + boxH + gap;
        int w1 = Theme.Px(88), w2 = Theme.Px(140), w3 = Theme.Px(88);
        _btnLogRefresh.SetBounds(pad, by, w1, btnH);
        _btnLogOpen.SetBounds(pad + w1 + Theme.Px(10), by, w2, btnH);
        _btnLogClear.SetBounds(pad + w1 + Theme.Px(10) + w2 + Theme.Px(10), by, w3, btnH);
    }

    void LayoutModelPage()
    {
        Panel p = _pages[1];
        if (p == null || _modelsBox == null || _btnAddProvider == null) return;
        int pageH = p.ClientSize.Height;
        int pageW = p.ClientSize.Width;

        int barY = Math.Max(Theme.Px(120), pageH - Theme.Px(56));
        _btnAddProvider.SetBounds(Theme.Px(24), barY, Theme.Px(156), Theme.Px(34));
        _btnDelProvider.SetBounds(Theme.Px(188), barY, Theme.Px(76), Theme.Px(34));

        int listTop = Theme.Px(306);
        int listH = Math.Max(Theme.Px(70), barY - Theme.Px(96) - listTop);
        int fw = Math.Max(Theme.Px(200), Math.Min(Theme.Px(370), pageW - Theme.Px(400) - Theme.Px(16)));
        _modelsBox.SetBounds(Theme.Px(400), listTop, fw, listH);
        _btnFetch.SetBounds(Theme.Px(400), listTop + listH + Theme.Px(8), Theme.Px(148), Theme.Px(34));
        if (_lblHint != null)
            _lblHint.SetBounds(Theme.Px(400), listTop + listH + Theme.Px(46),
                Math.Max(Theme.Px(140), pageW - Theme.Px(400) - Theme.Px(16)), Theme.Px(22));

        // 左列列表跟着页面高度走
        if (_lstCard != null)
            _lstCard.SetBounds(Theme.Px(24), Theme.Px(92), Theme.Px(240), Math.Max(Theme.Px(120), barY - Theme.Px(92) - Theme.Px(8)));
    }

    // 点「＋ 添加提供商」弹出的菜单：空白 or 常见平台预设
    void ShowAddProviderMenu(Control anchor)
    {
        StoreProviderFields();
        ContextMenuStrip m = new ContextMenuStrip();
        m.Items.Add("空白（自己填接口地址）", null, delegate { AddProviderFromPreset(null); });
        m.Items.Add(new ToolStripSeparator());
        foreach (Preset ps in Presets)
        {
            Preset cap = ps;
            m.Items.Add("添加：" + ps.name + "（" + ps.note + "）", null, delegate { AddProviderFromPreset(cap); });
        }
        m.Show(anchor, new Point(0, anchor.Height + 2));
    }

    void AddProviderFromPreset(Preset ps)
    {
        Provider np = new Provider();
        if (ps != null)
        {
            np.name = UniqueName(ps.name);
            np.type = ps.type;
            np.base_url = ps.baseUrl;
            if (ps.type == "ollama") np.models = new List<string>();
        }
        else
        {
            np.name = UniqueName("新提供商");
            np.type = "openai";
            np.base_url = "";
        }
        _c.providers.Add(np);
        RefreshProviderList();
        _lstProviders.SelectedIndex = _lstProviders.Items.Count - 1;   // 选中新加的那个
        _autoBase = ps != null ? ps.baseUrl : "";
        _lblHint.Text = ps != null
            ? ("已新增「" + np.name + "」，填上 API Key 再点「从接口拉取模型」")
            : "已新增一个空白提供商，接口地址自己填";
    }

    // 名称 / 模型名里出现已知平台关键字 → 自动把类型和接口地址填好（不覆盖你手改过的地址）
    void ApplyPresetToFields()
    {
        if (_loading) return;
        Provider p = _lstProviders.SelectedItem as Provider;
        if (p == null) return;
        Preset ps = MatchPreset(_txtName.Text);
        if (ps == null && _txtModels != null)
        {
            // 名称没写，就看模型名（比如第一行填 glm-4）
            string first = _txtModels.Text.Replace("\r\n", "\n").Split('\n')[0];
            ps = MatchPreset(first);
        }
        if (ps == null) return;
        string cur = _txtBase.Text.Trim();
        if (cur.Length > 0 && cur != _autoBase) return;      // 手动填过地址，不动它
        _loading = true;
        _cmbType.SelectedIndex = ps.type == "ollama" ? 0 : 1;
        _txtBase.Text = ps.baseUrl;
        _loading = false;
        _autoBase = ps.baseUrl;
        StoreProviderFields();
        _lblHint.Text = "已按「" + ps.name + "」填好接口地址；填上 API Key 后点「从接口拉取模型」";
    }

    void RefreshProviderList()
    {
        _loading = true;
        _lstProviders.Items.Clear();
        foreach (Provider p in _c.providers) _lstProviders.Items.Add(p);
        _loading = false;
        if (_lstProviders.Items.Count > 0 && _lstProviders.SelectedIndex < 0) _lstProviders.SelectedIndex = 0;
    }

    void LoadProviders()
    {
        RefreshProviderList();
        if (_lstProviders.Items.Count == 0)
        {
            _c.providers.Add(Provider.DefaultOllama());
            RefreshProviderList();
        }
        Provider cur = _c.Current();
        for (int i = 0; i < _lstProviders.Items.Count; i++)
            if (object.ReferenceEquals(_lstProviders.Items[i], cur)) { _lstProviders.SelectedIndex = i; break; }
    }

    void LoadProviderFields()
    {
        if (_loading) return;
        Provider p = _lstProviders.SelectedItem as Provider;
        if (p == null) return;
        _loading = true;
        _txtName.Text = p.name;
        _cmbType.SelectedIndex = p.type == "ollama" ? 0 : 1;
        _txtBase.Text = p.base_url;
        _txtKey.Text = p.api_key;
        _txtModels.Text = p.models == null ? "" : string.Join("\r\n", p.models.ToArray());
        _lblHint.Text = "";
        _loading = false;
    }

    void StoreProviderFields()
    {
        if (_loading) return;
        Provider p = _lstProviders.SelectedItem as Provider;
        if (p == null) return;
        p.name = _txtName.Text.Trim();
        p.type = _cmbType.SelectedIndex == 0 ? "ollama" : "openai";
        p.base_url = _txtBase.Text.Trim();
        p.api_key = _txtKey.Text.Trim();
        List<string> models = new List<string>();
        string[] lines = _txtModels.Text.Replace("\r\n", "\n").Split('\n');
        foreach (string line in lines)
        {
            string m = line.Trim();
            if (m.Length > 0 && !models.Contains(m)) models.Add(m);
        }
        p.models = models;
        int idx = _lstProviders.SelectedIndex;
        if (idx >= 0)
        {
            _loading = true;
            _lstProviders.Items[idx] = p;
            _lstProviders.SelectedIndex = idx;
            _loading = false;
        }
    }

    void FetchModels()
    {
        StoreProviderFields();
        Provider p = _lstProviders.SelectedItem as Provider;
        if (p == null) return;
        if (p.base_url.Trim().Length == 0)
        {
            _lblHint.Text = "先填接口地址";
            MessageBox.Show(this, "还没有填接口地址。\n在「名称」里写平台名（deepseek / glm / kimi …）会自动填上。",
                "拉取模型列表", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _btnFetch.Enabled = false;
        _lblHint.Text = "正在拉取…";
        Application.DoEvents();
        try
        {
            ILlm llm = LlmFactory.Create(p, "");
            List<string> models = llm.ListModels();
            llm.Close();
            if (models.Count == 0)
            {
                _lblHint.Text = "接口没返回任何模型";
                return;
            }
            _lblHint.Text = MergeModelList(models);
        }
        catch (Exception ex)
        {
            _lblHint.Text = "拉取失败";
            MessageBox.Show(this, ex.Message + "\n\n检查一下接口地址、API Key，以及这个平台是否兼容 OpenAI 的 /models 接口。",
                "拉取模型列表失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnFetch.Enabled = true;
        }
    }

    // 把拉到的模型并进"模型列表"：已有的保留，只补新的（换 Key / 重拉都不会把你手写的删掉）
    string MergeModelList(List<string> fetched)
    {
        List<string> have = new List<string>();
        foreach (string line in _txtModels.Text.Replace("\r\n", "\n").Split('\n'))
        {
            string m = line.Trim();
            if (m.Length > 0 && !have.Contains(m)) have.Add(m);
        }
        int added = 0;
        foreach (string m in fetched)
        {
            if (m == null || m.Trim().Length == 0) continue;
            if (!have.Contains(m)) { have.Add(m); added++; }
        }
        _loading = true;
        _txtModels.Text = string.Join("\r\n", have.ToArray());
        _loading = false;
        StoreProviderFields();
        return added > 0
            ? ("接口返回 " + fetched.Count + " 个，新增 " + added + " 个（共 " + have.Count + " 个）")
            : ("接口返回 " + fetched.Count + " 个，都已经在列表里了");
    }

    // ---------- 关于 ----------

    void BuildAboutPage()
    {
        Panel p = _pages[3];

        LogoMark logo = new LogoMark();
        logo.BackColor = Theme.Bg;
        logo.SetBounds(28, 26, 40, 40);
        p.Controls.Add(logo);

        Label t = new Label();
        t.Text = "Ahz Scribe";
        t.ForeColor = Theme.Fg;
        t.BackColor = Theme.Bg;
        t.Font = Theme.FB(16f);
        t.AutoSize = true;
        t.Location = new Point(80, 26);
        p.Controls.Add(t);

        Label v = Label2("版本 " + Program.Version + "  ·  本地 / 云端模型通用桌面智能体", 82, 54);
        v.ForeColor = Theme.FgDim;
        p.Controls.Add(v);

        RoundedPanel card = Card(p, 28, 92, Math.Max(420, p.Width - 56), 250);
        _aboutCard = card;

        Label d = new Label();
        d.Text = "一个能读写文件、执行命令的桌面智能体。\n"
            + "默认只用本地 Ollama（数据和命令都在你自己机器上）；\n"
            + "也可以在「模型」页添加云端提供商，填上 API Key 即可使用。\n\n"
            + "· 文件操作限制在工作目录内\n"
            + "· 写文件 / 改文件 / 执行命令前会征求你同意\n"
            + "· 危险命令（format、diskpart、shutdown 等）直接拒绝\n"
            + "· 对话记录保存在程序目录的 sessions.json\n\n"
            + "API Key 保存在程序目录的 config.json 里（明文），请注意保管。";
        d.Location = new Point(22, 20);
        d.AutoSize = true;
        d.ForeColor = Theme.FgMid;
        d.BackColor = Color.White;
        card.Controls.Add(d);
    }

    // ---------- 保存 ----------

    void Save()
    {
        StoreProviderFields();
        _c.cwd = _txtCwd.Text.Trim();
        int v;
        if (int.TryParse(Convert.ToString(_cmbCtx.SelectedItem), out v)) _c.num_ctx = v;
        _c.max_steps = (int)_numSteps.Value;
        _c.shell_timeout = (int)_numTimeout.Value;
        _c.think = _chkThink.Checked;
        _c.auto_yes = Perm.AutoYes(_c.permission);
        _c.allow_outside = _chkOutside.Checked;
        _c.auto_verify = _chkVerify.Checked;
        _c.hide_thinking = _chkHideThink.Checked;

        // 保证当前选中的提供商/模型仍然有效
        Provider cur = _c.Current();
        bool has = false;
        foreach (string m in cur.models) if (m == _c.model) has = true;
        if (!has) _c.model = cur.models.Count > 0 ? cur.models[0] : "";

        Result = _c;
        DialogResult = DialogResult.OK;
        Close();
    }
}

// ============================ 控制台入口（CLI 版） ============================

static class CliProgram
{
    public static int Run(string[] args)
    {
        Program.EnsureTls();
        // 低完整性 / 临时目录写不进：先自检并把话说清楚（CLI 也常被别的程序当子进程拉起来）
        StartupCheck.Run(AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory);
        if (StartupCheck.Warning.Length > 0)
            Console.WriteLine("  " + StartupCheck.Warning.Replace("\n", "\n  ") + "\n");
        AgentConfig config = AgentConfig.Load();
        config.Migrate();
        string prompt = null;
        bool listOnly = false;
        bool snapList = false;
        string snapRestore = null;
        List<string> words = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--cli") continue;
            if (a == "--version") { Console.WriteLine("Ahz Scribe v" + Program.Version); return 0; }
            if ((a == "-m" || a == "--model") && i + 1 < args.Length) { config.model = args[++i]; continue; }
            if ((a == "-p" || a == "--provider") && i + 1 < args.Length)
            {
                string want = args[++i];
                foreach (Provider pp in config.providers)
                    if (pp.id == want || pp.name == want) { config.provider = pp.id; break; }
                continue;
            }
            if ((a == "-C" || a == "--cwd") && i + 1 < args.Length) { config.cwd = args[++i]; continue; }
            if (a == "--ctx" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) config.num_ctx = v; continue; }
            if (a == "--think") { config.think = true; continue; }
            if (a == "--effort" && i + 1 < args.Length)
            {
                string e = args[++i].ToLowerInvariant();
                if (e == "auto" || e == "low" || e == "medium" || e == "high") config.effort = e;
                continue;
            }
            if (a == "-y" || a == "--yes") { config.auto_yes = true; continue; }
            if (a == "--models") { listOnly = true; continue; }
            // ---- 快照：列出 / 恢复（升级需求 P0-2，弥补这台机器没有 git）----
            if (a == "--snapshots") { snapList = true; continue; }
            if (a == "--restore" && i + 1 < args.Length) { snapRestore = args[++i]; continue; }
            words.Add(a);
        }
        if (words.Count > 0) prompt = string.Join(" ", words.ToArray());

        Console.OutputEncoding = Encoding.UTF8;
        Console.ForegroundColor = ConsoleColor.Gray;

        // 这两个是纯本地动作，不需要连模型，先处理掉
        if (snapList)
        {
            List<string> lines = SnapStore.List(config.cwd);
            if (lines.Count == 0) Console.WriteLine("还没有快照（改过文件之后会自动存到 .snapshots\\）。");
            else { Console.WriteLine("快照（新→旧，共 " + lines.Count + " 个）："); foreach (string l in lines) Console.WriteLine("  " + l); }
            return 0;
        }
        if (snapRestore != null)
        {
            int okR, failR;
            string msg = SnapStore.Restore(config.cwd, snapRestore, out okR, out failR);
            Console.WriteLine(msg);
            return okR > 0 ? 0 : 1;
        }

        Provider provider = config.Current();

        if (listOnly)
        {
            // 拉取该提供商的模型列表（和设置窗口里「从接口拉取模型列表」走同一条路）
            try
            {
                ILlm probe = LlmFactory.Create(provider, config.model);
                List<string> names = probe.ListModels();
                probe.Close();
                foreach (string nm in names) Console.WriteLine(nm);
                return 0;
            }
            catch (LocalAgentException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  ✗ " + ex.Message);
                Console.ResetColor();
                return 1;
            }
        }

        Console.WriteLine();
        Console.WriteLine("  Ahz Scribe v" + Program.Version + "  (CLI)");
        Console.WriteLine("  提供商 " + provider.name + "（" + provider.type + "） · 模型 " + config.model
            + " · 目录 " + config.cwd + " · ctx " + config.num_ctx);
        Console.WriteLine();

        ConsoleUI ui = new ConsoleUI();
        AgentRunner runner;
        try
        {
            runner = new AgentRunner(config, ui);
            runner.ProbeModel();
        }
        catch (LocalAgentException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ✗ " + ex.Message);
            Console.ResetColor();
            return 1;
        }
        catch (Exception ex)
        {
            // 兜底：以前这里只 catch LocalAgentException，别的异常直接逃出去 ——
            // 表现是"打完提供商那行就没了"、退出码 -532462766，连堆栈都看不到（实测踩过）。
            // 任何初始化期的意外都要说清楚是什么错、在哪一行。
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ✗ 启动时出错：" + ex.GetType().FullName);
            Console.WriteLine("    " + ex.Message);
            Console.WriteLine("    " + (ex.StackTrace == null ? "(没有堆栈)" : ex.StackTrace.Split('\n')[0].Trim()));
            if (ex.InnerException != null)
                Console.WriteLine("    内层：" + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
            Console.ResetColor();
            return 1;
        }
        string warn = runner.Warning();
        if (warn != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ⚠ " + warn);
            Console.ResetColor();
        }

        if (prompt != null)
        {
            try { runner.RunTurn(prompt); }
            catch (LocalAgentException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  ✗ " + ex.Message);
                Console.ResetColor();
                return 1;
            }
            return 0;
        }

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("› ");
            Console.ResetColor();
            string line;
            try { line = Console.ReadLine(); }
            catch { return 0; }
            if (line == null) return 0;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line == "/exit" || line == "/quit") return 0;
            if (line == "/help")
            {
                Console.WriteLine("  /exit 退出   /clear 清空对话");
                continue;
            }
            if (line == "/clear")
            {
                runner.Messages.Clear();
                runner.Messages.Add(AgentRunner.Msg("system", ""));
                Console.WriteLine("  已清空对话");
                continue;
            }
            try { runner.RunTurn(line); }
            catch (LocalAgentException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  ✗ " + ex.Message);
                Console.ResetColor();
            }
        }
    }
}

// ============================ 程序入口 ============================

static class Program
{
    public const string Version = "1.1.0";

    static bool _tlsReady;

    // .NET Framework 默认只开 SSL3 + TLS1.0；智谱、混元、硅基流动这些平台只收 TLS1.2+，
    // 不打开就会报"未能创建 SSL/TLS 安全通道"，看起来像 API Key 不对（其实是握手就被拒了）。
    public static void EnsureTls()
    {
        if (_tlsReady) return;
        _tlsReady = true;
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }   // TLS 1.3（.NET 4.8 / Win11）
    }

    static bool HasFlag(string[] args, string flag)
    {
        foreach (string a in args) if (a == flag) return true;
        return false;
    }

    [STAThread]
    static int Main(string[] args)
    {
#if CLI_BUILD
        return CliProgram.Run(args);
#else
        if (HasFlag(args, "--version")) { Console.WriteLine("Ahz Scribe v" + Program.Version); return 0; }
        if (HasFlag(args, "--cli")) return CliProgram.Run(args);
        // 单实例：双开两个窗口会同时读写 sessions.json / config.json，容易把数据写坏。
        bool createdNew;
        System.Threading.Mutex single = new System.Threading.Mutex(true, "AhzScribe_SingleInstance", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Ahz Scribe 已经在运行了。\n请使用已打开的那个窗口；数据文件不能被两个实例同时写。",
                "Ahz Scribe", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        try
        {
            try { single.ReleaseMutex(); } catch { }
        }
        finally { }
        GC.KeepAlive(single);
        EnsureTls();                       // 先把 TLS1.2/1.3 打开，否则连智谱这类平台会在握手就失败
        StartupCheck.Run(AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Application.Run(new MainForm(HasFlag(args, "--settings")));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Ahz Scribe 启动失败：\n" + ex.Message, "Ahz Scribe", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { single.ReleaseMutex(); } catch { }
        }
        return 0;
#endif
    }
}
