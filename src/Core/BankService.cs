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

    // ---- 归一化缓存 ----
    // MatchBank 每轮要对全库逐条打分（每轮 × 每题 × 每库条目），实时做正则归一化会重复成千上万次。
    // 题干一经构建即不再变化，故懒计算一次即可；纯加速，不改变任何匹配结果。
    private string[]? _stemWords;
    private List<string>? _normOpts;

    internal string[] StemWords => _stemWords ??= Matcher.StemWords(Qt);
    internal List<string> NormOpts => _normOpts ??= Matcher.NormAll(Opts);
}

/// <summary>
/// 题库构建：扫描客户端数据目录（flipbooks 绘本 + resources 作业），解密并抽取题目。
/// 与 Node 版 buildBank 逻辑一一对应。
/// </summary>
public static class BankService
{
    private static List<BankItem> _bank = new();
    private static string _bankKey = "";
    // 抓包等外部来源并入的条目：持久留存，Build 重建扫描题库后自动重放，用户不清空就不丢。
    private static readonly List<BankItem> _external = new();
    public static IReadOnlyList<BankItem> Items => _bank;
    public static int Count => _bank.Count;

    // AnswerTexts() 会被 DomEngine 每轮每题调用；全库遍历去重必须缓存，否则每轮白做几千次。
    static readonly object _gate = new();
    static int _ver;
    static List<string>? _ansCache;
    static int _ansCacheVer = -1;

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
                                if (root.HasValue && root.Value.TryGetProperty("questionObj", out var qo))
                                {
                                    string tag = unitName[..Math.Min(10, unitName.Length)] + "-长对话";
                                    // 形态①：questionObj.questions_list 是数组（一页多题）
                                    if (qo.TryGetProperty("questions_list", out var list) && list.ValueKind == JsonValueKind.Array)
                                        foreach (var q in list.EnumerateArray())
                                            Add(tag, Text(q, "question_text"), OptsOf(q), Text(q, "answer_text"), f);
                                    // 形态②：questionObj 本身就是一道题（question_text / options / answer_text 同级）
                                    // 旧实现只认形态①，这类题目**一道都进不了题库**——实测 180 个文件里 80 个是形态②，
                                    // 用户侧就表现为「未找到待作答选项」。补上这个分支即可。
                                    // 设 TXW_NO_SINGLE=1 可临时关掉（用于 A/B 对比修复前后的入库量）
                                    else if (Environment.GetEnvironmentVariable("TXW_NO_SINGLE") != "1")
                                    {
                                        var sOpts = OptsOf(qo).ToList();
                                        var sAns = Text(qo, "answer_text");
                                        // 形态②里混着「跟读/朗读」这类无选项、无答案的题（实测 qtype 110）：
                                        // 它们入库后只有题干，匹配时会给出"命中但答案为空"的假匹配 → 只在有选项或有答案时入库
                                        if (sOpts.Count > 0 || sAns.Length > 0)
                                            Add(tag, Text(qo, "question_text"), sOpts, sAns, f);
                                    }
                                }
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
            // 与 ScanService 保持一致：cache / personal_practice 是客户端缓存与自主练习目录，不入库
            if (Path.GetFileName(courseDir) is "cache" or "personal_practice") continue;
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

        // 重放外部缓存条目（抓包答案）：扫描题库整体替换后，持久缓存的抓包条目不能丢
        var existKeys = new HashSet<string>(bank.Select(b => NormKey(b.Qt)));
        foreach (var e in _external)
            if (existKeys.Add(NormKey(e.Qt))) bank.Add(e);

        _bank = bank;
        _bankKey = key;
        _ver++;
        return _bank.Count;
    }

    /// <summary>题库中所有带答案的选项内容（去重），供兜底匹配使用。
    /// 结果按题库版本缓存 —— DomEngine 每轮每题都会取一次，返回值<b>只读</b>，调用方不要修改。</summary>
    public static List<string> AnswerTexts()
    {
        lock (_gate)
        {
            if (_ansCache != null && _ansCacheVer == _ver) return _ansCache;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in _bank)
            {
                int i = "ABCD".IndexOf(b.Ans, StringComparison.Ordinal);
                if (i >= 0 && i < b.Opts.Count) set.Add(b.Opts[i]);
            }
            _ansCache = set.ToList();
            _ansCacheVer = _ver;
            return _ansCache;
        }
    }

    /// <summary>
    /// 把外部来源（如「无痕下载接管」抓包解析出的答案）并入题库，供「自动回答」视觉引擎匹配。
    /// 抓包答案只有选项文本、没有选项字母，统一记为 Opts=[答案文本] / Ans="A"，
    /// 这样 AnswerTexts() 会自动把答案文本纳入待选集合，自动回答无需重启即可用上。
    /// </summary>
    public static int MergeExternal(IEnumerable<(string qt, string ans)> items, string src = "抓包")
    {
        var added = 0;
        var exist = new HashSet<string>(_bank.Select(b => NormKey(b.Qt)));
        foreach (var (qt, ans) in items)
        {
            var q = (qt ?? "").Trim();
            var a = (ans ?? "").Trim();
            if (q.Length < 4 || a.Length == 0) continue;
            if (!exist.Add(NormKey(q))) continue;
            var item = new BankItem { Src = src, SrcFile = src, Qt = q, Opts = new List<string> { a }, Ans = "A" };
            _bank.Add(item);
            _external.Add(item);   // 同步留存：Build 重建后自动重放
            added++;
        }
        if (added > 0) _ver++;
        return added;
    }

    /// <summary>清空外部缓存条目（用户主动清空答案缓存时调用）。</summary>
    public static void ClearExternal()
    {
        _external.Clear();
        _bank.RemoveAll(b => b.Src == "抓包");
        _ver++;
    }

    internal static string NormKey(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = StripTags(s).ToLowerInvariant();
        return Regex.Replace(t, @"[^a-z0-9\u4e00-\u9fff]+", "");
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
