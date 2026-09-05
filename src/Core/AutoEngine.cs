using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
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

    // v1.0.6 选中态以像素为准（用户指定方案）：选项框变蓝 = 已选中，文字不变。
    // 点击是否生效不靠"点过记录"猜 —— 未选中（含上次点击失败）自动重试，单选项上限 3 次。
    private readonly Dictionary<string, int> _clickAttempts = new();
    private int _pageGen;             // 页代号：翻页/客户端跳页时 +1（重置重试计数）
    private int _clicksThisPage;      // 本页我方点击次数
    private bool _answeredSeenThisPage; // 本页见过"已选中"选项（含用户手动作答）—— 页面已答完即可推进
    private bool CanAdvanceThisPage => _clicksThisPage > 0 || _answeredSeenThisPage;

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
        // v1.0.13：会话状态必须一并重置 —— 旧版漏掉后，上一次运行攒的"连续 3 次失败"
        // 记录会带进新会话，重启后同样选项直接被"放弃"跳过（实锤 bug）。
        _clickAttempts.Clear();
        _pageGen = 0; _clicksThisPage = 0; _answeredSeenThisPage = false;
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
            // v1.0.7：本页已答完（同上）且连续稳定数轮 —— 推进（翻页/提交）
            if (CanAdvanceThisPage && UnchangedRounds >= StaleJumpThreshold)
            {
                bool ended = await SweepDrainAsync(hwnd, label, minConf, token, "本页已作答且稳定 —— 推进（翻页/提交）");
                _lastLines = null;   // SweepDrain 后页面已变，下轮重建基线
                return ended ? RoundOutcome.End : RoundOutcome.Ok;
            }
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
            _pageGen++; _clicksThisPage = 0; _answeredSeenThisPage = false;   // 客户端自行跳页：新页代号，允许新页同文本选项
            bool ended = await SweepDrainAsync(hwnd, label, minConf, token, "进入整页补答（页面稳定后突变）");
            return ended ? RoundOutcome.End : RoundOutcome.Ok;
        }

        // 3) 匹配 + 点击
        var ansTexts = BankService.AnswerTexts();
        if (ansTexts.Count == 0) return RoundOutcome.EmptyBank;

        // v1.0.12：先剔除已选中（框变蓝）的行再匹配 —— 否则最佳候选选中后 DecideClick
        // 仍返回它自己，同屏其余未答题永远轮不到（用户实测"一屏只点一次"）。
        var unsel = lines.Where(l => !IsOptionSelected(bmp, l)).ToList();
        if (unsel.Count < lines.Count) _answeredSeenThisPage = true;
        var hit = Matcher.DecideClick(unsel, ansTexts, minConf);
        string? skip = null;
        string? aKey = null;
        if (hit != null)
        {
            aKey = _pageGen + "|" + Norm(hit.Line.Text);
            if (_clickAttempts.TryGetValue(aKey, out var att) && att >= 3)
            {
                skip = $"“{Trunc(hit.Line.Text, 40)}” 连续 {att} 次点击未选中 —— 放弃（防死循环）";
                hit = null; aKey = null;
            }
        }
        if (hit == null)
        {
            NotFound++;
            Emit(skip ?? $"未找到待作答选项（本屏已选中 {lines.Count - unsel.Count}/{lines.Count} 行，题库 {ansTexts.Count} 个候选答案）");
            // v1.0.7：本页已答完（我方点过，或见过已选中选项——用户手动作答的场景）→ 推进。
            // 旧条件 _clicksThisPage>0 误伤"启动时题目已全部答完"的场景：工具一题未点 →
            // 安全阀拒绝推进 → 永远走不到提交（用户实测 v1.0.6"提交按不中"的真正原因）。
            // 题库完全失配的页面两者皆否 → 仍不推进，防跳过未答题。
            if (CanAdvanceThisPage)
            {
                bool ended = await SweepDrainAsync(hwnd, label, minConf, token, "本页作答完毕 —— 推进（翻页/提交）");
                return ended ? RoundOutcome.End : RoundOutcome.Ok;
            }
            // v2.1.24：无可点项时兜底检测"提交"——但仅在**没有"下一页"**时（提交与下一页互斥，
            // 有下一页=还有题要答，不能提前提交；无下一页=最后一页才点提交）
            if (!lines.Any(IsNextPageLine))
            {
                var sub = lines.FirstOrDefault(IsSubmitLine);
                if (sub != null)
                {
                    // v1.0.10：定位到"提交"二字本身（聚合行中心会落在页码附近）
                    if (!LocateSubText(sub, "提交", out int sx, out int sy))
                    { sx = sub.X + sub.W / 2; sy = sub.Y + sub.H / 2; }
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
        if (ok)
        {
            _clickAttempts[aKey!] = (_clickAttempts.TryGetValue(aKey!, out var n2) ? n2 : 0) + 1;
            _clicksThisPage++;
        }
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
        return Regex.IsMatch(joined, @"(得分|成绩|正确率)\s*[:：]?\s*[0-9０-９]|(提交成功|练习完成|试卷完成|作答完成|已交卷|本卷完成|重新测试|查看回顾|finished|submitted|completed)", RegexOptions.IgnoreCase);
    }

    /// <summary>OCR 行是否为"下一页"按钮（翻页题型标志）；去空格以容忍 OCR 断字。</summary>
    private static bool IsNextPageLine(OcrLine l) =>
        l.Text.Replace(" ", "").Replace("\u3000", "").Contains("下一页");

    /// <summary>行内子文本定位（v1.0.10）：底栏聚合行形如"上一页 2/4 下一页"，行中心会落在
    /// 页码附近（用户实测光标停在页码右边、页面不翻）。按字符比例定位目标子串的横向区间，
    /// 点击子串中心 —— 中文近似等宽，误差几 px，足够命中按钮文字。</summary>
    private static bool LocateSubText(OcrLine line, string target, out int cx, out int cy)
    {
        cx = cy = 0;
        int idx = line.Text.IndexOf(target, StringComparison.Ordinal);
        if (idx < 0) return false;
        double unit = (double)line.W / Math.Max(1, line.Text.Length);
        int sx = line.X + (int)Math.Round(idx * unit);
        int sw = Math.Max(1, (int)Math.Round(target.Length * unit));
        cx = sx + sw / 2;
        cy = line.Y + line.H / 2;
        return true;
    }

    /// <summary>选项是否已选中（用户指定方案）：选中 = 选项框变蓝。
    /// 检测：选项行条带内统计"高饱和蓝横向长段"总长 —— 选中框描边是整条长段，
    /// 文字反锯齿只是 1-3px 碎段（实测未选 0，已选 1419/4116）。
    /// v1.0.9 修正条带几何：描边可能距文字行 ±(2~3) 个行高（选项框比文字高得多，
    /// 旧条带 ±1/2 行高会整体落在框内部 → 已选也判 0，实机实测踩坑）。
    /// 现取 y ∈ [行-2行高, 行+3行高]。注意 BGRA 内存序：buf[i]=B、buf[i+2]=R，判蓝用 B-R。
    /// v1.0.13：MinRun 30→80 —— 实测标题栏/题头/答题卡区域的圆形蓝色徽章行程 31-45px，
    /// 在 MinRun30 下误判"已选中"（非选项行被剔除 + answeredSeen 误置位 → 未答页可能被
    /// 提前推进）；真选中框描边行程 1000+px，80 完美分离（实测误报源全部归零、真选中无损）。</summary>
    private static bool IsOptionSelected(Bitmap page, OcrLine line)
    {
        int w = page.Width, h = page.Height;
        int x0 = Math.Max(0, line.X - 15), x1 = Math.Min(w, line.X + line.W * 2);
        int y0 = Math.Max(0, line.Y - line.H * 2), y1 = Math.Min(h, line.Y + line.H * 3);
        if (x1 - x0 < 10 || y1 - y0 < 10) return false;
        using var band = page.Clone(new Rectangle(x0, y0, x1 - x0, y1 - y0), PixelFormat.Format32bppArgb);
        var bd = band.LockBits(new Rectangle(0, 0, band.Width, band.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = bd.Stride, bh = band.Height, bw = band.Width;
        var buf = new byte[stride * bh];
        Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
        band.UnlockBits(bd);
        const int MinRun = 80, Threshold = 150;
        int total = 0;
        for (int y = 0; y < bh && total < Threshold; y++)
        {
            int row = y * stride, run = 0;
            for (int x = 0; x < bw; x++)
            {
                int i = row + x * 4;
                if (buf[i] - buf[i + 2] > 60 && buf[i] > 180 && buf[i + 2] < 160) run++;
                else { if (run >= MinRun) total += run; run = 0; }
            }
            if (run >= MinRun) total += run;
        }
        return total >= Threshold;
    }

    // ---- v1.0.2 蓝色主操作按钮像素定位（下一页/提交兜底） ----
    // 根因（2026-09-04 用户截图 + 本地 Tesseract 实测）：整页 psm3 对底栏 0 词命中——
    // 浅蓝底上的"下一页/提交"胶囊按钮（描边+蓝字）无法被整页 OCR 读出（描边圆框
    // 破坏字符分割；裁掉描边+psm7 单行模式才稳定读出"下/页"）。
    // 因此翻页不能依赖整页 OCR 认出按钮文字，改为直接找按钮本身：
    // 按钮是底栏右侧唯一的高饱和蓝胶囊（文字/描边实测 RGB≈(77,113,255)，b-r≈178；
    // 背景浅蓝 b-r≈24、选中选项浅蓝填充 b-r≈25、"上一页"灰色、进度条居左 47% —— 全不冲突）。

    /// <summary>底栏右侧蓝色主操作按钮像素定位：底部 8% 条带收集高饱和蓝像素，
    /// 按列聚类取最靠右的簇；要求簇中心在窗宽 78% 以外（"上一页"居 73% 被排除，
    /// "下一页/提交"居 84%）、宽度 2%~25% 窗宽、高度 <10% 窗高。返回窗口相对坐标。</summary>
    private static Rectangle? FindActionPill(Bitmap page)
    {
        int w = page.Width, h = page.Height;
        int y0 = (int)(h * 0.92), x0 = (int)(w * 0.65);
        if (h - y0 < 8 || w - x0 < 8) return null;
        using var strip = page.Clone(new Rectangle(x0, y0, w - x0, h - y0), PixelFormat.Format32bppArgb);
        var bd = strip.LockBits(new Rectangle(0, 0, strip.Width, strip.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = bd.Stride, bw = strip.Width, bh = strip.Height;
        var buf = new byte[stride * bh];
        Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
        strip.UnlockBits(bd);

        var colCnt = new int[bw]; var colTop = new int[bw]; var colBot = new int[bw];
        for (int y = 0; y < bh; y++)
        {
            int row = y * stride;
            for (int x = 0; x < bw; x++)
            {
                int i = row + x * 4;
                int b = buf[i], r = buf[i + 2];
                if (b - r > 60 && b > 180 && r < 160)
                {
                    if (colCnt[x] == 0) colTop[x] = y;
                    colBot[x] = y; colCnt[x]++;
                }
            }
        }

        Rectangle? best = null; int bestCx = -1;
        int cs = -1, gap = Math.Max(6, w / 125);
        for (int x = 0; x <= bw; x++)
        {
            bool on = x < bw && colCnt[x] > 0;
            if (on && cs < 0) cs = x;
            if (on || cs < 0) continue;
            int ce = x - 1, cw = ce - cs + 1, cnt = 0, cyT = int.MaxValue, cyB = -1;
            for (int x2 = cs; x2 <= ce; x2++) { cnt += colCnt[x2]; cyT = Math.Min(cyT, colTop[x2]); cyB = Math.Max(cyB, colBot[x2]); }
            int absCx = x0 + cs + cw / 2;
            if (cnt >= 120 && cw >= w * 0.02 && cw <= w * 0.25 && absCx >= w * 0.78 && cyB - cyT < h * 0.10 && absCx > bestCx)
            {
                bestCx = absCx;
                best = new Rectangle(x0 + cs, y0 + cyT, cw, cyB - cyT + 1);
            }
            cs = -1;
        }
        return best;
    }

    /// <summary>按钮内文字验证：胶囊外框收缩到文字区（去描边——描边圆框是字符分割杀手，
    /// 实测带描边 psm7 全空、去描边后"下/页"稳定可读），按内区蓝占比自动判定样式：
    /// 描边样式（"下一页"，蓝<35%）蓝字→黑；实心样式（"提交"，蓝≈90%）蓝底→白、白字→黑。
    /// 3x 放大，psm7 单行 OCR。语言用纯 chi_sim —— chi_sim+eng 会把"提交"读成纯英文
    /// "FESS"（实测），归一化后为空导致验证永远失败。返回归一化后的中文字符串。</summary>
    private static async Task<string> PillCjkTextAsync(Bitmap page, Rectangle r)
    {
        int ix = r.X + (int)(r.Width * 0.12), iw = (int)(r.Width * 0.76);
        int iy = r.Y + (int)(r.Height * 0.28), ih = (int)(r.Height * 0.50);
        if (iw < 12 || ih < 8) return "";
        var inner = Rectangle.Intersect(new Rectangle(0, 0, page.Width, page.Height), new Rectangle(ix, iy, iw, ih));
        if (inner.Width < 12 || inner.Height < 8) return "";
        using var crop = page.Clone(inner, PixelFormat.Format32bppArgb);
        var bd = crop.LockBits(new Rectangle(0, 0, crop.Width, crop.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = bd.Stride, bh = crop.Height, bw = crop.Width;
        var buf = new byte[stride * bh];
        Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
        // 先统计内区蓝像素占比判定按钮样式（"下一页"=描边+蓝字；"提交"=实心蓝底+白字，
        // 实测内区蓝占比 90%）。注意内存序是 BGRA：buf[i]=B、buf[i+2]=R，判蓝必须 B-R！
        // （v1.0.3 曾误写 R-B，蓝占比恒 0 → 按钮"验证未通过"永不点击——实机无点击的元凶。）
        int total = bw * bh, blueCnt = 0;
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
                if (buf[y * stride + x * 4] - buf[y * stride + x * 4 + 2] > 60) blueCnt++;
        bool filled = blueCnt > total * 0.35;
        // 描边样式：蓝字→黑（描边已收缩裁掉）；实心样式：反转（蓝底→白、白字→黑）
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                int i = y * stride + x * 4;
                bool blue = buf[i] - buf[i + 2] > 60;
                byte v = filled ? (blue ? (byte)255 : (byte)0) : (blue ? (byte)0 : (byte)255);
                buf[i] = buf[i + 1] = buf[i + 2] = v;
            }
        Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
        crop.UnlockBits(bd);
        using var big = new Bitmap(crop.Width * 3, crop.Height * 3);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(crop, 0, 0, big.Width, big.Height);
        }
        var lines = await OcrService.RecognizeAsync(big, 7, "chi_sim");
        var sb = new StringBuilder();
        foreach (var c in string.Concat(lines.Select(l => l.Text)))
            if (c >= 0x4E00 && c <= 0x9FFF) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>蓝色胶囊兜底点击：定位 → 文字验证（须含 下/页/提）→ 点击。返回是否已点击。</summary>
    private async Task<bool> TryClickActionPillAsync(IntPtr hwnd, CancellationToken token)
    {
        using var page = WinApi.CaptureWindow(hwnd, out _);
        if (page == null) return false;
        var pill = FindActionPill(page);
        if (pill == null) return false;
        string cjk = await PillCjkTextAsync(page, pill.Value);
        if (!(cjk.Contains("下") || cjk.Contains("提") || cjk.Contains("页") || cjk.Contains("交")))
        {
            Emit($"底栏蓝色按钮文字验证未通过（OCR 得到 “{cjk}”），不点击");
            return false;
        }
        int cx = pill.Value.X + pill.Value.Width / 2, cy = pill.Value.Y + pill.Value.Height / 2;
        bool sent = WinApi.ClickWindow(hwnd, cx, cy);
        Emit($"蓝色主操作按钮像素定位（下一页/提交）—— 点击 @(窗口内 {cx},{cy}) 验证文字“{cjk}”" + (sent ? "" : "（投递失败）"));
        return sent;
    }

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
            // v1.0.10：定位到"提交"二字本身（底栏聚合行的行中心同样会落在页码附近）
            if (!LocateSubText(sub, "提交", out int sx, out int sy))
            { sx = sub.X + sub.W / 2; sy = sub.Y + sub.H / 2; }
            bool sent = WinApi.ClickWindow(hwnd, sx, sy);
            Emit($"检测到“提交”按钮（当前页已作答完全）—— 点击 @(窗口内 {sx},{sy})" + (sent ? "" : "（投递失败）"));
            await Task.Delay(900, token);   // 等提交/成绩渲染
            var (lines2, _) = await OcrAsync();
            if (IsEndPage(lines2)) { Emit("提交后检测到成绩/得分页 —— 自动回答完成 ✅"); return true; }
            return false;   // 仍在作答页（可能需确认弹窗），交回主循环/下一轮处理
        }

        for (int page = 1; page <= MaxPageTurns && !token.IsCancellationRequested; page++)
        {
            // ① 答当前屏（含刚翻过来的新页首屏）—— v1.0.11：点击未确认生效时不下滑
            if (!await AnswerScreenAsync(hwnd, minConf, token, seen))
            {
                Emit("存在未确认生效的点击 —— 不执行下滑（原地等待重试）");
                return false;
            }

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
                // v1.0.11：新屏答题同样要确认点击生效，未确认不再继续下滚
                if (!await AnswerScreenAsync(hwnd, minConf, token, seen))
                {
                    Emit("存在未确认生效的点击 —— 不再继续下滑");
                    return false;
                }
            }
            if (token.IsCancellationRequested) break;

            // ③ 翻页/提交（v1.0.10 重排：像素定位优先）——
            //    用户实测（19:06）：OCR 偶尔读出底栏整行"上一页 2/4 下一页"时，旧行中心点击
            //    恰好落在"页码右边"（光标停在页码右侧、页面不翻）——整行中心不可信。
            //    因此一律先走蓝色胶囊像素定位（不依赖该行文字），OCR 行仅作兜底且定位到
            //    "下一页"三字本身（LocateSubText）。
            if (await TryClickActionPillAsync(hwnd, token))
            {
                await Task.Delay(1000, token);   // 等翻页渲染
                var (_, pillAdded) = await OcrAsync();
                if (pillAdded > 0) { _pageGen++; _clicksThisPage = 0; _answeredSeenThisPage = false; continue; }   // 翻页成功 → 新页代号，对新页重复 ①②
                Emit("蓝色按钮点击后页面无变化，补答终止");
                return false;
            }
            var np = lastLines.FirstOrDefault(IsNextPageLine);
            if (np == null)
            {
                // 最后一页：检测"提交"并点击（当前页已作答完全）
                if (await SubmitIfShownAsync(lastLines)) return true;
                // 诊断：既无"下一页"也无"提交"也无蓝色按钮 —— 倒出 OCR 实际看到的行
                Emit($"未检测到“下一页/提交/蓝色按钮”。当前屏 OCR 共 {lastLines.Count} 行：" +
                     string.Join(" | ", lastLines.Take(12).Select(l => Trunc(l.Text, 24))));
                break;   // 无下一页且无提交（或提交后仍在作答页）：补答完成，交回主循环
            }
            // 兜底：OCR 行命中时定位到"下一页"三字本身（行中心会落在页码附近）
            if (!LocateSubText(np, "下一页", out int nx, out int ny))
            { nx = np.X + np.W / 2; ny = np.Y + np.H / 2; }
            bool sent = WinApi.ClickWindow(hwnd, nx, ny);
            Emit($"检测到“下一页”（翻页题型）—— 点击 @(窗口内 {nx},{ny})" + (sent ? "" : "（投递失败）"));
            await Task.Delay(700, token);   // 等翻页渲染
            var (_, afterAdded) = await OcrAsync();   // 新页首屏并入 seen（下一轮滚动比较的基线）
            if (afterAdded == 0)
            {
                Emit("点击“下一页”后页面无变化（疑似 PostMessage 点击对自绘控件无效），补答终止");
                break;
            }
            _pageGen++; _clicksThisPage = 0; _answeredSeenThisPage = false;   // v1.0.5：翻页成功 → 新页代号
        }

        Emit("整页补答完成，回到常规轮询");
        return false;
    }

    /// <summary>
    /// 对当前屏反复"OCR→匹配→点击→确认"直到没有新的可点项（一屏多题时逐个处理）。
    /// v1.0.11（用户要求）：点击必须确认生效（选项框变蓝）才允许执行下滑 ——
    /// 确认失败不下滑、原地等待重试（单选项 ≤3 次，超限放弃放行防死循环）。
    /// 返回 true = 本屏处理完成（无可点项/全部确认选中/放弃）；false = 存在未确认点击，调用方不得下滑。
    /// </summary>
    private async Task<bool> AnswerScreenAsync(IntPtr hwnd, ConfLevel minConf, CancellationToken token, HashSet<string> seen)
    {
        for (int i = 0; i < MaxClicksPerScreen && !token.IsCancellationRequested; i++)
        {
            using var bmp = WinApi.CaptureWindow(hwnd, out _);
            if (bmp == null) return true;
            var lines = await OcrService.RecognizeAsync(bmp);
            foreach (var l in lines) seen.Add(Norm(l.Text));
            // v1.0.12：先剔除已选中（框变蓝）行再匹配 —— 同屏多题逐个作答；
            // （若最佳候选选中后不剔除，DecideClick 仍返回它 → 一屏永远只点一次）
            var unsel = lines.Where(l => !IsOptionSelected(bmp, l)).ToList();
            if (unsel.Count < lines.Count) _answeredSeenThisPage = true;
            var hit = Matcher.DecideClick(unsel, BankService.AnswerTexts(), minConf);
            if (hit == null) return true;   // 无可点项（全部已选中 / 题库无匹配）
            string aKey = _pageGen + "|" + Norm(hit.Line.Text);
            if (_clickAttempts.TryGetValue(aKey, out var att) && att >= 3)
            {
                Emit($"“{Trunc(hit.Line.Text, 40)}” 连续 {att} 次点击未选中 —— 放弃该选项（防死循环）");
                return true;   // 放弃：放行后续下滑（已尽 3 次确认义务）
            }
            int cx = hit.Line.X + hit.Line.W / 2, cy = hit.Line.Y + hit.Line.H / 2;
            bool ok = WinApi.ClickWindow(hwnd, cx, cy);
            Round++; Clicked++;
            Emit($"补答: 点击 “{Trunc(hit.Line.Text, 40)}” @(窗口内 {cx},{cy}) 来源={hit.Via} 置信度={hit.Conf}" + (ok ? "" : " 点击失败"));
            if (ok)
            {
                _clickAttempts[aKey] = att + 1;
                _clicksThisPage++;
            }
            // v1.0.11（用户要求）：必须确认点击生效（框变蓝）才返回 true 放行下滑
            // v1.0.13：带上原行 y —— 听力题型不同题的选项文本常重复，纯文本匹配可能
            // 确认到别题的同名行（未选）→ 假"未确认"；优先就近匹配。
            bool confirmed = await ConfirmSelectedAsync(hwnd, hit.Line.Text, hit.Line.Y, token);
            if (!confirmed)
            {
                Emit($"“{Trunc(hit.Line.Text, 40)}” 点击后未确认选中 —— 不执行下滑");
                return false;
            }
            Emit($"“{Trunc(hit.Line.Text, 40)}” 已确认选中 ✓");
        }
        return true;
    }

    /// <summary>轮询确认某选项已变为选中态（框变蓝）：最多 6 次 × 400ms。
    /// v1.0.13：优先匹配"同文本且就在原位置附近"的行（|Δy|≤100，容忍滚动渲染抖动），
    /// 避免确认到别题的同名选项行；无就近命中才退化为任意同文本行。</summary>
    private async Task<bool> ConfirmSelectedAsync(IntPtr hwnd, string text, int origY, CancellationToken token)
    {
        string want = Norm(text), plain = text.Replace(" ", "");
        if (want.Length == 0) return false;
        for (int k = 0; k < 6; k++)
        {
            await Task.Delay(400, token);
            using var b = WinApi.CaptureWindow(hwnd, out _);
            if (b == null) return false;
            var ls = await OcrService.RecognizeAsync(b);
            var near = ls.Where(l => Norm(l.Text) == want && Math.Abs(l.Y - origY) <= 100)
                         .OrderBy(l => Math.Abs(l.Y - origY)).FirstOrDefault();
            var line = near
                    ?? ls.FirstOrDefault(l => Norm(l.Text) == want)
                    ?? ls.FirstOrDefault(l => l.Text.Replace(" ", "").Contains(plain));
            if (line != null && IsOptionSelected(b, line)) return true;
        }
        return false;
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
