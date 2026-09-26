using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxwExtract.Core;

/// <summary>
/// DOM 自动答题引擎（v1.2.0 新增）：通过 CDP（Chrome DevTools Protocol）直连客户端页面，
/// 直接读取 DOM 节点判题作答 —— 不依赖屏幕像素/OCR，不受客户端版式、字号、配色、渲染影响。
/// 客户端必须由本工具以调试开关启动（--remote-debugging-port-u3=9222，客户端源码级改名开关，
/// 见 _research/DOM可行性研究结论.md）。OCR 视觉引擎（AutoEngine）保留为降级方案，两者互不依赖。
/// 页面 DOM 约定（选项/选中态/填空/翻页）为对客户端页面结构的事实描述，实现代码全部自研。
/// </summary>
public sealed class DomEngine
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private CdpClient? _cdp;
    private string? _targetLabel;

    // 选择器多级降级（研究结论要求）：题干/选项/填空各备选依次尝试
    // v1.3.6：加入课程包诊断测试组件（U3CompoLib，u3-qst-opt 系，见 _research/dump 实测 DOM）
    private const string OptSel =
        ".u3-option__content.is-text, .u3-option__content--default, .u3-option-img, .u3-qst-opt";
    private const string QtSel =
        ".u3-question-text, .u3-question-stem, .u3-stem, [class*=\"question-stem\"], .u3-choice__question--text, .u3-fillblank-base__cont, .u3-qst-text-cont";
    private const string NoSel =
        ".u3-question-no, .u3-question__no, [class*=\"question-no\"], .u3-input__prepared, .u3-input__prepead, .u3-qst-num";
    private const string InputSel =
        ".u3-input__content--input input, .u3-input__content input, .u3-input__content--input, input[type=\"text\"], textarea.u3-translate__textarea";

    private const int MaxAttempts = 8;        // 单选项点击未确认选中的重试上限（与 OCR 引擎一致）
    private const int MaxNoTargetRounds = 15; // 调试端口连续不可达多少轮后判停
    private const int IntervalFloorMs = 800;  // DOM 读取一轮毫秒级完成，轮询下限远低于 OCR 的 5s

    private readonly Dictionary<string, int> _clickAttempts = new();
    private string? _pkLastQ;      // PK 防卡死：连续多轮同一题未推进则告警
    private int _pkSameCount;
    private int _emptyRounds;      // v1.4.1：连续无题轮数（≥3 强制重挑 target，PK 活动开新页）

    // v1.5.x：就绪/稳定门控（settle）—— 页面结构连续 ≥2 轮一致才作答，
    // 避免「客户端尚未进入可作答状态 / 翻页加载过渡中」就抢答（反作弊：作答时机须与题面呈现一致）
    private string? _settleSig;
    private int _settleStable;

    // v1.5.x：长页防漏答（P1 回归加固）—— 视口下方未答题的「滚动呈现」有界重试。
    // 仅在翻页（swiper 索引变化）时复位，使单页内最多尝试 2 次滚动后必 fallthrough 推进，绝不卡死。
    private int _revealTries;
    private bool _revealWarned;
    private int _lastSwiperIndex = -999;
    private const int RevealMaxTries = 2;

    // v1.6.6：「无进展」守卫 —— swiper 索引在 flipbook 上恒为 -1，翻页检测不能只靠它。
    // PageSig = 活动页文本指纹（页内一致的稳定标识），它变化即视为真的翻页了 → 复位守卫。
    // 连续 StallRoundsBeforeForce 轮既无答复、也无推进（按钮点不到/页面不动）→ 强制再试一次并告警。
    private string _lastPageSig = "";
    private int _stallRounds;
    private const int StallRoundsBeforeForce = 5;

    // ---- 帧上下文（v1.3.3）：题目可能在跨域 iframe / shadow DOM 里，仅探主文档会漏 ----
    // 每轮对页面全部 frame（Page.getFrameTree，跨域含内）建隔离世界后逐帧探测；
    // 「工作帧」= 探测到题目内容最多的帧，后续点击/填空/翻页全部定向到该上下文。
    private sealed class FrameCtx { public string Id = ""; public string Url = ""; public long Ctx; }
    private sealed class FrameProbe { public FrameCtx F = new(); public DomSnapshot S = new(); }
    private readonly Dictionary<string, long> _worlds = new();   // frameId → isolated world executionContextId
    private FrameCtx? _work;                                     // 当前工作帧

    private static int FrameScore(DomSnapshot s) =>
        (s.HasPk ? 1000 : 0)
        + s.Questions.Sum(q => Math.Max(q.Opts.Count, q.Inputs.Count)) * 10
        + (s.Questions.Count > 0 ? 5 : 0);

    /// <summary>枚举页面全部 frame 并确保各有一个隔离世界（复用缓存；rebuild=true 强制重建）。</summary>
    // 帧级探测失败原因（只报一次，避免刷屏）；帧级链路禁用后永久走主文档
    private string _frameFailReason = "";
    private bool _frameFailLogged;
    private bool _framesDisabled;

    private async Task<List<FrameCtx>> EnsureFramesAsync(CdpClient cdp, bool rebuild = false)
    {
        List<CdpClient.FrameInfo> frames;
        try { frames = await cdp.GetFramesAsync(); }
        catch (Exception e)
        {
            if (_frameFailReason.Length == 0) _frameFailReason = "getFrameTree失败: " + Trunc(e.Message, 80);
            return new List<FrameCtx>();
        }
        var keep = frames.Select(f => f.Id).ToHashSet();
        foreach (var k in _worlds.Keys.Where(k => !keep.Contains(k)).ToList()) _worlds.Remove(k);

        var list = new List<FrameCtx>();
        foreach (var f in frames)
        {
            long ctx = -1;
            if (!rebuild && _worlds.TryGetValue(f.Id, out var cached) && cached > 0) ctx = cached;
            if (ctx <= 0)
            {
                try { ctx = await cdp.CreateIsolatedWorldAsync(f.Id); }
                catch (Exception e)
                {
                    if (_frameFailReason.Length == 0) _frameFailReason = "createIsolatedWorld失败(" + Trunc(f.Url, 40) + "): " + Trunc(e.Message, 80);
                    continue;
                }
                if (ctx <= 0) continue;
                _worlds[f.Id] = ctx;
            }
            list.Add(new FrameCtx { Id = f.Id, Url = f.Url, Ctx = ctx });
        }
        return list;
    }

    /// <summary>全帧探测：逐帧执行探针脚本，返回有快照的帧列表。
    /// v1.3.4：帧级链路（getFrameTree/隔离世界/contextId 求值）任一环失败即**永久降级**为主文档直连
    /// （与 v1.3.2 行为一致；坏 contextId 可能导致 CEF 直接断开 WebSocket，故不重试帧级消息），
    /// 失败原因写日志（只报一次）。</summary>
    private async Task<List<FrameProbe>> ProbeAllFramesAsync(CdpClient cdp)
    {
        var res = new List<FrameProbe>();
        if (!_framesDisabled)
        {
            var frames = await EnsureFramesAsync(cdp);
            if (frames.Count == 0)
            {
                // getFrameTree 失败或隔离世界全部创建失败 —— 永久降级
                if (!_frameFailLogged)
                {
                    _frameFailLogged = true; _framesDisabled = true;
                    Emit($"帧级探测不可用（{Trunc(_frameFailReason, 100)}）—— 已降级为主文档探测");
                }
            }
            else
            {
                bool evalFail = false;
                string evalFailMsg = "";
                foreach (var f in frames)
                {
                    string json;
                    try { json = await cdp.EvaluateStringAsync(ProbeScript(), 10000, f.Ctx); }
                    catch (Exception e)
                    {
                        _worlds[f.Id] = -1; evalFail = true;
                        if (evalFailMsg.Length == 0) evalFailMsg = Trunc(e.Message, 90);
                        continue;   // 上下文已随导航销毁
                    }
                    var s = ParseSnapshot(json);
                    if (s == null) continue;
                    res.Add(new FrameProbe { F = f, S = s });
                }
                if (res.Count > 0) return res;
                if (evalFail && !_frameFailLogged)
                {
                    _frameFailLogged = true; _framesDisabled = true;
                    Emit($"帧级求值异常（{Trunc(evalFailMsg, 100)}）—— 已降级为主文档探测");
                }
                else if (!evalFail && !_frameFailLogged && _frameFailReason.Length > 0)
                {
                    _frameFailLogged = true; _framesDisabled = true;
                    Emit($"帧级探测无结果（{Trunc(_frameFailReason, 100)}）—— 已降级为主文档探测");
                }
            }
        }

        // 降级 / 回退：主文档直连（无 contextId，与 v1.3.2 行为一致）
        try
        {
            string json = await cdp.EvaluateStringAsync(ProbeScript(), 10000);
            var s = ParseSnapshot(json);
            if (s != null)
                res.Add(new FrameProbe { F = new FrameCtx { Id = "main", Url = "(主文档)", Ctx = -1 }, S = s });
        }
        catch { }
        return res;
    }

    public bool Running => _cts != null && !_cts.IsCancellationRequested;
    public int Round { get; private set; }
    public int Clicked { get; private set; }
    public int NotFound { get; private set; }
    public int UnchangedRounds { get; private set; }
    public int PagesTurned { get; private set; }
    public DateTime StartedAt { get; private set; }

    public event Action<string>? Log;
    public event Action<List<OcrLine>, string>? Preview;   // 复用前端预览通道展示 DOM 快照
    public event Action<StopReason, string>? Stopped;

    private void Emit(string msg)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg;
        Log?.Invoke(line);
    }

    /// <summary>启动。port 为客户端调试端口（9222）；minConf/intervalMs 语义与 OCR 引擎一致。</summary>
    public void Start(int port, ConfLevel minConf, int intervalMs)
    {
        if (Running) return;
        if (intervalMs < IntervalFloorMs) intervalMs = IntervalFloorMs;
        _cts = new CancellationTokenSource();
        Round = 0; Clicked = 0; NotFound = 0; UnchangedRounds = 0; PagesTurned = 0;
        _settleSig = null; _settleStable = 0;
        _revealTries = 0; _revealWarned = false; _lastSwiperIndex = -999;
        _lastPageSig = ""; _stallRounds = 0;
        _clickAttempts.Clear();
        _cdp?.Dispose(); _cdp = null; _targetLabel = null;
        StartedAt = DateTime.Now;
        Emit($"DOM 引擎启动（CDP 端口 {port}，间隔 {intervalMs}ms，最低置信度={minConf.Name()}）");
        _loop = Task.Run(() => LoopAsync(port, minConf, intervalMs, _cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        Stopped?.Invoke(StopReason.Manual, "已请求停止");
        Emit("正在停止…");
    }

    private async Task LoopAsync(int port, ConfLevel minConf, int intervalMs, CancellationToken token)
    {
        int noTargetRounds = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var outcome = await DoRoundAsync(port, minConf, token);
                    if (outcome == RoundOutcome.End)
                    {
                        Stopped?.Invoke(StopReason.EndDetected,
                            $"检测到成绩/得分页，完成 ✅（共 {Round} 轮，点击 {Clicked} 次，翻页 {PagesTurned} 页）");
                        _cts?.Cancel();
                        return;
                    }
                    if (outcome == RoundOutcome.NoTarget)
                    {
                        noTargetRounds++;
                        if (noTargetRounds == 1)
                            Emit($"未发现客户端调试端口（{port}）—— 请先【完全退出客户端】，再用本工具「启动客户端」按钮（DOM 模式）重新启动");
                        if (noTargetRounds >= MaxNoTargetRounds)
                        {
                            Stopped?.Invoke(StopReason.NoWindow,
                                $"连续 {noTargetRounds} 轮无法连接客户端调试端口 —— 请用 DOM 模式重启客户端后重试");
                            _cts?.Cancel();
                            return;
                        }
                    }
                    else noTargetRounds = 0;
                }
                catch (Exception ex)
                {
                    // 连接级异常：丢弃当前 CDP 连接，下一轮重新发现/附加
                    _cdp?.Dispose(); _cdp = null;
                    Emit("本轮异常（将自动重连）: " + ex.Message);
                }

                int wait = Math.Max(200, intervalMs - (int)sw.ElapsedMilliseconds);
                await Task.Delay(wait, token).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { Stopped?.Invoke(StopReason.Error, ex.Message); }
    }

    private enum RoundOutcome { Ok, End, NoTarget }

    // ---------------- 每轮主流程 ----------------

    private async Task<RoundOutcome> DoRoundAsync(int port, ConfLevel minConf, CancellationToken token)
    {
        // v1.4.1：连续 3 轮无题则强制重挑目标页（PK 等活动会开新 target，已附页面探不到）
        if (!await EnsureAttachedAsync(port, force: _emptyRounds >= 3)) return RoundOutcome.NoTarget;
        var cdp = _cdp!;

        // 1) 全帧快照（题干/选项/选中态/填空/全文；跨域 iframe、shadow DOM 一并覆盖）
        var probes = await ProbeAllFramesAsync(cdp);
        if (probes.Count == 0) { Emit("页面快照解析失败（下一轮重试）"); return RoundOutcome.Ok; }

        // 2) 终止判定：成绩/得分页（全帧拼接全文，签名与 OCR 引擎同一套词表）
        string allText = string.Join("\n", probes.Select(p => p.S.FullText));
        if (IsEndPage(allText, out var endSig))
        {
            Emit($"检测到成绩/得分页（签名“{Trunc(endSig, 24)}”）—— 自动回答完成 ✅");
            return RoundOutcome.End;
        }

        // 3) 工作帧：题目内容最多的帧；其快照作为本轮快照
        var best = probes.OrderByDescending(p => FrameScore(p.S)).First();
        _work = best.F;
        var snap = best.S;

        // 翻页（swiper 索引变化）→ 复位「滚动呈现」重试计数，避免跨页误判残留（单页内不复位以保上界）
        // v1.6.6：同时复位「无进展」守卫。swiper 索引在很多 flipbook 上恒为 -1（无 Swiper 实例 /
        // 非标准容器），只靠索引复位会让守卫永不归零 → 推进被永久拦住。
        if ((snap.SwiperIndex >= 0 && snap.SwiperIndex != _lastSwiperIndex)
            || snap.PageSig != _lastPageSig)
        {
            _lastSwiperIndex = snap.SwiperIndex;
            _lastPageSig = snap.PageSig;
            _revealTries = 0; _revealWarned = false;
            _stallRounds = 0;
        }

        // 4) DOM 预览（前端「最近识别到的屏幕文字」卡片；标注来源帧）
        string workTag = "";
        try { var u = _work.Url; workTag = u.Length > 46 ? u[^46..] : u; } catch { }
        Preview?.Invoke(snap.PreviewLines(), $"CDP: {Trunc(_targetLabel ?? "", 30)}{(workTag.Length > 0 ? " · " + workTag : "")}");

        Round++;

        // 5) 全帧无题：输出结构诊断（帮助定位页面形态），等待用户进入做题页
        if (FrameScore(snap) == 0)
        {
            _emptyRounds++;
            if (Round % 10 == 1)
            {
                Emit($"第 {Round} 轮: 尚未检测到题目内容（已扫全部帧）—— 请在客户端中打开试卷/练习（听力/跟读题型会自动播放跳转）");
                DumpFrameDiag(probes);
            }
            return RoundOutcome.Ok;
        }
        _emptyRounds = 0;

        // 6) 单词PK（v1.3.0）：拼写页优先，其次选择页 —— 词库来自「无痕下载接管」抓取的词书接口
        if (snap.PkSpell) { await DoSpellPkAsync(snap); return RoundOutcome.Ok; }
        if (snap.HasPk) { await DoChoicePkAsync(snap); return RoundOutcome.Ok; }

        // 6.5) 就绪/稳定门控（settle）：页面结构需连续 ≥2 轮一致才作答，
        // 避免「客户端尚未进入可作答状态 / 翻页加载过渡中」就抢答（反作弊：作答时机须与题面呈现一致）
        string sig = StructSig(snap);
        if (sig != _settleSig) { _settleSig = sig; _settleStable = 1; }
        else _settleStable++;
        if (_settleStable < 2)
        {
            if (Round % 4 == 1)
                Emit($"第 {Round} 轮: 等待页面进入可作答/稳定状态（{_settleStable}/2），暂不作答");
            return RoundOutcome.Ok;
        }

        // 7) 选择题：逐题匹配答案 → 批量点击未选中的命中项
        var clickItems = new List<ClickItem>();
        foreach (var q in snap.Questions)
        {
            foreach (var (text, optId, via) in DecideChoice(q, minConf))
            {
                clickItems.Add(new ClickItem { Qid = q.Qid, OptId = optId, Text = text });
                Emit($"第 {Round} 轮: 匹配到选项 “{Trunc(text, 40)}”（{via}，题 {Trunc(q.Num > 0 ? "#" + q.Num : q.Stem, 24)}）");
            }
        }
        // 可选（低风险）节奏门控：一轮至多作答 2 题，更像人工、避免机器级批量抢答；不拖慢单题页
        const int MaxAnsPerRound = 2;
        if (clickItems.Count > MaxAnsPerRound)
        {
            var capped = clickItems.GroupBy(x => x.Qid).Take(MaxAnsPerRound).SelectMany(g => g).ToList();
            Emit($"第 {Round} 轮: 本轮命中 {clickItems.Count} 项，按节奏先作答 {capped.Count} 项（余下轮次继续）");
            clickItems = capped;
        }
        if (clickItems.Count > 0)
        {
            int clicked = await ClickOptionsAsync(clickItems);
            Clicked += clicked;
            Emit((clicked > 0
                ? $"已点击 {clicked} 个选项（累计 {Clicked}）"
                : "点击未投递成功（页面可能正在切换），下一轮重试"));
            return RoundOutcome.Ok;
        }

        // 8) 填空题：题库命中 → 逐空填入 + 派发 input/change
        var fill = snap.Questions.FirstOrDefault(q => q.Inputs.Any(i => i.Length == 0));
        if (fill != null)
        {
            string? ans = DecideFill(fill, minConf);
            if (!string.IsNullOrEmpty(ans))
            {
                int n = await FillInputsAsync(ans);
                if (n > 0) { Clicked += n; Emit($"已填空 {n} 空: “{Trunc(ans, 40)}”"); }
                return RoundOutcome.Ok;
            }
            NotFound++;
            if (Round % 5 == 1)
                Emit($"第 {Round} 轮: 填空题未命中题库（题干: {Trunc(fill.Stem, 40)}）");
        }

        // 9) 长页防漏答（P1 回归加固）：本页视口下方尚有未答题 → 先滚动呈现、暂不推进，
        //    避免「答完可见题后提前翻页/提交」把折叠线以下题目漏答（长页版式；flipbook 紧凑分页通常不触发）。
        // v1.6.6 修复（翻页卡死根因）：原实现在重试用尽后仍会 `return RoundOutcome.Ok` 把当轮直接吞掉，
        // 且 `_revealTries` 只在 swiper 索引变化时复位——而 flipbook 的 swiper 索引恒为 -1，
        // 于是计数一次用尽后**永远**不再推进，翻页/提交按钮从此刻起再也点不到。
        // 现改为：重试用尽即「放行」推进（below 只统计本页，不再被他人页面的常驻题污染）。
        if (snap.OffscreenBelow > 0)
        {
            if (_revealTries < RevealMaxTries)
            {
                _revealTries++;
                bool scrolled = await RevealBelowAsync();
                Emit($"第 {Round} 轮: 本页视口下方尚有未答题目（{snap.OffscreenBelow} 道），尝试滚动呈现（第 {_revealTries}/{RevealMaxTries} 次，scrolled={scrolled}）");
                if (scrolled) return RoundOutcome.Ok;   // 滚动成功 → 下一轮重新探测（题目进入视口即可作答）
                // 滚动无效（无可滚动容器 / 已是底部）→ 直接放行推进，不浪费剩余重试
            }
            if (!_revealWarned)
            {
                _revealWarned = true;
                Emit($"第 {Round} 轮判定：本页视口下方仍有未答题目（{snap.OffscreenBelow} 道）且滚动无效 —— 放行推进以免卡死（该部分可能漏答）");
            }
            // fallthrough：不 return，继续走推进逻辑
        }

        // 9) 推进：本页无可答内容 → 翻页 / 提交
        bool hasUnanswered = snap.Questions.Any(q => q.Opts.Any(o => !o.Checked))
                          || snap.Questions.Any(q => q.Inputs.Any(i => i.Length == 0));
        // v1.6.6：若「无未答内容」但页面连续多轮毫无变化（既没点到按钮、也没翻页），
        // 说明推进链路失效（按钮不可见 / 索引不变 / 页面卡住）→ 强制走一次 TryAdvance 并告警，
        // 避免「静默空转」把用户的时间耗掉。
        if (!hasUnanswered || _stallRounds >= StallRoundsBeforeForce)
        {
            bool forced = _stallRounds >= StallRoundsBeforeForce;
            if (forced && hasUnanswered)
                Emit($"第 {Round} 轮: 页面连续 {_stallRounds} 轮无进展且仍有未答题 —— 强制尝试翻页/提交以脱困");
            bool advanced = await TryAdvanceAsync(cdp, snap);
            if (advanced) { PagesTurned++; _stallRounds = 0; }
            else
            {
                _stallRounds++;
                // 前 3 次与之后每 10 轮提示一次，避免刷屏又不至于「完全没消息」
                if (_stallRounds <= 3 || Round % 10 == 1)
                    Emit($"第 {Round} 轮: 本页无未答内容，也未见可用的“下一页/提交”按钮 —— 等待页面变化（第 {_stallRounds} 轮；听力/跟读题型会自动播放跳转）");
            }
            return RoundOutcome.Ok;
        }

        // 10) 有未答内容但未命中 —— 计数并等待
        NotFound++;
        if (Round % 5 == 1)
            Emit($"第 {Round} 轮: 未找到满足置信度（≥{minConf.Name()}）的可作答项（题库 {BankService.Count} 题）");
        return RoundOutcome.Ok;
    }

    /// <summary>把各帧的结构诊断（radio/选项类/shadow/iframe 计数 + 文本样例）打进日志，用于定位「检测不到题目」的页面形态。</summary>
    private void DumpFrameDiag(List<FrameProbe> probes)
    {
        foreach (var p in probes)
        {
            var d = p.S.Diag;
            if (d == null) continue;
            JsonElement dd = d.Value;
            string url = p.F.Url;
            if (url.Length > 56) url = "…" + url[^56..];
            if (url.Length == 0) url = "(主文档)";
            Emit($"诊断[{url}] radio={DV(dd, "radio")} checkbox={DV(dd, "cb")} *option*类={DV(dd, "optCls")} *question*类={DV(dd, "qCls")} u3选项={DV(dd, "u3opt")} 活动slide={DV(dd, "swActive")} shadow宿主={DV(dd, "shadow")} iframe={DV(dd, "iframes")}");
            string bodyCls = DV(dd, "bodyCls");
            if (bodyCls.Length > 0) Emit($"诊断[{url}] body.class: {Trunc(bodyCls, 90)}");
            string txt = DV(dd, "text");
            if (txt.Length > 0) Emit($"诊断[{url}] 文本样例: {Trunc(txt, 90)}");
        }
    }

    private static string DV(JsonElement d, string key)
    {
        try { if (d.TryGetProperty(key, out var v)) { var s = v.ToString(); return s ?? ""; } }
        catch { }
        return "";
    }

    // ---------------- 单词PK（v1.3.0） ----------------

    /// <summary>PK 同题多轮未推进的通用防护：返回 false 表示已卡死需跳过本轮作答。</summary>
    bool PkStuckGuard(string questionKey)
    {
        if (questionKey == _pkLastQ)
        {
            _pkSameCount++;
            if (_pkSameCount > 12)
            {
                if (_pkSameCount % 10 == 0)
                    Emit($"单词PK 同一题已连续 {_pkSameCount} 轮未推进（可能未命中词库或点击未生效），暂停该题作答");
                return false;
            }
            return true;
        }
        _pkLastQ = questionKey; _pkSameCount = 0;
        return true;
    }

    /// <summary>选择 PK：题面（单词或释义）→ WordBank 匹配 → 点击 .u3-pk-core__text 选项。</summary>
    private async Task DoChoicePkAsync(DomSnapshot snap)
    {
        if (snap.PkAudio || string.IsNullOrWhiteSpace(snap.PkCn))
        {
            // v1.4.1：听音模式题面是音频、无文本可读，词库无从匹配 —— 明确提示，不空转点击
            if (Round % 10 == 1) Emit("单词PK·听音模式：题面为音频，暂不支持自动作答（等待人工/跳过）");
            return;
        }
        if (!PkStuckGuard("C|" + Matcher_Priv_Norm(snap.PkCn))) return;
        // 该题的「首个处理轮」。判定明细只在首轮打印一行 —— 落盘后可以离线统计真实命中率；
        // 此前用 Round%5 抽样，样本残缺（同一题只会被抽到一部分），根本没法统计。
        bool firstRound = _pkSameCount == 0;
        if (WordBank.Count == 0)
        {
            if (Round % 10 == 1)
                Emit("单词PK词库为空 —— 请先在「无痕下载接管」运行时进入一次 PK/词书页面，词库会自动抓取");
            return;
        }
        var pick = WordBank.MatchChoiceEx(snap.PkCn, snap.PkOpts);
        if (pick == null) { NotFound++; return; }   // 仅「词库为空 / 无选项」才会为 null
        if (firstRound) Emit(PkChoiceDiag(snap, pick));
        int ok = await ClickPkChoiceAsync(pick.Answer);
        if (ok > 0)
        {
            Clicked++;
            Emit($"单词PK·选择: “{Trunc(snap.PkCn, 24)}” → “{Trunc(pick.Answer, 24)}”（{pick.Tier} {pick.Score:0}% / 分差 {pick.Gap:0}）");
        }
        else NotFound++;
    }

    /// <summary>
    /// 选择 PK 的单行判定明细（档位 / 分数 / 分差 / 原因 / 题面 / 选项）。每题只打一次，供离线统计命中率：
    /// 日志里 "单词PK·选择 命中*" 与其余档位（中置信/低置信/兜底）的行数之比即真实命中率与置信分布。
    /// 非「命中」档位再补一段候选词/义项诊断，便于定位词库缺口。
    /// </summary>
    private static string PkChoiceDiag(DomSnapshot snap, WordBank.Pick pick)
    {
        string head = $"{pick.Tier}“{Trunc(pick.Answer, 20)}”（{pick.Score:0}% / 次佳 {pick.Second:0} / 分差 {pick.Gap:0}"
                    + (pick.Reason.Length > 0 ? "；" + pick.Reason : "") + "）";
        string extra = "";
        if (!pick.Confident)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(snap.PkCn, "[\u4e00-\u9fff]"))
            {
                var cs = WordBank.MatchMeaningToWords(snap.PkCn, 3);
                extra = cs.Count > 0
                    ? "｜候选词: " + string.Join("/", cs.Select(c => c.word))
                    : "｜词库无匹配释义（该义项未抓取到？）";
            }
            else
            {
                var ms = WordBank.MeaningsOf(snap.PkCn);
                extra = ms.Count > 0
                    ? "｜该词义项: " + string.Join("/", ms.Take(4))
                    : "｜词库无此单词（只需进入一次 PK 页面即可抓取词书）";
            }
        }
        return $"单词PK·选择 {head}｜题面: {Trunc(snap.PkCn, 40)}｜选项: {Trunc(string.Join(" | ", snap.PkOpts.Take(6)), 80)}{extra}";
    }

    /// <summary>拼写 PK：题面中文释义 → WordBank 查词 → 按序点击碎片拼出单词。</summary>
    private async Task DoSpellPkAsync(DomSnapshot snap)
    {
        if (!PkStuckGuard("S|" + Matcher_Priv_Norm(snap.SpellQ))) return;
        bool firstRound = _pkSameCount == 0;   // 同选择 PK：判定明细每题只记一行
        if (WordBank.Count == 0)
        {
            if (Round % 10 == 1)
                Emit("单词PK词库为空 —— 请先在「无痕下载接管」运行时进入一次 PK/词书页面，词库会自动抓取");
            return;
        }
        var hit = WordBank.MatchMeaningToWord(snap.SpellQ);
        if (firstRound)
            Emit(hit == null
                ? $"单词PK·拼写 未命中｜题面: {Trunc(snap.SpellQ, 40)}｜碎片: {Trunc(string.Join("/", snap.SpellFrags.Take(10)), 60)}"
                : $"单词PK·拼写 命中“{Trunc(hit.Value.word, 24)}”（{hit.Value.score:0}%）｜题面: {Trunc(snap.SpellQ, 40)}");
        if (hit == null) { NotFound++; return; }
        string word = hit.Value.word;
        int n = await ClickSpellFragmentsAsync(word);
        if (n > 0) { Clicked += n; Emit($"单词PK·拼写: “{Trunc(snap.SpellQ, 24)}” → “{word}”（点碎片 {n} 个）"); }
        else NotFound++;
    }

    /// <summary>点击选择 PK 选项（按文本定位；连点元素及两级父级确保命中处理器，与参考实现行为一致）。</summary>
    private async Task<int> ClickPkChoiceAsync(string want)
    {
        string script = @"
((want) => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim();
  const els = Array.from(document.querySelectorAll('.u3-pk-core__text'));
  const wn = norm(want);
  let el = els.find(e => norm(e.textContent) === wn)
        || els.find(e => { const t = norm(e.textContent); return t && t.includes(wn); });
  if (!el) return JSON.stringify({ clicked: 0 });
  el.click();
  try { if (el.parentElement) el.parentElement.click(); } catch (e) {}
  try { if (el.parentElement && el.parentElement.parentElement) el.parentElement.parentElement.click(); } catch (e) {}
  return JSON.stringify({ clicked: 1 });
})(" + JsonSerializer.Serialize(want) + @")";
        string json = await _cdp!.EvaluateStringAsync(script, 8000, _work?.Ctx);
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("clicked", out var c) ? c.GetInt32() : 0;
        }
        catch { return 0; }
    }

    /// <summary>拼写 PK：在页面内按碎片文本做 DFS 搜索能拼出目标单词的碎片序列，按序点击。
    /// v1.4.1：碎片点击后即从 DOM 移除（源码 v-if hasClick），必须每点一个重新定位，不能预抓元素循环点。</summary>
    private async Task<int> ClickSpellFragmentsAsync(string word)
    {
        string script = @"
((word) => {
  const norm = s => (s || '').toLowerCase().replace(/[^a-z]/g, '');
  const target = norm(word);
  const scope = document.querySelector('.u3-spell') || document;
  const grab = () => Array.from(scope.querySelectorAll('.u3-spell__answer-right'))
    .map(el => ({ el: el, t: norm(el.textContent) })).filter(x => x.t);
  const frags = grab();
  if (!frags.length || !target) return JSON.stringify({ clicked: 0, reason: 'no-frags' });
  // DFS 求碎片文本序列（可重复字母用不同碎片）
  let found = null;
  const dfs = (acc, seq) => {
    if (found) return;
    if (acc === target) { found = seq.slice(); return; }
    if (acc.length >= target.length) return;
    for (const f of frags) {
      const nt = acc + f.t;
      if (nt.length > target.length || !target.startsWith(nt)) continue;
      seq.push(f.t);
      dfs(nt, seq);
      if (found) return;
      seq.pop();
    }
  };
  dfs('', []);
  if (!found) return JSON.stringify({ clicked: 0, reason: 'no-seq' });
  // 逐步点击：每步重新抓剩余碎片（v-if 使已点碎片从 DOM 消失），匹配序列中当前段文本
  (async () => {
    for (const seg of found) {
      let done = false;
      for (let attempt = 0; attempt < 3 && !done; attempt++) {
        const cur = grab().filter(x => x.t === seg);
        if (cur.length > 0) { try { cur[0].el.click(); done = true; } catch (e) {} }
        if (!done) await new Promise(r => setTimeout(r, 250));
      }
      if (!done) return;
      await new Promise(r => setTimeout(r, 220));
    }
  })();
  return JSON.stringify({ clicked: found.length });
})(" + JsonSerializer.Serialize(word) + @")";
        string json = await _cdp!.EvaluateStringAsync(script, 8000, _work?.Ctx);
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("clicked", out var c) ? c.GetInt32() : 0;
        }
        catch { return 0; }
    }

    // ---------------- 附加管理 ----------------

    private async Task<bool> EnsureAttachedAsync(int port, bool force = false)
    {
        if (!force && _cdp != null && _cdp.Connected && await _cdp.PingAsync()) return true;
        _cdp?.Dispose(); _cdp = null; _worlds.Clear(); _work = null;

        var pages = await CdpClient.ListPagesAsync(port);
        if (pages.Count == 0) return false;

        // 逐目标「全帧」探测：任一帧含题目内容即附加（题目常在跨域 iframe，仅探主文档会漏）
        CdpClient? fallback = null;
        string fallbackLabel = "";
        foreach (var p in pages)
        {
            var c = new CdpClient();
            try
            {
                await c.ConnectAsync(p.WsUrl);
                var probes = await ProbeAllFramesAsync(c);
                int best = probes.Count > 0 ? probes.Max(x => FrameScore(x.S)) : 0;
                string label = string.IsNullOrWhiteSpace(p.Title) ? p.Url : p.Title.Trim();
                if (best > 0)
                {
                    _cdp = c;
                    _targetLabel = label;
                    Emit($"已附加页面目标: “{Trunc(label, 50)}”（帧 {probes.Count} 个，含题目内容）");
                    return true;
                }
                if (fallback == null) { fallback = c; fallbackLabel = label; }
                else c.Dispose();
            }
            catch { c.Dispose(); }
        }

        // 没有页面带题目内容（如停在登录页/首页）—— 附加第一个可连目标等待用户进入做题页
        if (fallback != null)
        {
            _cdp = fallback;
            _targetLabel = fallbackLabel;
            if (Round % 10 == 0)
                Emit($"已附加页面: “{Trunc(fallbackLabel, 50)}”，尚未检测到题目内容 —— 请在客户端中打开试卷/练习");
            return true;
        }
        return false;
    }

    // ---------------- 页面快照 ----------------

    private sealed class DomOption { public string Clean = ""; public bool Checked; public string OptId = ""; }
    private sealed class DomQuestion
    {
        public int Num;
        public string Stem = "";
        public List<DomOption> Opts = new();
        public List<string> Inputs = new();   // 当前值（空串 = 未填）
        // v1.3.6：课程包答案直读（.u3-qst-answer-cont 藏在 DOM，A-F 单字母）+ 题容器 question_id
        public string AnsKey = "";
        public string Qid = "";
        public string Key => (Num > 0 ? "#" + Num : "") + Matcher_Priv_Norm(Stem);
    }
    private sealed class DomSnapshot
    {
        public List<DomQuestion> Questions = new();
        public bool HasSwiper;
        public int SwiperIndex = -1;
        // v1.5.x 长页防漏答：活动作用域内「视口下方（top >= vh - M）且含未选中选项」的题数（快照暴露，缺省 0）
        public int OffscreenBelow;
        // v1.6.6：他页（非活动 slide / 非活动作用域）的未答题数 —— 仅用于日志诊断，不参与推进阻断
        public int OffscreenOther;
        // v1.6.6：活动页指纹（题号+题干+选项文本，**不含选中态与页内计时**）。
        // swiper 索引恒为 -1 时用它判定「是否真的翻页了」→ 复位滚动重试与无进展守卫。
        public string PageSig = "";
        public string FullText = "";
        // 单词PK（v1.3.0）：选择 PK（题面 .u3-pk-core__cn / 选项 .u3-pk-core__text）与拼写 PK（.u3-spell*）
        public string PkCn = "";
        public List<string> PkOpts = new();
        public bool PkSpell;
        public bool PkAudio;   // v1.4.1：听音模式（题面是音频，无文本）
        public string SpellQ = "";
        public List<string> SpellFrags = new();
        public bool HasPk => PkSpell || (!string.IsNullOrEmpty(PkCn) && PkOpts.Count >= 2);
        // v1.3.3：通用兜底命中标志 + 结构诊断（0 题时由探针输出）
        public bool Generic;
        public JsonElement? Diag;

        public List<OcrLine> PreviewLines()
        {
            var lines = new List<OcrLine>();
            if (HasPk)
            {
                if (PkSpell) lines.Add(new OcrLine($"[单词PK·拼写] 题面: {SpellQ}  碎片×{SpellFrags.Count}", 0, 0, 0, 0));
                else lines.Add(new OcrLine($"[单词PK·选择] 题面: {PkCn}  选项: {string.Join(" / ", PkOpts)}", 0, 0, 0, 0));
                return lines;
            }
            foreach (var q in Questions)
            {
                string head = (q.Num > 0 ? q.Num + ". " : "") + q.Stem;
                if (head.Length > 0) lines.Add(new OcrLine(Trunc(head, 80), 0, 0, 0, 0));
                foreach (var o in q.Opts) lines.Add(new OcrLine((o.Checked ? "[已选] " : "[未选] ") + o.Clean, 0, 0, 0, 0));
                if (q.Inputs.Count > 0) lines.Add(new OcrLine("[填空×" + q.Inputs.Count + "] " + string.Join(" | ", q.Inputs.Select(i => i.Length == 0 ? "(空)" : i)), 0, 0, 0, 0));
            }
            return lines;
        }
    }

    /// <summary>页面内探针脚本（自研，v1.3.3）：在「本帧文档」内定位题目 ——
    /// ① u3 组件选择器（活动 swiper 页优先）；② 同源 iframe 与 open shadowRoot 一并探测；
    /// ③ 通用兜底：任意 radio/checkbox + 关联 label 文本按 name/最近容器分组（适配非 u3 组件的页面，如诊断测试）；
    /// ④ 全部落空时输出结构诊断（radio/option类/shadow/iframe 计数 + 文本样例），帮助定位页面形态。
    /// 输出 JSON。跨域 iframe 由引擎在帧级（隔离世界）执行本脚本覆盖，无需同源。</summary>
    private static string ProbeScript() => @"
