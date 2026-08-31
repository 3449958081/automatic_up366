using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Web.WebView2.Core;
using TxwExtract.Core;
using TxwExtract.UI;

namespace TxwExtract;

internal static class Program
{
    [STAThread]
    internal static void Main()
    {
        ApplicationConfiguration.Initialize();

        // v2.1.18 修 740：manifest 已改回 asInvoker（进程必然能启动，杜绝 CreateProcess 阶段
        // 的 ERROR_ELEVATION_REQUIRED），提权改在主入口做——非管理员则用 runas 重新启动自身：
        // 弹 UAC 提框，用户点"是"后新进程以管理员身份运行（才能向管理员运行的天学网客户端
        // 注入 SendInput/PostMessage，否则 UIPI 静默拦截"截图正常但点击无效"）。
        // 若用户拒绝 UAC（点"否"），则以非管理员身份继续运行，仅弹一次提示（自动回答点击
        // 可能被 UIPI 拦截，搜题/扫描等功能不受影响）。
        if (!IsRunningAsAdmin())
        {
            if (TryRelaunchAsAdmin()) return;   // 提权成功，新进程已在跑，当前进程立即退出
            WarnNoAdmin();
        }

        // 密钥 + 配置初始化
        CryptoService.Reload();

        var cfg = AppPaths.Load();
        // 默认数据目录：优先客户端配置，否则多候选自动探测；已有值先验证有效性，无效则重探
        cfg.ScanDir = IsValidResources(cfg.ScanDir) ? cfg.ScanDir! : DefaultScanDir();
        AppPaths.Save(cfg);

        // ① 独立开屏窗口：运行在【独立 STA 线程】。
        //    WebView2 初始化会占用主 UI 线程数秒，若动画跑在同一线程会被严重阻塞（卡顿）；
        //    分开后动画由自己的消息循环驱动，始终保持流畅。窗口 TopMost 且尺寸与主窗体一致，
        //    完整盖住 WebView2 的黑底，直到页面首帧绘制完成才关闭。
        var splashStart = DateTime.Now;
        SplashForm? splash = null;
        var splashReady = new ManualResetEventSlim(false);
        var splashThread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var sp = new SplashForm();
                splash = sp;
                sp.Load += (_, _) => splashReady.Set();
                Application.Run(sp);   // 该线程的消息循环，直到窗口关闭
            }
            catch (Exception ex) { State("splash thread EXC: " + ex.Message); splashReady.Set(); }
        });
        splashThread.SetApartmentState(ApartmentState.STA);
        splashThread.IsBackground = true;
        splashThread.Start();
        splashReady.Wait(5000);
        bool splashClosed = false;

        State("splash thread started");

        // ② 预热 WebView2 环境（与主窗体创建并行）
        Task<CoreWebView2Environment>? envTask = null;
        try
        {
            string userData = Path.Combine(AppPaths.DataDir, "webview2");
            Directory.CreateDirectory(userData);
            envTask = CoreWebView2Environment.CreateAsync(null, userData);
        }
        catch { }

        State("env task started");

        // ③ 主窗体（CapturePreview 确认页面真实绘制后回调撤掉静态遮罩）
        MainForm? main = null;
        main = new MainForm(cfg, envTask, () =>
        {
            if (splashClosed) return;
            splashClosed = true;
            // 最短展示时长：保证完整编排（纯白→跳出 0.84s→居中停留 1.48s→飞行 2.16s→落定+背景过渡 ≈2.7s）
            // 不被截断；页面就绪晚于此时长则按实际就绪时间撤下（商标已停在 webui logo 位置，淡出即无缝交接）
            int elapsed = (int)(DateTime.Now - splashStart).TotalMilliseconds;
            int wait = 2700 - elapsed;
            if (wait > 0)
            {
                var t = new System.Windows.Forms.Timer { Interval = wait };
                t.Tick += (_, _) => { t.Stop(); CloseSplash(); };
                t.Start();
            }
            else CloseSplash();
        });

        State("MainForm ctor done, Run");
        Application.Run(main);
        State("Run returned");

        void CloseSplash()
        {
            State("splash closing at +" + (int)(DateTime.Now - splashStart).TotalMilliseconds + "ms");
            try
            {
                var sp = splash;
                // 跨线程：在开屏自己的线程上交叉淡出（Opacity 渐降后自行 Close）
                if (sp != null && !sp.IsDisposed && sp.IsHandleCreated)
                    sp.Invoke((Action)(() => { try { sp.FadeOut(); } catch { } }));
            }
            catch { }
            // 交还焦点给主窗体（遮罩是 TopMost，关闭后需主动激活主窗体）
            try { main?.BeginInvoke(() => { main.Activate(); main.BringToFront(); }); } catch { }
        }
    }

    private static void State(string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxwExtract");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup_state.txt"), $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        }
        catch { }
    }

    /// <summary>当前进程是否在管理员上下文中启动。与 asInvoker manifest + Main 入口自我提权配套使用。</summary>
    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// 用 ShellExecuteEx(Verb=runas) 重新启动自身——会触发 UAC 提框，用户确认后以管理员身份
    /// 启动新进程。返回 true 表示新进程已成功拉起（调用方应立即退出当前进程）；返回 false
    /// 表示用户拒绝了 UAC 或启动失败（调用方应继续以非管理员身份运行）。
    /// </summary>
    private static bool TryRelaunchAsAdmin()
    {
        try
        {
            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            var psi = new ProcessStartInfo(exe) { Verb = "runas", UseShellExecute = true };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception) { return false; }  // 用户在 UAC 提框点"否"
        catch { return false; }
    }

    /// <summary>非管理员运行时的提示（只弹一次，不重复打扰）。</summary>
    private static void WarnNoAdmin()
    {
        try
        {
            System.Windows.Forms.MessageBox.Show(
                "未获得管理员权限。\r\n\r\n天学网客户端以管理员身份运行时，若本程序不是管理员，\r\n" +
                "「自动回答」的自动点击会被系统拦截（界面正常但点不动）。\r\n\r\n" +
                "建议关闭本程序后重新启动，并在 UAC 提框中点「是」。",
                "权限提示", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
        }
        catch { }
    }

    /// <summary>
    /// 解析客户端数据目录（resources）：优先复用已验证有效的配置，否则多候选自动探测。
    /// 候选顺序：① 客户端 %APPDATA%\up366\config.json 的 saveDir；② 常见安装位置（含用户实际路径
    /// D:\下载\up366\client 等）相邻的 resources；③ 常见默认目录。返回第一个真实存在且含题目的目录；
    /// 兜底的硬编码路径作为最后回退（即便不存在，界面也会明确提示）。
    /// </summary>
    internal static string DefaultScanDir()
    {
        string? fromCfg = TryConfigResources();
        if (fromCfg != null) return fromCfg;

        foreach (var exe in CandidateClientExes())
        {
            var d = AdjacentResources(exe);
            if (d != null) return d;
        }

        foreach (var b in CandidateResourceBases())
        {
            string r = Path.Combine(b, "resources");
            if (IsValidResources(r)) return r;
        }

        return @"D:\Up366StudentFiles\resources";
    }

    static string? TryConfigResources()
    {
        try
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "up366", "config.json");
            if (File.Exists(p))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("saveDir", out var sd))
                {
                    string dir = Path.Combine(sd.GetString() ?? "", "resources");
                    if (IsValidResources(dir)) return dir;
                }
            }
        }
        catch { }
        return null;
    }

    static IEnumerable<string> CandidateClientExes()
    {
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
        yield return @"D:\下载\up366\client\up366.exe";
        yield return @"D:\Up366StudentFiles\up366.exe";
        yield return Path.Combine(downloads, @"up366\client\up366.exe");
        yield return Path.Combine(downloads, @"up366\up366.exe");
        yield return @"C:\Program Files\up366\client\up366.exe";
        yield return @"D:\Program Files\up366\client\up366.exe";
    }

    static string? AdjacentResources(string exe)
    {
        try
        {
            var dir = Path.GetDirectoryName(exe);
            if (dir == null) return null;
            var cands = new[]
            {
                Path.Combine(dir, "resources"),
                Path.Combine(Path.GetDirectoryName(dir) ?? "", "resources"),
                dir,
            };
            foreach (var c in cands)
                if (IsValidResources(c)) return c;
        }
        catch { }
        return null;
    }

    static IEnumerable<string> CandidateResourceBases()
    {
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
        yield return @"D:\Up366StudentFiles";
        yield return @"D:\下载\up366\client";
        yield return Path.Combine(downloads, @"up366\client");
        yield return Path.Combine(downloads, @"up366");
        yield return @"C:\Program Files\up366\client";
        yield return @"D:\Program Files\up366\client";
    }

    /// <summary>目录有效判定：存在，且含 paper.xml.u3enc（作业）或相邻 flipbooks（绘本），或任意 .u3enc 文件。</summary>
    internal static bool IsValidResources(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        if (Directory.EnumerateFiles(dir, "paper.xml.u3enc", SearchOption.TopDirectoryOnly).Any()) return true;
        string flip = Path.Combine(Path.GetDirectoryName(dir) ?? "", "flipbooks");
        if (Directory.Exists(flip)) return true;
        return Directory.EnumerateFiles(dir, "*.u3enc", SearchOption.AllDirectories).Any();
    }
}
