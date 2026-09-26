using System.Text.RegularExpressions;

namespace TxwExtract.Core;

public sealed class MatchResult
{
    public string Conf { get; set; } = "未命中";
    public bool StemOk { get; set; }
    public bool OptOk { get; set; }
    public string BankAnsText { get; set; } = "";
    public string TargetAns { get; set; } = "";   // 本题应点的字母
    public string TargetText { get; set; } = "";
    public bool Mapped { get; set; }
    public BankItem? Best { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ConfLevel Level => ConfLevelEx.FromLabel(Conf);
}

public sealed record ClickDecision(OcrLine Line, string Target, string Via, string Conf);

/// <summary>题干/选项文本匹配与置信度判定（Node 版 matchBank / mapAnsToTarget / decideClick 的 C# 移植）。</summary>
public static class Matcher
{
    static string NormQ(string s) =>
        Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9\u4e00-\u9fff ]", " "), @"\s+", " ").Trim();

    static List<string> Stem(string s) => NormQ(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3).ToList();

    /// <summary>归一化题干词表（供 <see cref="BankItem.StemWords"/> 懒缓存调用，纯加速不改结果）。</summary>
    internal static string[] StemWords(string s) => Stem(s).ToArray();

    /// <summary>批量归一化选项文本（供 <see cref="BankItem.NormOpts"/> 懒缓存调用，剔除空项）。</summary>
    internal static List<string> NormAll(IEnumerable<string> items)
        => items.Select(NormQ).Where(x => x.Length > 0).ToList();

    /// <summary>把题库答案内容映射到本题选项（应对选项顺序打乱/轻微改写）。</summary>
    public static (string Ans, string Text, bool Mapped) MapAnsToTarget(string bankAnsText, List<string> targetOpts)
    {
        var nbt = NormQ(bankAnsText);
        var words = nbt.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToList();
        double bestScore = 0; int bestIdx = -1;
        for (int i = 0; i < targetOpts.Count; i++)
        {
            var no = NormQ(targetOpts[i]);
            if (string.IsNullOrEmpty(no)) continue;
            if (no == nbt) { bestScore = 1; bestIdx = i; break; }
            int hit = words.Count(w => no.Contains(w, StringComparison.Ordinal));
            double score = words.Count > 0 ? (double)hit / words.Count : 0;
            if (score > bestScore) { bestScore = score; bestIdx = i; }
        }
        bool mapped = bestIdx >= 0 && bestScore >= 0.6;
        return (mapped ? "ABCD"[bestIdx].ToString() : "", mapped ? targetOpts[bestIdx] : "", mapped);
    }

