using System.Text.Json;
using System.Text.Json.Serialization;

namespace TxwExtract.Core;

/// <summary>置信度档位。数值越大越严格；用户选择"最低置信度"，只有 ≥ 该档位的匹配才会被点击。</summary>
public enum ConfLevel { Low = 1, Mid = 2, High = 3 }

public static class ConfLevelEx
{
    public static ConfLevel Parse(string s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "high" or "高" => ConfLevel.High,
        "low" or "低" => ConfLevel.Low,
        _ => ConfLevel.Mid,
    };
    public static string Key(this ConfLevel c) => c switch { ConfLevel.High => "high", ConfLevel.Low => "low", _ => "mid" };
    public static string Name(this ConfLevel c) => c switch { ConfLevel.High => "高", ConfLevel.Low => "低", _ => "中" };
    /// <summary>把题库匹配得到的中文置信度标签映射为档位。</summary>
    public static ConfLevel FromLabel(string conf)
    {
        if (string.IsNullOrEmpty(conf)) return ConfLevel.Low;
        if (conf.StartsWith("高置信")) return ConfLevel.High;
        if (conf.StartsWith("中置信")) return ConfLevel.Mid;
        return ConfLevel.Low; // 未命中
    }
}

/// <summary>持久化配置（%LOCALAPPDATA%\TxwExtract\config.json）。</summary>
public sealed class AppConfig
{
    public string MinConf { get; set; } = "mid";
    /// <summary>OCR 引擎轮询间隔（毫秒）：一轮要截图 + 识别，慢是正常的。</summary>
    public int IntervalMs { get; set; } = 20000;
    /// <summary>
    /// DOM 引擎轮询间隔（毫秒）。旧版与 OCR 共用 <see cref="IntervalMs"/>（默认 20 秒），
    /// 结果是 DOM 一轮毫秒级完成却要干等 20 秒、点击延迟在 0~20 秒间随机浮动（重试 8 次可拖到分钟级）。
    /// DOM 一轮毫秒级完成，默认 1.5 秒。旧 config.json 无此字段 → 用默认值，自动生效。
    /// </summary>
    public int IntervalMsDom { get; set; } = 1500;
    public string ClientExe { get; set; } = "";      // 天学网客户端 exe 路径（本工具唯一记录处，存 config.json；无其它同步副本）
    public string ScanDir { get; set; } = "";        // 客户端数据目录（默认从客户端 config 推导）
    public bool FirstRunDone { get; set; } = false;  // 首次启动须知弹窗是否已确认

    /// <summary>
    /// 「下载JSON / 下载HTML」的落盘目录（v1.6.6）。留空 = 用户「下载」文件夹下的 TxwExtract 子目录。
    /// 界面上会明确显示该路径并提供「打开文件夹」入口，解决「下载完找不到文件」。
    /// </summary>
    public string ExportDir { get; set; } = "";

    [JsonIgnore] public ConfLevel MinConfLevel => ConfLevelEx.Parse(MinConf);
}

public static class AppPaths
{
    /// <summary>
    /// 全局单实例互斥量名。**唯一定义处** —— Program.Main 用它阻止多开，LogFiles 用它探测
    /// 「是否已有实例在跑」（决定本次启动要不要覆盖会话日志）。不要在别处再写字面量。
    /// </summary>
    public const string SingleInstanceMutex = @"Global\TxwExtract_SingleInstance_v1";

    /// <summary>程序安装目录（可能为 Program Files，只读，不写数据）。</summary>
    public static string AppDir => AppContext.BaseDirectory.TrimEnd('\\');

    /// <summary>用户数据目录（可写）。装到 Program Files 后所有运行时数据都放这里。</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxwExtract");

    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string KeysPath => Path.Combine(DataDir, "keys.json");
    public static string InstallerDir => Path.Combine(DataDir, "installer");
    public static string AutoDir => Path.Combine(DataDir, "auto");

    /// <summary>
    /// 导出下载目录（v1.6.6）。界面上的「下载JSON / 下载HTML」经由 WebView2 的 DownloadStarting
    /// 统一落到这里 —— 之前从未注册下载处理器，文件会落到 WebView2 的默认目录（用户找不到）。
    /// 缺省 = 用户「下载」文件夹下的 TxwExtract 子目录；用户可用 config.json 的 ExportDir 覆盖。
    /// </summary>
    public static string DefaultExportDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TxwExtract");

    /// <summary>解析导出目录：配置里有就用配置的（并确保存在），否则用缺省值。绝不抛异常。</summary>
    public static string ResolveExportDir(AppConfig cfg)
    {
        string dir = (cfg?.ExportDir ?? "").Trim();
        if (dir.Length == 0) dir = DefaultExportDir;
        try { Directory.CreateDirectory(dir); }
        catch
        {
            // 配置里的路径不可写（盘符不存在 / 无权限）→ 回退缺省，避免导出直接失败
            try { dir = DefaultExportDir; Directory.CreateDirectory(dir); } catch { }
        }
        return dir;
    }

    static AppPaths()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(InstallerDir);
        Directory.CreateDirectory(AutoDir);
    }

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch { }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Opts)); }
        catch { }
    }
}
