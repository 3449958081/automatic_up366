using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TxwExtract.Core;

namespace TxwExtract.UI;

/// <summary>
/// WebView2 → C# 后端桥接。通过 AddHostObjectToScript 暴露为 window.chrome.webview.hostObjects.sync.txw，
/// 前端以同步调用方式获取 JSON 字符串（方法签名对齐原版 /api/* 接口的请求/响应结构）。
/// </summary>
[ComVisible(true)]
public sealed class UiBridge
{
    private readonly AppConfig _cfg;
    private readonly AutoService _auto = new();
    private readonly MitmProxy _mitm = new();
    /// <summary>在 UI 线程执行动作（MainForm 注入 BeginInvoke）。</summary>
    public Action<Action>? RunOnUi { get; set; }
    private static readonly JsonSerializerOptions JOpts = new() { PropertyNamingPolicy = null };

    /// <summary>异步任务完成时通知前端（MainForm 注入：执行 toast）。</summary>
    public Action<string>? Notify { get; set; }

    /// <summary>弹窗宿主窗口（MainForm 注入），保证目录选择框/确认框显示在最前，不被 WebView2 遮挡。</summary>
    public IWin32Window? Owner { get; set; }

    public UiBridge(AppConfig cfg)
    {
        _cfg = cfg;
    }

    static string J(object o) => JsonSerializer.Serialize(o);
    static string Err(string msg) => J(new { ok = false, msg });

