using System.Text.RegularExpressions;

namespace TxwExtract.Core;

public enum StopReason { Manual, EndDetected, Error, NoWindow, EmptyBank }

/// <summary>
/// 视觉闭环自动答题引擎：截图 → OCR → 变化检测 → 题库匹配（按置信度门槛） → 点击。
/// 全部在进程内完成（P/Invoke + WinRT OCR），无外部进程调用。
/// </summary>
public sealed class AutoEngine : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private List<string>? _lastLines;

    /// <summary>页面连续无变化多少轮后，再次检测到变化即视为"客户端自行跳页"，触发整页补答。</summary>
    private const int StaleJumpThreshold = 2;
    /// <summary>整页补答中单屏点击上限（防 OCR 抖动导致同一屏无限点击）。</summary>
    private const int MaxClicksPerScreen = 15;
    /// <summary>整页补答最多翻页次数（防任何意外情况下的无限翻页）。</summary>
    private const int MaxPageTurns = 30;

    public bool Running => _cts != null && !_cts.IsCancellationRequested;
    public int Round { get; private set; }
    public int Clicked { get; private set; }
    public int NotFound { get; private set; }
    public int UnchangedRounds { get; private set; }
    public DateTime StartedAt { get; private set; }

    public event Action<string>? Log;
    public event Action<List<OcrLine>, string>? Preview;   // (识别到的行, 窗口信息)
    public event Action<StopReason, string>? Stopped;

    private void Emit(string msg)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
        Log?.Invoke(line);
    }

    public void Start(IntPtr hwnd, string windowLabel, ConfLevel minConf, int intervalMs)
    {
        if (Running) return;
        if (hwnd == IntPtr.Zero) { Stopped?.Invoke(StopReason.NoWindow, "未选择目标窗口"); return; }

        _cts = new CancellationTokenSource();
        Round = 0; Clicked = 0; NotFound = 0; UnchangedRounds = 0; _lastLines = null;
        StartedAt = DateTime.Now;

        WinApi.Activate(hwnd);
        Emit($"开始自动回答（窗口={windowLabel}，间隔 {intervalMs / 1000}s，最低置信度={minConf.Name()}；终止标志=检测到成绩/得分页）");

        _loop = Task.Run(() => LoopAsync(hwnd, windowLabel, minConf, intervalMs, _cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        Stopped?.Invoke(StopReason.Manual, "已请求停止");
        Emit("正在停止…");
    }

    private async Task LoopAsync(IntPtr hwnd, string label, ConfLevel minConf, int intervalMs, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var outcome = await DoRoundAsync(hwnd, label, minConf, token);
                    if (outcome == RoundOutcome.End)
                    {
                        Stopped?.Invoke(StopReason.EndDetected, $"检测到成绩/得分页，完成 ✅（共 {Round} 轮，点击 {Clicked} 次）");
                        _cts?.Cancel();
                        return;
                    }
                    if (outcome == RoundOutcome.NoWindow)
                    {
                        Stopped?.Invoke(StopReason.NoWindow, "未找到目标窗口（客户端已关闭？）");
                        _cts?.Cancel();
                        return;
                    }
                    if (outcome == RoundOutcome.EmptyBank)
                    {
                        Stopped?.Invoke(StopReason.EmptyBank, "题库为空——请先在客户端打开过试卷");
                        _cts?.Cancel();
                        return;
                    }
                }
                catch (Exception ex) { Emit("轮询异常: " + ex.Message); }

                int wait = Math.Max(1000, intervalMs - (int)sw.ElapsedMilliseconds);
                await Task.Delay(wait, token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { Stopped?.Invoke(StopReason.Error, ex.Message); }
    }

    private enum RoundOutcome { Ok, End, NoWindow, EmptyBank }

    private async Task<RoundOutcome> DoRoundAsync(IntPtr hwnd, string label, ConfLevel minConf, CancellationToken token)
    {
        using var bmp = WinApi.CaptureWindow(hwnd, out _);
        if (bmp == null) return RoundOutcome.NoWindow;

        var lines = await OcrService.RecognizeAsync(bmp);
        Preview?.Invoke(lines, $"HWND: {hwnd}  窗口: {label}");

        if (lines.Count == 0) { Emit("本轮未识别到文字"); return RoundOutcome.Ok; }

        // 1) 终止标志：成绩/得分/正确率 + 数字，或提交/完成类关键词
        if (IsEndPage(lines))
        {
            Emit("检测到成绩/得分页 —— 自动回答完成 ✅");
            return RoundOutcome.End;
        }

        // 2) 精确度优先：页面无变化则只 OCR，不做匹配/点击
        var now = lines.Select(l => Norm(l.Text)).ToList();
        double sim = _lastLines != null ? LinesSim(_lastLines, now) : -1;
        if (_lastLines != null && sim >= 0.55)
        {
            UnchangedRounds++;
            if (UnchangedRounds % 5 == 1)
                Emit($"页面稳定（相似度 {sim:F2}），仅轮询等待变化…（累计 {UnchangedRounds} 轮无变化）");
            return RoundOutcome.Ok;
        }
        bool jumpAfterStale = UnchangedRounds >= StaleJumpThreshold;
        _lastLines = now;
        if (UnchangedRounds > 0)
            Emit($"页面发生变化（此前稳定 {UnchangedRounds} 轮），" + (jumpAfterStale ? "疑似客户端自行跳页" : "进入匹配流程"));
        UnchangedRounds = 0;

        // 2.5) 页面稳定数轮后突变（天学网有时会自行跳页）：
        //      新页面可能因篇幅限制/题型特色（翻页式）无法一屏答完 —— 进入整页补答。
        if (jumpAfterStale)
        {
            bool ended = await SweepDrainAsync(hwnd, label, minConf, token, "进入整页补答（页面稳定后突变）");
            return ended ? RoundOutcome.End : RoundOutcome.Ok;
        }

        // 3) 匹配 + 点击（PostMessage 窗口内相对坐标 —— 目标窗口被遮挡也能命中）
        var ansTexts = BankService.AnswerTexts();
        if (ansTexts.Count == 0) return RoundOutcome.EmptyBank;

        var hit = Matcher.DecideClick(lines, ansTexts, minConf);
        if (hit == null)
        {
            NotFound++;
            Emit($"未找到满足置信度门槛（≥{minConf.Name()}）的选项（识别到 {lines.Count} 行，题库 {ansTexts.Count} 个候选答案）");
            // v2.1.24：无可点项时兜底检测"提交"——但仅在**没有"下一页"**时（提交与下一页互斥，
            // 有下一页=还有题要答，不能提前提交；无下一页=最后一页才点提交）
            if (!lines.Any(IsNextPageLine))
            {
                var sub = lines.FirstOrDefault(IsSubmitLine);
                if (sub != null)
                {
                    int sx = sub.X + sub.W / 2, sy = sub.Y + sub.H / 2;
                    bool sent = WinApi.ClickWindow(hwnd, sx, sy);
                    Emit($"检测到“提交”按钮 —— 点击 @(窗口内 {sx},{sy})" + (sent ? "" : "（投递失败）"));
                    await Task.Delay(900, token);   // 等提交/成绩渲染，下一轮主循环检测成绩页
                }
            }
            return RoundOutcome.Ok;
        }

        int cx = hit.Line.X + hit.Line.W / 2;
        int cy = hit.Line.Y + hit.Line.H / 2;
        bool ok = WinApi.ClickWindow(hwnd, cx, cy);
        Round++; Clicked++;
        Emit($"第 {Round} 轮: 点击 “{Trunc(hit.Line.Text, 40)}” @(窗口内 {cx},{cy}) 来源={hit.Via} 置信度={hit.Conf}" + (ok ? "" : " 点击失败"));
        if (!ok) return RoundOutcome.Ok;

        // v2.1.20：每次自发点击后立即推进——向下滚动找新题 → 滚到底点"下一页" → 翻页继续。
        // （原设计只在"页面稳定数轮后突变"才触发补答，点击后不推进，且 PostMessage 滚轮对
        //   Chromium 客户端无效 —— 用户反馈"下滚页面操作不执行"。）
        await Task.Delay(400, token);   // 等点击生效/选中态渲染，避免误判变化
        bool advEnd = await SweepDrainAsync(hwnd, label, minConf, token, "点击后推进（向下滚动 + 翻页）");
        return advEnd ? RoundOutcome.End : RoundOutcome.Ok;
    }

    /// <summary>成绩/得分/提交完成类页面判定（常规轮询与整页补答共用）。</summary>
    private static bool IsEndPage(List<OcrLine> lines)
    {
        string joined = string.Join(" ", lines.Select(l => l.Text));
        return Regex.IsMatch(joined, @"(得分|成绩|正确率)\s*[:：]?\s*[0-9０-９]|(提交成功|练习完成|试卷完成|作答完成|已交卷|本卷完成|finished|submitted|completed)", RegexOptions.IgnoreCase);
    }

    /// <summary>OCR 行是否为"下一页"按钮（翻页题型标志）；去空格以容忍 OCR 断字。</summary>
    private static bool IsNextPageLine(OcrLine l) =>
        l.Text.Replace(" ", "").Replace("\u3000", "").Contains("下一页");

    /// <summary>
    /// OCR 行是否为"提交"按钮（当前页已作答完全的标志）。
    /// 模糊匹配：含"提交"二字即可（去空格容忍 OCR 断字）；"提交"字样不会误伤题干/选项。
    /// </summary>
    private static bool IsSubmitLine(OcrLine l) =>
        l.Text.Replace(" ", "").Replace("\u3000", "").Contains("提交");

    /// <summary>
    /// 整页补答 / 点击后推进：滚动作答 + 翻页处理，全程不设轮询冷却，单线顺序执行。
    /// 触发时机（v2.1.20 起两个）：
    ///   ① 页面稳定数轮后突变（客户端自行跳页）—— 整页补答；
    ///   ② 每次自发点击选项后 —— 点击后推进（用户要求：点击后自动下滚 + 点下一页）。
    /// 流程：
    ///   ① 先答当前屏 → 向下滚动 → OCR：有新内容就立刻回答并继续滚动，直到"无新内容。"（滚到底）
    ///   ② 若 OCR 出现"下一页"（翻页题型），在 ① 之后点击它，翻页后对新页重复 ①②
    /// 返回 true = 补答中检测到成绩页（整个引擎应终止）。
    /// </summary>
    private async Task<bool> SweepDrainAsync(IntPtr hwnd, string label, ConfLevel minConf, CancellationToken token, string enterMsg)
    {
        Emit(enterMsg);
        var seen = new HashSet<string>();   // 补答全程已见过的行（Norm 后），判定"是否有新内容"

        // 截图+OCR 并并入 seen；返回 (行, 新增行数)。新增数 <0 = 窗口丢失。
        async Task<(List<OcrLine> Lines, int Added)> OcrAsync()
        {
            using var b = WinApi.CaptureWindow(hwnd, out _);
            if (b == null) return (new List<OcrLine>(), -1);
            var ls = await OcrService.RecognizeAsync(b);
            Preview?.Invoke(ls, $"HWND: {hwnd}  窗口: {label}（补答中）");
            _lastLines = ls.Select(l => Norm(l.Text)).ToList();   // 保持主循环变化检测同步
            int n = 0;
            foreach (var l in ls) { if (seen.Add(Norm(l.Text))) n++; }
            return (ls, n);
        }

        // v2.1.23：检测"提交"按钮并点击（当前页已作答完全的标志，模糊匹配"提交"二字）。
        // 返回 true = 点击提交后确认成绩/提交成功页（整个引擎应终止）。
        async Task<bool> SubmitIfShownAsync(IReadOnlyList<OcrLine> lines)
        {
            var sub = lines.FirstOrDefault(IsSubmitLine);
            if (sub == null) return false;
            int sx = sub.X + sub.W / 2, sy = sub.Y + sub.H / 2;
            bool sent = WinApi.ClickWindow(hwnd, sx, sy);
            Emit($"检测到“提交”按钮（当前页已作答完全）—— 点击 @(窗口内 {sx},{sy})" + (sent ? "" : "（投递失败）"));
            await Task.Delay(900, token);   // 等提交/成绩渲染
            var (lines2, _) = await OcrAsync();
            if (IsEndPage(lines2)) { Emit("提交后检测到成绩/得分页 —— 自动回答完成 ✅"); return true; }
            return false;   // 仍在作答页（可能需确认弹窗），交回主循环/下一轮处理
        }

        for (int page = 1; page <= MaxPageTurns && !token.IsCancellationRequested; page++)
        {
            // ① 答当前屏（含刚翻过来的新页首屏）
            await AnswerScreenAsync(hwnd, minConf, token, seen);

            // ② 滚动 → OCR → 有新内容立刻回答（无冷却立刻重复），直到"无新内容。"
            var lastLines = new List<OcrLine>();
            while (!token.IsCancellationRequested)
            {
                // v2.1.22：天学网题目占屏面积大，单次滚动 3 行幅度不够（用户实测），改 6 行
                WinApi.ScrollWindow(hwnd, -6);
                await Task.Delay(320, token);   // 仅滚动渲染缓冲，非轮询冷却
                var (lines, added) = await OcrAsync();
                if (added < 0) { Emit("补答中止：目标窗口丢失"); return false; }
                lastLines = lines;
                if (IsEndPage(lines)) { Emit("补答中检测到成绩/得分页 —— 自动回答完成 ✅"); return true; }
                if (added == 0) { Emit("无新内容。"); break; }   // 已滚到底
                await AnswerScreenAsync(hwnd, minConf, token, seen);
            }
            if (token.IsCancellationRequested) break;

            // ③ 翻页题型：滚到底后出现"下一页"→ 点击，然后对新页重复 ①②。
            //    v2.1.24 修正：**"提交"与"下一页"互斥、不会同时出现** ——
            //    有"下一页" = 还有后续页，必须继续作答；没有"下一页" = 最后一页，此时才点"提交"。
            var np = lastLines.FirstOrDefault(IsNextPageLine);
            if (np == null)
            {
                // 最后一页：检测"提交"并点击（当前页已作答完全）
                if (await SubmitIfShownAsync(lastLines)) return true;
                break;   // 无下一页且无提交（或提交后仍在作答页）：补答完成，交回主循环
            }
            int nx = np.X + np.W / 2, ny = np.Y + np.H / 2;
            bool sent = WinApi.ClickWindow(hwnd, nx, ny);
            Emit($"检测到“下一页”（翻页题型）—— 点击 @(窗口内 {nx},{ny})" + (sent ? "" : "（投递失败）"));
            await Task.Delay(700, token);   // 等翻页渲染
            var (_, afterAdded) = await OcrAsync();   // 新页首屏并入 seen（下一轮滚动比较的基线）
            if (afterAdded == 0)
            {
                Emit("点击“下一页”后页面无变化（疑似 PostMessage 点击对自绘控件无效），补答终止");
                break;
            }
        }

        Emit("整页补答完成，回到常规轮询");
        return false;
    }

    /// <summary>
    /// 对当前屏反复"OCR→匹配→点击"直到没有新的可点项（一屏多题时逐个处理）。
    /// 以"点击目标（文本+坐标）"去重：最佳可点项与已点过的一致即停 ——
    /// 该机制同时天然防住 PostMessage 失效时的死循环（点击无效→画面不变→同目标→停）。
    /// </summary>
    private async Task AnswerScreenAsync(IntPtr hwnd, ConfLevel minConf, CancellationToken token, HashSet<string> seen)
    {
        var clicked = new HashSet<string>();
        for (int i = 0; i < MaxClicksPerScreen && !token.IsCancellationRequested; i++)
        {
            using var bmp = WinApi.CaptureWindow(hwnd, out _);
            if (bmp == null) return;
            var lines = await OcrService.RecognizeAsync(bmp);
            foreach (var l in lines) seen.Add(Norm(l.Text));
            var hit = Matcher.DecideClick(lines, BankService.AnswerTexts(), minConf);
            if (hit == null) return;
            int cx = hit.Line.X + hit.Line.W / 2, cy = hit.Line.Y + hit.Line.H / 2;
            // v2.1.20：去重键改为仅文本（去坐标）——滚动后同一选项的 OCR 坐标会变，
            // 原"文本@坐标"键会导致滚动中同一选项被重复点击。
            string key = Norm(hit.Line.Text);
            if (!clicked.Add(key)) return;   // 最佳可点项无推进 → 本屏已处理完（或点击无效）
            bool ok = WinApi.ClickWindow(hwnd, cx, cy);
            Round++; Clicked++;
            Emit($"补答: 点击 “{Trunc(hit.Line.Text, 40)}” @(窗口内 {cx},{cy}) 来源={hit.Via} 置信度={hit.Conf}" + (ok ? "" : " 点击失败"));
            await Task.Delay(220, token);    // 等选中状态渲染，避免同帧重复判定
        }
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    static string Norm(string s) => Matcher_Priv_Norm(s);
    static string Matcher_Priv_Norm(string s) =>
        Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9\u4e00-\u9fff ]", " "), @"\s+", " ").Trim();

    static double LinesSim(List<string> a, List<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return a.Count == b.Count ? 1 : 0;
        var A = a.ToHashSet(); var B = b.ToHashSet();
        int inter = A.Count(x => B.Contains(x));
        int union = A.Count + B.Count - inter;
        return union == 0 ? 1 : (double)inter / union;
    }

    public void Dispose() { try { _cts?.Cancel(); } catch { } }
}
