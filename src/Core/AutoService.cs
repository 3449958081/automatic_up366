namespace TxwExtract.Core;

/// <summary>
/// 自动回答的服务包装：在 AutoEngine 之上维护日志缓冲与状态快照，
/// 供 WebView2 桥接层（UiBridge.autoStatus）轮询消费，字段对齐 Node 版 /api/auto/status。
/// </summary>
public sealed class AutoService
{
    private readonly AutoEngine _engine = new();
    private readonly List<string> _log = new();
    private List<OcrLine> _lastPreview = new();
    private string _previewInfo = "";
    public bool EndDetected { get; private set; }

    public AutoService()
    {
        _engine.Log += line => { lock (_log) { _log.Add(line); if (_log.Count > 300) _log.RemoveRange(0, _log.Count - 300); } };
        _engine.Preview += (lines, info) => { _lastPreview = lines; _previewInfo = info; };
        _engine.Stopped += (reason, msg) => { if (reason == StopReason.EndDetected) EndDetected = true; };
    }

    public bool Running => _engine.Running;

    /// <summary>启动（dir 为空则自动探测客户端数据目录）。</summary>
    public (bool Ok, string Msg) Start(string title, string proc, int intervalMs, string minConf, string dir)
    {
        var conf = ConfLevelEx.Parse(minConf);

        // v2.1.21：轮询间隔下限保护——OCR 识别一轮通常需数秒，低于 5 秒会与上一轮识别重叠
        // （前端已限制，这里兜底防绕过前端/直接改 config.json）。
        if (intervalMs < 5000)
        {
            intervalMs = 5000;
            PushLog("轮询间隔已自动调整为最短 5 秒（OCR 识别一轮通常需要数秒）");
        }

        // 未显式给目录时，自动探测客户端数据目录（多候选）
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = TxwExtract.Program.DefaultScanDir();
            if (!string.IsNullOrWhiteSpace(dir)) SaveScanDir(dir);
        }
        if (!string.IsNullOrWhiteSpace(dir))
        {
            try
            {
                int n = BankService.Build(dir);
                if (n > 0) SaveScanDir(dir);
                else if (_lastBuiltDir != dir)
                    PushLog($"已在目录 {dir} 构建题库，命中 {n} 题（若客户端尚未打开过对应试卷，请先打开）");
                _lastBuiltDir = dir;
            }
            catch (Exception e) { PushLog("构建题库异常: " + e.Message); }
        }

        if (BankService.Count == 0)
        {
            string msg = "题库为空——客户端数据目录未找到题目。请确认：① 已在天学网客户端打开过对应试卷；② 下方「数据目录」已指向客户端的 resources 目录（可在设置中修改）。当前探测目录：" + (dir ?? "(空)");
            PushLog(msg);
            return (false, msg);
        }

        // 目标窗口：进程名优先 → 标题精确 → 标题含"天学网"且排除工具自身；
        // 同名多窗口（客户端常有弹窗/悬浮窗同标题）一律取面积最大者（主窗口）。
        var wins = WinApi.EnumTopLevelWindows();
        WinInfo? w = null;
        if (!string.IsNullOrWhiteSpace(proc))
            w = WinApi.LargestWindow(wins.Where(x => x.ProcessName.Equals(proc, StringComparison.OrdinalIgnoreCase)));
        if (w == null && !string.IsNullOrWhiteSpace(title))
        {
            w = WinApi.LargestWindow(wins.Where(x => x.Title == title));
            w ??= WinApi.LargestWindow(wins.Where(x => x.Title.Contains(title, StringComparison.OrdinalIgnoreCase)));
        }
        w ??= WinApi.LargestWindow(wins.Where(x => x.Title.Contains("天学网", StringComparison.OrdinalIgnoreCase)
                                        && !x.Title.Contains("答案提取", StringComparison.OrdinalIgnoreCase)));
        if (w == null)
        {
            string msg = "未找到目标窗口——请先启动客户端并点【刷新窗口】后再开始";
            PushLog(msg);
            return (false, msg);
        }

        EndDetected = false;
        _engine.Start(w.Hwnd, w.Title, conf, intervalMs);
        string ok = "已开始自动回答（窗口=" + w.Title + "）";
        PushLog(ok);
        return (true, ok);
    }

    private string? _lastBuiltDir;
    private void PushLog(string line)
    {
        string l = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line;
        lock (_log) { _log.Add(l); if (_log.Count > 300) _log.RemoveRange(0, _log.Count - 300); }
    }

    public void Stop() => _engine.Stop();

    /// <summary>状态快照（序列化为 JSON 供前端轮询）。</summary>
    public object Status()
    {
        List<string> log;
        lock (_log) log = new List<string>(_log);
        var preview = _lastPreview.Select(l => new { t = l.Text }).ToList();
        long startedAt = _engine.StartedAt == default ? 0
            : new DateTimeOffset(_engine.StartedAt.ToUniversalTime()).ToUnixTimeMilliseconds();
        return new
        {
            ok = true,
            running = _engine.Running,
            round = _engine.Round,
            clicked = _engine.Clicked,
            notFound = _engine.NotFound,
            unchanged = _engine.UnchangedRounds,
            startedAt,
            endDetected = EndDetected,
            log,
            preview,
            previewInfo = _previewInfo,
        };
    }

    private static void SaveScanDir(string dir)
    {
        try
        {
            var cfg = AppPaths.Load();
            if (cfg.ScanDir != dir) { cfg.ScanDir = dir; AppPaths.Save(cfg); }
        }
        catch { }
    }
}
