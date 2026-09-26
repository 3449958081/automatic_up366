using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace TxwExtract.Core;

public sealed class InstallerInfo
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("versionName")] public string VersionName { get; set; } = "";
    [JsonPropertyName("apkSize")] public long Size { get; set; }
    [JsonPropertyName("apkMd5")] public string Md5 { get; set; } = "";
}

/// <summary>
/// 天学网客户端的查找 / 拉起 / 首次安装。
/// 关键：拉起时必须把工作目录设为 exe 所在目录，否则客户端找不到同级依赖会静默退出（Node 版踩过的坑）。
/// </summary>
public static class ClientService
{
    private const string VersionApi =
        "https://setup-api.up366.cn/front/appversion/latestVersion?modelName=PC-UP366-V2-EXE";

    static readonly string[] NameKeys = { "up366", "student", "天学网学生端" };
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>判定文件名 / 进程名是否"像安装包 / 卸载程序"而非客户端本体。true = 像安装包（应排除）。
    /// 兜底方向：不确定时返回 false（当作客户端本体），绝不把真客户端误杀。
    /// 依据：① 文件名（去扩展名）含 install / setup / unins / updater；② 文件名以官方安装包前缀 <c>up366student-</c> 开头。</summary>
    public static bool LooksLikeInstaller(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var n = Path.GetFileNameWithoutExtension(fileName); // 同时兼容传进程名（无扩展名）
        if (string.IsNullOrEmpty(n)) return false;
        if (n.StartsWith("up366student-", StringComparison.OrdinalIgnoreCase)) return true; // 官方命名：up366student-{版本}[.{构建号}]
        return new[] { "install", "setup", "unins", "updater" }
            .Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>配置里记录的客户端路径，且必须是"客户端本体"——文件存在 + 不像安装包/卸载程序。
    /// v1.6.4：历史上 <see cref="Running"/> 会把"安装包进程"当成客户端，把<strong>安装包路径</strong>写进
    /// config（实测踩到：config.ClientExe 指向 up366student-6.13.0.exe）。若这里只判 File.Exists，
    /// 那个坏值会永久生效并<strong>短路掉后面所有正确的查找</strong>——所以必须在这里也拦一道。
    /// 失效时返回 null，让调用方回落到 Running()/Search()（下次成功启动会回写正确路径，自愈）。</summary>
    public static string? ConfigClientExe(AppConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.ClientExe) && File.Exists(cfg.ClientExe)
        && !LooksLikeInstaller(cfg.ClientExe)
            ? cfg.ClientExe : null;

    /// <summary>已记录的客户端路径（launcher/config 中且文件存在，且不是安装包）。</summary>
    public static string? Configured(AppConfig cfg) => ConfigClientExe(cfg);

    /// <summary>正在运行的客户端进程路径。用 QueryFullProcessImageName 取路径（PROCESS_QUERY_LIMITED_INFORMATION，
    /// 权限要求低，避免 MainModule 在权限/架构差异下抛异常导致误判"未检测到客户端"）。</summary>
    public static string? Running()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                string name = "";
                try { name = p.ProcessName; } catch { continue; }
                if (!NameKeys.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                if (name.Contains("天学网答案提取", StringComparison.Ordinal)) continue; // 排除本工具
                if (LooksLikeInstaller(name)) continue; // 排除安装包 / 卸载程序进程
                string? path = ProcessPath(p.Id);
                if (!string.IsNullOrEmpty(path) && File.Exists(path) &&
                    path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return path;
            }
        }
        catch { }
        return null;
    }