(() => {
  const OPT_SEL = " + J(OptSel) + @";
  const QT_SEL = " + J(QtSel) + @";
  const NO_SEL = " + J(NoSel) + @";
  const INPUT_SEL = " + J(InputSel) + @";
  // v1.5.x 呈现门控：仅当元素真实与当前视口相交才算「可见」——离屏（翻页/滚动推到视口外的常驻题）不得作答
  // v1.6.6 修复：原来只判「垂直」相交。flipbook 的 swiper 是水平排列（8 个 slide 的 top 全是 -86、
  // 只靠 left 错开 880px），于是所有 slide 全部被判为「可见」——常驻题被反复答、below 被误计，
  // 推进（翻页/提交）永不触发。故补「水平」相交判定，并加 slide 宿主门控。
  const vw = window.innerWidth || document.documentElement.clientWidth || 0;
  const vis = el => { if (!el) return false; try { const r = el.getBoundingClientRect(); const st = getComputedStyle(el); if (r.width <= 1 || r.height <= 1 || st.visibility === 'hidden' || st.display === 'none') return false; const vh = window.innerHeight || document.documentElement.clientHeight || 0; if (vh > 0) { const M = 4; if (r.bottom <= -M || r.top >= vh + M) return false; } if (vw > 0) { const M = 4; if (r.right <= -M || r.left >= vw + M) return false; } return true; } catch (e) { return false; } };
  // v1.6.6：元素是否位于「当前呈现的 slide」内。常驻题散落在同文档各 slide 中，
  // 仅凭视口相交不足以排除，必须显式判宿主 slide 是否为 active。非 swiper 页面恒为 true。
  const inActiveSlide = el => { try { if (!el || !el.closest) return true; const s = el.closest('.swiper-slide'); return s ? s.classList.contains('swiper-slide-active') : true; } catch (e) { return true; } };
  const visA = el => vis(el) && inActiveSlide(el);
  const normTxt = s => (s || '').replace(/\s+/g, ' ').trim();
  const clean = el => { const c = el.cloneNode(true); c.querySelectorAll('.u3-audioPlayer, [slot*=audio]').forEach(e => e.remove()); return normTxt(c.textContent); };
  const isChecked = el => el.classList.contains('is-checked') || !!(el.closest('[class*=option]') && el.closest('[class*=option]').classList.contains('is-checked'));
  const stripLetter = t => { const m = t.match(/^([A-Fa-f])[.、)\s]+/); return m ? t.substring(m[0].length).trim() : t; };
  // v1.3.6：u3-qst-opt（课程包诊断测试组件）选中态 —— radio 或其内层 div 被加了样式类
  const isCheckedQst = el => { try { return el.classList.contains('u3-selected'); } catch (e) {} return false; };

  // 候选根：主文档 + 同源 iframe 文档 + 全部 open shadowRoot（含嵌套）
  const rootDocs = [document];
  document.querySelectorAll('iframe,frame').forEach(f => { try { const d = f.contentDocument; if (d && d.body) rootDocs.push(d); } catch (e) {} });
  const shadows = [];
  const walkShadows = n => { try { n.querySelectorAll('*').forEach(e => { if (e.shadowRoot) { shadows.push(e.shadowRoot); walkShadows(e.shadowRoot); } }); } catch (e) {} };
  rootDocs.forEach(d => walkShadows(d));

  // 选活动范围：优先顶层 swiper 活动页；顶层没有题目内容时依次尝试同源 iframe、shadow 根
  let scope = document.querySelector('.swiper-slide-active') || document.body;
  let optEls = Array.from(scope.querySelectorAll(OPT_SEL)).filter(vis);
  let inputEls = Array.from(scope.querySelectorAll(INPUT_SEL)).filter(vis);
  if (optEls.length === 0 && inputEls.length === 0) {
    for (const d of rootDocs) {
      if (d === document) continue;
      const s2 = (d.querySelector ? d.querySelector('.swiper-slide-active') : null) || d.body || d;
      const o2 = Array.from(s2.querySelectorAll(OPT_SEL)).filter(vis);
      if (o2.length >= 2) { scope = s2; optEls = o2; break; }
      const i2 = Array.from(s2.querySelectorAll(INPUT_SEL)).filter(vis);
      if (i2.length > 0) { scope = s2; inputEls = i2; break; }
    }
  }
  if (optEls.length === 0 && inputEls.length === 0) {
    for (const sh of shadows) {
      const o2 = Array.from(sh.querySelectorAll(OPT_SEL)).filter(vis);
      if (o2.length >= 2) { scope = sh; optEls = o2; break; }
      const i2 = Array.from(sh.querySelectorAll(INPUT_SEL)).filter(vis);
      if (i2.length > 0) { scope = sh; inputEls = i2; break; }
    }
  }

  // v1.5.x 长页防漏答（P1 回归加固）：统计活动作用域内「视口下方（top >= vh - M）且含未选中选项」的题数
  // v1.6.6 修复：原口径把**整个文档**的 .u3compo-qst 都算进来（scope 只是 querySelector 起点不同，
  // 用的是 document 级选择器）。flipbook 的 swiper 每页各带常驻题，水平离屏页的题 top 也会 >= vh，
  // 于是「他人页面的题」被误计为本页下方未答题 → 每轮触发滚动重试、用尽后 return 吞掉推进，
  // 「下一页」按钮（存在且可见）永远点不到。现按宿主区分：
  //   belowSelf  = 本页(scope)内、视口下方未答 → 才需要滚动呈现
  //   belowOther = 其他 slide / 其他作用域内的未答题 → 属他页，只作诊断，不得阻断推进
  let below = 0, belowOther = 0;
  const isUnansQ = qc => { const o = qc.querySelectorAll('.u3-qst-opt'); for (const x of o) { if (!x.classList.contains('u3-selected') && !x.classList.contains('is-checked')) return true; } return false; };
  try {
    const vhB = window.innerHeight || document.documentElement.clientHeight || 0;
    if (vhB > 0) {
      // 本页：直接按 scope 归属 + 视口下方 + 在活动 slide 内
      scope.querySelectorAll('.u3compo-qst').forEach(qc => {
        try {
          const rb = qc.getBoundingClientRect();
          if (rb.top >= vhB - 4 && inActiveSlide(qc) && isUnansQ(qc)) below++;
        } catch (e) {}
      });
      // 他页：全文档剩余部分（用于日志诊断，不参与阻断）
      try {
        if (scope !== document) {
          document.querySelectorAll('.u3compo-qst').forEach(qc => {
            try { if (!scope.contains(qc) && isUnansQ(qc)) belowOther++; } catch (e) {}
          });
        }
      } catch (e) {}
    }
  } catch (e) {}

  // 选项分组为题：从每个选项向上找包含 ≥2 个可见选项的最近容器
  const isTop = el => !el || el.tagName === 'BODY' || el.tagName === 'HTML';
  const groups = [], seen = new Set();
  optEls.forEach((el, i) => {
    if (seen.has(i)) return;
    let container = scope, p = el.parentElement;
    while (p && p !== scope && !isTop(p)) {
      const n = Array.from(p.querySelectorAll(OPT_SEL)).filter(vis).length;
      if (n >= 2) { container = p; break; }
      p = p.parentElement;
    }
    let fc = container;
    const total = Array.from(container.querySelectorAll(OPT_SEL)).filter(vis).length;
    if (total > 6) {
      for (const ch of container.children) {
        const n = Array.from(ch.querySelectorAll(OPT_SEL)).filter(vis).length;
        if (n >= 2 && n <= 6) { fc = ch; break; }
      }
    }
    const members = [];
    optEls.forEach((o, j) => { if (!seen.has(j) && fc.contains(o)) { seen.add(j); members.push(o); } });
    if (members.length) groups.push(members);
  });

  const questions = [];
  // 从分组向上 ≤3 级找题号/题干（u3 与通用兜底共用）
  const extractStem = (members, q) => {
    let anc = members[0].parentElement;
    for (let up = 0; anc && up < 3 && !isTop(anc); up++) {
      if (members.every(m => anc.contains(m))) {
        if (!q.num) { try { const noEl = anc.querySelector(NO_SEL); if (noEl) { const v = parseInt(noEl.textContent.trim()); if (v > 0) q.num = v; } } catch (e) {} }
        if (!q.stem) {
          try {
            const stemEl = Array.from(anc.querySelectorAll(QT_SEL)).find(e => !e.closest('[class*=option__content]') && !members.some(m => m.contains(e)));
            if (stemEl) { q.stem = clean(stemEl).substring(0, 200); if (q.stem) return; }
          } catch (e) {}
          // 通用兜底：公共祖先去掉选项文本后的剩余文本（题号常与题干同节点，如 “4. What does…”）
          try {
            const c = anc.cloneNode(true);
            c.querySelectorAll('input,textarea,button,select,[class*=audio]').forEach(e => e.remove());
            let t = normTxt(c.textContent);
            for (const m of members) { const mt = normTxt(m.textContent); if (mt) t = t.split(mt).join(' '); }
            t = normTxt(t).replace(/^\s*(\d{1,2})[.、)\s]/, (m0, d1) => { if (!q.num) q.num = parseInt(d1); return ''; });
            if (t.length >= 8) { q.stem = t.substring(0, 200); return; }
          } catch (e) {}
        }
        if (q.num && q.stem) return;
      }
      anc = anc.parentElement;
    }
  };
  for (const members of groups) {
    const q = { num: 0, stem: '', opts: [], inputs: [] };
    extractStem(members, q);
    for (const o of members) {
      try {
        const isImg = o.classList.contains('u3-option-img');
        let t;
        if (isImg) { const img = o.querySelector('img'); const src = img ? (img.getAttribute('src') || '') : ''; t = decodeURIComponent(src.split('/').pop().split('?')[0]); }
        else {
          const cont = o.querySelector ? o.querySelector('.u3-opt-cont') : null;   // v1.3.6：u3-qst-opt 文本子节点
          t = cont ? normTxt(cont.textContent) : stripLetter(clean(o));
          if (!t) t = clean(o);
        }
        const oid = (o.getAttribute && o.getAttribute('u3-opt-id')) || '';         // v1.3.6：真实选项字母（可能与显示字母不同）
        if (t || oid) q.opts.push({ t: t, c: isChecked(o) || isCheckedQst(o), id: oid });
      } catch (e) {}
    }
    // v1.3.6：课程包直读 —— 题容器（u3compo-qst/[question_id]）内藏参考答案（.u3-qst-answer-cont，A-F 单字母）
    try {
      const qc = members[0].closest ? members[0].closest('.u3compo-qst, [question_id]') : null;
      if (qc) {
        if (qc.getAttribute && qc.getAttribute('question_id')) q.qid = qc.getAttribute('question_id');
        const ak = qc.querySelector('.u3-qst-answer-cont');
        if (ak) { const v = normTxt(ak.textContent); if (/^[A-Fa-f]{1,2}$/.test(v)) q.ansKey = v.toUpperCase(); }
      }
    } catch (e) {}
    try {
      const inScope = (members[0].closest && members[0].closest('[class*=question]')) || scope;
      q.inputs = Array.from(inScope.querySelectorAll(INPUT_SEL)).filter(vis).map(el => el.value || '');
    } catch (e) {}
    if (q.opts.length || q.inputs.length) questions.push(q);
  }

  // 无选项组但有输入框 → 整页填空题
  if (questions.length === 0 && inputEls.length > 0) {
    const q = { num: 0, stem: '', opts: [], inputs: inputEls.map(el => el.value || '') };
    try { const stemEl = scope.querySelector(QT_SEL); if (stemEl) q.stem = clean(stemEl).substring(0, 200); } catch (e) {}
    questions.push(q);
  }

  // 通用兜底（v1.3.3）：非 u3 组件页面 —— 收集全部 radio/checkbox + 关联文本，按 name / 最近公共容器分组。
  // 覆盖同源 iframe 与 shadowRoot 内的输入框；文本取包裹 label / for 关联 / 父级去 input 后文本。
  let generic = false;
  if (questions.length === 0) {
    try {
      const radios = [];
      const rootsAll = rootDocs.concat(shadows);
      for (const root of rootsAll) {
        let els; try { els = root.querySelectorAll('input[type=radio],input[type=checkbox]'); } catch (e) { continue; }
        for (const el of els) {
          if (!vis(el)) continue;
          let t = '';
          try {
            let lab = el.closest ? el.closest('label') : null;
            if (!lab && el.id) { const rn = el.getRootNode(); if (rn && rn.querySelector) { try { lab = rn.querySelector('label[for=""' + el.id + '""]'); } catch (e2) {} } }
            if (lab) t = normTxt(lab.textContent);
            if (!t && el.parentElement) {
              const c = el.parentElement.cloneNode(true);
              c.querySelectorAll('input,textarea,button,select').forEach(x => x.remove());
              t = normTxt(c.textContent);
            }
          } catch (e) {}
          t = stripLetter(t);
          if (t) radios.push({ el: el, t: t, c: !!el.checked });
        }
      }
      const byName = new Map(), unnamed = [];
      for (const r of radios) {
        if (r.el.name) { const k = r.el.name; if (!byName.has(k)) byName.set(k, []); byName.get(k).push(r); }
        else unnamed.push(r);
      }
      const gs = [];
      for (const g of byName.values()) if (g.length >= 2 && g.length <= 10) gs.push(g);
      const useen = new Set();
      for (let i = 0; i < unnamed.length; i++) {
        if (useen.has(i)) continue;
        let container = null, p = unnamed[i].el.parentElement;
        while (p && !isTop(p)) {
          const n = Array.from(p.querySelectorAll('input[type=radio],input[type=checkbox]')).filter(vis).length;
          if (n >= 2) { container = p; break; }
          p = p.parentElement;
        }
        if (container) {
          const g = [];
          unnamed.forEach((r, j) => { if (!useen.has(j) && container.contains(r.el)) { useen.add(j); g.push(r); } });
          if (g.length >= 2 && g.length <= 10) gs.push(g);
        }
      }
      for (const g of gs) {
        const q = { num: 0, stem: '', opts: g.map(r => ({ t: r.t, c: r.c })), inputs: [] };
        try { extractStem(g.map(r => r.el), q); } catch (e) {}
        questions.push(q);
        generic = true;
      }
    } catch (e) {}
  }

  const swiperEl = document.querySelector('.swiper');
  const hasSwiper = !!swiperEl;
  const swiperIndex = (hasSwiper && swiperEl.swiper) ? swiperEl.swiper.activeIndex : -1;
  let nextBtn = false;
  try {
    for (const el of document.querySelectorAll('.x-button, .u3-button, .btn, button, [role=button]')) {
      if (!vis(el) || el.disabled) continue;
      if (/^下一(页|题)$/.test(normTxt(el.textContent))) { nextBtn = true; break; }
    }
  } catch (e) {}
  // 单词PK（v1.4.1 按课程包源码实测修正：_research/pk_src 解密组件）：
  //   选择 PK 题面 = .u3-pk-core__box（innerText；v1.3.0 的 .u3-pk-core__cn 不存在）
  //   听音模式题面 = .u3-pk-core__word-audio_ing（题面为音频，无文本可读）
  //   选项文本 = .u3-pk-core__text（click handler 在父级 .u3-pk-core__word）
  //   拼写 PK = .u3-spell（v1.3.0 的 .u3-spell.u3-page-pking__pk-core 组合类不存在）
  const pkAudio = !!document.querySelector('.u3-pk-core__word-audio_ing');
  const pkCnEl = pkAudio ? null : document.querySelector('.u3-pk-core__box');
  const pkCn = pkCnEl ? normTxt(pkCnEl.innerText).substring(0, 120) : '';
  const pkOpts = Array.from(document.querySelectorAll('.u3-pk-core__text')).filter(vis)
    .map(e => normTxt(e.textContent)).filter(Boolean);
  const spellCore = document.querySelector('.u3-spell');
  const pkSpell = !!spellCore;
  let spellQ = '';
  const spellFrags = [];
  if (pkSpell) {
    const wcs = Array.from(document.querySelectorAll('.u3-spell__word-cn'));
    const act = wcs.find(e => e.classList.contains('is-active')) || wcs[0];
    if (act) spellQ = normTxt(act.textContent).replace(/^\s*\/\s*/, '').substring(0, 120);
    for (const col of document.querySelectorAll('.u3-spell__answer-col')) {
      const r = col.querySelector('.u3-spell__answer-right');
      const t = r ? normTxt(r.textContent) : '';
      if (t) spellFrags.push(t);
    }
  }
  let fullText = '';
  try { fullText = normTxt((scope.body || (scope.ownerDocument && scope.ownerDocument.body) || document.body).innerText).substring(0, 4000); } catch (e) {}

  // 结构诊断（仅本轮无题时计算）：供引擎日志定位页面形态
  let diag = null;
  if (questions.length === 0) {
    try {
      const q = s => document.querySelectorAll(s).length;
      const fr = [];
      document.querySelectorAll('iframe,frame').forEach(f => {
        let cross = true; try { cross = !(f.contentDocument && f.contentDocument.body); } catch (e) {}
        fr.push((f.getAttribute('src') || '') + (cross ? '(跨域)' : ''));
      });
      let shN = 0; try { document.querySelectorAll('*').forEach(e => { if (e.shadowRoot) shN++; }); } catch (e) {}
      diag = {
        radio: q('input[type=radio]'), cb: q('input[type=checkbox]'),
        optCls: q('[class*=option]'), qCls: q('[class*=question]'), u3opt: q(OPT_SEL),
        swActive: q('.swiper-slide-active'), shadow: shN, iframes: fr,
        url: location.href, title: document.title,
        bodyCls: ((document.body && document.body.className) || '').substring(0, 110),
        text: normTxt((document.body ? document.body.innerText : '')).substring(0, 150)
      };
    } catch (e) {}
  }
  return JSON.stringify({ questions, hasSwiper, swiperIndex, nextBtn, below, belowOther, fullText, pkCn, pkOpts, pkSpell, pkAudio, spellQ, spellFrags, generic, diag });
})()";

    private static string J(string s) => JsonSerializer.Serialize(s);

    /// <summary>页面结构签名（不含作答状态）：swiper 页索引 + 各题(题号/题干/选项文本/填空数)。
    /// 用于「稳定/就绪门控」：结构连续 ≥2 轮一致才动手，避免页面加载/翻页过渡中抢答。
    /// 刻意排除选项选中态与计时文本，否则正常作答也会让签名每轮变化、门控永远不通过。</summary>
    private static string StructSig(DomSnapshot snap)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('S').Append(snap.SwiperIndex).Append('|').Append(snap.Questions.Count).Append('|');
        foreach (var q in snap.Questions)
        {
            sb.Append(q.Num).Append('#').Append(Matcher_Priv_Norm(q.Stem)).Append(':');
            foreach (var o in q.Opts) sb.Append(Matcher_Priv_Norm(o.Clean)).Append(',');
            sb.Append('[').Append(q.Inputs.Count).Append(']');
            sb.Append(';');
        }
        return sb.ToString();
    }

    private static DomSnapshot? ParseSnapshot(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var snap = new DomSnapshot();
            snap.HasSwiper = r.TryGetProperty("hasSwiper", out var hs) && hs.GetBoolean();
            snap.SwiperIndex = r.TryGetProperty("swiperIndex", out var si) ? si.GetInt32() : -1;
            snap.OffscreenBelow = r.TryGetProperty("below", out var bl) ? bl.GetInt32() : 0;
            snap.OffscreenOther = r.TryGetProperty("belowOther", out var bo) ? bo.GetInt32() : 0;
            snap.FullText = r.TryGetProperty("fullText", out var ft) ? ft.GetString() ?? "" : "";
            if (r.TryGetProperty("pkCn", out var pc)) snap.PkCn = pc.GetString() ?? "";
            if (r.TryGetProperty("pkOpts", out var po) && po.ValueKind == JsonValueKind.Array)
                foreach (var o in po.EnumerateArray()) snap.PkOpts.Add(o.GetString() ?? "");
            snap.PkSpell = r.TryGetProperty("pkSpell", out var ps) && ps.GetBoolean();
            snap.PkAudio = r.TryGetProperty("pkAudio", out var pa) && pa.GetBoolean();
            snap.Generic = r.TryGetProperty("generic", out var gn) && gn.GetBoolean();
            if (r.TryGetProperty("diag", out var dg) && dg.ValueKind == JsonValueKind.Object) snap.Diag = dg.Clone();
            if (r.TryGetProperty("spellQ", out var sq)) snap.SpellQ = sq.GetString() ?? "";
            if (r.TryGetProperty("spellFrags", out var sf) && sf.ValueKind == JsonValueKind.Array)
                foreach (var f in sf.EnumerateArray()) snap.SpellFrags.Add(f.GetString() ?? "");
            if (r.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in qs.EnumerateArray())
                {
                    var dq = new DomQuestion();
                    if (q.TryGetProperty("num", out var n)) dq.Num = n.GetInt32();
                    if (q.TryGetProperty("stem", out var st)) dq.Stem = st.GetString() ?? "";
                    if (q.TryGetProperty("ansKey", out var ak)) dq.AnsKey = ak.GetString() ?? "";
                    if (q.TryGetProperty("qid", out var qi)) dq.Qid = qi.GetString() ?? "";
                    if (q.TryGetProperty("opts", out var os) && os.ValueKind == JsonValueKind.Array)
                        foreach (var o in os.EnumerateArray())
                            dq.Opts.Add(new DomOption
                            {
                                Clean = o.TryGetProperty("t", out var t) ? t.GetString() ?? "" : "",
                                Checked = o.TryGetProperty("c", out var c) && c.GetBoolean(),
                                OptId = o.TryGetProperty("id", out var oid) ? oid.GetString() ?? "" : "",
                            });
                    if (q.TryGetProperty("inputs", out var ins) && ins.ValueKind == JsonValueKind.Array)
                        foreach (var i in ins.EnumerateArray()) dq.Inputs.Add(i.GetString() ?? "");
                    snap.Questions.Add(dq);
                }
            }
            // v1.6.6：活动页指纹（复用 StructSig 口径 —— 含题号/题干/选项文本，**不含选中态**）。
            // 用途：swiper 索引恒为 -1 时判定「是否真的翻页了」，从而正确复位滚动重试与无进展守卫。
            snap.PageSig = StructSig(snap);
            return snap;
        }
        catch { return null; }
    }

    // ---------------- 作答决策（C# 侧，题库在本地） ----------------

    /// <summary>选择题决策：返回本题应点击的 (选项文本, 匹配途径)。
    /// ⓪ v1.3.6 课程包直读：题容器 DOM 里藏有参考答案（.u3-qst-answer-cont，A-F）—— 100% 正确，直接点 u3-opt-id 对应项；
    /// ① 题库匹配（题干+选项 → 映射选项，受 minConf 门槛）；
    /// ② DOM 选项文本与题库答案原文精确相等（DOM 无 OCR 噪声，等同中置信，mid 档可用）；
    /// ③ 低档兜底：未命中也点（与 OCR 引擎"低=全部都点"语义一致）。
    /// 已选项、连续 MaxAttempts 次点击未确认选中的项不再点（防死循环）。</summary>
    private List<(string Text, string OptId, string Via)> DecideChoice(DomQuestion q, ConfLevel minConf)
    {
        var res = new List<(string, string, string)>();
        var ansTexts = BankService.AnswerTexts();

        // ⓪ 直读答案：u3-opt-id 是真实字母（页面可能把选项显示顺序打乱，u3-opt-mark 是显示字母）
        if (q.AnsKey.Length > 0 && q.Qid.Length > 0)
        {
            var hit = q.Opts.FirstOrDefault(o => !o.Checked && o.OptId.Equals(q.AnsKey, StringComparison.OrdinalIgnoreCase) && o.Clean.Length + o.OptId.Length > 0);
            if (hit != null)
            {
                string key0 = q.Key + "|@id" + hit.OptId;
                _clickAttempts.TryGetValue(key0, out var a0);
                if (a0 < MaxAttempts)
                {
                    _clickAttempts[key0] = a0 + 1;
                    res.Add((hit.Clean.Length > 0 ? hit.Clean : "optId=" + hit.OptId, hit.OptId, "DOM直读答案"));
                }
            }
            if (res.Count > 0) return res;
        }

        var r = Matcher.MatchBank(q.Stem, q.Opts.Select(x => x.Clean).ToList());
        int li = "ABCD".IndexOf(r.TargetAns, StringComparison.Ordinal);

        foreach (var o in q.Opts)
        {
            if (o.Checked) continue;
            string n = Matcher_Priv_Norm(o.Clean);
            if (n.Length == 0) continue;
            string key = q.Key + "|" + n;
            if (_clickAttempts.TryGetValue(key, out var att) && att >= MaxAttempts) continue;   // 已放弃

            bool hit = false; string via = "";
            if (r.Level >= minConf && r.Mapped &&
                ((li >= 0 && q.Opts.IndexOf(o) == li) ||
                 (r.TargetText.Length > 0 && Matcher_Priv_Norm(r.TargetText) == n)))
            { hit = true; via = "题库" + r.Conf; }
            if (!hit && minConf <= ConfLevel.Mid &&
                ansTexts.Any(a => Matcher_Priv_Norm(a) == n))
            { hit = true; via = "答案原文精确命中"; }
            if (!hit && minConf == ConfLevel.Low)
            { hit = true; via = "低档兜底"; }

            if (hit)
            {
                _clickAttempts[key] = att + 1;   // 下一轮仍未选中则继续累加，到上限放弃
                res.Add((o.Clean, "", via));
            }
        }
        return res;
    }

    private string? DecideFill(DomQuestion q, ConfLevel minConf)
    {
        var r = Matcher.MatchBank(q.Stem, new List<string>());
        if (r.Level >= minConf && !string.IsNullOrEmpty(r.BankAnsText)) return r.BankAnsText;
        return null;
    }

    // ---------------- 页面动作 ----------------

    private sealed class ClickItem { public string Qid = ""; public string OptId = ""; public string Text = ""; }

    /// <summary>点击选项（自研脚本）：v1.3.6 支持 u3 诊断测试组件 —— 优先 question_id + u3-opt-id 组合定位
    /// （u3-opt-id 为真实字母，与显示顺序无关，配合 DOM 直读参考答案），退回文本匹配，再退通用 radio/checkbox。
    /// 执行上下文为工作帧（题目所在帧）。</summary>
    private async Task<int> ClickOptionsAsync(List<ClickItem> items)
    {
        string script = @"
(async (items) => {
  const OPT_SEL = " + J(OptSel) + @";
  // v1.5.x 呈现门控：仅当元素真实与当前视口相交才算「可见」——离屏（翻页/滚动推到视口外的常驻题）不得作答
  // v1.6.6 修复：原来只判「垂直」相交。flipbook 的 swiper 是水平排列（8 个 slide 的 top 全是 -86、
  // 只靠 left 错开 880px），于是所有 slide 全部被判为「可见」——常驻题被反复答、below 被误计，
  // 推进（翻页/提交）永不触发。故补「水平」相交判定，并加 slide 宿主门控。
  const vw = window.innerWidth || document.documentElement.clientWidth || 0;
  const vis = el => { if (!el) return false; try { const r = el.getBoundingClientRect(); const st = getComputedStyle(el); if (r.width <= 1 || r.height <= 1 || st.visibility === 'hidden' || st.display === 'none') return false; const vh = window.innerHeight || document.documentElement.clientHeight || 0; if (vh > 0) { const M = 4; if (r.bottom <= -M || r.top >= vh + M) return false; } if (vw > 0) { const M = 4; if (r.right <= -M || r.left >= vw + M) return false; } return true; } catch (e) { return false; } };
  // v1.6.6：元素是否位于「当前呈现的 slide」内。常驻题散落在同文档各 slide 中，
  // 仅凭视口相交不足以排除，必须显式判宿主 slide 是否为 active。非 swiper 页面恒为 true。
  const inActiveSlide = el => { try { if (!el || !el.closest) return true; const s = el.closest('.swiper-slide'); return s ? s.classList.contains('swiper-slide-active') : true; } catch (e) { return true; } };
  const visA = el => vis(el) && inActiveSlide(el);
  const normTxt = s => (s || '').replace(/\s+/g, ' ').trim();
  const clean = el => { const c = el.cloneNode(true); c.querySelectorAll('.u3-audioPlayer, [slot*=audio]').forEach(e => e.remove()); return normTxt(c.textContent); };
  const stripLetter = t => { const m = t.match(/^([A-Fa-f])[.、)\s]+/); return m ? t.substring(m[0].length).trim() : t; };
  const isChecked = el => el.classList.contains('is-checked') || !!(el.closest('[class*=option]') && el.closest('[class*=option]').classList.contains('is-checked'));
  let scope = document.querySelector('.swiper-slide-active') || document.body;
  if (scope.querySelectorAll(OPT_SEL).length === 0) {
    for (const f of document.querySelectorAll('iframe,frame')) {
      try {
        const d = f.contentDocument; if (!d || !d.body) continue;
        const s2 = d.querySelector('.swiper-slide-active') || d.body;
        if (s2.querySelectorAll(OPT_SEL).length > 0) { scope = s2; break; }
      } catch (e) {}
    }
  }
  let clicked = 0;
  for (const it of items) {
    const qid = (it && it.q) || '';
    const oid = (it && it.i) || '';
    const wn = normTxt((it && it.t) || '').toLowerCase();
    const isCheckedQst = el => { try { return el.classList.contains('u3-selected'); } catch (e) {} return false; };
    let target = null;
    // ① u3 诊断测试：question_id + u3-opt-id 定位（选项显示顺序被打乱，必须按真实字母定位）
    if (qid && oid) {
      try { target = scope.querySelector('.u3compo-qst[question_id=""' + qid + '""] .u3-qst-opt[u3-opt-id=""' + oid + '""]'); } catch (e) {}
      if (!target) { try { target = document.querySelector('.u3compo-qst[question_id=""' + qid + '""] .u3-qst-opt[u3-opt-id=""' + oid + '""]'); } catch (e) {} }
    }
    // ② 文本匹配（u3-option / u3-qst-opt 通用）
    if (!target && wn) {
      const els = Array.from(scope.querySelectorAll(OPT_SEL)).filter(vis);
      for (const el of els) {
        if (isChecked(el) || isCheckedQst(el)) continue;
        const t = stripLetter(clean(el)).toLowerCase();
        if (t === wn) { target = el; break; }
      }
      if (!target) for (const el of els) {
        if (isChecked(el) || isCheckedQst(el)) continue;
        const t = stripLetter(clean(el)).toLowerCase();
        if (t && Math.min(t.length, wn.length) >= 4 && (t.includes(wn) || wn.includes(t))) { target = el; break; }
      }
    }
    if (target && !vis(target)) target = null;   // 呈现门控：仅点击真实处于视口内的选项
    if (target) {
      const clickTarget = target.classList.contains('u3-option-img') ? (target.querySelector('.u3-option-img__content') || target) : target;
      clickTarget.click();
      const rd = target.querySelector('.u3-qst-opt-radio');
      if (rd && rd !== target) { try { rd.click(); } catch (e) {} }
      clicked++;
      await new Promise(r => setTimeout(r, 220 + Math.floor(Math.random() * 480))); // 非固定停顿，更像人工
      continue;
    }
    if (!wn) continue;
    // 通用兜底：radio/checkbox 的 label 文本匹配（含同源 iframe 与 shadowRoot）
    const roots = [document];
    document.querySelectorAll('iframe,frame').forEach(f => { try { const d = f.contentDocument; if (d && d.body) roots.push(d); } catch (e) {} });
    try { const walk = n => { n.querySelectorAll('*').forEach(e => { if (e.shadowRoot) { roots.push(e.shadowRoot); walk(e.shadowRoot); } }); }; walk(document); } catch (e) {}
    outer:
    for (const root of roots) {
      let radios; try { radios = root.querySelectorAll('input[type=radio],input[type=checkbox]'); } catch (e) { continue; }
      for (const el of radios) {
        if (!vis(el) || el.disabled || el.checked) continue;
        let t = '';
        let lab = el.closest ? el.closest('label') : null;
        if (!lab && el.id) { const rn = el.getRootNode(); if (rn && rn.querySelector) { try { lab = rn.querySelector('label[for=""' + el.id + '""]'); } catch (e2) {} } }
        if (lab) t = normTxt(lab.textContent);
        if (!t && el.parentElement) {
          const c = el.parentElement.cloneNode(true);
          c.querySelectorAll('input,textarea,button,select').forEach(x => x.remove());
          t = normTxt(c.textContent);
        }
        t = stripLetter(t).toLowerCase();
        if (t === wn || (t && Math.min(t.length, wn.length) >= 4 && (t.includes(wn) || wn.includes(t)))) {
          try { (lab || el).click(); } catch (e) { try { el.click(); } catch (e2) {} }
          clicked++;
          await new Promise(r => setTimeout(r, 220 + Math.floor(Math.random() * 480))); // 非固定停顿，更像人工
          break outer;
        }
      }
    }
  }
  return JSON.stringify({ clicked: clicked });
})(" + JsonSerializer.Serialize(items.Select(x => new { q = x.Qid, i = x.OptId, t = x.Text })) + @")";
        string json = await _cdp!.EvaluateStringAsync(script, 10000, _work?.Ctx);
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("clicked", out var c) ? c.GetInt32() : 0;
        }
        catch { return 0; }
    }

    /// <summary>填空：原生 setter 赋值 + input/change 事件（Vue 受控输入的通用写法），
    /// 再补 mousedown/mouseup/click/focusin 提交"已作答"，最后 blur 保存（失焦保存类题型）。</summary>
    private async Task<int> FillInputsAsync(string answer)
    {
        string script = @"
(async (answer) => {
  const INPUT_SEL = " + J(InputSel) + @";
  // v1.5.x 呈现门控：仅当元素真实与当前视口相交才算「可见」——离屏填空不得作答
  // v1.6.6 修复：原来只判「垂直」相交。flipbook 的 swiper 是水平排列（8 个 slide 的 top 全是 -86、
  // 只靠 left 错开 880px），于是所有 slide 全部被判为「可见」——常驻题被反复答、below 被误计，
  // 推进（翻页/提交）永不触发。故补「水平」相交判定，并加 slide 宿主门控。
  const vw = window.innerWidth || document.documentElement.clientWidth || 0;
  const vis = el => { if (!el) return false; try { const r = el.getBoundingClientRect(); const st = getComputedStyle(el); if (r.width <= 1 || r.height <= 1 || st.visibility === 'hidden' || st.display === 'none') return false; const vh = window.innerHeight || document.documentElement.clientHeight || 0; if (vh > 0) { const M = 4; if (r.bottom <= -M || r.top >= vh + M) return false; } if (vw > 0) { const M = 4; if (r.right <= -M || r.left >= vw + M) return false; } return true; } catch (e) { return false; } };
  // v1.6.6：元素是否位于「当前呈现的 slide」内。常驻题散落在同文档各 slide 中，
  // 仅凭视口相交不足以排除，必须显式判宿主 slide 是否为 active。非 swiper 页面恒为 true。
  const inActiveSlide = el => { try { if (!el || !el.closest) return true; const s = el.closest('.swiper-slide'); return s ? s.classList.contains('swiper-slide-active') : true; } catch (e) { return true; } };
  const visA = el => vis(el) && inActiveSlide(el);
  let scope = document.querySelector('.swiper-slide-active') || document.body;
  if (scope.querySelectorAll(INPUT_SEL).length === 0) {
    for (const f of document.querySelectorAll('iframe,frame')) {
      try {
        const d = f.contentDocument; if (!d || !d.body) continue;
        const s2 = d.querySelector('.swiper-slide-active') || d.body;
        if (s2.querySelectorAll(INPUT_SEL).length > 0) { scope = s2; break; }
      } catch (e) {}
    }
  }
  const ins = Array.from(scope.querySelectorAll(INPUT_SEL)).filter(vis);
  let filled = 0;
  const touched = [];
  for (const el of ins) {
    if ((el.value || '').length > 0) continue;
    const proto = el.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
    if (!setter) continue;
    try { el.focus(); } catch (e) {}
    setter.call(el, answer);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    el.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    el.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    el.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    el.dispatchEvent(new Event('focusin', { bubbles: true }));
    touched.push(el);
    filled++;
    await new Promise(r => setTimeout(r, 220 + Math.floor(Math.random() * 480))); // 非固定停顿，更像人工
  }
  for (const el of touched) { try { el.dispatchEvent(new FocusEvent('blur')); } catch (e) {} }
  return JSON.stringify({ filled: filled });
})(" + JsonSerializer.Serialize(answer) + @")";
        string json = await _cdp!.EvaluateStringAsync(script, 10000, _work?.Ctx);
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("filled", out var f) ? f.GetInt32() : 0;
        }
        catch { return 0; }
    }

    /// <summary>长页防漏答（P1 回归加固）：把视口下方、含未选中选项的题滚动进视口。
    /// 仅执行只读 + 平滑滚动（scrollIntoView），不点击、不改 DOM/内容；找不到目标或无可滚动容器返回 false。
    /// 执行上下文沿用工作帧（与 ClickOptionsAsync / TryAdvanceAsync 一致）。</summary>
    private async Task<bool> RevealBelowAsync()
    {
        string script = @"
(() => {
  const M = 4;
  const vh = window.innerHeight || document.documentElement.clientHeight || 0;
  if (vh <= 0) return JSON.stringify({ scrolled: false });
  const isUnanswered = el => { try { return !el.classList.contains('u3-selected') && !el.classList.contains('is-checked'); } catch (e) { return false; } };
  // 与作答相同的活动作用域（含同源 iframe 回退）
  const scopes = [];
  const mainScope = document.querySelector('.swiper-slide-active') || document.body;
  scopes.push(mainScope);
  document.querySelectorAll('iframe,frame').forEach(f => { try { const d = f.contentDocument; if (d && d.body) scopes.push(d.querySelector('.swiper-slide-active') || d.body); } catch (e) {} });
  for (const sc of scopes) {
    const qcs = Array.from(sc.querySelectorAll('.u3compo-qst'));
    for (const qc of qcs) {
      try {
        const r = qc.getBoundingClientRect();
        if (r.top >= vh - M) {
          const opts = Array.from(qc.querySelectorAll('.u3-qst-opt'));
          if (opts.some(isUnanswered)) {
            // 找最近的可滚动祖先（无则直接对题容器滚动）；scrollIntoView 不改内容，只滚动
            let el = qc, scroller = null;
            while (el && el !== document.body && el !== document.documentElement) {
              const ov = getComputedStyle(el).overflowY || getComputedStyle(el).overflow;
              if (/auto|scroll|overlay/.test(ov)) { scroller = el; break; }
              el = el.parentElement;
            }
            const target = scroller || qc;
            target.scrollIntoView({ block: 'center', behavior: 'smooth' });
            return JSON.stringify({ scrolled: true });
          }
        }
      } catch (e) {}
    }
  }
  return JSON.stringify({ scrolled: false });
})()";
        try
        {
            string json = await _cdp!.EvaluateStringAsync(script, 8000, _work?.Ctx);
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("scrolled", out var s) && s.GetBoolean();
        }
        catch { return false; }
    }

    /// <summary>推进：优先"下一题/下一页"，无则"提交"。翻页成功以 swiper index 前进或正文变化为准。
    /// v1.3.3：在工作帧找按钮，找不到时回退主帧（翻页按钮可能挂在主文档）；验证按工作帧正文变化。</summary>
    private async Task<bool> TryAdvanceAsync(CdpClient cdp, DomSnapshot snap)
    {
        string before = snap.FullText.Length > 500 ? snap.FullText[..500] : snap.FullText;
        string script = @"
(() => {
  // v1.5.x 呈现门控：翻页/提交按钮也须真实处于视口内才点击（避免对未呈现的隐藏按钮误操作）
  // v1.6.6：原 `vis` 只判垂直、且直接丢弃「在 DOM 里但滚动到了视口外」的按钮 —— 实测该页
  // 「下一页」按钮 top=905 可见，但某些版式里按钮会落在滚动区外，导致 TryAdvance 永远返回 acted=''
  // （就是卡死的直接原因）。现改为：先找「真实存在且未被禁用」的候选，
  // 可见的直接点；仅因滚动离屏的，先 scrollIntoView 呈现再点。
  const shown = el => { try { return el.offsetParent !== null || el.getClientRects().length > 0; } catch (e) { return false; } };
  const inView = el => { try { const r = el.getBoundingClientRect(); const vh = window.innerHeight || document.documentElement.clientHeight || 0; if (vh <= 0) return true; const M = 4; return !(r.bottom <= -M || r.top >= vh + M); } catch (e) { return false; } };
  const inActiveSlide = el => { try { if (!el || !el.closest) return true; const s = el.closest('.swiper-slide'); return s ? s.classList.contains('swiper-slide-active') : true; } catch (e) { return true; } };
  const normTxt = s => (s || '').replace(/\s+/g, ' ').trim();
  const btnSel = '.x-button, .u3-button, .btn, button, [role=button]';
  // 候选必须在活动 slide 内（常驻的翻页控件可能同时存在于每个 slide，点错页会跳到别人那页）
  const ok = el => el && shown(el) && !el.disabled && inActiveSlide(el);
  let hit = null, kind = '';
  // v1.3.6：u3 诊断测试组件 class 直定位（无文本）
  const ub = document.querySelector('.u3-pagination-button-next');
  if (ok(ub)) { hit = ub; kind = 'next'; }
  if (!hit) {
    for (const want of ['下一题', '下一页']) {
      for (const el of document.querySelectorAll(btnSel)) {
        if (!ok(el)) continue;
        if (normTxt(el.textContent) === want) { hit = el; kind = 'next'; break; }
      }
      if (hit) break;
    }
  }
  if (!hit) {
    const sb = document.querySelector('.submit-btn');
    if (ok(sb)) { hit = sb; kind = 'submit'; }
  }
  if (!hit) {
    for (const el of document.querySelectorAll(btnSel)) {
      if (!ok(el)) continue;
      if (/^提交(答卷|答案)?$/.test(normTxt(el.textContent))) { hit = el; kind = 'submit'; break; }
    }
  }
  if (!hit) return JSON.stringify({ acted: '' });
  // 仅因滚动离屏 → 先滚进来再点（Presentation 门控：避免对未呈现按钮操作）
  if (!inView(hit)) {
    try { hit.scrollIntoView({ block: 'center', behavior: 'instant' }); } catch (e) {}
    if (!inView(hit)) return JSON.stringify({ acted: '', offscreen: true });
  }
  hit.click();
  return JSON.stringify({ acted: kind });
})()";
        long workCtx = _work?.Ctx ?? -1;
        string json = "";
        try { json = await cdp.EvaluateStringAsync(script, 8000, workCtx > 0 ? workCtx : null); }
        catch { }
        string acted = "";
        try
        {
            using var d = JsonDocument.Parse(json);
            acted = d.RootElement.TryGetProperty("acted", out var a) ? a.GetString() ?? "" : "";
        }
        catch { }
        if (acted.Length == 0 && workCtx > 0)
        {
            // 工作帧无按钮 → 回退主帧（第一帧）再试一次
            try
            {
                var frames = await EnsureFramesAsync(cdp);
                if (frames.Count > 0 && frames[0].Ctx != workCtx)
                {
                    json = await cdp.EvaluateStringAsync(script, 8000, frames[0].Ctx);
                    using var d2 = JsonDocument.Parse(json);
                    acted = d2.RootElement.TryGetProperty("acted", out var a2) ? a2.GetString() ?? "" : "";
                }
            }
            catch { }
        }
        if (acted.Length == 0) return false;

        // 等待页面变化（swiper index 前进、活动页指纹变化、或工作帧正文变化）
        // v1.6.6：新增 PageSig 判定 —— 很多 flipbook 没有 Swiper 实例（索引恒为 -1），
        // 只靠索引+文本前 500 字判定会漏掉「题干相同但换页了」的情况，导致误判“无变化”。
        // 判定顺序：索引前进 → 指纹变化 → 正文变化。
        for (int k = 0; k < 8; k++)
        {
            await Task.Delay(250);
            try
            {
                string j2 = await cdp.EvaluateStringAsync(ProbeScript(), 8000, workCtx > 0 ? workCtx : null);
                var s2 = ParseSnapshot(j2);
                if (s2 == null) continue;
                if (s2.SwiperIndex >= 0 && snap.SwiperIndex >= 0 && s2.SwiperIndex != snap.SwiperIndex)
                {
                    Emit($"{(acted == "next" ? "已翻页" : "已提交")}（swiper 第 {s2.SwiperIndex + 1} 页）");
                    return true;
                }
                if (s2.PageSig.Length > 0 && snap.PageSig.Length > 0 && s2.PageSig != snap.PageSig)
                {
                    Emit(acted == "next" ? "已翻页（页面内容已变化）" : "已点击提交（页面内容已变化）");
                    return true;
                }
                string now = s2.FullText.Length > 500 ? s2.FullText[..500] : s2.FullText;
                if (now != before)
                {
                    Emit(acted == "next" ? "已翻页（页面内容已变化）" : "已点击提交（页面内容已变化）");
                    return true;
                }
            }
            catch { return false; }
        }
        Emit($"点击“{(acted == "next" ? "下一页" : "提交")}”后页面无变化（可能已是最后一页）");
        return false;
    }

    // ---------------- 终止判定 / 工具 ----------------

    /// <summary>成绩/得分页签名（与 OCR 引擎 AutoEngine.IsEndPage 同一词表）。</summary>
    private static bool IsEndPage(string fullText, out string matched)
    {
        matched = "";
        if (string.IsNullOrEmpty(fullText)) return false;
        var m = Regex.Match(fullText,
            @"(得分|成绩|正确率)\s*[:：]?\s*[0-9０-９]" +
            @"|(提交成功|练习完成|试卷完成|作答完成|已交卷|本卷完成|重新测试|查看回顾|finished|submitted|completed)" +
            @"|(失分分析|考点分析|词汇分析|语音分析|同类题练习|已生成个性化|快去完成吧|生成.{0,8}练习|本次.{0,4}[0-9１-９]+\s*错)",
            RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        matched = m.Value;
        return true;
    }

    static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
    static string Matcher_Priv_Norm(string s) =>
        Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9\u4e00-\u9fff ]", " "), @"\s+", " ").Trim();
}
