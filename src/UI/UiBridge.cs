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

    /// <summary>在 UI 线程执行动作（MainForm 注入 BeginInvoke）。</summary>
    public Action<Action>? RunOnUi { get; set; }
    private static readonly JsonSerializerOptions JOpts = new() { PropertyNamingPolicy = null };

    /// <summary>异步任务完成时通知前端（MainForm 注入：执行 toast）。</summary>
    public Action<string>? Notify { get; set; }

    /// <summary>报告成功落盘后登记"最近一次导出路径"（MainForm 注入 RecordLastExport），供「打开所在文件夹」使用。</summary>
    public Action<string>? RecordExport { get; set; }

    /// <summary>弹窗宿主窗口（MainForm 注入），保证目录选择框/确认框显示在最前，不被 WebView2 遮挡。</summary>
    public IWin32Window? Owner { get; set; }

    public UiBridge(AppConfig cfg)
    {
        _cfg = cfg;
        // 任务三：启动回读持久化的抓包答案缓存，并入内存题库（后续 Build 重建会自动重放，用户不删就不丢）
        try
        {
            var pairs = _mc.LoadPersistedPairs();
            if (pairs.Count > 0) BankService.MergeExternal(pairs);
        }
        catch { }

        // ① 进程退出兜底：无论正常退出 / Environment.Exit / 主线程结束，都确保 hosts 还原
        //    （旧缺陷：关窗口只 Dispose WebView2，hosts 劫持残留 → 客户端下载全部 ERR_CONNECTION_REFUSED）
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _mc.Dispose(); } catch { } };
        // ② 启动自愈：上次异常退出残留的劫持块（本机 443 无人监听）在此立刻清除，
        //    用户不必手动点"停止接管"来还原。
        try { _mc.RecoverStaleMarker(); } catch { }
    }

    static string J(object o) => JsonSerializer.Serialize(o);
    static string Err(string msg) => J(new { ok = false, msg });

    /// <summary>桥接方法异常诊断日志（%LOCALAPPDATA%\TxwExtract\bridge.log，超 512KB 截尾），便于定位"后端启动失败"类问题。
    /// 同时写入本次运行的会话日志（logs\session.log，每次启动自动清除），便于单文件排查。</summary>
    static void LogErr(string tag, Exception e)
    {
        LogFiles.AppendCapped(Path.Combine(AppPaths.DataDir, "bridge.log"),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}: {e}\r\n");
        LogFiles.Session("err  | " + tag + ": " + e);
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
        // 用 ConfigClientExe（已排除安装包）而不是裸 _cfg.ClientExe：坏值会让默认目录指向 installer 目录
        var remembered = ClientService.ConfigClientExe(_cfg);
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择天学网客户端所在目录（程序会在其中查找 up366.exe 等，并记住该位置）",
            UseDescriptionForTitle = true,
            SelectedPath = !string.IsNullOrWhiteSpace(remembered)
                ? Path.GetDirectoryName(remembered)!
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        return dlg.ShowDialog(Owner) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    /// <summary>
    /// （v1.6.6）让用户指定「导出下载」的落盘目录。写入 config.json 的 ExportDir 后立即生效。
    /// 传空字符串表示恢复默认（Downloads\TxwExtract）。
    /// </summary>
    public string PickExportDir()
    {
        try
        {
            string cur = AppPaths.ResolveExportDir(_cfg);
            string? dir;
            if (RunOnUi != null)
            {
                var tcs = new TaskCompletionSource<string?>();
                RunOnUi(() =>
                {
                    using var dlg = new FolderBrowserDialog
                    {
                        Description = "选择答案导出的保存目录（下载的 JSON / HTML 会固定保存到这里）",
                        UseDescriptionForTitle = true,
                        SelectedPath = Directory.Exists(cur) ? cur : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    };
                    tcs.TrySetResult(dlg.ShowDialog(Owner) == DialogResult.OK ? dlg.SelectedPath : null);
                });
                dir = tcs.Task.GetAwaiter().GetResult();
            }
            else
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = cur };
                dir = dlg.ShowDialog(Owner) == DialogResult.OK ? dlg.SelectedPath : null;
            }
            if (string.IsNullOrWhiteSpace(dir)) return J(new { ok = false, msg = "已取消", dir = cur });

            return SetExportDir(dir);
        }
        catch (Exception e) { LogErr("PickExportDir", e); return Err(e.Message); }
    }

    /// <summary>
    /// 手动设置导出目录（v1.6.6）：用户在界面上直接输入/粘贴路径，无需弹文件夹选择框。
    /// 与 PickExportDir 共用 SetExportDir（含同一套可写校验），避免两处逻辑漂移。
    /// </summary>
    public string SetExportDirFromText(string dir)
    {
        try
        {
            // 传空字符串 = 恢复默认（用户想「重置」时不必知道默认路径长什么样）
            if (string.IsNullOrWhiteSpace(dir))
            {
                _cfg.ExportDir = "";
                AppPaths.Save(_cfg);
                string def = AppPaths.ResolveExportDir(_cfg);
                return J(new { ok = true, msg = "已恢复默认导出目录：" + def, dir = def, isDefault = true });
            }

            // 用户可能粘贴带引号的路径（从资源管理器「复制文件地址」得到）
            string p = dir.Trim().Trim('"').Trim();
            if (p.Length == 0) return J(new { ok = false, msg = "路径为空", dir = AppPaths.ResolveExportDir(_cfg) });
            try { p = Path.GetFullPath(Environment.ExpandEnvironmentVariables(p)); }
            catch (Exception ex) { return J(new { ok = false, msg = "路径格式不正确：" + ex.Message, dir = AppPaths.ResolveExportDir(_cfg) }); }

            return SetExportDir(p);
        }
        catch (Exception e) { LogErr("SetExportDirFromText", e); return Err(e.Message); }
    }

    /// <summary>
    /// 写入导出目录（唯一落盘点）。可写校验通过后才存，坏值绝不进 config。
    /// v1.6.6 审查项：读取侧 AppPaths.ResolveExportDir 仍有独立兜底 —— 即便历史 config 里
    /// 已存在坏值（手改 / 盘符被移除 / 权限变更），下载也不会失败，只会回退默认目录。
    /// </summary>
    private string SetExportDir(string dir)
    {
        // 校验可写：用户可能选了只读/无权限目录，坏值一旦存进 config 会挡住后续下载
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".txw_write_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return J(new { ok = false, msg = "该目录不可写：" + ex.Message + "（未保存）", dir = AppPaths.ResolveExportDir(_cfg) });
        }

        _cfg.ExportDir = dir;
        AppPaths.Save(_cfg);
        return J(new { ok = true, msg = "导出目录已设为：" + dir, dir, isDefault = false });
    }

    /// <summary>
    /// 立即打开当前导出目录（界面上常驻的「打开文件夹」入口）。
    /// 与 /api/download/open 的区别：后者传空时回退到「最近一次导出」，**从未导出过就会失败**；
    /// 常驻卡片场景下用户可能还没导出过任何文件，故这里直接用解析后的导出目录。
    /// </summary>
    public string OpenExportDirNow()
    {
        string dir = AppPaths.ResolveExportDir(_cfg);
        return J(new { ok = MainForm.OpenExportFolder(dir), dir });
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
            long mb = info != null && info.Size > 0 ? info.Size / 1048576 : 0;
            Toast("客户端安装完成 ✅ " + exe + (mb > 0 ? "　安装包（约 " + mb + " MB）已可删除，点【清理安装包】释放空间" : ""));
        }
        catch (Exception e) { Toast("安装客户端失败: " + e.Message); }
    }

    /// <summary>列出 installer 目录里被判定为安装包的文件（供前端确认弹窗）。</summary>
    public string InstallerInfo()
    {
        try
        {
            var files = ClientService.InstallerFiles();
            long total = files.Sum(x => x.Size);
            var arr = files.Select(x => new { name = Path.GetFileName(x.Path), size = x.Size, path = x.Path }).ToArray();
            return J(new { ok = true, count = files.Count, totalBytes = total, totalMb = Math.Round(total / 1048576.0, 1), files = arr });
        }
        catch (Exception e) { LogErr("InstallerInfo", e); return Err("获取安装包信息失败: " + e.Message); }
    }

    /// <summary>手动删除 installer 目录里的安装包（只删判定为安装包者）。</summary>
    public string ClearInstallers()
    {
        try
        {
            var (count, bytes) = ClientService.ClearInstallers();
            if (count == 0) return J(new { ok = true, count = 0, bytes = 0L, msg = "没有可删除的安装包" });
            var mb = Math.Round(bytes / 1048576.0, 1);
            return J(new { ok = true, count, bytes, msg = $"已删除 {count} 个安装包，释放 {mb} MB" });
        }
        catch (Exception e) { LogErr("ClearInstallers", e); return Err("删除失败: " + e.Message); }
    }

    public string AutoStart(string title, string proc, int intervalMs, string minConf, string dir, string engine)
    {
        // 间隔缺省值按引擎取（前端漏传时不能退化成另一条路径的值：DOM 一轮毫秒级完成，OCR 默认 20 秒）
        if (intervalMs <= 0)
            intervalMs = string.Equals(engine, "dom", StringComparison.OrdinalIgnoreCase) ? _cfg.IntervalMsDom : _cfg.IntervalMs;

        // 未装客户端时必须发 needInstall（与 AutoLaunch / AutoLaunchDom 同一套信号）：
        // 否则 AutoService.Start 会返回「题库为空」，把"没装客户端"误报成"没打开试卷"。
        // 已在运行但取不到路径（权限/架构差异）时不阻断，交给后续流程。
        var found = ClientService.Find(_cfg);
        if (found == null && !ClientService.AnyRunning())
            return J(new { ok = false, needInstall = true, msg = "未检测到天学网客户端" });

        // 自愈：把找到的正确路径回写（历史坏值 = 安装包路径，ConfigClientExe 已判无效，这里纠正落盘）
        if (found != null && !string.Equals(_cfg.ClientExe, found, StringComparison.OrdinalIgnoreCase))
        { _cfg.ClientExe = found; AppPaths.Save(_cfg); }

        // 集成：开始自动回答时若 hosts 劫持接管未在运行，则自动拉起抓题库（失败仅记接管日志，不阻断作答）
        if (!_mc.Running) { try { _mc.Start(); } catch { } }
        var (ok, msg) = _auto.Start(title ?? "", proc ?? "", intervalMs, minConf ?? "mid", dir ?? "", engine ?? "ocr");
        return J(new { ok, msg });
    }

    /// <summary>
    /// DOM 模式专用启动客户端：附加客户端源码级调试开关 --remote-debugging-port-u3=9222
    /// （魔改 Electron 的改名开关，见 _research/DOM可行性研究结论.md），
    /// 由本工具拉起后 DomEngine 才能通过 CDP 直连页面。
    /// </summary>
    public string AutoLaunchDom()
    {
        try
        {
            var exe = ClientService.Find(_cfg);
            if (exe == null)
            {
                if (ClientService.AnyRunning())
                    return J(new { ok = false, msg = "检测到客户端正在运行——请先【完全退出客户端】（含托盘），再点本按钮由工具带调试开关重启" });
                return J(new { ok = false, needInstall = true, msg = "未检测到天学网客户端" });
            }
            // v1.3.5：同时开主进程 Node 调试口 --inspect=9229（逆向诊断用：dump 主进程模块源码/解密逻辑）
            var msg = ClientService.Launch(exe,
                "--remote-debugging-port-u3=" + CdpClient.DefaultPort + " --inspect=9229");
            _cfg.ClientExe = exe; AppPaths.Save(_cfg);
            return J(new { ok = true, msg });
        }
        catch (Exception e) { LogErr("AutoLaunchDom", e); return Err("后端启动失败: " + e.Message); }
    }

    public string AutoStop() { _auto.Stop(); return J(new { ok = true, msg = "停止中…" }); }

    public string AutoStatus() => J(_auto.Status());

    public string GetConf() => J(new
    {
        ok = true,
        minConf = _cfg.MinConf,
        intervalMs = _cfg.IntervalMs,          // OCR 引擎
        intervalMsDom = _cfg.IntervalMsDom,    // DOM 引擎（独立，默认为 1.5 秒）
    });

    /// <summary>保存偏好。两条引擎路径的间隔各自独立：intervalMs=OCR、intervalMsDom=DOM（0 表示不改）。</summary>
    public string SaveConf(string minConf, int intervalMs, int intervalMsDom = 0)
    {
        try
        {
            _cfg.MinConf = ConfLevelEx.Parse(minConf).Key();
            if (intervalMs >= 3000 && intervalMs <= 120000) _cfg.IntervalMs = intervalMs;
            if (intervalMsDom >= 800 && intervalMsDom <= 120000) _cfg.IntervalMsDom = intervalMsDom;
            AppPaths.Save(_cfg);
            return J(new
            {
                ok = true,
                msg = "已保存：最低置信度=" + ConfLevelEx.Parse(_cfg.MinConf).Name()
                      + "，OCR 间隔=" + _cfg.IntervalMs + "ms，DOM 间隔=" + _cfg.IntervalMsDom + "ms"
            });
        }
        catch (Exception e) { LogErr("SaveConf", e); return Err(e.Message); }
    }

    // ---------- 假私有云（minicloud hosts 劫持 · 抓题库答案）----------
    private readonly MiniCloudService _mc = new();

    public string McStart() => J(new { ok = true, msg = _mc.Start() });
    public string McStop() => J(new { ok = true, msg = _mc.Stop() });
    public string McCache() => J(new { ok = true, msg = _mc.DeleteFlipbooks() });

    /// <summary>清空抓包答案缓存（用户主动删除才删；不删则永久保留以扩大题库）。</summary>
    public string McClear()
    {
        var msg = _mc.ClearAnswers();
        BankService.ClearExternal();
        return J(new { ok = true, msg });
    }

    int McAnswerCount()
    {
        try { using var d = JsonDocument.Parse(File.ReadAllText(_mc.AnswersPath)); return d.RootElement.GetArrayLength(); }
        catch { return 0; }
    }

    public string McStatus()
    {
        // 未运行时顺带做一次僵尸劫持自愈：hosts 有残留但本机 443 无人监听 → 立即清除，
        // 否则客户端所有下载都会 ERR_CONNECTION_REFUSED。
        if (!_mc.Running) _mc.RecoverStaleMarker();
        return J(new
        {
            ok = true,
            running = _mc.Running,
            hijacked = _mc.Hijacked,
            hostsStale = !_mc.Running && _mc.Hijacked,   // 前端据此提示"残留劫持，点停止接管可还原"
            answers = McAnswerCount(),
            words = WordBank.Count,
            log = _mc.LogText
        });
    }

    /// <summary>程序退出/关闭窗口时调用：停止自动答题并**确保 hosts 一定还原**（历史缺陷：关窗口不清 hosts）。</summary>
    public void Shutdown()
    {
        try { _auto.Stop(); } catch { }
        try { _mc.Dispose(); } catch { }
    }

    public string McList()
    {
        var items = new List<object>();
        try
        {
            var di = new DirectoryInfo(_mc.CaptureDir);
            if (di.Exists)
                foreach (var f in di.GetFiles("*.bin").OrderByDescending(f => f.LastWriteTime).Take(50))
                    items.Add(new { name = f.Name, size = f.Length, time = f.LastWriteTime.ToString("MM-dd HH:mm:ss") });
        }
        catch { }
        return J(new { ok = true, items });
    }

    public string McAnswers()
    {
        try { return File.ReadAllText(_mc.AnswersPath); }
        catch { return "[]"; }
    }

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
                using var doc = JsonDocument.Parse(DefaultDir());
                var d = doc.RootElement.GetProperty("dir").GetString() ?? "";
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

    /// <summary>
    /// 密钥狩猎（对应 CLI <c>--keyhunt</c> 的单轮扫描）：扫描客户端进程内存，找出能解开本地
    /// .u3enc 样本（V1/V2，题目数据排最前）的新密钥并自动并入 keys.json。
    /// 与 CLI 的差别：CLI 会每 30 秒重扫、最多轮询 20 分钟（等用户打开目标练习），
    /// 界面调用改为单轮即时返回 —— 未命中时提示用户保持练习页面稍后再点，避免请求挂起几十分钟。
    /// 客户端是提权进程，读取其内存需要本程序以管理员运行（主程序已自提权）。
    /// </summary>
    public string HuntKeys(string dir)
    {
        try
        {
            CryptoService.Reload();
            string scanDir = dir;
            if (string.IsNullOrWhiteSpace(scanDir)) scanDir = _cfg.ScanDir;
            if (string.IsNullOrWhiteSpace(scanDir) || !Directory.Exists(scanDir))
            {
                using var doc = JsonDocument.Parse(DefaultDir());
                var d = doc.RootElement.GetProperty("dir").GetString() ?? "";
                if (Directory.Exists(d)) scanDir = d;
            }
            string root = Program.DataRootOf(scanDir);
            var samples = KeyFinder.CollectSamples(root);
            if (samples.Count == 0)
                return J(new { ok = false, found = false, msg = "没找到 .u3enc 样本——请先在客户端打开过课程/练习" });
            var r = KeyFinder.HuntAndSave(samples);
            bool found = r.FoundKeys.Count > 0;
            return J(new
            {
                ok = true,
                found,
                sampleCount = samples.Count,
                procs = r.ProcessesScanned,
                msg = found
                    ? r.Msg
                    : r.Msg + "（未命中——请保持客户端处于目标练习页面，稍后再点一次）",
            });
        }
        catch (Exception e) { LogErr("HuntKeys", e); return Err(e.Message); }
    }

    /// <summary>
    /// 解密客户端代码/资源（对应 CLI <c>--u3edump all</c>）：借客户端原生 asar codec 导出
    /// 磁盘 <c>*.u3e</c> 与 <c>*.u3a</c> 容器内部条目（含写有 V1/V2 密钥的 dist/main/index.js）。
    /// 需客户端已由「启动客户端（调试模式）」带 <c>--inspect=9229</c> 拉起；对客户端目录只读，
    /// 产物全部写到本工具数据目录 u3edump/，随后可被密钥扫描复用。进度经 toast 推送前端。
    /// </summary>
    public string DumpClient()
    {
        try
        {
            var exe = ClientService.Find(_cfg) ?? ClientService.ConfigClientExe(_cfg);
            var r = U3eDumper.DumpAllAsync(exe ?? "", s => Toast(s)).GetAwaiter().GetResult();
            return J(new { ok = r.Ok, count = r.Count, fail = r.Fail, outDir = r.OutDir, msg = r.Msg });
        }
        catch (Exception e) { LogErr("DumpClient", e); return Err(e.Message); }
    }

    // ---------- 作文报告（手动触发）----------

    /// <summary>
    /// 提取客户端里**已打开**的作文报告（对应界面「作文报告」页的按钮）。
    /// 客户端里的报告页是远端 Web App；账号「写作」权益为试用型时，前端会给内容套上纯 CSS 遮罩
    /// （马赛克 + 禁点击），但 DOM 文本完整 —— 本方法只做运行时「摘遮罩 + 临时揭示折叠区块」并导出，
    /// **不写客户端任何文件、不改客户端代码**。产物（.txt 正文 + .html 自包含报告）落到导出目录。
    /// 前置：客户端须由【启动客户端（调试模式）】拉起（开放调试端口 9222）。
    /// </summary>
    public string ReportExtract()
    {
        try
        {
            string dir = AppPaths.ResolveExportDir(_cfg);
            var r = ReportExtractor.ExtractAsync(dir).GetAwaiter().GetResult();

            // 只有真的落了盘才登记"最近导出"，否则「打开所在文件夹」会指向空路径
            if (r.TxtPath.Length > 0) RecordExport?.Invoke(r.TxtPath);
            if (r.Ok) Toast(r.Msg);

            return J(new
            {
                ok = r.Ok,
                msg = r.Msg,
                text = r.Text,
                chars = r.Chars,
                url = r.Url,
                html = r.HtmlPath,
                txt = r.TxtPath,
                dir,
                blur = r.BlurRemoved,
                revealed = r.Revealed,
                tips = r.TipsHidden,
                candidates = r.Candidates,
            });
        }
        catch (Exception e) { LogErr("ReportExtract", e); return Err("提取作文报告失败: " + e.Message); }
    }
}
