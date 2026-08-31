using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxwExtract.Core;

public sealed class CourseInfo
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";        // resource | flipbook
    public string RootDir { get; set; } = "";
    public string Mtime { get; set; } = "";
    public long MtimeMs { get; set; }
    public long Size { get; set; }
    public bool HasPaper { get; set; }
    public bool HasAnswers { get; set; }
    public bool Extractable { get; set; }
    public int QCount { get; set; }
    public int ChoiceCount { get; set; }
    public string CourseType { get; set; } = "";
    public string Title { get; set; } = "";
    public string ParseError { get; set; } = "";
    public bool IsNew { get; set; }
}

public sealed class CourseQuestion
{
    public string No { get; set; } = "";
    public string Type { get; set; } = "";
    public string Material { get; set; } = "";
    public string Qt { get; set; } = "";
    public List<(string Id, string Text)> Options { get; set; } = new();
    public string Ans { get; set; } = "";
    public string AnsText { get; set; } = "";
    public string Analysis { get; set; } = "";
    public bool IsListen { get; set; }
}

public sealed class CourseRecord
{
    public string Title { get; set; } = "";
    public string CourseType { get; set; } = "";
    public List<CourseQuestion> Questions { get; set; } = new();
    public int ListenCount { get; set; }
    public int ChoiceCount { get; set; }
    public int MissingCount { get; set; }
    public string Error { get; set; } = "";
}

public sealed record ScanResult(string Dir, int Count, List<CourseInfo> Courses, bool HasBaseline);

/// <summary>
/// 扫描客户端数据目录：资源课程（paper.xml）+ 绘本（flipbooks），支持基线增量标记与单课程解密导出。
/// Node 版 scanCourses / extractCourse / extractFlipBook 的 C# 移植。
/// </summary>
public static class ScanService
{
    static string BaselinePath => Path.Combine(AppPaths.DataDir, "baseline.json");

