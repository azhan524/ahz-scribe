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
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CefSharp;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Ahz Scribe")]
[assembly: System.Reflection.AssemblyProduct("Ahz Scribe")]
[assembly: System.Reflection.AssemblyDescription("本地 / 云端模型通用桌面智能体")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

// ============================ 基础工具 ============================


// 新代码走 HttpClient；旧流式接口仍保留 HttpWebRequest（它要按行读流+取消）。
static class HttpJson
{
    public static string Get(string url, int timeoutMs, string apiKey)
    {
        HttpClientHandler h = new HttpClientHandler();
        h.UseProxy = false;
        h.AllowAutoRedirect = true;
        using (HttpClient c = new HttpClient(h))
        {
            c.Timeout = TimeSpan.FromMilliseconds(timeoutMs > 0 ? timeoutMs : 30000);
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrEmpty(apiKey))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
                using (HttpResponseMessage resp = c.SendAsync(req).GetAwaiter().GetResult())
                {
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                        throw new LocalAgentException("HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase + "：" + body);
                    return body;
                }
            }
        }
    }
}

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

// ============================ 图片（多模态） ============================
// 输入框里粘进来的图片会以文件路径的形式出现在用户消息里。这里负责把这些路径
// 变成模型真能"看到"的图片数据：
//   · Ollama /api/chat       → 消息上的 images 字段（base64 列表）
//   · OpenAI 兼容 /chat/completions → content 数组里的 image_url（data: URL）
// 只给"最后一条用户消息"带图：历史消息里的图没必要每轮重发，否则一轮比一轮胖。
static class Vision
{
    public const int MaxImages = 4;                    // 单条消息最多带几张
    public const long MaxBytes = 12L * 1024 * 1024;    // 单张文件上限，超过就不带（免得请求体炸掉）
    public const int MaxEdge = 1568;                   // 长边上限：多模态模型的常用像素预算

    // 接口明确回过"看不了图"之后置 true：本轮起退回纯文字，别一直撞同一堵墙
    public static bool Disabled = false;

    // 接口/探测声明过的结论：true=能看 false=看不了 null=没说
    // 与 VisionNote() 用同一处来源（Provider.CanSeeImages / ollama 的 capabilities），不另起一套。
    public static bool? Capability = null;

    // 上次"记住"是给哪个模型的（provider|model）：换了模型要重新给图片一次机会
    public static string Key = "";

    // 还要不要往请求里塞图片
    public static bool ShouldSend
    {
        get { return !Disabled && Capability != false; }
    }

    public static bool IsImageExt(string p)
    {
        if (p == null) return false;
        // 文本里什么字符都可能有（引号、竖线、控制字符），而这种"看着像路径"的串到处都是。
        // Path.GetExtension 遇到非法路径字符会抛 ArgumentException —— 2026-10-15 日志里
        // "压缩历史"就是这么连着失败 12 次的（异常从 ConvertMessages → ContentParts 一路上抛）。
        // 这里一律当成"不是图片路径"跳过，绝不往外抛。
        string e;
        try { e = Path.GetExtension(p).ToLowerInvariant(); }
        catch { return false; }
        return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".gif"
            || e == ".bmp" || e == ".webp";
    }

    // 从一段文字里挑出"确实存在的图片文件路径"（带引号的路径也能认出来）
    public static List<string> PathsIn(string text)
    {
        List<string> res = new List<string>();
        if (text == null || text.Length == 0) return res;
        MatchCollection ms = Regex.Matches(text, "\"([^\"]+)\"|([^\\s\"]+)");
        foreach (Match m in ms)
        {
            string cand = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (cand == null) continue;
            cand = cand.Trim();
            if (!IsImageExt(cand)) continue;
            if (!File.Exists(cand)) continue;
            if (!res.Contains(cand)) res.Add(cand);
        }
        return res;
    }

    // 对话里最后一条 user 消息（图片只跟它走）
    public static Dictionary<string, object> LastUser(List<Dictionary<string, object>> msgs)
    {
        Dictionary<string, object> last = null;
        if (msgs == null) return null;
        foreach (Dictionary<string, object> m in msgs)
            if (Json.Str(m, "role", "") == "user") last = m;
        return last;
    }

    // 这一轮的请求里带没带图（失败重发时要按它决定要不要退回纯文字）
    public static bool MessagesHaveImages(List<Dictionary<string, object>> msgs)
    {
        Dictionary<string, object> u = LastUser(msgs);
        return u != null && PathsIn(Json.Str(u, "content", "")).Count > 0;
    }

    // ---------- 工具产出的图片（2026-10-18 补回）----------
    // 只挂"最后一条 user 之后"的工具结果（本轮），最多 2 张；超大图跳过并报一声。
    public const int MaxToolImages = 2;
    public const long MaxToolImageBytes = 1536L * 1024L;   // 1.5MB
    public static Action<string> ToolImageReport = null;

    static void AddToolImage(string p, List<string> res)
    {
        if (string.IsNullOrEmpty(p) || res.Contains(p)) return;
        try
        {
            FileInfo fi = new FileInfo(p);
            if (!fi.Exists) return;
            if (fi.Length > MaxToolImageBytes)
            {
                if (ToolImageReport != null) ToolImageReport("图片太大（" + (fi.Length / 1024) + "KB），没有带上：" + Path.GetFileName(p));
                return;
            }
            res.Add(p);
        }
        catch { }
    }

    // 单条工具结果里的图片（最新优先），给 ConvertMessages 用。
    public static List<string> ToolImagesIn(Dictionary<string, object> msg)
    {
        List<string> res = new List<string>();
        if (!ShouldSend || msg == null) return res;
        if (Json.Str(msg, "role", "") != "tool") return res;
        List<string> ps = PathsIn(Json.Str(msg, "content", ""));
        for (int i = ps.Count - 1; i >= 0 && res.Count < MaxToolImages; i--)
            AddToolImage(ps[i], res);
        return res;
    }

    // 整轮的工具图（只算最后一条 user 之后的；从后往前，最多 MaxToolImages）。
    public static List<string> ToolImages(List<Dictionary<string, object>> msgs)
    {
        List<string> res = new List<string>();
        if (!ShouldSend || msgs == null) return res;
        int lastUser = -1;
        for (int i = 0; i < msgs.Count; i++)
            if (Json.Str(msgs[i], "role", "") == "user") lastUser = i;
        if (lastUser < 0) return res;
        for (int i = msgs.Count - 1; i > lastUser && res.Count < MaxToolImages; i--)
        {
            if (Json.Str(msgs[i], "role", "") != "tool") continue;
            List<string> ps = PathsIn(Json.Str(msgs[i], "content", ""));
            for (int j = ps.Count - 1; j >= 0 && res.Count < MaxToolImages; j--)
                AddToolImage(ps[j], res);
        }
        return res;
    }

    public static List<string> Base64ForPaths(List<string> paths)
    {
        List<string> res = new List<string>();
        if (paths == null) return res;
        foreach (string p in paths)
        {
            if (res.Count >= MaxToolImages) break;
            string b64 = Base64Of(p);
            if (b64 != null) res.Add(b64);
        }
        return res;
    }

    // Ollama 用：base64 列表
    public static List<string> Base64List(string text)
    {
        List<string> res = new List<string>();
        if (!ShouldSend) return res;
        foreach (string p in PathsIn(text))
        {
            if (res.Count >= MaxImages) break;
            string b64 = Base64Of(p);
            if (b64 != null) res.Add(b64);
        }
        return res;
    }

    // OpenAI 兼容接口用：content 数组（文字 + 图片）
    public static object ContentParts(string text)
    {
        if (!ShouldSend) return null;
        List<string> paths = PathsIn(text);
        if (paths.Count == 0) return null;
        List<object> parts = new List<object>();
        Dictionary<string, object> t = new Dictionary<string, object>();
        t["type"] = "text";
        t["text"] = text;
        parts.Add(t);
        int n = 0;
        foreach (string p in paths)
        {
            if (n >= MaxImages) break;
            string b64 = Base64Of(p);
            if (b64 == null) continue;
            Dictionary<string, object> url = new Dictionary<string, object>();
            url["url"] = "data:" + Mime(p) + ";base64," + b64;
            Dictionary<string, object> img = new Dictionary<string, object>();
            img["type"] = "image_url";
            img["image_url"] = url;
            parts.Add(img);
            n++;
        }
        return n > 0 ? (object)parts : null;
    }

    static string Mime(string p)
    {
        string e = Path.GetExtension(p).ToLowerInvariant();
        if (e == ".jpg" || e == ".jpeg") return "image/jpeg";
        if (e == ".gif") return "image/gif";
        if (e == ".bmp") return "image/bmp";
        if (e == ".webp") return "image/webp";
        return "image/png";
    }

    // 读成 base64；太大或太大尺寸就先缩一下（不然一张截图能把请求体撑到几 MB）
    public static string Base64Of(string path)
    {
        try
        {
            FileInfo fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > MaxBytes) return null;
            using (Image img = Image.FromFile(path))
            {
                int w = img.Width, h = img.Height;
                int edge = Math.Max(w, h);
                if (edge <= MaxEdge && fi.Length <= 2L * 1024 * 1024)
                    return Convert.ToBase64String(File.ReadAllBytes(path));
                double k = MaxEdge / (double)edge;
                int nw = Math.Max(1, (int)Math.Round(w * k));
                int nh = Math.Max(1, (int)Math.Round(h * k));
                using (Bitmap b = new Bitmap(nw, nh))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, nw, nh);
                    }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
        }
        catch { return null; }
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
            + "需要改动的话，请让用户在输入条左下角把权限档切成「工作区内修改」或「完全访问」。");
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

    // 换行符归一化：模型传进来的 old_string/new_string 一律是 LF，而磁盘上的文件
    // 常常是 CRLF（本仓库的 .ps1 / .md 就是）。不归一化的话 IndexOf(Ordinal) 永远匹配不上，
    // 报的却是"没找到 old_string"—— 把人往"你抄错了原文"的方向带偏。
    // 2026-10-13 实测复现：同一个多行 old_string，LF 文件成功、CRLF 文件必失败（改 .ps1 时连着撞两次）。
    public static string ToLf(string s)
    {
        if (s == null) return null;
        if (s.IndexOf('\r') < 0) return s;
        return s.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    // 文件主用哪种换行：CRLF 的条数是否多过"单独 LF"的条数
    public static bool UsesCrlf(string s)
    {
        if (s == null) return false;
        int crlf = 0, lf = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\n') continue;
            if (i > 0 && s[i - 1] == '\r') crlf++; else lf++;
        }
        return crlf > lf;
    }

    public static string EditFile(ToolContext ctx, Dictionary<string, object> a)
    {
        string path = ctx.Resolve(A.Str(a, "path", ""));
        if (!File.Exists(path)) throw new ToolException("文件不存在：" + path);
        bool all = A.Bool(a, "replace_all", false);
        string raw = File.ReadAllText(path, Encoding.UTF8);
        bool crlf = UsesCrlf(raw);                       // 记住文件原本的换行风格，写完要还原
        string text = crlf ? ToLf(raw) : raw;            // 匹配和替换一律在 LF 空间做
        string oldS = ToLf(A.Str(a, "old_string", ""));
        string newS = ToLf(A.Str(a, "new_string", ""));
        // 空的 old_string 会让下面的 IndexOf 循环原地打转（IndexOf("", i) 恒等于 i，idx 又不前进）。
        // 以前只有 schema 标了必填挡着，真传个 "" 进来就是死循环 —— 宁可明确报错。
        if (oldS.Length == 0) throw new ToolException("old_string 不能为空。要替换整份文件请用 write_file。");
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
        if (crlf) result = result.Replace("\n", "\r\n");   // 还原成文件原本的换行风格，别整体重写行尾
        File.WriteAllText(path, result, new UTF8Encoding(false));
        Changes.Record(path, raw, result);                 // 记原始内容，否则 diff 会显示"整个文件都在改行尾"
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

// ============================ 远程审批：接「Ahz 通信台」 ============================
// 开关打开时，高危操作不再本地弹窗，而是推到网页控制台（..\ahz通信\通讯.ps1），
// 等用户在手机上点「批准 / 拒绝」。返回 1=批准 / 0=拒绝；<0 = 通信台不可用（调用方回退本地弹窗）。
static class RemoteApproval
{
    // 约定路径：exe 所在目录（local-agent\）的上一层，进 ahz通信\通讯.ps1
    public static string DefaultScript()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            DirectoryInfo up = Directory.GetParent(baseDir);
            if (up != null)
            {
                string cand = Path.Combine(up.FullName, "ahz通信", "通讯.ps1");
                if (File.Exists(cand)) return cand;
            }
        }
        catch { }
        return "";
    }

    public static string ScriptPath(AgentConfig config)
    {
        string s = config != null ? config.remote_approval_script : null;
        if (!string.IsNullOrEmpty(s) && File.Exists(s)) return s;
        return DefaultScript();
    }

    // 发一条授权请求并阻塞等待。action 必须写清"到底要做什么"（用户靠它决定批不批）。
    public static int Ask(AgentConfig config, string action, string reason, out string detail)
    {
        detail = "";
        string script = ScriptPath(config);
        if (string.IsNullOrEmpty(script))
        {
            detail = "找不到通信台脚本（通讯.ps1）";
            return -1;
        }
        int wait = (config != null && config.remote_approval_wait > 0) ? config.remote_approval_wait : 600;

        ProcessStartInfo psi = new ProcessStartInfo();
        try { psi.FileName = Tools.FindShell(); }
        catch { psi.FileName = "powershell.exe"; }
        psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Mode 请求授权";
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        // 走环境变量传参：PS 5.1 用 -File 时会把空字符串参数整个丢掉，正文绝不能走命令行
        try
        {
            psi.EnvironmentVariables["AHZ_ACTION"] = action == null ? "" : action;
            psi.EnvironmentVariables["AHZ_REASON"] = reason == null ? "" : reason;
            psi.EnvironmentVariables["AHZ_WAIT"] = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { }

        try
        {
            using (Process p = Process.Start(psi))
            {
                if (p == null) { detail = "通信台进程没起来"; return -1; }
                StringBuilder so = new StringBuilder();
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) so.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) so.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                // 多给 60 秒余量，让脚本自己走完超时逻辑再收尾
                if (!p.WaitForExit((wait + 60) * 1000))
                {
                    try { Tools.KillTree(p); } catch { }
                    detail = "等通信台回话超时";
                    return -1;
                }
                try { p.WaitForExit(); } catch { }
                detail = so.ToString().Trim();
                int code = p.ExitCode;
                if (code == 0) return 1;        // 批准
                if (code == 3) return 0;        // 拒绝
                if (code == 4) { detail += "\n（等待超时，按未授权处理）"; return 0; }
                return -1;
            }
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return -1;
        }
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

        // 声明了但没传的参数补空串：模板里的 {{name}} 如果原样残留、又被替换进命令/URL，会变成语法错误。
        //（2026-10-16 修：模板手写引号 + 引擎自动再加引号 → ''值'' 解析失败，ahz_reply 连挂 4 次）
        if (t.parameters != null)
        {
            if (args == null) args = new Dictionary<string, object>();
            foreach (PluginParam p in t.parameters)
                if (p != null && !string.IsNullOrEmpty(p.name) && !args.ContainsKey(p.name))
                    args[p.name] = "";
        }

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
    private CancellationTokenSource _cts;
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
        lock (_lock)
        {
            if (_current != null) { try { _current.Abort(); } catch { } }
            if (_cts != null) { try { _cts.Cancel(); } catch { } }
        }
    }

    HttpWebRequest Make(string path, string method, string json)
    {
        return Make(path, method, json, 0);
    }

    // timeoutMs = 0 用默认（生成任务可能跑十几分钟）。
    // 但"探测 / 列模型"这种点一下就该立刻有反应的操作必须给短超时：
    // 它们是在 UI 线程上调的，默认 15 分钟超时 = 界面能假死一刻钟（2026-10-15 排查时发现）。
    HttpWebRequest Make(string path, string method, string json, int timeoutMs)
    {
        Program.EnsureTls();
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Host + path);
        req.Method = method;
        req.Proxy = null;
        req.Timeout = timeoutMs > 0 ? timeoutMs : 900000;
        req.ReadWriteTimeout = timeoutMs > 0 ? timeoutMs : 900000;
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
        Dictionary<string, object> result = PostJson("/api/show", payload, 20000);   // 探测：短超时，别把界面拖死
        if (result == null) throw new LocalAgentException("读不到模型信息：" + model);
        return result;
    }

    public Dictionary<string, object> PostJson(string path, Dictionary<string, object> payload)
    {
        return PostJson(path, payload, 0);
    }

    public Dictionary<string, object> PostJson(string path, Dictionary<string, object> payload, int timeoutMs)
    {
        HttpWebRequest req = Make(path, "POST", Json.Dump(payload), timeoutMs);
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
        try
        {
            string body = HttpJson.Get(Host + "/api/tags", 20000, null);   // 列模型：短超时
            Dictionary<string, object> o = Json.Obj(body);
            object[] arr = Json.Arr(Json.Get(o, "models"));
            if (arr != null)
                foreach (object it in arr)
                {
                    Dictionary<string, object> m = Json.Map(it);
                    if (m != null) list.Add(m);
                }
        }
        catch (Exception ex) { throw new LocalAgentException(ex.Message); }
        return list;
    }

    public async Task<Dictionary<string, object>> ChatStreamAsync(Dictionary<string, object> payload, Action<Dictionary<string, object>> onChunk)
    {
        Dictionary<string, object> last = null;
        CancellationTokenSource cts = new CancellationTokenSource();
        lock (_lock) { _cts = cts; }
        try
        {
            HttpClientHandler h = new HttpClientHandler();
            h.UseProxy = false;
            using (HttpClient c = new HttpClient(h))
            {
                c.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, Host + "/api/chat"))
                {
                    req.Content = new StringContent(Json.Dump(payload), Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            string err = await resp.Content.ReadAsStringAsync();
                            throw new LocalAgentException("Ollama 返回 HTTP " + (int)resp.StatusCode + "：" + err);
                        }
                        using (Stream s = await resp.Content.ReadAsStreamAsync())
                        using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        {
                            string line;
                            while ((line = await r.ReadLineAsync()) != null)
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
                }
            }
        }
        catch (OperationCanceledException) { throw new LocalAgentException("__CANCELLED__"); }
        catch (LocalAgentException) { throw; }
        catch (Exception ex) { throw new LocalAgentException("连不上 Ollama（" + ex.Message + "）\n提示：确认 Ollama 正在运行。"); }
        finally
        {
            lock (_lock) { _current = null; _cts = null; }
            try { cts.Dispose(); } catch { }
        }
        return last;
    }

    // 兼容旧调用（探针/测试）；产品 UI 走 ChatStreamAsync。
    public Dictionary<string, object> ChatStream(Dictionary<string, object> payload, Action<Dictionary<string, object>> onChunk)
    {
        return ChatStreamAsync(payload, onChunk).GetAwaiter().GetResult();
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
    Task<Dictionary<string, object>> StreamAsync(
        List<Dictionary<string, object>> messages, List<object> tools, bool think, string effort, int numCtx,
        Action<string> onThinking, Action<string> onContent, Action<Dictionary<string, object>> onToolCall);
    void Cancel();
    Dictionary<string, object> Probe();     // 能力探测，OpenAI 兼容接口返回 null
    List<ModelMeta> ListModelInfos();     // 拉模型清单（含接口给的能力标签）
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
    // 「完全访问」是照 Codex 那一屏写的（用户截图里输入条左下角就是这四个字）
    public static readonly string[] Labels = new string[] { "仅可查看", "工作区内修改", "完全访问" };
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

    // 有 Provider 的写法：接口在 /models 里声明过 supported_levels 就信接口，
    // 没声明才回落到上面那份写死的白名单（老行为不能丢）。
    public static bool Supported(Provider p, string model, string thinkingValues)
    {
        if (p == null) return false;
        if (p.type == "ollama") return Supported("ollama", model, thinkingValues);
        ModelMeta m = p.Meta(model);
        if (m != null && m.efforts != null && m.efforts.Count >= 2) return true;
        return CloudSupportsEffort(model);
    }

    // 界面三档（低/中/高）→ 接口真正认的那个字符串。
    //   接口声明了档位：按位置对（第一个=低、最后一个=高）。
    //     例：DeepSeek 给的是 low/high/max → 低=low、中=high、高=max。
    //   接口没声明：按老行为原样发（low/medium/high 是 OpenAI 那套）。
    public static string ResolveFor(Provider p, string model, string e)
    {
        if (!IsSet(e)) return null;
        ModelMeta m = p == null ? null : p.Meta(model);
        if (m == null || m.efforts == null || m.efforts.Count == 0) return e;
        int idx = Index(e);                                     // 0 / 1 / 2
        int k = m.efforts.Count == 1 ? 0 : (int)Math.Round(idx * (m.efforts.Count - 1) / 2.0);
        if (k < 0) k = 0;
        if (k >= m.efforts.Count) k = m.efforts.Count - 1;
        return m.efforts[k];
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
        // 只有最后一条用户消息带图：历史里的图不必每轮重发
        Dictionary<string, object> lastUser = Vision.LastUser(msgs);
        int lastUserIdx = -1;
        for (int mi = 0; mi < msgs.Count; mi++)
            if (msgs[mi] == lastUser) lastUserIdx = mi;
        for (int mi = 0; mi < msgs.Count; mi++)
        {
            Dictionary<string, object> m = msgs[mi];
            string role = Json.Str(m, "role", "user");
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["role"] = role;
            string text = Json.Str(m, "content", "");
            o["content"] = text;
            // 图片走 Ollama 自己的 images 字段（/api/chat 不认 OpenAI 那种 content 数组）
            if (m == lastUser)
            {
                List<string> imgs = Vision.Base64List(text);
                if (imgs.Count > 0) o["images"] = imgs;
            }
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
            // 本轮工具结果里的图：图挂在 tool 那条的 images 上，再补一条 user 说明。
            List<string> toolImgs = (role == "tool" && lastUserIdx >= 0 && mi > lastUserIdx)
                ? Vision.ToolImagesIn(m) : new List<string>();
            List<string> toolB64 = Vision.Base64ForPaths(toolImgs);
            if (toolB64.Count > 0) o["images"] = toolB64;
            list.Add(o);
            if (toolB64.Count > 0)
            {
                Dictionary<string, object> note = new Dictionary<string, object>();
                note["role"] = "user";
                note["content"] = "工具结果里的图片（见上一条工具消息）";
                list.Add(note);
            }
        }
        return list;
    }

    public async Task<Dictionary<string, object>> StreamAsync(
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

        return await _client.ChatStreamAsync(payload, delegate(Dictionary<string, object> chunk)
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

    // 兼容旧探针/测试；产品链路走 StreamAsync。
    public Dictionary<string, object> Stream(
        List<Dictionary<string, object>> messages, List<object> tools, bool think, string effort, int numCtx,
        Action<string> onThinking, Action<string> onContent, Action<Dictionary<string, object>> onToolCall)
    {
        return StreamAsync(messages, tools, think, effort, numCtx, onThinking, onContent, onToolCall)
            .GetAwaiter().GetResult();
    }

    public Dictionary<string, object> Probe() { return _client.Show(_model); }

    // /api/tags 只列"本地已经拉下来的"模型（不列能下载的），也没有能力标签 ——
    // 能力得走 /api/show，那是按需探测的，别在这里对每个模型都打一次。
    public List<ModelMeta> ListModelInfos()
    {
        List<ModelMeta> list = new List<ModelMeta>();
        foreach (Dictionary<string, object> m in _client.Tags())
        {
            string nm = Json.Str(m, "name", null);
            if (string.IsNullOrEmpty(nm)) nm = Json.Str(m, "model", null);
            if (string.IsNullOrEmpty(nm)) continue;
            ModelMeta mi = new ModelMeta();
            mi.id = nm;
            list.Add(mi);
        }
        list.Sort(delegate(ModelMeta a, ModelMeta b) { return string.CompareOrdinal(a.id, b.id); });
        return list;
    }
}

// ---------- OpenAI 兼容接口（DeepSeek / OpenAI / 通义 / Kimi / 智谱 …） ----------

class OpenAiLlm : ILlm
{
    private readonly Provider _p;
    private readonly string _model;
    private HttpWebRequest _current;
    private CancellationTokenSource _cts;
    private readonly object _lock = new object();

    public OpenAiLlm(Provider p, string model)
    {
        _p = p;
        _model = model;
    }

    public string Kind { get { return "openai"; } }

    public void Cancel()
    {
        lock (_lock)
        {
            if (_current != null) { try { _current.Abort(); } catch { } }
            if (_cts != null) { try { _cts.Cancel(); } catch { } }
        }
    }

    public void Close() { }

    public string Root()
    {
        string b = (_p.base_url ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) b = "https://api.openai.com/v1";
        return b;
    }

    HttpWebRequest Make(string url, string method, string json)
    {
        return Make(url, method, json, 0);
    }

    // timeoutMs = 0 用默认（生成要能跑十几分钟）；列模型这种"点一下要立刻有反应"的给短超时，
    // 否则在 UI 线程上等 15 分钟 = 界面假死（2026-10-15 排查"设置/拉取卡死"时发现）。
    HttpWebRequest Make(string url, string method, string json, int timeoutMs)
    {
        Program.EnsureTls();
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.Proxy = null;
        req.Timeout = timeoutMs > 0 ? timeoutMs : 900000;
        req.ReadWriteTimeout = timeoutMs > 0 ? timeoutMs : 900000;
        req.KeepAlive = false;
        req.Accept = "application/json";
        if (!string.IsNullOrEmpty(_p.api_key)) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + _p.api_key.Trim();
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
        Dictionary<string, object> lastUser = Vision.LastUser(msgs);
        int lastUserIdx = -1;
        for (int mi = 0; mi < msgs.Count; mi++)
            if (msgs[mi] == lastUser) lastUserIdx = mi;
        for (int mi = 0; mi < msgs.Count; mi++)
        {
            Dictionary<string, object> m = msgs[mi];
            string role = Json.Str(m, "role", "user");
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["role"] = role;
            string content = Json.Str(m, "content", "");
            // 带图的用户消息：content 换成"文字 + 图片"数组（OpenAI 多模态格式）
            object vision = (m == lastUser) ? Vision.ContentParts(content) : null;
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
                o["content"] = vision != null ? vision : (object)content;
            }
            list.Add(o);
            // 本轮工具图：tool 消息保持原样，在后面补一条“文字 + 图片”的合成 user。
            if (role == "tool" && lastUserIdx >= 0 && mi > lastUserIdx)
            {
                List<string> toolImgs = Vision.ToolImagesIn(m);
                if (toolImgs.Count > 0)
                {
                    object parts = Vision.ContentParts("工具结果里的图片：\n" + string.Join("\n", toolImgs.ToArray()));
                    if (parts != null)
                    {
                        Dictionary<string, object> note = new Dictionary<string, object>();
                        note["role"] = "user";
                        note["content"] = parts;
                        list.Add(note);
                    }
                }
            }
        }
        return list;
    }

    public async Task<Dictionary<string, object>> StreamAsync(
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
        // 接口在 /models 里声明过档位就按它发（比如 DeepSeek 是 low/high/max），否则原样发
        string eff = Effort.ResolveFor(_p, _model, effort);
        if (eff != null) payload["reasoning_effort"] = eff;

        Dictionary<int, Dictionary<string, object>> pending = new Dictionary<int, Dictionary<string, object>>();
        Dictionary<string, object> stats = null;
        CancellationTokenSource cts = new CancellationTokenSource();
        lock (_lock) { _cts = cts; }
        try
        {
            HttpClientHandler h = new HttpClientHandler();
            h.UseProxy = false;
            using (HttpClient c = new HttpClient(h))
            using (HttpRequestMessage hreq = new HttpRequestMessage(HttpMethod.Post, Root() + "/chat/completions"))
            {
                c.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                hreq.Content = new StringContent(Json.Dump(payload), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(_p.api_key))
                    hreq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _p.api_key.Trim());
                using (HttpResponseMessage resp = await c.SendAsync(hreq, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string err = await resp.Content.ReadAsStringAsync();
                        throw new LocalAgentException("接口返回 HTTP " + (int)resp.StatusCode + "：" + err);
                    }
                    using (Stream s = await resp.Content.ReadAsStreamAsync())
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    {
                string line;
                while ((line = await r.ReadLineAsync()) != null)
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
            }
        }
        catch (OperationCanceledException) { throw new LocalAgentException("__CANCELLED__"); }
        catch (LocalAgentException) { throw; }
        catch (Exception ex) { throw new LocalAgentException("接口请求失败：" + ex.Message); }
        finally
        {
            lock (_lock) { _current = null; _cts = null; }
            try { cts.Dispose(); } catch { }
        }

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

    // 兼容旧探针/测试；产品链路走 StreamAsync。
    public Dictionary<string, object> Stream(
        List<Dictionary<string, object>> messages, List<object> tools, bool think, string effort, int numCtx,
        Action<string> onThinking, Action<string> onContent, Action<Dictionary<string, object>> onToolCall)
    {
        return StreamAsync(messages, tools, think, effort, numCtx, onThinking, onContent, onToolCall)
            .GetAwaiter().GetResult();
    }

    public Dictionary<string, object> Probe() { return null; }

    public List<ModelMeta> ListModelInfos()
    {
        try
        {
            string body = HttpJson.Get(Root() + "/models", 30000, _p.api_key);   // 列模型：短超时（这步在 UI 线程上跑）
            return ParseModelList(body);
        }
        catch (Exception ex) { throw new LocalAgentException(ex.Message); }
    }

    // 解析 GET {base}/models 的返回。除了 id，接口通常还给了：
    //   name              显示名（DeepSeek-V4.1-Flash）
    //   context_window    上下文长度
    //   input_modalities  能不能看图（["text","image"]）
    //   effort.supported_levels  支持的档位（["low","high","max"]）
    // 这些以前全被扔了。抽成静态方法是为了能拿真返回离线跑自测（不打网络）。
    public static List<ModelMeta> ParseModelList(string json)
    {
        List<ModelMeta> list = new List<ModelMeta>();
        Dictionary<string, object> o = Json.Obj(json);
        object[] data = Json.Arr(Json.Get(o, "data"));
        if (data == null) return list;
        foreach (object d in data)
        {
            Dictionary<string, object> dm = Json.Map(d);
            if (dm == null) continue;
            string id = Json.Str(dm, "id", null);
            if (string.IsNullOrEmpty(id)) continue;
            ModelMeta mi = new ModelMeta();
            mi.id = id;
            mi.name = Json.Str(dm, "name", null);

            object ctx = Json.Get(dm, "context_window");
            if (ctx != null)
            {
                int v;
                if (int.TryParse(Convert.ToString(ctx, System.Globalization.CultureInfo.InvariantCulture), out v)) mi.ctx = v;
            }

            object[] mods = Json.Arr(Json.Get(dm, "input_modalities"));
            if (mods != null && mods.Length > 0)
            {
                bool hasImage = false, hasText = false;
                foreach (object mo in mods)
                {
                    string s = Convert.ToString(mo, System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
                    if (s == "image") hasImage = true;
                    else if (s == "text") hasText = true;
                }
                if (hasImage) mi.vision = true;
                else if (hasText) mi.vision = false;      // 明说了只有文字 → 就是看不了图
            }

            Dictionary<string, object> ef = Json.Map(Json.Get(dm, "effort"));
            object[] levels = ef == null ? null : Json.Arr(Json.Get(ef, "supported_levels"));
            if (levels != null)
                foreach (object l in levels)
                {
                    string s = Convert.ToString(l, System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(s)) mi.efforts.Add(s);
                }
            list.Add(mi);
        }
        list.Sort(delegate(ModelMeta a, ModelMeta b) { return string.CompareOrdinal(a.id, b.id); });
        return list;
    }
}

static class LlmFactory
{
    public static ILlm Create(Provider p, string model)
    {
        if (p != null && p.type == "ollama") return new OllamaLlm(p.base_url, model);
        return new OpenAiLlm(p, model);
    }

    // 只要 id 的调用（CLI 的 --list-models）用这个
    public static List<string> Ids(List<ModelMeta> infos)
    {
        List<string> ids = new List<string>();
        if (infos == null) return ids;
        foreach (ModelMeta m in infos) if (m != null && m.id != null) ids.Add(m.id);
        return ids;
    }
}

// ============================ 会话配置 ============================

// 接口在 /models 里给的"模型能力标签"。以前拉取只取 id，把接口能给的全扔了 ——
// 结果 chip 只能显示 deepseek-flash 这种生 id，档位也只能靠一份写死的模型名白名单猜。
// 现在拉一次就存一次，界面和请求参数都以接口说的为准。
class ModelMeta
{
    public string id { get; set; }            // 模型 id（真正发给接口的那个）
    public string name { get; set; }          // 显示名：接口的 name（DeepSeek-V4.1-Flash 这种）
    public bool? vision { get; set; }         // 能不能看图（null = 接口没说）
    public int ctx { get; set; }              // 上下文长度
    public List<string> efforts { get; set; } // 接口声明的档位，从低到高（low/high/max 这种）

    public ModelMeta()
    {
        id = "";
        name = "";
        efforts = new List<string>();
    }

    public string Display(string fallbackId)
    {
        if (name != null && name.Length > 0) return name;
        if (id != null && id.Length > 0) return id;
        return fallbackId;
    }
}

class Provider
{
    public string id { get; set; }
    public string name { get; set; }
    public string type { get; set; }          // ollama = 本地 Ollama；openai = OpenAI 兼容接口
    public string base_url { get; set; }
    public string api_key { get; set; }
    public List<string> models { get; set; }
    public Dictionary<string, ModelMeta> model_meta { get; set; }   // 模型 id → 接口给的能力（老配置没有这项，允许为空）

    public Provider()
    {
        id = "p" + Guid.NewGuid().ToString("N").Substring(0, 6);
        name = "新提供商";
        type = "openai";
        base_url = "";          // 别默认成某一家：新提供商应该由预设或用户自己填
        api_key = "";
        models = new List<string>();
        model_meta = new Dictionary<string, ModelMeta>();
    }

    // 这个模型的能力标签（没有就返回 null，调用方自己回落到老行为）
    public ModelMeta Meta(string model)
    {
        ModelMeta m;
        if (model_meta == null || model == null) return null;
        return model_meta.TryGetValue(model, out m) ? m : null;
    }

    public void SetMeta(ModelMeta m)
    {
        if (m == null || m.id == null || m.id.Length == 0) return;
        if (model_meta == null) model_meta = new Dictionary<string, ModelMeta>();
        model_meta[m.id] = m;
    }

    // chip / 列表上显示的名字：接口给了显示名就用显示名
    public string DisplayName(string model)
    {
        ModelMeta m = Meta(model);
        return m == null ? model : m.Display(model);
    }

    // 能不能看图：true/false = 知道；null = 接口没说
    // （云端看 /models 的 input_modalities，本地看 /api/show 的 capabilities）
    public bool? CanSeeImages(string model)
    {
        ModelMeta m = Meta(model);
        return m == null ? (bool?)null : m.vision;
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

// API Key 落盘加密：DPAPI CurrentUser（只有当前 Windows 用户能解）。
// 老 config.json 的明文 Key 仍能读；下一次保存时自动迁移成 dpapi-v1: 前缀。
static class Dpapi
{
    const string Prefix = "dpapi-v1:";
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AhzScribe.config.v1");

    public static bool IsProtected(string s)
    {
        return !string.IsNullOrEmpty(s) && s.StartsWith(Prefix, StringComparison.Ordinal);
    }

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain;
        try
        {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }
        catch (Exception ex) { Log.Swallow(ex, "加密 API Key"); throw; }
    }

    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored;
        try
        {
            byte[] data = Convert.FromBase64String(stored.Substring(Prefix.Length));
            byte[] plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) { Log.Swallow(ex, "解密 API Key"); return ""; }
    }
}

class AgentConfig
{
    // 启动时把“配置读坏了/写失败了”带到界面上，不再只写日志。
    public static string LastLoadError = "";
    public static string LastSaveError = "";
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
    public bool dark { get; set; }               // 暗色模式（启动时应用，切换后重启）
    // 智能意图澄清（2026-10-16）：GUI 提交指令后、进执行引擎前先用轻量模型（deepseek-flash）判一次歧义，
    // 有歧义就弹确认框让用户选具体方向，选中项的 command 原样下发。默认开；关掉 = 行为跟以前一模一样。
    public bool clarify { get; set; }
    public List<string> disabled_plugins { get; set; }   // 被停用的插件文件名
    public Dictionary<string, string> plugin_sources { get; set; }   // 文件名 -> 安装来源
    public string plugin_market_url { get; set; }        // 插件市场索引地址（留空则用 plugins\market.json）
    // 远程审批：高危操作改推到「Ahz 通信台」网页控制台，等用户在手机上点批准/拒绝（默认关，行为与以前一致）
    public bool remote_approval { get; set; }
    public string remote_approval_script { get; set; }   // 通信台脚本路径（留空则用默认的 ..\ahz通信\通讯.ps1）
    public int remote_approval_wait { get; set; }        // 远程审批最多等多少秒（默认 600）

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
        clarify = true;        // 意图澄清默认开（老配置里没这项也能用：Migrate 只补"缺字段"的默认值）
        disabled_plugins = new List<string>();
        plugin_sources = new Dictionary<string, string>();
        plugin_market_url = "";
        remote_approval = false;
        remote_approval_script = "";
        remote_approval_wait = 600;
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
        if (remote_approval_script == null) remote_approval_script = "";
        if (remote_approval_wait <= 0) remote_approval_wait = 600;
        // 模型能力标签：老配置里没有这一项，补成空表（不是"没有能力"，是"接口还没告诉过我们"）
        foreach (Provider p in providers)
            if (p != null && p.model_meta == null) p.model_meta = new Dictionary<string, ModelMeta>();
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
        LastLoadError = "";
        try
        {
            if (File.Exists(FilePath))
            {
                string raw = File.ReadAllText(FilePath, Encoding.UTF8);
                AgentConfig c = Json.Ser.Deserialize<AgentConfig>(raw);
                if (c != null)
                {
                    // 老配置是明文，IsProtected 会原样返回；新配置在这里解成明文。
                    bool hadPlain = false;
                    if (c.providers != null)
                        foreach (Provider p in c.providers)
                        {
                            if (p == null) continue;
                            string old = p.api_key;
                            p.api_key = Dpapi.Unprotect(old);
                            if (!string.IsNullOrEmpty(old) && !Dpapi.IsProtected(old)) hadPlain = true;
                        }
                    // 老 config.json 里没有 clarify 这一项，反序列化会把它落成 false ——
                    // 而需求是"默认开"。所以按"原文件里到底有没有这个键"来补，没法靠 Migrate 分辨。
                    if (raw.IndexOf("\"clarify\"", StringComparison.OrdinalIgnoreCase) < 0) c.clarify = true;
                    c.Migrate();
                    // 读到明文 Key 就顺手迁成 DPAPI 密文，别让老配置一直裸着。
                    if (hadPlain) { try { c.Save(); } catch { } }
                    return c;
                }
            }
        }
        catch (Exception ex)
        {
            LastLoadError = "配置读取失败：" + ex.Message;
            Log.Swallow(ex, "读取配置");
        }
        return new AgentConfig();
    }

    public void Save()
    {
        // 和 sessions 一样：先 .tmp 再替换，别让掉电把 config（含 Key）写成半截。
        string tmp = FilePath + ".tmp";
        try
        {
            // 只加密落盘的那一份，内存里始终是明文（设置页要回显、请求头要用）。
            string json;
            if (providers == null || providers.Count == 0)
                json = Json.Ser.Serialize(this);
            else
            {
                string[] keys = new string[providers.Count];
                try
                {
                    for (int i = 0; i < providers.Count; i++)
                    {
                        Provider p = providers[i];
                        if (p == null) continue;
                        keys[i] = p.api_key;
                        p.api_key = Dpapi.Protect(p.api_key);
                    }
                    json = Json.Ser.Serialize(this);
                }
                finally
                {
                    for (int i = 0; i < providers.Count; i++)
                        if (providers[i] != null) providers[i].api_key = keys[i];
                }
            }
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(FilePath))
            {
                try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
                try { File.Replace(tmp, FilePath, null); }
                catch { File.Copy(tmp, FilePath, true); try { File.Delete(tmp); } catch { } }
            }
            else File.Move(tmp, FilePath);
            // .bak 也必须是密文：先复制旧文件再替换的话，旧 .bak 可能还是明文。
            try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
            LastSaveError = "";
        }
        catch (Exception ex)
        {
            LastSaveError = "设置保存失败：" + ex.Message;
            Log.Swallow(ex, "保存配置（API Key 加密失败时不会退回明文）");
        }
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
    public AgentConfig Config;      // 设置窗口改完配置后由 ApplyConfig 换成新对象（所以不能 readonly）
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
        string[] preview = new string[]
        {
            full,
            "工作目录：" + Ctx.Root,
            "这是工作目录之外的路径，批准后才会访问"
        };
        int d;
        try
        {
            if (Config.remote_approval)
            {
                // 开关打开：越权申请也推到通信台（手机点批准才放行）。
                // 远程只有「批准/拒绝」两档，不给「本会话总是允许」——
                // 越权这种口子不该被一次点击永久打开。
                string detail;
                int r = RemoteApproval.Ask(Config, tool + "（工作目录外）\n" + string.Join("\n", preview),
                    "Ahz 想访问工作目录外的路径", out detail);
                if (r < 0)
                {
                    // 通信台起不来：回退本地弹窗，绝不因此把访问卡死或悄悄放行
                    Log.Audit("远程授权不可用（" + detail + "），回退本地窗口 · 越权 " + full);
                    d = UI.ApproveOutside(tool + "（工作目录外）", preview);
                }
                else
                {
                    d = (r == 1) ? 1 : 0;
                    Log.Audit("远程授权·" + (r == 1 ? "批准" : "拒绝") + " · 越权 " + tool + " · " + full);
                }
            }
            else d = UI.ApproveOutside(tool + "（工作目录外）", preview);
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
        // 内置浏览器：助手能自己开页/读正文/点链接/截图，用户同一时间看着同一个页面
        try { ToolList.AddRange(BrowserTools.All()); } catch { }
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

    // 发请求前的体检：坏历史在这里自愈。坏法有**两种**，以前只认半边：
    //   ① 孤儿 tool 消息：有 tool 结果，但它前面没有声明这条调用的 assistant（压缩切歪造成）→ 摘掉。
    //   ② 孤儿 tool_calls：assistant 声明了调用，配对的结果一条都没有（中途点停止、进程被杀造成）→ 补一条
    //      "未执行"的 tool 结果。这是 2026-10-17 用户那条会话永久卡死的真因：那条 assistant 消息已经带
    //      tool_calls 落进历史，结果没写回来，于是**之后每一句**（连"你好"）都被接口挡回来：
    //      "An assistant message with 'tool_calls' must be followed by tool messages responding to each
    //       'tool_call_id'"（DeepSeek）/ "Invalid request: …"（Moonshot）。
    //      两种坏法都不是模型的错，换模型更没用 —— 坏的是历史，必须在这儿补。
    internal static int DropOrphanTools(List<Dictionary<string, object>> msgs)
    {
        int fixedCount = 0;
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
            fixedCount++;
        }

        // ② 补"声明了调用、却没有结果"的那些（顺序也必须对：紧跟在那条 assistant 后面）
        i = 0;
        while (i < msgs.Count)
        {
            if (Json.Str(msgs[i], "role", "") != "assistant") { i++; continue; }
            object[] calls = Json.Arr(Json.Get(msgs[i], "tool_calls"));
            if (calls == null || calls.Length == 0) { i++; continue; }
            List<string> got = new List<string>();
            int j = i + 1;
            while (j < msgs.Count && Json.Str(msgs[j], "role", "") == "tool")
            {
                got.Add(Json.Str(msgs[j], "tool_call_id", ""));
                j++;
            }
            int blanks = 0;
            for (int g = 0; g < got.Count; g++) if (got[g].Length == 0) blanks++;
            List<Dictionary<string, object>> fill = new List<Dictionary<string, object>>();
            for (int c = 0; c < calls.Length; c++)
            {
                Dictionary<string, object> tc = Json.Map(calls[c]);
                if (tc == null) continue;
                string cid = Json.Str(tc, "id", "");
                bool answered = cid.Length > 0 && got.Contains(cid);
                if (!answered && blanks > 0) { blanks--; answered = true; }   // 老数据没带 id：按条数配
                if (answered) continue;
                fill.Add(SyntheticToolResult(cid, Json.Str(tc, "name", "?")));
                fixedCount++;
            }
            if (fill.Count > 0) msgs.InsertRange(i + 1 + got.Count, fill);
            i = i + 1 + got.Count + fill.Count;
        }
        return fixedCount;
    }

    // 补出来的那条"未执行"结果：内容要写清是"没跑"而不是"跑了没输出"，模型才不会当成真实结果接着推理
    internal static Dictionary<string, object> SyntheticToolResult(string callId, string toolName)
    {
        Dictionary<string, object> m = Msg("tool", "[" + toolName + " 结果]\n"
            + "（没有结果：这一条调用发出去之后那一轮被中断了（点了停止或程序退出），工具没有执行。"
            + "需要它的话，重新调用一次。）");
        m["tool_call_id"] = callId;
        m["tool_name"] = toolName;
        m["synthetic"] = true;          // 标一下是补的，不是真跑出来的
        return m;
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

    // 设置窗口里改完配置后接管新配置：**只换连接 / 工具表 / 系统提示，对话原样留着**。
    // 以前主窗口是 `runner = new AgentRunner(...)` + `rtb.ClearAll()` —— 一保存设置，
    // 界面上和内存里的上下文就一起没了（用户 2026-10-15 报的"设置后会清空上下文"）。
    public void ApplyConfig(AgentConfig config)
    {
        Config = config;
        ResetContext();                    // 权限档 / 越权放行 / 工作目录都按新配置重装
        ReloadTools();                     // 插件开关可能也变了；里面会顺带刷新系统提示
        if (Messages.Count == 0) Messages.Add(Msg("system", SystemPrompt()));
        else Messages[0] = Msg("system", SystemPrompt());
        RebuildLlm();
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
            + "用户消息里可能出现图片文件的路径（在输入框 Ctrl+V 粘图、或把图片拖进来就会生成）："
            + (Vision.ShouldSend
                ? "这类图片会随消息一起发给你，你可以直接看图，不需要再调工具去读路径。\n"
                : "当前模型看不了图，你只会拿到路径文字；要了解图里内容，就用 read_file 读它，或跟用户说明这个模型不支持图片。\n")
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

    // 最后一条消息是不是"同一句话"：重复的提醒没必要再塞一遍（模型上一步刚看过）
    static bool LastMsgIs(List<Dictionary<string, object>> msgs, string role, string text)
    {
        if (msgs == null || msgs.Count == 0) return false;
        Dictionary<string, object> last = msgs[msgs.Count - 1];
        return Json.Str(last, "role", "") == role && Json.Str(last, "content", "") == text;
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
            SyncVision();
            return;
        }
        Dictionary<string, object> info = Llm.Probe();
        if (info == null) { SyncVision(); return; }
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
        SyncVision();
    }

    // 把"这个模型能不能看图"喂给 Vision：来源和 VisionNote() 完全一致
    //   · 云端：接口 /models 里的 input_modalities（Provider.CanSeeImages）
    //   · 本地：/api/show 的 capabilities 里有没有 vision
    // 已知看不了就别再往请求里塞图片 —— 否则每轮都要白撞一次失败请求再退回纯文字。
    public void SyncVision()
    {
        try
        {
            Provider p = Config.Current();
            bool? v = p == null ? null : p.CanSeeImages(Config.model);
            if (v == null && p != null && p.type == "ollama" && Capabilities.Count > 0)
                v = Capabilities.Contains("vision");
            Vision.Capability = v;
        }
        catch { }
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
            UI.Write(OutKind.Error, MainForm.ErrorText("导出交付记录失败", ex.Message,
                "确认目标路径可写、文件没被别的程序占用，然后重试"));
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

    public async Task RunTurnAsync(string userText)
    {
        if (userText == null || userText.Trim().Length == 0) return;
        // 只在"换了模型 / 换了接口"时才重新给图片一次机会：
        // 同一个模型已知看不了图，就不该每轮都白撞一次失败请求。
        SyncVision();
        string vkey = Config.provider + "|" + Config.model;
        if (Vision.Key != vkey)
        {
            Vision.Key = vkey;
            Vision.Disabled = false;
        }
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
        int taskNudges = 0;                           // "计划还剩几步"注入了几次（要有上限，见下）
        const int TaskNudgeMax = 3;
        bool restartAsked = false;                    // 它叫了"重启并升级"：本轮不用再往下走了
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
            // 每步把"还剩几步"注入上下文：模型不用自己记计划，忘了就会被提醒（见 _tasks 那段注释）。
            // 但**必须有上限**：模型要是死不改那行 `- [x]`，这条提醒会一步步灌满上下文和对话区
            // （2026-10-15 用户报的"历史全变成同一句提醒"就是没上限导致的，一条会话灌了十几遍）。
            string tn = taskNudges < TaskNudgeMax ? TaskNudge(_tasks) : null;
            if (tn != null && !LastMsgIs(Messages, "user", tn))
            {
                taskNudges++;
                Messages.Add(Msg("user", tn));
            }
            UI.Status("等待模型…");
            StepResult res = await StepAsync();
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
            List<string> answered = new List<string>();     // 已经写回结果的 callId（收尾时查缺补漏，见下面那段）
            foreach (Dictionary<string, object> call in res.Calls)
            {
                if (Cancelled) break;
                string name = Json.Str(call, "name", "?");
                if (name == "restart_upgrade") restartAsked = true;   // 这一轮马上要退出，别再往下走
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
                    answered.Add(callId);
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
                toolCount++;
                // 工具结果**摆全文**（上限 3000 字兜底）：只摆第一行的话"展开"根本没有内容可展开。
                // 摘要那一行由折叠组件负责（MsgBlock.FoldSummary）。
                UI.Write(OutKind.ToolResult, "  └ " + (ok ? "✓ " : "✗ ") + Txt.Clip(result, 3000) + "\n");
                Dictionary<string, object> toolMsg = Msg("tool", "[" + name + " 结果]\n" + result);
                toolMsg["tool_call_id"] = callId;
                toolMsg["tool_name"] = name;
                Messages.Add(toolMsg);
                answered.Add(callId);
            }

            // 被中断（点了停止）时，后面没轮到的调用必须补一条"未执行"的结果。
            // assistant 那条带 tool_calls 的消息已经进历史了，少一条配对结果就是坏历史 —— 而且这个伤是永久的：
            // 之后每一句都会被接口 400 掉（"An assistant message with 'tool_calls' must be followed by tool
            // messages responding to each 'tool_call_id'"），换模型也没用。用户 2026-10-17 就是这么卡住的。
            for (int ci = 0; ci < res.Calls.Count; ci++)
            {
                string cid = Json.Str(res.Calls[ci], "id", "");
                if (answered.Contains(cid)) continue;
                string cname = Json.Str(res.Calls[ci], "name", "?");
                UI.Write(OutKind.ToolResult, "  └ ⏹ " + cname + " 没执行（本轮已中断），已补一条占位结果\n");
                Messages.Add(SyntheticToolResult(cid, cname));
            }

            stall = NextStallCount(stall, anyOk);
            if (stall >= StallLimit) { stallStopped = stall; break; }

            // 它刚叫了"重启并升级"：进程马上要退出。再往下走只会把提醒和工具调用一路灌进历史
            // （用户 2026-10-15 那条会话就是这么绕进去的：重启回来计划行还是未完成 → 提醒 → 又叫重启）。
            if (restartAsked)
            {
                UI.Write(OutKind.Info, "  · 已请求「重启并升级」，本轮到此为止（进程马上退出）\n");
                break;
            }

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
                StepResult fin = await StepAsync(false);
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
        // 同上：全文进对话区/轨迹页，摘要行交给折叠组件
        UI.Write(OutKind.ToolResult, "  └ " + (ok ? "✓ " : "✗ ") + output + "\n");
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
            string[] preview = Preview(name, args);
            int decision;
            if (Config.remote_approval)
            {
                // 开关打开：高危操作一律推到通信台，等用户在手机/网页上点批准或拒绝
                string detail;
                string what = string.Join("\n", preview);
                int r = RemoteApproval.Ask(Config, what, "Ahz 想执行工具 " + name, out detail);
                if (r < 0)
                {
                    // 通信台起不来 / 脚本不在：回退到本地弹窗，绝不因此把操作卡死
                    Log.Audit("远程审批不可用（" + detail + "），回退本地窗口 · 工具 " + name);
                    decision = UI.Approve(name, preview);
                }
                else
                {
                    decision = r;
                    Log.Audit("远程审批·" + (r == 1 ? "批准" : "拒绝") + " · 工具 " + name + " · " + what);
                }
            }
            else
            {
                decision = UI.Approve(name, preview);
            }
            try
            {
                Log.Audit("审批·" + (decision == 0 ? "拒绝" : (decision == 2 ? "本会话总是允许" : "允许"))
                    + " · 工具 " + name + " · " + string.Join(" / ", preview));
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

    // 兼容 CLI/旧测试；产品 UI 走 RunTurnAsync。
    public void RunTurn(string userText)
    {
        RunTurnAsync(userText).GetAwaiter().GetResult();
    }

    async Task<StepResult> StepAsync()
    {
        StepResult res = new StepResult();
        return await StepAsync(true);
    }

    // allowTools=false：这一轮不给模型任何工具，用来逼它直接给结论
    async Task<StepResult> StepAsync(bool allowTools)
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
        Func<Task<Dictionary<string, object>>> send = async delegate()
        {
            return await Llm.StreamAsync(Messages, schemas, Config.think, Config.effort, Config.num_ctx,
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
        };
        Dictionary<string, object> last = null;
        bool needRetry = false;
        try { last = await send(); }
        catch (LocalAgentException ex)
        {
            // 图片是被接口拒掉的头号嫌疑（很多模型根本不支持看图）：
            // 去掉图片、原样重发一次，别让用户卡在一个看不懂的报错上。
            if (!Vision.MessagesHaveImages(Messages)) throw;
            Vision.Disabled = true;
            UI.Write(OutKind.Info, "\n  · 这个模型看不了图（" + Txt.FirstLine(ex.Message) + "），已去掉图片重发一次\n"
                + "  · 记住这个模型：接下来再带图也不会发了（换一个能看图的模型就自动恢复）。\n");
            thinkingStarted = false; contentStarted = false; contentDecided = false; markup = false;
            res.Content = ""; res.Calls.Clear();
            needRetry = true;
        }
        if (needRetry) last = await send();

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
        Console.ForegroundColor = ConsoleColor.Yellow;        Console.WriteLine("  ⚠ 需要你确认：" + toolName);
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
    public static Color Bg = Color.FromArgb(255, 255, 255);
    public static Color Side = Color.FromArgb(244, 244, 245);
    public static Color Card = Color.FromArgb(249, 250, 251);
    public static Color Field = Color.FromArgb(255, 255, 255);
    public static Color Chip = Color.FromArgb(243, 244, 246);
    public static Color ChipHover = Color.FromArgb(233, 235, 239);
    public static Color Hover = Color.FromArgb(241, 242, 244);
    public static Color Sel = Color.FromArgb(236, 238, 240);
    public static Color Line = Color.FromArgb(231, 232, 234);
    public static Color LineSoft = Color.FromArgb(240, 240, 242);
    public static Color Fg = Color.FromArgb(15, 17, 21);
    public static Color FgMid = Color.FromArgb(88, 93, 100);
    // 2026-10-18：FgDim/FgFaint 原来只有 3.25/2.21:1，9.5pt 小字达不到 WCAG AA 4.5:1。
    // 现在 FgDim 5.96:1、FgFaint 5.28:1（白底），层级仍然保持 Mid > Dim > Faint。
    public static Color FgDim = Color.FromArgb(95, 100, 108);
    public static Color FgFaint = Color.FromArgb(103, 108, 116);
    // 强调色白字原为 4.33:1（9.5pt 按钮不达标），压深到 5.46:1。
    public static Color Accent = Color.FromArgb(61, 90, 234);
    public static Color AccentHover = Color.FromArgb(52, 82, 232);
    public static Color AccentIdle = Color.FromArgb(187, 200, 251);
    public static Color AccentSoft = Color.FromArgb(238, 241, 254);
    public static Color SelFill = Color.FromArgb(110, 77, 107, 254);   // 拖选高亮
    public static Color Band = Color.FromArgb(245, 246, 248);
    public static Color Warn = Color.FromArgb(180, 83, 9);
    public static Color Ok = Color.FromArgb(5, 150, 105);
    public static Color Err = Color.FromArgb(220, 38, 38);
    // diff 面板用的淡底：加行淡绿、删行淡红（跟 Codex 那种审查面板一个观感）
    public static Color OkSoft = Color.FromArgb(236, 253, 245);
    public static Color ErrSoft = Color.FromArgb(254, 242, 242);
    public static Color Clear = Color.FromArgb(0, 0, 0, 0);

    // ---- Codex 那套外壳色（2026-10-13 按用户给的 Codex 截图逐像素量出来的）----
    // 标题栏和侧栏是同一层浅灰（截图里两侧同一个色值），输入卡下面那块「井」再深一档。
    public static Color Chrome = Color.FromArgb(244, 244, 245);
    public static Color Well = Color.FromArgb(242, 242, 243);
    // 发送键：Codex 是个深色圆钮，空输入时是灰的（不是蓝色的）
    public static Color SendIdle = Color.FromArgb(154, 155, 160);
    public static Color SendOn = Color.FromArgb(51, 53, 60);
    public static Color SendOnHover = Color.FromArgb(26, 27, 31);
    // 输入条左下角的权限提示：Codex 的「完全访问」是这个橙色
    public static Color PermWarn = Color.FromArgb(190, 70, 5);   // 白底 5.16:1（原 226,89,10 只有 3.71:1）
    // 浮层卡片的描边：Codex 那圈实测 #DDDFDF（比正文分隔线 Theme.Line 略深一档）
    public static Color PopupLine = Color.FromArgb(221, 223, 225);

    public const string FontName = "Microsoft YaHei UI";
    public const string MonoName = "Consolas";

    // ---- 8px 栅格令牌（2026-10-16 统一）----
    // 间距一律取 4 的倍数、常态是 8 的倍数；圆角只用这四档；控件高度也取 4 的倍数。
    // 新写的界面代码请从这里取值，不要各写各的数字（以前同一个左内边距有 11/14/15/17 四种写法）。
    public const int Sp4 = 4, Sp8 = 8, Sp12 = 12, Sp16 = 16, Sp24 = 24, Sp32 = 32;
    public const int RadMenu = 6, RadRow = 8, RadCard = 10, RadInput = 12;
    public const int RowHeight = 28, CtrlHeight = 32, MenuRowHeight = 28;

    // ---- 聊天气泡 / 内容块（2026-10-16 整改②）----
    // 两套气泡靠"填充 + 1px 描边 + 左右对齐 + 圆角 10"区分；代码块/行内代码各有自己的底色与描边。
    public static Color BubbleUserFill = Color.FromArgb(238, 241, 254);   // #EEF1FE
    public static Color BubbleUserLine = Color.FromArgb(206, 216, 250);   // #CED8FA
    public static Color BubbleAiFill = Color.FromArgb(250, 250, 251);     // #FAFAFB
    public static Color BubbleAiLine = Color.FromArgb(227, 228, 230);     // #E3E4E6（比 #E7E8EA 深一档，描边才真的看得见）
    public static Color CodeFill = Color.FromArgb(241, 243, 246);         // #F1F3F6
    public static Color CodeLine = Color.FromArgb(232, 235, 240);         // #E8EBF0
    public static Color InlineFill = Color.FromArgb(243, 244, 246);
    public static Color InlineLine = Color.FromArgb(229, 231, 235);
    public static bool Dark;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

    // 原生滚动条/列表边框不会跟 Theme 走，用 DarkMode_Explorer 主题把非客户区也压暗。
    public static void DarkenTree(Control root)
    {
        if (!Dark || root == null) return;
        DarkenOne(root);
        UiOverlayScroll.Attach(root);
        foreach (Control c in root.Controls) DarkenTree(c);
    }

    public static void DarkenOne(Control c)
    {
        if (!Dark || c == null || !c.IsHandleCreated) return;
        try
        {
            SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
            int on = 1;
            DwmSetWindowAttribute(c.Handle, 20, ref on, 4);
            DwmSetWindowAttribute(c.Handle, 19, ref on, 4);
            EnumChildWindows(c.Handle, delegate(IntPtr h, IntPtr l)
            {
                StringBuilder sb = new StringBuilder(64);
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() == "ScrollBar") SetWindowTheme(h, "DarkMode_Explorer", null);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    // 暗色是一套完整调色板，不是简单反色；切换后由启动流程重建界面。
    public static void Apply(bool dark)
    {
        Dark = dark;
        if (!dark)
        {
            Bg = Color.FromArgb(255, 255, 255); Side = Color.FromArgb(244, 244, 245);
            Card = Color.FromArgb(249, 250, 251); Field = Color.FromArgb(255, 255, 255);
            Chip = Color.FromArgb(243, 244, 246); ChipHover = Color.FromArgb(233, 235, 239);
            Hover = Color.FromArgb(241, 242, 244); Sel = Color.FromArgb(236, 238, 240);
            Line = Color.FromArgb(231, 232, 234); LineSoft = Color.FromArgb(240, 240, 242);
            Fg = Color.FromArgb(15, 17, 21); FgMid = Color.FromArgb(88, 93, 100);
            FgDim = Color.FromArgb(95, 100, 108); FgFaint = Color.FromArgb(103, 108, 116);
            Accent = Color.FromArgb(61, 90, 234); AccentHover = Color.FromArgb(52, 82, 232);
            AccentIdle = Color.FromArgb(187, 200, 251); AccentSoft = Color.FromArgb(238, 241, 254);
            SelFill = Color.FromArgb(110, 77, 107, 254); Band = Color.FromArgb(245, 246, 248);
            Warn = Color.FromArgb(180, 83, 9); Ok = Color.FromArgb(5, 150, 105); Err = Color.FromArgb(220, 38, 38);
            OkSoft = Color.FromArgb(236, 253, 245); ErrSoft = Color.FromArgb(254, 242, 242);
            Chrome = Color.FromArgb(244, 244, 245); Well = Color.FromArgb(242, 242, 243);
            SendIdle = Color.FromArgb(154, 155, 160); SendOn = Color.FromArgb(51, 53, 60); SendOnHover = Color.FromArgb(26, 27, 31);
            PermWarn = Color.FromArgb(190, 70, 5); PopupLine = Color.FromArgb(221, 223, 225);
            BubbleUserFill = Color.FromArgb(238, 241, 254); BubbleUserLine = Color.FromArgb(206, 216, 250);
            BubbleAiFill = Color.FromArgb(250, 250, 251); BubbleAiLine = Color.FromArgb(227, 228, 230);
            CodeFill = Color.FromArgb(241, 243, 246); CodeLine = Color.FromArgb(232, 235, 240);
            InlineFill = Color.FromArgb(243, 244, 246); InlineLine = Color.FromArgb(229, 231, 235);
            return;
        }
        Bg = Color.FromArgb(20, 22, 27); Side = Color.FromArgb(22, 24, 29);
        Card = Color.FromArgb(32, 35, 42); Field = Color.FromArgb(26, 28, 33);
        Chip = Color.FromArgb(42, 46, 54); ChipHover = Color.FromArgb(52, 57, 66);
        Hover = Color.FromArgb(42, 46, 54); Sel = Color.FromArgb(50, 55, 64);
        Line = Color.FromArgb(58, 63, 72); LineSoft = Color.FromArgb(42, 46, 53);
        Fg = Color.FromArgb(232, 234, 238); FgMid = Color.FromArgb(174, 180, 190);
        FgDim = Color.FromArgb(152, 159, 170); FgFaint = Color.FromArgb(136, 143, 154);
        Accent = Color.FromArgb(124, 150, 255); AccentHover = Color.FromArgb(146, 168, 255);
        AccentIdle = Color.FromArgb(68, 82, 120); AccentSoft = Color.FromArgb(42, 48, 68);
        SelFill = Color.FromArgb(120, 105, 130, 220); Band = Color.FromArgb(30, 33, 39);
        Warn = Color.FromArgb(245, 170, 80); Ok = Color.FromArgb(90, 210, 150); Err = Color.FromArgb(255, 110, 110);
        OkSoft = Color.FromArgb(20, 45, 35); ErrSoft = Color.FromArgb(55, 28, 30);
        Chrome = Color.FromArgb(30, 33, 39); Well = Color.FromArgb(18, 20, 24);
        SendIdle = Color.FromArgb(90, 94, 102); SendOn = Color.FromArgb(205, 209, 216); SendOnHover = Color.FromArgb(235, 237, 240);
        PermWarn = Color.FromArgb(255, 160, 80); PopupLine = Color.FromArgb(72, 78, 90);
        BubbleUserFill = Color.FromArgb(40, 48, 75); BubbleUserLine = Color.FromArgb(70, 85, 130);
        BubbleAiFill = Color.FromArgb(35, 38, 46); BubbleAiLine = Color.FromArgb(58, 62, 72);
        CodeFill = Color.FromArgb(27, 30, 36); CodeLine = Color.FromArgb(50, 54, 62);
        InlineFill = Color.FromArgb(40, 44, 52); InlineLine = Color.FromArgb(54, 58, 68);
    }

    public const int PadBubbleX = Sp16, PadBubbleY = Sp12;   // 气泡内边距（横 / 纵）—— 栅格值，别写 14/10 这种脱栅格的数
    public const int BubbleMaxPct = 75;                   // 用户气泡最宽占块宽的比例

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
        if (FontCache.TryGetValue(k, out f))
        {
            // 共享 Font 一旦被别的控件 Dispose，Height 会抛 ArgumentException；
            // 这里先验证，坏了就重建，避免整窗在 OnPaint 里崩掉。
            try { f.GetHeight(); return f; }
            catch { FontCache.Remove(k); try { f.Dispose(); } catch { } }
        }
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

                case "arrowLeft":
                    g.DrawLine(p, PX(r, 0.74f), PY(r, 0.50f), PX(r, 0.30f), PY(r, 0.50f));
                    g.DrawLines(p, new PointF[] { Pt(r, 0.47f, 0.32f), Pt(r, 0.29f, 0.50f), Pt(r, 0.47f, 0.68f) });
                    break;

                case "arrowRight":
                    g.DrawLine(p, PX(r, 0.26f), PY(r, 0.50f), PX(r, 0.70f), PY(r, 0.50f));
                    g.DrawLines(p, new PointF[] { Pt(r, 0.53f, 0.32f), Pt(r, 0.71f, 0.50f), Pt(r, 0.53f, 0.68f) });
                    break;

                case "panel":
                    // 侧栏开关（Codex 标题栏最左边那个）：方框 + 靠左一条竖线
                    using (GraphicsPath pnl = Ui.Round(new Rectangle((int)PX(r, 0.10f), (int)PY(r, 0.18f),
                        (int)(r.Width * 0.80f), (int)(r.Height * 0.64f)), (int)(r.Width * 0.16f)))
                        g.DrawPath(p, pnl);
                    g.DrawLine(p, PX(r, 0.40f), PY(r, 0.20f), PX(r, 0.40f), PY(r, 0.80f));
                    break;

                case "bell":
                    // 铃铛：钟形 + 底座 + 小舌头
                    g.DrawArc(p, PX(r, 0.30f), PY(r, 0.20f), r.Width * 0.40f, r.Height * 0.40f, 180, 180);
                    g.DrawLines(p, new PointF[] {
                        Pt(r, 0.30f, 0.40f), Pt(r, 0.24f, 0.72f), Pt(r, 0.76f, 0.72f), Pt(r, 0.70f, 0.40f) });
                    g.DrawLine(p, PX(r, 0.41f), PY(r, 0.82f), PX(r, 0.59f), PY(r, 0.82f));
                    break;

                case "newchat":
                    // 新聊天：一个开口的方框 + 一支斜笔（Codex 侧栏第一项就是这个）
                    g.DrawLines(p, new PointF[] {
                        Pt(r, 0.56f, 0.24f), Pt(r, 0.30f, 0.24f), Pt(r, 0.24f, 0.30f),
                        Pt(r, 0.24f, 0.72f), Pt(r, 0.68f, 0.72f), Pt(r, 0.76f, 0.64f), Pt(r, 0.76f, 0.48f) });
                    g.DrawLines(p, new PointF[] {
                        Pt(r, 0.50f, 0.62f), Pt(r, 0.62f, 0.72f), Pt(r, 0.86f, 0.48f), Pt(r, 0.74f, 0.38f), Pt(r, 0.50f, 0.62f) });
                    break;

                case "warnCircle":
                    // 圈里一个叹号（「完全访问」前面那个）
                    g.DrawEllipse(p, PX(r, 0.12f), PY(r, 0.12f), r.Width * 0.76f, r.Height * 0.76f);
                    g.DrawLine(p, PX(r, 0.50f), PY(r, 0.30f), PX(r, 0.50f), PY(r, 0.56f));
                    using (SolidBrush wcb = new SolidBrush(color))
                        g.FillEllipse(wcb, PX(r, 0.45f), PY(r, 0.64f), r.Width * 0.10f, r.Height * 0.10f);
                    break;

                case "monitor":
                    // 一块屏幕（输入卡上方「本地」那个药丸）
                    using (GraphicsPath mon = Ui.Round(new Rectangle((int)PX(r, 0.10f), (int)PY(r, 0.20f),
                        (int)(r.Width * 0.80f), (int)(r.Height * 0.54f)), (int)(r.Width * 0.14f)))
                        g.DrawPath(p, mon);
                    g.DrawLine(p, PX(r, 0.34f), PY(r, 0.86f), PX(r, 0.66f), PY(r, 0.86f));
                    break;

                case "cloudCode":
                    // 首屏那个云朵 logo：外面一圈云，里面一个 >_
                    g.DrawArc(p, PX(r, 0.10f), PY(r, 0.40f), r.Width * 0.36f, r.Height * 0.36f, 90, 180);
                    g.DrawArc(p, PX(r, 0.22f), PY(r, 0.16f), r.Width * 0.42f, r.Height * 0.42f, 180, 180);
                    g.DrawArc(p, PX(r, 0.54f), PY(r, 0.40f), r.Width * 0.36f, r.Height * 0.36f, 270, 180);
                    g.DrawLine(p, PX(r, 0.28f), PY(r, 0.76f), PX(r, 0.72f), PY(r, 0.76f));
                    g.DrawLines(p, new PointF[] { Pt(r, 0.41f, 0.44f), Pt(r, 0.49f, 0.52f), Pt(r, 0.41f, 0.60f) });
                    g.DrawLine(p, PX(r, 0.55f), PY(r, 0.60f), PX(r, 0.65f), PY(r, 0.60f));
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
    public Color Fill = Theme.Card;
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
    public float IconYOffset;
    public int Radius = Theme.RadRow;
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
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.Selectable, true);
        // 透明底：OnPaint 是"用父底色擦一遍再画圆角"，所以 BackColor 必须是透明的。
        // 不设的话它是 Control.DefaultBackColor（不透明浅灰），于是 FillColor=Clear 的 chip
        // 只剩一块灰矩形 —— 这就是用户说的"控件周边多余的小框"（2026-10-16 查到）。
        BackColor = Theme.Clear;
        Cursor = Cursors.Hand;
        // 2026-10-18 验收：以前全部 TabStop=false，键盘/读屏完全到不了审批、权限、模型这些按钮。
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        Font = Theme.F(9.5f);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        if (AccessibleName == null || AccessibleName.Length == 0) AccessibleName = Text;
        base.OnTextChanged(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    // 空格 / 回车激活。以前自绘按钮只能鼠标点，审批框里的「本会话总是允许」没有键盘路径。
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled) return;
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
        {
            PerformClick();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

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
        // 用父底色擦一遍：这里要擦成圆角，不能再是"铺满直角客户区"的方形 ——
        // 异色底（比如灰底上的白胶囊）会从四角糊出四个直角块，看着就是"多余的小框"（2026-10-16）。
        using (GraphicsPath bgp = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(Radius)))
        using (SolidBrush bg = new SolidBrush(BackColor.A > 0 ? BackColor : parentBg)) g.FillPath(bg, bgp);

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
        // 文字超宽就按可用宽度裁尾巴：不裁的话会直接糊出控件外（设置页那个长类型名在 150% 下就中过）。
        // 这里用「两边内边距 + 图标 + 箭头」算真实可用宽度，和下面摆位用的是同一套数。
        int room = Width - Theme.Px(Padding.Left) - Theme.Px(Padding.Right) - iconW - caretW
            - (Icon != null && txt.Length > 0 ? gap : 0);
        if (room > 0 && tw > room)
        {
            string cut = txt;
            while (cut.Length > 1 && Ui.TextWidth(cut + "…", Font) > room) cut = cut.Substring(0, cut.Length - 1);
            txt = cut + "…";
            tw = Ui.TextWidth(txt, Font);
        }
        int total = iconW + (tw > 0 ? ((iconW > 0 ? gap : 0) + tw) : 0) + caretW;
        int x;
        if (Align == ContentAlignment.MiddleLeft) x = Theme.Px(Padding.Left);
        else if (Align == ContentAlignment.MiddleRight) x = Width - Theme.Px(Padding.Right) - total;
        else x = (Width - total) / 2;

        if (Icon != null)
        {
            Rectangle ir = new Rectangle(x, (Height - iconSize) / 2 + Theme.Px(IconYOffset), iconSize, iconSize);
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
            // 箭头方向跟着"展开"走：收起时朝下、展开时朝上。
            // Active 就是"浮层开着"那个状态（模型 chip 展开时会同时点亮一块浅灰底）。
            if (!(CaretFlip && Active))
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
        // 可见焦点框：自绘按钮以前 Tab 到了也看不出焦点在哪。
        if (Focused && Enabled)
        {
            using (GraphicsPath fp = Ui.Round(new Rectangle(Theme.Px(2), Theme.Px(2),
                Math.Max(2, Width - Theme.Px(4) - 1), Math.Max(2, Height - Theme.Px(4) - 1)),
                Theme.Px(Math.Max(2, Radius - 2))))
            using (Pen pen = new Pen(Theme.Accent, Theme.Pxf(2f)))
                g.DrawPath(pen, fp);
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
        AccessibleName = "图标：" + icon;
    }
}

// 下拉 / 菜单按钮：点开一个浅色菜单
// 菜单项：一个字 + 右边那列快捷键提示（Codex 的菜单就长这样）
class MenuItem2
{
    public string Text;
    public string Hint;
    public MenuItem2(string t, string h) { Text = t; Hint = h; }
    public override string ToString() { return Text; }
}

// 自绘复选框：替换可见 CheckBox，方块和文字都自己画。
class UiCheck : Control
{
    bool _hover;
    bool _checked;

    public string Label = "";
    public event EventHandler CheckedChanged;

    public bool Checked
    {
        get { return _checked; }
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }
    }

    public UiCheck()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Height = Theme.Px(23);
        Width = Theme.Px(160);
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        int box = Theme.Px(16);
        int by = (Height - box) / 2;
        Rectangle r = new Rectangle(Theme.Px(2), by, box, box);
        using (GraphicsPath p = Ui.Round(r, Theme.Px(4)))
        {
            using (SolidBrush b = new SolidBrush(Checked ? Theme.Accent : Color.White)) g.FillPath(b, p);
            using (Pen pen = new Pen(Checked ? Theme.Accent : (_hover ? Theme.FgDim : Theme.Line), Theme.Pxf(1f)))
                g.DrawPath(pen, p);
        }
        if (Checked)
        {
            using (Pen pen = new Pen(Color.White, Theme.Pxf(2f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, r.Left + Theme.Px(4), r.Top + Theme.Px(8), r.Left + Theme.Px(7), r.Top + Theme.Px(11));
                g.DrawLine(pen, r.Left + Theme.Px(7), r.Top + Theme.Px(11), r.Left + Theme.Px(12), r.Top + Theme.Px(5));
            }
        }
        if (Focused && ShowFocusCues)
        {
            using (Pen pen = new Pen(Theme.Accent, Theme.Pxf(1f)))
                g.DrawRectangle(pen, r.Left - 2, r.Top - 2, r.Width + 4, r.Height + 4);
        }
        Rectangle tr = new Rectangle(r.Right + Theme.Px(8), 0, Math.Max(1, Width - r.Right - Theme.Px(8)), Height);
        TextRenderer.DrawText(g, Label, Font, tr, Enabled ? Theme.FgMid : Theme.FgFaint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        Checked = !Checked;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
    }
}

// 自绘数值框：替换设置页的两个系统 NumericUpDown（保留 min/max/value 用法）。
class UiNumber : Control
{
    decimal _min, _max, _val;
    int _decimals;
    bool _hover;

    public event EventHandler ValueChanged;

    public UiNumber()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Card;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        Height = Theme.Px(22);
        TabStop = true;
        AccessibleRole = AccessibleRole.SpinButton;
        AccessibleName = "数值";
    }

    public decimal Minimum
    {
        get { return _min; }
        set { _min = value; if (_max < _min) _max = _min; if (_val < _min) _val = _min; Invalidate(); }
    }

    public decimal Maximum
    {
        get { return _max; }
        set { _max = value; if (_max < _min) _min = _max; if (_val > _max) _val = _max; Invalidate(); }
    }

    public int DecimalPlaces { get { return _decimals; } set { _decimals = Math.Max(0, value); Invalidate(); } }

    public decimal Value
    {
        get { return _val; }
        set
        {
            decimal v = value;
            if (v < _min) v = _min;
            if (v > _max) v = _max;
            if (v == _val) return;
            _val = v;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
    }

    void Step(int dir)
    {
        decimal step = _decimals > 0 ? (decimal)Math.Pow(10, -_decimals) : 1m;
        Value = _val + step * dir;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using (GraphicsPath p = Ui.Round(r, Theme.Px(4)))
        {
            using (SolidBrush b = new SolidBrush(BackColor)) g.FillPath(b, p);
            using (Pen pen = new Pen(_hover ? Theme.FgDim : Theme.Line, Theme.Pxf(1f))) g.DrawPath(pen, p);
        }
        int arrowW = Theme.Px(18);
        Rectangle tr = new Rectangle(Theme.Px(8), 0, Math.Max(1, Width - arrowW - Theme.Px(10)), Height);
        TextRenderer.DrawText(g, _val.ToString("F" + _decimals), Font, tr, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        // 上下两个小三角
        int cx = Width - Theme.Px(9);
        int cy = Height / 2;
        using (SolidBrush b = new SolidBrush(Theme.FgMid))
        {
            Point[] up = new Point[] { new Point(cx - Theme.Px(4), cy - Theme.Px(3)), new Point(cx + Theme.Px(4), cy - Theme.Px(3)), new Point(cx, cy - Theme.Px(7)) };
            Point[] dn = new Point[] { new Point(cx - Theme.Px(4), cy + Theme.Px(3)), new Point(cx + Theme.Px(4), cy + Theme.Px(3)), new Point(cx, cy + Theme.Px(7)) };
            g.FillPolygon(b, up);
            g.FillPolygon(b, dn);
        }
        if (Focused && ShowFocusCues)
        {
            using (Pen pen = new Pen(Theme.Accent, Theme.Pxf(2f))) g.DrawRectangle(pen, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.X >= Width - Theme.Px(20))
        {
            Step(e.Y < Height / 2 ? 1 : -1);
            return;
        }
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Step(e.Delta > 0 ? 1 : -1);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Up || keyData == Keys.Right) { Step(1); return true; }
        if (keyData == Keys.Down || keyData == Keys.Left) { Step(-1); return true; }
        if (keyData == Keys.PageUp) { Value = _val + 10; return true; }
        if (keyData == Keys.PageDown) { Value = _val - 10; return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

// 自绘列表：替换设置页/改动页/插件页里默认的系统白底蓝选 ListBox。
// 暗色原生滚动条的替代：宿主仍用 AutoScroll/TopIndex 滚，外面盖一条自绘 thumb。
// 只处理垂直滚动；水平条直接隐藏（对话区/侧栏都按宽度换行，不需要横向拖动）。
class UiOverlayScroll : Control
{
    public readonly Control Host;
    bool _drag;
    bool _syncing;
    int _dragY, _dragValue;

    [DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr hWnd, int bar, bool show);

    public UiOverlayScroll(Control host)
    {
        Host = host;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Side;
        Width = Theme.Px(8);
        host.Resize += delegate { Sync(); };
        host.LocationChanged += delegate { Sync(); };
        host.VisibleChanged += delegate { Sync(); };
        host.HandleCreated += delegate { Sync(); };
        host.Layout += delegate { Sync(); };
        host.Paint += delegate { try { ShowScrollBar(host.Handle, 1, false); ShowScrollBar(host.Handle, 0, false); } catch { } };
        host.MouseWheel += delegate { Sync(); };
        ScrollableControl sc0 = host as ScrollableControl;
        if (sc0 != null) sc0.Scroll += delegate { Sync(); };
        ListBox lb0 = host as ListBox;
        if (lb0 != null)
        {
            lb0.SelectedIndexChanged += delegate { Sync(); };
            lb0.MouseWheel += delegate { Sync(); };
        }
        ParentChanged += delegate { Sync(); };
        Sync();
    }

    int MaxValue()
    {
        ScrollableControl sc = Host as ScrollableControl;
        if (sc != null) return Math.Max(0, sc.VerticalScroll.Maximum - sc.VerticalScroll.LargeChange + 1);
        ListBox lb = Host as ListBox;
        if (lb != null)
        {
            int visible = Math.Max(1, lb.ClientSize.Height / Math.Max(1, lb.ItemHeight));
            return Math.Max(0, lb.Items.Count - visible);
        }
        return 0;
    }

    int CurValue()
    {
        ScrollableControl sc = Host as ScrollableControl;
        if (sc != null) return sc.VerticalScroll.Value;
        ListBox lb = Host as ListBox;
        if (lb != null) return lb.TopIndex;
        return 0;
    }

    void SetValue(int v)
    {
        ScrollableControl sc = Host as ScrollableControl;
        if (sc != null)
        {
            try { sc.VerticalScroll.Value = Math.Max(0, Math.Min(MaxValue(), v)); sc.PerformLayout(); } catch { }
            return;
        }
        ListBox lb = Host as ListBox;
        if (lb != null && lb.Items.Count > 0)
            lb.TopIndex = Math.Max(0, Math.Min(MaxValue(), v));
    }

    public void Sync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (Host == null || Host.Parent == null || !Host.IsHandleCreated) { Visible = false; return; }
            if (Host.Parent != Parent) { Visible = false; return; }
            ShowScrollBar(Host.Handle, 1, false);
            ShowScrollBar(Host.Handle, 0, false);
            int max = MaxValue();
            if (max <= 0) { Visible = false; return; }
            Visible = true;
            int w = Theme.Px(8);
            int h = Host.Height;
            SetBounds(Host.Left + Host.Width - w - Theme.Px(1), Host.Top + Theme.Px(1), w, Math.Max(10, h - Theme.Px(2)));
            BringToFront();
            Invalidate();
        }
        catch { Visible = false; }
        finally { _syncing = false; }
    }

    Rectangle Thumb()
    {
        int max = Math.Max(1, MaxValue());
        int h = Math.Max(10, Height - Theme.Px(4));
        int visible = 1;
        ScrollableControl sc = Host as ScrollableControl;
        if (sc != null) visible = Math.Max(1, sc.VerticalScroll.LargeChange);
        else if (Host is ListBox)
        {
            ListBox lb = (ListBox)Host;
            visible = Math.Max(1, lb.ClientSize.Height / Math.Max(1, lb.ItemHeight));
        }
        int total = max + visible;
        int th = Math.Max(Theme.Px(24), (int)((long)h * visible / Math.Max(1, total)));
        int ty = Theme.Px(2) + (int)((long)(h - th) * CurValue() / max);
        return new Rectangle(Theme.Px(2), ty, Math.Max(2, Width - Theme.Px(4)), th);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (SolidBrush bg = new SolidBrush(BackColor)) e.Graphics.FillRectangle(bg, ClientRectangle);
        Rectangle tr = Thumb();
        Color c = _drag ? Theme.FgMid : Theme.FgFaint;
        using (SolidBrush b = new SolidBrush(c)) e.Graphics.FillRectangle(b, tr);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Rectangle tr = Thumb();
        if (!tr.Contains(e.Location)) return;
        _drag = true;
        _dragY = e.Y;
        _dragValue = CurValue();
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drag) return;
        int h = Math.Max(10, Height - Theme.Px(4));
        int max = Math.Max(1, MaxValue());
        int delta = (int)((long)(e.Y - _dragY) * max / Math.Max(1, h - Thumb().Height));
        SetValue(_dragValue + delta);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = false;
        Capture = false;
        Sync();
    }

    public static void Attach(Control host)
    {
        if (!Theme.Dark || host == null || host.Parent == null) return;
        bool scrollable = host is ScrollableControl && ((ScrollableControl)host).AutoScroll;
        if (!scrollable && !(host is ListBox)) return;
        foreach (Control c in host.Parent.Controls)
        {
            UiOverlayScroll s = c as UiOverlayScroll;
            if (s != null && s.Host == host) return;
        }
        UiOverlayScroll bar = new UiOverlayScroll(host);
        host.Parent.Controls.Add(bar);
        bar.BringToFront();
    }
}

class UiList : ListBox
{
    public UiList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Card;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        ItemHeight = Theme.Px(28);
        IntegralHeight = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        Color bg = sel ? Theme.Sel : BackColor;
        using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
        Rectangle tr = new Rectangle(e.Bounds.Left + Theme.Px(8), e.Bounds.Top,
            Math.Max(1, e.Bounds.Width - Theme.Px(16)), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, Convert.ToString(Items[e.Index]), Font, tr,
            Enabled ? (sel ? Theme.Fg : ForeColor) : Theme.FgFaint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if ((e.State & DrawItemState.Focus) != 0 && sel)
        {
            using (Pen p = new Pen(Theme.Accent, Theme.Pxf(1f)))
                e.Graphics.DrawRectangle(p, e.Bounds.Left + 1, e.Bounds.Top + 1,
                    Math.Max(0, e.Bounds.Width - 3), Math.Max(0, e.Bounds.Height - 3));
        }
    }
}

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
        AccessibleRole = AccessibleRole.ComboBox;
        AccessibleName = "下拉选择";
        ShowCaret = true;
        Align = ContentAlignment.MiddleLeft;
        // 内边距走栅格令牌（左 12 给文字、右 12 给箭头），圆角吸附 RadRow
        Padding = new Padding(Theme.Sp12, 0, Theme.Sp12, 0);
        Radius = Theme.RadRow;
        Font = Theme.F(9.5f);
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
        MenuRound.Apply(menu);
        menu.ShowImageMargin = false;
        menu.DropShadowEnabled = true;       // 圆角需要阴影才能在白底上看出边界
        menu.BackColor = Theme.Card;
        menu.ForeColor = Theme.Fg;
        menu.Font = Theme.F(9.5f);
        menu.Padding = new Padding(Theme.Px(8), Theme.Px(6), Theme.Px(8), Theme.Px(6));
        menu.Renderer = new MenuTheme();
        foreach (object it in Items)
        {
            object captured = it;
            bool isSel = !KeepText && _sel != null && object.Equals(_sel, captured);
            MenuItem2 m2 = captured as MenuItem2;
            ToolStripMenuItem mi = new ToolStripMenuItem(Convert.ToString(captured));
            if (m2 != null && m2.Hint != null && m2.Hint.Length > 0)
                mi.ShortcutKeyDisplayString = m2.Hint;      // 右对齐那列灰字
            mi.Padding = new Padding(Theme.Px(12), Theme.Px(3), Theme.Px(12), Theme.Px(3));
            mi.Margin = new Padding(0, Theme.Px(1), 0, Theme.Px(1));
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
        // 下拉卡片水平居中在按钮下面（以前是左对齐，文件/编辑/视图/帮助看起来都偏一边）
        Size pref = menu.GetPreferredSize(Size.Empty);
        if (pref.Width < 1) pref = menu.Size;
        menu.Show(this, new Point((Width - pref.Width) / 2, Height + 6));
    }
}

class MenuTheme : ToolStripProfessionalRenderer
{
    public MenuTheme() : base(new MenuColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        Rectangle r = e.TextRectangle;
        int h = TextRenderer.MeasureText(e.Text, e.TextFont).Height;
        e.TextRectangle = new Rectangle(r.X, r.Y + Math.Max(0, (r.Height - h) / 2), r.Width, h);
        base.OnRenderItemText(e);
    }
}

// WinForms 的 ToolStripDropDown 会反复重置 Region；直接给原生窗口 SetWindowRgn，
// 让主菜单/右键菜单也跟主窗口一样是圆角，而不是方方正正的系统卡片。
static class MenuRound
{
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32.dll")]
    static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(ToolStripDropDown menu)
    {
        if (menu == null) return;
        menu.Opened += delegate
        {
            Round(menu);
            // ToolStrip 在 Opened 之后还会再摆一次布局，同步 SetWindowRgn 会被它覆盖；
            // 延迟到消息队列末尾再切一次，才是最终可见的窗口形状。
            try
            {
                if (menu.IsHandleCreated)
                    menu.BeginInvoke(new MethodInvoker(delegate { Round(menu); }));
            }
            catch { }
        };
        menu.SizeChanged += delegate { Round(menu); };
    }

    static void Round(ToolStripDropDown menu)
    {
        try
        {
            if (!menu.IsHandleCreated || menu.Width < 2 || menu.Height < 2) return;
            int r = Theme.Px(10);
            IntPtr h = CreateRoundRectRgn(0, 0, menu.Width + 1, menu.Height + 1, r * 2, r * 2);
            if (h != IntPtr.Zero) SetWindowRgn(menu.Handle, h, true);   // 区域所有权交给系统
            // Win11 的 DWM 圆角对弹出菜单比 SetWindowRgn 更稳（菜单句柄会被 ToolStrip 重整）。
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(menu.Handle, 33, ref pref, sizeof(int));
        }
        catch { }
    }
}

class MenuColors : ProfessionalColorTable
{
    public override Color MenuItemSelected { get { return Theme.Hover; } }
    // 选中项的渐变也要给：只给 MenuItemSelected 的话，系统渲染器还会画它自己那套默认渐变，
    // 灰底上叠出一条和自绘悬停色不一样的横带（浮层里"看着有第二层框"的来源之一）。
    public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
    public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
    public override Color MenuItemPressedGradientBegin { get { return Theme.Hover; } }
    public override Color MenuItemPressedGradientMiddle { get { return Theme.Hover; } }
    public override Color MenuItemPressedGradientEnd { get { return Theme.Hover; } }
    public override Color MenuItemBorder { get { return Theme.Hover; } }
    public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
    public override Color MenuBorder { get { return Theme.Line; } }
    public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
    public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
    public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
    public override Color SeparatorDark { get { return Theme.LineSoft; } }
    public override Color SeparatorLight { get { return Theme.Card; } }
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
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = "模型强度";
        Font = Theme.F(9.5f);
        Size = new Size(Theme.Px(232), Theme.Px(112));
    }

    int HeadTop { get { return Theme.Px(12); } }
    int TrackL { get { return Theme.Px(22); } }
    int TrackW { get { return Math.Max(Theme.Px(60), Width - Theme.Px(44)); } }
    int TrackY { get { return Theme.Px(104); } }
    int TrackH { get { return Theme.Px(13); } }
    int LevelTop { get { return Theme.Px(36); } }
    int TitleTop { get { return Theme.Px(78); } }
    int TitleH { get { return Theme.Px(22); } }

    // 中间那行「模型名 ›」的胶囊区域（点它 = 换模型）
    public Rectangle TitleRect
    {
        get
        {
            Font f = Theme.Cache(Theme.FontName, 9f, FontStyle.Regular);
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
            Font fh = Theme.Cache(Theme.FontName, 10f, FontStyle.Regular);   // 参考图那行小标题比正文略大、中灰
            TextRenderer.DrawText(g, Head, fh, new Point(Theme.Px(14), HeadTop), Theme.FgDim,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        // 当前档位（强调蓝、加粗、居中）。不支持调强度的模型也照常显示当前档位文字，
        // 只是下面不画滑块 —— 否则整张卡片会是一片空白（用户报的 bug）。
        string lvl = Effort.Label(Effort.Levels[Value]);
        Font fb = Theme.Cache(Theme.FontName, 17f, FontStyle.Bold);
        TextRenderer.DrawText(g, lvl, fb,
            new Point((Width - Ui.TextWidth(lvl, fb)) / 2, LevelTop), Theme.Accent,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        // 中间：「模型名 ›」。参考图（Codex）这里是纯灰文字，不是胶囊底 —— 胶囊会看成一枚按钮
        Rectangle tr = TitleRect;
        Font ft = Theme.Cache(Theme.FontName, 9f, FontStyle.Regular);
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
            Font fn = Theme.Cache(Theme.FontName, 9.5f, FontStyle.Regular);
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
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.ListItem;
        AccessibleName = "变更文件";
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
            Font fv = Theme.Cache(Theme.FontName, 9f, FontStyle.Regular);
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
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "选择模型";
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
                h.H = Theme.Px(Theme.Sp24);        // 组头行高吸附 24（原 26）
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
        _contentH = y + Theme.Px(Theme.Sp8);
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
        int h = rows * _rowH + Theme.Px(Theme.Sp4);
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
    int RowW { get { return Math.Max(Theme.Px(60), Width - Theme.Px(Theme.Sp12)); } }

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
            using (Font ft = Theme.F(10f))   // 和强度卡那行小标题统一；本控件自己持有，不碰共享缓存
                TextRenderer.DrawText(g, "选择模型", ft, new Point(Theme.Px(14), Theme.Px(10)), Theme.FgDim,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        using (Font fh = Theme.F(8.5f))
        using (Font fr = Theme.F(9.5f))
        {
        // 行和组标题都画在列表区里，超出列表区的部分裁掉
        Region old = g.Clip;
        g.SetClip(new Rectangle(0, ViewTop, Width, ViewH));
        foreach (PickItem it in _items)
        {
            int y = ViewTop + it.Y - _scrollTop;
            // 只画"完整落在列表区里"的行。以前允许半行露出，但 TextRenderer.DrawText 不遵守
            // GDI+ 的 SetClip，半行文字会画进上面的"选择模型"标题区（2026-10-18 验收 P0）。
            if (y < ViewTop || y + it.H > ViewTop + ViewH) continue;
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
    public int HeadHeight { get { return _headH; } }          // 头部占位（调用方要区分"总高"与"列表体高"）
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

    public int Pad = Theme.Sp8;       // 窗口给卡片留的边（只要够容纳 1px 描边就行；以前是 16，当阴影区用）
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
        BackColor = Theme.Card;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.F(9.5f);
        Gap = Theme.Px(Theme.Sp8);
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
        // 1px 细线要配 PixelOffsetMode.Half，否则描边会被糊成 2px（"多余小框"的来源之一）
        g.PixelOffsetMode = PixelOffsetMode.Half;
        Rectangle card = CardRect;
        // 不再画阴影圈（2026-10-16）：窗口 region 的半径是"卡片半径 + 留白"，而卡片描边半径只有卡片半径，
        // 夹在中间那圈留白会被 region 硬化成一个更大的白框 —— 用户看到的就是"白框里套一个小框"。
        // 现在层次只靠"白底 + 1px 描边"，和整体那种克制的观感一致。
        using (GraphicsPath cp = Ui.Round(card, Theme.Px(Theme.RadCard)))
        {
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, cp);
            using (Pen pen = new Pen(Theme.PopupLine, Theme.Pxf(1f))) g.DrawPath(pen, cp);
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
            // 只比卡片大一个描边宽度：再大就会露出一圈纯白，被裁成"外面那个白框"
            int grow = Math.Max(1, Theme.Px(1));
            Rectangle outer = new Rectangle(
                Math.Max(0, card.X - grow), Math.Max(0, card.Y - grow),
                Math.Min(Width, card.Width + grow * 2), Math.Min(Height, card.Height + grow * 2));
            using (GraphicsPath gp = Ui.Round(outer, Theme.Px(Theme.RadCard) + grow))
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
        Native.RoundWindow(Handle);
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
        // Codex 输入条右下角那个：平时就是一行深色小字 + 箭头，没有底色；
        // 只有浮层开着的时候才浮出一块浅灰底（Active 那套现成的）。
        FillColor = Theme.Clear;
        HoverColor = Theme.Hover;
        TextColor = Theme.Fg;
        // 输入条上这颗是**胶囊**（半径 = 高度的一半），和设置页那种 8px 圆角下拉是两种形状：
        // 这是有意的区分（参考图里输入条的 chip 就是胶囊），不是为了"统一"就该拉平 —— 自测也在守它。
        Radius = 18;
        Font = Theme.F(9.5f);
        Padding = new Padding(Theme.Sp4, 0, Theme.Sp8, 0);
        ActiveFill = Theme.Chip;
        ActiveText = Theme.Fg;
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

    int RowH { get { return Theme.Px(Theme.MenuRowHeight); } }   // 栅格：菜单行高 28
    int PadY { get { return Theme.Px(Theme.Sp4); } }
    int PadX { get { return Theme.Px(Theme.Sp4); } }
    private int _down = -1;                     // 按下态（三态：选中 / 按下 / 悬停）
    public int Disabled = -1;                   // 要置灰的行（-1 = 没有）

    public PermMenu(int cur)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.MenuItem;
        AccessibleName = "权限选择";
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
#if !NET10_0_OR_GREATER
        _proxies = new RowProxy[Perm.Labels.Length];
        for (int i = 0; i < Perm.Labels.Length; i++)
        {
            RowProxy p = new RowProxy(this, i);
            _proxies[i] = p;
            Controls.Add(p);
        }
        LayoutProxies();
#endif
    }

#if !NET10_0_OR_GREATER
    RowProxy[] _proxies;

    internal void PickRow(int i)
    {
        if (i < 0 || i >= Perm.Labels.Length || i == Disabled || i == Current) return;
        Current = i;
        Invalidate();
        InvalidateProxies();
        if (RowPicked != null) RowPicked(this, EventArgs.Empty);
    }

    void LayoutProxies()
    {
        if (_proxies == null) return;
        for (int i = 0; i < _proxies.Length; i++)
        {
            _proxies[i].Bounds = RowRect(i);
            _proxies[i].BringToFront();
        }
    }

    void InvalidateProxies()
    {
        if (_proxies == null) return;
        for (int i = 0; i < _proxies.Length; i++) _proxies[i].Invalidate();
    }

    class RowProxy : Control
    {
        readonly PermMenu _owner;
        readonly int _index;
        public RowProxy(PermMenu owner, int index)
        {
            _owner = owner; _index = index;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = owner.BackColor;
            Cursor = Cursors.Hand;
            TabStop = false;
            AccessibleRole = AccessibleRole.RadioButton;
            AccessibleName = Perm.Labels[index];
            AccessibleDefaultActionDescription = "选择";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(_owner.BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
            _owner.DrawRow(e.Graphics, _index, Left, Top);
        }
        protected override void OnMouseEnter(EventArgs e) { _owner._hover = _index; _owner.InvalidateProxies(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _owner._hover = -1; _owner.InvalidateProxies(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) _owner.PickRow(_index);
        }
    }
#endif

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
#if !NET10_0_OR_GREATER
        LayoutProxies();
#endif
    }

    public Rectangle RowRect(int i)
    {
        return new Rectangle(PadX, PadY + i * RowH, Width - PadX * 2, RowH);
    }

#if NET10_0_OR_GREATER
    // .NET 10 的 WinForms 允许自定义 AccessibleObject 进入 UIA 树；
    // 旧 .NET Framework 只保留控件级 Role/Name（子项会被 Control.AccessibilityObject 判空）。
    protected override AccessibleObject CreateAccessibilityInstance()
    {
        return new MenuAccessible(this);
    }

    internal void PickRow(int i)
    {
        if (i < 0 || i >= Perm.Labels.Length || i == Disabled || i == Current) return;
        Current = i;
        Invalidate();
        if (RowPicked != null) RowPicked(this, EventArgs.Empty);
    }

    class MenuAccessible : AccessibleObject
    {
        readonly PermMenu _owner;
        public MenuAccessible(PermMenu owner) { _owner = owner; }
        public override string Name { get { return "权限选择"; } }
        public override AccessibleRole Role { get { return AccessibleRole.MenuItem; } }
        public override int GetChildCount() { return Perm.Labels.Length; }
        public override AccessibleObject GetChild(int index)
        {
            if (index < 0 || index >= Perm.Labels.Length) return null;
            return new RowAccessible(_owner, index);
        }
        public override AccessibleObject GetFocused()
        {
            return _owner.Current >= 0 ? new RowAccessible(_owner, _owner.Current) : null;
        }
    }

    class RowAccessible : AccessibleObject
    {
        readonly PermMenu _owner;
        readonly int _index;
        public RowAccessible(PermMenu owner, int index) { _owner = owner; _index = index; }
        public override string Name { get { return Perm.Labels[_index]; } }
        public override AccessibleRole Role { get { return AccessibleRole.RadioButton; } }
        public override AccessibleObject Parent { get { return _owner.AccessibilityObject; } }
        public override Rectangle Bounds { get { return _owner.RectangleToScreen(_owner.RowRect(_index)); } }
        public override string DefaultAction { get { return "选择"; } }
        public override AccessibleStates State
        {
            get
            {
                AccessibleStates s = AccessibleStates.Focusable | AccessibleStates.Selectable;
                if (_owner.Current == _index) s |= AccessibleStates.Checked | AccessibleStates.Selected | AccessibleStates.Focused;
                if (_owner.Disabled == _index) s |= AccessibleStates.Unavailable;
                return s;
            }
        }
        public override void DoDefaultAction() { _owner.PickRow(_index); }
    }
#endif

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
        for (int i = 0; i < Perm.Labels.Length; i++) DrawRow(g, i);
    }

    internal void DrawRow(Graphics g, int i, int offsetX = 0, int offsetY = 0)
    {
        Font f = Theme.Cache(Theme.FontName, 9.5f, FontStyle.Regular);
        Rectangle r = RowRect(i);
        r.Offset(-offsetX, -offsetY);
        bool dis = (i == Disabled);
        bool cur = (i == Current);
        if (cur || i == _down || (i == _hover && !dis))
        {
            Color fill = cur ? Theme.Sel : (i == _down ? Theme.ChipHover : Theme.Hover);
            using (GraphicsPath hp = Ui.Round(r, Theme.Px(Theme.RadRow)))
            using (SolidBrush hb = new SolidBrush(fill)) g.FillPath(hb, hp);
        }
        int ic = Theme.Px(16);
        Color icc = dis ? Theme.FgFaint : (cur ? Theme.Accent : Theme.FgMid);
        Icons.Draw(g, Perm.Icons2[i], new Rectangle(r.Left + Theme.Px(10), r.Top + (r.Height - ic) / 2, ic, ic), icc, 1.5f);
        TextRenderer.DrawText(g, Perm.Labels[i], f,
            new Rectangle(r.Left + Theme.Px(34), r.Top, r.Width - Theme.Px(62), r.Height),
            dis ? Theme.FgFaint : Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        if (cur)
            Icons.Draw(g, "check", new Rectangle(r.Right - Theme.Px(22), r.Top + r.Height / 2 - Theme.Px(8),
                Theme.Px(15), Theme.Px(15)), Theme.Accent, 1.7f);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = RowAt(e.Location);
        if (h == Disabled) h = -1;
        if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1 || _down != -1) { _hover = -1; _down = -1; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int h = RowAt(e.Location);
        if (h < 0 || h == Disabled) return;
        _down = h;                       // 按下就先给一次视觉回应（选中态落定之前）
        Invalidate();
        if (h == Current) { _down = -1; Invalidate(); return; }   // 点当前档不折腾
        Current = h;
        Invalidate();
        if (RowPicked != null) RowPicked(this, EventArgs.Empty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_down != -1) { _down = -1; Invalidate(); }
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
        // 传"列表体"高度：ListHeight 已经含 _headH，而 SetListHeight 会再加一次 ——
        // 直接传 ListHeight 会让子控件比卡片高一个 _headH（36px），正好盖掉卡片底边描边
        // （用户看到的"多余的框/底边断掉"，2026-10-16 查到）。
        _pick.SetListHeight(_hList - _pick.HeadHeight);
        _pick.Scrolled += delegate { _pick.Invalidate(); };
        _pick.Location = new Point(0, 0);        // Location 由 LayoutPage 统一摆
        _pick.Visible = false;
        Controls.Add(_pick);

        // ---- 强度页 ----
        _eff = new EffortSlider();
        _eff.Head = "选择强度";
        _wStrength = Theme.Px(204);
        // 卡片高 136 → 124：内容只画到 119（TrackY 106 + 轨道 13），底部那 17px 是凑出来的空白。
        _hStrength = Theme.Px(124);
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
            ForceRepaint();      // 不激活窗口换页后必须整窗重画，否则会短暂/持续空白
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
            ForceRepaint();      // 换页结束整窗重画，避免浮层变白/空白
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
            _pick.SetListHeight(room - _pick.HeadHeight);   // 同上：只给列表体高度
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
// 首屏那个云朵标（Codex 空状态的 logo）：只描边、不填色
class CloudMark : Control
{
    public CloudMark()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        Icons.Draw(g, "cloudCode", new Rectangle(0, 0, Width - 1, Height - 1), Theme.FgFaint, 1.7f);
    }
}

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
        _fTime = Theme.F(8.5f);
        Font = _fTitle;
        Height = Theme.Px(30);
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
            using (GraphicsPath p = Ui.Round(new Rectangle(Theme.Px(8), Theme.Px(1), Width - Theme.Px(16), Height - Theme.Px(2)), Theme.Px(8)))
            using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
        }

        // Codex 侧栏的行：项目行前面一个文件夹图标，会话行没有图标；
        // 两种行的文字左边缘对齐（x=40），后面不跟时间（时间挪到鼠标悬停的提示里）。
        if (IsProject)
        {
            int iconSize = Theme.Px(14);
            Icons.Draw(g, "folder", new Rectangle(Theme.Px(18), (Height - iconSize) / 2, iconSize, iconSize),
                Theme.FgMid, 1.4f);
        }

        int textX = Theme.Px(40);
        Rectangle cr = new Rectangle(textX, 0, Math.Max(10, Width - textX - Theme.Px(16)), Height);
        TextRenderer.DrawText(g, Text, _fTitle, cr, IsProject ? Theme.Fg : Theme.FgMid,
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
            y += c.Height + Theme.Px(1);
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
        AccessibleRole = AccessibleRole.PageTabList;
        AccessibleName = "对话与轨迹";
        Cursor = Cursors.Hand;
#if !NET10_0_OR_GREATER
        // 旧 Framework 的 Control.AccessibilityObject 不收自定义 AccessibleObject；
        // 用真实子控件承载每个页签，MSAA/UIA 才能逐项朗读。
        _proxies = new TabProxy[Tabs.Length];
        for (int i = 0; i < Tabs.Length; i++)
        {
            TabProxy p = new TabProxy(this, i);
            _proxies[i] = p;
            Controls.Add(p);
        }
        LayoutProxies();
#endif
    }

#if !NET10_0_OR_GREATER
    TabProxy[] _proxies;

    void LayoutProxies()
    {
        if (_proxies == null) return;
        for (int i = 0; i < _proxies.Length; i++)
        {
            Rectangle r = TabBounds(i);
            r.Height = Math.Max(1, Height - Theme.Px(1));
            _proxies[i].Bounds = r;
            _proxies[i].BringToFront();
        }
    }

    void InvalidateProxies()
    {
        if (_proxies == null) return;
        for (int i = 0; i < _proxies.Length; i++) _proxies[i].Invalidate();
    }

    class TabProxy : Control
    {
        readonly TabsBar _owner;
        readonly int _index;
        public TabProxy(TabsBar owner, int index)
        {
            _owner = owner; _index = index;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint, true);
            BackColor = owner.BackColor;
            Cursor = Cursors.Hand;
            TabStop = false;
            AccessibleRole = AccessibleRole.PageTab;
            AccessibleName = owner.Tabs[index];
            AccessibleDefaultActionDescription = "切换";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(_owner.BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
            _owner.DrawTab(e.Graphics, _index, Left);
        }
        protected override void OnMouseEnter(EventArgs e)
        {
            _owner._hot = _index;
            _owner.InvalidateProxies();
            base.OnMouseEnter(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            _owner._hot = -1;
            _owner.InvalidateProxies();
            base.OnMouseLeave(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) _owner.SelectTab(_index);
        }
    }
#endif

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
#if !NET10_0_OR_GREATER
        LayoutProxies();
#endif
    }

    int TabX(int i)
    {
        int x = Theme.Px(22);
        using (Font f = Theme.F(9.5f))
        {
            for (int k = 0; k < i; k++) x += Ui.TextWidth(Tabs[k], f) + Theme.Px(30);
        }
        return x;
    }

    internal Rectangle TabBounds(int i)
    {
        using (Font f = Theme.F(9.5f))
        {
            int x = TabX(i);
            int w = Ui.TextWidth(Tabs[i], f) + Theme.Px(30);
            return new Rectangle(x - Theme.Px(8), 0, w, Height);
        }
    }

    int HitTest(Point p)
    {
        for (int i = 0; i < Tabs.Length; i++)
            if (TabBounds(i).Contains(p)) return i;
        return -1;
    }

    internal void SelectTab(int i)
    {
        if (i < 0 || i >= Tabs.Length || i == SelectedIndex) return;
        SelectedIndex = i;
        Invalidate();
#if !NET10_0_OR_GREATER
        InvalidateProxies();
#endif
        if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
    }

#if NET10_0_OR_GREATER
    protected override AccessibleObject CreateAccessibilityInstance()
    {
        return new TabsAccessible(this);
    }

    class TabsAccessible : AccessibleObject
    {
        readonly TabsBar _owner;
        public TabsAccessible(TabsBar owner) { _owner = owner; }
        public override string Name { get { return "对话与轨迹"; } }
        public override AccessibleRole Role { get { return AccessibleRole.PageTabList; } }
        public override int GetChildCount() { return _owner.Tabs.Length; }
        public override AccessibleObject GetChild(int index)
        {
            if (index < 0 || index >= _owner.Tabs.Length) return null;
            return new TabAccessible(_owner, index);
        }
        public override AccessibleObject GetFocused()
        {
            return _owner.SelectedIndex >= 0 ? new TabAccessible(_owner, _owner.SelectedIndex) : null;
        }
    }

    class TabAccessible : AccessibleObject
    {
        readonly TabsBar _owner;
        readonly int _index;
        public TabAccessible(TabsBar owner, int index) { _owner = owner; _index = index; }
        public override string Name { get { return _owner.Tabs[_index]; } }
        public override AccessibleRole Role { get { return AccessibleRole.PageTab; } }
        public override AccessibleObject Parent { get { return _owner.AccessibilityObject; } }
        public override Rectangle Bounds { get { return _owner.RectangleToScreen(_owner.TabBounds(_index)); } }
        public override string DefaultAction { get { return "切换"; } }
        public override AccessibleStates State
        {
            get
            {
                AccessibleStates s = AccessibleStates.Focusable | AccessibleStates.Selectable;
                if (_owner.SelectedIndex == _index) s |= AccessibleStates.Selected | AccessibleStates.Focused;
                return s;
            }
        }
        public override void DoDefaultAction() { _owner.SelectTab(_index); }
    }
#endif

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
        if (h >= 0) SelectTab(h);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        for (int i = 0; i < Tabs.Length; i++) DrawTab(g, i);
        using (Pen p = new Pen(Theme.LineSoft, Theme.Pxf(1f))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
    }

    internal void DrawTab(Graphics g, int i, int offsetX = 0)
    {
        bool sel = i == SelectedIndex;
        Font f = Theme.Cache(Theme.FontName, 9.5f, sel ? FontStyle.Bold : FontStyle.Regular);
        Color c = sel ? Theme.Fg : (_hot == i ? Theme.FgMid : Theme.FgDim);
        int x = TabX(i) - offsetX;
        int w = Ui.TextWidth(Tabs[i], f);
        Rectangle tr = new Rectangle(x, 1, w + 4, Height - Theme.Px(8));
        TextRenderer.DrawText(g, Tabs[i], f, tr, c,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (sel)
        {
            using (SolidBrush b = new SolidBrush(Theme.Accent))
            using (GraphicsPath p = Ui.Round(new Rectangle(x, Height - Theme.Px(5), w, Theme.Px(2)), Theme.Px(1)))
                g.FillPath(b, p);
        }
        f.Dispose();
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
    private int _bubbleH;      // 气泡实际高度（含操作行；_contentH 仍只算正文）

    // ---------- 气泡里的图片缩略图（2026-10-18 补回）----------
    public readonly List<string> Images = new List<string>();
    private readonly List<Size> _imgSizes = new List<Size>();
    private readonly List<Size> _thumbs = new List<Size>();
    private readonly List<Rectangle> _thumbRects = new List<Rectangle>();
    private readonly List<Bitmap> _thumbCache = new List<Bitmap>();
    private int _imgH, _imgTop;

    // 正文的左右边界与气泡矩形（用户气泡靠右、AI 气泡靠左，见 LayoutBox）；
    // 排版一律用 _left/_right，绘制再按 _bubX/_bubW 铺底 —— 只改绘制不改排版的话，
    // 拖选高亮和"点哪儿选哪儿"会跟画面错位。
    private int _left, _right, _bubX, _bubW;
    private int _laidLeft;         // 上次排版用的 _left（宽度变化判据要带上它，否则换宽度不重排）

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

    // ---------- 长内容折叠（⑤）：工具结果 / 轨迹页思考默认收起 ----------
    // 收起态只画一行摘要，排版**直接 return、不推进定稿点 _laidChars** ——
    // 否则流式期间每来一批就要把全量文字重排一遍，退回 O(n²)（硬约束）。
    public bool Foldable;          // 这条允许折叠（定稿后才置位，流式期间不折叠）
    public bool Collapsed;         // 是不是收起态（权威在 TranscriptView._foldedKeys，这里只是镜像）
    public string FoldSummary;     // 摘要行文案："已执行 run_shell · 12 行"
    public string FoldTool;        // 这条工具结果是哪个工具跑的（摘要里要报工具名）
    public int FoldKey = -1;       // 没有 MsgIdx 的块（实时流式）拿它当折叠状态的 key
    private Rectangle _foldRect;   // 摘要行 / 收起按钮的客户区矩形（和 CopyRect 一样是客户区坐标）
    private int _foldChars = -1;   // 摘要里的行数是按多少字符算的（文本长过就要重算）
    private bool _foldHot;         // 鼠标压在这行的折叠入口上
    internal int FoldToggles = 0;  // 自测用：这条被折叠/展开过几次

    internal Rectangle FoldRect { get { return _foldRect; } }

    internal bool ToggleFold()
    {
        if (!Foldable) return false;
        Collapsed = !Collapsed;
        FoldToggles++;
        ResetLayout();          // 定稿点作废（_laidChars/_laidPieces 只清不删）
        Rebuild();
        return true;
    }

    // 定稿后置位：默认收起（视图那边同时把 key 记进 _foldedKeys）
    internal void SetFold(bool foldable, string summary, bool collapsed)
    {
        Foldable = foldable;
        FoldSummary = summary;
        _foldChars = summary == null ? -1 : Raw.Length;
        Collapsed = foldable && collapsed;
        ResetLayout();
        Rebuild();
    }

    internal const int ActCopy = 0, ActBranch = 1;

    Rectangle CopyRect() { return new Rectangle(_left, _actY + Theme.Px(2), Theme.Px(17), Theme.Px(17)); }
    Rectangle BranchRect() { return new Rectangle(_left + Theme.Px(28), _actY + Theme.Px(2), Theme.Px(17), Theme.Px(17)); }
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
        _imgH = 0; _imgTop = 0; _thumbRects.Clear();
        _actH = 0; _actY = _contentH;
        _bubbleH = _contentH;
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

    // 真路径进 Images，正文只显示 [图片 文件名]（MainForm.Attach.Show.Text 负责替换）。
    // 坏路径 / 不存在的文件直接忽略：不能因为一张图把整条消息排崩。
    public void SetImages(List<string> paths)
    {
        Images.Clear();
        _imgSizes.Clear();
        DisposeThumbs();
        _thumbs.Clear();
        _thumbRects.Clear();
        if (paths != null)
        {
            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                try
                {
                    if (!File.Exists(p)) continue;
                    int w, h;
                    using (Image img = Image.FromFile(p)) { w = img.Width; h = img.Height; }
                    if (w <= 0 || h <= 0) continue;
                    Images.Add(p);
                    _imgSizes.Add(new Size(w, h));
                }
                catch { }
            }
        }
        ResetLayout();
        Rebuild();
    }

    // 缩略图按 220×160 逻辑像素等比缩（只缩不放），每行放不下就换行。
    void LayoutImages(int top)
    {
        _thumbs.Clear();
        _thumbRects.Clear();
        _imgTop = 0;
        _imgH = 0;
        if (Images.Count == 0) return;
        int maxW = Theme.Px(220), maxH = Theme.Px(160), gap = Theme.Px(Theme.Sp8);
        List<Size> sizes = new List<Size>();
        foreach (Size s in _imgSizes)
        {
            double k = Math.Min(1.0, Math.Min(maxW / (double)s.Width, maxH / (double)s.Height));
            int tw = Math.Max(1, (int)Math.Round(s.Width * k));
            int th = Math.Max(1, (int)Math.Round(s.Height * k));
            sizes.Add(new Size(tw, th));
        }
        int x = _left, y = top, rowH = 0;
        foreach (Size s in sizes)
        {
            if (x > _left && x + s.Width > _right)
            {
                x = _left;
                y += rowH + gap;
                rowH = 0;
            }
            _thumbs.Add(s);
            _thumbRects.Add(new Rectangle(x, y, s.Width, s.Height));
            x += s.Width + gap;
            if (s.Height > rowH) rowH = s.Height;
        }
        _imgTop = top;
        _imgH = (y + rowH) - top;
    }

    // 真加载缩略图（不是只算尺寸）；缓存到 Dispose 时逐个释放。
    internal Bitmap LoadThumb(int i)
    {
        if (i < 0 || i >= Images.Count || i >= _thumbs.Count) return null;
        while (_thumbCache.Count <= i) _thumbCache.Add(null);
        if (_thumbCache[i] != null) return _thumbCache[i];
        try
        {
            using (Image src = Image.FromFile(Images[i]))
            {
                Bitmap b = new Bitmap(_thumbs[i].Width, _thumbs[i].Height);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, 0, 0, b.Width, b.Height);
                }
                _thumbCache[i] = b;
                return b;
            }
        }
        catch { return null; }
    }

    void DrawImages(Graphics g)
    {
        for (int i = 0; i < _thumbRects.Count; i++)
        {
            Bitmap b = LoadThumb(i);
            if (b == null) continue;
            g.DrawImage(b, _thumbRects[i]);
        }
    }

    void DisposeThumbs()
    {
        foreach (Bitmap b in _thumbCache) if (b != null) b.Dispose();
        _thumbCache.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeThumbs();
        base.Dispose(disposing);
    }

    // ---------- 排版：把文本切成"行内片段"，算出每个片段的位置 ----------

    // 这条消息带不带气泡：只有用户消息和助手回复是"气泡"，思考/工具/提示仍是通栏小字
    bool Bubbled { get { return Kind == OutKind.User || Kind == OutKind.Assistant; } }

    // 气泡几何 + 正文左右边界。必须在排版之前调用；换了宽度/字号也要重算。
    void LayoutBox()
    {
        int pad = Theme.Px(Theme.PadBubbleX);
        int gap = Theme.Px(Theme.Sp4);
        if (!Bubbled)
        {
            // 非气泡消息保持原样：正文左边界就是原来的 _pads，右边界也照旧
            _bubX = 0; _bubW = Width;
            _left = _pads; _right = Math.Max(_pads + Theme.Px(40), Width - _pads);
            return;
        }
        if (Kind == OutKind.User)
        {
            // 靠右：左边留出 25%，气泡最宽 75%（窄块下保底一个内边距）
            _bubX = (int)((Width - gap) * (100 - Theme.BubbleMaxPct) / 100);
            if (_bubX < pad) _bubX = pad;
        }
        else
        {
            _bubX = gap;                     // 靠左：左右各留 4
        }
        _bubW = Math.Max(Theme.Px(48), Width - _bubX - gap);
        _left = _bubX + pad;
        _right = _bubX + _bubW - pad;
        if (_right - _left < Theme.Px(48)) _right = _left + Theme.Px(48);
    }

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
        // 气泡里面要留 PadBubbleY 的内边距；思考/工具/提示这些通栏小字维持原来的紧凑排版
        _laidX = _left;
        _laidY = (Bubbled ? Theme.Px(Theme.PadBubbleY) : Theme.Px(3)) + FoldHeaderH();
        _laidBlank = false; _laidMidLine = false; _laidWrap = 0;
        _laidInCode = false;
        _laidLeft = _left;
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
        LayoutBox();                                             // 气泡/左右边界先定下来，再谈排版
        if (Kind == OutKind.Changes) { BuildCard(); return; }     // 卡片有自己的一套排版
        // 收起态：只有一行摘要，**不碰定稿点**（_laidChars 不推进）。
        // 推进了就等于"收起时也把全文排了一遍"，流式期间每批一次 → O(n²)。
        if (Foldable && Collapsed) { LayoutCollapsed(); return; }
        string text = Raw.ToString();
        int wrapW = Math.Max(Theme.Px(80), _right - _left);
        int lineH = BaseFont().Height + Theme.Px(Theme.Sp4);      // 行高吸附 4px 栅格（原来 +3）

        if (text.Length < _laidChars || _laidWrap != wrapW || _laidLeft != _left) ResetLayout();
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
                if (midLine) { x = _left; y += lineH + Theme.Px(2); lastBlank = false; }
                else { if (y > 0) y += (int)(lineH * 0.55f); lastBlank = true; }
            }
            else
            {
                string trimmed = line.TrimStart();
                if (midLine && pos == 0)
                {
                    // 续排上一次定稿留下的半行：它是普通正文的一部分，不能再按行首去识别标题/列表/表格
                    LayLine(line, lineAbs, null, 0, null, null, lineH, wrapW, 0, ref x, ref y);
                    x = _left; y += lineH + Theme.Px(2);
                }
                else if (trimmed.StartsWith("```"))
                {
                    // 代码块围栏：本身不画字，只留间距（正文的圆角框由相邻 style==3 的片段聚合成一个）
                    if (inCode) { inCode = false; y += Theme.Px(Theme.Sp12); }
                    else { inCode = true; if (y > 0) y += Theme.Px(Theme.Sp12); }
                    lastBlank = false;
                    x = _left;
                }
                else if (inCode)
                {
                    // 代码块内容：等宽、浅底、保留原样
                    LayLine(line, lineAbs, _fontMono, 0, null, null, lineH, wrapW, 3, ref x, ref y);
                    x = _left; y += lineH + Theme.Px(2);
                }
                else if (Kind == OutKind.Assistant && ToolNoise.IsAnnounceLine(line))
                {
                    // 模型把"要调用工具"写成了正文：不显示（否则对话区会被十几条 [调用] 刷屏）
                    x = _left; y += Theme.Px(3);
                    lastBlank = false;
                }
                else
                {
                    int hPrefix;
                    int lvl = HeadingLevel(line, out hPrefix);
                    if (lvl > 0)
                    {
                        Font hf = HeadingFont(lvl);
                        int hh = hf.Height + Theme.Px(Theme.Sp4);
                        if (y > 0) y += Theme.Px(lvl == 1 ? 10 : 7);
                        string body = line.Substring(hPrefix).TrimStart();
                        LayLine(body, lineAbs + (line.Length - body.Length), hf, 0, null, null, hh, wrapW, 0, ref x, ref y);
                        x = _left; y += hh + Theme.Px(4);
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
                        x = _left; y += lineH + Theme.Px(2);
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
            _laidX = _left; _laidY = y;
            _laidBlank = lastBlank; _laidMidLine = false; _laidWrap = wrapW;
            _laidLeft = _left;
            _laidInCode = inCode;
        }

        // 气泡底边 = 正文底 + 下内边距；通栏小字（思考/工具/提示）保持原来的紧凑留白
        // 图片放在正文下面：文本排版完全不动，只在最后加一段图片区。
        if (Images.Count > 0)
        {
            LayoutImages(y + Theme.Px(Theme.Sp8));
            y = _imgTop + _imgH;
        }
        else LayoutImages(0);
        _contentH = Math.Max(1, y + (Bubbled ? Theme.Px(Theme.PadBubbleY) : Theme.Px(2)));
        // AI 气泡按正文实际右边界收窄；否则短回复会拉成整行白卡片，右边一大块空白（截图里红叉那块）。
        if (Kind == OutKind.Assistant && _bubW > Theme.Px(80))
        {
            int maxR = _left;
            foreach (Piece pc in _pieces) if (pc.Rect.Width > 0 && pc.Rect.Right > maxR) maxR = pc.Rect.Right;
            foreach (Rectangle tr in _thumbRects) if (tr.Right > maxR) maxR = tr.Right;
            int pad = _left - _bubX;
            int want = Math.Max(Theme.Px(160), maxR + pad - _bubX);
            if (want < _bubW) _bubW = want;
        }
        _actH = (ShowCopy || ShowBranch) ? Theme.Px(22) : 0;
        _actY = _contentH + Theme.Px(Theme.Sp8);                   // 操作行留 8px（轮内间距吸附栅格）
        _bubbleH = Bubbled ? Math.Max(_contentH, _actY + _actH + Theme.Px(Theme.Sp4)) : _contentH;
        Height = Math.Max(_bubbleH, _actY + _actH);
        _laidWrap = wrapW;
        _laidLeft = _left;
        // 展开态的摘要行（表头）：还是块顶那一格，和收起态位置完全一样 —— 正文从它下面开始
        if (Foldable)
            _foldRect = new Rectangle(_pads, 0, Math.Max(Theme.Px(40), Width - _pads * 2),
                FoldHeaderH() - Theme.Px(Theme.Sp4));
        Invalidate();
    }

    // ---------- 折叠的两种版面 ----------
    // 摘要行（表头）在**收起和展开时都占块顶同一格**：
    // 这样 _foldRect.Top 在两种状态下是同一个点，防跳位那套"记它切换前的位置"才有意义。

    // 可折叠的块顶上那一行摘要有多高（收起/展开一样高）
    int FoldHeaderH()
    {
        if (!Foldable) return 0;
        return BaseFont().Height + Theme.Px(Theme.Sp8) + Theme.Px(Theme.Sp4);
    }

    // 收起态：只有一行摘要 + 操作行。_laidChars/_laidPieces 只能由 ResetLayout 作废，这里不动它们。
    void LayoutCollapsed()
    {
        if (_pieces.Count > 0) ResetLayout();
        RefreshFoldSummary();
        int fh = FoldHeaderH();
        _contentH = fh;
        _imgH = 0; _imgTop = 0; _thumbRects.Clear();
        _foldRect = new Rectangle(_pads, 0, Math.Max(Theme.Px(40), Width - _pads * 2), fh - Theme.Px(Theme.Sp4));
        _actH = (ShowCopy || ShowBranch) ? Theme.Px(22) : 0;
        _actY = _contentH + Theme.Px(Theme.Sp4);
        _bubbleH = Bubbled ? Math.Max(_contentH, _actY + _actH + Theme.Px(Theme.Sp4)) : _contentH;
        Height = Math.Max(_bubbleH, _actY + _actH);
        Invalidate();
    }

    // 收起期间文本还会长（轨迹页的思考就是一条大块一路续）：
    // 只有"字符数变了"才重算行数，避免每次 Append 都扫一遍全文。
    void RefreshFoldSummary()
    {
        if (FoldSummary == null || Raw.Length == _foldChars) return;
        _foldChars = Raw.Length;
        string t = Raw.ToString().Replace("\r\n", "\n").TrimEnd('\n', '\r');
        string head = Kind == OutKind.Thinking ? "思考"
            : (string.IsNullOrEmpty(FoldTool) ? "已执行" : ("已执行 " + FoldTool));
        FoldSummary = head + " · " + Txt.LineCount(t) + " 行";
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

    // 表格单元格不做行内 Markdown 排版（整张表用等宽字体对齐），但至少把 **粗体** / `代码`
    // 的标记剥掉，别让模型输出的 "**状态码**" 原样带星号显示（2026-10-18 验收）。
    static string TableCellText(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
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
        int left = _left + hang;
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
            mp.Rect = new Rectangle(Math.Max(_left, _left + hang - mw - Theme.Px(6)), y, mw, lineH + Theme.Px(2));
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
            // 行内代码的底框比文字左右各宽 3px，文字前面也要让出一点，否则底框会压到前一个字上
            if (p.Style == 2 && x > left) x += Theme.Px(Theme.Sp4);
            // inPart 是"这一段内部"已经排掉的字符数：必须每段归零，
            // 否则第二段起的 SrcAt 会把前面所有段的长度重复加一遍（选中高亮就会错位、盖不住字）
            int inPart = 0;
            while (seg.Length > 0)
            {
                int room = wrapW - (x - _left);
                if (room < Theme.Px(24) && x > left)
                {
                    x = left;
                    y += lineH + Theme.Px(2);
                    room = wrapW - (x - _left);
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
            if (p.Style == 2) x += Theme.Px(Theme.Sp4);
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
                int w = Measure(TableCellText(rows[k][j]), fm);
                if (w > colW[j]) colW[j] = w;
            }
        }
        int pad = Theme.Px(18), total = 0;
        for (int j = 0; j < cols; j++) { colW[j] += pad; total += colW[j]; }
        if (total > wrapW) return -1;                       // 太宽：退回普通文本折行
        int[] colX = new int[cols];
        int cx = _left;
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
                sp.Rect = new Rectangle(_left, y + lineH / 2, total, Math.Max(1, Theme.Px(1)));
                _pieces.Add(sp);
            }
            else
            {
                for (int j = 0; j < rows[k].Count; j++)
                {
                    string cell = TableCellText(rows[k][j]);
                    if (cell.Length == 0) continue;
                    Piece cp = new Piece();
                    cp.Text = cell;
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
        x = _left;
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
#if NET10_0_OR_GREATER
        // .NET 10：不做"往前找标点"的二次切分。Fitting 只拿到当前 chunk，看不到后面的字，
        // 按标点回退会让同一段文字在"流式分片"和"全量重排"下切在不同位置，高度对不上。
        return Math.Max(1, lo);
#else
        // .NET Framework：保留旧的"尽量断在空格/标点后"视觉；旧版测量下回归全过。
        int cut = lo;
        for (int i = lo - 1; i > lo - 12 && i > 0; i--)
        {
            char c = s[i - 1];
            if (c == ' ' || c == '，' || c == '。' || c == ',' || c == '.' || c == '、' || c == '：' || c == ':') { cut = i; break; }
        }
        return Math.Max(1, cut);
#endif
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
        // 长内容折叠：点摘要行 / 「收起」小按钮 → 切换（同样不进入拖选）
        if (Foldable && _foldRect.Contains(e.Location))
        {
            TranscriptView tvf = Parent as TranscriptView;
            if (tvf != null) tvf.ChildFold(this);
            return;
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
        // 折叠入口也要给"手型"：否则用户不知道那一行能点
        bool foldHot = Foldable && _foldRect.Contains(pt);
        if (foldHot != _foldHot)
        {
            _foldHot = foldHot;
            Cursor = (foldHot || ActionAt(pt) >= 0) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
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
        if (HoverAct != -1 || Tip != null || _foldHot)
        {
            HoverAct = -1; Tip = null; TipAct = -1; Cursor = Cursors.Default;
            _foldHot = false;
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
        switch (Kind)
        {
            case OutKind.User: fg = Theme.Fg; break;
            case OutKind.Thinking: fg = Theme.FgDim; break;
            case OutKind.ToolCall: fg = Theme.FgMid; break;
            case OutKind.ToolResult: fg = Theme.FgFaint; break;
            case OutKind.Error: fg = Theme.Err; break;
            case OutKind.Info: fg = Theme.FgDim; break;
            case OutKind.Stats: fg = Theme.FgFaint; break;
        }
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        // 两套气泡：用户靠右、AI 靠左，填充 + 1px 描边 + 圆角 10（几何由 LayoutBox 算好）
        if (Bubbled)
        {
            Rectangle br = new Rectangle(_bubX, 0, Math.Max(Theme.Px(8), _bubW), Math.Max(Theme.Px(8), _bubbleH));
            using (GraphicsPath bp = Ui.Round(br, Theme.Px(Theme.RadCard)))
            {
                using (SolidBrush b = new SolidBrush(Kind == OutKind.User ? Theme.BubbleUserFill : Theme.BubbleAiFill))
                    g.FillPath(b, bp);
                // 1px 细线要配 PixelOffsetMode.Half，否则抗锯齿会把描边糊成 2px 灰边
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (Pen p = new Pen(Kind == OutKind.User ? Theme.BubbleUserLine : Theme.BubbleAiLine, Theme.Pxf(1f)))
                    g.DrawPath(p, bp);
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
        }
        if (Kind == OutKind.Changes) { DrawCard(g); return; }

        // 折叠的摘要行（表头）：收起时它下面是空的，展开时它下面是全文
        if (Foldable) DrawFoldRow(g);

        // 分三趟画：先底（代码块底/行内代码底/表格线）→ 再选中高亮 → 最后文字。
        // 高亮必须在"所有底色之上、文字之下"，否则合并后的高亮会被后面的代码底色盖住。
        DrawPieceBacks(g);
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
        DrawImages(g);

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
                TextRenderer.DrawText(g, ts, f2, new Point(Math.Max(_left, _right - tw), _actY + Theme.Px(4)),
                    Theme.FgFaint, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }

            // 深色小气泡：悬停时给按钮名，点完给"已复制"
            string bubble = Flash != null && Flash.Length > 0 ? Flash : Tip;
            int bAct = Flash != null && Flash.Length > 0 ? FlashAct : TipAct;
            if (bubble != null && bubble.Length > 0)
                DrawBubble(g, bubble, bAct == ActBranch ? BranchRect() : CopyRect());
        }
    }

    // 折叠的摘要行：一个浅底圆角条 + ▸/▾ + "已执行 run_shell · 12 行 · 点击展开"
    // 收起 / 展开都用它（位置也一样），所以点它就能来回切。
    void DrawFoldRow(Graphics g)
    {
        Font f = BaseFont();
        Color fg = Kind == OutKind.Thinking ? Theme.FgDim : Theme.FgFaint;
        using (GraphicsPath bp = Ui.Round(_foldRect, Theme.Px(Theme.RadRow)))
        {
            using (SolidBrush b = new SolidBrush(_foldHot ? Theme.Hover : Theme.Card)) g.FillPath(b, bp);
            using (Pen p = new Pen(Theme.Line, 1f)) g.DrawPath(p, bp);
        }
        Rectangle ic = new Rectangle(_foldRect.Left + Theme.Px(8),
            _foldRect.Top + (_foldRect.Height - Theme.Px(14)) / 2, Theme.Px(14), Theme.Px(14));
        Icons.Draw(g, Collapsed ? "chevronRight" : "chevronDown", ic, fg, 1.3f);
        string head = FoldSummary == null || FoldSummary.Length == 0 ? "长内容" : FoldSummary;
        // 高度就是"字体高 + Sp8"，文字正好居中；末尾那句提示让用户知道这行能点
        TextRenderer.DrawText(g, head + (Collapsed ? " · 点击展开" : " · 点击收起"), f,
            new Point(ic.Right + Theme.Px(6), _foldRect.Top + Theme.Px(Theme.Sp4)), fg,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
            | TextFormatFlags.EndEllipsis);
    }

    // 片段底色：代码块聚合成"一个"圆角描边矩形（以前逐行各画一个平矩形，
    // 行与行之间留 2px 白缝，看着像被切成一条条）；行内代码补 1px 描边。
    void DrawPieceBacks(Graphics g)
    {
        g.PixelOffsetMode = PixelOffsetMode.Half;
        int i = 0;
        while (i < _pieces.Count)
        {
            Piece p = _pieces[i];
            if (p.Style == 6)
            {
                using (SolidBrush lb = new SolidBrush(Theme.LineSoft)) g.FillRectangle(lb, p.Rect);
                i++;
                continue;
            }
            if (p.Style == 3)
            {
                // 连续 style==3 的片段 = 同一个代码块：取并集，只画一个框
                int top = p.Rect.Top, bot = p.Rect.Bottom, j = i;
                while (j + 1 < _pieces.Count && _pieces[j + 1].Style == 3)
                {
                    j++;
                    if (_pieces[j].Rect.Top < top) top = _pieces[j].Rect.Top;
                    if (_pieces[j].Rect.Bottom > bot) bot = _pieces[j].Rect.Bottom;
                }
                Rectangle box = new Rectangle(_left - Theme.Px(Theme.Sp8), top - Theme.Px(6),
                    Math.Max(Theme.Px(20), (_right - _left) + Theme.Px(Theme.Sp16)),
                    Math.Max(Theme.Px(12), bot - top + Theme.Px(12)));
                using (GraphicsPath bp = Ui.Round(box, Theme.Px(Theme.RadRow)))
                {
                    using (SolidBrush cb = new SolidBrush(Theme.CodeFill)) g.FillPath(cb, bp);
                    using (Pen cp = new Pen(Theme.CodeLine, Theme.Pxf(1f))) g.DrawPath(cp, bp);
                }
                i = j + 1;
                continue;
            }
            if (p.Style == 2)
            {
                Rectangle ip = new Rectangle(p.Rect.X - Theme.Px(3), p.Rect.Y + Theme.Px(1),
                    p.Rect.Width + Theme.Px(6), Math.Max(Theme.Px(4), p.Rect.Height - Theme.Px(2)));
                using (GraphicsPath cp = Ui.Round(ip, Theme.Px(Theme.RadMenu)))
                {
                    using (SolidBrush cb = new SolidBrush(Theme.InlineFill)) g.FillPath(cb, cp);
                    using (Pen pen = new Pen(Theme.InlineLine, Theme.Pxf(1f))) g.DrawPath(pen, cp);
                }
            }
            i++;
        }
        g.PixelOffsetMode = PixelOffsetMode.Default;
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
        AccessibleRole = AccessibleRole.ToolBar;
        AccessibleName = "选中文字操作";
        Visible = false;
    }

    int BtnW(int i)
    {
        return Ui.TextWidth(Labels[i], Theme.Cache(Theme.FontName, 9f, FontStyle.Regular)) + Theme.Px(20);
    }

    // 整条小操作条的高度：文字高度 + 上下各 6（以前写死 32，150% 下文字贴着上下边，看着"框太大/不贴合"）
    public static int PreferredH()
    {
        return Theme.Cache(Theme.FontName, 9f, FontStyle.Regular).Height + Theme.Px(12);
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
        Font f = Theme.Cache(Theme.FontName, 9f, FontStyle.Regular);
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
    // ---------- 长内容折叠（⑤）----------
    // 折叠状态只放内存：key = MsgIdx（实时流式的块没有 MsgIdx，就用块自己的 FoldKey），
    // **不写进 Session.messages** —— 那是原始 OpenAI 消息，加 UI 位会被 ConvertMessages 带进请求。
    private readonly HashSet<int> _foldedKeys = new HashSet<int>();
    private int _foldSeq = -2;          // 实时块的折叠 key（负数，永远不和 MsgIdx 撞）
    public bool IsTrace;                // 轨迹页：思考默认只在这里收起（对话区归 hide_thinking 管）
    private bool _autoFollow = true;    // 配置项 follow_output 的镜像（主窗体同步过来）
    private bool _inRelayout;           // Relayout / 程序化改位置期间：防自反馈，也防把"框架自己挪位置"误判成用户滚动
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
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "对话内容";
        BuildMenu();
        _bar = new SelBar();
        _bar.OnAct = delegate(int i) { if (OnBarAct != null) OnBarAct(i); };
        Controls.Add(_bar);
    }

    // 让小操作条跟着选区走（拖动中不显示，松手才浮出来——和 Codex 一样）
    //
    // 定位规则（2026-10-16 重写）：以**选区几何**为准，不用任何魔数。
    //   水平：贴选区首行左端 → 右边放不下就翻到首行右端 → 最后夹进可视区。
    //   竖直：优先浮在首行**上方** → 上方放不下就贴**整个选区下方** → 再不够就夹进可视区。
    // 以前是"上翻失败就 y = minY + 26"（26 与条高 bh 无关，150% DPI 下 bh≈41），
    // 于是小条压住选区第 2/3 行文字，而且 DPI 越大压得越多。
    void UpdateBar()
    {
        if (_bar == null) return;
        if (_dragging || !HasSelection)
        {
            if (_bar.Visible) _bar.Visible = false;
            return;
        }
        int pad = Theme.Px(4);
        int gap = Theme.Px(6);
        int vw = ClientSize.Width, vh = ClientSize.Height;
        int selTop = int.MaxValue, selBottom = int.MinValue;
        int rowTop = int.MaxValue, rowL = 0, rowR = 0;
        foreach (MsgBlock b in _blocks)
        {
            if (!b.HasSel) continue;
            List<Rectangle> hl = b.HighlightRects();
            if (hl.Count == 0) continue;
            foreach (Rectangle r in hl)
            {
                int left = r.Left + b.Left, top = r.Top + b.Top;
                int right = r.Right + b.Left, bottom = r.Bottom + b.Top;
                if (top < selTop) selTop = top;
                if (bottom > selBottom) selBottom = bottom;
                if (top < rowTop) { rowTop = top; rowL = left; rowR = right; }
                else if (top == rowTop)
                {
                    if (left < rowL) rowL = left;
                    if (right > rowR) rowR = right;
                }
            }
        }
        if (selBottom == int.MinValue) { if (_bar.Visible) _bar.Visible = false; return; }
        int w = SelBar.PreferredW();
        int bh = SelBar.PreferredH();
        int x = rowL;
        if (x + w > vw - pad) x = rowR - w;
        if (x < pad) x = pad;
        if (x + w > vw - pad) x = Math.Max(pad, vw - pad - w);
        int y = rowTop - bh - gap;
        if (y < pad) y = selBottom + gap;
        if (y + bh > vh - pad) y = vh - pad - bh;
        if (y < pad) y = pad;
        _bar.SetBounds(x, y, w, bh);
        _bar.Visible = selBottom > 0 && selTop < vh;
        // SetChildIndex 会触发一次布局，没必要每次滚动都做
        if (Controls.GetChildIndex(_bar) != 0) _bar.BringToFront();
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
        else if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown
            || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
        {
            // 键盘翻页也是"用户意图"：现在只是记一笔，实际滚动交给框架，随后由 OnScroll 落到跟随状态上
            NoteUserScroll();
            UpdateBar();
        }
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
        MenuRound.Apply(_menu);
        _menu.Renderer = new MenuSkin();
        _menu.Font = Theme.F(9.5f);
        _menu.ShowImageMargin = true;
        // 试过自己切圆角：WinForms 的下拉会强制自己的方角 Region，改不动。
        // 所以保留系统阴影（至少不像一块平板），里面用白底 + 圆角悬停 + 图标 + 快捷键做得干净些。
        _menu.DropShadowEnabled = true;
        _menu.Padding = new Padding(Theme.Px(Theme.Sp4));
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
        // 长内容折叠：批量展开 / 收起（只作用在右键点开的这一页）
        _menu.Items.Add(new ToolStripSeparator());
        it = (ToolStripMenuItem)_menu.Items.Add("全部展开长内容", MenuIcon("chevronRight"), delegate(object s2, EventArgs e2) { ExpandAll(); });
        it = (ToolStripMenuItem)_menu.Items.Add("全部收起长内容", MenuIcon("chevronDown"), delegate(object s2, EventArgs e2) { CollapseAll(); });
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
        // 折叠状态跟着块一起清掉：重开会话回默认收起，不会拿旧 MsgIdx 的折叠位套到新块上
        _foldedKeys.Clear();
        _foldSeq = -2;
        _follow = true;                         // 清空 = 回到"跟着最新"的状态
        AutoScrollMinSize = new Size(0, 0);
        AutoScrollPosition = new Point(0, 0);   // 清空就得回到顶部，否则下一轮排版会带着旧偏移
        if (_bar != null) _bar.Visible = false;
        Invalidate();
    }

    public MsgBlock LastBlock { get { return _last; } }

    // 历史渲染专用的"下一条块的标记"：这条消息属于哪一步（分支切点）＋它的时间。
    // 以前是"写完立刻抓 LastBlock"，所以每条后面都得 FlushNow() —— 891 条就是 891 次
    // 全量重排（每次还要把最多 400 个块重新摆一遍），实测打开那条会话 >95 秒，窗口直接"(未响应)"。
    // 改成先把标记存着、等块真建出来时贴上，这样渲染历史就能攒批刷新。
    public int PendingIdx = -1;
    public DateTime? PendingStamp;
    // 侧信道：历史/实时渲染先把真路径放这里，下一条 User 块建出来时取走并清空。
    public List<string> PendingImages;

    // ---------- 攒批：120ms 一刷。不然每个 token 都要把这一条消息重新排版一次 ----------
    class Pend
    {
        public OutKind Kind;
        public StringBuilder Text;
        public bool BreakOnly;
        public string Tool;      // 工具结果专属：跑它的工具名（折叠摘要行要报）
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

    // 工具结果要带"是谁跑的"：名字跟着这一批文字走，建块时才贴上
    // （不能用视图上的一个共享槽位 —— 一批里可能有好几条工具结果，名字会串）
    public void AddToolChunk(string text, string tool)
    {
        if (string.IsNullOrEmpty(text)) return;
        Pend p = new Pend();
        p.Kind = OutKind.ToolResult;
        p.Text = new StringBuilder(text);
        p.Tool = tool;
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
            {
                m.Text.Append(p.Text);
                if (p.Tool != null) m.Tool = p.Tool;      // 合并后仍然记得是谁跑的
            }
            else
                merged.Add(p);
        }
        // 只有"这一批写进来之前用户就贴在底部"才自动跟随；
        // 用户在中间看历史时（_follow 为 false 或已经滚离底部）绝不拽走他的视口。
        bool follow = _follow && _autoFollow && NearBottom();
        bool touched = false;
        foreach (Pend p in merged)
        {
            if (p.BreakOnly) { _last = null; _hasLast = false; continue; }
            WriteNow(p.Kind, p.Text.ToString(), p.Tool);
            touched = true;
        }
        if (!touched) return;
        ApplyFoldDefaults();     // 定稿后再谈默认收起（流式期间不折叠，见下）
        Relayout();
        if (follow) ScrollToEnd();
    }

    // ---------- 默认折叠策略 ----------
    // 工具结果：行 ≥ 8 或 字 ≥ 600 → 默认收起；轨迹页思考：行 ≥ 6 → 默认收起；
    // 助手长回答里**以长代码块为主**的（代码 ≥ 20 行且占正文 60% 以上）也整条收起 —— 用户的验收原话里
    // "所有长文本、代码块、命令执行结果"都要能折，但纯讲解类回答不该被折掉正文，所以加了占比判据。
    // 已经 Foldable 的不再动 —— 用户手点过就得尊重。
    void ApplyFoldDefaults()
    {
        foreach (MsgBlock b in _blocks) ApplyFoldDefault(b);
    }

    void ApplyFoldDefault(MsgBlock b)
    {
        if (b == null || b.Foldable) return;
        if (b.Kind == OutKind.ToolResult)
        {
            string t = b.Body.Replace("\r\n", "\n").TrimEnd('\n', '\r');
            if (t.Length == 0) return;
            int lines = Txt.LineCount(t);
            if (lines < 8 && t.Length < 600) return;
            MarkFolded(b, (string.IsNullOrEmpty(b.FoldTool) ? "已执行" : ("已执行 " + b.FoldTool))
                + " · " + lines + " 行");
            return;
        }
        // 对话区的思考内容不在这里折叠：显不显示归 config.hide_thinking 那个开关（那套逻辑不动）
        if (b.Kind == OutKind.Thinking && IsTrace)
        {
            string t = b.Body.Replace("\r\n", "\n").TrimEnd('\n', '\r');
            int lines = Txt.LineCount(t);
            if (lines < 6) return;
            MarkFolded(b, "思考 · " + lines + " 行");
            return;
        }
        if (b.Kind == OutKind.Assistant)
        {
            int codeLines, totalLines;
            CodeShare(b.Body, out codeLines, out totalLines);
            if (codeLines >= 20 && codeLines * 10 >= totalLines * 6)
                MarkFolded(b, "代码 · " + codeLines + " 行");
        }
    }

    // 统计正文里围栏代码块的行数与正文总行数（判"长代码块为主的回答才折叠"）
    static void CodeShare(string body, out int codeLines, out int totalLines)
    {
        codeLines = 0; totalLines = 0;
        if (string.IsNullOrEmpty(body)) return;
        string[] lines = body.Replace("\r\n", "\n").Split('\n');
        bool inCode = false;
        int cur = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].TrimStart();
            bool fence = t.StartsWith("```") || t.StartsWith("~~~");
            if (fence)
            {
                if (inCode) { codeLines += cur; cur = 0; inCode = false; }
                else inCode = true;
                continue;
            }
            if (inCode) cur++;
            else if (lines[i].Trim().Length > 0) totalLines++;
        }
        if (inCode) codeLines += cur;        // 没闭合的围栏也算（模型常忘了收尾）
        totalLines += codeLines;
    }

    // 把一块置成"可折叠 + 默认收起"，并把状态记进内存（key = MsgIdx / FoldKey）
    void MarkFolded(MsgBlock b, string summary)
    {
        _foldedKeys.Add(FoldKeyOf(b));
        b.SetFold(true, summary, true);
    }

    internal int FoldKeyOf(MsgBlock b)
    {
        return b.MsgIdx >= 0 ? b.MsgIdx : b.FoldKey;
    }

    // 块的折叠入口（MsgBlock 点摘要行时调进来）
    internal void ChildFold(MsgBlock b) { ToggleFold(b); }

    // 单块展开 / 收起。
    // 防跳位：切换前记下摘要行的视口坐标，重排后**只在它已经滚出视口上方**时补偿滚动位置；
    // 块顶还看得见就一动不动。展开块顶出视口底部时也不许 ScrollToEnd（别跟智能跟随打架）。
    internal bool ToggleFold(MsgBlock b)
    {
        if (b == null || !b.Foldable) return false;
        int top0 = b.Top + b.FoldRect.Top;
        if (!b.ToggleFold()) return false;
        int key = FoldKeyOf(b);
        if (b.Collapsed) _foldedKeys.Add(key); else _foldedKeys.Remove(key);
        Relayout();                    // 重算可滚范围（不然就是"幽灵高度"）
        if (top0 < 0)
        {
            int top1 = b.Top + b.FoldRect.Top;
            int cur = -AutoScrollPosition.Y;
            int want = cur + (top1 - top0);
            if (want < 0) want = 0;
            int max = MaxScroll();
            if (want > max) want = max;
            if (want != cur)
            {
                _inRelayout = true;
                try { AutoScrollPosition = new Point(0, want); } catch { }
                finally { _inRelayout = false; }
            }
        }
        return true;
    }

    // 批量：全部展开 / 全部收起（遍历所有块，最后一次 Relayout）
    public void ExpandAll() { SetAllFolds(false); }
    public void CollapseAll() { SetAllFolds(true); }

    void SetAllFolds(bool collapsed)
    {
        bool any = false;
        foreach (MsgBlock b in _blocks)
        {
            if (!b.Foldable || b.Collapsed == collapsed) continue;
            b.ToggleFold();
            int key = FoldKeyOf(b);
            if (collapsed) _foldedKeys.Add(key); else _foldedKeys.Remove(key);
            any = true;
        }
        if (any) Relayout();
    }

    // 自测用：还有几块是可折叠的 / 其中几块是收起的
    public int FoldableCount()
    {
        int n = 0;
        foreach (MsgBlock b in _blocks) if (b.Foldable) n++;
        return n;
    }

    public int CollapsedCount()
    {
        int n = 0;
        foreach (MsgBlock b in _blocks) if (b.Foldable && b.Collapsed) n++;
        return n;
    }

    // 追加一段文字：同一种 kind 就接到最后一块上
    void WriteNow(OutKind kind, string text, string tool)
    {
        if (!_hasLast || _lastKind != kind)
        {
            // 新起一条消息：开头的空行没必要（块与块之间本来就有间距）
            text = text.TrimStart('\n', '\r');
            if (text.Length == 0) return;
            _last = new MsgBlock(kind, _norm, _bold, _mono, _small);
            _last.Width = BodyWidth;
            _last.FoldKey = _foldSeq--;      // 实时块（没有 MsgIdx）的折叠状态 key
            // 工具结果是谁跑的：摘要行要报工具名（名字跟着这一批文字一起来，见 Pend.Tool）
            if (tool != null) _last.FoldTool = tool;
            // 历史渲染会先给 PendingStamp/PendingIdx（攒批时块还没建出来，抓不到 LastBlock）；
            // 实时流式没有 pending，就用"现在"。
            _last.Stamp = PendingStamp.HasValue ? PendingStamp : (DateTime?)DateTime.Now;
            if (PendingIdx >= 0) _last.MsgIdx = PendingIdx;
            PendingIdx = -1;
            PendingStamp = null;
            _last.ShowCopy = (kind == OutKind.Assistant || kind == OutKind.User);
            _last.ShowBranch = (kind == OutKind.Assistant);
            if (kind == OutKind.User && PendingImages != null)
            {
                _last.SetImages(PendingImages);
                PendingImages = null;      // 取走即清空，绝不串到下一块
            }
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

    // 内容的真实总高（唯一权威）：所有块高 + 段间距 + 底部留白。
    // 以前这个值只在 Relayout 末尾算一次，却有三种语义的读点（BodyWidth / ScrollToEnd / AtEnd），
    // 三者看到的"范围"互不一致 —— 底部空白和回弹就是这么来的。
    int ContentHeight()
    {
        int y = 0;
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (i > 0) y += GapBefore(_blocks[i]);
            y += _blocks[i].Height;
        }
        return y + Theme.Px(8);
    }

    int MaxScroll()
    {
        int m = ContentHeight() - ClientSize.Height;
        return m < 0 ? 0 : m;
    }

    // 把可滚范围刷成 content。WinForms 的 AutoScrollMinSize setter 在"值没变"时是 no-op，
    // 于是滚动范围会停在上一帧算出来的旧值（实测能差 12~86px，正是"滚到底留空白/幽灵高度"的来源）。
    // 先写一个不同的值逼它重算，再写目标值。
    void ApplyScrollRange(int content)
    {
        Size want = new Size(0, content);
        // 宽度也要"逼"一次：WinForms 对同值 AutoScrollMinSize 是 no-op，窗口从宽拖窄时
        // 横向滚动条会残留下来（内容其实不宽），2026-10-18 验收在 920 逻辑宽下复现。
        if (AutoScrollMinSize.Width == 0) AutoScrollMinSize = new Size(1, content + 1);
        else if (AutoScrollMinSize == want) AutoScrollMinSize = new Size(0, content + 1);
        AutoScrollMinSize = want;
    }

    bool StickToBottom { get { return _follow && _autoFollow; } }

    // "贴底"判定，容差 2 逻辑像素。原来这里写死 28 —— 等于离底还剩 28px 就认为"到底了"，
    // 跟随状态下视口就永远停在那 28px 上，看上去就是"滚到底还留一片空白"。
    bool NearBottom()
    {
        int m = MaxScroll();
        if (m <= Theme.Px(2)) return true;
        int pos = -AutoScrollPosition.Y;
        if (pos >= m - Theme.Px(2)) return true;
        // 框架自己那套可滚范围（DisplayRectangle）会晚一帧才生效，可能比我们算的小几像素：
        // 只要位置已经顶到框架给的上限，也算"到底"，否则滚动条永远判不成贴底、跟随回不来。
        try
        {
            int real = VerticalScroll.Maximum - VerticalScroll.LargeChange;
            if (real > 0 && pos >= real - Theme.Px(2)) return true;
        }
        catch { }
        return false;
    }

    // 用户主动滚动 → 停止自动跟随。滚轮、拖动滚动条、PageUp/PageDown 都要走到这里。
    void NoteUserScroll()
    {
        _follow = false;
    }

    void Relayout()
    {
        // 防自反馈：Relayout 里会改 AutoScrollMinSize / AutoScrollPosition，框架会回抛 OnScroll，
        // 那种"滚动"不是用户意图，不能拿来改跟随状态。
        if (_inRelayout) return;
        _inRelayout = true;
        // 搬运期间挂起布局：否则框架每改一次子控件位置就按"中间态"重算一次范围并钳位置，
        // 中间态范围偏小 → 位置被钳到 0 → 表现就是"新消息一来滚动条跳回顶端"。
        SuspendLayout();
        try
        {
            int w = BodyWidth;
            // 先只量宽、不摆位：这一步会触发 MsgBlock.OnResize → Rebuild → 改 Height，
            // 所以必须等所有块的高度都稳定下来，再去算内容高与位置。
            foreach (MsgBlock b in _blocks) if (b.Width != w) b.Width = w;

            bool follow = StickToBottom;
            int content = ContentHeight();
            // 两趟收敛：MsgBlock 的高度可能在"改宽度"或"摆位置"之后还会再变一次，
            // 一趟算出来的内容高与真正摆出来的高度会差几个像素（幽灵高度就是这么来的）。
            for (int pass = 0; pass < 2; pass++)
            {
                // 先定范围、再定位置；这两步之后位置可能被框架夹住
                ApplyScrollRange(content);
                if (follow)
                {
                    int max = content - ClientSize.Height; if (max < 0) max = 0;
                    try { AutoScrollPosition = new Point(0, max); } catch { }
                }
                // 之后一律以**读回的**偏移为准来摆子控件。
                // 教训（2026-10-16）：每设一次 AutoScrollPosition，框架就会把所有子控件整体平移一次；
                // 摆完再去"复核位置"就等于把子控件二次平移，坐标与内容高错位 —— 底部空白就是这么来的。
                int off = AutoScrollPosition.Y;
                int y = 0;
                bool first = true;
                foreach (MsgBlock b in _blocks)
                {
                    b.Left = 0;
                    if (!first) y += GapBefore(b);
                    first = false;
                    b.Top = y + off;
                    y += b.Height;
                }
                int after = ContentHeight();
                if (after == content) break;
                content = after;
            }
        }
        finally
        {
            ResumeLayout(false);
            _inRelayout = false;
        }
        // 字号/宽度变了但选区还在：小条必须跟着重算，否则停在旧坐标（缩放字号后最明显）
        if (_bar != null && _bar.Visible)
        {
            _inRelayout = true;
            try { UpdateBar(); } finally { _inRelayout = false; }
        }
    }

    // 块前面留多少空隙。同一轮任务内部紧凑（6px），
    // 轮到"用户又提了新问题"就直接拉开一大截（24px，吸附 4px 栅格）—— 这样一眼就能看出哪几段是同一轮干的活。
    // 同轮内部 8px（栅格值）：原来是 6px，不在 4/8 栅格上。
    // 注意 flicker §27 那条断言也跟着钉到 Theme.Px(Theme.Sp8) —— 这是"有意改设计值"，不是放宽断言：
    // 它守的语义仍是"同轮内部保持紧凑"，只是把值从 6 钉到栅格上（改前 6px 是该节里唯一脱栅格的数）。
    int GapBefore(MsgBlock b)
    {
        return b.Kind == OutKind.User ? Theme.Px(Theme.Sp24) : Theme.Px(Theme.Sp8);
    }

    public void ScrollToEnd()
    {
        // 先用权威值刷范围，再滚。以前目标写的是"刚赋值、这一帧还没生效"的 AutoScrollMinSize.Height ——
        // setter 超范围会被静默钳到旧上限，于是"滚到底还差一截"，那截就是底部空白。
        ApplyScrollRange(ContentHeight());
        _follow = true;
        int max = MaxScroll();        try { AutoScrollPosition = new Point(0, max); } catch { }
        // 不足一屏时 max=0，这里会明确回到顶部（原来是提前 return，页面停在中间留一片空白）；
        // 被框架钳住则下一帧再补一次（只补一次，不自我循环）。
        if (-AutoScrollPosition.Y < max - Theme.Px(2) && IsHandleCreated)
        {
            BeginInvoke(new MethodInvoker(delegate
            {
                try { AutoScrollPosition = new Point(0, MaxScroll()); } catch { }
            }));
        }
    }

    // 视野是不是已经在最底下（统一走 NearBottom，容差 2px）
    public bool AtEnd { get { return NearBottom(); } }

    // 拖动滚动条 / PageUp / PageDown / Home / End 都会走这里。
    // 以前只有滚轮记"用户意图"，拖滚动条时 _follow 还是 true —— 下一批内容就把视口拽回底部，
    // 用户看到的就是"手动滑下去又被弹回原来的位置"。
    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        if (se.ScrollOrientation == ScrollOrientation.VerticalScroll && !_inRelayout)
        {
            if (NearBottom()) { _follow = true; }
            else NoteUserScroll();
            UpdateBar();
        }
    }

    // 滚轮：自己滚（不依赖系统往父窗口冒泡），并且"滚上去就停止自动跟随，滚回来再跟上"
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!AutoScroll) { base.OnMouseWheel(e); return; }
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines < 1) lines = 3;
        int step = Theme.Px(18) * lines;
        int max = MaxScroll();
        int target = (-AutoScrollPosition.Y) - (e.Delta * step / 120);
        if (target < 0) target = 0;
        if (target > max) target = max;
        _inRelayout = true;      // 这次是程序化改位置，别让 OnScroll 把自己的动作算成"用户滚动"
        try { AutoScrollPosition = new Point(0, target); }
        finally { _inRelayout = false; }
        if (NearBottom()) { _follow = true; }
        else NoteUserScroll();
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
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
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

// 自绘只读文本：旧栈不再用 RichTextBox；支持按行换色/底色、滚轮、Text 接口。
class UiReadOnlyText : Panel
{
    class Line { public string Text; public Color Fg, Bg; public bool Bold; }
    readonly List<Line> _lines = new List<Line>();
    int _selStart;

    public new BorderStyle BorderStyle { get; set; }
    public bool ReadOnly { get; set; }
    public bool Multiline { get; set; }
    public ScrollBars ScrollBars { get; set; }
    public bool WordWrap { get; set; }
    public int SelectionStart
    {
        get { return _selStart; }
        set { _selStart = Math.Max(0, value); }
    }
    public int TextLength
    {
        get
        {
            int n = 0;
            foreach (Line l in _lines) n += l.Text.Length + 1;
            return n;
        }
    }
    public override string Text
    {
        get
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (Line l in _lines) sb.Append(l.Text).Append("\n");
            return sb.ToString();
        }
        set
        {
            Reset();
            if (value == null) return;
            string[] ls = value.Replace("\r\n", "\n").Split('\n');
            foreach (string s in ls) AppendStyledLine(s, Theme.Fg, false, Theme.Card);
        }
    }

    public UiReadOnlyText()
    {
        AutoScroll = true;
        BackColor = Theme.Card;
        ForeColor = Theme.Fg;
        Font = Theme.M(9.5f);
        BorderStyle = BorderStyle.None;
        ReadOnly = true;
        Multiline = true;
        ScrollBars = ScrollBars.None;
    }

    public void Reset()
    {
        SuspendLayout();
        Controls.Clear();
        _lines.Clear();
        _selStart = 0;
        AutoScrollPosition = new Point(0, 0);
        ResumeLayout();
        Invalidate();
    }

    public void AppendStyledLine(string s, Color fg, bool bold, Color bg)
    {
        Line l = new Line();
        l.Text = s ?? "";
        l.Fg = fg;
        l.Bg = bg;
        l.Bold = bold;
        _lines.Add(l);

        Label lb = new Label();
        lb.AutoSize = false;
        lb.AutoEllipsis = true;
        lb.UseMnemonic = false;
        lb.Text = l.Text;
        lb.ForeColor = l.Fg;
        lb.BackColor = l.Bg;
        lb.Font = l.Bold ? Theme.MB(9.8f) : Font;
        lb.Height = Font.Height + Theme.Px(2);
        lb.Width = Math.Max(20, ClientSize.Width - (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
        lb.Left = 0;
        lb.Top = _lines.Count == 1 ? 0 : (Controls.Count == 0 ? 0 : Controls[Controls.Count - 1].Bottom);
        Controls.Add(lb);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int w = Math.Max(20, ClientSize.Width - (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
        foreach (Control c in Controls) c.Width = w;
    }

    public void ScrollToCaret()
    {
        AutoScrollPosition = new Point(0, Math.Max(0, DisplayRectangle.Height));
    }

    public void ScrollTop() { AutoScrollPosition = new Point(0, 0); }

    public Color ForeAt(int pos)
    {
        int n = 0;
        foreach (Line l in _lines)
        {
            if (pos <= n + l.Text.Length) return l.Fg;
            n += l.Text.Length + 1;
        }
        return Theme.Fg;
    }

    public Color BackAt(int pos)
    {
        int n = 0;
        foreach (Line l in _lines)
        {
            if (pos <= n + l.Text.Length) return l.Bg;
            n += l.Text.Length + 1;
        }
        return Theme.Card;
    }
}
// 「查看变更」的代码视图：.NET 10 用 Scintilla；旧栈用自绘只读文本，不再用 RichTextBox。
#if NET10_0_OR_GREATER
class UiDiff : ScintillaNET.Scintilla
{
    public new BorderStyle BorderStyle { get; set; }

    public UiDiff()
    {
        base.BorderStyle = ScintillaNET.BorderStyle.None;
        ReadOnly = true;
        WrapMode = ScintillaNET.WrapMode.Word;
        BackColor = Theme.Card;
        ForeColor = Theme.Fg;
        StyleResetDefault();
        Styles[ScintillaNET.Style.Default].Font = "Consolas";
        Styles[ScintillaNET.Style.Default].Size = 10;
        Styles[ScintillaNET.Style.Default].ForeColor = Theme.Fg;
        Styles[ScintillaNET.Style.Default].BackColor = Theme.Card;
        Styles[1].Font = "Consolas"; Styles[1].Size = 10;
        Styles[1].ForeColor = Theme.Ok; Styles[1].BackColor = Theme.OkSoft;
        Styles[2].Font = "Consolas"; Styles[2].Size = 10;
        Styles[2].ForeColor = Theme.Err; Styles[2].BackColor = Theme.ErrSoft;
        Styles[3].Font = "Consolas"; Styles[3].Size = 10; Styles[3].Bold = true;
        Styles[3].ForeColor = Theme.Fg; Styles[3].BackColor = Theme.LineSoft;
        StyleClearAll();
    }

    public void Reset()
    {
        ReadOnly = false;
        ClearAll();
        ReadOnly = true;
    }

    public void AppendStyledLine(string s, Color fg, bool bold, Color bg)
    {
        int start = TextLength;
        AppendText((s ?? "") + "\n");
        int st = 0;
        if (bg == Theme.OkSoft) st = 1;
        else if (bg == Theme.ErrSoft) st = 2;
        else if (bold) st = 3;
        int len = TextLength - start;
        if (len > 0)
        {
            StartStyling(start);
            SetStyling(len, st);
        }
    }

    public bool Multiline { get; set; }
    public ScrollBars ScrollBars { get; set; }
    public bool WordWrap
    {
        get { return WrapMode != ScintillaNET.WrapMode.None; }
        set { WrapMode = value ? ScintillaNET.WrapMode.Word : ScintillaNET.WrapMode.None; }
    }
    public void ScrollToCaret() { ScrollCaret(); }

    public void ScrollTop()
    {
        SelectionStart = 0;
        SelectionEnd = 0;
        ScrollCaret();
    }

    public Color ForeAt(int pos)
    {
        try { return Styles[GetStyleAt(pos)].ForeColor; } catch { return Theme.Fg; }
    }

    public Color BackAt(int pos)
    {
        try { return Styles[GetStyleAt(pos)].BackColor; } catch { return Theme.Card; }
    }
}
#else
class UiDiff : UiReadOnlyText
{
}
#endif

// 「查看变更」窗口：左边选文件，右边看带颜色的 diff
class ChangesForm : Form
{
    private ListBox _list;
    private UiDiff _diff;
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
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(0, Theme.Px(40), 0, 0);

        Panel head = new Panel();
        head.Dock = DockStyle.Top;
        head.Height = Theme.Px(46);
        head.BackColor = Theme.Bg;
        Label t = new Label();
        t.Text = "这一轮改了 " + files.Count + " 个文件";
        t.Font = Theme.FB(10.5f);
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
        close.Radius = Theme.RadRow;
        close.Font = Theme.F(9.5f);
        close.Size = new Size(Theme.Px(88), Theme.Px(34));
        close.Click += delegate { Close(); };
        bottom.Controls.Add(close);
        UiButton copy = new UiButton();
        copy.Text = "复制这个 diff";
        copy.FillColor = Color.White;
        copy.BorderColor = Theme.Line;
        copy.HoverColor = Theme.Hover;
        copy.Radius = Theme.RadRow;
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

        _list = new UiList();
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Theme.Card;
        _list.ForeColor = Theme.Fg;
        _list.Font = Theme.F(9.5f);
        _list.IntegralHeight = false;
        _list.SelectedIndexChanged += delegate { ShowDiff(_list.SelectedIndex); };
        body.Controls.Add(_list);

        _diff = new UiDiff();
        body.Controls.Add(_diff);
        body.Resize += delegate { LayoutBody(); };

        for (int i = 0; i < files.Count; i++)
        {
            Changes.EnsureDiff(files[i]);
            _list.Items.Add(files[i].Rel + "   +" + files[i].Added + " −" + files[i].Removed);
        }
        _list.SelectedIndex = (start >= 0 && start < files.Count) ? start : 0;
        LayoutBody();
        ChromeBar bar = new ChromeBar(Text);
        bar.Bounds = new Rectangle(0, 0, ClientSize.Width, Theme.Px(40));
        bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        bar.Closed += delegate { Close(); };
        Controls.Add(bar);
        bar.BringToFront();
        Height += Theme.Px(40);
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
        _diff.Reset();
        AppendLine(c.Rel + "   +" + c.Added + " −" + c.Removed + "   （下面 + 绿 = 新增，− 红 = 删除）",
            Theme.Fg, true, Theme.LineSoft);
        AppendLine("", Theme.Fg, false, Theme.Card);
        string[] lines = c.DiffText.Replace("\r\n", "\n").Split('\n');
        for (int k = 0; k < lines.Length; k++)
        {
            string ln = lines[k];
            Color col = Theme.FgMid, bg = Theme.Card;
            if (ln.StartsWith("+ ")) { col = Theme.Ok; bg = Theme.OkSoft; }
            else if (ln.StartsWith("- ")) { col = Theme.Err; bg = Theme.ErrSoft; }
            AppendLine(ln, col, false, bg);
        }
        _diff.ScrollTop();
    }

    void AppendLine(string s, Color c, bool bold, Color bg)
    {
        _diff.AppendStyledLine(s, c, bold, bg);
    }
}

// 输入条：直接用 Scintilla（双栈），不再保留 HintTextBox 回退。
// 外观/事件保持原样：Hint/Multiline/ScrollBars/Text/TextLength/SelectionStart/Clear/Focus。
class UiEditor : ScintillaNET.Scintilla
{
    Label _hint;
    string _hintText = "";
    bool _multi;
    public bool Multiline
    {
        get { return _multi; }
        set
        {
            _multi = value;
            WrapMode = value ? ScintillaNET.WrapMode.Word : ScintillaNET.WrapMode.None;
        }
    }
    public bool AcceptsReturn { get; set; }
    public bool WordWrap
    {
        get { return WrapMode != ScintillaNET.WrapMode.None; }
        set { WrapMode = value ? ScintillaNET.WrapMode.Word : ScintillaNET.WrapMode.None; }
    }
    public int PreferredHeight { get { return Math.Max(Theme.Px(22), Height); } }
    public void ScrollToCaret() { ScrollCaret(); }
    public ScrollBars ScrollBars { get; set; }
    public new BorderStyle BorderStyle { get; set; }
    // Scintilla 自己注册了 IDropTarget，再设 WinForms AllowDrop 会抛“DragDrop 注册失败”；
    // 图片拖入改走 WM_DROPFILES，再转成原来的 DragDrop 事件。
    public new bool AllowDrop { get { return true; } set { } }

    const int WM_DROPFILES = 0x0233;
    [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr hWnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr hDrop, uint i, StringBuilder file, uint cch);
    [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);

    public UiEditor()
    {
        base.BorderStyle = ScintillaNET.BorderStyle.None;
        BackColor = Theme.Card;
        ForeColor = Theme.Fg;
        StyleResetDefault();
        Styles[ScintillaNET.Style.Default].Font = "Microsoft YaHei UI";
        Styles[ScintillaNET.Style.Default].Size = 10;
        Styles[ScintillaNET.Style.Default].ForeColor = Theme.Fg;
        Styles[ScintillaNET.Style.Default].BackColor = Theme.Card;
        StyleClearAll();
        WrapMode = ScintillaNET.WrapMode.Word;
        Margins[0].Width = 0;
        HScrollBar = false;
        VScrollBar = false;
        _hint = new Label();
        _hint.AutoSize = false;
        _hint.BackColor = Theme.Card;
        _hint.ForeColor = Theme.FgFaint;
        _hint.Font = Theme.F(10f);
        _hint.TextAlign = ContentAlignment.MiddleLeft;
        _hint.Enabled = false;
        _hint.MouseDown += delegate { _hint.Visible = false; Focus(); };
        Controls.Add(_hint);
    }

    public string Hint
    {
        get { return _hintText; }
        set { _hintText = value ?? ""; _hint.Text = _hintText; SyncHint(); }
    }

    void SyncHint()
    {
        if (_hint == null) return;
        _hint.Visible = TextLength == 0 && _hintText.Length > 0;
        if (_hint.Visible) _hint.BringToFront();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DragAcceptFiles(Handle, true);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DROPFILES)
        {
            try
            {
                uint n = DragQueryFile(m.WParam, uint.MaxValue, null, 0);
                string[] files = new string[(int)n];
                for (uint i = 0; i < n; i++)
                {
                    uint len = DragQueryFile(m.WParam, i, null, 0);
                    StringBuilder sb = new StringBuilder((int)len + 1);
                    DragQueryFile(m.WParam, i, sb, (uint)sb.Capacity);
                    files[i] = sb.ToString();
                }
                if (files.Length > 0)
                    OnDragDrop(new DragEventArgs(new DataObject(DataFormats.FileDrop, files), 0, 0, 0,
                        DragDropEffects.Copy, DragDropEffects.Copy));
            }
            catch { }
            finally { DragFinish(m.WParam); }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_hint != null) _hint.SetBounds(4, 3, Math.Max(1, Width - 8), Math.Max(1, Height - 6));
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); SyncHint(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); SyncHint(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); SyncHint(); }

    public new void Clear() { Text = ""; }
}

// 自绘密码框：API Key 不再用原生 TextBox，也不依赖 UseSystemPasswordChar。
// 只支持 ASCII Key 编辑（粘贴/退格/输入），显示时按需打码；比系统密码框少 IME/选区。
class UiPasswordBox : Control
{
    string _text = "";
    bool _mask = true;
    bool _readOnly;
    int _caret;
    int _selStart = -1, _selEnd = -1;
    bool _drag;
    string _imeComp = "";
    bool _imeActive;

    const int WM_IME_STARTCOMPOSITION = 0x010D;
    const int WM_IME_ENDCOMPOSITION = 0x010E;
    const int WM_IME_COMPOSITION = 0x010F;
    const int WM_IME_CHAR = 0x0286;
    const int GCS_COMPSTR = 0x0008;
    const int GCS_RESULTSTR = 0x0800;

    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern int ImmGetCompositionString(IntPtr hIMC, int index, StringBuilder buf, int len);

    static string ImeString(IntPtr hIMC, int kind)
    {
        int bytes = ImmGetCompositionString(hIMC, kind, null, 0);
        if (bytes <= 0) return "";
        StringBuilder sb = new StringBuilder(bytes / 2 + 1);
        ImmGetCompositionString(hIMC, kind, sb, sb.Capacity * 2);
        return sb.ToString();
    }

    public UiPasswordBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        ForeColor = Theme.Fg;
        Font = Theme.F(9.5f);
        TabStop = true;
        Cursor = Cursors.IBeam;
        ImeMode = ImeMode.On;
    }

    // IME 合成串只放 _imeComp，绝不写 _text；只有 GCS_RESULTSTR 才提交到密码内存。
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_IME_STARTCOMPOSITION)
        {
            _imeActive = true;
            _imeComp = "";
            Invalidate();
            return;
        }
        if (m.Msg == WM_IME_ENDCOMPOSITION)
        {
            _imeActive = false;
            _imeComp = "";
            Invalidate();
            return;
        }
        if (m.Msg == WM_IME_CHAR)
        {
            return;   // 合成结果由 WM_IME_COMPOSITION 统一提交，避免双写
        }
        if (m.Msg == WM_IME_COMPOSITION)
        {
            IntPtr h = ImmGetContext(Handle);
            if (h != IntPtr.Zero)
            {
                try
                {
                    int flags = m.LParam.ToInt32();
                    if ((flags & GCS_RESULTSTR) != 0)
                    {
                        string s = ImeString(h, GCS_RESULTSTR);
                        _imeComp = "";
                        _imeActive = false;
                        if (s.Length > 0) ReplaceSelection(s);
                    }
                    else if ((flags & GCS_COMPSTR) != 0)
                    {
                        _imeComp = ImeString(h, GCS_COMPSTR);
                        _imeActive = true;
                        Invalidate();
                    }
                }
                finally { ImmReleaseContext(Handle, h); }
            }
            return;
        }
        base.WndProc(ref m);
    }

    public override string Text
    {
        get { return _text; }
        set { _text = value ?? ""; _imeComp = ""; _imeActive = false; _caret = _text.Length; _selStart = _selEnd = -1; OnTextChanged(EventArgs.Empty); Invalidate(); }
    }
    public bool ReadOnly { get { return _readOnly; } set { _readOnly = value; } }
    public bool UseSystemPasswordChar { get { return _mask; } set { _mask = value; Invalidate(); } }
    // 自测用：只进合成缓冲区，不写密码；只有 Commit 才提交。
    public void DebugCompose(string s) { _imeComp = s ?? ""; _imeActive = true; Invalidate(); }
    public void DebugCommit(string s) { ReplaceSelection(s ?? ""); _imeComp = ""; _imeActive = false; }
    public string DebugComposition { get { return _imeComp; } }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { _imeComp = ""; _imeActive = false; base.OnLostFocus(e); Invalidate(); }

    int SelA { get { return _selStart < 0 ? _caret : Math.Min(_selStart, _selEnd); } }
    int SelB { get { return _selStart < 0 ? _caret : Math.Max(_selStart, _selEnd); } }
    bool HasSel { get { return _selStart >= 0 && _selStart != _selEnd; } }

    void ReplaceSelection(string s)
    {
        int a = SelA, b = SelB;
        _text = _text.Substring(0, a) + s + _text.Substring(b);
        _caret = a + s.Length;
        _selStart = _selEnd = -1;
        OnTextChanged(EventArgs.Empty);
        Invalidate();
    }

    int CaretX(int pos)
    {
        string disp = _mask ? new string('●', Math.Min(pos, _text.Length)) : _text.Substring(0, Math.Min(pos, _text.Length));
        return Theme.Px(2) + TextRenderer.MeasureText(disp, Font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    }

    int CaretFromX(int x)
    {
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i <= _text.Length; i++)
        {
            int d = Math.Abs(CaretX(i) - x);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _caret = CaretFromX(e.X);
        _selStart = _selEnd = _caret;
        _drag = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drag) return;
        _selEnd = CaretFromX(e.X);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = false;
        if (_selStart == _selEnd) _selStart = _selEnd = -1;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (_readOnly) return;
        if (e.KeyChar == '\b')
        {
            if (HasSel) ReplaceSelection("");
            else if (_caret > 0)
            {
                _text = _text.Remove(_caret - 1, 1);
                _caret--;
                OnTextChanged(EventArgs.Empty); Invalidate();
            }
            e.Handled = true;
            return;
        }
        if (e.KeyChar >= ' ' && e.KeyChar != 127)
        {
            ReplaceSelection(e.KeyChar.ToString());
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_readOnly) return;
        bool shift = e.Shift;
        int old = _caret;
        if (e.KeyCode == Keys.Left) _caret = Math.Max(0, _caret - 1);
        else if (e.KeyCode == Keys.Right) _caret = Math.Min(_text.Length, _caret + 1);
        else if (e.KeyCode == Keys.Home) _caret = 0;
        else if (e.KeyCode == Keys.End) _caret = _text.Length;
        else if (e.KeyCode == Keys.Delete)
        {
            if (HasSel) ReplaceSelection("");
            else if (_caret < _text.Length)
            {
                _text = _text.Remove(_caret, 1);
                OnTextChanged(EventArgs.Empty); Invalidate();
            }
            e.SuppressKeyPress = true; return;
        }
        else if (e.Control && e.KeyCode == Keys.A)
        {
            _selStart = 0; _selEnd = _text.Length; _caret = _text.Length;
            Invalidate(); e.SuppressKeyPress = true; return;
        }
        else if (e.Control && e.KeyCode == Keys.V)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string s = Clipboard.GetText().Replace("\r", "").Replace("\n", "").Trim();
                    if (s.Length > 0) ReplaceSelection(s);
                }
            }
            catch { }
            e.SuppressKeyPress = true; return;
        }
        else return;

        if (shift)
        {
            if (_selStart < 0) _selStart = old;
            _selEnd = _caret;
        }
        else _selStart = _selEnd = -1;
        Invalidate();
        e.SuppressKeyPress = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        string disp = _mask ? new string('●', _text.Length) : _text;
        if (_imeActive || _imeComp.Length > 0) disp += _imeComp;
        Rectangle r = new Rectangle(Theme.Px(2), 0, Math.Max(1, Width - Theme.Px(4)), Height);
        if (HasSel)
        {
            int x1 = CaretX(SelA), x2 = CaretX(SelB);
            using (SolidBrush sb = new SolidBrush(Theme.Sel))
                g.FillRectangle(sb, x1, Theme.Px(4), Math.Max(1, x2 - x1), Height - Theme.Px(8));
        }
        TextRenderer.DrawText(g, disp, Font, r, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (Focused)
        {
            int x = Math.Min(Math.Max(Theme.Px(2), CaretX(_caret)), Width - Theme.Px(2));
            using (Pen p = new Pen(Theme.Fg, Theme.Pxf(1f))) g.DrawLine(p, x, Theme.Px(4), x, Height - Theme.Px(4));
        }
    }
}

// 圆角输入框：外面是画的圆角边框，里面塞一个无边框文本框
class FieldBox : RoundedPanel
{
    public Control Inner;
    private bool _multi;

    public FieldBox(bool multiline) : this(multiline, false) { }

    public FieldBox(bool multiline, bool password)
    {
        _multi = multiline;
        Radius = 10;
        Fill = Theme.Card;
        BorderColor = Theme.Line;
        BackColor = Theme.Bg;
        Padding = new Padding(11, 2, 11, 2);
        if (password)
        {
            UiPasswordBox rt = new UiPasswordBox();
            rt.Height = Theme.Px(22);
            Inner = rt;
        }
        else
        {
            UiEditor t = new UiEditor();
            t.Multiline = multiline;
            t.AcceptsReturn = multiline;
            t.ScrollBars = ScrollBars.None;
            t.WordWrap = multiline;
            t.Font = Theme.F(9.5f);
            t.Height = Theme.Px(22);
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

// 自绘消息框：替换系统 MessageBox（系统标题栏/系统图标/系统按钮和自绘 UI 不一致）。
static class UiMsg
{
    public static DialogResult Show(IWin32Window owner, string text, string caption,
        MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        using (UiMsgForm f = new UiMsgForm(text, caption, buttons, icon))
            return owner == null ? f.ShowDialog() : f.ShowDialog(owner);
    }
    public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons)
    { return Show(owner, text, caption, buttons, MessageBoxIcon.None); }
    public static DialogResult Show(IWin32Window owner, string text, string caption)
    { return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None); }
    public static DialogResult Show(IWin32Window owner, string text)
    { return Show(owner, text, "Ahz Scribe", MessageBoxButtons.OK, MessageBoxIcon.None); }
    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    { return Show(null, text, caption, buttons, icon); }
    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons)
    { return Show(null, text, caption, buttons, MessageBoxIcon.None); }
    public static DialogResult Show(string text, string caption)
    { return Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None); }
}

class UiMsgForm : Form
{
    readonly string _text, _caption;
    readonly MessageBoxButtons _buttons;
    readonly MessageBoxIcon _icon;
    int _headerH, _closeW, _bodyTop;
    Rectangle _closeRect;
    UiButton _ok, _cancel, _yes, _no;
    bool _closeHot;

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public UiMsgForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        _text = text ?? "";
        _caption = string.IsNullOrEmpty(caption) ? "Ahz Scribe" : caption;
        _buttons = buttons;
        _icon = icon;
        Text = _caption;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;
        BackColor = Theme.Bg;
        Font = Theme.F(9.5f);
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.Dialog;
        AccessibleName = _caption;
        _headerH = Theme.Px(42);
        _closeW = Theme.Px(38);
        int w = Theme.Px(520);
        int textW = w - Theme.Px(92);
        Size sz = TextRenderer.MeasureText(_text, Font, new Size(textW, Theme.Px(520)),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        int bodyH = Math.Max(Theme.Px(38), sz.Height);
        Width = w;
        Height = _headerH + bodyH + Theme.Px(88);
        _bodyTop = _headerH + Theme.Px(22);
        BuildButtons();
        AcceptButton = _ok ?? _yes;
        CancelButton = _cancel ?? _no;
        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape) return;
            DialogResult = (_buttons == MessageBoxButtons.YesNo) ? DialogResult.No : DialogResult.Cancel;
            Close();
        };
        Resize += delegate { SetRounded(); };
        SetRounded();
    }

    void SetRounded()
    {
        try
        {
            if (Region != null) Region.Dispose();
            Region = new Region(Ui.Round(ClientRectangle, Theme.Px(12)));
        }
        catch { }
    }

    UiButton Btn(string text, bool primary)
    {
        UiButton b = new UiButton();
        b.Text = text;
        b.Font = Theme.F(9.5f);
        b.Radius = Theme.RadRow;
        b.Height = Theme.Px(32);
        b.Width = Ui.TextWidth(text, b.Font) + Theme.Px(36);
        b.FillColor = primary ? Theme.Accent : Color.White;
        b.HoverColor = primary ? Theme.AccentHover : Theme.Hover;
        b.BorderColor = primary ? Theme.Accent : Theme.Line;
        b.TextColor = primary ? Color.White : Theme.Fg;
        Controls.Add(b);
        return b;
    }

    void BuildButtons()
    {
        bool yesNo = _buttons == MessageBoxButtons.YesNo || _buttons == MessageBoxButtons.YesNoCancel;
        bool cancel = _buttons == MessageBoxButtons.OKCancel || _buttons == MessageBoxButtons.YesNoCancel;
        if (yesNo)
        {
            _yes = Btn("是", true);
            _yes.Click += delegate { DialogResult = DialogResult.Yes; Close(); };
        }
        if (cancel)
        {
            _cancel = Btn("取消", false);
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        }
        if (!yesNo)
        {
            _ok = Btn("确定", true);
            _ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
        }
        if (yesNo)
        {
            _no = Btn("否", false);
            _no.Click += delegate { DialogResult = DialogResult.No; Close(); };
        }
        UiButton[] arr = new UiButton[] { _ok, _yes, _no, _cancel };
        int x = Width - Theme.Px(22);
        for (int i = 0; i < arr.Length; i++)
        {
            UiButton b = arr[i];
            if (b == null) continue;
            x -= b.Width;
            b.SetBounds(x, Height - Theme.Px(52), b.Width, b.Height);
            x -= Theme.Px(10);
        }
        _closeRect = new Rectangle(Width - _closeW, 0, _closeW, _headerH);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);
        using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, 0, 0, Width, _headerH);
        using (Pen p = new Pen(Theme.Line, Theme.Pxf(1f))) g.DrawLine(p, 0, _headerH - 1, Width, _headerH - 1);
        TextRenderer.DrawText(g, _caption, Theme.FB(9.5f),
            new Rectangle(Theme.Px(18), 0, Width - Theme.Px(70), _headerH), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "×", Theme.F(12f), _closeRect,
            _closeHot ? Theme.Fg : Theme.FgFaint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        int ic = Theme.Px(28);
        Rectangle ir = new Rectangle(Theme.Px(24), _headerH + Theme.Px(22), ic, ic);
        Color icc = _icon == MessageBoxIcon.Error ? Theme.Err
            : (_icon == MessageBoxIcon.Warning ? Theme.Warn : Theme.Accent);
        using (SolidBrush b = new SolidBrush(icc)) g.FillEllipse(b, ir);
        string mark = _icon == MessageBoxIcon.Error ? "!" : (_icon == MessageBoxIcon.Warning ? "!" : "i");
        TextRenderer.DrawText(g, mark, Theme.FB(11f), ir, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        Rectangle tr = new Rectangle(Theme.Px(66), _bodyTop, Width - Theme.Px(92), Height - _bodyTop - Theme.Px(66));
        TextRenderer.DrawText(g, _text, Font, tr, Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak
            | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hot = _closeRect.Contains(e.Location);
        if (hot != _closeHot) { _closeHot = hot; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _closeHot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_closeRect.Contains(e.Location))
        {
            DialogResult = (_buttons == MessageBoxButtons.YesNo) ? DialogResult.No : DialogResult.Cancel;
            Close();
            return;
        }
        if (e.Y < _headerH)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }
    }
}

// 无边框弹窗的圆角：和主窗口的 Win11 圆角/自绘卡片保持同一视觉。
static class WindowRound
{
    public static void Apply(Form f)
    {
        if (f == null) return;
        f.HandleCreated += delegate { Round(f); };
        f.Resize += delegate { Round(f); };
        if (f.IsHandleCreated) Round(f);
    }

    static void Round(Form f)
    {
        try
        {
            if (f.WindowState != FormWindowState.Normal || f.ClientSize.Width < 2) return;
            using (GraphicsPath p = Ui.Round(f.ClientRectangle, Theme.Px(12)))
            {
                if (f.Region != null) f.Region.Dispose();
                f.Region = new Region(p);
            }
        }
        catch { }
    }
}

// 自绘弹窗标题栏：标题 + 关闭；拖拽走 WM_NCLBUTTONDOWN/HTCAPTION。
class ChromeBar : Control
{
    string _caption;
    Rectangle _closeRect;
    bool _closeHot;
    public event EventHandler Closed;

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public ChromeBar(string caption)
    {
        _caption = caption ?? "";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = Theme.Px(40);
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.TitleBar;
        AccessibleName = _caption;
        TabStop = false;
        ParentChanged += delegate
        {
            Form f = FindForm();
            if (f != null) WindowRound.Apply(f);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Form f = FindForm();
        if (f != null) WindowRound.Apply(f);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _closeRect = new Rectangle(Math.Max(0, Width - Theme.Px(44)), 0, Theme.Px(44), Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, ClientRectangle);
        using (Pen p = new Pen(Theme.Line, Theme.Pxf(1f))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        TextRenderer.DrawText(g, _caption, Theme.FB(9.5f),
            new Rectangle(Theme.Px(16), 0, Math.Max(1, Width - Theme.Px(70)), Height), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "×", Theme.F(12f), _closeRect,
            _closeHot ? Theme.Fg : Theme.FgFaint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hot = _closeRect.Contains(e.Location);
        if (hot != _closeHot) { _closeHot = hot; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { _closeHot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_closeRect.Contains(e.Location))
        {
            if (Closed != null) Closed(this, EventArgs.Empty);
            Form f = FindForm();
            if (f != null) f.Close();
            return;
        }
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
    }
}

// 无边框可缩放窗口的右下角拖拽柄（换掉系统标题栏后，至少要保留缩放能力）。
class SizeGrip : Control
{
    readonly Form _form;
    bool _drag;
    Point _start;
    Size _startSize;

    public SizeGrip(Form form)
    {
        _form = form;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(Theme.Px(18), Theme.Px(18));
        Cursor = Cursors.SizeNWSE;
        Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        AccessibleRole = AccessibleRole.Grip;
        AccessibleName = "调整窗口大小";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (Pen p = new Pen(Theme.FgFaint, Theme.Pxf(1f)))
        {
            for (int i = 0; i < 3; i++)
            {
                int d = Theme.Px(5 + i * 4);
                e.Graphics.DrawLine(p, Width - d, Height - 2, Width - 2, Height - d);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _drag = true;
        _start = Cursor.Position;
        _startSize = _form.Size;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drag) return;
        Point p = Cursor.Position;
        int w = Math.Max(_form.MinimumSize.Width, _startSize.Width + p.X - _start.X);
        int h = Math.Max(_form.MinimumSize.Height, _startSize.Height + p.Y - _start.Y);
        _form.Size = new Size(w, h);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = false;
        Capture = false;
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
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(0, Theme.Px(40), 0, 0);
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
        box.Radius = Theme.RadInput;
        box.Fill = Theme.Card;
        box.BorderColor = Theme.Line;
        box.BackColor = Theme.Bg;
        box.Padding = new Padding(Theme.Sp16, Theme.Sp12, Theme.Sp16, Theme.Sp12);

        UiDiff body = new UiDiff();
        body.Dock = DockStyle.Fill;
        body.Text = string.Join("\n", preview);
        box.Controls.Add(body);

        Panel bodyWrap = new Panel();
        bodyWrap.Dock = DockStyle.Fill;
        bodyWrap.BackColor = Theme.Bg;
        bodyWrap.Padding = new Padding(Theme.Sp24, 0, Theme.Sp24, 0);
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
        yes.Radius = Theme.RadCard;
        yes.Font = Theme.F(9.5f);
        yes.Size = new Size(104, 36);
        yes.Click += delegate { Decision = 1; Close(); };

        UiButton always = new UiButton();
        always.Text = "本会话总是允许";
        always.Radius = Theme.RadCard;
        always.BorderColor = Theme.Line;
        always.FillColor = Color.White;
        always.HoverColor = Theme.Hover;
        always.Font = Theme.F(9.5f);
        always.Size = new Size(146, 36);
        always.Click += delegate { Decision = 2; Close(); };

        UiButton no = new UiButton();
        no.Text = "拒绝";
        no.Radius = Theme.RadCard;
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
            int right = buttons.ClientSize.Width - Theme.Px(20);
            no.Location = new Point(right - no.Width, Theme.Px(14));
            yes.Location = new Point(no.Left - Theme.Px(10) - yes.Width, Theme.Px(14));
            always.Location = new Point(yes.Left - Theme.Px(10) - always.Width, Theme.Px(14));
        };

        Label hint = new Label();
        hint.Text = safeDefault ? "Enter 拒绝 · 要放行请点「允许」或「本会话总是允许」" : "Enter 允许 · Esc 拒绝";
        hint.ForeColor = Theme.FgFaint;
        // 不再 AutoSize：最小宽度 900 时，AutoSize 的提示会一路画到按钮底下
        // （2026-10-18 验收：提示宽 481，按钮从 331 开始，重叠 186px）。
        hint.AutoSize = false;
        hint.AutoEllipsis = true;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        hint.Location = new Point(Theme.Px(24), Theme.Px(22));
        buttons.Controls.Add(hint);
        // 按钮排完后再按 always.Left 收一次提示宽度；两个 Layout 按订阅顺序执行。
        buttons.Layout += delegate
        {
            int hintW = Math.Max(Theme.Px(100), always.Left - Theme.Px(36));
            hint.SetBounds(Theme.Px(24), Theme.Px(22), hintW, Math.Max(Theme.Px(24), buttons.Height - Theme.Px(28)));
        };

        Controls.Add(bodyWrap);
        Controls.Add(buttons);
        Controls.Add(head);
        ChromeBar bar = new ChromeBar("需要确认");
        bar.Bounds = new Rectangle(0, 0, ClientSize.Width, Theme.Px(40));
        bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        bar.Closed += delegate { Decision = 0; Close(); };
        Controls.Add(bar);
        bar.BringToFront();

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
        Height += Theme.Px(40);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundWindow(Handle);
    }
}

// ============================ 主窗口 ============================

// ============================ 智能意图澄清（2026-10-16） ============================
// 用户在 GUI 提交指令后、进 AgentRunner 之前，先用轻量模型跑一次歧义检测：
//   ambiguous=false        → 原指令直接下发（和以前完全一样）
//   ambiguous=true         → 弹确认框渲染选项，选中项的 command 原样下发（不再二次拼接）
// 纯逻辑都抽成静态成员（Parse / PickProvider / DefaultOption / ShouldAsk / Count），
// 这样 rules-unittest.cs 能直接断言，不用起窗口（弹窗那部分在 ClarifyForm 里）。
static class Clarify
{
    // 系统提示词：原文照抄需求（不要改字、不要精简）。
    // 末尾那段 Schema 里的 { } 是给模型看的，不是 C# 的占位符 —— 用逐字字符串 + 拼接装进来。
    const string Schema =
        "{\n" +
        "  \"ambiguous\": boolean,\n" +
        "  \"title\": \"string // 确认弹窗标题，仅ambiguous为true时返回\",\n" +
        "  \"options\": [\n" +
        "    {\n" +
        "      \"id\": number,\n" +
        "      \"label\": \"string // 选项短标题\",\n" +
        "      \"desc\": \"string // 选项详细说明，讲清执行范围和内容\",\n" +
        "      \"command\": \"string // 精准执行指令，用户选中后直接下发执行引擎\",\n" +
        "      \"recommended\": boolean // 是否为推荐选项，第一个选项设为true\n" +
        "    }\n" +
        "  ]\n" +
        "}";

    public const string Prompt =
        "# 角色：AhzScribe指令歧义检测专家\n" +
        "# 任务：在用户指令进入执行引擎前，判断指令是否存在多种解读可能、执行范围不明确、操作对象模糊。若存在歧义，生成2-4个互斥的具体执行选项供用户确认；若指令明确，则直接放行。\n" +
        "\n" +
        "## 歧义判定标准（满足任意一条即判定为有歧义）\n" +
        "1.  使用泛化动词，无明确对象与范围：如“更新一下”“优化一下”“修一下”“改改”“整理一下”“处理一下”等，未说明具体操作对象、深度和边界。\n" +
        "2.  操作对象存在多种可能：如“更新deepseek”可对应刷新模型清单、切换模型版本、更新相关文档、升级接口配置等多个方向。\n" +
        "3.  执行范围与深度不明确：如“优化界面”可对应样式微调、交互重构、全局重设计；“修bug”可对应修复单问题、全量排查等。\n" +
        "4.  风险边界不清晰：如“修改代码”可对应仅编辑文件、修改并编译验证、修改并重启全流程测试。\n" +
        "5.  使用简称、代称，可能对应多个功能模块，无法唯一确认意图。\n" +
        "\n" +
        "## 输出要求（强制遵守）\n" +
        "1.  **仅输出纯JSON字符串**，无任何前后缀文字、无markdown格式、无代码块包裹、无解释性内容，确保可直接被JSON.parse解析。\n" +
        "2.  严格遵循下方字段定义，禁止新增字段、禁止缺失必填字段。\n" +
        "3.  有歧义时选项数量控制在2-4个，按优先级排序，第一个为最推荐的高频场景。\n" +
        "4.  选项必须互斥，覆盖主要执行方向，描述具体可执行，不能再次模糊。\n" +
        "5.  贴合AhzScribe的功能范围：模型配置、界面UI、代码文件、插件管理、性能优化、运行日志、系统设置等场景。\n" +
        "\n" +
        "## 输出JSON Schema\n" + Schema;

    public const int MaxTokens = 500;      // 轻量：只要一小段 JSON，500 token 足够
    public const int TimeoutMs = 12000;    // 12 秒（不能用默认的 15 分钟：那会把工作线程拖死）
    public const int AskLimit = 2;         // 同一条原始输入问过 2 次之后，第 3 次不再问，直接用推荐项

    // ---------- 结果对象 ----------

    public class Option
    {
        public int id;
        public string label = "";
        public string desc = "";
        public string command = "";
        public bool recommended;
    }

    public class Result
    {
        public bool ambiguous;
        public string title = "";
        public List<Option> options = new List<Option>();

        public Option First()
        {
            return (options != null && options.Count > 0) ? options[0] : null;
        }
    }

    // ---------- 纯逻辑（可离线断言） ----------

    // 只把开头一段送去检测：判断"要不要问"看开头就够了，一次长粘贴不该把 token 打爆
    // （需求写明单轮开销要极低）。真正下发给执行引擎的仍是完整原文。
    public const int ProbeChars = 800;

    // 网络层失败后的冷却：断网/接口挂掉时，别让"每一轮都白等一次 12 秒超时"拖垮体验。
    public const int FailCooldownMs = 5 * 60 * 1000;
    static int _cooldownUntil;

    public static bool InCooldown()
    {
        if (_cooldownUntil == 0) return false;
        return unchecked(Environment.TickCount - _cooldownUntil) < 0;
    }

    public static void MarkFailed() { _cooldownUntil = unchecked(Environment.TickCount + FailCooldownMs); }
    public static void ResetCooldown() { _cooldownUntil = 0; }

    // 结构化指令不问：以 / 开头的是精确命令（如「/计划 开」），再问一遍纯属浪费一次调用，还多一次误弹窗的机会。
    public static bool LooksStructured(string text)
    {
        if (text == null) return false;
        return text.TrimStart().StartsWith("/");
    }

    // 送检测的正文（截断到 ProbeChars）
    public static string Probe(string text)
    {
        if (text == null) return "";
        string t = text.Trim();
        return t.Length <= ProbeChars ? t : t.Substring(0, ProbeChars);
    }

    // 该不该把选项摆到用户面前：
    //   · 开关关掉 → 不问
    //   · 没有可用的检测提供商（名字/id 含 deepseek 的 openai + 有 Key + 有模型）→ 静默跳过
    //   · 模型说"不歧义" → 不问（原指令直接下发）
    //   · options 为空或只有 1 项 → 算不上"多个解读"，按无歧义处理
    public static bool ShouldAsk(AgentConfig config, Result r)
    {
        if (config == null || !config.clarify) return false;
        string model;
        if (PickProvider(config, out model) == null) return false;
        if (r == null || !r.ambiguous) return false;
        return r.options != null && r.options.Count >= 2;
    }

    // 检测模型：名字或 id 含 deepseek、type=openai、api_key 非空、模型列表非空。
    // 模型取列表里含 flash 的那个（deepseek-flash 快），找不到就取第一个。
    public static Provider PickProvider(AgentConfig config, out string model)
    {
        model = null;
        if (config == null || config.providers == null) return null;
        foreach (Provider p in config.providers)
        {
            if (p == null) continue;
            if (p.type != "openai") continue;
            if (string.IsNullOrEmpty(p.api_key) || p.api_key.Trim().Length == 0) continue;
            if (p.models == null || p.models.Count == 0) continue;
            string name = (p.name == null ? "" : p.name.ToLowerInvariant());
            string id = (p.id == null ? "" : p.id.ToLowerInvariant());
            if (name.IndexOf("deepseek", StringComparison.Ordinal) < 0
                && id.IndexOf("deepseek", StringComparison.Ordinal) < 0) continue;
            string pick = null;
            foreach (string m in p.models)
            {
                if (m == null || m.Trim().Length == 0) continue;
                if (pick == null) pick = m.Trim();
                if (m.ToLowerInvariant().IndexOf("flash", StringComparison.Ordinal) >= 0) { pick = m.Trim(); break; }
            }
            if (pick == null) continue;
            model = pick;
            return p;
        }
        return null;
    }

    // 推荐项 = 第一个选项（提示词里要求第一个就是最推荐的高频场景）
    public static Option DefaultOption(Result r)
    {
        return r == null ? null : r.First();
    }

    // 用户选中项的 command：原样返回，不做任何拼接
    public static string CommandOf(Result r, int index)
    {
        if (r == null || r.options == null) return null;
        if (index < 0 || index >= r.options.Count) return null;
        string c = r.options[index].command;
        return (c == null || c.Trim().Length == 0) ? null : c;
    }

    // ---------- 同一指令去重（只算内存，不落盘） ----------

    static readonly Dictionary<string, int> Asked = new Dictionary<string, int>(StringComparer.Ordinal);

    public static string KeyOf(string text) { return text == null ? "" : text.Trim(); }

    public static int Count(string text)
    {
        int n;
        return Asked.TryGetValue(KeyOf(text), out n) ? n : 0;
    }

    public static void Bump(string text)
    {
        string k = KeyOf(text);
        Asked[k] = Count(text) + 1;
    }

    public static void Reset() { Asked.Clear(); ResetCooldown(); }

    // ---------- JSON 解析 ----------

    // 先严格解析；失败再剥掉 ```json 围栏重试一次；仍失败返回 null（上层当"跳过澄清"处理）。
    public static Result Parse(string content)
    {
        if (content == null) return null;
        Result r = ParseOnce(content);
        if (r != null) return r;
        string inner = StripFence(content);
        if (inner != null) r = ParseOnce(inner);
        return r;
    }

    static Result ParseOnce(string text)
    {
        try
        {
            string json = ExtractObject(text);
            if (json == null) return null;
            Dictionary<string, object> o = Json.Obj(json);
            if (o == null) return null;
            Result r = new Result();
            r.ambiguous = Json.Bool(o, "ambiguous", false);
            string t = Json.Str(o, "title", null);
            r.title = t == null ? "" : t.Trim();
            object[] arr = Json.Arr(Json.Get(o, "options"));
            if (arr != null)
            {
                foreach (object it in arr)
                {
                    Dictionary<string, object> m = Json.Map(it);
                    if (m == null) continue;
                    Option op = new Option();
                    op.id = Json.Int(m, "id", r.options.Count + 1);
                    string s = Json.Str(m, "label", null); op.label = s == null ? "" : s.Trim();
                    s = Json.Str(m, "desc", null); op.desc = s == null ? "" : s.Trim();
                    s = Json.Str(m, "command", null); op.command = s == null ? "" : s.Trim();
                    op.recommended = Json.Bool(m, "recommended", false);
                    r.options.Add(op);
                }
            }
            return r;
        }
        catch { return null; }
    }

    // 把 ```json ... ``` 那层围栏剥掉（模型偶尔不听话会套代码块）。没有围栏就返回 null。
    static string StripFence(string s)
    {
        int i = s.IndexOf("```", StringComparison.Ordinal);
        if (i < 0) return null;
        int j = s.IndexOf("```", i + 3, StringComparison.Ordinal);
        if (j < 0) return null;
        string body = s.Substring(i + 3, j - i - 3);
        if (body.StartsWith("json", StringComparison.OrdinalIgnoreCase)) body = body.Substring(4);
        return body.Trim();
    }

    // 取第一个配平的 { ... }（模型可能在 JSON 前后带一句话）。字符串里的括号不算，转义也不误判。
    static string ExtractObject(string s)
    {
        if (s == null) return null;
        int start = s.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inStr = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return s.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    // ---------- 网络（只在回合工作线程上调用，绝不进 UI 线程） ----------

    // 探一次歧义：返回 null = 跳过澄清（超时 / 非 200 / 解析失败 / 任何异常），按原指令执行。
    public static Result Ask(AgentConfig config, string text)
    {
        try
        {
            if (config == null || text == null || text.Trim().Length == 0) return null;
            if (InCooldown()) return null;                     // 刚失败过（多半是断网）：这段时间不再尝试，别每轮白等超时
            string probe = Probe(text);                        // 长粘贴只送开头一段
            string model;
            Provider p = PickProvider(config, out model);
            if (p == null) return null;
            Program.EnsureTls();
            string url = (p.base_url == null ? "" : p.base_url.Trim().TrimEnd('/'));
            if (url.Length == 0) url = "https://api.openai.com/v1";
            url += "/chat/completions";

            Dictionary<string, object> sys = new Dictionary<string, object>();
            sys["role"] = "system";
            sys["content"] = Prompt;
            Dictionary<string, object> usr = new Dictionary<string, object>();
            usr["role"] = "user";
            usr["content"] = "用户指令：" + probe;
            List<object> msgs = new List<object>();
            msgs.Add(sys);
            msgs.Add(usr);
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = model;
            payload["messages"] = msgs;
            payload["temperature"] = 0;
            payload["max_tokens"] = MaxTokens;
            payload["stream"] = false;
            Dictionary<string, object> fmt = new Dictionary<string, object>();
            fmt["type"] = "json_object";
            payload["response_format"] = fmt;

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Proxy = null;                      // 和 OpenAiLlm 同一套约定：不走系统代理
            req.Timeout = TimeoutMs;               // 必须短超时：默认 900000 会把这一轮卡一刻钟
            req.ReadWriteTimeout = TimeoutMs;
            req.KeepAlive = false;
            req.Accept = "application/json";
            if (!string.IsNullOrEmpty(p.api_key)) req.Headers["Authorization"] = "Bearer " + p.api_key.Trim();
            byte[] data = Encoding.UTF8.GetBytes(Json.Dump(payload));
            req.ContentType = "application/json";
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);

            string body;
            using (WebResponse resp = req.GetResponse())
            using (Stream rs = resp.GetResponseStream())
            using (StreamReader rd = new StreamReader(rs, Encoding.UTF8))
                body = rd.ReadToEnd();

            Dictionary<string, object> o = Json.Obj(body);
            object[] choices = Json.Arr(Json.Get(o, "choices"));
            if (choices == null || choices.Length == 0) return null;
            Dictionary<string, object> ch = Json.Map(choices[0]);
            if (ch == null) return null;
            Dictionary<string, object> msg = Json.Map(Json.Get(ch, "message"));
            if (msg == null) return null;
            string content = Json.Str(msg, "content", null);
            return Parse(content);
        }
        catch (Exception ex) { Log.Swallow(ex, "意图澄清检测"); MarkFailed(); return null; }
    }
}

// 「这条指令有几种理解，你要哪一种」确认框。风格照 ProposalForm（同一套 Theme / 栅格令牌 / 自绘按钮）。
// SelectedIndex：点「执行」= 选中项下标；点「跳过」/ESC/点 X = -1（跳过 = 用原指令执行）。
// ShownTitle = 模型返回的 title（用来摆在框里当主标题；没给 title 时用兜底文案）。
class ClarifyForm : Form
{
    public int SelectedIndex = -1;
    public string ShownTitle = "";

    ClarifyRow[] _rows;
    int _sel;
    // 用户已经明确表过态（点执行 / 点跳过 / 按 ESC）：OnDeactivate 不许再把它改成"跳过"。
    // 原因：Close() 会先触发 OnDeactivate（此时 IsDisposed 还是 false），再真正 Dispose ——
    // 只靠 IsDisposed 判断的话，"点执行"的结果会被 OnDeactivate 抹成 -1。
    bool _decided;

    public ClarifyForm(string title, List<Clarify.Option> options)
    {
        Theme.EnsureDpi();
        AutoScaleMode = AutoScaleMode.None;
        Text = "确认执行意图";      // 窗口标题栏（自绘外壳之外的那一圈）
        ShownTitle = (title == null || title.Trim().Length == 0) ? "这条指令有几种理解，选一个" : title.Trim();
        Icon = Theme.AppIcon();
        Size = new Size(Theme.Px(720), Theme.Px(520));
        MinimumSize = new Size(Theme.Px(560), Theme.Px(400));
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(0, Theme.Px(40), 0, 0);
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
        LogoMark mark = new LogoMark();
        mark.BackColor = Theme.Bg;
        mark.SetBounds(Theme.Px(24), Theme.Px(24), Theme.Px(26), Theme.Px(26));
        head.Controls.Add(mark);
        Label t = new Label();
        t.Text = ShownTitle;
        t.Font = Theme.FB(13f);
        t.ForeColor = Theme.Fg;
        t.BackColor = Theme.Bg;
        t.AutoSize = true;
        t.Location = new Point(Theme.Px(60), Theme.Px(20));
        t.MaximumSize = new Size(Theme.Px(600), 0);
        head.Controls.Add(t);
        Label sub = new Label();
        sub.Text = "指令有歧义，先确认你要哪一种；选中后按它的说法直接执行。";
        sub.ForeColor = Theme.FgDim;
        sub.BackColor = Theme.Bg;
        sub.AutoSize = true;
        sub.Location = new Point(Theme.Px(62), Theme.Px(50));
        head.Controls.Add(sub);
        Controls.Add(head);

        Panel bottom = new Panel();
        bottom.Dock = DockStyle.Bottom;
        bottom.Height = Theme.Px(64);
        bottom.BackColor = Theme.Bg;
        UiButton ok = new UiButton();
        ok.Text = "执行";
        ok.FillColor = Theme.Accent;
        ok.HoverColor = Theme.AccentHover;
        ok.TextColor = Color.White;
        ok.Radius = Theme.RadCard;
        ok.Font = Theme.F(9.5f);
        ok.Size = new Size(Theme.Px(104), Theme.Px(36));
        ok.Click += delegate { _decided = true; SelectedIndex = _sel; Close(); };
        bottom.Controls.Add(ok);
        UiButton skip = new UiButton();
        skip.Text = "跳过";
        skip.FillColor = Color.White;
        skip.BorderColor = Theme.Line;
        skip.HoverColor = Theme.Hover;
        skip.Radius = Theme.RadCard;
        skip.Font = Theme.F(9.5f);
        skip.Size = new Size(Theme.Px(88), Theme.Px(36));
        skip.Click += delegate { _decided = true; SelectedIndex = -1; Close(); };
        bottom.Controls.Add(skip);
        Label hint = new Label();
        hint.Text = "Enter 执行选中项 · Esc 跳过（跳过 = 按原话执行）";
        hint.ForeColor = Theme.FgFaint;
        hint.BackColor = Theme.Bg;
        hint.AutoSize = true;
        hint.Location = new Point(Theme.Px(24), Theme.Px(24));
        bottom.Controls.Add(hint);
        bottom.Layout += delegate
        {
            skip.Location = new Point(bottom.ClientSize.Width - Theme.Px(20) - skip.Width, Theme.Px(14));
            ok.Location = new Point(skip.Left - Theme.Px(10) - ok.Width, Theme.Px(14));
        };
        Controls.Add(bottom);

        Panel wrap = new Panel();
        wrap.Dock = DockStyle.Fill;
        wrap.BackColor = Theme.Bg;
        wrap.AutoScroll = true;
        List<Clarify.Option> opts = options == null ? new List<Clarify.Option>() : options;
        _rows = new ClarifyRow[opts.Count];
        int idx = 0;
        bool hasRec = false;
        foreach (Clarify.Option op in opts) if (op.recommended) hasRec = true;
        _sel = hasRec ? -1 : 0;
        foreach (Clarify.Option op in opts)
        {
            if (op.recommended && _sel < 0) _sel = idx;
            ClarifyRow row = new ClarifyRow();
            row.Index = idx;
            row.OptionRef = op;
            row.Left = Theme.Sp16;
            row.Width = Math.Max(Theme.Px(200), wrap.ClientSize.Width - Theme.Sp32);
            row.Top = Theme.Sp8 + idx * Theme.Px(90);
            int my = idx;
            // 按下即选中（OnPick），Click 只当兜底：两条都走同一个 SelectRow，不会各说各话
            row.OnPick = delegate(ClarifyRow r) { SelectRow(r.Index); };
            row.Click += delegate { SelectRow(my); };
            _rows[idx] = row;
            wrap.Controls.Add(row);
            idx++;
        }
        if (_sel < 0 && _rows.Length > 0) _sel = 0;
        wrap.Layout += delegate
        {
            int y = Theme.Sp8;
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i] == null) continue;
                _rows[i].SetBounds(Theme.Sp16, y, Math.Max(Theme.Px(200), wrap.ClientSize.Width - Theme.Sp32), Theme.Px(88));
                y += Theme.Px(90);
            }
        };
        Controls.Add(wrap);
        wrap.BringToFront();
        SyncRows();
        ChromeBar bar = new ChromeBar("确认执行意图");
        bar.Bounds = new Rectangle(0, 0, ClientSize.Width, Theme.Px(40));
        bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        bar.Closed += delegate { _decided = true; SelectedIndex = -1; Close(); };
        Controls.Add(bar);
        bar.BringToFront();
        Height += Theme.Px(40);

        AcceptButton = ok;
        KeyPreview = true;
        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { _decided = true; SelectedIndex = -1; Close(); return; }
            // 上下键也能换选项（以前只能点，点了还可能被吞）
            if (e.KeyCode == Keys.Up) { SelectRow(Math.Max(0, _sel - 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { SelectRow(Math.Min(_rows.Length - 1, _sel + 1)); e.Handled = true; }
        };
    }

    // 弹出来就抢焦点：否则用户的第一下点击只是"激活窗口"，会被系统吞掉
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try { Activate(); } catch { }
    }

    void SyncRows()
    {
        if (_rows == null) return;
        for (int i = 0; i < _rows.Length; i++)
        {
            if (_rows[i] == null) continue;
            _rows[i].Selected = (i == _sel);
        }
    }

    // 选中第 i 行：唯一的入口（鼠标按下 / 键盘上下 / 初始化都走它）
    public void SelectRow(int i)
    {
        if (_rows == null || i < 0 || i >= _rows.Length) return;
        if (_sel == i) { SyncRows(); return; }   // 已经是它也要同步一次（防状态错位）
        _sel = i;
        SyncRows();
    }

    // Blur / 点关闭 = 跳过（用原指令执行），绝不把用户卡在这一步。
    // 已经表过态的（点执行 / 点跳过 / ESC）不再改写结论 —— Close() 会先走这里再 Dispose。
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_decided) SelectedIndex = -1;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundWindow(Handle);
    }
}

// 一行选项：单选圈 + label（主文）+ desc（灰色小字）+ 推荐项右上角「推荐」角标
class ClarifyRow : Control
{
    public int Index;
    public Clarify.Option OptionRef;
    private bool _selected;

    // 选中态：改了就重画（以前是公开字段，SyncRows 只赋值不重画 —— 改了也看不出来）
    public bool Selected
    {
        get { return _selected; }
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    // 点一下就选中，不等 MouseUp —— 以前只挂 Click，而 Click 要求"按下和抬起落在同一控件、
    // 且中途没丢鼠标捕获"，对话框刚弹出/焦点切换/鼠标微动都会把这次点击吞掉，用户看到的就是
    // "点第二个、第三个选项没反应"（devtools\bench\_probe_clicks.cs 复现过：_sel 一直是 0）。
    public Action<ClarifyRow> OnPick;

    public ClarifyRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        AccessibleRole = AccessibleRole.RadioButton;
        AccessibleName = "澄清选项";
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (OnPick != null) OnPick(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (GraphicsPath path = Ui.Round(r, Theme.Px(Theme.RadCard)))
        {
            Color fill = Selected ? Theme.AccentSoft : Color.White;
            using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
            using (Pen p = new Pen(Selected ? Theme.Accent : Theme.Line, Theme.Pxf(1f))) g.DrawPath(p, path);
        }
        string label = OptionRef == null ? "" : OptionRef.label;
        string desc = OptionRef == null ? "" : OptionRef.desc;
        int x = Theme.Sp16;
        int cy = Height / 2;
        int d = Theme.Px(16);
        Rectangle circle = new Rectangle(x, cy - d / 2, d - 1, d - 1);
        using (Pen p = new Pen(Selected ? Theme.Accent : Theme.FgFaint, Theme.Pxf(1.4f))) g.DrawEllipse(p, circle);
        if (Selected)
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                g.FillEllipse(b, new Rectangle(circle.X + Theme.Px(4), circle.Y + Theme.Px(4),
                    circle.Width - Theme.Px(8), circle.Height - Theme.Px(8)));
        int tx = x + d + Theme.Sp8;
        int tw = Width - tx - Theme.Sp16;
        TextRenderer.DrawText(g, label, Theme.FB(10.5f),
            new Rectangle(tx, Theme.Sp12, tw, Theme.Px(22)), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (!string.IsNullOrEmpty(desc))
            TextRenderer.DrawText(g, desc, Theme.F(9f),
                new Rectangle(tx, Theme.Sp12 + Theme.Px(24), tw, Theme.Px(40)), Theme.FgDim,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);
        if (OptionRef != null && OptionRef.recommended)
        {
            string badge = "推荐";
            int bw = Ui.TextWidth(badge, Theme.F(9f)) + Theme.Sp12;
            Rectangle br = new Rectangle(Width - Theme.Sp12 - bw, Theme.Sp12, bw, Theme.Px(20));
            using (GraphicsPath bp = Ui.Round(br, Theme.Px(Theme.RadMenu)))
            {
                using (SolidBrush b = new SolidBrush(Theme.AccentSoft)) g.FillPath(b, bp);
                using (Pen p = new Pen(Theme.Accent, Theme.Pxf(1f))) g.DrawPath(p, bp);
            }
            TextRenderer.DrawText(g, badge, Theme.F(9f), br, Theme.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}

// ============================ 内置浏览器（给人看的那一半，Chromium 内核） ============================
// 引擎：CefSharp（自带一份 Chromium，和 Claude / Codex / DSH 同一个路子）。
// 换掉 IE 的原因：IE 内核跑不动现代前端，chat.deepseek.com 这类站点直接白屏。
// 代价：程序目录多一个 cef\（约 300MB，libcef.dll 一个就 205MB）；exe 本身还是 0.5MB。
// 外壳照 Edge 排：标签页条（40）→ 工具条（44）→ 书签栏（32）→ 内容区 → 状态栏（22）。
class BrowserTab
{
    public CefSharp.WinForms.ChromiumWebBrowser Web;
    public string Title = "新标签页";
    public string Url = "";
    public volatile bool Loaded;
    public string Pending = "";      // 浏览器还没初始化完时要导航过去的地址
    public List<string> Hist = new List<string>();   // 自己维护的浏览历史
    public int HistPos = -1;
}

class AhzBrowser : Panel
{
    public Action<string> AskAgent;
    public Action<string> SendToInput;
    public ToolTip tipsRef;
    public UiEditor Addr;

    List<BrowserTab> _tabs = new List<BrowserTab>();
    int _active = -1;
    bool _reading;
    string _readingUrl = "";
    string _lastText = "";              // 最近一次读到的正文（UI 线程只读缓存，绝不阻塞）
    public bool AgentDriving;
    internal Rectangle AddrBox;

    Label _status, _hint;
    UiButton _back, _fwd, _reload, _home, _read, _star, _ask, _edge;
    string _siteMark = "•";
    Color _siteColor = Theme.FgFaint;
    List<string[]> _marks = new List<string[]>();
    int _hotTab = -1, _hotClose = -1, _hotMark = -1;
    System.Windows.Forms.Timer _blankTimer;
    long _placedLogged;

    const int StripH = 40, ToolH = 44, MarkH = 32;
    int StripHP { get { return Theme.Px(StripH); } }
    int ToolHP { get { return Theme.Px(ToolH); } }
    int MarkHP { get { return Theme.Px(MarkH); } }

    const string BodyTextJs = "document.body ? document.body.innerText : ''";

    // ---------- Chromium 生命周期 ----------
    static bool _cefReady, _cefFailed;

    // 必须和 exe 同目录：CefSharp.Core.Runtime.dll 是混合模式程序集，它的原生依赖（libcef.dll）
    // 只会在 exe 所在目录被找到 —— 放 cef\ 子目录会报"找不到指定的模块"（实测）。
    static string CefDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string path);

    // libcef.dll 在 cef\ 里（不在 exe 旁边），所以先把它加进 DLL 搜索路径再 Initialize
    public static bool EnsureCef(out string err)
    {
        err = null;
        if (_cefReady) return true;
        if (_cefFailed) { err = "Chromium 之前初始化失败过（详见 Ahz错误日志）"; return false; }
        string dir = CefDir;
        try
        {
            if (!File.Exists(Path.Combine(dir, "libcef.dll")))
            {
                _cefFailed = true;
                err = "缺 libcef.dll（内置浏览器的 Chromium 内核文件不全）";
                return false;
            }
            CefSharp.WinForms.CefSettings st = new CefSharp.WinForms.CefSettings();
            st.BrowserSubprocessPath = Path.Combine(dir, "CefSharp.BrowserSubprocess.exe");
            st.ResourcesDirPath = dir;
            st.LocalesDirPath = Path.Combine(dir, "locales");
            st.CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AhzScribe", "cef-cache");
            st.LogSeverity = CefSharp.LogSeverity.Disable;
            st.CefCommandLineArgs.Add("disable-gpu", "1");
            st.CefCommandLineArgs.Add("disable-gpu-compositing", "1");
            if (Theme.Dark)
            {
                st.CefCommandLineArgs.Add("force-dark-mode", "1");
                st.CefCommandLineArgs.Add("enable-features", "WebContentsForceDark");
            }
            CefSharp.Cef.Initialize(st, true, (CefSharp.IBrowserProcessHandler)null);
            _cefReady = true;
            return true;
        }
        catch (Exception ex)
        {
            _cefFailed = true;
            err = ex.GetType().Name + ": " + ex.Message;
            try { Log.Audit("内置浏览器 · Chromium 初始化失败：" + err); } catch { }
            return false;
        }
    }

    public static void ShutdownCef()
    {
        try { if (_cefReady) CefSharp.Cef.Shutdown(); } catch { }
        _cefReady = false;
    }

    // ---------- 构造 ----------

    public AhzBrowser()
    {
        BackColor = Theme.Bg;
        DoubleBuffered = true;

        _back = Nav("←", "后退");
        _back.Click += delegate { NavBack(); };
        _fwd = Nav("→", "前进");
        _fwd.Click += delegate { NavForward(); };
        _reload = Nav("↻", "刷新");
        _reload.Click += delegate
        {
            try { BrowserTab t = Active(); if (t != null && t.Web != null) { t.Loaded = false; _lastText = ""; t.Web.Reload(); } }
            catch { }
        };
        _home = Nav("⌂", "新标签页");
        _home.Click += delegate { NewTab(StartPage()); };

        Addr = new UiEditor();
        Addr.BorderStyle = BorderStyle.None;
        Addr.Font = Theme.F(9.5f);
        Addr.BackColor = Theme.Card;
        Addr.ForeColor = Theme.Fg;
        Addr.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Go(Addr.Text);
        };
        Addr.TextChanged += delegate { UpdateSiteMark(Addr.Text); Invalidate(); };
        Controls.Add(Addr);

        _read = Chip("阅读模式", "把页面剥成只有正文的纯文本页");
        _read.Click += delegate { ToggleRead(); };

        _star = Chip("☆", "收藏这一页（存进书签栏）");
        _star.Click += delegate { AddBookmark(); };

        _ask = Chip("交给 AS", "把当前地址填进输入框，让助手用 web_open 读一遍");
        _ask.Click += delegate
        {
            string u = CurrentUrl;
            if (u.Length == 0) { Say("还没打开任何页面"); return; }
            if (AskAgent != null) AskAgent(u);
        };

        // 出口：站点拒绝嵌入 / 加载失败时，一键交给系统 Edge
        _edge = Chip("用 Edge 打开", "这个站点在窗口里打不开时，用系统的 Edge 打开同一个地址");
        _edge.FillColor = Color.White;
        _edge.HoverColor = Theme.AccentSoft;
        _edge.BorderColor = Theme.Accent;
        _edge.TextColor = Theme.Accent;
        _edge.Font = Theme.F(9.5f);
        _edge.Visible = false;
        _edge.Click += delegate { OpenInEdge(); };

        _hint = new Label();
        _hint.Font = Theme.F(10f);
        _hint.ForeColor = Theme.FgMid;
        _hint.BackColor = Theme.Bg;
        _hint.TextAlign = ContentAlignment.MiddleCenter;
        _hint.Visible = false;
        Controls.Add(_hint);

        _status = new Label();
        _status.Font = Theme.F(8.5f);
        _status.ForeColor = Theme.FgFaint;
        _status.BackColor = Theme.Bg;
        _status.Text = "输入网址或本地 html 路径，回车打开 · Ctrl+Shift+B 开关";
        Controls.Add(_status);

        // 加载失败时兜底判一次（现代站点偶尔连加载完成事件都不给）
        _blankTimer = new System.Windows.Forms.Timer();
        _blankTimer.Interval = 5000;
        _blankTimer.Tick += delegate { _blankTimer.Stop(); try { HintIfBlank(); } catch { } };

        LoadBookmarks();
        // 不在构造里建标签页：Chromium 首次初始化很重，推迟到"第一次打开浏览器"
    }

    // ---------- 小控件 ----------

    UiButton Nav(string text, string tip)
    {
        UiButton b = Chip(text, tip);
        b.Font = Theme.F(12f);
        return b;
    }

    UiButton Chip(string text, string tip)
    {
        UiButton b = new UiButton();
        b.Text = text;
        b.Font = Theme.F(9f);
        b.FillColor = Theme.Clear;
        b.HoverColor = Theme.Hover;
        b.BorderColor = Theme.Clear;
        b.TextColor = Theme.FgDim;
        b.Radius = Theme.Px(Theme.RadRow);
        if (tipsRef != null) tipsRef.SetToolTip(b, tip);
        Controls.Add(b);
        return b;
    }

    // ---------- 标签页 ----------

    public BrowserTab Active() { return (_active >= 0 && _active < _tabs.Count) ? _tabs[_active] : null; }

    public object Web { get { BrowserTab t = Active(); return t == null ? null : (object)t.Web; } }
    public bool Loaded { get { BrowserTab t = Active(); return t != null && t.Loaded; } }

    public string CurrentUrl
    {
        get
        {
            BrowserTab t = Active();
            if (t == null) return "";
            try { if (t.Web != null && !string.IsNullOrEmpty(t.Web.Address)) return t.Web.Address; } catch { }
            return t.Url;
        }
    }

    public BrowserTab NewTab(string url)
    {
        string err;
        if (!EnsureCef(out err)) { Say("Chromium 起不来：" + err); return null; }

        BrowserTab t = new BrowserTab();
        try { t.Web = new CefSharp.WinForms.ChromiumWebBrowser("about:blank"); }
        catch (Exception ex) { Say("建浏览器失败：" + Txt.FirstLine(ex.Message)); return null; }
        // 关键：ChromiumWebBrowser 自带停靠，放任它就会 Dock=Fill 摊满整块面板，
        // 于是把自绘的页签条 / 书签栏 / 地址框边框全盖住（用户截图里就是这样）。
        // 我们是用 Place() 手工摆位的，必须把停靠和锚点都关掉。
        t.Web.Dock = DockStyle.None;
        t.Web.Anchor = AnchorStyles.None;
        t.Web.Visible = false;
        BrowserTab cap = t;

        // Chromium 初始化是异步的：初始化完之前 LoadUrl 会被丢掉，所以先存"待发地址"
        t.Web.IsBrowserInitializedChanged += delegate
        {
            try
            {
                if (!cap.Web.IsBrowserInitialized) return;
                if (cap.Pending == null || cap.Pending.Length == 0) return;
                string u = cap.Pending;
                cap.Pending = "";
                try { Log.Audit("内置浏览器 · 初始化完成，导航到 " + u); } catch { }
                cap.Web.LoadUrl(u);
            }
            catch { }
        };

        t.Web.LoadingStateChanged += delegate(object s, CefSharp.LoadingStateChangedEventArgs e)
        {
            if (e.IsLoading) { cap.Loaded = false; return; }
            cap.Loaded = true;
            try { cap.Url = cap.Web.Address == null ? "" : cap.Web.Address; } catch { }
            try
            {
                if (cap.Url.Length > 0 && (cap.HistPos < 0 || cap.Hist[cap.HistPos] != cap.Url))
                {
                    while (cap.Hist.Count > cap.HistPos + 1) cap.Hist.RemoveAt(cap.Hist.Count - 1);
                    cap.Hist.Add(cap.Url);
                    cap.HistPos = cap.Hist.Count - 1;
                }
            }
            catch { }
            try { if (cap == Active()) { Addr.Text = cap.Url; } } catch { }
            try { if (cap == Active()) { _blankTimer.Stop(); KickTextCache(); } } catch { }
            Invalidate();
        };
        t.Web.TitleChanged += delegate(object s, CefSharp.TitleChangedEventArgs e)
        {
            cap.Title = string.IsNullOrEmpty(e.Title) ? "新标签页" : e.Title;
            Invalidate();
        };

        Controls.Add(t.Web);
        _tabs.Add(t);
        SetActive(_tabs.Count - 1);
        if (url != null && url.Length > 0) Go(url);
        Place();
        Invalidate();
        return t;
    }

    void SetActive(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        _active = i;
        for (int k = 0; k < _tabs.Count; k++)
        {
            try { if (_tabs[k].Web != null) _tabs[k].Web.Visible = (k == i); } catch { }
        }
        try { Addr.Text = _tabs[i].Url; } catch { }
        try { UpdateSiteMark(Addr.Text); } catch { }
        _lastText = "";
        Place();
        try { _blankTimer.Stop(); _blankTimer.Start(); } catch { }
        Invalidate();
    }

    void CloseTab(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        BrowserTab t = _tabs[i];
        _tabs.RemoveAt(i);
        try { Controls.Remove(t.Web); t.Web.Dispose(); } catch { }
        if (_tabs.Count == 0) { _active = -1; NewTab(StartPage()); return; }
        if (_active >= _tabs.Count) _active = _tabs.Count - 1;
        SetActive(_active);
    }

    Rectangle TabRect(int i)
    {
        int w = Math.Min(Theme.Px(210), Math.Max(Theme.Px(90), (Width - Theme.Px(44)) / Math.Max(1, _tabs.Count)));
        return new Rectangle(Theme.Px(6) + i * w, Theme.Px(6), Math.Max(Theme.Px(60), w - Theme.Px(4)), Theme.Px(30));
    }

    Rectangle PlusRect()
    {
        int w = Math.Min(Theme.Px(210), Math.Max(Theme.Px(90), (Width - Theme.Px(44)) / Math.Max(1, _tabs.Count)));
        return new Rectangle(Theme.Px(6) + _tabs.Count * w + Theme.Px(2), Theme.Px(8), Theme.Px(24), Theme.Px(24));
    }

    Rectangle CloseRect(Rectangle tab) { return new Rectangle(tab.Right - Theme.Px(24), tab.Y + Theme.Px(7), Theme.Px(16), Theme.Px(16)); }

    // ---------- 导航 ----------

    public string StartPage()
    {
        string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser-home.html");
        try
        {
            if (!File.Exists(p))
            {
                File.WriteAllText(p,
                    "<!doctype html><meta charset=\"utf-8\"><title>Ahz Scribe · 新标签页</title>"
                    + "<body style=\"font:15px/1.8 'Microsoft YaHei',sans-serif;color:#1f2023;margin:64px auto;max-width:660px\">"
                    + "<h1 style=\"font-size:26px;margin-bottom:4px\">新标签页</h1>"
                    + "<p style=\"color:#8a8f98;margin-top:0\">Ahz Scribe 内置浏览器 · Chromium 内核</p>"
                    + "<p>① 地址栏输入网址或本地 html 路径，回车打开；页签条上 + 开新标签。</p>"
                    + "<p>② 交给 AS：把当前地址丢给助手，它会读一遍再讲给你。</p>"
                    + "<p>③ 阅读模式：把页面剥成只有正文的纯文本页。</p>"
                    + "<p>④ 助手动页面时，顶上会亮一条强调色，状态栏写它在干什么。</p>"
                    + "</body>", new UTF8Encoding(false));
            }
        }
        catch { }
        string uri = new Uri(p).AbsoluteUri;
        if (Theme.Dark) uri += "?dark=1";
        return uri;
    }

    public void Go(string target)
    {
        string t = target == null ? "" : target.Trim();
        if (t.Length == 0) return;
        if (!Regex.IsMatch(t, "^[a-zA-Z][a-zA-Z0-9+.\\-]*://") && !t.StartsWith("about:"))
        {
            try
            {
                if (File.Exists(t)) t = new Uri(Path.GetFullPath(t)).AbsoluteUri;
                else if (Regex.IsMatch(t, "^(localhost|127\\.|10\\.|192\\.168\\.)")) t = "http://" + t;
                else t = "https://" + t;
            }
            catch { return; }
        }
        BrowserTab tab = Active();
        if (tab == null) { NewTab(t); return; }
        _reading = false;
        _lastText = "";
        tab.Loaded = false;
        try { _blankTimer.Stop(); _blankTimer.Start(); } catch { }
        try
        {
            bool ready = false;
            try { ready = tab.Web != null && tab.Web.IsBrowserInitialized; } catch { }
            if (ready) { tab.Pending = ""; tab.Web.LoadUrl(t); }
            else { tab.Pending = t; }          // 初始化完成事件里再发
            try { Log.Audit("内置浏览器 · 导航 " + t + (ready ? "（立即）" : "（排队等初始化）")); } catch { }
        }
        catch (Exception ex) { tab.Loaded = true; Say("打不开：" + Txt.FirstLine(ex.Message)); }
    }

    // ---------- 读页面（工具用；工作线程上可以阻塞等，UI 线程只给缓存） ----------

    public string EvalJs(string script, int timeoutMs)
    {
        BrowserTab t = Active();
        if (t == null || t.Web == null) return null;
        try
        {
            System.Threading.Tasks.Task<CefSharp.JavascriptResponse> task =
                t.Web.GetBrowser().MainFrame.EvaluateScriptAsync(script);
            if (!task.Wait(timeoutMs)) return null;
            CefSharp.JavascriptResponse r = task.Result;
            if (r == null || !r.Success) return null;
            return r.Result == null ? "" : Convert.ToString(r.Result);
        }
        catch { return null; }
    }

    // 加载完成后把正文异步缓存一份（UI 线程上不能阻塞等 JS，CEF 的结果要靠 UI 线程回填）
    void KickTextCache()
    {
        BrowserTab t = Active();
        if (t == null || t.Web == null) return;
        BrowserTab cap = t;
        try
        {
            cap.Web.GetBrowser().MainFrame.EvaluateScriptAsync(BodyTextJs).ContinueWith(
                delegate(System.Threading.Tasks.Task<CefSharp.JavascriptResponse> tr)
                {
                    try
                    {
                        CefSharp.JavascriptResponse r = tr.Result;
                        if (r != null && r.Success) _lastText = r.Result == null ? "" : Convert.ToString(r.Result);
                    }
                    catch { }
                    try
                    {
                        if (Active() == cap) BeginInvoke(new MethodInvoker(delegate { try { HintIfBlank(); } catch { } }));
                    }
                    catch { }
                });
        }
        catch { }
    }

    public string ReadText()
    {
        BrowserTab t = Active();
        if (t == null) return "";
        if (!InvokeRequired) return _lastText;          // UI 线程：只给缓存，绝不阻塞
        string r = EvalJs(BodyTextJs, 8000);
        if (r != null) _lastText = r;
        return _lastText;
    }

    public string ReadCache { get { return _lastText; } }

    string ReadTextNow()
    {
        string r = EvalJs(BodyTextJs, 8000);
        if (r != null) _lastText = r;
        return _lastText;
    }

    void ToggleRead()
    {
        BrowserTab t = Active();
        if (t == null || t.Web == null) return;
        if (!_reading)
        {
            _readingUrl = CurrentUrl;
            string body = ReadTextNow();
            if (body == null || body.Trim().Length == 0) { Say("这一页没有可摘的正文（可能要登录或是纯图）"); return; }
            _reading = true;
            string html = "<!doctype html><meta charset=\"utf-8\"><title>阅读模式</title>"
                + "<body style=\"font:16px/1.9 'Microsoft YaHei',serif;color:#24262b;margin:48px auto;max-width:680px\">"
                + "<div style=\"font-size:12px;color:#8a8f98;letter-spacing:.08em;margin-bottom:20px\">阅读模式 · 只留正文</div>"
                + "<pre style=\"white-space:pre-wrap;font:inherit;margin:0\">" + HtmlEsc(body) + "</pre></body>";
            try
            {
                t.Loaded = false;
                t.Web.LoadUrl("data:text/html;charset=utf-8," + Uri.EscapeDataString(html));
                Say("阅读模式 · 已剥掉导航/广告/侧栏");
            }
            catch { _reading = false; }
            return;
        }
        _reading = false;
        Go(_readingUrl);
    }

    static string HtmlEsc(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    void HintIfBlank()
    {
        string u = CurrentUrl;
        bool http = u.StartsWith("http");
        bool bare = http && _lastText.Trim().Length < 40 && !_reading;
        // 不再画那个大白框：它会盖住页面本身（豆包那张就是这么被糊住的）。
        // 只在状态栏说一句，并把「用 Edge 打开」常驻在右下角。
        _hint.Visible = false;
        _edge.Visible = true;
        if (bare) Say("这一页没读到正文（可能要登录/拒绝嵌入）· 可用「用 Edge 打开」");
        Place();
    }

    // 后退 / 前进：用自己记的历史（成功返回目标地址，失败返回一句原因）
    public string NavBack()
    {
        BrowserTab t = Active();
        if (t == null) return "没有打开的页面。";
        if (t.HistPos <= 0) return "已经是第一页了，没有可后退的。";
        t.HistPos--;
        string target = t.Hist[t.HistPos];
        t.Loaded = false;
        try { t.Web.LoadUrl(target); } catch (Exception ex) { return "后退失败：" + ex.Message; }
        return target;
    }

    public string NavForward()
    {
        BrowserTab t = Active();
        if (t == null) return "没有打开的页面。";
        if (t.HistPos >= t.Hist.Count - 1) return "已经在最后一页了，没有可前进的。";
        t.HistPos++;
        string target = t.Hist[t.HistPos];
        t.Loaded = false;
        try { t.Web.LoadUrl(target); } catch (Exception ex) { return "前进失败：" + ex.Message; }
        return target;
    }

    public void OpenInEdge()
    {
        string u = CurrentUrl;
        if (u.Length == 0) { Say("还没打开任何页面"); return; }
        try
        {
            System.Diagnostics.Process.Start("msedge.exe", u);
            Say("已交给系统 Edge 打开");
        }
        catch
        {
            try { System.Diagnostics.Process.Start(u); Say("已交给默认浏览器打开"); }
            catch { Say("叫不起浏览器，请手动复制地址"); }
        }
    }

    // ---------- 书签栏 ----------

    void LoadBookmarks()
    {
        string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser-bookmarks.txt");
        try
        {
            if (!File.Exists(p))
                File.WriteAllText(p,
                    "有道翻译|https://fanyi.youdao.com\n"
                    + "GitHub|https://github.com\n"
                    + "DeepSeek|https://chat.deepseek.com\n"
                    + "豆包|https://www.doubao.com\n"
                    + "文心一言|https://yiyan.baidu.com\n"
                    + "千问|https://tongyi.aliyun.com\n", new UTF8Encoding(false));
            foreach (string ln in File.ReadAllLines(p, Encoding.UTF8))
            {
                string s = ln.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                int bar = s.IndexOf('|');
                if (bar <= 0) continue;
                _marks.Add(new string[] { s.Substring(0, bar).Trim(), s.Substring(bar + 1).Trim() });
            }
        }
        catch { }
    }

    void AddBookmark()
    {
        string u = CurrentUrl;
        if (u.Length == 0) { Say("这一页还没地址，收藏不了"); return; }
        string name = u;
        BrowserTab t = Active();
        if (t != null && t.Title.Length > 0) name = t.Title;
        if (name.Length > 16) name = name.Substring(0, 16);
        for (int i = 0; i < _marks.Count; i++)
            if (_marks[i][1] == u) { Say("这一页已经在书签栏里了"); return; }
        _marks.Add(new string[] { name, u });
        try
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "browser-bookmarks.txt");
            File.WriteAllText(p, string.Join("\n", MarkLines()) + "\n", new UTF8Encoding(false));
        }
        catch { }
        Say("已收藏到书签栏：" + name);
        Invalidate();
    }

    string[] MarkLines()
    {
        string[] r = new string[_marks.Count];
        for (int i = 0; i < _marks.Count; i++) r[i] = _marks[i][0] + "|" + _marks[i][1];
        return r;
    }

    Rectangle MarkRect(int i, int x)
    {
        int w = Ui.TextWidth(_marks[i][0], Theme.F(9f)) + Theme.Px(20);
        return new Rectangle(x, StripHP + ToolHP + Theme.Px(6), w, Theme.Px(21));
    }

    // 地址栏左侧的站点身份：https 锁 / http 警告 / 本地文件 / 未知。
    // 只提示当前地址的协议，证书校验仍由 Chromium 负责。
    void UpdateSiteMark(string url)
    {
        string u = (url ?? "").Trim();
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _siteMark = "🔒";
            _siteColor = Theme.Ok;
            if (tipsRef != null) tipsRef.SetToolTip(Addr, "HTTPS 加密连接（证书由 Chromium 校验）");
        }
        else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            _siteMark = "⚠";
            _siteColor = Theme.Warn;
            if (tipsRef != null) tipsRef.SetToolTip(Addr, "HTTP 明文连接：不要在这个页面输入密码或密钥");
        }
        else if (u.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            _siteMark = "▤";
            _siteColor = Theme.FgFaint;
            if (tipsRef != null) tipsRef.SetToolTip(Addr, "本地 HTML 文件");
        }
        else
        {
            _siteMark = "•";
            _siteColor = Theme.FgFaint;
            if (tipsRef != null) tipsRef.SetToolTip(Addr, "输入网址或本地 html 路径，回车打开");
        }
    }

    public void Say(string s) { try { _status.Text = s; _status.ForeColor = Theme.FgFaint; Invalidate(); } catch { } }
    public void AgentSay(string s) { try { _status.Text = s; _status.ForeColor = Theme.Accent; Invalidate(); } catch { } }
    public void AgentDone(string s) { Say(s); }
    public void FocusAddress() { try { Addr.Focus(); Addr.SelectAll(); } catch { } }

    // ---------- 排版 ----------

    void Place()
    {
        if (Addr == null) return;
        int boxH = Theme.Px(28);
        int boxY = StripHP + Theme.Px(8);
        int midY = boxY + boxH / 2;

        int btn = Theme.Px(26);
        int btnY = midY - btn / 2;
        int x = Theme.Px(8);
        UiButton[] navs = new UiButton[] { _back, _fwd, _reload, _home };
        for (int i = 0; i < navs.Length; i++)
        {
            navs[i].SetBounds(x, btnY, btn, btn);
            x += btn + Theme.Px(2);
        }
        x += Theme.Px(8);

        int rw = Theme.Px(74);
        int gap = Theme.Px(6);
        int right = Theme.Px(8);
        _ask.SetBounds(Width - right - rw, btnY, rw, btn);
        right += rw + gap;
        _read.SetBounds(Width - right - rw, btnY, rw, btn);
        right += rw + gap;
        int sw = Theme.Px(32);
        _star.SetBounds(Width - right - sw, btnY, sw, btn);
        right += sw + gap;

        int addrW = Math.Max(Theme.Px(120), Width - x - right - Theme.Px(8));
        AddrBox = new Rectangle(x, boxY, addrW, boxH);
        int pad = Theme.Px(12);
        int siteW = Theme.Px(22);
        Addr.SetBounds(AddrBox.Left + pad + siteW, AddrBox.Top,
            Math.Max(Theme.Px(60), AddrBox.Width - pad * 2 - siteW), Addr.Height);
        Addr.Top = AddrBox.Top + (AddrBox.Height - Addr.Height) / 2;

        int top = StripHP + ToolHP + (_marks.Count > 0 ? MarkHP : 0);
        int statusH = Theme.Px(22);
        for (int i = 0; i < _tabs.Count; i++)
        {
            BrowserTab tb = _tabs[i];
            if (tb == null || tb.Web == null) continue;
            tb.Web.SetBounds(0, top + Theme.Px(2), Width, Math.Max(Theme.Px(40), Height - top - statusH - Theme.Px(4)));
            if (i == _active)
            {
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                if (now - _placedLogged > 2)
                {
                    _placedLogged = now;
                    try
                    {
                        Log.Audit("浏览器布局 · 面板=" + Width + "x" + Height + " top=" + top
                            + " web=" + tb.Web.Bounds + " dock=" + tb.Web.Dock
                            + " 书签=" + _marks.Count + " tabs=" + _tabs.Count);
                    }
                    catch { }
                }
            }
        }
        bool edgeOn = _edge != null && _edge.Visible;
        _status.SetBounds(Theme.Px(12), Height - statusH,
            Math.Max(Theme.Px(60), Width - Theme.Px(24) - (edgeOn ? Theme.Px(136) : 0)), statusH - Theme.Px(2));
        if (_hint != null)
            _hint.SetBounds(Theme.Px(40), top + Theme.Px(40), Math.Max(Theme.Px(120), Width - Theme.Px(80)),
                Math.Max(Theme.Px(40), (Height - top - statusH) / 2));
        // 出口按钮放在底栏，不再压住网页右下角的内容（原来会盖住首页第 7 个快捷入口）。
        if (edgeOn)
            _edge.SetBounds(Width - Theme.Px(136), Height - statusH + Theme.Px(1),
                Theme.Px(128), Math.Max(Theme.Px(18), statusH - Theme.Px(2)));
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Place();
    }

    // ---------- 自绘外壳 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(Theme.ChipHover)) g.FillRectangle(b, 0, 0, Width, StripHP);
        for (int i = 0; i < _tabs.Count; i++)
        {
            Rectangle r = TabRect(i);
            bool on = (i == _active);
            int tabH = on ? Math.Max(r.Height, StripHP - r.Y) : r.Height;
            using (GraphicsPath p = Ui.Round(new Rectangle(r.X, r.Y, r.Width, tabH), Theme.Px(8)))
            {
                using (SolidBrush b = new SolidBrush(on ? Theme.Bg : (_hotTab == i ? Theme.Hover : Theme.Clear))) g.FillPath(b, p);
            }
            Rectangle ic = new Rectangle(r.X + Theme.Px(9), r.Y + Theme.Px(9), Theme.Px(14), Theme.Px(14));
            using (GraphicsPath ip = Ui.Round(ic, Theme.Px(4)))
            using (SolidBrush ib = new SolidBrush(on ? Theme.AccentSoft : Theme.Card)) g.FillPath(ib, ip);
            string mono = _tabs[i].Title.Length > 0 ? _tabs[i].Title.Substring(0, 1) : "·";
            TextRenderer.DrawText(g, mono, Theme.F(7.5f), ic, Theme.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int tw = r.Width - Theme.Px(52);
            TextRenderer.DrawText(g, _tabs[i].Title, Theme.F(9f),
                new Rectangle(ic.Right + Theme.Px(7), r.Y, Math.Max(Theme.Px(20), tw), r.Height),
                on ? Theme.Fg : Theme.FgMid,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            Rectangle cr = CloseRect(r);
            if (_hotClose == i)
                using (GraphicsPath cp = Ui.Round(cr, Theme.Px(4)))
                using (SolidBrush cb = new SolidBrush(Theme.Hover)) g.FillPath(cb, cp);
            TextRenderer.DrawText(g, "×", Theme.F(9f), cr, Theme.FgFaint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        Rectangle pr = PlusRect();
        if (_hotTab == -2)
            using (GraphicsPath pp = Ui.Round(pr, Theme.Px(6)))
            using (SolidBrush pb = new SolidBrush(Theme.Hover)) g.FillPath(pb, pp);
        TextRenderer.DrawText(g, "+", Theme.F(13f), pr, Theme.FgMid,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        int toolY = StripHP;
        using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 0, toolY, Width, ToolHP + (_marks.Count > 0 ? MarkHP : 0));
        Rectangle box = AddrBox;
        if (box.Width > Theme.Px(20))
        {
            using (GraphicsPath bp = Ui.Round(box, Theme.Px(box.Height / 2)))
            {
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, bp);
                using (Pen p = new Pen(Theme.Line, Theme.Pxf(1f))) g.DrawPath(p, bp);
            }
            TextRenderer.DrawText(g, _siteMark, Theme.F(10f),
                new Rectangle(box.Left + Theme.Px(8), box.Top, Theme.Px(22), box.Height),
                _siteColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        if (_marks.Count > 0)
        {
            int mx = Theme.Px(10), my = toolY + ToolHP;
            for (int i = 0; i < _marks.Count; i++)
            {
                Rectangle mr = MarkRect(i, mx);
                if (mr.Right > Width - Theme.Px(8)) break;
                if (_hotMark == i)
                    using (GraphicsPath mp = Ui.Round(mr, Theme.Px(6)))
                    using (SolidBrush mb = new SolidBrush(Theme.Hover)) g.FillPath(mb, mp);
                TextRenderer.DrawText(g, _marks[i][0], Theme.F(9f), mr, Theme.FgMid,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                mx = mr.Right + Theme.Px(4);
            }
        }
        if (AgentDriving)
            using (SolidBrush b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, 0, 0, Width, Theme.Px(3));
    }

    // ---------- 鼠标 ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hotTab = PlusRect().Contains(e.Location) ? -2 : -1;
        int hotClose = -1, hotMark = -1;
        for (int i = 0; i < _tabs.Count; i++)
        {
            Rectangle r = TabRect(i);
            if (!r.Contains(e.Location)) continue;
            hotTab = i;
            if (CloseRect(r).Contains(e.Location)) hotClose = i;
            break;
        }
        int mx = Theme.Px(10), my = StripHP + ToolHP;
        for (int i = 0; i < _marks.Count; i++)
        {
            Rectangle mr = MarkRect(i, mx);
            mx = mr.Right + Theme.Px(4);
            if (e.Y >= my && mr.Contains(e.Location)) { hotMark = i; break; }
        }
        if (hotTab != _hotTab || hotClose != _hotClose || hotMark != _hotMark)
        {
            _hotTab = hotTab; _hotClose = hotClose; _hotMark = hotMark;
            Cursor = (hotTab >= 0 || hotClose >= 0 || hotMark >= 0) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hotTab = -1; _hotClose = -1; _hotMark = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_marks.Count > 0 && e.Y >= StripHP + ToolHP)
        {
            int mx = Theme.Px(10);
            for (int i = 0; i < _marks.Count; i++)
            {
                Rectangle mr = MarkRect(i, mx);
                if (mr.Contains(e.Location)) { Go(_marks[i][1]); return; }
                mx = mr.Right + Theme.Px(4);
            }
        }
        for (int i = 0; i < _tabs.Count; i++)
        {
            Rectangle r = TabRect(i);
            if (!r.Contains(e.Location)) continue;
            if (CloseRect(r).Contains(e.Location)) CloseTab(i);
            else SetActive(i);
            return;
        }
        if (PlusRect().Contains(e.Location)) { NewTab(StartPage()); return; }
    }

    // ---------- 截图（UI 线程只发起，等工作线程去等结果，别在 UI 线程上阻塞） ----------

    public System.Threading.Tasks.Task<byte[]> BeginScreenshot()
    {
        BrowserTab t = Active();
        if (t == null || t.Web == null) return null;
        try { return t.Web.CaptureScreenshotAsync(); }
        catch { return null; }
    }
}

// ============================ 助手那半边：它自己开页、读 DOM、点链接 ============================
// 和"人手动点"的根本区别（Claude / Codex 那种浏览器）：面板只有一个。
// 助手操作它就等于用户眼前那个页面在动 —— 不是两套管道、不是无头另开一份。
static class BrowserHub
{
    public static AhzBrowser Panel;
    public static Action ShowPanel;

    static Control Marshal { get { return Panel == null ? null : (Panel.FindForm() as Control); } }

    // 回到 UI 线程（句柄用主窗口的：面板自己还没句柄时 InvokeRequired 会骗人）
    static string CallOn(Control c, Func<string> fn, int timeoutMs)
    {
        if (c == null) return "内置浏览器不可用（找不到主窗口）。";
        if (!c.InvokeRequired)
        {
            try { return fn(); }
            catch (Exception ex) { return "内置浏览器操作失败：" + ex.Message; }
        }
        string result = null;
        Exception err = null;
        IAsyncResult ar = c.BeginInvoke((MethodInvoker)delegate
        {
            try { result = fn(); }
            catch (Exception ex) { err = ex; }
        });
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return "__TIMEOUT__";
        if (err != null) return "内置浏览器操作失败：" + err.Message;
        return result == null ? "" : result;
    }

    static void Ui(Control c, MethodInvoker act)
    {
        try { if (c == null || c.IsDisposed) return; if (c.InvokeRequired) c.BeginInvoke(act); else act(); }
        catch { }
    }

    static string OnUi(string busy, string done, Func<string> fn, int timeoutMs)
    {
        AhzBrowser p = Panel;
        if (p == null) return "内置浏览器还没建起来（主窗口没初始化完）。";
        Control m = Marshal;
        if (m == null) m = p;
        Ui(m, delegate { if (ShowPanel != null) ShowPanel(); });
        Ui(m, delegate { p.AgentDriving = true; p.AgentSay(busy); });
        try
        {
            string r = CallOn(m, fn, timeoutMs);
            if (r == "__TIMEOUT__") return "内置浏览器操作超时（" + (timeoutMs / 1000) + " 秒没做完）。";
            return r;
        }
        finally
        {
            Ui(m, delegate { p.AgentDriving = false; p.AgentDone(done); });
        }
    }

    static bool WaitLoaded(AhzBrowser p, int seconds)
    {
        if (p.InvokeRequired == false) return p.Loaded;
        DateTime end = DateTime.Now.AddSeconds(Math.Min(seconds, 20));
        while (DateTime.Now < end)
        {
            if (p.Loaded) return true;
            System.Threading.Thread.Sleep(80);
        }
        return false;
    }

    static string Title(AhzBrowser p)
    {
        BrowserTab t = p.Active();
        return t == null ? "" : (t.Title == null ? "" : t.Title);
    }

    public static string Open(string target)
    {
        string t = target == null ? "" : target.Trim();
        if (t.Length == 0) return "缺参数：url 是空的。";
        string url = t;
        string nav = OnUi("正在打开 " + t, "已打开 " + t, delegate
        {
            AhzBrowser p = Panel;
            if (p.Active() == null) p.NewTab(url);      // 第一次打开：这里才建 Chromium
            else p.Go(url);
            return "";
        }, 20000);
        if (nav.StartsWith("内置浏览器")) return nav;
        WaitLoaded(Panel, 20);
        string text = Panel.ReadText();
        if (text.Length > 600) text = text.Substring(0, 600) + "…";
        return "已在内置浏览器打开（用户也能看到）。\n标题：" + Title(Panel)
            + "\n地址：" + Panel.CurrentUrl + (text.Length > 0 ? "\n正文开头：\n" + text : "\n（正文是空的：可能要登录、是纯图，或站点拒绝被嵌入）");
    }

    public static string Read(int max)
    {
        AhzBrowser p = Panel;
        if (p == null) return "内置浏览器还没建起来。";
        if (ShowPanel != null) { try { ShowPanel(); } catch { } }
        Ui(Marshal, delegate { p.AgentDriving = true; p.AgentSay("正在读页面正文"); });
        try
        {
            string t = p.ReadText();
            if (t == null || t.Length == 0) return "这一页没有可读的正文（可能要登录、是纯图，或还没加载完）。";
            if (max <= 0) max = 3000;
            if (t.Length > max) t = t.Substring(0, max) + "\n…（还有 " + (t.Length - max) + " 字没显示）";
            return "标题：" + Title(p) + "\n地址：" + p.CurrentUrl + "\n正文：\n" + t;
        }
        finally { Ui(Marshal, delegate { p.AgentDriving = false; p.AgentDone("已读取"); }); }
    }

    public static string Links(int max)
    {
        if (max <= 0) max = 40;
        AhzBrowser p = Panel;
        if (p == null) return "内置浏览器还没建起来。";
        if (ShowPanel != null) { try { ShowPanel(); } catch { } }
        string js = "(function(){var out=[];var as=document.querySelectorAll('a');"
            + "for(var i=0;i<as.length&&out.length<" + max + ";i++){var a=as[i];"
            + "var t=(a.innerText||'').replace(/\\s+/g,' ').trim();var h=a.href||'';"
            + "if(!t&&!h)continue;out.push(t+'|||'+h);}return out.join('\\n');})()";
        string r = p.EvalJs(js, 10000);
        if (r == null) return "拿不到页面链接（页面没加载完，或这个页面没有可读的 DOM）。";
        if (r.Trim().Length == 0) return "这一页没有链接。";
        StringBuilder sb = new StringBuilder();
        string[] lines = r.Split('\n');
        int n = 0;
        for (int i = 0; i < lines.Length && n < max; i++)
        {
            string ln = lines[i].Trim();
            if (ln.Length == 0) continue;
            string[] parts = ln.Split(new string[] { "|||" }, StringSplitOptions.None);
            string txt = parts.Length > 0 ? parts[0] : "";
            string href = parts.Length > 1 ? parts[1] : "";
            if (txt.Length > 60) txt = txt.Substring(0, 60) + "…";
            sb.Append("  [" + (n + 1) + "] " + (txt.Length > 0 ? txt : "(无文字)") + "  →  " + href + "\n");
            n++;
        }
        return "列了 " + n + " 条链接：\n" + sb;
    }

    static string JsStr(string s)
    {
        if (s == null) return "''";
        StringBuilder b = new StringBuilder("'");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\') b.Append("\\\\");
            else if (c == '\'') b.Append("\\'");
            else if (c == '\n') b.Append("\\n");
            else if (c == '\r') b.Append("\\r");
            else b.Append(c);
        }
        b.Append("'");
        return b.ToString();
    }

    public static string Click(string text)
    {
        string want = text == null ? "" : text.Trim();
        if (want.Length == 0) return "缺参数：text 是空的。先 browse_links 看清有哪些链接。";
        AhzBrowser p = Panel;
        if (p == null) return "内置浏览器还没建起来。";
        if (ShowPanel != null) { try { ShowPanel(); } catch { } }
        Ui(Marshal, delegate { p.AgentDriving = true; p.AgentSay("正在点「" + want + "」"); });
        try
        {
            string js = "(function(){var want=" + JsStr(want) + ".toLowerCase();"
                + "var as=document.querySelectorAll('a,button,input[type=button],input[type=submit]');"
                + "for(var i=0;i<as.length;i++){var a=as[i];"
                + "var t=((a.innerText||a.value||'')+'').replace(/\\s+/g,' ').trim();"
                + "if(t&&t.toLowerCase().indexOf(want)>=0){a.click();return 'OK:'+a.tagName;}"
                + "}return 'NF';})()";
            string r = p.EvalJs(js, 10000);
            if (r == null) return "点不动（页面没加载完，或这个页面不允许脚本操作）。";
            if (r == "NF") return "页面上没找到写着「" + want + "」的链接或按钮。先用 browse_links 看看能点哪些。";
            if (!r.StartsWith("OK:")) return "点击返回了意外结果：" + r;
            WaitLoaded(p, 15);
            string body = p.ReadText();
            if (body.Length > 400) body = body.Substring(0, 400) + "…";
            return "已点击 <" + r.Substring(3) + ">「" + want + "」。\n现在页面：" + Title(p)
                + "\n地址：" + p.CurrentUrl + "\n正文开头：\n" + body;
        }
        finally { Ui(Marshal, delegate { p.AgentDriving = false; p.AgentDone("已点击「" + want + "」"); }); }
    }

    public static string Back()
    {
        AhzBrowser pp = Panel;
        if (pp == null) return "内置浏览器还没建起来。";
        string r = OnUi("正在后退", "已后退", delegate { return pp.NavBack(); }, 15000);
        if (r == null || r.Length == 0) return "后退失败：没有返回目标地址。";
        if (r.StartsWith("已经") || r.StartsWith("没有") || r.StartsWith("后退失败")) return r;
        string target = r;
        if (pp.InvokeRequired)
        {
            DateTime end = DateTime.Now.AddSeconds(12);
            while (DateTime.Now < end)
            {
                string cur = pp.EvalJs("location.href", 4000);
                if (cur != null && (cur == target || cur.StartsWith(target))) break;
                System.Threading.Thread.Sleep(150);
            }
        }
        WaitLoaded(pp, 8);
        return "已后退。现在页面：" + Title(pp) + "\n地址：" + pp.CurrentUrl;
    }

    public static string Shot(string path)
    {
        string target = path == null ? "" : path.Trim();
        if (target.Length == 0)
            target = Path.Combine(Environment.CurrentDirectory,
                "browser-shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
        AhzBrowser p = Panel;
        if (p == null) return "内置浏览器还没建起来。";
        if (ShowPanel != null) { try { ShowPanel(); } catch { } }

        // 截图任务必须在 UI 线程发起，但"等结果"要在工作线程上等（UI 线程上一等就死锁）
        System.Threading.Tasks.Task<byte[]> task = null;
        string start = CallOn(Marshal, delegate { task = p.BeginScreenshot(); return task == null ? "NULL" : "OK"; }, 10000);
        if (start != "OK") return start == "NULL" ? "截图失败：没有可截的页面。" : start;
        try
        {
            if (!task.Wait(20000)) return "截图超时（20 秒没拿到图）。";
            byte[] bytes = task.Result;
            if (bytes == null || bytes.Length < 100) return "截图失败：Chromium 没给图。";
            File.WriteAllBytes(target, bytes);      // CefSharp 直接给 PNG 字节
        }
        catch (Exception ex) { return "截图失败：" + ex.Message; }
        return "已截图（Chromium CaptureScreenshot）：" + target;
    }

    public static void ReportLoaded()
    {
        AhzBrowser p = Panel;
        if (p == null) return;
        Ui(Marshal, delegate { try { p.Say("已加载 · " + Title(p)); } catch { } });
    }
}

// ============================ 界面看门狗 ============================
// 卡死是"偶发、现场一关就没了"的 bug，光靠复现永远抓不到。这个线程每 2 秒问 UI 线程一次
// "还活着吗"：4 秒没回应就把时间点、卡了多久、当时浏览器开没开写进 Ahz错误日志.txt。
static class UiWatchdog
{
    static Control _host;
    static long _lastBeat;

    public static void Start(Control host)
    {
        if (_host != null) return;
        _host = host;
        System.Threading.Thread t = new System.Threading.Thread(Loop);
        t.IsBackground = true;
        t.Name = "ui-watchdog";
        t.Start();
    }

    static void Loop()
    {
        while (true)
        {
            System.Threading.Thread.Sleep(2000);
            try
            {
                Control h = _host;
                if (h == null || h.IsDisposed) return;
                System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false);
                DateTime t0 = DateTime.Now;
                h.BeginInvoke(new MethodInvoker(delegate { try { done.Set(); } catch { } }));
                if (!done.WaitOne(4000))
                {
                    double sec = (DateTime.Now - t0).TotalSeconds;
                    long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                    if (now - _lastBeat > 30)
                    {
                        _lastBeat = now;
                        bool br = BrowserHub.Panel != null && BrowserHub.Panel.Visible;
                        Log.Audit("⚠ 界面卡顿 " + sec.ToString("0.0") + "s（看门狗）· 浏览器=" + (br ? "开" : "关")
                            + " · 检查: Chromium 初始化/页面加载/读DOM/截图");
                    }
                }
            }
            catch { }
        }
    }
}

// 把这六件事挂成工具：名字都用 browse_ 开头，和插件的 web_open（无头）分开，
// 一眼能看出"这个会开真窗口、用户看得见"
static class BrowserTools
{
    public static List<ToolDef> All()
    {
        List<ToolDef> list = new List<ToolDef>();
        list.Add(new ToolDef("browse_open",
            "在内置浏览器（Chromium 内核）里打开网址，会弹出可见窗口，用户和你看到的是同一个页面。本地 html 路径也行。",
            false, delegate(ToolContext c, Dictionary<string, object> a) { return BrowserHub.Open(A.Str(a, "url", "")); })
            .Prop("url", "string", "网址或本地 html 路径", true));

        list.Add(new ToolDef("browse_read",
            "读内置浏览器当前页面的正文（纯文本）。想读细节用它，不要整页倒进上下文。",
            false, delegate(ToolContext c, Dictionary<string, object> a) { return BrowserHub.Read(A.Int(a, "max_chars", 3000)); })
            .Prop("max_chars", "integer", "最多返回多少字，默认 3000", false));

        list.Add(new ToolDef("browse_links",
            "列出内置浏览器当前页面上的链接（序号 + 文字 + 地址）。要进下一页前先用它看清入口。",
            false, delegate(ToolContext c, Dictionary<string, object> a) { return BrowserHub.Links(A.Int(a, "max_items", 40)); })
            .Prop("max_items", "integer", "最多列多少条，默认 40", false));

        list.Add(new ToolDef("browse_click",
            "按文字点击内置浏览器当前页面上的链接/按钮（用户同时看得见）。先 browse_links 看清再点。",
            false, delegate(ToolContext c, Dictionary<string, object> a) { return BrowserHub.Click(A.Str(a, "text", "")); })
            .Prop("text", "string", "链接或按钮上的文字，可只给一部分", true));

        list.Add(new ToolDef("browse_back", "内置浏览器后退一页。", false,
            delegate(ToolContext c, Dictionary<string, object> a) { return BrowserHub.Back(); }));

        list.Add(new ToolDef("browse_shot",
            "把内置浏览器当前画面截成 png（给用户看，也给你自己看）。不填路径就自动命名。",
            false, delegate(ToolContext c, Dictionary<string, object> a) { return BrowserHub.Shot(A.Str(a, "path", "")); })
            .Prop("path", "string", "png 保存路径，不填自动命名", false));
        return list;
    }
}

// ============================ 自我迭代「改造申请单」（GUI 用） ============================
// 智能体发现自己能力不够时，用它把方案摆到用户面前：改造内容 / 影响范围 / 风险 / 预期效果 / 怎么验证。
// 用户点「批准执行」或「驳回」；批准后框架要求它按固定四步走（改代码 → build → 自测 → 升级重启），
// 任何一步失败就回滚（源码走改动前快照，二进制走上一版 exe）。
class ProposalForm : Form
{
    public int Decision = 0;      // 1 = 批准执行 / 0 = 驳回
    private UiDiff _body;          // 只读正文（自测/取证用）

    public ProposalForm(string title, string body)
    {
        Theme.EnsureDpi();
        AutoScaleMode = AutoScaleMode.None;
        Text = "自我迭代改造申请";
        Icon = Theme.AppIcon();
        Size = new Size(Theme.Px(860), Theme.Px(640) + Theme.Px(40));
        MinimumSize = new Size(Theme.Px(660), Theme.Px(480) + Theme.Px(40));
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(0, Theme.Px(40), 0, 0);
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
        ok.Radius = Theme.RadCard;
        ok.Font = Theme.F(9.5f);
        ok.Size = new Size(Theme.Px(118), Theme.Px(36));
        ok.Click += delegate { Decision = 1; Close(); };
        bottom.Controls.Add(ok);
        UiButton no = new UiButton();
        no.Text = "驳回";
        no.FillColor = Color.White;
        no.BorderColor = Theme.Line;
        no.HoverColor = Theme.Hover;
        no.Radius = Theme.RadCard;
        no.Font = Theme.F(9.5f);
        no.Size = new Size(Theme.Px(88), Theme.Px(36));
        no.Click += delegate { Decision = 0; Close(); };
        bottom.Controls.Add(no);
        Label hint = new Label();
        hint.Text = "Enter 驳回 · 批准后：① 改代码 ② build ③ 跑自测 ④ 自动升级重启；任一步失败自动回滚";
        hint.ForeColor = Theme.FgFaint;
        hint.AutoSize = false;
        hint.AutoEllipsis = true;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        hint.Location = new Point(Theme.Px(22), Theme.Px(22));
        bottom.Controls.Add(hint);
        bottom.Layout += delegate
        {
            no.Location = new Point(bottom.ClientSize.Width - Theme.Px(20) - no.Width, Theme.Px(14));
            ok.Location = new Point(no.Left - Theme.Px(10) - ok.Width, Theme.Px(14));
            int hintW = Math.Max(Theme.Px(100), ok.Left - Theme.Px(33));
            hint.SetBounds(Theme.Px(22), Theme.Px(22), hintW, Math.Max(Theme.Px(24), bottom.Height - Theme.Px(28)));
        };
        Controls.Add(bottom);

        Panel wrap = new Panel();
        wrap.Dock = DockStyle.Fill;
        wrap.BackColor = Theme.Bg;
        // 正文用圆角卡片装：原来直接一个 FixedSingle 的直角 TextBox，和审批/澄清不是一套。
        RoundedPanel box = new RoundedPanel();
        box.Dock = DockStyle.Fill;
        box.Radius = Theme.RadInput;
        box.Fill = Theme.Card;
        box.BorderColor = Theme.Line;
        box.BackColor = Theme.Bg;
        box.Padding = new Padding(Theme.Sp16, Theme.Sp12, Theme.Sp16, Theme.Sp12);
        UiDiff tb = new UiDiff();
        tb.Dock = DockStyle.Fill;
        tb.Text = (body == null ? "" : body).Replace("\r\n", "\n").Replace("\n", "\r\n");
        _body = tb;
        box.Controls.Add(tb);
        wrap.Controls.Add(box);
        Controls.Add(wrap);
        wrap.BringToFront();

        ChromeBar bar = new ChromeBar("自我迭代改造申请");
        bar.Bounds = new Rectangle(0, 0, ClientSize.Width, Theme.Px(40));
        bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        bar.Closed += delegate { Decision = 0; Close(); };
        Controls.Add(bar);
        bar.BringToFront();

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
    public static string LastLoadError = "";
    public static string LastSaveError = "";
    public List<Session> sessions { get; set; }

    public SessionStore() { sessions = new List<Session>(); }

    public static string FilePath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sessions.json"); }
    }

    public static SessionStore Load()
    {
        LastLoadError = "";
        try
        {
            if (File.Exists(FilePath))
            {
                SessionStore s = Json.Ser.Deserialize<SessionStore>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (s != null && s.sessions != null) return s;
            }
        }
        catch (Exception ex)
        {
            LastLoadError = "会话读取失败：" + ex.Message;
            Log.Swallow(ex, "读取会话");
        }
        return new SessionStore();
    }

    public void Save()
    {
        // 先写 .tmp，再替换正式文件；中途崩溃/断电不会把 1.8MB 的会话库写成半截。
        // 旧版直接 File.WriteAllText，写一半就是全丢（2026-10-18 验收 P1-7）。
        string tmp = FilePath + ".tmp";
        try
        {
            File.WriteAllText(tmp, Json.Ser.Serialize(this), new UTF8Encoding(false));
            if (File.Exists(FilePath))
            {
                try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
                try { File.Replace(tmp, FilePath, null); }
                catch { File.Copy(tmp, FilePath, true); try { File.Delete(tmp); } catch { } }
            }
            else File.Move(tmp, FilePath);
            LastSaveError = "";
        }
        catch (Exception ex)
        {
            LastSaveError = "会话保存失败：" + ex.Message;
            Log.Swallow(ex, "保存会话");
        }
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

    // 只读日志末尾若干字节（设置页"运行日志"用，2026-10-16）。
    // 为什么不全读：日志可以很大，整文件塞进 TextBox 再整块替换 = 整屏重排，
    // 而且每次都把用户的滚动位置顶到底部（用户报的"日志刷新频繁重绘、还带着闪烁"）。
    public static string ReadTail(int maxBytes)
    {
        try
        {
            if (!File.Exists(FilePath)) return "（暂无日志：没有审批记录、没有异常）";
            using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length;
                if (len <= 0) return "（日志为空）";
                long start = len > maxBytes ? len - maxBytes : 0;
                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[len - start];
                int read = fs.Read(buf, 0, buf.Length);
                string s = Encoding.UTF8.GetString(buf, 0, read);
                // 日志是带 BOM 的 UTF-8（File.AppendAllText 新建时写的），而 ReadAllText 会剥掉；
                // 从字节流读时必须自己剥，否则设置页会多一个不可见的 U+FEFF，长度断言也会差 1。
                if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);
                if (start > 0)
                {
                    // 从中间切进去可能落在半个字符/半行上：丢掉第一个不完整行
                    int nl = s.IndexOf('\n');
                    if (nl >= 0) s = s.Substring(nl + 1);
                    s = "…（只显示最后 " + (maxBytes / 1024) + " KB）\n" + s;
                }
                return s.Length > 0 ? s : "（日志为空）";
            }
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
            sb.Append("  · ").Append(j.id).Append("  ").Append(StateOf(j));
            int kids = LiveChildCount(j);
            if (kids > 0) sb.Append("  [子孙进程 ").Append(kids).Append(" 个]");
            sb.Append("  ").Append(Txt.Clip(j.name, 60)).Append('\n');
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
                int pid = j.proc.Id;
                // 必须杀整棵进程树：只 Kill 外壳的话，它派生的子进程（node/python…）会残留下来继续跑
                Tools.KillTree(j.proc);
                Log.Audit("后台任务·终止 · " + j.id + " · " + Txt.Clip(j.command, 120));
                List<int> left = SurvivedChildren(pid);
                if (left.Count > 0)
                    return "已结束后台任务 " + j.id + "（" + j.name + "），但仍有 " + left.Count
                         + " 个子孙进程未退出：" + string.Join(", ", left.ConvertAll(x => x.ToString()).ToArray());
                return "已结束后台任务 " + j.id + "（" + j.name + "），进程树已清空";
            }
        }
        catch (Exception ex) { return "结束失败：" + ex.Message; }
        return "任务 " + j.id + " 早就结束了（" + StateOf(j) + "）";
    }

    // 还活着的子孙进程数（不看外壳自己是否已退出）
    static int LiveChildCount(BgJob j)
    {
        try { return SurvivedChildren(j.proc.Id).Count; }
        catch { return 0; }
    }

    // 兜底核对：taskkill 之后还有哪些子孙进程活着（按父进程关系遍历）
    static List<int> SurvivedChildren(int rootPid)
    {
        List<int> alive = new List<int>();
        try
        {
            Dictionary<int, List<int>> kids = new Dictionary<int, List<int>>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    int pid = p.Id;
                    if (pid == rootPid) continue;
                    int ppid = ParentPidOf(p);
                    if (ppid <= 0) continue;
                    if (!kids.ContainsKey(ppid)) kids[ppid] = new List<int>();
                    kids[ppid].Add(pid);
                }
                catch { }
            }
            Queue<int> q = new Queue<int>();
            q.Enqueue(rootPid);
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                if (!kids.ContainsKey(cur)) continue;
                foreach (int k in kids[cur]) { alive.Add(k); q.Enqueue(k); }
            }
        }
        catch { }
        return alive;
    }

    // 取父进程 PID：走 kernel32 的 Toolhelp32 快照（build.cmd 没引用 System.Management，用不了 WMI）
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    // 懒加载的 pid → ppid 映射，避免每个进程都做一次快照
    static Dictionary<int, int> _ppidCache = null;
    static DateTime _ppidCacheAt = DateTime.MinValue;

    static Dictionary<int, int> PpidMap()
    {
        if (_ppidCache != null && (DateTime.Now - _ppidCacheAt).TotalSeconds < 2) return _ppidCache;
        Dictionary<int, int> map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            PROCESSENTRY32 pe = new PROCESSENTRY32();
            pe.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PROCESSENTRY32));
            if (Process32First(snap, ref pe))
            {
                do
                {
                    if (pe.th32ProcessID != 0) map[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID;
                    pe.dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PROCESSENTRY32));
                } while (Process32Next(snap, ref pe));
            }
        }
        catch { }
        finally { try { CloseHandle(snap); } catch { } }
        _ppidCache = map; _ppidCacheAt = DateTime.Now;
        return map;
    }

    static int ParentPidOf(Process p)
    {
        try
        {
            Dictionary<int, int> map = PpidMap();
            int ppid;
            if (map.TryGetValue(p.Id, out ppid)) return ppid;
        }
        catch { }
        return 0;
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
// 浮层的"点窗口别处就收"不能只靠 MainForm.OnMouseDown：鼠标点在 MsgBlock/TextBox/侧栏
// 这些子控件上时，事件不会冒泡到 Form。用消息过滤器在点击落到主窗口任意子控件时收浮层，
// 但排除 chip 自己（否则点一下先收再 toggle，看起来没反应）。
class PopoverDismissFilter : IMessageFilter
{
    private readonly MainForm _f;
    public PopoverDismissFilter(MainForm f) { _f = f; }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x0201 && m.Msg != 0x0204) return false;
        Control c = Control.FromHandle(m.HWnd);
        if (c == null) return false;
        if (c.FindForm() != _f) return false;          // 点在浮层/其它窗口上：不关
        if (_f.IsPopoverAnchor(c)) return false;        // 点在 chip 上：交给 chip 自己 toggle
        _f.ClosePopoversFromFilter();
        return false;                                   // 放行这次点击
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
    private PopoverDismissFilter _popFilter;
    private int stepNo = 0;

    // 图片路径 ⇄ 气泡短标记。真路径留在 runner.Messages 里给模型看，
    // 界面上只显示 [图片 xxx.png]；历史渲染和实时写入共用这一套。
    public static class Attach
    {
        public static class Show
        {
            public static List<string> ImagePaths(string text)
            {
                return Vision.PathsIn(text);
            }

            public static string Text(string text)
            {
                if (string.IsNullOrEmpty(text)) return text;
                foreach (string p in Vision.PathsIn(text))
                    text = text.Replace(p, "[图片 " + Path.GetFileName(p) + "]");
                return text;
            }
        }
    }
    private int turnNo = 0;
    private readonly bool _openSettingsOnStart;
    private readonly ToolTip tips = new ToolTip();

    // ---------- 控件 ----------
    private Panel side, main, contentHost, composerHost, heroPanel;
    private UiDrop mFile, mEdit, mView, mHelp;
    private Control heroMark;
    private LogoMark avatar;
    private Label lblUser, lblSec, lblTitle, lblBadge, lblStatus, lblStats;
    private UiDrop sideTitle;          // 侧栏顶上那个「Ahz Scribe ⌄」（Codex 的「Codex ⌄」）
    private Label heroTitle;
    // 输入卡上面那三个药丸（项目 / 本地·云端 / 分支）——Codex 输入框上方那行
    private UiButton pillProj, pillEnv, pillBranch;
    private UiButton navNew, navPlugins, navExplore, navSettings, hNew, hRefresh, hDir, hSettings;
    private UiButton btnMin, btnMax, btnClose;
    private UiIconButton btnBack, btnFwd, btnSide, btnBell, btnSearch, btnUserDir;
    private Dot dotStatus;
    private bool sideOpen = true;            // 侧栏展开/收起（标题栏最左边那个方框按钮）
    private readonly List<string> hist = new List<string>();   // 访问过的会话（← → 用）
    private int histPos = -1;
    private string gitBranch = "";           // 工作目录的 git 分支（不是仓库就留空）
    private FieldBox searchBox;
    private UiEditor txtSearch;
    private bool searchOpen;
    private SidebarList list;
    private TabsBar tabs;
    // 对话区：一条消息一个控件（学 Codex），流式输出只重画最后那一条
    private TranscriptView rtb;
    // 轨迹页用的是同一套渲染：思考正文 + 工具步骤在这里流式输出，同样不会整块重排
    private TranscriptView trace;
    // 上一条工具调用是谁（下一条工具结果的折叠摘要行要报工具名，Write 里一路带过去）
    private string lastToolName = "";
    private float zoom = 1f;
    private RoundedPanel inputCard;
    private UiEditor txtInput;
    // 输入框里显示 [图片 xxx.png] 短标记，发送前再还原成真实路径；否则一粘图就是一长串绝对路径。
    private readonly Dictionary<string, string> _attachPaths =
        new Dictionary<string, string>(StringComparer.Ordinal);
    // 输入框里那行淡淡的提示。Codex 只写「随心输入」，长的那套挪到 tooltip 里去
    private const string PlaceholderText = "随心输入";
    private const string HintDetail = "发消息或创建任务，/ 调用指令，@ 文件或对话（Ctrl+V 可直接粘图片）";
    private UiButton btnSend;
    private UiDrop permChip, modeChip, btnPlus, planChip;
    private ModelChip cmbModel;
    private ModelPopover modelPop;                  // 打开着的模型浮层（没开就是 null）
    private PermPopover permPop;                    // 打开着的权限浮层

    // chip 上要显示的档位。第三步接上自绘滑块后改成读 config.effort
    private const string ChipEffort = "medium";
    // 设置窗口要用到的数据载体（主界面隐藏，只当数据源）
    private UiEditor txtCwd;
    private CheckBox chkThink, chkAutoYes, chkRemoteApproval;
    private UiDrop cmbCtx;

    // ---------- 字体 ----------

    public MainForm(bool openSettingsOnStart)
    {
        _openSettingsOnStart = openSettingsOnStart;
        config = AgentConfig.Load();
        config.Migrate();
        Theme.Apply(config.dark || Environment.GetEnvironmentVariable("AHZ_DARK") == "1");
        BuildUi();
        // 点主窗口任意子控件都收浮层（不能只靠 Form.OnMouseDown：子控件不冒泡）
        _popFilter = new PopoverDismissFilter(this);
        Application.AddMessageFilter(_popFilter);
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
        LoadGitBranch();          // 窗口这时候才有句柄，git 分支读回来才能更新界面
        AutoFetchModelMeta();     // 顺手把"模型能力标签"补一次（后台线程，失败静默）
        // 启动自检的告警：状态栏放一句，鼠标停上去看全文（全文已经写进 Ahz错误日志.txt）
        if (StartupCheck.Warning.Length > 0)
        {
            lblStatus.Text = "⚠ " + Txt.FirstLine(StartupCheck.Warning);
            lblStatus.ForeColor = Theme.Warn;
            tips.SetToolTip(lblStatus, StartupCheck.Warning);
        }
        // 配置/会话读坏了不能只在日志里；状态栏给一句，鼠标停上去看全文。
        string loadWarn = "";
        if (!string.IsNullOrEmpty(AgentConfig.LastLoadError)) loadWarn += AgentConfig.LastLoadError;
        if (!string.IsNullOrEmpty(SessionStore.LastLoadError))
            loadWarn += (loadWarn.Length > 0 ? "\n" : "") + SessionStore.LastLoadError;
        if (loadWarn.Length > 0)
        {
            if (StartupCheck.Warning.Length > 0) loadWarn = StartupCheck.Warning + "\n" + loadWarn;
            lblStatus.Text = "⚠ " + Txt.FirstLine(loadWarn);
            lblStatus.ForeColor = Theme.Warn;
            tips.SetToolTip(lblStatus, loadWarn);
        }
        UiWatchdog.Start(this);        // 卡死取证：下次再卡就留日志
        foreach (string a in Environment.GetCommandLineArgs())
        {
            if (a != "--selftest=browse") continue;
            BeginInvoke(new MethodInvoker(delegate { RunBrowseSelfTest(); }));
            break;
        }
        if (_openSettingsOnStart)
            BeginInvoke(new MethodInvoker(delegate { OpenSettings(); }));
        // --browser：启动就直接打开内置浏览器（我拿它做可复现的"开浏览器"验证，不用手点）
        foreach (string a in Environment.GetCommandLineArgs())
        {
            if (a != "--browser" && !a.StartsWith("--url=")) continue;
            string openUrl = a.StartsWith("--url=") ? a.Substring(6) : null;
            BeginInvoke(new MethodInvoker(delegate
            {
                try
                {
                    ToggleBrowser();
                    if (openUrl != null && browser != null) browser.Go(openUrl);
                }
                catch (Exception ex) { try { Log.Audit("--browser 打开失败：" + ex.Message); } catch { } }
            }));
            if (a == "--browser") break;
        }
        // 自绘按钮现在可 Tab，若不给初始焦点，开窗第一下焦点会落在标题栏按钮上（蓝框）。
        try { txtInput.Focus(); } catch { }
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
        txtCwd = new UiEditor();
        txtCwd.Text = config.cwd;
        chkThink = new CheckBox();
        chkThink.Checked = config.think;
        chkAutoYes = new CheckBox();
        chkAutoYes.Checked = config.auto_yes;
        chkRemoteApproval = new CheckBox();
        chkRemoteApproval.Checked = config.remote_approval;
        chkRemoteApproval.CheckedChanged += delegate
        {
            config.remote_approval = chkRemoteApproval.Checked;
            SaveConfig();
        };
        cmbCtx = new UiDrop();
        cmbCtx.AddRange(new object[] { "2048", "4096", "8192", "16384", "32768" });
        cmbCtx.SelectedItem = config.num_ctx.ToString();
        cmbCtx.SelectedIndexChanged += delegate
        {
            int v;
            if (int.TryParse(Convert.ToString(cmbCtx.SelectedItem), out v)) { config.num_ctx = v; SaveConfig(); }
        };

        SetPermMode(config.auto_yes, false);

        runner = new AgentRunner(config, this);
        UpdatePlacePills();        // 输入卡上面那三颗药丸（项目 / 本地 · 云端 / 分支）
        LoadGitBranch();
        UpdateHistoryButtons();
        UpdatePlanChip();
        LayoutUi();
        SyncWindowButtons();
        Resize += delegate { LayoutUi(); SyncWindowButtons(); };
        Shown += delegate { Theme.DarkenTree(this); };
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
        //
        // 左边三个图标照 Codex 标题栏来：← → 是会话来回（就像浏览器前后退），方框是收起/展开侧栏。

        btnBack = TitleIcon("arrowLeft");
        btnBack.Click += delegate { GoHistory(-1); };
        tips.SetToolTip(btnBack, "回到上一个会话");

        btnFwd = TitleIcon("arrowRight");
        btnFwd.Click += delegate { GoHistory(1); };
        tips.SetToolTip(btnFwd, "回到下一个会话");

        btnSide = TitleIcon("panel");
        btnSide.Click += delegate { ToggleSidebar(); };
        tips.SetToolTip(btnSide, "收起 / 展开侧栏");

        mFile = MenuButton("文件");
        mFile.Items.Add(new MenuItem2("新建会话", "Ctrl+N"));
        mFile.Items.Add(new MenuItem2("选择工作目录…", "Ctrl+O"));
        mFile.Items.Add(new MenuItem2("插件…", ""));
        mFile.Items.Add(new MenuItem2("导出交付记录…", ""));
        mFile.Items.Add(new MenuItem2("重启并升级（用 .new）…", ""));
        mFile.Items.Add(new MenuItem2("回滚这次改动（用快照）…", ""));
        mFile.Items.Add(new MenuItem2("关闭窗口", "Ctrl+W"));

        mEdit = MenuButton("编辑");
        mEdit.Items.Add(new MenuItem2("复制全部对话", "Ctrl+Shift+C"));
        mEdit.Items.Add(new MenuItem2("清空当前对话", ""));
        mEdit.Items.Add(new MenuItem2("设置…", "Ctrl+,"));

        mView = MenuButton("视图");
        mView.Items.Add(new MenuItem2("放大字号", "Ctrl+="));
        mView.Items.Add(new MenuItem2("缩小字号", "Ctrl+-"));
        mView.Items.Add(new MenuItem2("重置字号", "Ctrl+0"));
        // 长内容折叠的批量入口（对话区和轨迹页一起处理）
        mView.Items.Add(new MenuItem2("全部展开长内容", ""));
        mView.Items.Add(new MenuItem2("全部收起长内容", ""));
        mView.Items.Add(new MenuItem2("显示对话", "Ctrl+1"));        mView.Items.Add(new MenuItem2("显示轨迹", "Ctrl+2"));
        mView.Items.Add(new MenuItem2("内置浏览器", "Ctrl+Shift+B"));
        mView.Items.Add(new MenuItem2("展开 / 收起侧栏", "Ctrl+B"));
        mView.Items.Add(new MenuItem2("会话：新的在前", ""));
        mView.Items.Add(new MenuItem2("会话：旧的在前", ""));
        mView.Items.Add(new MenuItem2(config.dark ? "切换浅色模式" : "切换暗色模式", ""));

        mHelp = MenuButton("帮助");
        mHelp.Items.Add(new MenuItem2("使用说明", ""));
        mHelp.Items.Add(new MenuItem2("显示键盘快捷键", "Ctrl+/"));
        mHelp.Items.Add(new MenuItem2("关于 Ahz Scribe", ""));

        Controls.Add(btnBack);
        Controls.Add(btnFwd);
        Controls.Add(btnSide);
        Controls.Add(mFile);
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

    // 标题栏左边那种「只有图标、没有底色」的小按钮
    UiIconButton TitleIcon(string icon)
    {
        UiIconButton b = new UiIconButton(icon);
        b.IconSize = 15;
        b.IconWeight = 1.5f;
        b.IconColor = Theme.FgMid;
        b.BackColor = Theme.Chrome;
        b.HoverColor = Theme.Hover;
        b.Radius = Theme.RadMenu;
        return b;
    }

    UiButton WindowButton(string icon, bool close)
    {
        UiButton b = new UiButton();
        b.Icon = icon;
        b.IconSize = 11;
        b.IconWeight = 1.3f;
        b.Text = "";
        b.Radius = 0;
        b.BackColor = Theme.Chrome;      // 标题栏是浅灰的，窗口按钮得跟它同色
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
        b.TextColor = Theme.Fg;
        b.Radius = Theme.RadMenu;
        b.BackColor = Theme.Chrome;      // 同上：菜单按钮底就是标题栏那块浅灰
        // 字号是从用户的 Codex 截图上量出来的：「文件」两个字墨迹宽 40 物理像素 → 9 磅
        b.Font = Theme.F(9f);
        b.Padding = new Padding(10, 0, 10, 0);
        b.ShowCaret = false;              // Codex 的菜单标题后面没有小三角
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
            {
                e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height);
                // 底部那一行（头像 + 用户名）上面一条细线，Codex 侧栏底也是这样分的
                using (Pen p2 = new Pen(Theme.LineSoft, Theme.Pxf(1f)))
                    e.Graphics.DrawLine(p2, 0, side.Height - D(72), side.Width - 1, side.Height - D(72));
            }
        };

        // 顶上这一行：产品名 + 小三角 + 通知 / 搜索（Codex 侧栏顶上就是「Codex ⌄ 🔔 🔍」）
        sideTitle = new UiDrop();
        sideTitle.KeepText = true;
        sideTitle.Text = "Ahz Scribe";
        sideTitle.ShowCaret = true;
        sideTitle.Align = ContentAlignment.MiddleLeft;
        sideTitle.FillColor = Theme.Clear;
        sideTitle.HoverColor = Theme.Hover;      // Codex 那个三角也是悬停才亮
        sideTitle.TextColor = Theme.Fg;
        sideTitle.Radius = Theme.RadRow;
        sideTitle.Font = Theme.FB(11.5f);        // 参考图「Codex」标题的高度换算过来约 11.5 磅
        sideTitle.Padding = new Padding(Theme.Sp4, 0, Theme.Sp8, 0);
        sideTitle.BackColor = Theme.Side;
        sideTitle.Items.Add(new MenuItem2("设置…", "Ctrl+,"));
        sideTitle.Items.Add(new MenuItem2("使用说明", ""));
        sideTitle.Items.Add(new MenuItem2("显示键盘快捷键", "Ctrl+/"));
        sideTitle.Items.Add(new MenuItem2("关于 Ahz Scribe", ""));
        sideTitle.ItemChosen += delegate(object s, EventArgs e) { MenuAction(Convert.ToString(s)); };
        tips.SetToolTip(sideTitle, "设置 / 使用说明 / 关于");
        side.Controls.Add(sideTitle);

        btnBell = new UiIconButton("bell");
        btnBell.IconSize = 15;
        btnBell.BackColor = Theme.Side;
        btnBell.Click += delegate { ShowRecentLog(); };
        tips.SetToolTip(btnBell, "通知：最近的错误与警告");
        side.Controls.Add(btnBell);

        // 导航（Codex 那一列：新聊天 / 插件 / 探索）
        navNew = NavRow("newchat", "新聊天");
        navNew.Click += delegate { NewSession(); };
        tips.SetToolTip(navNew, "新聊天");
        side.Controls.Add(navNew);

        navPlugins = NavRow("plug", "插件");
        navPlugins.Click += delegate { OpenPlugins(); };
        tips.SetToolTip(navPlugins, "插件：给 agent 加工具");
        side.Controls.Add(navPlugins);

        navExplore = NavRow("dots", "探索");
        navExplore.Click += delegate { OpenPluginMarket(); };
        tips.SetToolTip(navExplore, "探索：去插件市场看看有什么能装");
        side.Controls.Add(navExplore);

        navSettings = NavRow("gear", "设置");
        navSettings.Click += delegate { OpenSettings(); };
        tips.SetToolTip(navSettings, "设置（模型、权限、外观）");
        side.Controls.Add(navSettings);

        lblSec = new Label();
        lblSec.Text = "项目";
        lblSec.ForeColor = Theme.FgDim;
        lblSec.BackColor = Theme.Side;
        lblSec.Font = Theme.F(9.5f);
        lblSec.AutoSize = true;
        side.Controls.Add(lblSec);

        btnSearch = new UiIconButton("search");
        btnSearch.IconSize = 15;
        btnSearch.BackColor = Theme.Side;
        btnSearch.Click += delegate { ToggleSearch(); };
        tips.SetToolTip(btnSearch, "搜索会话");
        side.Controls.Add(btnSearch);

        searchBox = new FieldBox(false);
        searchBox.Visible = false;
        txtSearch = (UiEditor)searchBox.Inner;
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

        // 底下一行只留「头像 + 用户名 + 换工作目录」——工作目录本身挪到输入卡上面的药丸里了
        // （Codex 就是这么摆的：项目名在输入框上方那颗药丸上）
    }

    // ---------- 右侧主区 ----------

    // 侧栏里那种「只有悬停才亮」的导航行（Codex 的 新聊天 / 插件 / 探索 就是这德行）
    UiButton NavRow(string icon, string text)
    {
        UiButton b = new UiButton();
        b.Text = text;
        b.Icon = icon;
        b.IconSize = 14;
        b.IconGap = 10;
        b.IconColor = Theme.FgMid;
        b.Align = ContentAlignment.MiddleLeft;
        b.Padding = new Padding(Theme.Sp8, 0, 0, 0);
        b.FillColor = Theme.Clear;
        b.HoverColor = Theme.Hover;
        b.TextColor = Theme.Fg;
        b.Radius = 8;
        b.Font = Theme.F(9.5f);
        return b;
    }

    // 收起 / 展开侧栏（标题栏最左边那个方框按钮，或者 Ctrl+B）
    // 输入卡上面那三颗药丸（项目 / 本地·云端 / 分支）：图标 + 文字，没有底色
    UiButton PlacePill(string icon)
    {
        UiButton b = new UiButton();
        b.Icon = icon;
        b.IconSize = 14;
        b.IconGap = 8;
        b.IconColor = Theme.FgDim;
        b.Align = ContentAlignment.MiddleLeft;
        b.Padding = new Padding(Theme.Sp4, 0, Theme.Sp8, 0);
        b.FillColor = Theme.Clear;
        b.HoverColor = Theme.Hover;
        b.TextColor = Theme.FgMid;
        b.Radius = Theme.RadRow;
        b.Font = Theme.F(9.5f);
        // 药丸是摆在灰底那条上的：底色必须跟"井"一个色，不然自绘的按钮会糊出一个个白方块
        b.BackColor = Theme.Well;
        return b;
    }

    void ToggleSidebar()
    {
        sideOpen = !sideOpen;
        if (side != null) side.Visible = sideOpen;
        LayoutUi();
        if (lblStatus != null) lblStatus.Text = sideOpen ? "侧栏已展开" : "侧栏已收起（Ctrl+B 可以再叫出来）";
    }

    // ← → 两个箭头：在"访问过的会话"里来回走，跟浏览器前后退一个意思
    void GoHistory(int delta)
    {
        int n = histPos + delta;
        if (n < 0 || n >= hist.Count) return;
        histPos = n;
        LoadSessionById(hist[n], false);
        UpdateHistoryButtons();
    }

    void PushHistory(string id)
    {
        if (id == null || id.Length == 0) return;
        if (histPos >= 0 && histPos < hist.Count && hist[histPos] == id) return;
        while (hist.Count > histPos + 1) hist.RemoveAt(hist.Count - 1);   // 往前走了，前面的记录作废
        hist.Add(id);
        histPos = hist.Count - 1;
        UpdateHistoryButtons();
    }

    void UpdateHistoryButtons()
    {
        if (btnBack != null) { btnBack.Enabled = histPos > 0; btnBack.Invalidate(); }
        if (btnFwd != null)
        {
            btnFwd.Enabled = histPos >= 0 && histPos < hist.Count - 1;
            btnFwd.Invalidate();
        }
    }

    // 「探索」= 去插件市场看看有什么能装（插件窗口里的那套安装/市场界面）
    void OpenPluginMarket()
    {
        try
        {
            using (PluginInstallForm dlg = new PluginInstallForm(config,
                delegate(string installedPath)
                {
                    if (runner != null) runner.ReloadTools();
                    lblStatus.Text = "已装插件：" + Path.GetFileName(installedPath);
                }))
                dlg.ShowDialog(this);
            SaveConfig();
            if (runner != null) runner.ReloadTools();
        }
        catch (Exception ex)
        {
            Log.Swallow(ex, "打开插件市场");
            UiMsg.Show(this, "插件市场没打开：" + ex.Message, "探索", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // 铃铛：把最近的错误/警告拿出来给人看（不是假装有个通知中心）
    void ShowRecentLog()
    {
        string tail = "";
        int shown = 0;
        try
        {
            if (File.Exists(Log.FilePath))
            {
                string[] all = File.ReadAllLines(Log.FilePath, Encoding.UTF8);
                int n = Math.Min(24, all.Length);
                shown = n;
                string[] last = new string[n];
                Array.Copy(all, all.Length - n, last, 0, n);
                tail = string.Join("\n", last);
            }
        }
        catch (Exception ex) { Log.Swallow(ex, "读日志尾巴"); }

        string msg = StartupCheck.Warning.Length > 0
            ? "启动自检：\n" + StartupCheck.Warning + "\n\n"
            : "";
        msg += tail.Length > 0
            ? "最近 " + shown + " 行日志（" + Log.FilePath + "）：\n\n" + tail
            : "没有错误日志 —— 目前一切正常。";
        UiMsg.Show(this, msg, "通知", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // 输入卡上方那三个药丸：项目 / 本地·云端 / 分支
    void UpdatePlacePills()
    {
        if (pillProj == null) return;
        Provider p = config.Current();
        string proj = ProjectName(config.cwd);
        if (pillProj.Text != proj) pillProj.Text = proj;
        tips.SetToolTip(pillProj, "工作目录：" + config.cwd + "（点一下换一个）");

        string env = (p != null && p.id != null && p.id.IndexOf("ollama", StringComparison.OrdinalIgnoreCase) >= 0)
            ? "本地" : "云端";
        if (pillEnv.Text != env) pillEnv.Text = env;
        tips.SetToolTip(pillEnv, "当前走的是 " + (p == null ? "?" : p.name) + " · " + config.model);
        if (heroTitle != null) heroTitle.Text = HeroText();     // 首屏那句「…在 <项目名> 中构建什么？」跟着走

        string br = gitBranch == null ? "" : gitBranch;
        if (pillBranch.Text != br) pillBranch.Text = br;
        tips.SetToolTip(pillBranch, br.Length == 0 ? "这个工作目录不是 git 仓库" : "git 分支：" + br);
        pillBranch.Visible = br.Length > 0;
    }

    // 启动后悄悄补一次"模型能力标签"：接口在 /models 里给的显示名（DeepSeek-V4.1-Flash）、
    // 能不能看图、支持的档位、上下文长度 —— 只有拉过一次才有。以前全靠用户记得去设置里点按钮，
    // 结果 chip 一直显示 deepseek-flash 这种生 id（用户 2026-10-15 对比截图时发现的）。
    // 放在后台线程 + 短超时，失败就静默（设置里那个手动按钮照旧）。
    void AutoFetchModelMeta()
    {
        Provider p = config.Current();
        if (p == null || p.models == null || p.models.Count == 0) return;
        if (p.model_meta != null && p.model_meta.Count > 0) return;      // 拉过了就别再打扰接口
        ThreadPool.QueueUserWorkItem(delegate(object st)
        {
            List<ModelMeta> infos = null;
            try
            {
                ILlm llm = LlmFactory.Create(p, "");
                infos = llm.ListModelInfos();
                llm.Close();
            }
            catch { infos = null; }
            if (infos == null || infos.Count == 0) return;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    foreach (ModelMeta mi in infos) p.SetMeta(mi);
                    SaveConfig();
                    LoadModels();
                    UpdatePlacePills();
                    Write(OutKind.Info, "· 已从接口补上模型能力（显示名 / 看图 / 档位）：共 " + infos.Count + " 个\n");
                }));
            }
            catch { }
        });
    }

    // 读一眼工作目录的 git 分支（不是仓库 / 没装 git 就留空，药丸自动藏起来）
    void LoadGitBranch()
    {
        string dir = config.cwd;
        gitBranch = "";
        UpdatePlacePills();
        ThreadPool.QueueUserWorkItem(delegate(object st)
        {
            string br = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD");
                psi.WorkingDirectory = dir;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } }
                    if (p.HasExited && p.ExitCode == 0) br = outp.Trim();
                }
            }
            catch { br = ""; }
            try
            {
                // 构造函数里调这一句时窗口句柄还没建出来，BeginInvoke 会抛 —— 那就等 OnShown 再补一次
                if (!IsHandleCreated) return;
                BeginInvoke(new MethodInvoker(delegate
                {
                    gitBranch = br;
                    UpdatePlacePills();
                    LayoutUi();
                }));
            }
            catch { }
        });
    }

    // 内置浏览器：盖在对话区上的一层（输入条不遮，边看网页边追问）
    Panel browserHost;
    AhzBrowser browser;
    bool browserOpen;

    void BuildMain()
    {
        main = new Panel();
        main.BackColor = Theme.Bg;

        lblTitle = new Label();
        lblTitle.Text = "新会话";
        lblTitle.Font = Theme.FB(9.5f);
        lblTitle.ForeColor = Theme.Fg;
        // 不 AutoSize：标题再长也只能占"页签右侧到四个图标之间"这一条，
        // 否则最小窗口下会压住 TabsBar（2026-10-18 验收：重叠 59px）。
        lblTitle.AutoSize = false;
        lblTitle.AutoEllipsis = true;
        lblTitle.TextAlign = ContentAlignment.MiddleRight;
        lblTitle.BackColor = Theme.Bg;
        main.Controls.Add(lblTitle);

        modeChip = new UiDrop();
        modeChip.Icon = "lock";
        modeChip.IconSize = 13;
        modeChip.IconGap = Theme.Sp8;
        modeChip.FillColor = Theme.Clear;
        modeChip.HoverColor = Theme.Hover;
        modeChip.TextColor = Theme.FgDim;
        modeChip.Radius = Theme.RadRow;
        modeChip.Font = Theme.F(9.5f);
        modeChip.Padding = new Padding(Theme.Sp8, 0, Theme.Sp12, 0);
        // 顶栏这个也走自绘的三档浮层（和输入条那个同一套）
        modeChip.KeepText = true;
        modeChip.Text = Perm.Label(config.permission);
        modeChip.Click += delegate { TogglePermPopover(modeChip); };
        // 顶栏这个权限 chip 不再摆了：权限现在只在输入条左下角显示（Codex 就是这样），
        // 控件留着是因为权限浮层还要用它当锚点（点顶栏那个位置也能弹）。
        modeChip.Visible = false;
        main.Controls.Add(modeChip);

        lblBadge = new Label();
        lblBadge.Text = "";
        lblBadge.Font = Theme.F(8.5f);
        lblBadge.ForeColor = Theme.FgFaint;
        lblBadge.BackColor = Theme.Bg;
        lblBadge.AutoSize = true;
        lblBadge.TextChanged += delegate { LayoutUi(); };
        lblBadge.Visible = false;      // 「模型 · 名称」现在显示在输入条右下角的 chip 上
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
        trace.IsTrace = true;        // 轨迹页：思考默认收起（对话区那套 hide_thinking 逻辑不动）
        trace.Visible = false;
        contentHost.Controls.Add(trace);

        BuildHero();
        contentHost.Controls.Add(heroPanel);

        // 内置浏览器（Alt+ 那一层）：默认收起，Ctrl+Shift+B 或菜单「查看」里开
        browserHost = new Panel();
        browserHost.BackColor = Theme.Bg;
        browserHost.Visible = false;
        main.Controls.Add(browserHost);
        browser = new AhzBrowser();
        browser.tipsRef = tips;
        browser.Dock = DockStyle.Fill;
        browser.AskAgent = delegate(string url) { AskAgentAboutPage(url); };
        browser.SendToInput = delegate(string text) { FillInputWith(text); };
        browserHost.Controls.Add(browser);

        // 把面板交给助手那半边：browse_* 工具就是操作它（同一个人眼前的那份）
        BrowserHub.Panel = browser;
        BrowserHub.ShowPanel = delegate
        {
            if (browserHost != null && !browserHost.Visible) ToggleBrowser();
        };
        heroPanel.BringToFront();

        BuildComposer();

        // 状态不再占底部一整条横带（Codex 底下就是空的，卡片离窗口底 16px）：
        // 小圆点 + 一句话挪到输入卡上面那行药丸的右边。
        dotStatus = new Dot();
        dotStatus.DotColor = Theme.Ok;
        dotStatus.BackColor = Theme.Well;
        composerHost.Controls.Add(dotStatus);

        lblStatus = new Label();
        lblStatus.Text = "就绪";
        lblStatus.ForeColor = Theme.FgDim;
        lblStatus.BackColor = Theme.Well;
        lblStatus.Font = Theme.F(9.5f);
        lblStatus.AutoSize = true;
        composerHost.Controls.Add(lblStatus);

        lblStats = new Label();
        lblStats.Text = "";
        lblStats.ForeColor = Theme.FgFaint;
        lblStats.BackColor = Theme.Bg;
        lblStats.Font = Theme.F(8.5f);
        lblStats.AutoSize = true;
        lblStats.TextChanged += delegate { LayoutUi(); };
        lblStats.Visible = false;      // 「Ollama 本地 / qwen3.5:4b · 连接失败」这类信息进模型 chip 的 tooltip
        composerHost.Controls.Add(lblStats);
    }

    void BuildHero()
    {
        heroPanel = new Panel();
        heroPanel.BackColor = Theme.Bg;

        // Codex 首屏那个云朵标：浅灰描边，不填色、不带方块底
        heroMark = new CloudMark();
        heroMark.BackColor = Theme.Bg;
        heroPanel.Controls.Add(heroMark);

        heroTitle = new Label();
        heroTitle.Text = HeroText();
        heroTitle.Font = Theme.F(20f);       // 参考图那句大字墨迹 712 物理像素宽 → 20 磅
        heroTitle.ForeColor = Theme.Fg;
        heroTitle.AutoSize = false;
        heroTitle.TextAlign = ContentAlignment.MiddleCenter;
        heroTitle.BackColor = Theme.Bg;
        heroPanel.Controls.Add(heroTitle);
    }

    // 首屏大字：Codex 写的是「你想让我们在 <项目名> 中构建什么?」——项目名跟着工作目录走
    string HeroText()
    {
        return "你想让我们在 " + ProjectName(config.cwd) + " 中构建什么？";
    }

    void BuildComposer()
    {
        composerHost = new Panel();
        composerHost.BackColor = Theme.Bg;
        // 输入卡下面那块浅灰的「井」（Codex 的做法：卡片压在一块灰底上，
        // 灰底比卡片略窄一点点、往上露出一条，项目/本地/分支三颗药丸就摆在那条上）
        composerHost.Paint += delegate(object s, PaintEventArgs e)
        {
            if (inputCard == null) return;
            Rectangle w = new Rectangle(
                inputCard.Left - inputCard.InsetPx - D(1),
                inputCard.Top - D(36),
                inputCard.Width + (inputCard.InsetPx + D(1)) * 2,
                composerHost.Height - inputCard.Top + D(36) + D(8));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Ui.Round(w, D(Theme.RadInput)))
            using (SolidBrush b = new SolidBrush(Theme.Well)) e.Graphics.FillPath(b, p);
        };
        main.Controls.Add(composerHost);

        // 药丸：项目 / 本地·云端 / 分支（都是真的，点项目那颗可以换工作目录）
        pillProj = PlacePill("folder");
        pillProj.Click += delegate { PickFolder(); };
        composerHost.Controls.Add(pillProj);

        pillEnv = PlacePill("monitor");
        pillEnv.Click += delegate { OpenSettings(); };
        composerHost.Controls.Add(pillEnv);

        pillBranch = PlacePill("branch");
        pillBranch.Click += delegate { LoadGitBranch(); };
        composerHost.Controls.Add(pillBranch);

        inputCard = new RoundedPanel();
        inputCard.Radius = Theme.RadCard;      // 圆角吸附 RadCard（原 16）
        // 不要阴影、也不要那圈浅色描边（用户 2026-10-15 指出层次太乱：灰影 → 白框 → 圆角框三层叠着）。
        // RoundedPanel 的"阴影"其实是叠 5 层半透明圆角矩形，边缘是硬的，看着就是一圈灰框；
        // 灰底上放一块白圆角已经够分层了。Shadow=false 时 InsetPx 自动变 0，子控件布局跟着铺满。
        inputCard.Shadow = false;
        inputCard.DrawBorder = false;
        inputCard.Inset = 5;               // 留着：万一以后要把阴影加回来，留白还是对的
        inputCard.Fill = Theme.Card;
        inputCard.BorderColor = Theme.Line;
        inputCard.BackColor = Theme.Bg;
        composerHost.Controls.Add(inputCard);

        txtInput = new UiEditor();
        txtInput.Hint = PlaceholderText;
        txtInput.Multiline = true;
        txtInput.BorderStyle = BorderStyle.None;
        txtInput.BackColor = Theme.Card;
        txtInput.ForeColor = Theme.Fg;
        txtInput.Font = Theme.F(10f);
        txtInput.ScrollBars = ScrollBars.None;
        tips.SetToolTip(txtInput, HintDetail);
        txtInput.TextChanged += delegate { UpdateSendButton(); };
        txtInput.KeyDown += delegate(object s, KeyEventArgs e)
        {
            // Ctrl+V：剪贴板里是图片（或图片文件的副本）就先落盘，再把路径写进输入框。
            // 文本框本身不认图片，不拦这一步就会"按了没反应"。
            if (e.Control && e.KeyCode == Keys.V)
            {
                if (TryPasteImage()) { e.SuppressKeyPress = true; e.Handled = true; }
                return;
            }
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                SendMessage();
            }
        };
        // 直接把图片文件拖进输入框（截图工具存盘后最常见的操作）
        txtInput.AllowDrop = true;
        txtInput.DragEnter += delegate(object s, DragEventArgs e)
        {
            if (HasImageDrop(e.Data)) e.Effect = DragDropEffects.Copy;
        };
        txtInput.DragDrop += delegate(object s, DragEventArgs e)
        {
            string[] imgs = ImagePathsFrom(e.Data);
            if (imgs.Length > 0) AttachImages(imgs);
        };
        inputCard.Controls.Add(txtInput);

        btnPlus = new UiDrop();
        btnPlus.KeepText = true;
        btnPlus.ShowCaret = false;
        btnPlus.Icon = "plus";
        btnPlus.IconSize = 15;
        btnPlus.IconYOffset = 1.5f;
        btnPlus.Align = ContentAlignment.MiddleCenter;
        btnPlus.FillColor = Theme.Clear;
        btnPlus.HoverColor = Theme.Hover;
        btnPlus.IconColor = Theme.Fg;
        btnPlus.Radius = Theme.RadRow;
        btnPlus.Padding = new Padding(0);
        btnPlus.Text = "";
        btnPlus.Items.Add("选择工作目录…");
        btnPlus.Items.Add("清空当前对话");
        btnPlus.Items.Add("设置…");
        btnPlus.ItemChosen += delegate(object s, EventArgs e) { MenuAction(Convert.ToString(s)); };
        tips.SetToolTip(btnPlus, "更多");
        inputCard.Controls.Add(btnPlus);

        permChip = new UiDrop();
        permChip.Icon = "warnCircle";
        permChip.IconSize = 14;
        permChip.IconYOffset = 1f;
        permChip.IconGap = 7;
        permChip.FillColor = Theme.Clear;
        permChip.HoverColor = Theme.Hover;
        permChip.TextColor = Theme.PermWarn;
        permChip.Radius = Theme.RadRow;
        permChip.Font = Theme.F(9.5f);
        permChip.Padding = new Padding(Theme.Sp4, 0, Theme.Sp12, 0);
        permChip.ShowCaret = false;
        permChip.KeepText = true;                  // 文字自己刷，走 UpdatePermChip()
        permChip.Text = Perm.Label(config.permission);
        permChip.Click += delegate { TogglePermPopover(); };   // 自绘一列三档（Codex 那样）
        inputCard.Controls.Add(permChip);

        // 计划模式开关：开着只调研、只出计划，框架层会拦下动手类工具
        planChip = new UiDrop();
        planChip.Icon = "eye";
        planChip.IconSize = 13;
        planChip.IconGap = 6;
        planChip.FillColor = Theme.Clear;
        planChip.HoverColor = Theme.Hover;
        planChip.TextColor = Theme.FgDim;
        planChip.Radius = Theme.RadRow;
        planChip.Font = Theme.F(9.5f);
        planChip.Padding = new Padding(Theme.Sp4, 0, Theme.Sp12, 0);
        planChip.ShowCaret = false;
        planChip.KeepText = true;
        planChip.Click += delegate { TogglePlanMode(); };
        tips.SetToolTip(planChip, "计划模式：只调研、只出计划，不改文件（点一下切换）");
        inputCard.Controls.Add(planChip);

        cmbModel = new ModelChip();
        cmbModel.SelectedIndexChanged += delegate { OnModelChanged(); };
        cmbModel.ChipClicked += delegate { ToggleModelPopover(); };    // 点它开自绘浮层（不再用 ContextMenuStrip）
        inputCard.Controls.Add(cmbModel);

        btnSend = new UiButton();
        btnSend.Icon = "send";
        btnSend.IconSize = 14;
        btnSend.IconColor = Color.White;
        btnSend.IconWeight = 1.9f;
        btnSend.Text = "";
        btnSend.Radius = 14;                       // 28 直径的圆钮（Codex 那个深色 ↑）
        btnSend.FillColor = Theme.SendIdle;
        btnSend.HoverColor = Theme.SendOn;
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
        // 最小化那一瞬间客户区会被压到 237x39 这种怪尺寸，仍然 >200，于是以前会**真的按它排版**：
        // side 高 = 39-56 = -17（被 WinForms 夹成 0）、main 宽 = 237-412 = -175（同样夹成 0）。
        // 还原时靠再来一次 Resize 才能恢复 —— 万一那次 Resize 不触发（尺寸没变、或还原路径不同），
        // 界面就停在"所有面板 0 尺寸"的状态，看着就是大片空白 + 控件点不到。
        // 所以这里把阈值提到"真的能放下标题栏 + 一点内容"，退化尺寸一律不排。
        if (side == null || ClientSize.Width < 200 || ClientSize.Height < D(37) + 100) return;
        int W = ClientSize.Width, H = ClientSize.Height;
        // 外壳：顶上 37（Codex 量出来就是 37）是自绘标题栏，下面左侧栏 / 右主区
        int chromeH = D(37);
        side.Visible = sideOpen;
        int sideW = sideOpen ? D(275) : 0;
        side.SetBounds(0, chromeH, sideW, H - chromeH);
        main.SetBounds(sideW, chromeH, W - sideW, H - chromeH);

        // 标题栏左边三个：← → 会话来回、▯ 收起/展开侧栏
        int tiw = D(24), tiy = (chromeH - tiw) / 2;
        btnBack.SetBounds(D(10), tiy, tiw, tiw);
        btnFwd.SetBounds(D(38), tiy, tiw, tiw);
        btnSide.SetBounds(D(66), tiy, tiw, tiw);

        int mx = D(112);
        UiDrop[] menus = new UiDrop[] { mFile, mEdit, mView, mHelp };
        foreach (UiDrop b in menus)
        {
            b.SetBounds(mx, D(5), b.PreferredWidth() + D(8), chromeH - D(10));
            mx = b.Right + D(4);
        }

        // 右上角三个窗口按钮（贴边，和 Windows 11 一致）
        int wbW = D(46), wbH = chromeH;
        btnClose.SetBounds(W - wbW, 0, wbW, wbH);
        btnMax.SetBounds(W - wbW * 2, 0, wbW, wbH);
        btnMin.SetBounds(W - wbW * 3, 0, wbW, wbH);

        // ---- 侧栏 ----
        if (sideOpen)
        {
            // 注意：侧栏里的坐标是"相对侧栏面板"的，侧栏自己被摆在标题栏下面（+37），
            // 所以这里的数值都要比目标绝对位置少 37。
            // 侧栏左内边距统一 Sp12（原来 11/14/15/17/8 五种），右内边距也统一 Sp12
            sideTitle.SetBounds(D(Theme.Sp12), D(5), sideTitle.PreferredWidth() + D(6), D(30));
            int hw = D(26), hy2 = D(Theme.Sp12);
            btnSearch.SetBounds(sideW - D(Theme.Sp12) - hw, hy2, hw, hw);
            btnBell.SetBounds(btnSearch.Left - D(Theme.Sp8) - hw, hy2, hw, hw);

            int navY = D(38), navH = D(32);
            UiButton[] navs = new UiButton[] { navNew, navPlugins, navExplore, navSettings };
            for (int i = 0; i < navs.Length; i++)
                navs[i].SetBounds(D(Theme.Sp12), navY + i * navH, sideW - D(Theme.Sp24), navH);

            lblSec.Location = new Point(D(Theme.Sp24), navY + navs.Length * navH + D(Theme.Sp24));
            searchBox.SetBounds(D(Theme.Sp12), lblSec.Top - D(Theme.Sp4), sideW - D(Theme.Sp24), D(30));

            int listTop = navY + navs.Length * navH + D(50);
            int listBottom = side.Height - D(84);
            list.SetBounds(0, listTop, sideW, Math.Max(D(40), listBottom - listTop));

            avatar.SetBounds(D(Theme.Sp16), side.Height - D(63), D(22), D(22));
            lblUser.SetBounds(D(48), side.Height - D(62), sideW - D(48) - D(44), D(20));
            btnUserDir.SetBounds(sideW - D(Theme.Sp12) - D(26), side.Height - D(65), D(26), D(26));
        }

        // ---- 主区顶上那一行：页签在左、会话名靠右、四个小图标最右 ----
        int hy = D(7);
        // 页签条只占左边一小段：它是整块不透明的，铺满整行会把右边那四个小图标盖掉
        // （实测过：图标全在，就是点不到，因为压在页签条底下）。
        tabs.SetBounds(0, hy, D(240), D(30));
        int rxi = main.Width - D(Theme.Sp16);
        UiButton[] hicons = new UiButton[] { hSettings, hDir, hRefresh, hNew };
        for (int i = 0; i < hicons.Length; i++)
        {
            hicons[i].SetBounds(rxi - D(26), hy, D(26), D(26));
            rxi -= D(30);
        }
        // 标题的左右边界：左边不能压 TabsBar，右边不能压四个小图标；放不下就省略号。
        int iconsLeft = hNew.Left;
        int titleMinX = tabs.Right + D(12);
        int titleX = Math.Max(titleMinX, iconsLeft - D(10) - Ui.TextWidth(lblTitle.Text, lblTitle.Font));
        lblTitle.SetBounds(titleX, hy + D(4), Math.Max(0, iconsLeft - D(8) - titleX), D(24));

        // ---- 内容区 + 输入卡 ----
        int composerH = D(152);
        contentHost.SetBounds(0, D(40), main.Width, Math.Max(D(60), main.Height - D(40) - composerH));
        // 浏览器和对话区同位置（盖在上面），输入条不遮 —— 边看网页边追问
        if (browserHost != null)
            browserHost.SetBounds(0, D(40), main.Width, Math.Max(D(60), main.Height - D(40) - composerH));

        int colW = Math.Min(D(1040), Math.Max(D(320), contentHost.Width - D(80)));
        int colX = (contentHost.Width - colW) / 2;
        rtb.SetBounds(colX, D(8), colW, Math.Max(D(30), contentHost.Height - D(16)));
        trace.SetBounds(colX, D(8), colW, Math.Max(D(30), contentHost.Height - D(16)));
        heroPanel.SetBounds(0, 0, contentHost.Width, contentHost.Height);

        // 空状态：云朵 + 大字，位置照 Codex 量的（云朵中心比内容区中线高 43，大字低 28）
        int markSize = D(48);
        int markCy = heroPanel.Height / 2 - D(43);
        int titleCy = heroPanel.Height / 2 + D(28);
        heroMark.SetBounds(heroPanel.Width / 2 - markSize / 2, markCy - markSize / 2, markSize, markSize);
        int heroW = Math.Min(D(900), Math.Max(D(320), heroPanel.Width - D(60)));
        int heroX = (heroPanel.Width - heroW) / 2;
        heroTitle.SetBounds(heroX, titleCy - D(21), heroW, D(42));

        // ---- 输入卡（Codex：739x100，离窗口底 16；上面露一条灰底摆三个药丸）----
        if (inputCard == null || pillProj == null || lblStatus == null || dotStatus == null) return;
        composerHost.SetBounds(0, main.Height - composerH, main.Width, composerH);
        composerHost.Invalidate();          // 灰底跟着卡片走，重排后要重画
        int cardW = Math.Min(D(739), Math.Max(D(320), composerHost.Width - D(80)));
        int cardH = D(100);
        int cardX = (composerHost.Width - cardW) / 2;
        inputCard.SetBounds(cardX, D(36), cardW, cardH);

        int inset = inputCard.InsetPx;
        int ix = inset + D(Theme.Sp12), iy = inset + D(Theme.Sp12);
        int rowH = D(Theme.RowHeight);
        int rowY = cardH - inset - D(Theme.Sp12) - rowH;
        txtInput.SetBounds(ix, iy, cardW - inset * 2 - D(Theme.Sp24), Math.Max(D(24), rowY - iy - D(Theme.Sp8)));

        btnPlus.SetBounds(ix - D(Theme.Sp4), rowY, D(Theme.RowHeight), rowH);
        permChip.SetBounds(ix + D(Theme.RowHeight), rowY, permChip.PreferredWidth() + D(Theme.Sp8), rowH);
        planChip.SetBounds(ix + D(Theme.RowHeight) + permChip.Width + D(Theme.Sp8), rowY, planChip.PreferredWidth() + D(Theme.Sp8), rowH);
        int rx2 = cardW - inset - D(Theme.Sp12);
        btnSend.SetBounds(rx2 - D(Theme.RowHeight), rowY, D(Theme.RowHeight), rowH);
        rx2 -= D(38);
        int mw = cmbModel.PreferredWidth() + D(Theme.Sp8);
        cmbModel.SetBounds(rx2 - mw, rowY, mw, rowH);

        // 药丸行：项目 / 本地·云端 / 分支（摆在卡片上方那条灰底上，左边对齐卡片内文）
        // 药丸起点 = 卡左 + 和卡片内文同一个内边距（原来是硬编码 28，为"绕开 inset"凑的数）
        int ppx = cardX + D(Theme.Sp12);
        UiButton[] pills = new UiButton[] { pillProj, pillEnv, pillBranch };
        for (int i = 0; i < pills.Length; i++)
        {
            if (!pills[i].Visible) continue;
            int pw = pills[i].PreferredWidth() + D(Theme.Sp8);
            pills[i].SetBounds(ppx, D(Theme.Sp4), pw, D(30));
            ppx += pw + D(Theme.Sp16);
        }
        // 运行状态：小圆点 + 一句话，挂在药丸行最右边（原来那条底部横带没了）
        lblStatus.Location = new Point(cardX + cardW - D(Theme.Sp12) - lblStatus.Width, D(Theme.Sp8));
        dotStatus.SetBounds(lblStatus.Left - D(Theme.Sp12), D(Theme.Sp12), D(8), D(8));
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
        case "内置浏览器": ToggleBrowser(); break;
            case "设置…": OpenSettings(); break;
            case "退出": Close(); break;
            case "关闭窗口": Close(); break;
            case "展开 / 收起侧栏": ToggleSidebar(); break;
            case "显示键盘快捷键": ShowShortcuts(); break;
            case "会话：新的在前": sortDesc = true; RefreshSessionList(); break;
            case "会话：旧的在前": sortDesc = false; RefreshSessionList(); break;
            case "切换暗色模式":
            case "切换浅色模式":
                config.dark = !config.dark;
                SaveConfig();
                try { SaveCurrentSession(); } catch { }
                try { Process.Start(Application.ExecutablePath); } catch { }
                Application.Exit();
                break;
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
            case "全部展开长内容": rtb.ExpandAll(); trace.ExpandAll(); break;
            case "全部收起长内容": rtb.CollapseAll(); trace.CollapseAll(); break;
            case "显示对话": tabs.SelectedIndex = 0; SwitchTab(); break;
            case "显示轨迹": tabs.SelectedIndex = 1; SwitchTab(); break;
            case "关于 Ahz Scribe": OpenSettings(); break;
            case "使用说明":
                UiMsg.Show(this,
                    "· 在底部输入框写任务，Enter 发送，Shift+Enter 换行\n"
                    + "· 写文件 / 改文件 / 执行命令前会弹窗征求同意\n"
                    + "· 输入框左下角「⚠ …」是权限档（仅可查看 / 工作区内修改 / 完全访问），点它换档\n"
                    + "· 输入框上面那三颗药丸：工作目录 / 本地或云端 / git 分支\n"
                    + "· 左上「对话 / 轨迹」在正文和工具步骤之间切换（Ctrl+1 / Ctrl+2）\n"
                    + "· 标题栏左边 ← → 在逛过的会话之间来回，方框按钮收起 / 展开侧栏\n"
                    + "· 全部快捷键：帮助 → 显示键盘快捷键（Ctrl+/）\n"
                    + "· 模型和提供商在「设置」里管理（本地 Ollama 或云端接口）",
                    "Ahz Scribe 使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
        }
    }

    // 帮助 → 显示键盘快捷键
    void ShowShortcuts()
    {
        UiMsg.Show(this,
            "Ctrl+N         新聊天\n"
            + "Ctrl+O         选择工作目录\n"
            + "Ctrl+W         关闭窗口\n"
            + "Ctrl+,         设置\n"
            + "Ctrl+B         展开 / 收起侧栏\n"
            + "Ctrl+1 / 2     对话页 / 轨迹页\n"
            + "Ctrl+= / -     放大 / 缩小字号        Ctrl+0  重置字号\n"
            + "Ctrl+Shift+C   复制全部对话\n"
            + "Ctrl+/         这份快捷键表\n\n"
            + "输入框里：Enter 发送，Shift+Enter 换行，Ctrl+V 可以直接粘图片。",
            "键盘快捷键", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SwitchTab()
    {
        bool traceMode = tabs.SelectedIndex == 1;
        trace.Visible = traceMode;
        rtb.Visible = !traceMode;
        // 对话区已经有东西了就不能再让空状态大标题盖上来（它会整块挡住对话区 = 一次"闪屏"）
        heroPanel.Visible = !traceMode && !HasMessages() && !rtb.HasContent;
        SyncHeroChrome();
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

    // 权限档刚变过：给 chip 一次短暂的强调底色，让"真的改了"看得见（700ms 自己褪掉）。
    // 以前只有文字变化，切到同一档附近时看不出反馈（用户报的"操作后没有视觉确认"）。
    System.Windows.Forms.Timer _permFlashTimer;

    void FlashPermChip()
    {
        if (permChip == null) return;
        try
        {
            permChip.FillColor = Theme.AccentSoft;
            permChip.Invalidate();
            if (_permFlashTimer == null)
            {
                _permFlashTimer = new System.Windows.Forms.Timer();
                _permFlashTimer.Interval = 700;
                _permFlashTimer.Tick += delegate
                {
                    _permFlashTimer.Stop();
                    if (permChip != null) { permChip.FillColor = Theme.Clear; permChip.Invalidate(); }
                };
            }
            _permFlashTimer.Stop();
            _permFlashTimer.Start();
        }
        catch (Exception ex) { Log.Swallow(ex, "权限 chip 反馈"); }
    }

    void SetPerm(string level, bool save)
    {
        config.permission = level;
        config.auto_yes = Perm.AutoYes(level);     // 老字段跟着同步，别处的判断不用全改
        if (save) SaveConfig();
        UpdatePermChip();
        FlashPermChip();
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
        if (permChip != null)
        {
            // 「完全访问」这种不再逐条问的档，用橙色提醒一下（Codex 也是这个色）；其它档是灰的
            Color want = Perm.AutoYes(config.permission) ? Theme.PermWarn : Theme.FgDim;
            if (permChip.TextColor != want) { permChip.TextColor = want; permChip.Invalidate(); }
        }
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
        // 关着的时候整颗藏起来 —— Codex 输入条左下角就只有 + 和权限那一颗，
        // 计划模式开着才冒出来（免得常驻占地方，也免得忘了自己还开着）。
        planChip.Text = "计划模式";
        planChip.TextColor = Theme.Accent;
        planChip.IconColor = Theme.Accent;
        planChip.Visible = config.plan_mode;
        planChip.Invalidate();
        LayoutUi();
    }

    void TogglePlanMode()
    {
        config.plan_mode = !config.plan_mode;
        SaveConfig();
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
            UiMsg.Show(this,
                "已导出到：\n" + path + "\n\n内容：用户要求 / 改动的文件（带行数差）/ 跑过的命令与结果 / 结论。",
                "导出交付记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Swallow(ex, "导出交付记录");
            UiMsg.Show(this, "导出失败：\n" + ex.Message, "导出交付记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 「文件」菜单里的重启并升级：退出 → 换 .new → 自动启动
    void ConfirmUpgrade()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        List<string> pending = Upgrader.PendingNewFiles(exeDir);
        if (pending.Count == 0)
        {
            UiMsg.Show(this,
                "没有待升级的 .new。\n\n先跑一遍  cmd /c build.cmd —— 窗口版正在运行时它会自动产出 .new，再来点这里。",
                "重启并升级", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (UiMsg.Show(this,
            "将做三件事：\n1) 退出当前程序（会保存会话）\n2) 用这些文件替换正式版：" + string.Join("、", pending.ToArray())
            + "\n3) 替换完自动重新启动\n\n现在继续？",
            "重启并升级", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        string msg;
        if (Upgrader.Schedule(exeDir, true, out msg)) Close();
        else UiMsg.Show(this, msg, "重启并升级", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // 「文件」菜单里的回滚：把文件恢复到改动前（最近一条快照）
    void ConfirmRollback()
    {
        if (runner == null) { lblStatus.Text = "还没有会话可以回滚"; return; }
        if (UiMsg.Show(this,
            "把工作目录里的文件恢复到**改动前**（用最近一条 .snapshots 快照）？\n\n"
            + "适合场景：刚改完发现不对、或自我改造验证没过。\n回滚之后如果已经 build 出新版，"
            + "要再跑一次 build 才能让二进制和源码一致。",
            "回滚这次改动", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        string msg = Upgrader.RollbackTool(runner.Ctx, new Dictionary<string, object>());
        lblStatus.Text = "回滚完成";
        UiMsg.Show(this, msg, "回滚这次改动", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        MenuRound.Apply(m);
        m.ShowImageMargin = false;
        m.BackColor = Theme.Card;
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
        if (save) SaveConfig();
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
        // 接口声明过档位的话，真正发出去的可能不是 low/medium/high（DeepSeek 是 low/high/max）
        string real = Effort.ResolveFor(p, config.model, e);
        return "reasoning_effort=" + (real == null ? e : real);
    }

    // 当前模型能不能调推理强度（见 Effort.Supported）
    bool EffortSupported()
    {
        Provider p = config.Current();
        if (p == null) return false;
        string tv = (p.type == "ollama" && runner != null) ? runner.ThinkingValues : "";
        return Effort.Supported(p, config.model, tv);
    }

    // 输入条上那个 chip 的文字：「厂商 / 模型 · 档位」（Codex 风格）
    void UpdateModelChip()
    {
        if (cmbModel == null || cmbModel.SelectedItem == null) return;
        // Codex 的写法是「模型名 档位」（中间一个空格）——只要模型名，不要「厂商 / 」那半截，
        // 厂商已经在输入框上方那颗「本地 / 云端」药丸上写着了。
        ModelItem it = cmbModel.SelectedItem as ModelItem;
        // 显示名优先用接口给的（DeepSeek-V4.1-Flash），没有才用 id（deepseek-flash）
        string name = it != null
            ? (it.Display != null && it.Display.Length > 0 ? it.Display : it.Model)
            : Convert.ToString(cmbModel.SelectedItem);
        string t = name + " " + Effort.Label(config.effort);
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
                item.Display = p.DisplayName(m);       // 接口给了显示名就用显示名
                item.Vision = p.CanSeeImages(m);
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
        SaveConfig();
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
            WriteError("连接不上「" + p.name + "」", ex.Message,
                "本地模型：确认 Ollama 在运行（托盘图标，或执行 ollama serve）；云端接口：检查接口地址与 API Key");
            lblStats.Text = p.name + " / " + config.model + " · 连接失败";
            if (lblBadge != null) lblBadge.Text = p.name + " · " + config.model;
            if (cmbModel != null) tips.SetToolTip(cmbModel, "⚠ " + lblStats.Text + "（设置里可以换模型 / 换接口）" + VisionNote());
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
        // 悬停模型 chip：模型/上下文这些 + 能不能看图（接口声明过才说）
        if (cmbModel != null) tips.SetToolTip(cmbModel, lblStats.Text + VisionNote());
    }

    void UpdateSendButton()
    {
        if (btnSend == null) return;
        if (running)
        {
            btnSend.Icon = "stop";
            btnSend.IconSize = 12;
            btnSend.FillColor = Theme.SendOn;
            btnSend.HoverColor = Theme.SendOnHover;
        }
        else
        {
            btnSend.Icon = "send";
            btnSend.IconSize = 14;
            bool has = txtInput != null && txtInput.Text.Trim().Length > 0;
            // 空输入是浅灰、有字变深（Codex 那个深色圆钮）—— 不是蓝色
            btnSend.FillColor = has ? Theme.SendOn : Theme.SendIdle;
            btnSend.HoverColor = has ? Theme.SendOnHover : Theme.SendOn;
        }
        btnSend.Invalidate();
    }

    // ============================ 会话 ============================

    void LoadSessions()
    {
        sessionStore = SessionStore.Load();
        RepairSessionTitles();
        RefreshSessionList();
        if (sessionStore.sessions.Count > 0) LoadSessionById(sessionStore.sessions[0].id);
        else { currentSessionId = ""; ShowHero(true); }
    }

    // 「（系统提醒）…」「（踩坑本…」是框架注入的 user 消息，不是用户说的话。
    // 以前 SaveCurrentSession 拿第一条 user 消息当标题，于是侧栏/标题栏全是同一句提醒。
    static bool IsFrameworkUserText(string txt)
    {
        if (string.IsNullOrEmpty(txt)) return false;
        txt = txt.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return txt.StartsWith("（系统提醒）") || txt.StartsWith("（系统提醒 ·")
            || txt.StartsWith("（踩坑本");
    }

    static string FirstUserTitle(List<Dictionary<string, object>> msgs)
    {
        if (msgs == null) return null;
        foreach (Dictionary<string, object> m in msgs)
        {
            if (Json.Str(m, "role", "") != "user") continue;
            string txt = Json.Str(m, "content", "");
            if (string.IsNullOrEmpty(txt) || IsFrameworkUserText(txt)) continue;
            txt = txt.Replace("\r", " ").Replace("\n", " ").Trim();
            if (txt.Length == 0) continue;
            return txt.Length > 22 ? txt.Substring(0, 22) + "…" : txt;
        }
        return null;
    }

    // 老会话的标题已经被写坏了：加载时先按消息回填一次（只改内存，等下次保存落盘）。
    void RepairSessionTitles()
    {
        if (sessionStore == null || sessionStore.sessions == null) return;
        foreach (Session s in sessionStore.sessions)
        {
            if (s == null) continue;
            bool bad = string.IsNullOrEmpty(s.title) || s.title == "新会话" || IsFrameworkUserText(s.title);
            if (!bad) continue;
            string t = FirstUserTitle(s.messages);
            if (!string.IsNullOrEmpty(t)) s.title = t;
        }
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

    // 保存失败不能只写日志：状态栏给一句，鼠标停上去看全文。
    void ShowSaveWarning()
    {
        string err = "";
        if (!string.IsNullOrEmpty(AgentConfig.LastSaveError)) err += AgentConfig.LastSaveError;
        if (!string.IsNullOrEmpty(SessionStore.LastSaveError))
            err += (err.Length > 0 ? "\n" : "") + SessionStore.LastSaveError;
        if (err.Length == 0) return;
        lblStatus.Text = "⚠ " + Txt.FirstLine(err);
        lblStatus.ForeColor = Theme.Warn;
        tips.SetToolTip(lblStatus, err);
    }

    // 所有 MainForm 里的 config.Save() 都走这里，保存失败统一进状态栏。
    void SaveConfig()
    {
        if (config == null) return;
        config.Save();
        ShowSaveWarning();
    }

    void SaveStore()
    {
        if (sessionStore == null) return;
        while (sessionStore.sessions.Count > 200) sessionStore.sessions.RemoveAt(sessionStore.sessions.Count - 1);
        sessionStore.Save();
        ShowSaveWarning();
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
        string title = FirstUserTitle(s.messages);
        if (!string.IsNullOrEmpty(title)) s.title = title;
        SaveStore();
    }

    void LoadSessionById(string id)
    {
        LoadSessionById(id, true);
    }

    // record=false 是给 ← → 两个箭头用的（自己走过来的一步，不该再压一条历史）
    void LoadSessionById(string id, bool record)
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
        if (record) PushHistory(id);
        UpdatePlacePills();
        LoadGitBranch();
        LoadModels();
        ApplyModelInfo();
        RefreshSessionList();
    }

    // 打开会话时最多往对话区摆多少条历史。
    // 以前是"有多少摆多少"：891 条那条会话实测要 95 秒以上，窗口一直"(未响应)"。
    // 老消息仍然在会话里（sessions.json 一条没少），只是不摆到界面上 ——
    // 接着聊、分支、导出交付记录都不受影响。
    const int RenderHistoryMax = 200;

    void RenderHistory()
    {
        int total = runner.Messages.Count - 1;          // 减掉开头那条 system
        int start = Math.Max(1, runner.Messages.Count - RenderHistoryMax);
        if (start > 1)
            Write(OutKind.Info, "· 这条会话共 " + total + " 条消息，为免卡顿只摆出最近 "
                + RenderHistoryMax + " 条（模型看到的上下文不受影响）\n");
        for (int i = start; i < runner.Messages.Count; i++)
        {
            Dictionary<string, object> m = runner.Messages[i];
            string role = Json.Str(m, "role", "");
            string content = Json.Str(m, "content", "");
            // 先把"这条属于哪一步 + 时间"挂上，等块建出来时自动贴好（见 TranscriptView.PendingIdx）
            rtb.PendingIdx = i;
            long ts0 = Json.Long(m, "ts", 0);
            rtb.PendingStamp = ts0 > 0
                ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts0).ToLocalTime()
                : (DateTime?)null;
            if (role == "user")
            {
                // 历史消息里存的是真路径；界面显示短标记，真路径走 PendingImages 贴到新块上。
                rtb.PendingImages = Attach.Show.ImagePaths(content);
                Write(OutKind.User, "› " + Attach.Show.Text(content) + "\n");
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
                // 历史里的工具结果也摆全文（原来只有第一行，展开就是空的）
                lastToolName = Json.Str(m, "tool_name", "");
                Write(OutKind.ToolResult, "  └ " + Txt.Clip(content, 3000) + "\n");
            }
        }
        // 一次性刷出来：中途刷一次就要把所有块重排一遍（每条都刷 = 891 次，就是卡死的原因；
        // 每 20 条刷一次实测仍有 3.4 秒，索性攒到最后刷一次）。
        rtb.FlushNow();
        rtb.PendingIdx = -1;            // 最后一条的标记别漏给下一条（比如马上要来的流式回答）
        rtb.PendingStamp = null;
        rtb.PendingImages = null;
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

    // ---------- 内置浏览器 ----------

    // 自测开关：--selftest=browse 时在程序自己的消息循环里跑一遍 browse_* 并把结果写日志。
    // 为什么要这么做：进程内探针（DoEvents 泵）跑不了 Chromium（Cef.Initialize 需要真正的 Application.Run），
    // 所以只能在真程序里自测。
    void RunBrowseSelfTest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ahz-selftest-" + DateTime.Now.ToString("HHmmss"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "one.html"),
                "<!doctype html><meta charset=\"utf-8\"><title>第一页</title><body><h1>第一页</h1>"
                + "<p>PAGE1-MARKER</p><a href=\"two.html\">进入第二页</a></body>", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "two.html"),
                "<!doctype html><meta charset=\"utf-8\"><title>第二页</title><body><h1>第二页</h1>"
                + "<p>PAGE2-MARKER</p><a href=\"one.html\">回第一页</a></body>", new UTF8Encoding(false));
        }
        catch { }

        string p1 = Path.Combine(dir, "one.html");
        System.Threading.Thread t = new System.Threading.Thread(delegate()
        {
            StringBuilder log = new StringBuilder();
            try
            {
                log.Append("open: ").Append(OneLine(BrowserHub.Open(p1))).Append("\n");
                log.Append("read: ").Append(BrowserHub.Read(2000).IndexOf("PAGE1-MARKER") >= 0 ? "OK" : "FAIL").Append("\n");
                log.Append("links: ").Append(BrowserHub.Links(10).IndexOf("进入第二页") >= 0 ? "OK" : "FAIL").Append("\n");
                log.Append("click: ").Append(OneLine(BrowserHub.Click("进入第二页"))).Append("\n");
                log.Append("back: ").Append(OneLine(BrowserHub.Back())).Append("\n");
                log.Append("shot: ").Append(OneLine(BrowserHub.Shot(Path.Combine(dir, "shot.png")))).Append("\n");
            }
            catch (Exception ex) { log.Append("EX ").Append(ex.Message).Append("\n"); }
            try { Log.Audit("浏览器自测结果：\n" + log.ToString()); } catch { }
            try { BeginInvoke(new MethodInvoker(delegate { Application.Exit(); })); } catch { }
        });
        t.IsBackground = true;
        t.Start();
    }

    static string OneLine(string s)
    {
        if (s == null) return "null";
        s = s.Replace("\r", " ").Replace("\n", " | ");
        return s.Length > 220 ? s.Substring(0, 220) + "…" : s;
    }

    void ToggleBrowser()
    {
        try { ToggleBrowserCore(); }
        catch (Exception ex)
        {
            try
            {
                Log.Audit("内置浏览器打开失败：" + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null) Log.Audit("  inner: " + ex.InnerException.Message);
            }
            catch { }
            try { lblStatus.Text = "内置浏览器打不开：" + Txt.FirstLine(ex.Message); } catch { }
        }
    }

    void ToggleBrowserCore()
    {
        if (browserHost == null || browser == null) return;
        bool want = !browserHost.Visible;
        browserHost.Visible = want;
        browserOpen = want;
        if (want)
        {
            browserHost.BringToFront();
            lblStatus.Text = "内置浏览器 · 正在准备（第一次要等 IE 内核起来，稍等一下）";
            // 这里绝不能 Application.DoEvents()：工具调用已经是从 BeginInvoke 回到 UI 线程的，
            // 再泵一次消息会让 ToggleBrowser 重入自己（栈溢出 / 卡死就是这么来的）。
            if (browser.CurrentUrl.Length == 0) browser.Go(browser.StartPage());
            browser.FocusAddress();
            // 自证几何：面板/宿主/主区/Web/右簇各多宽，卡住或错位时一眼能看出来（写进 Ahz错误日志）
            try
            {
                Log.Audit("内置浏览器几何 · 面板=" + browser.Width + " 宿主=" + browserHost.Width
                    + " 主区=" + main.Width + " 窗口客户区=" + ClientSize.Width
                    + " Web=" + ((browser.Web as Control) == null ? -1 : ((Control)browser.Web).Width));
            }
            catch { }
            lblStatus.Text = "内置浏览器 · 看完点「交给 AS」，我就用 web_open 读这一页";
        }
        else
        {
            lblStatus.Text = "已收起浏览器";
            try { rtb.Focus(); } catch { }
        }
    }

    // 「交给 AS」：只把地址塞进输入框，由用户自己按回车 —— 不在人没同意时直接花 token
    void AskAgentAboutPage(string url)
    {
        if (url == null || url.Trim().Length == 0) return;
        FillInputWith("用 web_open 打开这个页面，把要点讲给我（正文太长就摘要，别贴原文）：\n" + url.Trim());
        lblStatus.Text = "已把地址填到输入框，回车就让我读这一页";
    }

    // 和"选中文字 → 填到输入框"同一个动作：追加、不覆盖，滚到末尾、抢焦点
    void FillInputWith(string text)
    {
        if (text == null) return;
        string cur = txtInput.Text;
        txtInput.Text = (cur.Trim().Length > 0 ? cur.TrimEnd() + "\n\n" : "") + text + "\n";
        txtInput.SelectionStart = txtInput.TextLength;
        txtInput.Focus();
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
        if (UiMsg.Show(this, "把这一轮改的 " + list.Count + " 个文件恢复原样？\n\n"
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
        SyncHeroChrome();
        RefreshSessionList();
    }

    // 空态顶上干干净净：会话名和「对话 / 轨迹」页签都不摆（Codex 那一屏就一个云朵 + 一句大字），
    // 有内容了再放回来（Ctrl+1 / Ctrl+2 和视图菜单随时还能切）。
    void SyncHeroChrome()
    {
        bool hero = heroPanel != null && heroPanel.Visible;
        if (lblTitle != null) lblTitle.Visible = !hero;
        if (tabs != null) tabs.Visible = !hero;
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
        SaveConfig();
        if (txtCwd != null) txtCwd.Text = config.cwd;
        runner.SetWorkingDir(config.cwd);
        NewSession();
        UpdatePlacePills();
        LoadGitBranch();
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
            string ga = SessionGroup(a), gb = SessionGroup(b);
            bool da = ga == "默认工作区", db = gb == "默认工作区";
            if (da != db) return da ? -1 : 1;          // 默认工作区固定排最上面
            int c = string.Compare(ga, gb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;                      // 同项目连续摆，避免同名分组重复出现
            string x = a.updated == null ? "" : a.updated;
            string y = b.updated == null ? "" : b.updated;
            return sortDesc ? string.CompareOrdinal(y, x) : string.CompareOrdinal(x, y);
        });

        string f = (filter == null ? "" : filter.Trim().ToLowerInvariant());
        string lastProject = null;
        List<Control> rows = new List<Control>();
        foreach (Session s in items)
        {
            string proj = SessionGroup(s);
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

    string SessionGroup(Session s)
    {
        // 当前工作目录统一显示成「默认工作区」，和 DeepSeek Harness/Codex 的默认工作区对齐。
        string sc = (s.cwd == null ? "" : s.cwd).TrimEnd('\\');
        string cc = (config.cwd == null ? "" : config.cwd).TrimEnd('\\');
        if (sc.Length > 0 && string.Equals(sc, cc, StringComparison.OrdinalIgnoreCase)) return "默认工作区";
        return ProjectName(s.cwd);
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
        SaveConfig();
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
            SaveConfig();
            rtb.AutoFollow = config.follow_output;
            suppressModelChange = true;
            txtCwd.Text = config.cwd;
            chkThink.Checked = config.think;
            chkAutoYes.Checked = config.auto_yes;
            cmbCtx.SelectedItem = config.num_ctx.ToString();
            if (cmbCtx.SelectedIndex < 0) cmbCtx.SelectedItem = "8192";
            suppressModelChange = false;
            SetPerm(config.permission, false);        // 权限档：顶栏 / 输入条跟着设置页一起变
            // 注意：这里**不能**重建 runner、也不能清对话区 —— 那等于"一保存设置就把上下文扔了"。
            // 只把新配置交给 runner（换连接、换工具表、重拼系统提示），历史一条不动。
            runner.ApplyConfig(config);
            UpdatePlacePills();
            LoadGitBranch();
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

    // "read_file(a.txt)" → "read_file"；"read_file" / "自动验证(cmd)" 这类也认。
    // 只用来给折叠摘要行报工具名，认不出就返回原文（截短）。
    static string ToolNameOf(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOf('(');
        string n = (i < 0 ? s : s.Substring(0, i)).Trim();
        if (n.Length > 24) n = n.Substring(0, 24);
        return n;
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
                    // 「（系统提醒）…」不是用户说的话，是框架每步注入的提醒。摆成一个大「你」块，
                    // 历史里堆十几遍看着就像刷屏（2026-10-15 用户报的就是这个）。这里当成小字提示渲染。
                    if (body.StartsWith("（系统提醒）"))
                    {
                        rtb.Break();
                        rtb.AddChunk(OutKind.Info, "· " + body + "\n");
                        rtb.FlushNow();
                        break;
                    }
                    rtb.Break();
                    // 真路径留在 runner.Messages 里；界面只显示短标记，图片从侧信道贴到块上。
                    List<string> imgs = Attach.Show.ImagePaths(body);
                    string shown = Attach.Show.Text(body);
                    if (imgs.Count > 0) rtb.PendingImages = imgs;
                    // 不再拼「你\n」标签：区分用户/AI 已经交给气泡本身（靠右 + 蓝底 + 描边），
                    // 留着它等于每条用户消息头上多一行噪声，复制出来也多两个字。
                    rtb.AddChunk(OutKind.User, shown + "\n");
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
                    lastToolName = ToolNameOf(body);        // 下面那条工具结果的摘要行要报工具名
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
                    // 摘要行要报"是谁跑的"：工具名跟着这一批文字走（建块时贴到 MsgBlock.FoldTool）
                    rtb.Break();
                    rtb.AddToolChunk(body + "\n", lastToolName);
                    trace.Break();
                    trace.AddToolChunk("    " + body + "\n", lastToolName);
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
        {            object result = null;
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

    // 统一错误呈现（2026-10-16）：一句"发生了什么" + 一句"为什么" + 一句"怎么办"。
    // 以前八处各写各的，用户只看到一串原始报错，不知道下一步该干什么（用户报的"错误状态无友好提示"）。
    internal static string ErrorText(string what, string why, string howto)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("\n× ").Append(what).Append('\n');
        if (!string.IsNullOrEmpty(why)) sb.Append("    原因：").Append(Txt.FirstLine(why)).Append('\n');
        if (!string.IsNullOrEmpty(howto)) sb.Append("    怎么办：").Append(howto).Append('\n');
        return sb.ToString();
    }

    void WriteError(string what, string why, string howto)
    {
        Write(OutKind.Error, ErrorText(what, why, howto));
    }

    string _lastStatus;      // 同值不重绘（状态栏每条 token 被刷一次的话，状态点会跟着闪）

    public void Status(string text)
    {
        if (_lastStatus == text) return;
        _lastStatus = text;
        if (InvokeRequired) { SafeUi(delegate { ApplyStatus(text); }); return; }
        ApplyStatus(text);
    }

    void ApplyStatus(string text)
    {
        if (lblStatus.Text != text) lblStatus.Text = text;
        UpdatePlanChip();      // /计划 开|关 是在 AgentRunner 里切的，chip 得跟着刷新
        Color want = running ? Theme.Accent : Theme.Ok;
        if (dotStatus.DotColor != want) { dotStatus.DotColor = want; dotStatus.Invalidate(); }
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

    // ============================ 粘贴 / 拖入图片 ============================
    // 输入框是自绘 TextBox，天生不认图片：不接这一段，Ctrl+V 和拖进来都会"按了没反应"。

    static bool IsImageFile(string p)
    {
        return Vision.IsImageExt(p);      // 判定只留一份（Vision.IsImageExt），避免两处规则漂移
    }

    static string AttachDir()
    {
        string d = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "attachments");
        try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
        return d;
    }

    static string[] ImagePathsFrom(IDataObject data)
    {
        List<string> ok = new List<string>();
        if (data == null) return ok.ToArray();
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return ok.ToArray();
            string[] fs = data.GetData(DataFormats.FileDrop) as string[];
            if (fs == null) return ok.ToArray();
            foreach (string p in fs)
                if (p != null && File.Exists(p) && IsImageFile(p)) ok.Add(p);
        }
        catch { }
        return ok.ToArray();
    }

    static bool HasImageDrop(IDataObject data)
    {
        if (data == null) return false;
        try { if (data.GetDataPresent(DataFormats.Bitmap)) return true; }
        catch { }
        return ImagePathsFrom(data).Length > 0;
    }

    // 返回 true 表示"这一下 Ctrl+V 归我处理"，false 表示是普通文字，交给文本框自己粘
    bool TryPasteImage()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                Image img = Clipboard.GetImage();
                if (img != null)
                {
                    string p = SavePastedImage(img);
                    img.Dispose();
                    if (p != null)
                    {
                        AttachImages(new string[] { p });
                        return true;
                    }
                }
            }
        }
        catch { }
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                System.Collections.Specialized.StringCollection sc = Clipboard.GetFileDropList();
                List<string> imgs = new List<string>();
                foreach (string f in sc)
                    if (f != null && File.Exists(f) && IsImageFile(f)) imgs.Add(f);
                if (imgs.Count > 0) { AttachImages(imgs.ToArray()); return true; }
            }
        }
        catch { }
        return false;
    }

    string SavePastedImage(Image img)
    {
        try
        {
            string p = Path.Combine(AttachDir(), "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
            img.Save(p, System.Drawing.Imaging.ImageFormat.Png);
            return p;
        }
        catch (Exception ex)
        {
            lblStatus.Text = "图片保存失败：" + ex.Message;
            return null;
        }
    }

    void AttachImages(string[] paths)
    {
        if (paths == null || paths.Length == 0) return;
        StringBuilder sb = new StringBuilder();
        int n = 0;
        foreach (string p in paths)
        {
            if (p == null || !File.Exists(p)) continue;
            string full = Path.GetFullPath(p);
            string name = Path.GetFileName(full);
            string mark = "[图片 " + name + "]";
            if (_attachPaths.ContainsKey(mark))
            {
                int k = 2;
                while (_attachPaths.ContainsKey("[图片 " + name + " (" + k + ")]")) k++;
                mark = "[图片 " + name + " (" + k + ")]";
            }
            _attachPaths[mark] = full;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(mark);
            n++;
        }
        if (n == 0) return;
        InsertText(sb.ToString());
        lblStatus.Text = "已加入 " + n + " 张图片，可以直接发给我" + VisionNote();
    }

    // 当前这个模型能不能看图（接口声明过就按接口说，本地按 /api/show 的能力探测）。
    // 粘图的时候直接告诉用户，省得发过去才发现模型"瞎"。
    string VisionNote()
    {
        try
        {
            Provider p = config.Current();
            if (p == null) return "";
            bool? v = p.CanSeeImages(config.model);
            if (v == null && p.type == "ollama" && runner != null)
                v = runner.Capabilities.Contains("vision");
            if (v == true) return "（这个模型能看图）";
            if (v == false) return "（注意：当前模型 " + p.DisplayName(config.model) + " 是纯文本，看不到图；换带视觉的模型才行）";
            return "";
        }
        catch { return ""; }
    }

    static string QuotePath(string p)
    {
        return p.IndexOf(' ') >= 0 ? "\"" + p + "\"" : p;
    }

    // 发送前把输入框里的 [图片 xxx.png] 换回真实路径；模型只认路径，不认短标记。
    string ExpandAttachMarks(string text)
    {
        if (string.IsNullOrEmpty(text) || _attachPaths.Count == 0) return text;
        foreach (KeyValuePair<string, string> kv in _attachPaths)
            text = text.Replace(kv.Key, QuotePath(kv.Value));
        return text;
    }

    void InsertText(string t)
    {
        if (txtInput == null || t == null || t.Length == 0) return;
        string cur = txtInput.Text;
        bool needNl = cur.Length > 0 && !cur.EndsWith("\n");
        txtInput.Text = cur + (needNl ? "\n" : "") + t + "\n";
        txtInput.SelectionStart = txtInput.TextLength;
        txtInput.Focus();
        UpdateSendButton();
    }

    // ============================ 发送 ============================

    // 智能意图澄清（2026-10-16）：跑在回合工作线程上。
    //   返回 null / 空 → 不澄清，用原指令；返回别的字符串 = 用户选中项（或第 3 次自动用推荐项）的 command。
    // 任何一步出问题（没开关 / 没有可用提供商 / 超时 / 非 200 / JSON 解析失败 / options < 2 / 用户跳过）
    // 都退化成"按原指令执行"，绝不阻塞主流程。
    string ClarifyText(string text)
    {
        try
        {
            if (config == null || !config.clarify) return null;
            if (Clarify.LooksStructured(text)) return null;   // 「/计划 开」这类精确命令不拿去问
            string model;
            Provider p = Clarify.PickProvider(config, out model);
            if (p == null) return null;                       // 找不到 deepseek 提供商 / Key 为空 / 模型列表为空 → 静默跳过

            Clarify.Result r = Clarify.Ask(config, text);
            if (!Clarify.ShouldAsk(config, r)) return null;    // 无歧义 / options 不足 2 项 → 原指令直接下发

            // 同一条原始输入已经弹过 2 次澄清：第 3 次不再弹，直接用推荐项（第一个选项）的 command
            if (Clarify.Count(text) >= Clarify.AskLimit)
            {
                string auto = Clarify.CommandOf(r, 0);
                if (auto != null)
                {
                    Log.Audit("意图澄清 · 同一指令已问过 " + Clarify.Count(text) + " 次，改用推荐项：" + Txt.FirstLine(auto));
                    Write(OutKind.Info, "· 这条指令已经确认过 " + Clarify.Count(text) + " 次，这次按推荐项「"
                        + (r.First() == null ? "" : r.First().label) + "」执行\n");
                    return auto;
                }
                return null;
            }

            int pick = AskClarifyOnUi(r);
            Clarify.Bump(text);                                // 弹过才算一次
            if (pick < 0) return null;                         // 跳过 / ESC / 关窗 = 用原指令
            string cmd = Clarify.CommandOf(r, pick);
            return cmd;                                        // 原样下发，不二次拼接
        }
        catch (Exception ex) { Log.Swallow(ex, "意图澄清"); return null; }
    }

    // 弹窗一律回 UI 线程（工作线程直接 new Form 会出跨线程的怪问题）。取不到结果就按跳过处理。
    int AskClarifyOnUi(Clarify.Result r)
    {
        object box = null;
        try
        {
            if (IsDisposed || !IsHandleCreated) return -1;
            box = Invoke(new Func<int>(delegate
            {
                using (ClarifyForm dlg = new ClarifyForm(r.title, r.options))
                {
                    dlg.ShowDialog(this);
                    return dlg.SelectedIndex;
                }
            }));
        }
        catch (Exception ex) { Log.Swallow(ex, "意图澄清弹窗"); return -1; }
        return box == null ? -1 : (int)box;
    }

    void SendMessage()
    {
        if (running) return;
        string text = ExpandAttachMarks(txtInput.Text.Trim());
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
        Task.Run(async delegate
        {
            // 进执行引擎之前先问一句"你到底要哪一种"（意图澄清）。
            // 检测 + 网络都在这个回合线程上；弹窗走 Invoke 回 UI 线程。
            // 检测失败 / 超时 / 用户跳过 → 用原 text 继续，行为与以前完全一致。
            try
            {
                string use = ClarifyText(text);
                if (use != null && use.Length > 0 && use != text) text = use;
            }
            catch (Exception ex) { Log.Swallow(ex, "意图澄清"); }
            try { await runner.RunTurnAsync(text); }
            catch (LocalAgentException ex)
            {
                if (ex.Message != "__CANCELLED__")
                    SafeWrite(OutKind.Error, MainForm.ErrorText("本轮中断", ex.Message,
                        "可以直接重发上一句话重试；若是连接问题，先在设置里点「重新加载模型」"));
                else SafeWrite(OutKind.Error, "\n! 已中断本轮\n");
            }
            catch (Exception ex)
            {
                SafeWrite(OutKind.Error, MainForm.ErrorText("本次操作出错", ex.Message,
                    "可以把上面的错误发给我，或换个说法重试一次"));
            }
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
    }

    // ============================ 窗口事件 ============================

    // ---- 自绘标题栏：干掉系统标题栏（那条"图标 + Ahz Scribe — 本地智能体"），
    //      但仍然保留窗口边框，所以拖动、缩放、贴边分屏、圆角和阴影都还在 ----

    const int WM_NCCALCSIZE = 0x0083;
    const int WM_NCHITTEST = 0x0084;
    const int WM_ACTIVATEAPP = 0x001C;
    const int WM_DPICHANGED = 0x02E0;

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

        if (m.Msg == WM_DPICHANGED)
        {
            // PerMonitorV2：窗口跨屏时按新 DPI 重排，不再重启进程。
            int dpi = (int)(((long)m.WParam >> 16) & 0xFFFF);
            int old = (int)Math.Round(Theme.Dpi * 96);
            if (dpi > 0 && Math.Abs(dpi - old) > 8)
            {
                try
                {
                    Theme.Dpi = dpi / 96f;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        try { LayoutUi(); Invalidate(true); } catch { }
                    }));
                }
                catch { }
            }
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
        if (pt.Y >= 0 && pt.Y <= D(37))
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
        // 顶部这一条是自绘标题栏：跟侧栏一个浅灰色（Codex 的标题栏和侧栏本来就是一块），
        // 底边留一条分隔线。
        using (SolidBrush b = new SolidBrush(Theme.Chrome))
            e.Graphics.FillRectangle(b, 0, 0, ClientSize.Width, D(37));
        using (Pen p = new Pen(Theme.Line, Theme.Pxf(1f)))
            e.Graphics.DrawLine(p, 0, D(37) - 1, ClientSize.Width, D(37) - 1);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            if (_popFilter != null) { Application.RemoveMessageFilter(_popFilter); _popFilter = null; }
            if (runner != null) runner.Cancel();
            SaveCurrentSession();
        }
        catch { }
        base.OnFormClosing(e);
    }

    // 给 PopoverDismissFilter 用：点在 chip（或它的子控件）上不算"点别处"。
    internal bool IsPopoverAnchor(Control c)
    {
        for (Control x = c; x != null; x = x.Parent)
        {
            if (x == cmbModel || x == permChip || x == modeChip) return true;
        }
        return false;
    }

    internal void ClosePopoversFromFilter()
    {
        if (modelPop != null || permPop != null)
        {
            CloseModelPopover();
            ClosePermPopover();
        }
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
        if (cmbModel != null) { cmbModel.Expanded = false; cmbModel.Active = false; cmbModel.Invalidate(); }
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
        cmbModel.Active = true;          // 浮层开着 → chip 浮出一块浅灰底（Codex 那样）
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

    bool _wasMinimized;
    Size _restoreSize;
    bool _restoringSize;
    bool _restorePending;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_restoringSize || _restorePending) return;
        CloseModelPopover();      // 窗口尺寸一变，浮层的绝对位置也过期了
        ClosePermPopover();

        if (WindowState == FormWindowState.Minimized)
        {
            // 最小化前记下真正的普通尺寸。WM_NCCALCSIZE 抹平非客户区后，
            // Win32 会把"窗口尺寸"当成客户区反推，还原时每轮多出 22×56（150%）。
            // 系统还原路径改不动，就在还原的第一时间把窗口尺寸压回这一份。
            _wasMinimized = true;
            return;
        }

        // 最小化→还原：客户区是系统刚还给我们的。先压回最小化前的普通尺寸，再重排 + 整窗重画。
        // 这样既修掉"每轮长大一圈"，也不会停在最小化时算出来的退化布局上。
        if (_wasMinimized)
        {
            _wasMinimized = false;
            // WinForms 在还原消息里会按系统给的新窗口尺寸再盖一次；同步 Size= 会被盖回去。
            // 等这一轮尺寸消息走完，再把窗口压回最小化前记下的普通尺寸。
            if (IsHandleCreated && _restoreSize.Width > 0 && _restoreSize.Height > 0)
            {
                _restorePending = true;
                BeginInvoke(new MethodInvoker(delegate
                {
                    try
                    {
                        if (WindowState == FormWindowState.Normal && Size != _restoreSize)
                        {
                            _restoringSize = true;
                            try { Size = _restoreSize; }
                            catch { }
                            finally { _restoringSize = false; }
                        }
                        LayoutUi();
                        Invalidate(true);
                        Update();
                    }
                    finally { _restorePending = false; }
                }));
                return;
            }
            LayoutUi();
            Invalidate(true);
            Update();
            return;
        }

        // 普通状态下的尺寸变化（用户拖动缩放 / 最大化→还原）才是下一次最小化的基准。
        if (WindowState == FormWindowState.Normal) _restoreSize = Size;
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

        // 菜单里写的那几列快捷键：真的能用（不做"看着有、按了没反应"的假提示）
        bool ctrl = (keyData & Keys.Control) == Keys.Control;
        bool shift = (keyData & Keys.Shift) == Keys.Shift;
        Keys code = keyData & Keys.KeyCode;
        if (ctrl && tabs != null)
        {
            if (code == Keys.N) { NewSession(); return true; }
            if (code == Keys.O) { PickFolder(); return true; }
            if (shift && code == Keys.B) { ToggleBrowser(); return true; }
            if (code == Keys.B) { ToggleSidebar(); return true; }
            if (code == Keys.D1) { tabs.SelectedIndex = 0; SwitchTab(); return true; }
            if (code == Keys.D2) { tabs.SelectedIndex = 1; SwitchTab(); return true; }
            if (code == Keys.Oemcomma) { OpenSettings(); return true; }
            if (code == Keys.Oemplus || code == Keys.Add) { AdjustZoom(1f); return true; }
            if (code == Keys.OemMinus || code == Keys.Subtract) { AdjustZoom(-1f); return true; }
            if (code == Keys.D0) { AdjustZoom(0f); return true; }
            if (code == Keys.OemQuestion) { ShowShortcuts(); return true; }
            if (shift && code == Keys.C)
            {
                string all = rtb != null ? rtb.AllText() : "";
                if (all.Length > 0) { try { Clipboard.SetText(all); } catch { } lblStatus.Text = "已复制整段对话"; }
                return true;
            }
        }
        if (ctrl && code == Keys.W) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

// ============================ 设置窗口（左侧导航 + 右侧内容） ============================

class ModelItem
{
    public string ProviderId;
    public string ProviderName;
    public string Model;
    public string Display;      // 接口给的显示名（没有就跟 Model 一样）
    public bool? Vision;        // 能不能看图（null = 接口没说）
    public override string ToString() { return ProviderName + " / " + Model; }
}

// ============================ 插件窗口 ============================

// 安装插件：从地址装 / 从市场索引一键装
class PluginInstallForm : Form
{
    private readonly AgentConfig _c;
    private readonly Action<string> _onInstalled;
    private FieldBox _addrBox, _marketUrlBox;
    private UiEditor _txtAddr, _txtMarketUrl;
    private UiCheck _chkOverwrite;
    private UiDiff _preview;
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
        h1.Font = Theme.FB(10.5f);
        h1.ForeColor = Theme.Fg;
        h1.BackColor = Theme.Bg;
        h1.AutoSize = true;
        h1.Location = new Point(Theme.Px(Theme.Sp24), Theme.Px(Theme.Sp16));
        _body.Controls.Add(h1);

        Label hint = new Label();
        hint.Text = "可以填：json 直链 / GitHub 的 作者/仓库（会去找仓库根目录的 plugin.json）/ 本地 json 路径";
        hint.ForeColor = Theme.FgDim;
        hint.BackColor = Theme.Bg;
        hint.Font = Theme.F(8.5f);
        hint.AutoSize = true;
        hint.Location = new Point(Theme.Px(Theme.Sp24), Theme.Px(Theme.Sp24) + Theme.Px(Theme.Sp24));
        _body.Controls.Add(hint);

        _addrBox = new FieldBox(false);
        _addrBox.BackColor = Theme.Bg;
        _addrBox.SetBounds(26, 70, 520, 34);
        _body.Controls.Add(_addrBox);
        _txtAddr = (UiEditor)_addrBox.Inner;

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

        _chkOverwrite = new UiCheck();
        _chkOverwrite.Label = "覆盖已存在的插件";
        _chkOverwrite.AccessibleName = "覆盖已存在的插件";
        _chkOverwrite.Font = Theme.F(9.5f);
        _chkOverwrite.Size = new Size(Theme.Px(220), Theme.Px(23));
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
        _previewCard.Radius = Theme.RadCard;
        _previewCard.Fill = Theme.Card;
        _previewCard.BorderColor = Theme.Line;
        _previewCard.BackColor = Theme.Bg;
        _previewCard.Padding = new Padding(Theme.Sp16, Theme.Sp12, Theme.Sp16, Theme.Sp12);
        _previewCard.SetBounds(26, 140, 722, 180);
        _body.Controls.Add(_previewCard);

        _preview = new UiDiff();
        _preview.Dock = DockStyle.Fill;
        _preview.Text = "还没有获取内容。填好地址点「获取」，这里会显示这个插件声明了什么。";
        _previewCard.Controls.Add(_preview);

        Label warn = new Label();
        warn.Text = "⚠ 插件能让 agent 跑 PowerShell 命令、发网络请求。只装你信任的来源，装完先看一眼上面的预览。";
        warn.ForeColor = Theme.Warn;
        warn.BackColor = Theme.Bg;
        warn.Font = Theme.F(8.5f);
        warn.AutoSize = true;
        warn.Location = new Point(28, 328);
        _body.Controls.Add(warn);

        _lblMarketHead = new Label();
        _lblMarketHead.Text = "插件市场";
        _lblMarketHead.ForeColor = Theme.Fg;
        _lblMarketHead.BackColor = Theme.Bg;
        _lblMarketHead.Font = Theme.FB(10.5f);
        _lblMarketHead.AutoSize = true;
        _lblMarketHead.Location = new Point(26, 362);
        _body.Controls.Add(_lblMarketHead);

        _lblMarketSrc = new Label();
        _lblMarketSrc.ForeColor = Theme.FgFaint;
        _lblMarketSrc.BackColor = Theme.Bg;
        _lblMarketSrc.Font = Theme.F(8.5f);
        _lblMarketSrc.AutoSize = false;
        _lblMarketSrc.SetBounds(120, 368, 628, 18);
        _body.Controls.Add(_lblMarketSrc);

        _marketUrlBox = new FieldBox(false);
        _marketUrlBox.BackColor = Theme.Bg;
        _marketUrlBox.SetBounds(26, 394, 470, 34);
        _body.Controls.Add(_marketUrlBox);
        _txtMarketUrl = (UiEditor)_marketUrlBox.Inner;
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
        _marketCard.Radius = Theme.RadCard;
        _marketCard.Fill = Theme.Card;
        _marketCard.BorderColor = Theme.Line;
        _marketCard.BackColor = Theme.Bg;
        _marketCard.Padding = new Padding(Theme.Sp4);
        _marketCard.SetBounds(26, 438, 722, 130);
        _body.Controls.Add(_marketCard);

        _market = new UiList();
        _market.Dock = DockStyle.Fill;
        _market.BorderStyle = BorderStyle.None;
        _market.BackColor = Theme.Card;
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
        _lblMsg.Font = Theme.F(9.5f);
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
        _btnClose.Radius = Theme.RadCard;
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
        int m = Theme.Px(Theme.Sp24);
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
        b.Radius = Theme.RadRow;
        b.Font = Theme.F(9.5f);
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
        UiMsg.Show(this, msg + "\n\n可以在「插件」窗口里启用/停用它。", "安装完成",
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
        if (UiMsg.Show(this, "要安装「" + (e.name == null ? e.url : e.name) + "」吗？\n\n插件能让 agent 跑命令，只装你信任的来源。",
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
    private UiCheck _chkEnabled;
    private UiDiff _detail;
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
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(0, Theme.Px(40), 0, 0);

        BuildChrome();
        if (Theme.Dpi > 1.01f)
        {
            Theme.ScaleBounds(this, Theme.Dpi);
            Size = new Size(Theme.Px(900), Theme.Px(620));
            MinimumSize = new Size(Theme.Px(760), Theme.Px(520));
        }
        Reload();
        ChromeBar bar = new ChromeBar("Ahz Scribe — 插件");
        bar.Bounds = new Rectangle(0, 0, ClientSize.Width, Theme.Px(40));
        bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        bar.Closed += delegate { Close(); };
        Controls.Add(bar);
        bar.BringToFront();
        SizeGrip grip = new SizeGrip(this);
        grip.SetBounds(ClientSize.Width - grip.Width, ClientSize.Height - grip.Height, grip.Width, grip.Height);
        Controls.Add(grip);
        grip.BringToFront();
        Height += Theme.Px(40);
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
        listCard.Radius = Theme.RadCard;
        listCard.Fill = Theme.Card;
        listCard.BorderColor = Theme.Line;
        listCard.BackColor = Theme.Side;
        listCard.Padding = new Padding(Theme.Sp4);
        listCard.SetBounds(14, 76, 222, 400);
        // 残留的 Anchor 删掉：缩放 + 锚点会互相打架（算出来的位置会跑到窗口外面），这里不用它
        nav.Controls.Add(listCard);

        _list = new UiList();
        _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Theme.Card;
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
        body.Padding = new Padding(Theme.Sp24, Theme.Sp24, Theme.Sp24, 0);

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
        _lblMeta.Font = Theme.F(9.5f);
        _lblMeta.AutoSize = true;
        _lblMeta.Location = new Point(28, 50);
        body.Controls.Add(_lblMeta);

        _chkEnabled = new UiCheck();
        _chkEnabled.Label = "启用这个插件（启用的插件，它的工具会出现在模型的工具列表里）";
        _chkEnabled.AccessibleName = "启用这个插件";
        _chkEnabled.Font = Theme.F(9.5f);
        _chkEnabled.Size = new Size(Theme.Px(680), Theme.Px(23));
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
        _lblPath.Font = Theme.F(8.5f);
        _lblPath.AutoSize = false;
        _lblPath.SetBounds(26, 150, 560, 20);
        body.Controls.Add(_lblPath);

        _lblToolsHead = new Label();
        _lblToolsHead.Text = "工具";
        _lblToolsHead.ForeColor = Theme.Fg;
        _lblToolsHead.BackColor = Theme.Bg;
        _lblToolsHead.Font = Theme.FB(10.5f);
        _lblToolsHead.AutoSize = true;
        _lblToolsHead.Location = new Point(26, 182);
        body.Controls.Add(_lblToolsHead);

        _card = new RoundedPanel();
        _card.Radius = 12;
        _card.Fill = Theme.Card;
        _card.BorderColor = Theme.Line;
        _card.BackColor = Theme.Bg;
        _card.Padding = new Padding(Theme.Sp16, Theme.Sp12, Theme.Sp16, Theme.Sp12);
        _card.SetBounds(26, 210, 600, 300);
        body.Controls.Add(_card);

        _detail = new UiDiff();
        _detail.Dock = DockStyle.Fill;
        _card.Controls.Add(_detail);

        Label foot = new Label();
        foot.Text = "插件工具默认每次都要你点「允许」才会执行（shell 类型始终需要确认）。插件文件放在程序目录的 plugins\\ 里，一个 json 就是一个插件。";
        foot.ForeColor = Theme.FgFaint;
        foot.BackColor = Theme.Bg;
        foot.Font = Theme.F(8.5f);
        foot.AutoSize = false;
        foot.SetBounds(26, 522, 600, 40);
        body.Controls.Add(foot);
        body.Resize += delegate
        {
            int m = Theme.Px(Theme.Sp24);
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
        _btnClose.Radius = Theme.RadCard;
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
        b.Radius = Theme.RadRow;
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
        TextRenderer.DrawText(e.Graphics, sub, Theme.F(8.5f), sr, p.Error != null ? Theme.Err : Theme.FgFaint,
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
    private UiEditor _txtName;
    private UiDrop _cmbType;                  // 原来是系统 ComboBox（FlatStyle=Flat），和另外两个下拉不是一套质感
    private UiEditor _txtBase;
    private UiPasswordBox _txtKey;
    private UiEditor _txtModels;
    private UiCheck _chkShowKey;
    private Label _lblHint;
    private bool _loading;

    private UiEditor _txtCwd;
    private UiDrop _cmbCtx;
    private UiNumber _numSteps;
    private UiNumber _numTimeout;
    private UiCheck _chkThink;
    private UiDrop _cmbPerm;
    private Action<string> _onPermChanged;    // 设置页改权限档时通知主窗口刷新 chip
    private UiCheck _chkOutside;
    private UiCheck _chkVerify;
    private UiCheck _chkHideThink;
    private UiCheck _chkClarify;         // 意图澄清开关（默认开）
    private UiCheck _chkRemote;
    private Panel _navPanel;
    private Label _lblSec;
    private UiButton _btnAddProvider, _btnDelProvider, _btnFetch;
    private RoundedPanel _lstCard;
    private FieldBox _modelsBox;
    private string _autoBase = "";      // 上一次"自动填进去"的接口地址（用户手改过就不再覆盖）
    private readonly ToolTip _tip = new ToolTip();   // 模型列表明细（接口声明了哪些能力）挂在它上面
    private FieldBox _cwdBox;
    private UiButton _btnPickCwd;
    private RoundedPanel _generalCard, _aboutCard;
    // 运行日志页（位置一律由 LayoutLogPage 按页面尺寸算，不用 Anchor）
    private Panel _logPage;
    private Label _logHead, _logTip;
    private UiDiff _logBox;
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
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(0, Theme.Px(40), 0, 0);
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
        ChromeBar bar = new ChromeBar("Ahz Scribe — 设置");
        bar.Bounds = new Rectangle(0, 0, ClientSize.Width, Theme.Px(40));
        bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        bar.Closed += delegate { Close(); };
        Controls.Add(bar);
        bar.BringToFront();
        Height += Theme.Px(40);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundWindow(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.DarkenTree(this);
    }

    // 和主窗口一样：按屏幕 DPI 等比放大，否则 150% 缩放下字挤在一起
    void ApplyDpiScale()
    {
        Theme.EnsureDpi();
        if (Theme.Dpi > 1.01f)
        {
            Theme.ScaleBounds(this, Theme.Dpi);
            Size = new Size(Theme.Px(1000), Theme.Px(660));
            MinimumSize = new Size(Theme.Px(820), Theme.Px(540));
        }
        // 这三步在 100% 缩放下也要跑：页面刚建出来时还是 200×100 的隐藏尺寸，
        // 不重排会出现"卡片装不下最后一个复选框"（以前只在 DPI>1 时调用，100% 下同样有坑）。
        LayoutGeneralFields();
        LayoutModelPage();     // 按放大后的页面尺寸重新摆一遍（否则底部按钮会被推到可视区外）
        LayoutOtherPages();
        LayoutLogPage();
        RepositionFields(this);   // 输入框内部文字也重摆一遍（防"比框宽 / 偏下"）
        LayoutFields(this);       // 下拉那类"没有 Inner"的 FieldBox：把子控件铺满框内
    }

    // 通用设置页的"审批与安全"卡片：权限下拉和下面的复选框按实际高度顺排。
    // 不能只写死 y=80/112：_cmbPerm 的高度已经含 DPI（Theme.Px(CtrlHeight)），
    // ScaleBounds 之后会再长一截，写死 112 的复选框就会被下拉压住（2026-10-18 验收 P0）。
    void LayoutGeneralFields()
    {
        if (_cmbPerm == null || _chkOutside == null) return;
        // _cmbPerm 的 y/宽已经是放大后的值；高度在 Build 时先乘过一次 DPI，这里拉回本 DPI 下的 CtrlHeight。
        _cmbPerm.Height = Theme.Px(Theme.CtrlHeight);
        int left = _chkOutside.Left;
        int y = _cmbPerm.Bottom + Theme.Px(Theme.Sp8);
        _chkOutside.Location = new Point(left, y);
        y = _chkOutside.Bottom + Theme.Px(Theme.Sp4);
        _chkVerify.Location = new Point(left, y);
        y = _chkVerify.Bottom + Theme.Px(Theme.Sp4);
        _chkHideThink.Location = new Point(left, y);
        y = _chkHideThink.Bottom + Theme.Px(Theme.Sp4);
        _chkClarify.Location = new Point(left, y);
        y = _chkClarify.Bottom + Theme.Px(Theme.Sp4);
        _chkRemote.Location = new Point(left, y);
    }

    // 和 RepositionFields 成对：FieldBox 里的 Inner 被换成 UiDrop 之后，ScaleBounds 不递归进
    // FieldBox、Reposition 又只管 Inner —— 结果就是"小下拉框待在大空框左上角"。
    // 这里统一补一刀：子控件按框当前尺寸铺满（留 1px 给外框描边）。
    static void LayoutFields(Control root)
    {
        foreach (Control c in root.Controls)
        {
            FieldBox fb = c as FieldBox;
            if (fb != null)
            {
                if (fb.Inner != null) fb.Reposition();
                else
                {
                    int i = Theme.Px(1);
                    foreach (Control kid in fb.Controls)
                        kid.SetBounds(i, i, Math.Max(Theme.Px(10), fb.ClientSize.Width - i * 2),
                            Math.Max(Theme.Px(10), fb.ClientSize.Height - i * 2));
                }
            }
            if (c.Controls.Count > 0) LayoutFields(c);
        }
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
        _navPanel.Width = 212;                    // 栅格：212 = 侧栏内边距 Sp16 两侧 + 行宽 180
        _navPanel.BackColor = Theme.Side;
        _navPanel.Paint += delegate(object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Theme.Line, 1f))
                e.Graphics.DrawLine(p, _navPanel.Width - 1, 0, _navPanel.Width - 1, _navPanel.Height);
        };

        LogoMark logo = new LogoMark();
        logo.BackColor = Theme.Side;
        // 这里一律写逻辑值：ApplyDpiScale() 还会对整窗做一次 ScaleBounds，
        // 先乘 Theme.Px 等于乘两次（2026-10-18 验收：导航按钮 405 > 父容器 318）。
        logo.SetBounds(Theme.Sp16, Theme.Sp24, Theme.Sp24, Theme.Sp24);
        _navPanel.Controls.Add(logo);

        Label title = new Label();
        title.Text = "设置";
        title.Font = Theme.FB(12.5f);
        title.ForeColor = Theme.Fg;
        title.AutoSize = true;
        title.BackColor = Theme.Side;
        title.Location = new Point(Theme.Sp16 + 34, Theme.Sp24);
        _navPanel.Controls.Add(title);

        string[] names = new string[] { "通用设置", "模型", "运行日志", "关于" };
        string[] icons = new string[] { "gear", "spark", "list", "info" };
        // 和主窗体同一套节奏：行高 36、行间距 40、左内边距走令牌（逻辑值，交给 ScaleBounds）
        int navRowH = 36, navStep = 40, navTop = 72;
        _nav = new UiButton[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            UiButton b = new UiButton();
            b.Text = names[i];
            b.Tag = i;
            b.Icon = icons[i];
            b.IconSize = 16;
            b.IconGap = Theme.Sp8;
            b.Align = ContentAlignment.MiddleLeft;
            b.Padding = new Padding(Theme.Sp12, 0, 0, 0);
            b.FillColor = Theme.Clear;
            b.HoverColor = Theme.Hover;
            b.TextColor = Theme.FgMid;
            b.ActiveFill = Theme.AccentSoft;
            b.ActiveText = Theme.Accent;
            b.Radius = Theme.RadMenu;              // 标题栏那些小按钮吸附 RadMenu
            b.Font = Theme.F(9.5f);
            b.SetBounds(Theme.Sp16, navTop + i * navStep, 180, navRowH);
            b.Click += delegate(object s, EventArgs e) { ShowPage((int)((UiButton)s).Tag); };
            _navPanel.Controls.Add(b);
            _nav[i] = b;
        }

        _lblSec = new Label();
        _lblSec.Text = "改完记得点右下角「保存」";
        _lblSec.ForeColor = Theme.FgFaint;
        _lblSec.BackColor = Theme.Side;
        _lblSec.Font = Theme.F(8.5f);
        _lblSec.AutoSize = false;
        _lblSec.SetBounds(Theme.Sp16, navTop + names.Length * navStep + Theme.Sp16,
            180, 40);
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
        ok.Radius = Theme.RadCard;
        ok.Font = Theme.F(9.5f);
        ok.Size = new Size(96, 36);
        ok.Click += delegate { Save(); };

        UiButton cancel = new UiButton();
        cancel.Text = "取消";
        cancel.BorderColor = Theme.Line;
        cancel.FillColor = Color.White;
        cancel.HoverColor = Theme.Hover;
        cancel.Radius = Theme.RadCard;
        cancel.Font = Theme.F(9.5f);
        cancel.Size = new Size(88, 36);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);
        bottom.Layout += delegate
        {
            cancel.Location = new Point(bottom.ClientSize.Width - Theme.Px(22) - cancel.Width, Theme.Px(14));
            ok.Location = new Point(cancel.Left - Theme.Px(10) - ok.Width, Theme.Px(14));
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
        b.Radius = Theme.RadRow;
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
        l.Font = Theme.FB(10.5f);
        return l;
    }

    FieldBox MakeField(Panel p, int x, int y, int w, int h, bool multiline, bool password = false)
    {
        FieldBox f = new FieldBox(multiline, password);
        f.SetBounds(x, y, w, h);
        f.BackColor = p.BackColor;
        p.Controls.Add(f);
        return f;
    }

    static RoundedPanel Card(Panel p, int x, int y, int w, int h)
    {
        RoundedPanel c = new RoundedPanel();
        c.Radius = 10;
        c.Fill = Theme.Card;
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
        _txtCwd = (UiEditor)cwd.Inner;
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
        ctx.DrawBorder = false;                   // 同三个下拉：外框描边关掉，只留里面这颗 UiDrop 自己的描边
        ctx.Radius = Theme.RadRow;
        ctx.Controls.Remove(ctx.Inner);
        _cmbCtx = new UiDrop();
        _cmbCtx.Text = "8192";
        _cmbCtx.FillColor = Color.White;
        _cmbCtx.BorderColor = Theme.Line;
        _cmbCtx.HoverColor = Theme.Hover;
        _cmbCtx.Radius = Theme.RadRow;
        _cmbCtx.Font = Theme.F(9.5f);
        _cmbCtx.AddRange(new object[] { "2048", "4096", "8192", "16384", "32768" });
        // 摆位交给 LayoutFields（DPI 放大后它会按 FieldBox 的实际尺寸铺满，不再"小下拉框待在空框左上角"）
        _cmbCtx.SetBounds(Theme.Px(1), Theme.Px(1), ctx.Width - Theme.Px(2), Theme.Px(Theme.CtrlHeight));
        ctx.Controls.Add(_cmbCtx);
        Label ctxHint = Label2("本地模型越大越慢；云端模型由服务商决定", 306, 169);
        ctxHint.ForeColor = Theme.FgFaint;
        ctxHint.Font = Theme.F(8.5f);
        p.Controls.Add(ctxHint);

        p.Controls.Add(Label2("单轮最多工具步数", 28, 216));
        FieldBox steps = MakeField(p, 176, 210, 92, 34, false);
        steps.Controls.Remove(steps.Inner);
        _numSteps = new UiNumber();
        _numSteps.Minimum = 1;
        _numSteps.Maximum = 200;
        _numSteps.BackColor = Theme.Card;
        _numSteps.ForeColor = Theme.Fg;
        _numSteps.Font = Theme.F(9.5f);
        _numSteps.SetBounds(10, 8, 72, 22);
        steps.Controls.Add(_numSteps);

        p.Controls.Add(Label2("命令超时（秒）", 300, 216));
        FieldBox timeout = MakeField(p, 424, 210, 100, 34, false);
        timeout.Controls.Remove(timeout.Inner);
        _numTimeout = new UiNumber();
        _numTimeout.Minimum = 5;
        _numTimeout.Maximum = 3600;
        _numTimeout.BackColor = Theme.Card;
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
        ch.BackColor = Theme.Card;
        ch.AutoSize = true;
        ch.Location = new Point(20, 18);
        card.Controls.Add(ch);

        _chkThink = Check2(card, "默认打开思考模式（本地小模型开着明显变慢）", 20, 52);
        // 权限档：三档（仅可查看 / 工作区内修改 / 完全权限），和输入条上那个浮层是同一套取值
        Label pl = new Label();
        pl.Text = "权限档";
        pl.Font = Theme.F(9.5f);
        pl.ForeColor = Theme.FgMid;
        pl.BackColor = Theme.Card;
        pl.AutoSize = true;
        pl.Location = new Point(20, 85);
        card.Controls.Add(pl);
        _cmbPerm = new UiDrop();
        _cmbPerm.FillColor = Color.White;         // 原来没设 → 落到 UiButton 默认的灰底，和另外两个下拉不是一套
        _cmbPerm.BorderColor = Theme.Line;
        _cmbPerm.HoverColor = Theme.Hover;
        _cmbPerm.Font = Theme.F(9.5f);
        _cmbPerm.Radius = Theme.RadRow;
        _cmbPerm.Padding = new Padding(Theme.Sp12, 0, Theme.Sp12, 0);
        for (int i = 0; i < Perm.Labels.Length; i++) _cmbPerm.Items.Add(Perm.Labels[i]);
        _cmbPerm.SelectedItem = Perm.Label(_c.permission);
        _cmbPerm.SelectedIndexChanged += delegate
        {
            string lv = Perm.ValueAt(_cmbPerm.SelectedIndex);
            _c.permission = lv;
            _c.auto_yes = Perm.AutoYes(lv);
            if (_onPermChanged != null) _onPermChanged(lv);
        };
        _cmbPerm.SetBounds(90, 80, 150, Theme.Px(Theme.CtrlHeight));
        card.Controls.Add(_cmbPerm);
        _chkOutside = Check2(card, "允许访问工作目录以外的路径（关闭沙箱）", 20, 112);
        _chkVerify = Check2(card, "改完文件后自动跑一次项目验证（测试/编译），失败自动回灌给模型", 20, 142);
        _chkHideThink = Check2(card, "思考过程只进「轨迹」页（对话区不被长思考一直往上顶，推荐开）", 20, 172);
        // 意图澄清：发指令后先跑一次歧义检测（固定用 deepseek-flash），有歧义就弹框让你选具体方向
        _chkClarify = Check2(card, "发指令前先做一次意图澄清（用 deepseek-flash 判歧义，模糊时弹框让你选具体方向）", 20, 202);
        _chkRemote = Check2(card, "高危操作走「Ahz 通信台」远程审批（需要先启动控制台；通信台不可用时回退本地弹窗）", 20, 232);
    }

    UiCheck Check2(Control parent, string text, int x, int y)
    {
        UiCheck c = new UiCheck();
        c.Label = text;
        c.Location = new Point(x, y);
        c.Font = Theme.F(9.5f);
        c.ForeColor = Theme.FgMid;
        c.BackColor = parent.BackColor;
        int w = Math.Min(Theme.Px(700), Ui.TextWidth(text, c.Font) + Theme.Px(34));
        c.Size = new Size(Math.Max(Theme.Px(120), w), Theme.Px(23));
        c.AccessibleName = text;
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
        _chkClarify.Checked = _c.clarify;
        _chkRemote.Checked = _c.remote_approval;
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
        listCard.Padding = new Padding(Theme.Sp4);
        _lstProviders = new UiList();
        _lstProviders.Dock = DockStyle.Fill;
        _lstProviders.BorderStyle = BorderStyle.None;
        _lstProviders.BackColor = Theme.Card;
        _lstProviders.ForeColor = Theme.Fg;
        _lstProviders.Font = Theme.F(9.5f);
        _lstProviders.DrawMode = DrawMode.OwnerDrawFixed;
        _lstProviders.ItemHeight = Theme.RowHeight;
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
            TextRenderer.DrawText(e.Graphics, sub, Theme.F(8.5f), tr, Theme.FgFaint,
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
            if (_c.providers.Count <= 1) { UiMsg.Show(this, "至少要保留一个提供商。", "Ahz Scribe"); return; }
            if (UiMsg.Show(this, "确定删除「" + sel.name + "」？", "Ahz Scribe", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            _c.providers.Remove(sel);
            RefreshProviderList();
            _lblHint.Text = "已删除「" + sel.name + "」";
        };
        p.Controls.Add(_btnDelProvider);

        int lx = 272, fx = 380;
        p.Controls.Add(Label2("名称", lx, 88));
        FieldBox nameB = MakeField(p, fx, 80, 370, 34, false);      // 四行输入框统一 370 宽，看着才齐
        _txtName = (UiEditor)nameB.Inner;
        _txtName.TextChanged += delegate
        {
            StoreProviderFields();
            ApplyPresetToFields();          // 名称里出现 glm / kimi / 通义 … 就自动把接口地址填上
        };

        p.Controls.Add(Label2("类型", lx, 136));
        FieldBox typeB = MakeField(p, fx, 128, 370, 34, false);
        typeB.DrawBorder = false;                 // 外框不画描边：三个下拉统一"白底 + 1px Line 描边"，不再套双层框
        typeB.Radius = Theme.RadRow;
        typeB.Controls.Remove(typeB.Inner);
        _cmbType = new UiDrop();
        _cmbType.FillColor = Color.White;
        _cmbType.BorderColor = Theme.Line;
        _cmbType.HoverColor = Theme.Hover;
        _cmbType.Radius = Theme.RadRow;
        _cmbType.Font = Theme.F(9.5f);
        _cmbType.Items.AddRange(new object[] { "本地 Ollama", "OpenAI 兼容接口（DeepSeek / OpenAI / Kimi / 智谱 …）" });
        _tip.SetToolTip(_cmbType, "本地 Ollama / OpenAI 兼容接口（DeepSeek / OpenAI / Kimi / 智谱 …）");
        _cmbType.SetBounds(Theme.Px(1), Theme.Px(1), typeB.Width - Theme.Px(2), Theme.Px(Theme.CtrlHeight));
        _cmbType.SelectedIndexChanged += delegate { StoreProviderFields(); };
        typeB.Controls.Add(_cmbType);

        p.Controls.Add(Label2("接口地址", lx, 184));
        FieldBox baseB = MakeField(p, fx, 176, 370, 34, false);
        _txtBase = (UiEditor)baseB.Inner;
        _txtBase.TextChanged += delegate { StoreProviderFields(); };
        Label bh = Label2("例如 https://api.deepseek.com/v1 或 http://127.0.0.1:11434", fx, 214);
        bh.ForeColor = Theme.FgFaint;
        bh.Font = Theme.F(8.5f);
        p.Controls.Add(bh);

        p.Controls.Add(Label2("API Key", lx, 246));
        FieldBox keyB = MakeField(p, fx, 238, 370, 34, false, true);      // 智谱的 Key 很长，这里给满剩下的宽度
        _txtKey = (UiPasswordBox)keyB.Inner;
        _txtKey.UseSystemPasswordChar = true;
        _txtKey.TextChanged += delegate
        {
            StoreProviderFields();
            // 填了 Key、有地址、但模型列表还是空的：提示一下可以一键拉取
            if (_txtModels != null && _txtModels.Text.Trim().Length == 0
                && _txtBase.Text.Trim().Length > 0 && _txtKey.Text.Trim().Length > 0)
                _lblHint.Text = "点「从接口拉取模型」就能拿到这个 Key 能用的模型";
        };
        _chkShowKey = new UiCheck();
        _chkShowKey.Label = "显示";
        _chkShowKey.AccessibleName = "显示 API Key";
        _chkShowKey.Font = Theme.F(9.5f);
        _chkShowKey.Size = new Size(Theme.Px(84), Theme.Px(23));
        // 「显示」挪到"API Key"标签和输入框之间：这样密钥框能吃到整行剩下的宽度（长 Key 才看得全）
        _chkShowKey.Location = new Point(lx + 62, 246);
        _chkShowKey.ForeColor = Theme.FgMid;
        _chkShowKey.BackColor = p.BackColor;
        _chkShowKey.CheckedChanged += delegate { _txtKey.UseSystemPasswordChar = !_chkShowKey.Checked; };
        p.Controls.Add(_chkShowKey);

        p.Controls.Add(Label2("模型列表（每行一个）", lx, 286));
        FieldBox modelsB = MakeField(p, fx, 306, 370, 172, true);
        _modelsBox = modelsB;
        _txtModels = (UiEditor)modelsB.Inner;
        _txtModels.Multiline = true;
        _txtModels.ScrollBars = ScrollBars.None;
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
        _lblHint.AutoEllipsis = true;
        _lblHint.TextAlign = ContentAlignment.MiddleLeft;
        _lblHint.SetBounds(fx, 526, 540, 22);
        _lblHint.ForeColor = Theme.FgDim;
        _lblHint.TextChanged += delegate { _tip.SetToolTip(_lblHint, _lblHint.Text); };
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
                if (_chkClarify != null) cardH = Math.Max(cardH, _chkClarify.Bottom + Theme.Px(18));
                if (_chkRemote != null) cardH = Math.Max(cardH, _chkRemote.Bottom + Theme.Px(18));
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

        _logBox = new UiDiff();
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.None;
        _logBox.WordWrap = true;
        _logBox.Font = new Font("Consolas", 9.5f);
        _logBox.BackColor = Theme.Card;
        _logBox.ForeColor = Theme.Fg;
        _logBox.BorderStyle = BorderStyle.None;
        // 位置/大小交给 LayoutLogPage：Anchor 在 DPI 放大后会把控件推出可视区
        p.Controls.Add(_logBox);

        _btnLogRefresh = Btn("刷新", 0, 0, 88, 34);
        _btnLogRefresh.Click += delegate { RequestLogRefresh(); };
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

    // 日志页刷新（2026-10-16 防抖版）：
    //   1) 只读末尾 64KB —— 日志可以很大，整读会把 UI 拖住；
    //   2) 内容没变就一个像素都不动 —— 也不动滚动位置（以前每次都 ScrollToCaret 把用户顶到底部）；
    //   3) 短时间内的多次请求合并成一次（120ms），连点刷新也不会反复整屏重排。
    string _logShown;
    System.Windows.Forms.Timer _logDebounce;

    void RequestLogRefresh()
    {
        if (_logDebounce == null)
        {
            _logDebounce = new System.Windows.Forms.Timer();
            _logDebounce.Interval = 120;
            _logDebounce.Tick += delegate { _logDebounce.Stop(); LoadLogText(); };
        }
        _logDebounce.Stop();
        _logDebounce.Start();
    }

    void LoadLogText()
    {
        if (_logBox == null) return;
        string text;
        try { text = Log.ReadTail(64 * 1024); }
        catch (Exception ex) { Log.Swallow(ex, "读日志尾巴"); return; }
        if (text == _logShown) return;              // 没变就一个像素都不动
        bool atBottom = _logBox.SelectionStart >= _logBox.TextLength - 1;
        _logShown = text;
        _logBox.Text = text;
        if (atBottom)                                // 用户本来就在底部才跟着走，否则保住他的浏览位置
        {
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
    }

    // 位置全按页面实际尺寸算：日志框撑满（底部给按钮留一条），按钮排在框下面
    void LayoutLogPage()
    {
        if (_logPage == null || _logBox == null || _btnLogRefresh == null) return;
        int pageW = _logPage.ClientSize.Width;
        int pageH = _logPage.ClientSize.Height;
        int pad = Theme.Px(Theme.Sp24);
        int top = Theme.Px(92);
        int btnH = Theme.Px(34);
        int gap = Theme.Px(Theme.Sp12);
        int boxH = Math.Max(Theme.Px(120), pageH - top - btnH - gap - Theme.Px(Theme.Sp24));
        _logBox.SetBounds(pad, top, Math.Max(Theme.Px(200), pageW - pad * 2), boxH);

        int by = top + boxH + gap;
        int w1 = Theme.Px(88), w2 = Theme.Px(140), w3 = Theme.Px(88);
        _btnLogRefresh.SetBounds(pad, by, w1, btnH);
        _btnLogOpen.SetBounds(pad + w1 + Theme.Px(Theme.Sp8), by, w2, btnH);
        _btnLogClear.SetBounds(pad + w1 + Theme.Px(Theme.Sp8) + w2 + Theme.Px(Theme.Sp8), by, w3, btnH);
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
        MenuRound.Apply(m);
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
        // 之前"拉取"时接口声明过的能力，这里不联网就能标一眼
        _lblHint.Text = CapabilitySummary(p);
        _tip.SetToolTip(_modelsBox, CapabilityDetail(p));
        _loading = false;
    }

    // 接口声明过什么能力（读 config 里存着的那份，不联网）
    static string CapabilitySummary(Provider p)
    {
        int vis = 0, eff = 0, known = 0;
        if (p != null && p.model_meta != null)
            foreach (KeyValuePair<string, ModelMeta> kv in p.model_meta)
            {
                ModelMeta m = kv.Value;
                if (m == null) continue;
                known++;
                if (m.vision == true) vis++;
                if (m.efforts != null && m.efforts.Count >= 2) eff++;
            }
        if (known == 0) return "";
        return "接口声明过：" + (vis > 0 ? ("能看图 " + vis + " 个") : "没有能看图的")
            + " · " + (eff > 0 ? ("能调档 " + eff + " 个") : "都不支持调档")
            + "（明细：鼠标停在上面那栏模型列表上）";
    }

    // 每个模型一行明细：显示名 / 能不能看图 / 档位 / 上下文长度
    static string CapabilityDetail(Provider p)
    {
        if (p == null || p.model_meta == null || p.model_meta.Count == 0)
            return "还没从接口拉过这个提供商的清单 —— 点「从接口拉取模型」，接口声明的能力会一起记下来。";
        StringBuilder sb = new StringBuilder();
        foreach (KeyValuePair<string, ModelMeta> kv in p.model_meta)
        {
            ModelMeta m = kv.Value;
            if (m == null) continue;
            if (sb.Length > 0) sb.Append("\n");
            sb.Append(m.Display(m.id));
            sb.Append(m.vision == true ? "：能看图" : (m.vision == false ? "：纯文本" : "：看图能力未知"));
            if (m.efforts != null && m.efforts.Count >= 2)
                sb.Append(" · 档位 " + string.Join("/", m.efforts.ToArray()));
            if (m.ctx > 0) sb.Append(" · 上下文 " + m.ctx);
        }
        return sb.ToString();
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
            UiMsg.Show(this, "还没有填接口地址。\n在「名称」里写平台名（deepseek / glm / kimi …）会自动填上。",
                "拉取模型列表", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _btnFetch.Enabled = false;
        _lblHint.Text = "正在拉取…";
        Application.DoEvents();
        try
        {
            ILlm llm = LlmFactory.Create(p, "");
            List<ModelMeta> infos = llm.ListModelInfos();
            llm.Close();
            if (infos.Count == 0)
            {
                _lblHint.Text = "接口没返回任何模型";
                return;
            }
            _lblHint.Text = MergeModelList(p, infos);
        }
        catch (Exception ex)
        {
            _lblHint.Text = "拉取失败";
            UiMsg.Show(this, ex.Message + "\n\n检查一下接口地址、API Key，以及这个平台是否兼容 OpenAI 的 /models 接口。",
                "拉取模型列表失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnFetch.Enabled = true;
        }
    }

    // 把拉到的模型并进"模型列表"：已有的保留，只补新的（换 Key / 重拉都不会把你手写的删掉）；
    // 顺手把接口给的能力标签（显示名 / 能不能看图 / 档位 / 上下文）存进这个提供商。
    string MergeModelList(Provider p, List<ModelMeta> infos)
    {
        List<string> have = new List<string>();
        foreach (string line in _txtModels.Text.Replace("\r\n", "\n").Split('\n'))
        {
            string m = line.Trim();
            if (m.Length > 0 && !have.Contains(m)) have.Add(m);
        }
        int added = 0;
        foreach (ModelMeta mi in infos)
        {
            if (mi == null || mi.id == null || mi.id.Trim().Length == 0) continue;
            if (!have.Contains(mi.id)) { have.Add(mi.id); added++; }
            if (p != null) p.SetMeta(mi);        // 能力标签：拉一次记一次
        }
        _loading = true;
        _txtModels.Text = string.Join("\r\n", have.ToArray());
        _loading = false;
        StoreProviderFields();
        _tip.SetToolTip(_modelsBox, CapabilityDetail(p));
        // 提示语一行放不下太多字：这里只报"个数"，明细挂在模型列表的悬停提示里
        int vis = 0, eff = 0;
        foreach (ModelMeta mi in infos)
        {
            if (mi == null) continue;
            if (mi.vision == true) vis++;
            if (mi.efforts != null && mi.efforts.Count >= 2) eff++;
        }
        string head = added > 0
            ? ("接口返回 " + infos.Count + " 个，新增 " + added + " 个（共 " + have.Count + " 个）")
            : ("接口返回 " + infos.Count + " 个，都已经在列表里了");
        return head + " · 能看图 " + vis + " 个 · 能调档 " + eff + " 个";
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
            + "API Key 保存在程序目录的 config.json 里（DPAPI 加密，仅当前 Windows 用户可解）。";
        d.Location = new Point(22, 20);
        d.AutoSize = true;
        d.ForeColor = Theme.FgMid;
        d.BackColor = Theme.Card;
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
        _c.clarify = _chkClarify.Checked;
        _c.remote_approval = _chkRemote.Checked;

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
            // 高危操作改走「Ahz 通信台」网页控制台审批（等用户在手机/网页上点批准或拒绝）
            if (a == "--remote-approval") { config.remote_approval = true; continue; }
            if (a == "--remote-wait" && i + 1 < args.Length)
            {
                int v; if (int.TryParse(args[++i], out v) && v > 0) config.remote_approval_wait = v;
                continue;
            }
            if (a == "--remote-script" && i + 1 < args.Length) { config.remote_approval_script = args[++i]; continue; }
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
                List<string> names = LlmFactory.Ids(probe.ListModelInfos());
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
            UiMsg.Show("Ahz Scribe 已经在运行了。\n请使用已打开的那个窗口；数据文件不能被两个实例同时写。",
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
            UiMsg.Show("Ahz Scribe 启动失败：\n" + ex.Message, "Ahz Scribe", MessageBoxButtons.OK, MessageBoxIcon.Error);
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





