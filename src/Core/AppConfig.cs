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
    public int IntervalMs { get; set; } = 20000;
    public string ClientExe { get; set; } = "";      // 天学网客户端 exe 路径（launcher.json 同步）
    public string ScanDir { get; set; } = "";        // 客户端数据目录（默认从客户端 config 推导）
    public double LineMatchScore { get; set; } = 0.6; // OCR 行匹配阈值
    public bool NoAutoTabRestore { get; set; } = false;
    public bool FirstRunDone { get; set; } = false;  // 首次启动须知弹窗是否已确认

    [JsonIgnore] public ConfLevel MinConfLevel => ConfLevelEx.Parse(MinConf);
}

public static class AppPaths
{
    /// <summary>程序安装目录（可能为 Program Files，只读，不写数据）。</summary>
    public static string AppDir => AppContext.BaseDirectory.TrimEnd('\\');

    /// <summary>用户数据目录（可写）。装到 Program Files 后所有运行时数据都放这里。</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TxwExtract");

    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string LauncherPath => Path.Combine(DataDir, "launcher.json");
    public static string KeysPath => Path.Combine(DataDir, "keys.json");
    public static string InstallerDir => Path.Combine(DataDir, "installer");
    public static string MitmDir => Path.Combine(DataDir, "mitm");
    public static string AutoDir => Path.Combine(DataDir, "auto");

    static AppPaths()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(InstallerDir);
        Directory.CreateDirectory(MitmDir);
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