    /// <summary>桥接方法异常诊断日志（%LOCALAPPDATA%\TxwExtract\bridge.log），便于定位"后端启动失败"类问题。</summary>
    static void LogErr(string tag, Exception e)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "bridge.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}: {e}\r\n");
        }
        catch { }
    }

    // ---------- 目录 ----------
    public string DefaultDir()
    {
        string dir = @"D:\Up366StudentFiles\resources";
        try
        {
            var cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "up366", "config.json");
            if (File.Exists(cfgPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                if (doc.RootElement.TryGetProperty("saveDir", out var sd) && sd.GetString() is string s && s.Length > 0)
                    dir = Path.Combine(s, "resources");
            }
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(_cfg.ScanDir) && Directory.Exists(_cfg.ScanDir)) dir = _cfg.ScanDir;
        return J(new { dir });
    }

    // ---------- 扫描 / 提取 ----------
    public string Scan(string dir)
    {
        try
        {
            var r = ScanService.Scan(dir);
            return J(new
            {
                dir = r.Dir, count = r.Count, hasBaseline = r.HasBaseline,
                courses = r.Courses.Select(c => new
                {
                    id = c.Id, kind = c.Kind, rootDir = c.RootDir, mtime = c.Mtime, mtimeMs = c.MtimeMs,
                    size = c.Size, hasPaper = c.HasPaper, hasAnswers = c.HasAnswers, extractable = c.Extractable,
                    qCount = c.QCount, choiceCount = c.ChoiceCount, courseType = c.CourseType,
                    title = c.Title, parseError = c.ParseError, isNew = c.IsNew,
                }).ToList(),
            });
        }
        catch (Exception e) { LogErr("Scan", e); return Err(e.Message); }
    }

    public string SetBaseline(string dir)
    {
        try
        {
            var r = ScanService.Scan(dir);
            ScanService.SaveBaseline(dir, r.Courses);
            int set = r.Courses.Count;
            return J(new { ok = true, set });
        }
        catch (Exception e) { LogErr("SetBaseline", e); return Err(e.Message); }
    }

    public string Extract(string dir, string idsJson)
    {
        try
        {
            var ids = (JsonSerializer.Deserialize<List<string>>(idsJson) ?? new()).ToHashSet();
            var byId = ScanService.Scan(dir).Courses.ToDictionary(c => c.Id);
            var outList = new List<object>();
            foreach (var id in ids)
            {
                if (!byId.TryGetValue(id, out var c)) { outList.Add(new { id, error = "未找到该课程" }); continue; }
                try
                {
                    var rec = ScanService.Extract(c);
                    if (rec.Error.Length > 0) { outList.Add(new { id, error = rec.Error }); continue; }
                    var qs = rec.Questions.Select(q => new
                    {
                        no = q.No, type = q.Type,
                        material = c.Kind == "flipbook" ? "" : q.Material,
                        src = c.Kind == "flipbook" ? q.Material : "",
                        qt = q.Qt,
                        options = q.Options.Select(o => new { id = o.Item1, text = o.Item2 }).ToList(),
                        ans = q.Ans, ansText = q.AnsText, analysis = q.Analysis, isListen = q.IsListen,
                    }).ToList();
                    outList.Add(new
                    {
                        id, downloadTime = c.Mtime, title = rec.Title, courseType = rec.CourseType,
                        questions = qs, listenCount = rec.ListenCount, choiceCount = rec.ChoiceCount,
                        missingCount = rec.MissingCount,
                    });
                }
                catch (Exception e) { outList.Add(new { id, error = e.Message }); }
            }
            return J(new { courses = outList });
        }
        catch (Exception e) { LogErr("Extract", e); return Err(e.Message); }
    }

    /// <summary>题库源文件查看：返回一个独立 HTML 页面文本（前端用 modal/iframe 展示）。</summary>
    public string BankSource(string f)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) return "<html><body><p>文件不存在: " + Esc(f) + "</p></body></html>";
            string dec = CryptoService.DecryptFile(f);
            string bn = Path.GetFileName(f);
            string pre = Esc(dec);
            return "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>" + Esc(bn) + "</title>"
                + "<style>body{font-family:system-ui,'Microsoft YaHei',sans-serif;margin:0;color:#1b2334;background:#eef1f7;line-height:1.55}"
                + "h2{margin:16px 22px 4px;font-size:15px}pre{background:#101828;color:#9cdcfe;padding:12px;border-radius:10px;font-size:12px;overflow:auto;max-height:70vh;white-space:pre-wrap;word-break:break-all;margin:10px 22px 22px}</style></head><body>"
                + "<h2>" + Esc(bn) + "（解密后）</h2><pre>" + pre + "</pre></body></html>";
        }
        catch (Exception e) { return "<html><body><p>解密失败: " + Esc(e.Message) + "</p></body></html>"; }
    }

    static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ---------- 搜题 ----------
    public string BankAnswer(string dir, string paper)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(dir)) BankService.Build(dir);
            else if (BankService.Count == 0 && !string.IsNullOrWhiteSpace(_cfg.ScanDir)) BankService.Build(_cfg.ScanDir);
            var qs = Matcher.ParsePaperText(paper ?? "");
            var outQ = qs.Select(q =>
            {
                var r = Matcher.MatchBank(q.Qt, q.Opts);
                return new
                {
                    no = q.No, qt = q.Qt, opts = q.Opts,
                    conf = r.Conf,
                    src = r.Best?.Src ?? "", srcFile = r.Best?.SrcFile ?? "", srcQt = r.Best?.Qt ?? "",
                    ans = r.Best?.Ans ?? "", bankAnsText = r.BankAnsText,
                    targetAns = r.TargetAns, targetText = r.TargetText, mapped = r.Mapped,
                };
            }).ToList();
            return J(new { bankCount = BankService.Count, questions = outQ });
        }
        catch (Exception e) { LogErr("BankAnswer", e); return Err(e.Message); }
    }

    // ---------- OCR ----------
    public string Ocr(string base64)
    {
        try
        {
            string b64 = (base64 ?? "").Trim();
            var m = System.Text.RegularExpressions.Regex.Match(b64, @"^data:[^;]+;base64,(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success) b64 = m.Groups[1].Value;
            if (b64.Length == 0) return Err("缺少 base64 图片数据");
            byte[] buf = Convert.FromBase64String(b64);
            if (buf.Length < 100 || buf.Length > 30 * 1024 * 1024) return Err("图片大小异常");
            using var ms = new MemoryStream(buf);
            using var bmp = new Bitmap(ms);
            var lines = OcrService.RecognizeAsync(bmp).GetAwaiter().GetResult();
            var joined = string.Join("\r\n", lines.Select(l => l.Text));
            string engine = OcrService.Available ? "内置 Tesseract" : "不可用";
            return J(new { ok = true, count = lines.Count, engine, joined });
        }
        catch (Exception e)
        {
            string detail = e.Message + (e.InnerException != null ? " | " + e.InnerException.Message : "");
            LogErr("Ocr", e);
            return Err("OCR 失败: " + detail);
        }
    }

    // ---------- 自动回答 ----------
    public string AutoWindows()
    {
        try
        {
            var self = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            var wins = WinApi.EnumTopLevelWindows()
                .Where(w => !w.Title.Contains("天学网答案提取", StringComparison.Ordinal)
                            && !w.ProcessName.Equals(self, StringComparison.OrdinalIgnoreCase))
                .GroupBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
                .Select(g => WinApi.LargestWindow(g))          // 同名多窗口只留面积最大者（主窗口）
                .Where(w => w != null)
                .OrderBy(w => w!.Title, StringComparer.OrdinalIgnoreCase)
                .Select(w => new { title = w!.Title, pid = w.Pid, proc = w.ProcessName })
                .ToList();
            return J(new { windows = wins });
        }
        catch (Exception e) { LogErr("AutoWindows", e); return Err(e.Message); }
    }

    public string AutoLaunch()
    {
        try
        {
            var exe = ClientService.Find(_cfg);
            if (exe == null)
            {
                // 客户端进程在运行但拿不到路径（权限/架构差异）→ 不是失败，提示刷新窗口即可
                if (ClientService.AnyRunning())
                    return J(new { ok = true, msg = "检测到天学网客户端正在运行，请点【刷新窗口】选择目标窗口" });
                return J(new { ok = false, needInstall = true, msg = "未检测到天学网客户端" });
            }
            var msg = ClientService.Launch(exe);
            _cfg.ClientExe = exe; AppPaths.Save(_cfg);
            return J(new { ok = true, msg });
        }
        catch (Exception e) { LogErr("AutoLaunch", e); return Err("后端启动失败: " + e.Message); }
    }

    /// <summary>
    /// 手动自选客户端位置：弹目录选择框定位客户端 exe 并写入配置（满足"让用户自己选择安装位置"）。
    /// </summary>
    public string PickClientDir()
    {
        try
        {
            string? dir;
            if (RunOnUi != null)
            {
                var tcs = new TaskCompletionSource<string?>();
                RunOnUi(() => tcs.TrySetResult(PickDirDialog()));
                dir = tcs.Task.GetAwaiter().GetResult();
            }
            else
            {
                dir = PickDirDialog();
            }
            if (string.IsNullOrWhiteSpace(dir)) return J(new { ok = true, msg = "已取消" });

            var exe = ClientService.FindIn(dir);
            if (exe == null) return Err("所选目录中未找到客户端 exe（up366.exe / 天学网学生端.exe 等）");
            _cfg.ClientExe = exe; AppPaths.Save(_cfg);
            return J(new { ok = true, msg = "已记录客户端位置：" + exe + "（下次自动使用）" });
        }
        catch (Exception e) { LogErr("PickClientDir", e); return Err(e.Message); }
    }

    private string? PickDirDialog()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择天学网客户端所在目录（程序会在其中查找 up366.exe 等，并记住该位置）",
            UseDescriptionForTitle = true,
            SelectedPath = !string.IsNullOrWhiteSpace(_cfg.ClientExe)
                ? Path.GetDirectoryName(_cfg.ClientExe)!
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        return dlg.ShowDialog(Owner) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    /// <summary>
    /// 首次启用引导：下载官方安装包并安装到用户自选目录（与程序本体分立）。
    /// 同步方法里只发起后台任务并立即返回；目录选择在 UI 线程弹窗，进度通过 Notify 通知前端。
    /// </summary>
    public string InstallClient()
    {
        _ = Task.Run(() => InstallClientAsync());
        return J(new { ok = true, msg = "已在后台准备安装向导…" });
    }

    private void Toast(string msg) => RunOnUi?.Invoke(() => Notify?.Invoke(msg));

    private async Task InstallClientAsync()
    {
        try
        {
            // 1) 确认（UI 线程，带 Owner 确保弹窗在最前）
            bool yes = false;
            if (RunOnUi != null)
            {
                var tcs = new TaskCompletionSource<bool>();
                RunOnUi(() =>
                {
                    var r = MessageBox.Show(Owner!,
                        "未检测到天学网客户端。\r\n\r\n是否现在自动下载官方安装包（约 250 MB）并安装？\r\n（接下来会请你选择客户端的安装目录，与程序本体分开存放）",
                        "首次使用 · 安装天学网客户端", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    tcs.TrySetResult(r == DialogResult.Yes);
                });
                yes = await tcs.Task;
            }
            else yes = true;
            if (!yes) { Toast("已取消安装"); return; }

            // 2) 目录选择（UI 线程，带 Owner）
            string? dir = null;
            if (RunOnUi != null)
            {
                var tcs = new TaskCompletionSource<string?>();
                RunOnUi(() =>
                {
                    using var dlg = new FolderBrowserDialog
                    {
                        Description = "选择天学网客户端的安装目录（建议与程序分开存放，例如 D:\\Up366StudentFiles）",
                        UseDescriptionForTitle = true,
                        SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    };
                    tcs.TrySetResult(dlg.ShowDialog(Owner) == DialogResult.OK ? dlg.SelectedPath : null);
                });
                dir = await tcs.Task;
            }
            if (string.IsNullOrWhiteSpace(dir)) { Toast("已取消安装"); return; }

            Toast("正在获取官方安装包信息…");
            var info = await ClientService.FetchLatestAsync();
            if (info == null) { Toast("获取安装包信息失败（请检查网络）"); return; }

            Toast("正在下载官方安装包（" + (info.Size / 1048576) + " MB）…");
            var setup = await ClientService.DownloadInstallerAsync(info, s => Toast(s));
            if (setup == null) { Toast("下载失败"); return; }

            Toast("正在静默安装到 " + dir + " …");
            var installed = await ClientService.InstallToAsync(setup, dir, s => Toast(s));
            var exe = installed ?? await Task.Run(() => ClientService.FindIn(dir));
            if (exe == null)
            {
                Toast("安装未完成，请在弹出的安装向导中完成安装后点【刷新窗口】");
                return;
            }
            _cfg.ClientExe = exe; AppPaths.Save(_cfg);
            Toast("客户端安装完成 ✅ " + exe);
        }
        catch (Exception e) { Toast("安装客户端失败: " + e.Message); }
    }

    public string AutoStart(string title, string proc, int intervalMs, string minConf, string dir)
    {
        var (ok, msg) = _auto.Start(title ?? "", proc ?? "", intervalMs, minConf ?? "mid", dir ?? "");
        return J(new { ok, msg });
    }

    public string AutoStop() { _auto.Stop(); return J(new { ok = true, msg = "停止中…" }); }

    public string AutoStatus() => J(_auto.Status());

    public string GetConf() => J(new { ok = true, minConf = _cfg.MinConf, intervalMs = _cfg.IntervalMs });

    public string SaveConf(string minConf, int intervalMs)
    {
        try
        {
            _cfg.MinConf = ConfLevelEx.Parse(minConf).Key();
            if (intervalMs >= 3000 && intervalMs <= 120000) _cfg.IntervalMs = intervalMs;
            AppPaths.Save(_cfg);
            return J(new { ok = true, msg = "已保存：最低置信度=" + ConfLevelEx.Parse(_cfg.MinConf).Name() + "，间隔=" + _cfg.IntervalMs + "ms" });
        }
        catch (Exception e) { LogErr("SaveConf", e); return Err(e.Message); }
    }

    // ---------- 抓包 ----------
    public string CapStart() => J(new { ok = true, msg = _mitm.Start(8899) });
    public string CapStop() => J(new { ok = true, msg = _mitm.Stop() });

    public string CapLaunch()
    {
        try
        {
            var exe = ClientService.Find(_cfg);
            if (exe == null) return Err("未检测到天学网客户端");
            var msg = ClientService.Launch(exe);
            _cfg.ClientExe = exe; AppPaths.Save(_cfg);
            msg += "｜请把系统代理指向 127.0.0.1:8899 后重开客户端（仅记录不篡改流量）";
            return J(new { ok = true, msg });
        }
        catch (Exception e) { LogErr("CapLaunch", e); return Err(e.Message); }
    }

    public string CapLog()
    {
        try
        {
            string log = "(代理未运行)";
            if (File.Exists(_mitm.LogPath))
            {
                using var fs = new FileStream(_mitm.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                log = sr.ReadToEnd();
                if (log.Length > 12000) log = log[^12000..];
            }
            return J(new { log });
        }
        catch (Exception e) { LogErr("CapLog", e); return Err(e.Message); }
    }

    public string CapTest() => J(new { ok = true, msg = _mitm.Running ? ("代理运行中（端口 " + _mitm.Port + "）") : "代理未运行" });

    // ---------- 密钥 ----------
    public string Keys()
    {
        var custom = CryptoService.LoadCustomKeys();
        var all = CryptoService.BuiltinKeys.Concat(custom).Distinct().ToList();
        var activeKey = CryptoService.ActiveKey;   // 真实"当前生效"密钥（最近一次解密成功所用）
        return J(new
        {
            count = all.Count,
            keys = all.Select((k, i) => new
            {
                i, key = k,
                masked = k.Length > 10 ? k[..6] + "…" + k[^4..] : k,
                builtin = CryptoService.BuiltinKeys.Contains(k),
                active = k == activeKey,
            }).ToList(),
        });
    }

    public string AddKey(string key)
    {
        string k = (key ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(k, @"^[A-Za-z0-9+/]{22}==$"))
            return Err("密钥格式不对：应为 24 位 base64（解码后 16 字节）");
        if (CryptoService.BuiltinKeys.Contains(k))
            return J(new { ok = true, msg = "该密钥为内置密钥，已存在" });
        var custom = CryptoService.LoadCustomKeys();
        if (custom.Contains(k)) return J(new { ok = true, msg = "该密钥已存在，无需重复添加" });
        custom.Add(k);
        CryptoService.SaveCustomKeys(custom);
        CryptoService.Reload();
        return J(new { ok = true, msg = "已添加（当前共 " + (CryptoService.BuiltinKeys.Count + custom.Count) + " 个密钥）" });
    }

    public string DelKey(string key)
    {
        if (CryptoService.BuiltinKeys.Contains(key)) return Err("内置密钥不可删除");
        var custom = CryptoService.LoadCustomKeys();
        if (!custom.Remove(key)) return Err("未找到该密钥");
        CryptoService.SaveCustomKeys(custom);
        CryptoService.Reload();
        return J(new { ok = true, msg = "已删除（剩余 " + (CryptoService.BuiltinKeys.Count + custom.Count) + " 个密钥）" });
    }

    public string DiscoverKeys(string dir, string exe)
    {
        try
        {
            string clientExe = exe;
            if (string.IsNullOrWhiteSpace(clientExe)) clientExe = ClientService.Find(_cfg) ?? "";
            if (string.IsNullOrWhiteSpace(clientExe)) return Err("未找到客户端 exe——请手动填写路径或先启动客户端");
            string scanDir = dir;
            if (string.IsNullOrWhiteSpace(scanDir)) scanDir = _cfg.ScanDir;
            if (string.IsNullOrWhiteSpace(scanDir) || !Directory.Exists(scanDir))
            {
                var d = JsonDocument.Parse(DefaultDir()).RootElement.GetProperty("dir").GetString() ?? "";
                if (Directory.Exists(d)) scanDir = d;
            }
            var samples = new List<string>();
            if (Directory.Exists(scanDir))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(scanDir, "*.u3enc", SearchOption.AllDirectories).Take(200))
                        samples.Add(f);
                }
                catch { }
            }
            if (samples.Count == 0) return Err("数据目录下未找到 .u3enc 样本文件");
            var found = CryptoService.DiscoverKeys(clientExe, samples);
            if (found.Count > 0)
            {
                var custom = CryptoService.LoadCustomKeys();
                foreach (var k in found) if (!custom.Contains(k)) custom.Add(k);
                CryptoService.SaveCustomKeys(custom);
                CryptoService.Reload();
                return J(new { ok = true, found = true, msg = "找到 " + found.Count + " 个新密钥：" + string.Join("、", found.Select(x => x[..6] + "…")) });
            }
            return J(new { ok = true, found = false, msg = "未在客户端中发现新密钥（内置密钥可能仍然有效）" });
        }
        catch (Exception e) { LogErr("DiscoverKeys", e); return Err(e.Message); }
    }
}
