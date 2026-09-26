using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;
using TxwExtract.Core;

namespace TxwExtract.UI;

/// <summary>
/// 主窗体：WebView2 承载原版 WebUI（视觉 1:1），后端逻辑走 C#。
/// 通信采用 postMessage 请求-响应（最可靠的 WebView2 通道）：
///   JS: window.chrome.webview.postMessage({id, path, args})
///   C#: WebMessageReceived → 后台执行 UiBridge 方法 → PostWebMessageAsString({id, data})
/// 启动顺序由 Program 控制：独立开屏窗口（SplashForm）先行显示盖住主窗体，
/// 页面导航完成（onReady 回调）后再关闭开屏——WebView2 初始化期的黑屏被完全遮挡。
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppConfig _cfg;
    private readonly UiBridge _bridge;
    private readonly RouteRegistry _routes;     // 中央路由表（BuildRouteTable 构建，Dispatch 查表分发）
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0xf0, 0xf3, 0xf9) };
    private readonly Task<CoreWebView2Environment>? _envTask;   // Program.Main 预热的环境
    private readonly Action? _onReady;                          // 页面就绪后由 Program 关闭独立开屏窗口

    /// <summary>客户区屏幕矩形缓存：SplashForm 由此计算商标飞行落点（对齐 webui header 中 logo 位置），跨线程只读。</summary>
    internal static Rectangle ClientScreenRect;

    public MainForm(AppConfig cfg, Task<CoreWebView2Environment>? envTask = null, Action? onReady = null)
    {
        _cfg = cfg;
        _envTask = envTask;
        _onReady = onReady;
        _bridge = new UiBridge(cfg) { Owner = this };
        _bridge.RecordExport = RecordLastExport;   // 报告等自写文件的导出登记（供「打开所在文件夹」）
        _routes = BuildRouteTable();

        Text = "天学网答案提取";
        BackColor = Color.FromArgb(0xf0, 0xf3, 0xf9);   // 窗体底色与页面/SplashForm 背景同色（#f0f3f9），消除 WebView2 首帧上屏前的黑底
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 840);
        MinimumSize = new Size(960, 640);
        Font = new Font("Microsoft YaHei UI", 9f);

        Controls.Add(_web);     // WebView2（初始化期黑屏由独立开屏窗口盖住）

        Load += async (_, _) => { TraceLog("MainForm Load → InitWebAsync"); await InitWebAsync(); };
        // 保险：15 秒内页面未上报首帧也关闭开屏窗口，防止永盖
        Shown += (_, _) =>
        {
            var t = new System.Windows.Forms.Timer { Interval = 15000 };
            t.Tick += (_, _) => { t.Stop(); TraceLog("SafeReady by timeout"); SafeReady(); };
            t.Start();
        };
        // 关键：关闭窗口必须还原 hosts 劫持（旧版只 Dispose WebView2，导致 hosts 残留 →
        // 客户端下载全部被送到无人监听的 127.0.0.1:443 → ERR_CONNECTION_REFUSED）
        FormClosing += (_, _) =>
        {
            TraceLog("FormClosing → Shutdown（停止接管并还原 hosts）");
            try { _bridge.Shutdown(); } catch (Exception e) { TraceLog("Shutdown EXC: " + e.Message); }
            try { _web.Dispose(); } catch { }
        };
        TraceLog("MainForm ctor");
        Shown += (_, _) => TraceLog("MainForm Shown");
        // 缓存客户区屏幕矩形（含 DPI/边框实际值），供开屏商标飞向 webui logo 落点
        void CacheClientRect()
        {
            if (IsHandleCreated)
                ClientScreenRect = new Rectangle(PointToScreen(Point.Empty), ClientSize);
        }
        Shown += (_, _) => CacheClientRect();
        LocationChanged += (_, _) => CacheClientRect();
        Resize += (_, _) => CacheClientRect();
    }

    private bool _readyFired;

    /// <summary>
    /// 关闭静态开屏遮罩。触发时机为「CapturePreview 轮询确认页面已真实绘制出内容」，
    /// 而不是导航完成或页面自报——导航完成/JS rAF 时 Chromium 合成器可能尚未上屏，
    /// 过早撤遮罩会露出黑底（此前黑屏的根因）。
    /// </summary>
    private void SafeReady()
    {
        if (_readyFired) return;
        _readyFired = true;
        TraceLog("SafeReady (ui paint verified)");
        try { _onReady?.Invoke(); } catch { }
    }

    /// <summary>
    /// 首帧监视：导航完成后周期性用 CapturePreview 抓取页面渲染位图，
    /// 抓到「非单一颜色」的画面（即真实 UI 已绘制）才撤开屏。确定性方案，替代盲等延时。
    /// </summary>
    private async Task WatchFirstPaintAsync()
    {
        var deadline = DateTime.Now.AddSeconds(10);
        while (DateTime.Now < deadline && !_readyFired)
        {
            try
            {
                if (_web.CoreWebView2 != null)
                {
                    using var ms = new MemoryStream();
                    await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    if (IsContentful(ms.ToArray()))
                    {
                        TraceLog("WatchFirstPaint: contentful preview, ready");
                        BeginInvoke(SafeReady);
                        return;
                    }
                }
            }
            catch { }
            await Task.Delay(120);
        }
        if (!_readyFired)
        {
            TraceLog("WatchFirstPaint: timeout, force ready");
            BeginInvoke(SafeReady);
        }
    }

    /// <summary>抽样判断位图是否包含多种颜色（纯黑/纯白等单一底色视为"还没画出来"）。</summary>
    private static bool IsContentful(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            using var bmp = new System.Drawing.Bitmap(ms);
            int sx = Math.Max(1, bmp.Width / 40), sy = Math.Max(1, bmp.Height / 24);
            var colors = new HashSet<int>();
            for (int y = 0; y < bmp.Height; y += sy)
                for (int x = 0; x < bmp.Width; x += sx)
                {
                    colors.Add(bmp.GetPixel(x, y).ToArgb() & 0x00FFFFFF);
                    if (colors.Count > 10) return true;
                }
            return false;
        }
        catch { return false; }
    }

    // ---------- 诊断 ----------
    private static void TraceLog(string msg)
    {
        string dir = AppPaths.DataDir;   // 单一来源（与 AppConfig/Program 一致）
        LogFiles.AppendCapped(Path.Combine(dir, "bridge.log"), $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        LogFiles.AppendCapped(Path.Combine(dir, "startup_state.txt"), $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        LogFiles.Session("app  | " + msg);
    }

    // ---------- 导出下载（v1.6.6） ----------

    /// <summary>最近一次成功导出的完整路径（前端可读取并在界面上显示 + 一键打开所在文件夹）。</summary>
    internal static string LastExportPath = "";

    /// <summary>把下载文件名净化成合法文件名（去掉路径分隔符与非法字符），只保留基名。</summary>
    private static string SanitizeFileName(string raw)
    {
        string name = "";
        try { name = Path.GetFileName(raw ?? ""); } catch { }
        if (name.Length == 0) name = "export";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        // 防御：Windows 保留名（CON/PRN/AUX/NUL/COM1..9/LPT1..9）会写入失败
        string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL"
            || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$"))
            name = "_" + name;
        return name;
    }

    /// <summary>若目标已存在则追加 (1)(2)…，避免静默覆盖用户既有的导出文件。</summary>
    private static string UniquePath(string path)
    {
        try
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; i < 1000; i++)
            {
                string cand = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(cand)) return cand;
            }
        }
        catch { }
        return path;
    }

    /// <summary>记录本次导出路径到内存 + 会话日志，供前端查询与「打开文件夹」使用。</summary>
    private static void RecordLastExport(string fullPath)
    {
        LastExportPath = fullPath;
        LogFiles.Session("app  | 导出完成: " + fullPath);
    }

    /// <summary>
    /// 打开导出目录（或选中某文件）。前端「打开所在文件夹」调用。
    /// 目录不存在时退化为打开其父目录；再失败则返回 false 由前端提示路径文本。
    /// </summary>
    internal static bool OpenExportFolder(string? fileOrDir)
    {
        try
        {
            string target = (fileOrDir ?? "").Trim();
            if (target.Length == 0) target = LastExportPath;
            if (target.Length == 0) return false;
            if (File.Exists(target))
            {
                // /select 让资源管理器高亮该文件
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + target + "\"");
                return true;
            }
            string dir = Directory.Exists(target) ? target : (Path.GetDirectoryName(target) ?? "");
            if (dir.Length > 0 && Directory.Exists(dir))
            {
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                return true;
            }
        }
        catch (Exception ex) { TraceLog("OpenExportFolder 失败: " + ex.Message); }
        return false;
    }

    /// <summary>
    /// 用系统默认程序打开「作文报告」HTML（前端「打开报告」按钮调用）。
    /// **白名单校验**：只允许打开导出目录内的文件 —— 否则该接口会变成一个"任意路径启动器"
    /// （前端传来的 path 一旦被污染，等于给了外部一个执行入口）。校验不过即返回 false，由前端提示路径。
    /// </summary>
    private bool OpenReportFile(string? path)
    {
        try
        {
            string p = (path ?? "").Trim();
            if (p.Length == 0) p = LastExportPath;
            if (p.Length == 0 || !File.Exists(p)) return false;

            string dir = Path.GetFullPath(AppPaths.ResolveExportDir(_cfg)).TrimEnd('\\');
            string full = Path.GetFullPath(p);
            if (!full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) return false;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { TraceLog("OpenReportFile 失败: " + ex.Message); }
        return false;
    }

    // ---------- 初始化 ----------
    private async Task InitWebAsync()
    {
        TraceLog("InitWeb: start");
        try
        {
            string userData = Path.Combine(AppPaths.DataDir, "webview2");
            Directory.CreateDirectory(userData);
            var env = _envTask != null ? await _envTask : await CoreWebView2Environment.CreateAsync(null, userData);
            TraceLog("InitWeb: env ok, ensuring core");
            await _web.EnsureCoreWebView2Async(env);
            TraceLog("InitWeb: core ok");

            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.Settings.IsZoomControlEnabled = true;

            // postMessage 请求-响应
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _bridge.RunOnUi = a => BeginInvoke(a);
            TraceLog("InitWeb: message bridge ready");

            // 版本徽章联动：csproj <Version> → 程序集版本 → 文档创建前注入页面（外置/嵌入两条加载路径都生效）
            string appVer = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "";
            if (appVer.Length > 0)
                _ = _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync($"window.__APP_VER={JsonSerializer.Serialize(appVer)};");

            _web.CoreWebView2.NavigationStarting += (_, e) => TraceLog("NavigationStarting: " + (e.Uri ?? "?"));
            _web.CoreWebView2.SourceChanged += (_, _) => TraceLog("SourceChanged");

            // v1.6.6：导出下载统一落盘 + 告知路径。
            // 背景：界面上的「下载JSON / 下载HTML」走的是浏览器式 Blob 下载，而此前**从未注册**
            // 下载处理器 → 文件落到 WebView2 的默认目录（实测 Preferences 里 default_directory 为空），
            // 用户"根本找不到"。现在：① 固定落到 ExportDir ② 完成后 Toast + 前端提示完整路径
            // ③ 前端可一键打开该文件夹。
            _web.CoreWebView2.DownloadStarting += (_, e) =>
            {
                try
                {
                    string dir = AppPaths.ResolveExportDir(_cfg);
                    string safeName = SanitizeFileName(e.ResultFilePath);
                    string full = Path.Combine(dir, safeName);
                    // 同名不覆盖：追加 (1)(2)…，与浏览器习惯一致
                    full = UniquePath(full);
                    e.ResultFilePath = full;
                    e.Handled = true;          // 抑制 WebView2 默认下载弹窗/气泡
                    TraceLog("DownloadStarting: " + safeName + " -> " + full);

                    var op = e.DownloadOperation;
                    string target = full;
                    op.StateChanged += (_, _) =>
                    {
                        try
                        {
                            if (op.State == CoreWebView2DownloadState.Completed)
                            {
                                _bridge.Notify?.Invoke("已下载到: " + target);
                                try { BeginInvoke(new Action(() => RecordLastExport(target))); } catch { }
                            }
                            else if (op.State == CoreWebView2DownloadState.Interrupted)
                            {
                                _bridge.Notify?.Invoke("下载失败（" + op.InterruptReason + "）: " + target);
                                TraceLog("Download interrupted: " + op.InterruptReason);
                            }
                        }
                        catch (Exception ex) { TraceLog("Download 状态回调异常: " + ex.Message); }
                    };
                }
                catch (Exception ex)
                {
                    // 兜底：任何异常都不能让下载消失 —— 交回 WebView2 默认行为
                    TraceLog("DownloadStarting 处理异常（回退默认行为）: " + ex.Message);
                }
            };
            TraceLog("InitWeb: download handler ready");

            _bridge.Notify = msg =>
            {
                try
                {
                    string js = "toast(" + JsonSerializer.Serialize(msg) + ");";
                    _ = _web.CoreWebView2.ExecuteScriptAsync(js)
                          .ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                }
                catch { }
            };

            // 页面导航完成：桥接自检 + 启动首帧监视（CapturePreview 确认真实绘制后才撤开屏）
            _web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                TraceLog("NavigationCompleted fired: " + (_web.CoreWebView2.Source ?? "?"));
                _ = WatchFirstPaintAsync();
                await Task.Delay(400);
                await RunBridgeSelfTestAsync();
            };

            TraceLog("InitWeb: navigating");
            string webDir = Path.Combine(AppContext.BaseDirectory, "web");
            if (Directory.Exists(webDir) && File.Exists(Path.Combine(webDir, "index.html")))
            {
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", webDir,
                    CoreWebView2HostResourceAccessKind.Allow);
                TraceLog("InitWeb: mapped app.local -> " + webDir);
                _web.CoreWebView2.Navigate("https://app.local/index.html");
            }
            else
            {
                TraceLog("InitWeb: fallback NavigateToString");
                _web.CoreWebView2.NavigateToString(ReadIndexHtml());
            }
            TraceLog("InitWeb: done");
        }
        catch (Exception e)
        {
            TraceLog("InitWeb EXCEPTION: " + e);
            SafeReady();
        }
    }

    // ---------- postMessage 桥接 ----------
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.TryGetWebMessageAsString(); }
        catch (Exception ex) { TraceLog("MSG recv raw EXC: " + ex.Message); return; }
        int id; string path; JsonElement args = default;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
            path = doc.RootElement.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
            if (doc.RootElement.TryGetProperty("args", out var aEl) && aEl.ValueKind == JsonValueKind.Object)
                args = aEl.Clone();
        }
        catch (Exception ex) { TraceLog("MSG parse EXC: " + ex.Message + " raw=" + raw); return; }
        TraceLog($"MSG recv id={id} path={path}");

        // 后台执行（避免长操作冻结界面），完成后回 UI 线程投递响应
        _ = Task.Run(() =>
        {
            string resp;
            try { resp = Dispatch(path, args); }
            catch (Exception ex)
            {
                TraceLog("Dispatch " + path + " EXC: " + ex.Message);
                resp = JsonSerializer.Serialize(new { ok = false, msg = ex.Message });
            }
            string payload = "{\"id\":" + id + ",\"data\":" + JsonSerializer.Serialize(resp) + "}";
            void Post() { try { _web.CoreWebView2.PostWebMessageAsString(payload); } catch { } }
            BeginInvoke(Post);
            TraceLog($"MSG resp id={id} path={path}");
        });
    }

    private static string S(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int I(JsonElement a, string k, int def)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    private string Dispatch(string path, JsonElement a)
        => _routes.TryDispatch(path, a, out var resp)
            ? resp
            : JsonSerializer.Serialize(new { ok = false, msg = "未知接口 " + path });   // 未知即拒绝

    /// <summary>
    /// 构建中央路由表（单一事实源）：全部 /api/* 路由在此登记 —— path、参数名清单（供审计/文档）、
    /// 处理委托。参数提取（S/I/JsonText）与默认值与旧 switch 分发逐字节等价
    /// （含 /api/auto/start 的 6 参数、/api/auto/conf 的 minConf 存在性分支、/api/firstrun 的 done 分支）。
    /// </summary>
    private RouteRegistry BuildRouteTable()
    {
        var reg = new RouteRegistry();
        reg.Add("/api/defaultdir", Array.Empty<string>(), _ => _bridge.DefaultDir());
        reg.Add("/api/scan", new[] { "dir" }, a => _bridge.Scan(S(a, "dir")));
        reg.Add("/api/baseline", new[] { "dir" }, a => _bridge.SetBaseline(S(a, "dir")));
        reg.Add("/api/extract", new[] { "dir", "ids" }, a => _bridge.Extract(S(a, "dir"), JsonText(a, "ids", "[]")));
        reg.Add("/api/bank/answer", new[] { "dir", "paper" }, a => _bridge.BankAnswer(S(a, "dir"), S(a, "paper")));
        reg.Add("/api/bank/source", new[] { "f" }, a => JsonSerializer.Serialize(new { ok = true, html = _bridge.BankSource(S(a, "f")) }));
        reg.Add("/api/ocr", new[] { "base64" }, a => _bridge.Ocr(S(a, "base64")));
        reg.Add("/api/auto/windows", Array.Empty<string>(), _ => _bridge.AutoWindows());
        reg.Add("/api/auto/launch", Array.Empty<string>(), _ => _bridge.AutoLaunch());
        reg.Add("/api/auto/launchdom", Array.Empty<string>(), _ => _bridge.AutoLaunchDom());
        reg.Add("/api/auto/install", Array.Empty<string>(), _ => _bridge.InstallClient());
        reg.Add("/api/auto/pickdir", Array.Empty<string>(), _ => _bridge.PickClientDir());
        // ---- 导出落盘（v1.6.6：原来浏览器式下载不落盘、用户找不到文件）----
        // dir   : 当前导出目录（供前端展示/复制）
        // last  : 本会话最近一次成功导出的完整文件路径
        // open  : 在资源管理器中定位该文件（path 可缺省 → 用 last）
        // pick  : 让用户另选导出目录（无 UI 弹窗则返回 ok=false + 原因）
        // set   : 手动设置导出目录（dir 为空 = 恢复默认；含可写校验，坏值不入 config）
        // opendir: 打开当前导出目录（常驻入口）
        reg.Add("/api/download/conf", Array.Empty<string>(), _ => JsonSerializer.Serialize(new
        {
            ok = true,
            dir = AppPaths.ResolveExportDir(_cfg),
            last = LastExportPath ?? "",
            isDefault = string.IsNullOrWhiteSpace(_cfg.ExportDir)
        }));
        reg.Add("/api/download/last", Array.Empty<string>(), _ => JsonSerializer.Serialize(new
        {
            ok = LastExportPath != null && LastExportPath.Length > 0,
            last = LastExportPath ?? ""
        }));
        reg.Add("/api/download/open", new[] { "path" }, a => JsonSerializer.Serialize(new
        {
            ok = OpenExportFolder(S(a, "path")),
            last = LastExportPath ?? ""
        }));
        reg.Add("/api/download/pick", Array.Empty<string>(), _ => _bridge.PickExportDir());
        // set   : 手动设置导出目录（前端输入框/粘贴路径；传空 = 恢复默认）
        reg.Add("/api/download/set", new[] { "dir" }, a => _bridge.SetExportDirFromText(S(a, "dir")));
        // opendir: 直接打开当前导出目录（常驻入口，不依赖"最近一次导出"）
        reg.Add("/api/download/opendir", Array.Empty<string>(), _ => _bridge.OpenExportDirNow());
        // ---- 作文报告（手动触发）----
        // extract : 附着客户端里已打开的报告页，摘遮罩 + 揭示折叠区块 → 导出 .txt/.html 到导出目录
        // open    : 用系统默认程序打开报告 HTML（**只允许导出目录内的文件**，避免该接口变成任意路径启动器）
        reg.Add("/api/report/extract", Array.Empty<string>(), _ => _bridge.ReportExtract());
        reg.Add("/api/report/open", new[] { "path" }, a => JsonSerializer.Serialize(new
        {
            ok = OpenReportFile(S(a, "path")),
            last = LastExportPath ?? ""
        }));
        // ---- 安装包清理（手动触发）----
        reg.Add("/api/client/installerinfo", Array.Empty<string>(), _ => _bridge.InstallerInfo());
        reg.Add("/api/client/installerclear", Array.Empty<string>(), _ => _bridge.ClearInstallers());
        reg.Add("/api/auto/start", new[] { "title", "proc", "intervalMs", "minConf", "dir", "engine" },
            a => _bridge.AutoStart(S(a, "title"), S(a, "proc"), I(a, "intervalMs", 0), S(a, "minConf"), S(a, "dir"), S(a, "engine")));
        reg.Add("/api/auto/stop", Array.Empty<string>(), _ => _bridge.AutoStop());
        reg.Add("/api/auto/status", Array.Empty<string>(), _ => _bridge.AutoStatus());
        reg.Add("/api/auto/conf", new[] { "minConf", "intervalMs", "intervalMsDom" }, a =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty("minConf", out _)
                ? _bridge.SaveConf(S(a, "minConf"), I(a, "intervalMs", 20000), I(a, "intervalMsDom", 0))
                : _bridge.GetConf());
        // ---- 无痕下载接管（hosts 劫持 + 本地 HTTPS · 抓题库答案）----
        reg.Add("/api/mc/start", Array.Empty<string>(), _ => _bridge.McStart());
        reg.Add("/api/mc/stop", Array.Empty<string>(), _ => _bridge.McStop());
        reg.Add("/api/mc/status", Array.Empty<string>(), _ => _bridge.McStatus());
        reg.Add("/api/mc/list", Array.Empty<string>(), _ => _bridge.McList());
        reg.Add("/api/mc/answers", Array.Empty<string>(), _ => _bridge.McAnswers());
        reg.Add("/api/mc/cache", Array.Empty<string>(), _ => _bridge.McCache());
        reg.Add("/api/mc/clear", Array.Empty<string>(), _ => _bridge.McClear());
        // ---- 密钥 ----
        reg.Add("/api/keys", Array.Empty<string>(), _ => _bridge.Keys());
        reg.Add("/api/keys/add", new[] { "key" }, a => _bridge.AddKey(S(a, "key")));
        reg.Add("/api/keys/del", new[] { "key" }, a => _bridge.DelKey(S(a, "key")));
        reg.Add("/api/keys/discover", new[] { "dir", "exe" }, a => _bridge.DiscoverKeys(S(a, "dir"), S(a, "exe")));
        reg.Add("/api/keys/hunt", new[] { "dir" }, a => _bridge.HuntKeys(S(a, "dir")));
        reg.Add("/api/keys/dumpclient", Array.Empty<string>(), _ => _bridge.DumpClient());
        reg.Add("/api/firstrun", new[] { "done" }, a =>
        {
            if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("done", out _))
            {
                _cfg.FirstRunDone = true; AppPaths.Save(_cfg);
                return JsonSerializer.Serialize(new { ok = true });
            }
            return JsonSerializer.Serialize(new { ok = true, show = !_cfg.FirstRunDone });
        });
        return reg;
    }

    private static string JsonText(JsonElement a, string k, string def)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) ? v.GetRawText() : def;

    // ---------- 桥接自检 ----------
    private async Task RunBridgeSelfTestAsync()
    {
        string log = Path.Combine(AppPaths.DataDir, "bridge.log");
        LogFiles.AppendCapped(log, $"[{DateTime.Now:HH:mm:ss}] selftest start\r\n");

        // 启动自检（结果写 window.__st），随后轮询读取
        const string js = @"(function(){
            if (window.__stDone) return 'already';
            window.__stDone = true;
            function stCall(path, args){
                return new Promise(function(resolve, reject){
                    var h = function(ev){
                        var m; try { m = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data; } catch(_){ return; }
                        if (!m || m.id !== 990001) return;
                        window.chrome.webview.removeEventListener('message', h);
                        try { resolve(JSON.parse(m.data)); } catch(_){ reject(new Error('bad payload')); }
                    };
                    window.chrome.webview.addEventListener('message', h);
                    window.chrome.webview.postMessage(JSON.stringify({id: 990001, path: path, args: args || {}}));
                    setTimeout(function(){ window.chrome.webview.removeEventListener('message', h); reject(new Error('timeout')); }, 12000);
                });
            }
            (function(){
                var out = {ok:true, defaultDir:'', keysCount:0, autoWindows:0, scanCount:0, bankCount:0, firstrunApi:'', error:''};
                stCall('/api/defaultdir', {}).then(function(j){
                    out.defaultDir = JSON.stringify(j); var dir=''; try{ dir=j.dir||''; }catch(_){}
                    return stCall('/api/keys', {}).then(function(j2){ out.keysCount = j2.count||0;
                        return stCall('/api/auto/windows', {});
                    }).then(function(j3){ out.autoWindows = (j3.windows||[]).length;
                        return stCall('/api/scan', {dir: dir});
                    }).then(function(j4){ out.scanCount = j4.count||0;
                        return stCall('/api/bank/answer', {dir: dir, paper: '1. What is your name?\nA. Tom  B. Jerry'});
                    }).then(function(j5){ out.bankCount = j5.bankCount||0;
                        return stCall('/api/firstrun', {});
                    }).then(function(j6){ out.firstrunApi = JSON.stringify(j6);
                        try { window.checkFirstRun && window.checkFirstRun(); } catch(e){ window.__fr = 'call-err:' + e.message; }
                        return JSON.stringify(out); });
                }).catch(function(e){ out.ok=false; out.error=(e && e.message) ? e.message : String(e); return JSON.stringify(out); })
                  .then(function(s){ window.__st = s; });
            })();
            return 'started';
        })()";
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync(js);

            // 轮询读取结果（最多 20 秒）；同时带回首次启动检查标记 window.__fr
            string inner = "", frTag = "";
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(500);
                string r = await _web.CoreWebView2.ExecuteScriptAsync("(window.__st || 'pending') + '||fr:' + (window.__fr || 'none')");
                string v = JsonSerializer.Deserialize<string>(r) ?? "pending";
                int sep = v.IndexOf("||fr:", StringComparison.Ordinal);
                if (sep > 0) frTag = v[(sep + 5)..];
                string st = sep > 0 ? v[..sep] : v;
                if (st != "pending" && st != "already") { inner = st; break; }
            }
            if (inner.Length == 0) inner = "{\"ok\":false,\"error\":\"selftest timeout\"}";
            inner = inner.TrimEnd('}') + ",\"firstRun\":\"" + frTag.Replace("\"", "'") + "\"}";
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "bridge_selftest.json"), inner);
            LogFiles.AppendCapped(log, $"[{DateTime.Now:HH:mm:ss}] selftest done: {inner}\r\n");
            if (inner.Contains("\"ok\":false"))
                _bridge.Notify?.Invoke("界面与后端桥接异常：" + inner);
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "bridge_selftest.json"), "{\"ok\":false,\"error\":\"selftest-fatal: " + e.Message.Replace("\"", "'") + "\"}"); } catch { }
            LogFiles.AppendCapped(log, $"[{DateTime.Now:HH:mm:ss}] selftest fatal: {e}\r\n");
        }
    }

    private static string ReadIndexHtml()
    {
        // 优先读外置文件（开发期可直接改 web/index.html），否则回退嵌入资源
        try
        {
            string ext = Path.Combine(AppContext.BaseDirectory, "web", "index.html");
            if (File.Exists(ext)) return File.ReadAllText(ext, System.Text.Encoding.UTF8);
        }
        catch { }
        try
        {
            var asm = typeof(MainForm).Assembly;
            string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("web.index.html", StringComparison.OrdinalIgnoreCase));
            if (name != null)
                using (var sr = new StreamReader(asm.GetManifestResourceStream(name)!))
                    return sr.ReadToEnd();
        }
        catch { }
        return "<html><body><p>界面文件缺失（web/index.html）</p></body></html>";
    }
}