    static (Dictionary<string, long> Courses, Dictionary<string, long> Flipbooks, string Dir, string FlipDir) LoadBaseline()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(BaselinePath));
            var r = doc.RootElement;
            var courses = new Dictionary<string, long>();
            if (r.TryGetProperty("courses", out var c) && c.ValueKind == JsonValueKind.Object)
                foreach (var p in c.EnumerateObject()) courses[p.Name] = p.Value.GetInt64();
            var flips = new Dictionary<string, long>();
            if (r.TryGetProperty("flipbooks", out var f) && f.ValueKind == JsonValueKind.Object)
                foreach (var p in f.EnumerateObject()) flips[p.Name] = p.Value.GetInt64();
            return (courses, flips,
                    r.TryGetProperty("dir", out var d) ? d.GetString() ?? "" : "",
                    r.TryGetProperty("flipDir", out var fd) ? fd.GetString() ?? "" : "");
        }
        catch { return (new(), new(), "", ""); }
    }

    static void SaveBaseline(string dir, string flipDir, List<CourseInfo> courses)
    {
        try
        {
            var coursesObj = new Dictionary<string, long>();
            var flipsObj = new Dictionary<string, long>();
            foreach (var c in courses)
            {
                if (c.Kind == "flipbook") flipsObj[c.Id] = c.MtimeMs;
                else coursesObj[c.Id] = c.MtimeMs;
            }
            var payload = new { dir, flipDir, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), courses = coursesObj, flipbooks = flipsObj };
            File.WriteAllText(BaselinePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static ScanResult Scan(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return new(dir, 0, new(), false);

        var (bc, bf, baseDir, baseFlip) = LoadBaseline();
        bool hasBase = baseDir == dir;
        var list = new List<CourseInfo>();

        // ---- resources ----
        foreach (var e in Directory.EnumerateDirectories(dir))
        {
            string name = Path.GetFileName(e);
            if (name is "cache" or "personal_practice") continue;
            string paperPath = Path.Combine(e, "paper.xml.u3enc");
            string htmlPath = Path.Combine(e, "index.html.u3enc");
            bool hasPaper = File.Exists(paperPath);
            bool hasAns = File.Exists(Path.Combine(e, "correctAnswer.xml.u3enc"));
            if (!hasPaper && !File.Exists(htmlPath)) continue;
            var st = File.GetLastWriteTime(e);
            var ci = new CourseInfo
            {
                Id = name, Kind = "resource", RootDir = e,
                Mtime = st.ToUniversalTime().ToString("o"), MtimeMs = (long)(st - DateTime.UnixEpoch).TotalMilliseconds,
                Size = DirSize(e),
                HasPaper = hasPaper, HasAnswers = hasAns, Extractable = hasPaper && hasAns,
                CourseType = hasPaper ? "" : "仅壳(请在客户端打开练习)",
            };
            if (hasPaper)
            {
                try
                {
                    var paper = ParsePaper(CryptoService.DecryptFile(paperPath));
                    ci.QCount = paper.Questions.Count;
                    ci.CourseType = paper.Materials.Count > 0 ? paper.Materials.Values.First().Name : "";
                    ci.Title = paper.Title;
                }
                catch (Exception err) { ci.ParseError = err.Message; }
            }
            ci.IsNew = !hasBase || !bc.ContainsKey(name) || ci.MtimeMs > bc.GetValueOrDefault(name);
            list.Add(ci);
        }

        // ---- flipbooks ----
        string flipDir = Path.Combine(Path.GetDirectoryName(dir.TrimEnd('\\')) ?? "", "flipbooks");
        if (Directory.Exists(flipDir))
        {
            foreach (var e in Directory.EnumerateDirectories(flipDir))
            {
                string name = Path.GetFileName(e);
                int q = 0, choice = 0;
                foreach (var f in EnumerateFiles(e, "questionData.js.u3enc"))
                {
                    try
                    {
                        var root = BankService.ParsePc(CryptoService.DecryptFile(f));
                        if (root.HasValue && root.Value.TryGetProperty("questionObj", out var o) &&
                            o.TryGetProperty("question_id", out _))
                        {
                            q++;
                            if (o.TryGetProperty("qtype_id", out var t) && t.GetInt32() == 108) choice++;
                        }
                    }
                    catch { }
                }
                if (q == 0) continue;
                var st = Directory.GetLastWriteTime(e);
                var ci = new CourseInfo
                {
                    Id = name, Kind = "flipbook", RootDir = e,
                    Mtime = st.ToUniversalTime().ToString("o"), MtimeMs = (long)(st - DateTime.UnixEpoch).TotalMilliseconds,
                    QCount = q, ChoiceCount = choice, CourseType = "绘本/点读",
                    HasPaper = true, HasAnswers = true, Extractable = true, Title = name,
                };
                ci.IsNew = !hasBase || baseFlip != flipDir || !bf.ContainsKey(name) || ci.MtimeMs > bf.GetValueOrDefault(name);
                list.Add(ci);
            }
        }

        list.Sort((a, b) => b.MtimeMs.CompareTo(a.MtimeMs));
        return new(dir, list.Count, list, hasBase);
    }

    public static void SaveBaseline(string dir, List<CourseInfo> courses)
    {
        string flipDir = Path.Combine(Path.GetDirectoryName(dir.TrimEnd('\\')) ?? "", "flipbooks");
        SaveBaseline(dir, flipDir, courses);
    }

    static long DirSize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    static IEnumerable<string> EnumerateFiles(string dir, string fileName)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(d); } catch { continue; }
            foreach (var f in files)
                if (Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase)) yield return f;
            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { continue; }
            foreach (var s in subs) stack.Push(s);
        }
    }

    public static CourseRecord Extract(CourseInfo c)
    {
        try
        {
            return c.Kind == "flipbook" ? ExtractFlipBook(c.RootDir) : ExtractResource(c.RootDir);
        }
        catch (Exception e) { return new CourseRecord { Title = c.Id, Error = e.Message }; }
    }

    // ---------- resources（paper.xml + correctAnswer.xml） ----------
    static string FieldInner(string inner)
    {
        if (string.IsNullOrEmpty(inner)) return "";
        var m = Regex.Match(inner, @"<!\[CDATA\[([\s\S]*?)\]\]>");
        return m.Success ? m.Groups[1].Value : inner;
    }

    static string StripTags(string s) =>
        string.IsNullOrEmpty(s) ? "" : Regex.Replace(Regex.Replace(s, "<[^>]+>", " ").Replace("&nbsp;", " "), @"\s+", " ").Trim();

    sealed class PaperModel
    {
        public string Title = "";
        public Dictionary<string, (string Name, string Text)> Materials = new();
        public List<(string Id, string No, string Qt, List<(string Id, string Text)> Opts, string Rid)> Questions = new();
    }

    static PaperModel ParsePaper(string xml)
    {
        var paper = new PaperModel();
        foreach (Match em in Regex.Matches(xml, "<element\\b([\\s\\S]*?)</element>"))
        {
            string e = em.Groups[1].Value;
            string type = Regex.Match(e, @"type=""(\d+)""").Groups[1].Value;
            string id = Regex.Match(e, @"id=""([^""]+)""").Groups[1].Value;
            if (type == "0")
            {
                string cap = FieldInner(Regex.Match(e, "<element_caption>([\\s\\S]*?)</element_caption>").Groups[1].Value);
                if (cap.Length > 0) paper.Title = cap;
            }
            else if (type == "1")
            {
                string name = Regex.Match(e, "<qlib_qst_type_name>([\\s\\S]*?)</qlib_qst_type_name>").Groups[1].Value;
                string txt = StripTags(FieldInner(Regex.Match(e, "<element_text>([\\s\\S]*?)</element_text>").Groups[1].Value));
                paper.Materials[id] = (StripTags(name), txt);
            }
            else if (type == "3")
            {
                string no = Regex.Match(e, "<question_no>([\\s\\S]*?)</question_no>").Groups[1].Value;
                string qt = StripTags(FieldInner(Regex.Match(e, "<question_text>([\\s\\S]*?)</question_text>").Groups[1].Value));
                var opts = new List<(string, string)>();
                foreach (Match om in Regex.Matches(e, "<option\\s+id=\"([^\"]+)\">([\\s\\S]*?)</option>"))
                    opts.Add((om.Groups[1].Value, StripTags(FieldInner(om.Groups[2].Value))));
                string rid = Regex.Match(e, "<ref_question_id>([^<]+)</ref_question_id>").Groups[1].Value;
                paper.Questions.Add((id, no, qt, opts, rid));
            }
        }
        return paper;
    }

    static (List<(string Ans, string Ana)> List, Dictionary<string, (string, string)> ById, Dictionary<string, (string, string)> ByNo) ParseAnswers(string xml)
    {
        var list = new List<(string, string)>();
        var byId = new Dictionary<string, (string, string)>();
        var byNo = new Dictionary<string, (string, string)>();
        int order = 0;
        foreach (Match em in Regex.Matches(xml, "<element\\b([\\s\\S]*?)</element>"))
        {
            string e = em.Groups[1].Value;
            string id = Regex.Match(e, @"id=""([^""]+)""").Groups[1].Value;
            string ans = FieldInner(Regex.Match(e, "<answers>([\\s\\S]*?)</answers>").Groups[1].Value).Trim();
            string ana = StripTags(FieldInner(Regex.Match(e, "<analysis>([\\s\\S]*?)</analysis>").Groups[1].Value));
            list.Add((ans, ana));
            if (id.Length > 0)
            {
                byId[id] = (ans, ana);
                if (Regex.IsMatch(id, @"^\d+$")) byNo[id] = (ans, ana);
            }
            order++;
        }
        return (list, byId, byNo);
    }

    static CourseRecord ExtractResource(string dir)
    {
        string paperPath = Path.Combine(dir, "paper.xml.u3enc");
        if (!File.Exists(paperPath))
            return new CourseRecord
            {
                Title = Path.GetFileName(dir),
                Error = "本地无 paper.xml.u3enc —— 该课程只下载了 Web 壳，答案数据尚未写入磁盘。请在天学网客户端中打开此练习（加载/播放听力），待数据落地后重新扫描即可解密。",
            };
        string ansPath = Path.Combine(dir, "correctAnswer.xml.u3enc");
        var paper = ParsePaper(CryptoService.DecryptFile(paperPath));
        var (list, byId, byNo) = File.Exists(ansPath) ? ParseAnswers(CryptoService.DecryptFile(ansPath)) : (new(), new(), new());
        int listenCount = 0;
        var qs = new List<CourseQuestion>();
        for (int i = 0; i < paper.Questions.Count; i++)
        {
            var q = paper.Questions[i];
            (string Ans, string Ana) a = (q.Id.Length > 0 && byId.ContainsKey(q.Id)) ? byId[q.Id]
                : (q.No.Length > 0 && byNo.ContainsKey(q.No)) ? byNo[q.No]
                : (i < list.Count ? list[i] : ("", ""));
            (string Name, string Text) mat = q.Rid.Length > 0 && paper.Materials.ContainsKey(q.Rid) ? paper.Materials[q.Rid] : (Name: "", Text: "");
            bool isListen = mat.Name.Contains("听力");
            if (isListen) listenCount++;
            int aIdx = q.Opts.FindIndex(o => o.Id == a.Ans);
            string ansText = aIdx >= 0 ? q.Opts[aIdx].Text : "";   // 找不到（多选/格式变化）不能 NRE
            qs.Add(new CourseQuestion
            {
                No = q.No, Type = mat.Name, Material = mat.Text, Qt = q.Qt,
                Options = q.Opts, Ans = a.Ans, AnsText = ansText, Analysis = a.Ana, IsListen = isListen,
            });
        }
        return new CourseRecord
        {
            Title = paper.Title.Length > 0 ? paper.Title : "",
            CourseType = paper.Materials.Count > 0 ? paper.Materials.Values.First().Name : "",
            Questions = qs, ListenCount = listenCount,
        };
    }

    // ---------- flipbook ----------
    static CourseQuestion ParseFlipQuestion(JsonElement q)
    {
        string qt = BankService.StripTags(
            q.TryGetProperty("question_text", out var qtv) && qtv.ValueKind == JsonValueKind.String ? qtv.GetString() ?? ""
            : (qtv.ValueKind == JsonValueKind.Object && qtv.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : ""));
        if ((q.TryGetProperty("qtype_id", out var tid) && tid.GetInt32() == 108) ||
            (q.TryGetProperty("options", out var optsEl) && optsEl.ValueKind == JsonValueKind.Array && optsEl.GetArrayLength() > 0))
        {
            var opts = new List<(string, string)>();
            if (q.TryGetProperty("options", out var oEl) && oEl.ValueKind == JsonValueKind.Array)
                foreach (var o in oEl.EnumerateArray())
                {
                    string oid = o.TryGetProperty("id", out var oidEl) ? oidEl.GetString() ?? "" : "";
                    string oct = o.TryGetProperty("content", out var octEl) ? BankService.StripTags(octEl.GetString() ?? "") : "";
                    opts.Add((oid, oct));
                }
            string ans = q.TryGetProperty("answer_text", out var ae) ? (ae.GetString() ?? "").Trim() : "";
            string ansText = opts.FirstOrDefault(o => o.Item1 == ans).Item2 ?? "";
            string ana = q.TryGetProperty("analysis", out var anEl) ? BankService.StripTags(anEl.GetString() ?? "") : "";
            return new CourseQuestion { Qt = qt, Options = opts, Ans = ans, AnsText = ansText, Analysis = ana, Type = "选择题" };
        }
        // 跟读/听读
        string transcript = "";
        if (q.TryGetProperty("record_follow_read", out var rfr) && rfr.ValueKind == JsonValueKind.Object &&
            rfr.TryGetProperty("paragraph_list", out var pl) && pl.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var p in pl.EnumerateArray())
            {
                string pre = p.TryGetProperty("pre", out var pe) ? pe.GetString() ?? "" : "";
                var sents = new List<string>();
                if (p.TryGetProperty("sentences", out var se) && se.ValueKind == JsonValueKind.Array)
                    foreach (var s in se.EnumerateArray())
                        if (s.TryGetProperty("content_en", out var ce)) sents.Add(ce.GetString() ?? "");
                parts.Add((pre.Length > 0 ? pre + "：" : "") + string.Join(" ", sents));
            }
            transcript = string.Join("\n", parts);
        }
        return new CourseQuestion { Qt = qt, Analysis = transcript, IsListen = true, Type = "跟读/听读" };
    }

    static CourseRecord ExtractFlipBook(string bookDir)
    {
        // qid → questionObj 索引
        var byQid = new Dictionary<string, JsonElement>();
        foreach (var f in EnumerateFiles(bookDir, "questionData.js.u3enc"))
        {
            try
            {
                var root = BankService.ParsePc(CryptoService.DecryptFile(f));
                if (root.HasValue && root.Value.TryGetProperty("questionObj", out var q) &&
                    q.TryGetProperty("question_id", out var qid))
                {
                    string key = qid.GetString() ?? "";
                    if (key.Length > 0 && !byQid.ContainsKey(key)) byQid[key] = q.Clone();
                }
            }
            catch { }
        }

        // 单元目录：<unit>/1/page1.js.u3enc 存在
        var unitDirs = new List<string>();
        var stack = new Stack<string>();
        stack.Push(bookDir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            string[] subs;
            try { subs = Directory.GetDirectories(d); } catch { continue; }
            foreach (var s in subs)
            {
                if (File.Exists(Path.Combine(s, "1", "page1.js.u3enc"))) unitDirs.Add(s);
                else stack.Push(s);
            }
        }

        // 单元名映射（book.cache）
        var unitNames = new Dictionary<string, string>();
        try
        {
            var root = BankService.ParsePc(CryptoService.DecryptFile(Path.Combine(bookDir, "book.cache")));
            if (root.HasValue && root.Value.TryGetProperty("bookCatalog", out var cat))
            {
                var byId = new Dictionary<string, string>();
                foreach (var g in new[] { "chapters", "pages", "tasks" })
                    if (cat.TryGetProperty(g, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var e in arr.EnumerateArray())
                            if (e.TryGetProperty("id", out var iel) && e.TryGetProperty("name", out var nel))
                                byId[iel.GetString() ?? ""] = nel.GetString() ?? "";
                foreach (var d in Directory.EnumerateDirectories(bookDir))
                {
                    string nm = Path.GetFileName(d);
                    if (byId.TryGetValue(nm, out var mapped)) unitNames[nm] = mapped;
                }
            }
        }
        catch { }

        var questions = new List<CourseQuestion>();
        int listenCount = 0, choiceCount = 0, missingCount = 0, no = 0;
        void Push(string src, CourseQuestion pq) { pq.No = (++no).ToString(); pq.Material = src; if (pq.Type == "选择题") choiceCount++; else listenCount++; questions.Add(pq); }

        foreach (var unitDir in unitDirs)
        {
            string src = unitNames.GetValueOrDefault(Path.GetFileName(unitDir)) ?? Path.GetFileName(unitDir)[..Math.Min(10, Path.GetFileName(unitDir).Length)];
            try
            {
                var root = BankService.ParsePc(CryptoService.DecryptFile(Path.Combine(unitDir, "1", "page1.js.u3enc")));
                if (!root.HasValue || !root.Value.TryGetProperty("slides", out var slides) || slides.ValueKind != JsonValueKind.Array) continue;
                foreach (var s in slides.EnumerateArray())
                {
                    if (!s.TryGetProperty("questionList", out var ql) || ql.ValueKind != JsonValueKind.Array) continue;
                    foreach (var q in ql.EnumerateArray())
                    {
                        if (!q.TryGetProperty("question_id", out var qidEl)) continue;
                        string qid = qidEl.GetString() ?? "";
                        bool hasOpts = q.TryGetProperty("options", out var oEl) && oEl.ValueKind == JsonValueKind.Array && oEl.GetArrayLength() > 0;
                        bool isChoice = q.TryGetProperty("qtype_id", out var tEl) && tEl.GetInt32() == 108;
                        if (hasOpts || isChoice) { Push(src, ParseFlipQuestion(q)); continue; }
                        if (!byQid.TryGetValue(qid, out var qo)) { missingCount++; continue; }
                        if (qo.TryGetProperty("questions_list", out var sub) && sub.ValueKind == JsonValueKind.Array)
                            foreach (var sq in sub.EnumerateArray()) Push(src, ParseFlipQuestion(sq));
                    }
                }
            }
            catch { }
        }
        return new CourseRecord
        {
            Title = Path.GetFileName(bookDir), CourseType = "绘本/点读",
            Questions = questions, ListenCount = listenCount, ChoiceCount = choiceCount, MissingCount = missingCount,
        };
    }

    // ---------- 导出 ----------
    public static string ExportJson(CourseInfo c, CourseRecord r)
    {
        string outDir = Path.Combine(AppPaths.AutoDir, "export");
        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, Sanitize(c.Id) + ".json");
        var payload = new
        {
            course = c.Id, title = r.Title, type = r.CourseType,
            listen = r.ListenCount, choice = r.ChoiceCount,
            questions = r.Questions.Select(q => new
            {
                no = q.No, type = q.Type, material = q.Material, qt = q.Qt,
                options = q.Options.Select(o => new { id = o.Item1, text = o.Item2 }).ToList(),
                ans = q.Ans, ansText = q.AnsText, analysis = q.Analysis, isListen = q.IsListen,
            }).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static string ExportHtml(CourseInfo c, CourseRecord r)
    {
        string outDir = Path.Combine(AppPaths.AutoDir, "export");
        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, Sanitize(c.Id) + ".html");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>" + HtmlEsc(c.Title) + "</title>");
        sb.AppendLine("<style>body{font-family:'Microsoft YaHei',sans-serif;max-width:900px;margin:24px auto;padding:0 16px;color:#222}h1{font-size:20px}.q{margin:18px 0;padding:14px;border:1px solid #e3e8f0;border-radius:8px}.mat{color:#7a8699;font-size:13px}.qt{font-weight:600;margin:6px 0}.opt{margin:2px 0}.ans{margin-top:6px;color:#0a7d2f;font-weight:600}.ana{margin-top:4px;color:#555;font-size:13px;white-space:pre-wrap}</style></head><body>");
        sb.AppendLine($"<h1>{HtmlEsc(r.Title)}</h1><p style=\"color:#7a8699\">{HtmlEsc(r.CourseType)} · 共 {r.Questions.Count} 题（听力 {r.ListenCount}）</p>");
        foreach (var q in r.Questions)
        {
            sb.AppendLine("<div class=\"q\">");
            sb.AppendLine($"<div class=\"mat\">[{HtmlEsc(q.No)}] {HtmlEsc(q.Type)} {HtmlEsc(q.Material)}</div>");
            if (q.Material.Length > 0 && q.IsListen) sb.AppendLine($"<div class=\"ana\">{HtmlEsc(q.Material)}</div>");
            sb.AppendLine($"<div class=\"qt\">{HtmlEsc(q.Qt)}</div>");
            foreach (var o in q.Options) sb.AppendLine($"<div class=\"opt\">{HtmlEsc(o.Item1)}. {HtmlEsc(o.Item2)}</div>");
            if (q.Ans.Length > 0)
                sb.AppendLine($"<div class=\"ans\">答案：{HtmlEsc(q.Ans)}{(q.AnsText.Length > 0 ? " " + HtmlEsc(q.AnsText) : "")}</div>");
            if (q.Analysis.Length > 0) sb.AppendLine($"<div class=\"ana\">{HtmlEsc(q.Analysis)}</div>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</body></html>");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    static string HtmlEsc(string s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