    /// <summary>是否有"天学网客户端"进程在运行（拿不到路径时也能判断，用于避免误报启动失败）。</summary>
    public static bool AnyRunning()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                string name = "";
                try { name = p.ProcessName; } catch { continue; }
                if (name.Contains("天学网答案提取", StringComparison.Ordinal)) continue;
                if (LooksLikeInstaller(name)) continue; // 排除安装包 / 卸载程序进程
                if (NameKeys.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        catch { }
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private static string? ProcessPath(int pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>常见目录有界搜索。</summary>
    public static string? Search()
    {
        var names = new[] { "up366.exe", "天学网学生端.exe", "txwstudent.exe", "student.exe", "tianxuewang.exe" };
        var roots = new List<string>();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.AddRange(new[]
        {
            Path.Combine(user, "Desktop"), Path.Combine(user, "Downloads"),
            local, pf, pf86, AppPaths.DataDir, AppPaths.AppDir,
            @"D:\下载", @"D:\Downloads", @"D:\",
        });

        foreach (var root in roots.Where(Directory.Exists))
        {
            var found = Walk(root, names, 0, 4);
            if (found != null) return found;
        }
        return null;
    }

    static string? Walk(string dir, string[] names, int depth, int maxDepth)
    {
        if (depth > maxDepth) return null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (names.Contains(Path.GetFileName(f).ToLowerInvariant()))
                {
                    if (LooksLikeInstaller(f)) continue; // 精确命中后再排除安装包
                    return f;
                }
            if (depth == maxDepth) return null;
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                string nm = Path.GetFileName(d);
                if (nm is "node_modules" or "$Recycle.Bin" or "System Volume Information" or "Windows" or "ProgramData") continue;
                var r = Walk(d, names, depth + 1, maxDepth);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }

    /// <summary>综合查找：配置 → 运行中 → 搜索。</summary>
    public static string? Find(AppConfig cfg) => Configured(cfg) ?? Running() ?? Search();

    /// <summary>拉起客户端（已运行则激活到前台；否则用 exe 所在目录作为工作目录启动）。</summary>
    public static string Launch(string exe) => Launch(exe, "");

    /// <summary>
    /// 拉起客户端并附加 Chromium/Node 命令行开关。
    /// DOM 直连模式必须走这个重载：必须由本工具带 <c>--remote-debugging-port-u3=9222</c>
    /// （+ <c>--inspect=9229</c> 主进程调试口）启动，客户端才会开出 CDP 端口。
    /// </summary>
    public static string Launch(string exe, string extraArgs)
    {
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return "客户端 exe 不存在: " + exe;

        // 已在运行 → 激活（注意：此时新参数不会生效，需先完全退出客户端）
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { continue; }
                if (string.Equals(path, exe, StringComparison.OrdinalIgnoreCase))
                {
                    if (p.MainWindowHandle != IntPtr.Zero) WinApi.Activate(p.MainWindowHandle);
                    return extraArgs.Length == 0
                        ? "天学网客户端已在运行，已激活到前台"
                        : "天学网客户端已在运行 —— 调试参数需先【完全退出客户端】再启动才生效";
                }
            }
        }
        catch { }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                // 关键：工作目录必须是 exe 所在目录（否则客户端找不到同级依赖会静默退出，也会导致调试开关不监听）
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                UseShellExecute = true,
            };
            if (extraArgs.Length > 0) psi.Arguments = extraArgs;
            Process.Start(psi);
            return "已启动天学网客户端: " + exe + (extraArgs.Length > 0 ? "（带调试参数）" : "");
        }
        catch (Exception e) { return "启动失败: " + e.Message; }
    }

    /// <summary>查询官方最新版本信息。</summary>
    public static async Task<InstallerInfo?> FetchLatestAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, VersionApi);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            using var res = await Http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("data", out var d) && d.TryGetProperty("url", out var u))
                return new InstallerInfo
                {
                    Url = u.GetString() ?? "",
                    VersionName = d.TryGetProperty("versionName", out var v) ? v.GetString() ?? "" : "",
                    Size = d.TryGetProperty("apkSize", out var s) ? s.GetInt64() : 0,
                    Md5 = d.TryGetProperty("apkMd5", out var m) ? m.GetString() ?? "" : "",
                };
        }
        catch { }
        return null;
    }

    /// <summary>下载安装包到用户数据目录（复用已下载的完整文件）。</summary>
    public static async Task<string?> DownloadInstallerAsync(InstallerInfo info, Action<string>? log = null)
    {
        try
        {
            string path = Path.Combine(AppPaths.InstallerDir, $"up366student-{info.VersionName}.exe");
            var fi = new FileInfo(path);
            if (fi.Exists && info.Size > 0 && fi.Length == info.Size) { log?.Invoke("复用已下载的安装包"); return path; }

            log?.Invoke($"下载安装包（{info.Size / 1048576} MB）…");
            using var res = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead);
            res.EnsureSuccessStatusCode();
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await res.Content.CopyToAsync(fs);
            log?.Invoke($"下载完成（{new FileInfo(path).Length / 1048576} MB）");
            return path;
        }
        catch (Exception e) { log?.Invoke("下载失败: " + e.Message); return null; }
    }

    /// <summary>把安装包装到指定目录（依次尝试 NSIS / Inno 静默参数，失败则弹向导）。</summary>
    public static async Task<string?> InstallToAsync(string setupPath, string installDir, Action<string>? log = null)
    {
        try { Directory.CreateDirectory(installDir); } catch { }

        bool TryArgs(string args, bool wait)
        {
            try
            {
                var psi = new ProcessStartInfo(setupPath) { Arguments = args, WorkingDirectory = Path.GetDirectoryName(setupPath) ?? "", UseShellExecute = true };
                var p = Process.Start(psi);
                if (p != null && wait) p.WaitForExit(60000);
                return true;
            }
            catch (Exception e) { log?.Invoke("安装启动失败: " + e.Message); return false; }
        }

        log?.Invoke($"尝试静默安装到: {installDir}");
        // NSIS
        if (TryArgs($"/S /D={installDir}", true))
        {
            await Task.Delay(3000);
            var f = FindIn(installDir);
            if (f != null) return f;
        }
        // Inno
        if (TryArgs($"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{installDir}\"", true))
        {
            await Task.Delay(3000);
            var f = FindIn(installDir);
            if (f != null) return f;
        }
        // 兜底：弹向导让用户手动完成（目录已给出）
        log?.Invoke("静默安装未成功，已弹出安装向导，请在向导中把安装位置设为：" + installDir);
        TryArgs("", false);
        return null;
    }

    public static string? FindIn(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories))
            {
                var fn = Path.GetFileNameWithoutExtension(f);
                if (!NameKeys.Any(k => fn.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                if (LooksLikeInstaller(f)) continue; // 排除安装包 / 卸载程序
                return f;
            }
        }
        catch { }
        return null;
    }

    /// <summary>列出 %LOCALAPPDATA%\TxwExtract\installer 这一层目录里、被 <see cref="LooksLikeInstaller"/> 判定为安装包的文件及其大小。</summary>
    public static List<(string Path, long Size)> InstallerFiles()
    {
        var list = new List<(string Path, long Size)>();
        try
        {
            var dir = AppPaths.InstallerDir;
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir)) // 只扫这一层，绝不递归
            {
                if (!f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!LooksLikeInstaller(f)) continue;
                try { var fi = new FileInfo(f); list.Add((f, fi.Length)); } catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>删除 <see cref="InstallerFiles"/> 列出的文件（只删 installer 这一层、且通过判定者；删不掉的跳过不抛）。</summary>
    public static (int Count, long Bytes) ClearInstallers()
    {
        int count = 0; long bytes = 0;
        foreach (var (p, size) in InstallerFiles())
        {
            try { File.Delete(p); count++; bytes += size; }
            catch { } // 被占用等：跳过，不阻断
        }
        return (count, bytes);
    }
}
