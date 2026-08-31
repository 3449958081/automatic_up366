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
        FormClosing += (_, _) => { try { _web.Dispose(); } catch { } };
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
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxwExtract");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "bridge.log"), $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
            File.AppendAllText(Path.Combine(dir, "startup_state.txt"), $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        }
        catch { }
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

            _web.CoreWebView2.NavigationStarting += (_, e) => TraceLog("NavigationStarting: " + (e.Uri ?? "?"));
            _web.CoreWebView2.SourceChanged += (_, _) => TraceLog("SourceChanged");

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
    {
        switch (path)
        {
            case "/api/defaultdir": return _bridge.DefaultDir();
            case "/api/scan": return _bridge.Scan(S(a, "dir"));
            case "/api/baseline": return _bridge.SetBaseline(S(a, "dir"));
            case "/api/extract": return _bridge.Extract(S(a, "dir"), JsonText(a, "ids", "[]"));
            case "/api/bank/answer": return _bridge.BankAnswer(S(a, "dir"), S(a, "paper"));
            case "/api/bank/source": return JsonSerializer.Serialize(new { ok = true, html = _bridge.BankSource(S(a, "f")) });
            case "/api/ocr": return _bridge.Ocr(S(a, "base64"));
            case "/api/auto/windows": return _bridge.AutoWindows();
            case "/api/auto/launch": return _bridge.AutoLaunch();
            case "/api/auto/install": return _bridge.InstallClient();
            case "/api/auto/pickdir": return _bridge.PickClientDir();
            case "/api/auto/start": return _bridge.AutoStart(S(a, "title"), S(a, "proc"), I(a, "intervalMs", 20000), S(a, "minConf"), S(a, "dir"));
            case "/api/auto/stop": return _bridge.AutoStop();
            case "/api/auto/status": return _bridge.AutoStatus();
            case "/api/auto/conf":
                return a.ValueKind == JsonValueKind.Object && a.TryGetProperty("minConf", out _)
                    ? _bridge.SaveConf(S(a, "minConf"), I(a, "intervalMs", 20000))
                    : _bridge.GetConf();
            case "/api/capture/start": return _bridge.CapStart();
            case "/api/capture/stop": return _bridge.CapStop();
            case "/api/capture/launch": return _bridge.CapLaunch();
            case "/api/capture/log": return _bridge.CapLog();
            case "/api/capture/test": return _bridge.CapTest();
            case "/api/keys": return _bridge.Keys();
            case "/api/keys/add": return _bridge.AddKey(S(a, "key"));
            case "/api/keys/del": return _bridge.DelKey(S(a, "key"));
            case "/api/keys/discover": return _bridge.DiscoverKeys(S(a, "dir"), S(a, "exe"));
            case "/api/firstrun":
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("done", out _))
                {
                    _cfg.FirstRunDone = true; AppPaths.Save(_cfg);
                    return JsonSerializer.Serialize(new { ok = true });
                }
                return JsonSerializer.Serialize(new { ok = true, show = !_cfg.FirstRunDone });
            default: return JsonSerializer.Serialize(new { ok = false, msg = "未知接口 " + path });
        }
    }

    private static string JsonText(JsonElement a, string k, string def)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) ? v.GetRawText() : def;

    // ---------- 桥接自检 ----------
    private async Task RunBridgeSelfTestAsync()
    {
        string log = Path.Combine(AppPaths.DataDir, "bridge.log");
        try { File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] selftest start\r\n"); } catch { }

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
            try { File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] selftest done: {inner}\r\n"); } catch { }
            if (inner.Contains("\"ok\":false"))
                _bridge.Notify?.Invoke("界面与后端桥接异常：" + inner);
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "bridge_selftest.json"), "{\"ok\":false,\"error\":\"selftest-fatal: " + e.Message.Replace("\"", "'") + "\"}"); } catch { }
            try { File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss}] selftest fatal: {e}\r\n"); } catch { }
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