    /// <summary>选项打分：<paramref name="tOpts"/> 是页面原文（需归一化）；<paramref name="bNorm"/> 是题库侧
    /// <b>已归一化</b>的选项（由 <see cref="BankItem.NormOpts"/> 懒缓存提供，避免每轮重复归一化）。</summary>
    static int OptScore(List<string> tOpts, List<string> bNorm)
    {
        int hit = 0;
        var to = tOpts.Select(NormQ).Where(x => x.Length > 0).ToList();
        foreach (var t in to)
        {
            var w = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 3).ToList();
            if (w.Count == 0) continue;
            double best = 0;
            foreach (var b in bNorm)
            {
                int s = w.Count(x => b.Contains(x, StringComparison.Ordinal));
                best = Math.Max(best, (double)s / w.Count);
            }
            if (best >= 0.6) hit++;
        }
        return hit;
    }

    public static MatchResult MatchBank(string targetQt, List<string> targetOpts)
    {
        var tw = Stem(targetQt);
        BankItem? best = null; double bs = 0; int bOpt = 0;

        foreach (var b in BankService.Items)
        {
            // 题干/选项的归一化结果在 BankItem 内懒缓存（每轮对全库打分，实时归一化是主要开销）
            int ss = tw.Count(w => b.StemWords.Contains(w));
            int os = OptScore(targetOpts, b.NormOpts);
            double total = ss * 2 + os * 3;
            if (total > bs) { bs = total; best = b; bOpt = os; }
        }

        bool stemOk = best != null && best.StemWords.Count(w => tw.Contains(w)) >= 2;
        bool optOk = bOpt >= 2;

        string conf = (stemOk && optOk) ? "高置信"
                    : (stemOk || optOk) ? "中置信(仅" + (stemOk ? "题干" : "选项") + ")"
                    : "未命中";

        var res = new MatchResult { Conf = conf, StemOk = stemOk, OptOk = optOk, Best = best };

        if (best != null && !string.IsNullOrEmpty(best.Ans))
        {
            int bi = "ABCD".IndexOf(best.Ans, StringComparison.Ordinal);
            string bankAnsText = (bi >= 0 && bi < best.Opts.Count) ? best.Opts[bi] : "";
            if (!string.IsNullOrEmpty(bankAnsText))
            {
                res.BankAnsText = bankAnsText;
                var m = MapAnsToTarget(bankAnsText, targetOpts ?? new List<string>());
                res.TargetAns = m.Ans; res.TargetText = m.Text; res.Mapped = m.Mapped;
            }
        }
        return res;
    }

    /// <summary>从 OCR 文本中解析出题目（题号 + 题干 + A-D 选项）。</summary>
    public static List<(int No, string Qt, List<string> Opts)> ParsePaperText(string text)
    {
        var qs = new List<(int, string, List<string>)>();
        (int No, string Qt, List<string> Opts)? cur = null;
        // v2.1.19：听力长对话等题型的小题题干常常不带题号（如 "What benefit of staying in nature...?"），
        // 原先只认数字题号导致整屏解析出 0 题 → 永远"未找到满足置信度门槛"。补充规则：
        // 以疑问词开头的行且（当前无题 或 当前题已有选项=上一题已结束）→ 视为新题题干。
        var mwRe = new Regex(@"^(what|which|why|who|whom|whose|where|when|how|does|do|is|are|can|will)\b(.{3,150})$", RegexOptions.IgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var ln = raw.Trim();
            var mq = Regex.Match(ln, @"^(\d{1,2})[.、)\s]+(.{3,150}?)\s*$");
            var mo = Regex.Match(ln, @"^([A-D])[.、)\s]+(.{1,120}?)\s*$");
            var mw = mwRe.Match(ln);
            if (mq.Success && !mo.Success)
            {
                if (cur != null) qs.Add(cur.Value);
                cur = (int.Parse(mq.Groups[1].Value), mq.Groups[2].Value.Trim(), new List<string>());
            }
            else if (mw.Success && !mo.Success && (cur == null || cur.Value.Opts.Count > 0))
            {
                if (cur != null) qs.Add(cur.Value);
                cur = ((cur?.No ?? 0) + 1, ln, new List<string>());
            }
            else if (mo.Success && cur != null)
            {
                var c = cur.Value; c.Opts.Add(mo.Groups[2].Value.Trim()); cur = c;
            }
        }
        if (cur != null) qs.Add(cur.Value);
        return qs;
    }

    /// <summary>找与目标文本最相似的 OCR 行（词重叠度 ≥ 0.6）。</summary>
    public static (OcrLine? Line, double Score) BestOcrLine(string target, IReadOnlyList<OcrLine> lines)
    {
        var tw = NormQ(target).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToList();
        if (tw.Count == 0) return (null, 0);
        OcrLine? best = null; double bs = 0;
        foreach (var L in lines)
        {
            var lw = NormQ(L.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToList();
            if (lw.Count == 0) continue;
            int hit = tw.Count(w => lw.Contains(w));
            double score = (double)hit / tw.Count;
            if (score > bs) { bs = score; best = L; }
        }
        return bs >= 0.6 ? (best, bs) : (null, bs);
    }

    /// <summary>短答案（≤3 字）专用：去掉选项前缀后要求 OCR 行主体基本等于答案，避免误命中长句。</summary>
    static (OcrLine? Line, double Score) BestOcrLineExact(string target, IReadOnlyList<OcrLine> lines)
    {
        var nt = NormQ(target);
        if (nt.Length == 0) return (null, 0);
        OcrLine? best = null; double bs = 0;
        foreach (var L in lines)
        {
            var raw = NormQ(L.Text);
            var nl = Regex.Replace(raw, @"^[abcd][.\u3002\uff09)\uff1a:]\s*", ""); // 去选项前缀 A. B) 等
            if (string.IsNullOrEmpty(nl) || !nl.Contains(nt, StringComparison.Ordinal)) continue;
            double cover = (double)nt.Length / nl.Length;   // 答案占整行比例
            if (cover < 0.6) continue;                       // 比例过低 → 长句误命中，跳过
            double score = 0.6 + 0.4 * Math.Min(1, cover);
            if (score > bs) { bs = score; best = L; }
        }
        return best != null ? (best, bs) : (null, 0);
    }

    /// <summary>按答案长度自动选择匹配器：短答案精确匹配，长答案词重叠匹配（避免 "Yes/No/Red" 被漏点）。</summary>
    static (OcrLine? Line, double Score) MatchAnswerLine(string target, IReadOnlyList<OcrLine> lines)
    {
        var t = (target ?? "").Trim();
        return t.Length <= 3 ? BestOcrLineExact(t, lines) : BestOcrLine(t, lines);
    }

    /// <summary>题干样行判定（v1.0.19）：以数字题号开头（"5. " / "5、" / "5)"）或以问号结尾。
    /// 点击候选必须排除这类行 —— 题干与答案共用大量词（问 "What does the woman need to fix
    /// her phone?" vs 答案含 "her phone"），词重叠打分会让题干行得分反超真正的选项行，
    /// 用户实测兜底路径点到了题干本身（严重 bug）。</summary>
    static bool IsStemLike(string s)
    {
        s = (s ?? "").Trim();
        // A-D 前缀行是选项（对话补全题型选项本身可能是问句），永不视为题干
        if (Regex.IsMatch(s, @"^[A-Da-d][.、)：:]\s*\S")) return false;
        // (?!\d)：排除 "3.14" / "10:00" 这类以数字+标点+数字开头的选项（题号后不会紧跟数字）
        return Regex.IsMatch(s, @"^\d{1,2}[.、)：:](?!\d)") || s.EndsWith("?") || s.EndsWith("？");
    }

    /// <summary>
    /// 决策：给定 OCR 行与题库答案内容列表，找出应当点击的行。
    /// minConf 为用户选择的"最低置信度"：低于该档位的匹配直接跳过；
    /// 兜底模糊匹配（无题干/选项支撑，质量最低）仅"低"档启用。
    /// </summary>
    public static ClickDecision? DecideClick(IReadOnlyList<OcrLine> lines, IReadOnlyList<string> ansTexts, ConfLevel minConf)
    {
        // v1.0.19：点击候选剔除题干样行（数字开头/问号结尾）——防"点到题干"（见 IsStemLike 注释）。
        var clickable = lines.Where(l => !IsStemLike(l.Text)).ToList();

        // 1) 解析成题 → 题库匹配 → 置信度门槛过滤
        var parsed = ParsePaperText(string.Join("\n", lines.Select(l => l.Text)));
        if (parsed.Count > 0)
        {
            OcrLine? bestHit = null; double bestScore = 0; string bestConf = ""; string target = "";
            foreach (var q in parsed)
            {
                var r = MatchBank(q.Qt, q.Opts);
                if (r.Level < minConf) continue;   // ← 置信度门槛
                var m = MatchAnswerLine(r.BankAnsText, clickable);   // 短答案精确匹配 / 长答案词重叠
                if (m.Line != null && m.Score > bestScore)
                { bestScore = m.Score; bestHit = m.Line; target = r.BankAnsText; bestConf = r.Conf; }
            }
            if (bestHit != null) return new ClickDecision(bestHit, target, "parse", bestConf);
        }

        // 2) 兜底：答案内容直接与 OCR 行模糊匹配（仅"低"档）
        if (minConf > ConfLevel.Low) return null;
        OcrLine? fb = null; double fbs = 0; string ft = "";
        foreach (var at in ansTexts)
        {
            var m = MatchAnswerLine(at, clickable);
            if (m.Line != null && m.Score > fbs) { fbs = m.Score; fb = m.Line; ft = at; }
        }
        return fb != null ? new ClickDecision(fb, ft, "fallback", "未命中(兜底)") : null;
    }
}
