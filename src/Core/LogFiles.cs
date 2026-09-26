namespace TxwExtract.Core;

/// <summary>
/// 诊断日志写入器（bridge.log / startup_state.txt / logs\session.log）：追加写入并在超过容量上限时保留「尾部」截断，
/// 落实诊断文件「超上限截尾」的项目约定 —— 旧的 <c>File.AppendAllText</c> 直写会无限增长，
/// 而简单清空重写又会丢失近期内容，因此这里只丢弃最旧的前缀、保留最近的字节。
/// 时间戳格式与消息语义由调用方拼接，本方法只负责「追加 + 截尾」，并吞掉一切 IO 异常（诊断日志不惊扰主流程）。
/// </summary>
public static class LogFiles
{
    /// <summary>诊断文件默认上限：512 KB。</summary>
    public const int DefaultCapBytes = 512 * 1024;

    /// <summary>会话日志上限：4 MB（一次运行的全部明细，比 bridge.log 宽得多）。</summary>
    public const int SessionCapBytes = 4 * 1024 * 1024;

    /// <summary>跨线程串行化（调用点多在 UI 线程，但 selftest/后台路径也会写，锁开销可忽略）。</summary>
    static readonly object _gate = new();

    static volatile bool _sessionStarted;
    static string? _sessionPath;

    /// <summary>会话日志目录：<c>%LOCALAPPDATA%\TxwExtract\logs</c>。</summary>
    public static string LogDir => Path.Combine(AppPaths.DataDir, "logs");

    /// <summary>
    /// 本次运行的会话日志路径。正常为 <c>logs\session.log</c>；若启动时**已有另一个实例在运行**
    /// （典型：GUI 开着的时候跑 <c>--pkcheck</c> 这类 CLI 诊断），则改用 <c>logs\session-&lt;pid&gt;.log</c>，
    /// 绝不覆盖别人正在写的日志。
    /// </summary>
    public static string SessionPath => _sessionPath ?? Path.Combine(LogDir, "session.log");

    /// <summary>
    /// 已有本工具的另一个实例在运行吗？用全局单实例互斥量做一次性**非破坏性**探测：
    /// 能拿到（含"前任已崩溃"的 abandoned 情形）→ 没有实例在跑；拿不到 → 有人持有。
    /// 探测失败一律按「没有」处理：宁可清自己的日志，也不误判成"有人在跑"导致日志找不到。
    /// </summary>
    public static bool AnotherInstanceRunning()
    {
        try
        {
            using var m = new Mutex(false, AppPaths.SingleInstanceMutex, out _);
            bool got;
            try { got = m.WaitOne(0); }
            catch (AbandonedMutexException) { got = true; }   // 前任崩溃未释放 → 实际无人
            if (!got) return true;
            try { m.ReleaseMutex(); } catch { }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// 启动时调用一次：**清空重建**会话日志（「每次启动自动清除」约定，文件只反映当前这一次运行）。
    /// 进程内幂等；<see cref="Session"/> 会兜底触发，因此任何时刻写日志都不会追加到上一轮的文件里。
    /// </summary>
    public static void StartSession(string note = "")
    {
        lock (_gate)
        {
            if (_sessionStarted) return;
            try
            {
                Directory.CreateDirectory(LogDir);
                // 已有实例在跑 → 不碰 session.log，本进程写自己的独立文件
                _sessionPath = AnotherInstanceRunning()
                    ? Path.Combine(LogDir, $"session-{Environment.ProcessId}.log")
                    : Path.Combine(LogDir, "session.log");
                PruneStraySessions();
                File.WriteAllText(SessionPath,
                    $"==== TxwExtract 本次运行日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss} {note} ====\r\n" +
                    $"==== 本文件每次启动自动清空重建，只保留当前这一次运行的日志（日志目录：{LogDir}）====\r\n");
            }
            catch { }
            // 标志位放在**最后**：其它线程一旦看到 true，就能保证 _sessionPath 已写好，
            // 否则并发首写可能落到默认的 session.log（那个文件在"已有实例"时是不该被碰的）。
            finally { _sessionStarted = true; }
        }
    }

    /// <summary>
    /// 清理历史遗留：只保留最近 <paramref name="keepDays"/> 天内修改过的 <c>session-*.log</c>
    /// （正常路径下每次启动就一个自建文件，这里兜住「进程被反复强杀、pid 文件越积越多」）。
    /// </summary>
    static void PruneStraySessions(int keepDays = 3)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.GetFiles(LogDir, "session-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }

    /// <summary>写一行会话日志（自动补时间戳与换行；超过 <see cref="SessionCapBytes"/> 保留尾部）。</summary>
    public static void Session(string msg)
    {
        if (!_sessionStarted) StartSession();
        AppendCapped(SessionPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n", SessionCapBytes);
    }

    /// <summary>
    /// 追加写入一段诊断文本；写入后若文件超过 <paramref name="capBytes"/>，重写为「尾部保留」内容
    /// （对齐到行首丢弃最旧前缀，保留最近 capBytes 字节）。
    /// </summary>
    public static void AppendCapped(string path, string text, int capBytes = DefaultCapBytes)
    {
        try
        {
            lock (_gate)
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, text);
                TrimIfNeeded(path, capBytes);
            }
        }
        catch { }
    }

    /// <summary>超过上限时把文件重写为尾部内容：从「倒数第 capBytes 字节所在的下一行行首」起整段保留。</summary>
    static void TrimIfNeeded(string path, int capBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= capBytes) return;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                if (fs.Length <= capBytes) return;
                fs.Seek(-capBytes, SeekOrigin.End);
                // 对齐到下一行行首：避免截断点落在 UTF-8 多字节字符或半行中间
                int b;
                while ((b = fs.ReadByte()) >= 0 && b != '\n') { }
                long start = fs.Position;
                int len = (int)(fs.Length - start);
                var tail = new byte[len];
                fs.ReadExactly(tail, 0, len);
                fs.SetLength(0);
                fs.Position = 0;
                fs.Write(tail, 0, tail.Length);
            }
        }
        catch { }
    }
}
