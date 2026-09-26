using System.Text;

namespace TxwExtract.Core;

/// <summary>
/// 客户端本地密钥静态扫描（「密钥管理 → 从客户端源码查找」的后端）：
/// <b>只读</b>，不碰客户端任何文件。把客户端可读文本统统抽一遍 base64 候选再用真实样本试解：
///
///   · up366.exe（主程序二进制，内嵌部分常量）
///   · resources/app.u3a 及同目录 *.u3a（客户端代码容器本体）
///   · resources/**/*.u3e（渲染层资源；未解码时为原生编码，能扫到明文常量就算赚）
///   · resources/**/*.json / *.conf / *.mod / *.text / *.map（明文清单，含模块名与路径常量）
///   · %APPDATA%\up366\**（客户端配置）
///   · <see cref="U3eDumper.DumpDir"/>（本工具此前用原生 codec 解密导出的客户端源码 —— 一旦导出，
///     新版密钥常量就在里面，可离线反复抽取，不必再依赖客户端运行）
///
/// 相比旧实现（只扫 exe + 正则只认 25 字符 base64），本类支持多文件、多编码、二进制 latin1 视图，
/// 并额外精确抽取 <c>Buffer.from("…","base64")</c> 形式的密钥常量。
/// </summary>
public static class ClientKeyScanner
{
    /// <summary>扫描时最多读取的文件数（防止在大目录上失控）。</summary>
    const int MaxFiles = 600;

    public sealed record ScanResult(int FilesScanned, int Candidates, List<string> Found, string Msg);

    /// <summary>汇总本次扫描涉及的文件清单（存在且可读）。</summary>
    public static List<string> ScanFiles(string clientExe)
    {
        var list = new List<string>();
        void Add(string p) { try { if (File.Exists(p)) list.Add(p); } catch { } }
        void AddDir(string dir, string pattern, bool recursive)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, pattern,
                             recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                { list.Add(f); if (list.Count > MaxFiles) return; }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(clientExe) && File.Exists(clientExe))
        {
            Add(clientExe);
            string clientDir = Path.GetDirectoryName(clientExe) ?? "";
            string res = Path.Combine(clientDir, "resources");

            // 客户端代码容器（真正的 JS 源码 bundle 就在这里）
            AddDir(res, "*.u3a", false);
            AddDir(clientDir, "*.u3a", false);

            // 渲染层资源 + 明文清单
            foreach (var pat in new[] { "*.u3e", "*.json", "*.conf", "*.mod", "*.text", "*.txt", "*.map", "*.pak" })
                AddDir(res, pat, true);

            // 版本/更新配置（常带模块与接口常量）
            AddDir(clientDir, "*.json", false);
        }

        // 客户端配置目录
        try
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "up366");
            AddDir(appData, "*.json", true);
        }
        catch { }

        // 本工具此前导出的客户端源码（最高价值：明文 JS）
        AddDir(U3eDumper.DumpDir, "*", true);

        return list.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToList();
    }

    /// <summary>收集解密样本：优先 V2（魔数头）文件，其次 V1；两类都收了才可能同时找出各自密钥。</summary>
    public static List<string> CollectSamples(string resourcesOrDataRoot, int cap = 120)
    {
        var v2 = new List<string>();
        var v1 = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(resourcesOrDataRoot) || !Directory.Exists(resourcesOrDataRoot)) return new();
            foreach (var f in Directory.EnumerateFiles(resourcesOrDataRoot, "*.u3enc", SearchOption.AllDirectories))
            {
                if (v2.Count + v1.Count >= cap * 3) break;
                try
                {
                    using var fs = File.OpenRead(f);
                    var head = new byte[12];
                    int n = fs.Read(head, 0, 12);
                    bool isV2 = n == 12 && head.AsSpan().SequenceEqual(CryptoService.MagicV2);
                    // 题目数据优先（体积小、明文特征稳定）
                    bool prefer = Path.GetFileName(f).StartsWith("questionData", StringComparison.OrdinalIgnoreCase);
                    (isV2 ? v2 : v1).Insert(prefer ? 0 : (isV2 ? v2.Count : v1.Count), f);
                }
                catch { }
            }
        }
        catch { }
        return v2.Concat(v1).Take(cap).ToList();
    }

    /// <summary>执行扫描：抽候选 → 试解样本 → 命中并入 keys.json。
    /// <paramref name="testBuiltin"/>=true 时把内置密钥也纳入试解范围，用于自检抽取链路（CLI <c>--keyscan all</c>）。</summary>
    public static ScanResult Scan(string clientExe, string sampleDir, Action<string>? log = null, bool testBuiltin = false)
    {
        var files = ScanFiles(clientExe);
        var samples = CollectSamples(sampleDir);
        if (files.Count == 0)
            return new ScanResult(0, 0, new(), "未找到可扫描的客户端文件（请填写 up366.exe 路径，或先启动过客户端）");
        if (samples.Count == 0)
            return new ScanResult(files.Count, 0, new(), "未找到 .u3enc 样本 —— 请先在客户端打开过课程/练习");

        log?.Invoke($"扫描 {files.Count} 个客户端文件（含 .u3a/.u3e/清单/已导出源码），样本 {samples.Count} 个…");
        var found = CryptoService.DiscoverKeysInFiles(files, samples, out int candidates, testBuiltin);

        if (testBuiltin)
        {
            // 自检模式：只报告，不写 keys.json
            string detail = found.Count == 0
                ? "未命中任何密钥（抽取或试解链路可能失效）"
                : string.Join("、", found.Select(x => (CryptoService.BuiltinKeys.Contains(x) ? "[内置] " : "[新] ") +
                                                       (x.Length > 10 ? x[..6] + "…" + x[^4..] : x)));
            string m = $"自检完成：{files.Count} 个文件抽出 16 字节 base64 候选 {candidates} 个，试解命中 {found.Count} 把 —— {detail}";
            log?.Invoke(m);
            return new ScanResult(files.Count, candidates, found, m);
        }

        int added = CryptoService.MergeCustomKeys(found);
        string msg = found.Count == 0
            ? $"已扫描 {files.Count} 个文件（抽到 16 字节 base64 候选 {candidates} 个）/ {samples.Count} 个样本，未发现新密钥（内置密钥可能仍然有效）"
            : $"发现 {found.Count} 个新密钥（新增 {added} 个）：" +
              string.Join("、", found.Select(x => x.Length > 10 ? x[..6] + "…" + x[^4..] : x));
        log?.Invoke(msg);
        return new ScanResult(files.Count, candidates, found, msg);
    }

    /// <summary>把扫描结果写成一行人类可读的清单（诊断/日志用）。</summary>
    public static string Describe(List<string> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.Take(40)) sb.AppendLine("  " + f);
        if (files.Count > 40) sb.AppendLine($"  …共 {files.Count} 个");
        return sb.ToString();
    }
}
