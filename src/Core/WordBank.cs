using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxwExtract.Core;

/// <summary>
/// 单词 PK 词库（v1.3.0 新增；v1.6.5 按参考实现 Auto366 `rulesets/auto-pk/auto-pk.js` 重做匹配侧）。
///   - 来源：「无痕下载接管」劫持 words-v2-api / wordsbtl-api 后抓到的词书接口响应
///     （bucket/detail-info、wordsbtl/student/start、entry/list-by-book），解析出 词条+释义 对并累积合并。
///     🔴 **抓包成功即词书完整**：客户端出的题只从该词书里取，答案必然在本地，不必怀疑"词库不全"。
///   - 持久化：AppPaths.DataDir/minicloud/wordbank.json；
///   - 用途：DomEngine 识别到 PK 页面（选择 PK / 拼写 PK）时作答。
///
/// 匹配侧对齐两处外部事实：
///   ① 客户端 `PKCore`（_research/pk_src）：题面/选项都过 `H()` = **连括号内容一起删** → 按 `;|,|；|，` 切分取首段
///      → 首段无中文则回退整串 → 去 phr./phrase。所以题面大量是**单字首义项**（`v.提前`、`n.奖`、`n.伞`）。
///   ② 参考实现 Auto366 的四段式：目标词定位 → 逐选项打分（含词性兼容加成）→ 反向查库 / 二轮纯净释义兜底
///      → 置信阶梯（best 与次佳分差）。**除 AI 兜底外全部对齐**。
/// </summary>
public static class WordBank
{
    public sealed class Entry
    {
        public string Word = "";
        public string Paraphrase = "";
        /// <summary>`；;` 级义项，**括号内容保留**（对应参考实现的 meanings）。与 <see cref="CleanMeanings"/> 下标一一对应。</summary>
        public List<string> Meanings = new();
        /// <summary>`；;` 级义项，**逐条去括号**（对应参考实现的 clean_meanings）。与 <see cref="Meanings"/> 下标一一对应。</summary>
        public List<string> CleanMeanings = new();
        /// <summary>词性标签（v/n/adj/…；vt·vi 归并为 v）。无前缀则为空集。</summary>
        public HashSet<string> Pos = new();
        /// <summary>
        /// 客户端 `PKCore.H(cn)` 的忠实结果——即「本题面会以什么形态出现/该词的正确选项长什么样」。
        /// 这是天学网出题语义的唯一权威：去括号(含内容) → 按 `; , ； ，` 切分取第 1 段 → 无中文则回退整串。
        /// 用于打破「多选项共享同一义项」的并列（如 resolve → 解决/决定 两侧都命中）。
        /// </summary>
        public string ExpectedForm = "";
    }

    /// <summary>选择 PK 的一次判定结果（带置信度），供 DomEngine 日志与门控使用。</summary>
    public sealed class Pick
    {
        public int Index;
        public string Answer = "";
        public double Score;       // 最佳匹配度 0-100
        public double Second;      // 次佳匹配度
        public double Gap => Score - Second;
        public string Tier = "";   // 命中 / 中置信 / 低置信 / 兜底
        public string Reason = "";
        public bool Confident => Tier == "命中";
    }

    static readonly object Lock = new();
    static List<Entry>? _cache;
    static Dictionary<string, List<Entry>>? _byWord;        // 归一化单词 → 词条
    static Dictionary<string, List<Entry>>? _meaningExact;  // 归一化义项（最细片段） → 词条（O(1) 命中）
    static List<(string key, Entry e)>? _byMeaning;         // (归一化义项片段, 词条) —— 兜底线性扫描

    public static string FilePath => Path.Combine(AppPaths.DataDir, "minicloud", "wordbank.json");

    public static int Count
    {
        get { EnsureLoaded(); lock (Lock) return _cache!.Count; }
    }

    static void EnsureLoaded()
    {
        lock (Lock)
        {
            if (_cache != null) return;
            _cache = new List<Entry>();
            try
            {
                if (File.Exists(FilePath))
                {
                    using var d = JsonDocument.Parse(File.ReadAllText(FilePath));
                    if (d.RootElement.TryGetProperty("words", out var ws) && ws.ValueKind == JsonValueKind.Array)
                        foreach (var w in ws.EnumerateArray())
                        {
                            var e = new Entry
                            {
                                Word = w.TryGetProperty("entry", out var en) ? en.GetString() ?? "" : "",
                                Paraphrase = w.TryGetProperty("paraphrase", out var pa) ? pa.GetString() ?? "" : "",
                            };
                            if (e.Word.Length > 0) _cache.Add(e);
                        }
                }
                RebuildIndex();
            }
            catch { }
        }
    }

