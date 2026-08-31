using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace TxwExtract.Core;

public sealed class BankItem
{
    public string Src { get; set; } = "";
    public string SrcFile { get; set; } = "";
    public string Qt { get; set; } = "";
    public List<string> Opts { get; set; } = new();
    public string Ans { get; set; } = "";   // 正确选项字母（A/B/C/D）
}

/// <summary>
/// 题库构建：扫描客户端数据目录（flipbooks 绘本 + resources 作业），解密并抽取题目。
/// 与 Node 版 buildBank 逻辑一一对应。
/// </summary>
public static class BankService
{
    private static List<BankItem> _bank = new();
    private static string _bankKey = "";
    public static IReadOnlyList<BankItem> Items => _bank;
    public static int Count => _bank.Count;

    static readonly JsonDocumentOptions JsonOpts = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static int Build(string resourcesDir)
    {
        if (string.IsNullOrWhiteSpace(resourcesDir) || !Directory.Exists(resourcesDir)) return 0;
        var flipDir = Path.Combine(Path.GetDirectoryName(resourcesDir.TrimEnd('\\')) ?? "", "flipbooks");
        // 缓存键必须覆盖"课程目录内容变化"：新落盘的 paper/correctAnswer 只改课程子目录的 mtime，
        // 不改 resourcesDir/flipDir 自身 —— 仅用目录 mtime 会漏掉新数据（旧 bug：题库永不刷新）
        string key = resourcesDir + "|" + DirTreeStamp(resourcesDir) + "|" + (Directory.Exists(flipDir) ? DirTreeStamp(flipDir) : 0);
        if (_bankKey == key && _bank.Count > 0) return _bank.Count;

        var bank = new List<BankItem>();
        void Add(string src, string qt, IEnumerable<string> opts, string ans, string srcFile)
        {
            if (string.IsNullOrWhiteSpace(qt)) return;
            bank.Add(new BankItem
            {
                Src = src,
                SrcFile = srcFile,
                Qt = StripTags(qt),
                Opts = opts.Select(StripTags).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
                Ans = (ans ?? "").Trim(),
            });
        }

        // ---- flipbooks：每个单元 page1.js（短对话）+ questions/*（长对话材料）----
        if (Directory.Exists(flipDir))
        {
            foreach (var bookDir in Directory.EnumerateDirectories(flipDir))
            {
                foreach (var unitDir in Directory.EnumerateDirectories(bookDir))
                {
                    string unitName = Path.GetFileName(unitDir);
                    string p1 = Path.Combine(unitDir, "1", "page1.js.u3enc");
                    if (File.Exists(p1))
                    {
                        try
                        {
                            var root = ParsePc(CryptoService.DecryptFile(p1));
                            if (root.HasValue && root.Value.TryGetProperty("slides", out var slides) && slides.ValueKind == JsonValueKind.Array)
                                foreach (var s in slides.EnumerateArray())
                                    if (s.TryGetProperty("questionList", out var ql) && ql.ValueKind == JsonValueKind.Array)
                                        foreach (var q in ql.EnumerateArray())
                                            Add(unitName[..Math.Min(10, unitName.Length)] + "-短对话",
                                                Text(q, "question_text"), OptsOf(q), Text(q, "answer_text"), p1);
                        }
                        catch { }
                    }

                    string qd = Path.Combine(unitDir, "1", "questions");
                    if (Directory.Exists(qd))
                    {
                        foreach (var g in Directory.EnumerateDirectories(qd))
                        {
                            string f = Path.Combine(g, "questionData.js.u3enc");
                            if (!File.Exists(f)) continue;
                            try
                            {
                                var root = ParsePc(CryptoService.DecryptFile(f));
                                if (root.HasValue && root.Value.TryGetProperty("questionObj", out var qo) &&
                                    qo.TryGetProperty("questions_list", out var list) && list.ValueKind == JsonValueKind.Array)
                                    foreach (var q in list.EnumerateArray())
                                        Add(unitName[..Math.Min(10, unitName.Length)] + "-长对话",
                                            Text(q, "question_text"), OptsOf(q), Text(q, "answer_text"), f);
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        // ---- resources：每个课程 paper.xml（题目 element type="3"）+ correctAnswer.xml（答案按 element id 关联）----
        // v2.1.19 重写：此前用正则全文匹配 <question_text>/<options>，会把听力材料 attachment CDATA
        // 内嵌的 XML 也抓进来，导致题干带 "]]>" 残留、选项带 "<![CDATA[...]]>" 包装、题目-选项错位
        // （实测 3 题干 vs 12 options 块），部分课程甚至 0 题入库 —— 用户题匹配不上。
        // 现在：XmlDocument 只取 <element type="3"> 题目节点；<answers> 从 correctAnswer.xml 按 id 关联。
        foreach (var courseDir in Directory.EnumerateDirectories(resourcesDir))
        {
            string paper = Path.Combine(courseDir, "paper.xml.u3enc");
            if (!File.Exists(paper)) continue;
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(CryptoService.DecryptFile(paper));

                // 答案表：correctAnswer.xml.u3enc 的 <element id=...><answers>字母</answers>
                var ansMap = new Dictionary<string, string>();
                string ca = Path.Combine(courseDir, "correctAnswer.xml.u3enc");
                if (File.Exists(ca))
                {
                    try
                    {
                        var adoc = new XmlDocument();
                        adoc.LoadXml(CryptoService.DecryptFile(ca));
                        foreach (XmlElement el in adoc.SelectNodes("//element")!)
                        {
                            var id = el.GetAttribute("id");
                            var ans = el.SelectSingleNode("answers")?.InnerText?.Trim();
                            if (id.Length > 0 && !string.IsNullOrEmpty(ans)) ansMap[id] = ans;
                        }
                    }
                    catch { }
                }

                string courseTag = Path.GetFileName(courseDir);
                courseTag = courseTag[..Math.Min(8, courseTag.Length)] + "-作业";
                var nodes = doc.SelectNodes("//element[@type='3']");
                if (nodes == null) continue;
                foreach (XmlElement el in nodes.Cast<XmlElement>())
                {
                    string qt = el.SelectSingleNode("question_text")?.InnerText ?? "";
                    qt = StripTags(qt);
                    if (qt.Length == 0) continue;
                    var opts = new List<string>();
                    var on = el.SelectSingleNode("options");
                    if (on != null)
                        foreach (XmlNode op in on.SelectNodes("option")!)
                        {
                            string o = StripTags(op.InnerText);
                            if (o.Length > 0) opts.Add(o);
                        }
                    string ans = ansMap.TryGetValue(el.GetAttribute("id"), out var a) ? a : "";
                    Add(courseTag, qt, opts, ans, paper);
                }
            }
            catch { }
        }

        _bank = bank;
        _bankKey = key;
        return _bank.Count;
    }

    /// <summary>题库中所有带答案的选项内容（去重），供兜底匹配使用。</summary>
    public static List<string> AnswerTexts()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in _bank)
        {
            int i = "ABCD".IndexOf(b.Ans, StringComparison.Ordinal);
            if (i >= 0 && i < b.Opts.Count) set.Add(b.Opts[i]);
        }
        return set.ToList();
    }

    // ---------- 工具 ----------
    /// <summary>目录自身 + 全部一级子目录的最大 mtime（Ticks）——子目录内容变化会更新子目录 mtime。</summary>
    static long DirTreeStamp(string dir)
    {
        long max = 0;
        try
        {
            max = Directory.GetLastWriteTimeUtc(dir).Ticks;
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                long t = Directory.GetLastWriteTimeUtc(d).Ticks;
                if (t > max) max = t;
            }
        }
        catch { }
        return max;
    }

    public static string StripTags(string s) =>
        string.IsNullOrEmpty(s) ? "" : Regex.Replace(Regex.Replace(s, "<[^>]*>", " "), @"\s+", " ").Trim();

    /// <summary>question_text 可能是字符串，也可能是 {text:...}</summary>
    static string Text(JsonElement q, string prop)
    {
        if (!q.TryGetProperty(prop, out var v)) return "";
        if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("text", out var t)) return t.GetString() ?? "";
        return "";
    }

    static List<string> OptsOf(JsonElement q)
    {
        var list = new List<string>();
        if (q.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
            foreach (var op in o.EnumerateArray())
            {
                if (op.ValueKind == JsonValueKind.String) list.Add(op.GetString() ?? "");
                else if (op.TryGetProperty("content", out var c)) list.Add(c.GetString() ?? "");
            }
        return list;
    }

    /// <summary>
    /// 解密后内容是 JS 字面量（var pageConfig = {...};）。这里做花括号配对提取出对象体再按 JSON 解析，
    /// 替代 Node 版的 eval()，避免引入脚本引擎。
    /// </summary>
    internal static JsonElement? ParsePc(string js)
    {
        int start = -1;
        int idx = js.IndexOf("pageConfig", StringComparison.Ordinal);
        if (idx >= 0) start = js.IndexOf('{', idx);
        start = start >= 0 ? start : js.IndexOf('{');
        if (start < 0) return null;

        int depth = 0; bool inStr = false, esc = false;
        for (int i = start; i < js.Length; i++)
        {
            char c = js[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    string json = js[start..(i + 1)];
                    try { return JsonDocument.Parse(json, JsonOpts).RootElement.Clone(); }
                    catch { return null; }
                }
            }
        }
        return null;
    }
}