    static void RebuildIndex()
    {
        _byWord = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        _meaningExact = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        _byMeaning = new List<(string, Entry)>();

        foreach (var e in _cache!)
        {
            // `；;`/换行 级切片：两套义项**同下标对齐**（参考实现是 clean_meanings = meanings.map(去括号)）
            e.Meanings = new List<string>();
            e.CleanMeanings = new List<string>();
            foreach (var seg in Segments1(e.Paraphrase))
            {
                var rawN = Norm(seg, false);                 // 括号内容保留（括号字符本身丢弃）
                var clnN = Norm(StripParens(seg), false);    // 先去括号再归一
                if (rawN.Length == 0 && clnN.Length == 0) continue;
                e.Meanings.Add(rawN);
                e.CleanMeanings.Add(clnN);
            }
            e.Pos = ExtractPos(e.Paraphrase);
            e.ExpectedForm = HForm(e.Paraphrase);

            var wk = NormWord(e.Word);
            if (wk.Length == 0) continue;
            if (!_byWord.TryGetValue(wk, out var list)) _byWord[wk] = list = new List<Entry>();
            list.Add(e);

            // 索引粒度 = 最细义项片段，**原样 + 纯净两套都要**：否则单字义项（奖/梦/门/伞/响…）匹配不到。
            foreach (var part in Parts(e.Meanings)) Push(part, e);
            foreach (var part in Parts(e.CleanMeanings)) Push(part, e);
            // 「题面形态」也建索引（精确 + 分项），让中文题面能一击命中出题词
            if (e.ExpectedForm.Length > 0)
            {
                Push(e.ExpectedForm, e);
                foreach (var p in Parts(new List<string> { e.ExpectedForm })) Push(p, e);
            }
        }

        void Push(string key, Entry e)
        {
            if (key.Length == 0) return;
            _byMeaning!.Add((key, e));
            if (!_meaningExact!.TryGetValue(key, out var l)) _meaningExact[key] = l = new List<Entry>();
            if (!l.Contains(e)) l.Add(e);
        }
    }

    // ---------------- 抓取侧：解析接口响应 ----------------

    /// <summary>解析词书接口响应（递归收集 entryList/words），合并入词库。返回**本次新增**词条数（0 = 无新词）。</summary>
    public static int MergeFromJson(string json)
    {
        var collected = new List<(string word, string para)>();
        try
        {
            using var d = JsonDocument.Parse(json);
            Collect(d.RootElement, collected);
        }
        catch { return 0; }
        if (collected.Count == 0) return 0;

        EnsureLoaded();
        int added = 0;
        lock (Lock)
        {
            var seen = new HashSet<string>(_cache!.Select(e => e.Word.ToLowerInvariant() + "|" + NormWord(e.Paraphrase)));
            foreach (var (word, para) in collected)
            {
                var key = word.ToLowerInvariant() + "|" + NormWord(para);
                if (word.Length == 0 || para.Length == 0 || !seen.Add(key)) continue;
                _cache!.Add(new Entry { Word = word.Trim(), Paraphrase = para.Trim() });
                added++;
            }
            try
            {
                var payload = new
                {
                    updatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    count = _cache!.Count,
                    words = _cache!.Select(e => new { entry = e.Word, paraphrase = e.Paraphrase }).ToList(),
                };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
            RebuildIndex();
        }
        return added;
    }

    static void Collect(JsonElement el, List<(string, string)> outList)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var it in el.EnumerateArray()) Collect(it, outList);
                break;
            case JsonValueKind.Object:
                if (el.TryGetProperty("entryList", out var eL) && eL.ValueKind == JsonValueKind.Array)
                    foreach (var e in eL.EnumerateArray()) AddPair(e, outList);
                if (el.TryGetProperty("words", out var ws) && ws.ValueKind == JsonValueKind.Array)
                    foreach (var e in ws.EnumerateArray()) AddPair(e, outList);
                foreach (var p in el.EnumerateObject()) Collect(p.Value, outList);
                break;
        }
    }

    static void AddPair(JsonElement e, List<(string, string)> outList)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        string word = FirstStr(e, "entry", "en", "word");
        string para = FirstStr(e, "paraphrase", "cn", "meaning", "trans");
        if (word.Length > 0 && para.Length > 0) outList.Add((word, para));
    }

    static string FirstStr(JsonElement o, params string[] names)
    {
        foreach (var n in names)
            if (o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        return "";
    }

    // =================================================================================
    // 匹配侧
    // =================================================================================

    /// <summary>
    /// 选择 PK：题面（中文释义或英文单词）→ 逐选项打分 → 置信阶梯 → 返回应点选项。
    /// **不再"未命中即放弃"**：词库非空时必给一个选项（最差走「兜底 = 选项 [0]」）——
    /// PK 是限时赛，不点是必错，乱猜不劣于 0 分。词库为空时返回 null（交由上层提示去抓词书）。
    /// </summary>
    public static (string answer, double score)? MatchChoice(string question, List<string> optionTexts)
        => MatchChoiceEx(question, optionTexts) is { } p ? (p.Answer, p.Score) : null;

    /// <summary>同 <see cref="MatchChoice"/>，但返回带置信度/分差/原因/档位的完整结果（日志与门控用）。</summary>
    public static Pick? MatchChoiceEx(string question, List<string> optionTexts)
    {
        EnsureLoaded();
        var opts = (optionTexts ?? new List<string>()).Select(o => (o ?? "").Trim()).ToList();
        while (opts.Count > 0 && opts[^1].Length == 0) opts.RemoveAt(opts.Count - 1);
        if (opts.Count == 0) return null;

        List<Entry> all;
        lock (Lock) all = _cache!;
        if (all.Count == 0) return null;

        string q = question ?? "";
        bool isCnQ = Regex.IsMatch(q, "[\u4e00-\u9fff]");
        var qPos = ExtractPos(q);

        // ① 目标词定位：中文题面查释义索引，英文题面查单词索引
        var targets = FindTargets(q, isCnQ);

        // ①b 多目标时按**题面词性**消歧（对齐参考实现 1261–1284）：
        //     题面「v. 装载」→ 只保留 v. 的候选词，可把 load / loading 这类**时态+派生词**分开。
        //     仅当过滤后仍有候选时才采用（否则宁可不过滤，避免召回被清空）。
        if (targets.Count > 1 && qPos.Count > 0 && !qPos.Contains("other"))
        {
            var filtered = targets.Where(t => t.Pos.Count > 0 && IsPosCompatible(t.Pos, qPos)).ToList();
            if (filtered.Count > 0 && filtered.Count < targets.Count) targets = filtered;
        }

        // ② 逐选项打分（order = 命中义项的下标，越小越靠前；ev = 证据强度 1弱/2中/3强）
        var score = new double[opts.Count];
        var reason = new string[opts.Count];
        var order = new int[opts.Count];
        var ev = new int[opts.Count];
        for (int i = 0; i < opts.Count; i++)
            score[i] = ScoreOption(opts[i], isCnQ, targets, qPos, all, out reason[i], out order[i], out ev[i]);

        // ③ 中文题面且首轮全 0 → 反向查库（拿选项反查它的释义是否对得上题面）
        double bestBefore = 0;
        foreach (var s in score) if (s > bestBefore) bestBefore = s;
        if (isCnQ && bestBefore == 0 && targets.Count > 0)
            for (int i = 0; i < opts.Count; i++)
            {
                var r = ReverseLookup(opts[i], q, qPos);
                if (r.score > score[i]) { score[i] = r.score; reason[i] = "反向查库·" + r.why; ev[i] = 1; }
            }

        // 并列打破第 3 顺位：选项含屈折后缀（时态/复数）时，优先词典原形
        var baseRank = new int[opts.Count];
        if (isCnQ) for (int i = 0; i < opts.Count; i++) baseRank[i] = Lemma(opts[i]) == NormWord(opts[i]) ? 0 : 1;

        int bi = ArgMax(score, order, baseRank, out double best, out double second);
        double gap = best - second;

        // ④ 英文题面且首轮不可信 → 二轮纯净释义（去括号义项 + 题面形态）
        if (!isCnQ && targets.Count > 0 && (best < 50 || gap < 15))
            for (int i = 0; i < opts.Count; i++)
            {
                double s = ScoreOptionClean(opts[i], targets, qPos, out var why, out var ord, out var evc);
                if (s > best || (s > 0 && s == best && (ord < order[i] || (ord == order[i] && evc > ev[i]))))
                {
                    best = s; bi = i; order[i] = ord; ev[i] = evc;
                    second = SecondOf(score, i);
                    gap = best - second;
                    reason[i] = "二轮·" + why;
                }
            }

        // ⑤ 置信阶梯（对齐参考实现）：>=90 或分差 >=10 → 命中；>=15 → 低/中置信；否则兜底选项 [0]
        //    🔴 「命中」额外要求 **证据强度 ev>=2**（≥2 字义项精确 / 题面形态精确 / 选项即目标词 / 变体）。
        //       理由：词书里存在大量**单字义项**与**时态/派生词条**，它们本身没歧义，
        //       但会让「单字精确」「一字子串」「词形相似」这类**弱证据**冒出来抢分；
        //       弱证据绝不允许冒充"高置信命中"——降档后仍会作答，但会落进判定明细供离线审计。
        string tier;
        if ((best >= 90 || gap >= 10) && ev[bi] >= 2) tier = "命中";
        else if (best >= 40 && gap >= 5) tier = "中置信";
        else if (best >= 15) tier = "低置信";
        else { tier = "兜底"; bi = 0; }

        return new Pick
        {
            Index = bi,
            Answer = opts[bi],
            Score = best,
            Second = second,
            Tier = tier,
            Reason = reason[bi] ?? "",
        };
    }

    /// <summary>
    /// 取最高分下标。并列打破顺位（严格按序，绝不越过分数）：
    /// ① <paramref name="order"/> 义项下标更小（客户端正确选项恒为第 1 段义项）
    /// ② <paramref name="baseRank"/> 更小（0 = 词典原形，1 = 屈折形式；时态/复数并列时取原形）
    /// ③ 选项下标更靠前（对齐参考实现「并列取靠前候选」）
    /// </summary>
    static int ArgMax(double[] a, int[] order, int[] baseRank, out double best, out double second)
    {
        int bi = 0; best = 0; second = 0;
        for (int i = 0; i < a.Length; i++)
        {
            bool take = a[i] > best
                || (a[i] > 0 && a[i] == best
                    && (order[i] < order[bi] || (order[i] == order[bi] && baseRank[i] < baseRank[bi])));
            if (take)
            {
                if (a[i] > best) second = best;
                else if (a[i] > second) second = a[i];
                best = a[i]; bi = i;
            }
            else if (a[i] > second) second = a[i];
        }
        return bi;
    }

    static double SecondOf(double[] a, int skip)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) if (i != skip && a[i] > s) s = a[i];
        return s;
    }

    /// <summary>题面 → 目标词条集合。精确索引优先，未命中退回包含关系的线性扫描。</summary>
    static List<Entry> FindTargets(string q, bool isCnQ)
    {
        var res = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(Entry e)
        {
            var k = e.Word + "\u0001" + e.Paraphrase;
            if (seen.Add(k)) res.Add(e);
        }

        if (!isCnQ)
        {
            foreach (var w in new[] { NormWord(q), NormWord(StripParens(q)) })
                if (w.Length > 0 && _byWord!.TryGetValue(w, out var list))
                    foreach (var e in list) Add(e);
            if (res.Count == 0)                        // 变体兜底：词条按 / 拆分
                foreach (var e in Snapshot())
                {
                    foreach (var v in e.Word.Split('/'))
                        if (v.Length > 0 && NormWord(v) == NormWord(q)) { Add(e); break; }
                }
            return res;
        }

        var keys = new List<string>();
        foreach (var form in new[] { q, StripParens(q) })
        {
            var n = Norm(form, false);
            if (n.Length > 0) keys.Add(n);
            foreach (var p in Parts(new List<string> { n })) keys.Add(p);
        }
        foreach (var k in keys)
            if (k.Length > 0 && _meaningExact!.TryGetValue(k, out var hit))
                foreach (var e in hit) Add(e);
        if (res.Count > 0) return PreferExpectedForm(res, q);

        string qc = Norm(q, true);
        foreach (var (key, e) in _byMeaning!)
        {
            if (qc.Length == 0 || key.Length == 0) continue;
            if (key == qc) { Add(e); continue; }
            if (qc.Length >= 2 && key.Length >= 2 && (key.Contains(qc) || qc.Contains(key))) Add(e);
        }
        return PreferExpectedForm(res, q);
    }

    /// <summary>
    /// 题面形态优先：客户端题面就是 `H(出题词的 cn)`，所以「<see cref="Entry.ExpectedForm"/> == 题面」是最强信号。
    /// 当多个词共享同一义项（如 object/objective 都含"目标"）时用它收敛到唯一出题词。
    /// 无任何命中则原样返回（保持原候选集，不缩小召回）。
    /// </summary>
    static List<Entry> PreferExpectedForm(List<Entry> res, string q)
    {
        if (res.Count <= 1) return res;
        var qn = Norm(q, true);
        if (qn.Length == 0) return res;
        var byForm = new List<Entry>();
        foreach (var e in res)
            if (e.ExpectedForm.Length > 0 && e.ExpectedForm == qn) byForm.Add(e);
        return byForm.Count > 0 ? byForm : res;
    }

    static List<Entry> Snapshot()
    {
        lock (Lock) return new List<Entry>(_cache!);
    }

    /// <summary>首轮单选项打分：中文题面走「选项 vs 目标词」，英文题面走「选项 vs 目标词释义」。</summary>
    static double ScoreOption(string opt, bool isCnQ, List<Entry> targets, HashSet<string> qPos, List<Entry> all, out string why, out int order, out int ev)
    {
        double best = 0; why = ""; order = int.MaxValue; ev = 1;
        var optNorm = NormWord(opt);
        var optPos = ExtractPos(opt);

        if (isCnQ)
        {
            order = -1;
            foreach (var t in targets)
            {
                double s = 0; string r = "";
                int e = 1;
                if (optNorm.Length > 0 && optNorm == NormWord(t.Word)) { s = 100; r = "选项即目标词"; e = 3; }
                else if (optNorm.Length > 0 && t.Word.Split('/').Any(v => v.Length > 0 && NormWord(v) == optNorm)) { s = 95; r = "变体精确匹配"; e = 3; }
                else
                {
                    double sim = Similarity(opt, t.Word);
                    if (sim > 50) { s = sim * 0.7; r = $"相似度{sim:0}*0.7"; e = 1; }   // 仅词形相似 → 弱证据
                }
                if (s > 0 && optPos.Count > 0 && qPos.Count > 0 && !IsPosCompatible(optPos, qPos)) { s *= 0.6; r += "|词性不兼容*0.6"; e = Math.Min(e, 2); }
                if (s > best) { best = s; why = r; ev = e; }
            }
        }
        else
        {
            var optClean = Norm(opt, true);
            int bestOrd = int.MaxValue;
            foreach (var t in targets)
            {
                // ① 决定性：选项 == 该词「正确选项形态」H(cn)。天学网的正确选项就是这么生成的。
                if (t.ExpectedForm.Length > 0 && optClean.Length > 0 && optClean == t.ExpectedForm)
                {
                    if (100 > best) { best = 100; why = "题面形态精确"; order = -2; bestOrd = -2; ev = 3; }
                    continue;
                }
                // ② 逐义项比对（同下标）：纯净释义在前、原样在后（对齐参考 clean_meanings.concat(meanings)）
                for (int mi = 0; mi < t.Meanings.Count; mi++)
                {
                    var clean = mi < t.CleanMeanings.Count ? t.CleanMeanings[mi] : "";
                    var raw = t.Meanings[mi];
                    for (int v = 0; v < 2; v++)
                    {
                        var m = v == 0 ? clean : raw;
                        if (m.Length == 0) continue;
                        double s = ScoreMeaning(m, opt, out var r, out var e);
                        if (s <= 0) continue;
                        if (optPos.Count > 0 && qPos.Count > 0 && !IsPosCompatible(optPos, qPos)) { s *= 0.6; r += "|词性不兼容*0.6"; e = Math.Min(e, 2); }
                        if (s > best || (s == best && mi < bestOrd)) { best = s; why = r; order = mi; bestOrd = mi; ev = e; }
                    }
                }
            }
            if (best == 0)   // 整体相似度兜底（对齐参考实现：>30 → *0.3）
            {
                double sim = 0;
                foreach (var t in targets) sim = Math.Max(sim, Similarity(opt, t.Word));
                if (sim > 30) { best = sim * 0.3; why = $"整体相似度{sim:0}*0.3"; order = int.MaxValue - 1; ev = 1; }
            }
        }

        // 中文近义干扰项惩罚 / 无信号保底（对齐参考实现；只在选项含 >=2 个汉字且低分时生效）
        if (best < 20 && isCnQ && Regex.Matches(opt, "[\u4e00-\u9fff]").Count >= 2)
        {
            bool inSelf = false, inOther = false;
            foreach (var e in all)
                foreach (var m in e.Meanings)
                    if (m.Contains(opt))
                    {
                        if (targets.Contains(e)) inSelf = true; else inOther = true;
                        break;
                    }
            if (inOther && !inSelf) { best *= 0.2; why += "|出现在他词释义*0.2"; }
            else if (!inOther && !inSelf && best < 15) { best = 15; why += "|无信号保底15"; }
        }
        return best;
    }

    /// <summary>二轮：只用「去括号后的纯净义项」打分（对齐参考 clean_meanings 优先）。</summary>
    static double ScoreOptionClean(string opt, List<Entry> targets, HashSet<string> qPos, out string why, out int order, out int ev)
    {
        double best = 0; why = ""; order = int.MaxValue; ev = 1;
        var optPos = ExtractPos(opt);
        var optClean = Norm(opt, true);
        int bestOrd = int.MaxValue;
        foreach (var t in targets)
        {
            if (t.ExpectedForm.Length > 0 && optClean.Length > 0 && optClean == t.ExpectedForm)
            {
                if (100 > best) { best = 100; why = "题面形态精确"; order = -2; bestOrd = -2; ev = 3; }
                continue;
            }
            for (int mi = 0; mi < t.CleanMeanings.Count; mi++)
            {
                var m = t.CleanMeanings[mi];
                if (m.Length == 0) continue;
                double s = ScoreMeaning(m, opt, out var r, out var e);
                if (s <= 0) continue;
                if (optPos.Count > 0 && qPos.Count > 0 && !IsPosCompatible(optPos, qPos)) { s *= 0.6; r += "|词性不兼容*0.6"; e = Math.Min(e, 2); }
                if (s > best || (s == best && mi < bestOrd)) { best = s; why = r; order = mi; bestOrd = mi; ev = e; }
            }
        }
        return best;
    }

    /// <summary>
    /// 义项（`；` 级）与选项文本的匹配度 + 证据强度 <paramref name="ev"/>
    /// （3 = 整条精确 / 2 = ≥2 字分项精确 / 1 = 单字精确或子串 —— 弱证据）。
    /// 🔴 **长度约束**：单字既不享受 90 分档（降到 65），也**完全不参与子串匹配**。
    /// 原因：词书里存在大量单字义项（奖/门/伞/梦…），若让 1 字符参与子串比对，
    /// 它们会靠"一个字符被长义项包含"污染判定；且 1 字"精确"若按 90 计，
    /// 还会压过真正的 2 字子串命中（90×2/3=60）。故单字只作**弱证据**。
    /// </summary>
    static double ScoreMeaning(string meaning, string opt, out string why, out int ev)
    {
        why = ""; ev = 1;
        var o = Norm(opt, true);
        if (o.Length == 0) return 0;
        var m = Norm(meaning, false);
        if (m.Length == 0) return 0;
        if (m == o) { why = "释义完全相同"; ev = 3; return 100; }

        double best = 0;
        foreach (var part in m.Split(new[] { '，', '、' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            if (p == o)
            {
                double b = p.Length >= 2 ? 90 : 65;
                if (b > best) { best = b; why = $"释义分项相同“{p}”"; ev = p.Length >= 2 ? 2 : 1; }
            }
            else if (p.Length >= 2 && o.Length >= 2 && (p.Contains(o) || o.Contains(p)))
            {
                double s = 90.0 * Math.Min(p.Length, o.Length) / Math.Max(p.Length, o.Length);
                if (s > best) { best = s; why = $"释义分项子串“{p}”({Math.Min(p.Length, o.Length)}/{Math.Max(p.Length, o.Length)})"; ev = 1; }
            }
        }
        return best;
    }

    /// <summary>反向查库：拿选项反查它的释义是否与题面吻合（中文题面首轮全 0 时启用）。</summary>
    static (double score, string why) ReverseLookup(string opt, string q, HashSet<string> qPos)
    {
        var entries = FindTargets(opt, false);
        if (entries.Count == 0) return (0, "");
        var qn = Norm(q, true);
        var qc = Norm(q, false);
        double best = 0; string why = "";
        foreach (var e in entries)
            foreach (var m in e.Meanings)
            {
                double s = ScoreMeaningInverse(m, qn, qc, out var r);
                if (s > best) { best = s; why = r; }
            }
        var optPos = ExtractPos(opt);
        if (best > 0 && optPos.Count > 0 && qPos.Count > 0 && !IsPosCompatible(optPos, qPos)) best *= 0.6;
        return (best, why);
    }

    static double ScoreMeaningInverse(string meaning, string qn, string qc, out string why)
    {
        why = "";
        var m = Norm(meaning, false);
        if (m.Length == 0) return 0;
        if (m == qn || m == qc) { why = "反向释义相同"; return 85; }
        double best = 0;
        foreach (var part in m.Split(new[] { '，', '、' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            if (p == qn || p == qc)
            {
                // 与正向一致：单字义项只作**弱证据**（65），不得压过 2 字精确（85）与 2 字子串
                double b = p.Length >= 2 ? 85 : 65;
                if (best < b) { best = b; why = $"反向分项相同“{p}”"; }
            }
            else if (p.Length >= 2 && qc.Length >= 2 && (p.Contains(qc) || qc.Contains(p)))
            {
                double s = 80.0 * Math.Min(p.Length, qc.Length) / Math.Max(p.Length, qc.Length);
                if (s > best) { best = s; why = $"反向分项子串“{p}”"; }
            }
        }
        return best;
    }

    // ---------------- 供拼写 PK / 诊断使用 ----------------

    /// <summary>中文释义 → 单词（拼写PK / CN→EN）。返回 (单词, 匹配分) 或 null（拼写无法靠选项消歧，只能精确些）。</summary>
    public static (string word, double score)? MatchMeaningToWord(string question)
    {
        EnsureLoaded();
        List<Entry> all; lock (Lock) all = _cache!;
        if (all.Count == 0) return null;
        var q = question ?? "";
        var qc = Norm(q, true);
        if (qc.Length == 0) return null;

        var targets = FindTargets(q, true);
        if (targets.Count == 0)   // 反查兜底：词库里的词反过来比释义
        {
            foreach (var e in all)
            {
                foreach (var m in e.Meanings)
                    if (ScoreMeaningInverse(m, qc, Norm(q, false), out _) >= 80) { targets.Add(e); break; }
            }
        }
        if (targets.Count == 0) return null;

        // 题面（客户端 H(cn)）**保留词性前缀**，是白捡的判别信号：
        // 题面「n. 决定」与 decision(n.) 词性吻合、与 decide(v.) 不吻合 → 优先词性兼容者。
        // 先按词性**过滤**候选（对齐参考 1261–1284），过滤后仍有候选才采用，避免把召回清空。
        var qPos = ExtractPos(q);
        if (targets.Count > 1 && qPos.Count > 0)
        {
            var filtered = targets.Where(t => t.Pos.Count > 0 && IsPosCompatible(t.Pos, qPos)).ToList();
            if (filtered.Count > 0 && filtered.Count < targets.Count) targets = filtered;
        }
        (Entry e, double s, int posOk, int baseRank, int len)? best = null;
        foreach (var t in targets)
        {
            double s = 0;
            foreach (var m in t.Meanings) s = Math.Max(s, ScoreMeaningInverse(m, qc, Norm(q, false), out _));
            if (s <= 0) s = 70;      // 精确索引命中但反查不出 → 基准分
            int posOk = (qPos.Count > 0 && t.Pos.Count > 0 && IsPosCompatible(t.Pos, qPos)) ? 1 : 0;
            int baseRank = Lemma(t.Word) == NormWord(t.Word) ? 0 : 1;   // 时态/派生并列时优先原形
            if (best == null || s > best.Value.s
                || (s == best.Value.s && posOk > best.Value.posOk)
                || (s == best.Value.s && posOk == best.Value.posOk && baseRank < best.Value.baseRank)
                || (s == best.Value.s && posOk == best.Value.posOk && baseRank == best.Value.baseRank && t.Word.Length < best.Value.e.Word.Length))
                best = (t, s, posOk, baseRank, t.Word.Length);
        }
        return best == null ? null : (best.Value.e.Word, best.Value.s);
    }

    /// <summary>中文释义 → 候选单词（按分降序，最多 topN）。用于诊断与消歧。</summary>
    public static List<(string word, double score)> MatchMeaningToWords(string question, int topN = 8)
    {
        EnsureLoaded();
        var res = new List<(string, double)>();
        var q = question ?? "";
        var qc = Norm(q, true);
        if (qc.Length == 0) return res;
        foreach (var t in FindTargets(q, true))
        {
            double s = 0;
            foreach (var m in t.CleanMeanings) s = Math.Max(s, ScoreMeaningInverse(m, qc, Norm(q, false), out _));
            if (s <= 0) s = 70;
            res.Add((t.Word, s));
        }
        return res.GroupBy(x => NormWord(x.Item1))
                  .Select(g => g.OrderByDescending(x => x.Item2).First())
                  .OrderByDescending(x => x.Item2)
                  .Take(topN).ToList();
    }

    /// <summary>单词 → 中文释义（选择PK 的 EN→CN 题）。返回 (释义, 匹配分) 或 null。</summary>
    public static (string meaning, double score)? MatchWordToMeaning(string word)
    {
        EnsureLoaded();
        var w = NormWord(word);
        if (w.Length == 0) return null;
        lock (Lock)
        {
            if (_byWord == null || !_byWord.TryGetValue(w, out var list) || list.Count == 0) return null;
            var e = list[0];
            var m = e.Meanings.FirstOrDefault(p => p.Length > 0) ?? e.Paraphrase;
            return (m, 100);
        }
    }

    /// <summary>该单词的全部中文义项（EN→CN 选择题要逐个义项去比选项）。</summary>
    public static List<string> MeaningsOf(string word)
    {
        EnsureLoaded();
        var w = NormWord(word);
        lock (Lock)
        {
            if (_byWord == null || !_byWord.TryGetValue(w, out var list) || list.Count == 0) return new List<string>();
            var e = list[0];
            var res = new List<string>();
            res.AddRange(Parts(e.Meanings));
            res.AddRange(Parts(e.CleanMeanings));
            return res.Distinct().ToList();
        }
    }

    // ---------------- 归一化 / 工具 ----------------

    /// <summary>单词归一化：小写、只留字母与连字符。</summary>
    internal static string NormWord(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsAsciiLetter(ch) || ch == '-') sb.Append(ch);
        return sb.ToString().Trim('-');
    }

    /// <summary>
    /// 规则化词干（仅用于**并列打破**，不参与打分）：把复数/三单/过去式/现在分词的
    /// 屈折形式折回词典原形（loads/loaded/loading → load）。
    /// 用途：词书里同时收了原形与屈折形式时（「时态词条」），题面/义项可能完全一样；
    /// 此时优先取**原形**——因为天学网的题面 H(cn) 与正确选项都取词条原形语义。
    /// </summary>
    internal static string Lemma(string w)
    {
        var s = NormWord(w);
        if (s.Length < 4) return s;
        if (s.EndsWith("ies")) return s[..^3] + "y";
        if (s.EndsWith("ing"))
        {
            var b = s[..^3];
            if (b.Length >= 2 && b[^1] == b[^2]) b = b[..^1];   // running → run
            return b;
        }
        if (s.EndsWith("ed"))
        {
            var b = s[..^2];
            if (b.Length >= 2 && b[^1] == b[^2]) b = b[..^1];   // planned → plan
            return b;
        }
        if (s.EndsWith("es")) return s[..^2];
        if (s.EndsWith("s")) return s[..^1];
        return s;
    }

    /// <summary>
    /// 权威归一化（保留 `，、；` 分隔符，便于后续分项切分）：
    /// 解 HTML 实体 → 去词性前缀 → 按需去括号 → 转小写 → 只留「汉字 / 英文字母 / 空格 / 分隔符」。
    /// </summary>
    internal static string Norm(string s, bool stripParens)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = RemovePos(DecodeHtml(s).Trim());
        if (stripParens) t = StripParens(t);
        var sb = new StringBuilder();
        foreach (var ch in t.ToLowerInvariant())
        {
            if (ch >= 0x4e00 && ch <= 0x9fff) sb.Append(ch);
            else if (char.IsAsciiLetter(ch)) sb.Append(ch);
            else if (ch is ' ' or '，' or '、' or '；') sb.Append(ch);
            else if (ch == ',') sb.Append('，');
            else if (ch == ';') sb.Append('；');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>历史兼容的义项清洗：等价于「去词性前缀 + 去括号 + 去全部标点」。</summary>
    internal static string CleanMeaning(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in Norm(s, true))
            if (ch is not ('，' or '、' or '；')) sb.Append(ch);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>删除括号及其内容（对齐客户端 H()）。</summary>
    internal static string StripParens(string s) => Regex.Replace(s ?? "", @"[(（][^)）]*[)）]", "");

    static string DecodeHtml(string s)
    {
        try { return WebUtility.HtmlDecode(s ?? ""); } catch { return s ?? ""; }
    }

    /// <summary>`；;`/换行 级义项原始切片（未归一化、保留括号），供 Meanings/CleanMeanings 同下标构建。</summary>
    static List<string> Segments1(string paraphrase)
    {
        var raw = (paraphrase ?? "").Replace("\r\n", "；").Replace("\n", "；").Replace("\r", "；");
        var res = new List<string>();
        foreach (var p in raw.Split('；', ';'))
        {
            var t = p.Trim();
            if (t.Length > 0) res.Add(t);
        }
        return res;
    }

    /// <summary>
    /// 客户端 PKCore `H(t)` 的忠实移植（题面/正确选项的生成形态）：
    ///   去括号（含内容）→ 按 `; | , | ； | ，` 切分取第 1 段 → 若第 1 段无中文则回退整串 → 去掉 phr./phrase。
    /// 返回经 <see cref="Norm"/>（去词性、小写、只留汉字/字母/空格/分隔符）后的结果。
    /// </summary>
    internal static string HForm(string paraphrase)
    {
        var t = DecodeHtml(paraphrase ?? "");
        if (t.Length == 0) return "";
        var n = StripParens(t);
        var parts = Regex.Split(n, @"[;|,|；|，]");
        var r = parts.Length > 0 ? parts[0] : "";
        if (!Regex.IsMatch(r, @"[\u4E00-\u9FFF]")) r = t;      // 第 1 段无中文 → 回退整串（对齐客户端）
        r = r.Replace("phr.", "").Replace("phrase", "").Replace("Phrase", "");
        return Norm(r, false);
    }

    /// <summary>把 `；` 级义项再按 `，,、` 切成最细片段（索引与诊断用）。</summary>
    static List<string> Parts(List<string> meanings)
    {
        var res = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in meanings)
            foreach (var p in m.Split(new[] { '，', '、' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = p.Trim();
                if (t.Length >= 1 && seen.Add(t)) res.Add(t);
            }
        return res;
    }

    static readonly string[] PosList = { "interj", "prep", "pron", "abbr", "conj", "adj", "adv", "num", "art", "vt", "vi", "n", "v" };

    /// <summary>抽取词性标签（对齐参考实现：`v.` / `v ` / `v` 直接接汉字都算；`vt`·`vi` 归并为 `v`）。</summary>
    internal static HashSet<string> ExtractPos(string text)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        var t = DecodeHtml(text ?? "").TrimStart().ToLowerInvariant();
        int i = 0;
        while (i < t.Length)
        {
            int j = MatchPos(t, i);
            if (j < 0) break;
            var tag = t.Substring(i, j - i).TrimEnd('.', ' ');
            tag = tag.Trim();
            res.Add(tag is "vt" or "vi" ? "v" : tag);
            i = j;
            while (i < t.Length && (char.IsWhiteSpace(t[i]) || t[i] == '&')) i++;
        }
        return res;
    }

    /// <summary>尝试在 pos 处匹配一个词性缩写；命中返回结束下标（含点号），否则 -1。</summary>
    static int MatchPos(string lower, int pos)
    {
        foreach (var p in PosList)
        {
            if (pos + p.Length > lower.Length) continue;
            if (string.CompareOrdinal(lower, pos, p, 0, p.Length) != 0) continue;
            int j = pos + p.Length;
            if (j < lower.Length)
            {
                char c = lower[j];
                // 词性后必须是 . / 空白 / 汉字 / 串尾，避免把 "nice" 误当 "n"
                if (c != '.' && !char.IsWhiteSpace(c) && !(c >= 0x4e00 && c <= 0x9fff)) continue;
            }
            if (j < lower.Length && lower[j] == '.') j++;
            return j;
        }
        return -1;
    }

    /// <summary>去掉开头的词性前缀（多段以 `&` 连接也一并去掉）。**不动括号**（括号由调用方按需处理）。</summary>
    internal static string RemovePos(string text)
    {
        var t = text ?? "";
        var lower = t.ToLowerInvariant();
        int i = 0;
        while (i < lower.Length)
        {
            int j = MatchPos(lower, i);
            if (j < 0) break;
            i = j;
            while (i < lower.Length && (char.IsWhiteSpace(lower[i]) || lower[i] == '&')) i++;
        }
        var rest = t[i..].Trim();
        if (rest.Length == 0)
        {
            // 整串只有词性（如 "n."）→ 参考实现退回按第一个 '.' 切
            int dot = t.IndexOf('.');
            rest = dot >= 0 && dot < t.Length - 1 ? t[(dot + 1)..].Trim() : t.Trim();
        }
        return rest;
    }

    /// <summary>词性是否兼容（任一侧为空 / 无交集即判不兼容由调用方决定；这里只回答有无交集）。</summary>
    internal static bool IsPosCompatible(HashSet<string> a, HashSet<string> b) => a.Overlaps(b);

    /// <summary>参考实现的 calculateSimilarity：整串 → 包含（比例×90）→ 词级（×80）+ 字符级（×20）。</summary>
    internal static double Similarity(string a, string b)
    {
        var s1 = Norm(a, true);
        var s2 = Norm(b, true);
        if (s1.Length == 0 || s2.Length == 0) return 0;
        if (s1 == s2) return 100;
        if (s1.Contains(s2) || s2.Contains(s1))
            return 90.0 * Math.Min(s1.Length, s2.Length) / Math.Max(s1.Length, s2.Length);

        var w1 = s1.Split(new[] { ' ', '，', '、', '；' }, StringSplitOptions.RemoveEmptyEntries);
        var w2 = s2.Split(new[] { ' ', '，', '、', '；' }, StringSplitOptions.RemoveEmptyEntries);
        if (w1.Length == 0 || w2.Length == 0)
            return 70.0 * CommonChars(s1, s2) / Math.Max(s1.Length, s2.Length);

        double matched = 0;
        foreach (var x in w1)
            foreach (var y in w2)
            {
                if (x == y) { matched += 1; break; }
                if (x.Contains(y) || y.Contains(x)) { matched += 0.7; break; }
            }
        double wordScore = 80.0 * matched / Math.Max(w1.Length, w2.Length);
        double charScore = 20.0 * CommonChars(s1, s2) / Math.Max(s1.Length, s2.Length);
        return wordScore + charScore;
    }

    static int CommonChars(string s1, string s2)
    {
        var set = new HashSet<char>(s2);
        int n = 0;
        foreach (var c in s1) if (set.Contains(c)) n++;
        return n;
    }
}
