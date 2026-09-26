using System.Text;
using System.Text.Json;

namespace TxwExtract.Core;

/// <summary>
/// .u3e / .u3a 条目解密 —— <b>客户端原生 codec 路线，不需要密钥、不碰客户端文件</b>。
///
/// 原理（见 <c>_research/DOM可行性研究结论.md</c> 第十/十一节）：客户端是魔改 Electron，其 .u3e/.u3a 的
/// 编解码不是 JS 层算法，而是由原生模块 <c>electron_common_asar</c> 完成 —— 客户端自己的 <c>SL()</c> 就是
/// <c>process._linkedBinding("electron_common_asar").encodeBuffer|decodeBuffer</c>。
/// 因此只要客户端以 <c>--inspect=9229</c> 启动（本工具「启动客户端（调试模式）」已带该参数），
/// 就能把待解密路径交给客户端主进程，用它的原生解码器解出来，再写到本工具自己的目录。
///
/// <para><b>两条来源、两种读法（实测确认，勿混）：</b></para>
/// <list type="bullet">
/// <item>磁盘上的 <c>resources/**/*.u3e</c>：<c>fs.readFileSync</c> 返回 <b>U3EF 密文</b>（魔数 55 33 45 46），
/// 需要再过一道 <c>decodeBuffer</c>。</item>
/// <item><c>app.u3a</c> 的<b>虚拟目录</b>路径（如 <c>…/app.u3a/dist/main/index.js.u3e</c>）：魔改 Electron 的 fs
/// 已透明解密，<c>fs.readFileSync</c> 直接返回<b>明文 JS</b>，<b>不能再 decodeBuffer</b>（会二次解码而损坏）。</item>
/// </list>
/// <para>本类用「看头部」自适应：不是文本 且 以 U3EF 开头 → 才调 decodeBuffer。两种来源都能正确落盘。</para>
///
/// <para><b>为什么要导 app.u3a 内部条目</b>：V1/V2（.u3enc）密钥写在主进程 bundle
/// <c>app.u3a/dist/main/index.js</c> 里，而 app.u3a 是<b>单个文件</b>、不是磁盘目录 ——
/// 只扫 <c>*.u3e</c> 通配符永远拿不到它（实测客户端磁盘有 43 个 .u3e 但一个都不含该 bundle）。
/// 必须列虚拟目录逐条导出，<see cref="ClientKeyScanner"/> 才抽得到新密钥。</para>
///
/// 产物落在 <see cref="DumpDir"/>，随后被 ClientKeyScanner 扫描 —— 客户端升级换密钥后，
/// "导出 → 扫描 → 写入"三步自愈。
/// </summary>
public static class U3eDumper
{
    /// <summary>客户端主进程 Node inspector 端口（「启动客户端（调试模式）」注入 <c>--inspect=9229</c>）。</summary>
    public const int InspectorPort = 9229;

    /// <summary>导出目录（本工具数据目录下，绝不写客户端目录）。</summary>
    public static string DumpDir => Path.Combine(AppPaths.DataDir, "u3edump");

    public sealed record DumpResult(bool Ok, string Msg, int Count, int Fail, string OutDir);

    // ---------------- 主进程连接 ----------------

    /// <summary>轮询等待主进程调试口就绪（客户端启动到监听需要几秒）。</summary>
    public static async Task<bool> PortReadyAsync(int port = InspectorPort, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var t = await CdpClient.ListTargetsAsync(port, pagesOnly: false);
                if (t.Count > 0) return true;
            }
            catch { }
            await Task.Delay(400);
        }
        return false;
    }

    /// <summary>连接客户端主进程（9229 的 type="node" 目标）。连不上返回 null。</summary>
    static async Task<CdpClient?> ConnectMainAsync()
    {
        var targets = await CdpClient.ListTargetsAsync(InspectorPort, pagesOnly: false);
        if (targets.Count == 0) return null;
        var target = targets.FirstOrDefault(t => !string.IsNullOrEmpty(t.WsUrl)) ?? targets[0];
        var cdp = new CdpClient();
        try { await cdp.ConnectAsync(target.WsUrl); return cdp; }
        catch { try { cdp.Dispose(); } catch { } return null; }
    }

    // ---------------- 注入到客户端主进程的脚本片段 ----------------

    /// <summary>公共前缀：取 fs/path、取原生 asar 编解码器、文本/密文判定。</summary>
    const string Pre = @"
    const get = n => { try { return process.mainModule.require(n); } catch (e) { return require(n); } };
    const fs = get('fs'), pathMod = get('path');
    let asar = null; try { asar = process._linkedBinding('electron_common_asar'); } catch (e) {}
    const looksText = b => {
      const n = Math.min(b.length, 512);
      if (n === 0) return true;
      let bad = 0;
      for (let i = 0; i < n; i++) { const c = b[i]; if (c === 0) bad += 4; else if (c < 9 || (c > 13 && c < 32)) bad++; }
      return bad < Math.max(2, n * 0.02);
    };
    const isU3EF = b => b.length >= 4 && b[0] === 0x55 && b[1] === 0x33 && b[2] === 0x45 && b[3] === 0x46;
";

    /// <summary>「取内容并按需解码」片段：明文原样、U3EF 密文过 decodeBuffer，解不开则明确报错（绝不把密文当明文写出）。</summary>
    static string ReadExpr(string srcJson) => @"
    const raw = fs.readFileSync(" + srcJson + @");
    let out = raw, via = 'plain', decErr = '';
    if (!looksText(raw) && isU3EF(raw)) {
      if (!asar) { decErr = 'no-asar-binding'; }
      else {
        try { const d = asar.decodeBuffer(raw); if (looksText(d)) { out = d; via = 'decode'; } else decErr = 'decode-not-text'; }
        catch (e) { decErr = String((e && e.message) || e); }
      }
    }
";

    // ---------------- 单文件 ----------------

    /// <summary>用客户端进程解码单个条目并写到 <paramref name="dstPath"/>。
    /// <paramref name="srcPath"/> 可以是磁盘 .u3e，也可以是 app.u3a 的虚拟目录路径。</summary>
    public static async Task<(bool Ok, string Msg)> DumpOneAsync(string srcPath, string dstPath)
    {
        using var cdp = await ConnectMainAsync();
        if (cdp == null)
            return (false, $"连不上客户端主进程调试口（127.0.0.1:{InspectorPort}）——请先在「自动回答」页用 DOM 直连模式「启动客户端」");
        return await DumpOneAsync(cdp, srcPath, dstPath);
    }

    /// <summary>单文件导出（复用已有连接）。先让客户端直接写盘，写不到再用 base64 回传由本进程写。</summary>
    static async Task<(bool Ok, string Msg)> DumpOneAsync(CdpClient cdp, string srcPath, string dstPath)
    {
        string dstJson = JsonSerializer.Serialize(dstPath);
        string script = @"(() => {
  try {" + Pre + ReadExpr(JsonSerializer.Serialize(srcPath)) + @"
    if (decErr) return JSON.stringify({ ok: false, err: 'U3EF 解码失败(' + decErr + ')' });
    fs.mkdirSync(pathMod.dirname(" + dstJson + @"), { recursive: true });
    fs.writeFileSync(" + dstJson + @", out);
    return JSON.stringify({ ok: true, via: via, inLen: raw.length, outLen: out.length });
  } catch (e) { return JSON.stringify({ ok: false, err: String((e && e.message) || e) }); }
})()";

        long outLen = 0; string via = "";
        try
        {
            string json = await cdp.EvaluateStringAsync(script, 120000, null, userGesture: false);
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            var root = doc.RootElement;
            if (!(root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True))
            {
                string err = root.TryGetProperty("err", out var e2) ? e2.GetString() ?? "" : "未知错误";
                return (false, "原生解码失败：" + err);
            }
            outLen = root.TryGetProperty("outLen", out var ol) && ol.ValueKind == JsonValueKind.Number ? ol.GetInt64() : 0;
            via = root.TryGetProperty("via", out var v) ? v.GetString() ?? "" : "";
        }
        catch (Exception e) { return (false, "调用客户端解码器异常：" + e.Message); }

        // 校验客户端是否真的写成功（提权/权限差异下可能没落盘）→ 失败则用 base64 回传兜底
        try
        {
            if (File.Exists(dstPath) && new FileInfo(dstPath).Length == outLen) return (true, via);
        }
        catch { }
        return await DumpOneViaBase64Async(cdp, srcPath, dstPath);
    }

    /// <summary>回退路径：让客户端把解出的内容 base64 回传，由本进程写盘（客户端无法写目标目录时使用）。</summary>
    static async Task<(bool Ok, string Msg)> DumpOneViaBase64Async(CdpClient cdp, string srcPath, string dstPath)
    {
        const int MaxBytes = 24 * 1024 * 1024;
        string script = @"(() => {
  try {" + Pre + ReadExpr(JsonSerializer.Serialize(srcPath)) + @"
    if (decErr) return JSON.stringify({ ok: false, err: 'U3EF 解码失败(' + decErr + ')' });
    if (out.length > " + MaxBytes + @") return JSON.stringify({ ok: false, err: 'too-large' });
    return JSON.stringify({ ok: true, via: via, b64: out.toString('base64') });
  } catch (e) { return JSON.stringify({ ok: false, err: String((e && e.message) || e) }); }
})()";

        try
        {
            string json = await cdp.EvaluateStringAsync(script, 180000, null, userGesture: false);
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            var root = doc.RootElement;
            if (!(root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True))
            {
                string err = root.TryGetProperty("err", out var e2) ? e2.GetString() ?? "" : "未知错误";
                return (false, "客户端写盘失败且回传兜底失败（" + err + "）");
            }
            string b64 = root.TryGetProperty("b64", out var b) ? b.GetString() ?? "" : "";
            var bytes = Convert.FromBase64String(b64);
            Directory.CreateDirectory(Path.GetDirectoryName(dstPath) ?? ".");
            File.WriteAllBytes(dstPath, bytes);
            return (true, "b64");
        }
        catch (Exception e) { return (false, "回传兜底异常：" + e.Message); }
    }

    // ---------------- 虚拟目录枚举 ----------------

    /// <summary>列出 app.u3a 虚拟目录下某一层的条目（客户端 fs 语义）。</summary>
    public static async Task<List<string>> ListVirtualDirAsync(string appU3aPath, string sub = "")
    {
        using var cdp = await ConnectMainAsync();
        if (cdp == null) return new();
        string rel = string.IsNullOrEmpty(sub) ? "" : "/" + sub.Replace('\\', '/').Trim('/');
        return await ListVirtualDirAsync(cdp, appU3aPath.Replace('\\', '/') + rel);
    }

    static async Task<List<string>> ListVirtualDirAsync(CdpClient cdp, string dir)
    {
        string script = @"(() => {
  try {
    const fs = (() => { try { return process.mainModule.require('fs'); } catch (e) { return require('fs'); } })();
    return JSON.stringify({ ok: true, items: fs.readdirSync(" + JsonSerializer.Serialize(dir) + @") });
  } catch (e) { return JSON.stringify({ ok: false, err: String((e && e.message) || e) }); }
})()";
        var list = new List<string>();
        try
        {
            string json = await cdp.EvaluateStringAsync(script, 30000, null, userGesture: false);
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var it in arr.EnumerateArray()) list.Add(it.GetString() ?? "");
        }
        catch { }
        return list;
    }

    /// <summary>递归列出 u3a 容器内全部文件（相对路径，'/' 分隔）。这一步才能拿到 dist/main/index.js。</summary>
    public static async Task<List<string>> ListVirtualTreeAsync(string u3aPath)
    {
        using var cdp = await ConnectMainAsync();
        if (cdp == null) return new();
        return await ListVirtualTreeAsync(cdp, u3aPath);
    }

    static async Task<List<string>> ListVirtualTreeAsync(CdpClient cdp, string u3aPath)
    {
        string script = @"(() => {
  try {
    const fs = (() => { try { return process.mainModule.require('fs'); } catch (e) { return require('fs'); } })();
    const root = " + JsonSerializer.Serialize(u3aPath.Replace('\\', '/')) + @";
    const out = [];
    (function walk(d, rel) {
      let es; try { es = fs.readdirSync(d, { withFileTypes: true }); } catch (e) { return; }
      for (const e of es) {
        const r = rel ? rel + '/' + e.name : e.name;
        if (e.isDirectory()) walk(d + '/' + e.name, r); else out.push(r);
      }
    })(root, '');
    return JSON.stringify({ ok: true, files: out });
  } catch (e) { return JSON.stringify({ ok: false, err: String((e && e.message) || e) }); }
})()";
        var list = new List<string>();
        try
        {
            string json = await cdp.EvaluateStringAsync(script, 60000, null, userGesture: false);
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            if (doc.RootElement.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var it in arr.EnumerateArray()) list.Add(it.GetString() ?? "");
        }
        catch { }
        return list;
    }

    // ---------------- 批量导出 ----------------

    /// <summary>
    /// 批量导出：① 客户端磁盘上全部 <c>*.u3e</c>；② 全部 <c>*.u3a</c> 容器的<b>内部条目</b>
    /// （含主进程 <c>dist/main/index.js</c>，V1/V2 密钥就在这里）。
    /// 全部镜像到 <see cref="DumpDir"/>（如 <c>u3edump/u3a/app/dist/main/index.js</c>），
    /// 扩展名 <c>.u3e</c> 一律换成 <c>.js</c>，随后可被 <see cref="ClientKeyScanner"/> 扫描。
    /// </summary>
    public static async Task<DumpResult> DumpAllAsync(string clientExe, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(clientExe) || !File.Exists(clientExe))
            return new DumpResult(false, "未找到客户端 exe", 0, 0, DumpDir);

        string clientDir = Path.GetDirectoryName(clientExe) ?? "";
        var jobs = new List<(string Src, string Dst)>();

        // ① 磁盘 .u3e
        try
        {
            foreach (var f in Directory.EnumerateFiles(clientDir, "*.u3e", SearchOption.AllDirectories))
            {
                string rel;
                try { rel = Path.GetRelativePath(clientDir, f); } catch { rel = Path.GetFileName(f); }
                jobs.Add((f, Path.Combine(DumpDir, StripU3e(rel))));
            }
        }
        catch { }
        int diskCount = jobs.Count;

        // ② *.u3a 容器内部条目（必须走主进程调试口读虚拟目录）
        var u3aFiles = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(clientDir, "*.u3a", SearchOption.AllDirectories)) u3aFiles.Add(f);
        }
        catch { }

        using var cdp = await ConnectMainAsync();
        if (cdp == null)
            return new DumpResult(false,
                $"连不上客户端主进程调试口（127.0.0.1:{InspectorPort}）——请先在「自动回答」页用 DOM 直连模式「启动客户端」",
                0, 0, DumpDir);

        int u3aCount = 0;
        foreach (var u3a in u3aFiles)
        {
            var tree = await ListVirtualTreeAsync(cdp, u3a);
            log?.Invoke($"{Path.GetFileName(u3a)} 内部条目 {tree.Count} 个");
            foreach (var rel in tree)
            {
                string baseName = Path.GetFileNameWithoutExtension(u3a);   // app
                string dstRel = Path.Combine(new[] { "u3a", baseName }.Concat(rel.Split('/')).ToArray());
                jobs.Add((u3a.Replace('\\', '/') + "/" + rel, Path.Combine(DumpDir, StripU3e(dstRel))));
                u3aCount++;
            }
        }

        return await RunJobsAsync(cdp, jobs, diskCount, u3aCount, log);
    }

    static async Task<DumpResult> RunJobsAsync(CdpClient cdp, List<(string Src, string Dst)> jobs,
                                               int diskCount, int u3aCount, Action<string>? log)
    {
        if (jobs.Count == 0) return new DumpResult(false, "客户端目录下未找到 .u3e / .u3a 资源", 0, 0, DumpDir);

        log?.Invoke($"待导出 {jobs.Count} 个条目（磁盘 .u3e {diskCount} 个，.u3a 内部 {u3aCount} 个）…");
        int ok = 0, fail = 0; string firstErr = "";
        foreach (var (src, dst) in jobs)
        {
            var (good, msg) = await DumpOneAsync(cdp, src, dst);
            if (good) ok++;
            else { fail++; if (firstErr.Length == 0) firstErr = msg; log?.Invoke("✗ " + Path.GetFileName(src) + " —— " + msg); }
        }

        string summary = fail == 0
            ? $"全部 {ok} 个条目已解密导出到 {DumpDir}"
            : $"成功 {ok} 个 / 失败 {fail} 个（{firstErr}）；导出目录 {DumpDir}";
        log?.Invoke(summary);
        return new DumpResult(ok > 0, summary, ok, fail, DumpDir);
    }

    /// <summary>把镜像相对路径末尾的 .u3e 换成 .js（没有该后缀则原样）。</summary>
    static string StripU3e(string rel)
        => rel.EndsWith(".u3e", StringComparison.OrdinalIgnoreCase) ? rel[..^4] + ".js" : rel;

    /// <summary>导出目录里已有的文件数（UI 用来判断"是否已经导出过客户端源码"）。</summary>
    public static int DumpedFileCount()
    {
        try
        {
            return Directory.Exists(DumpDir)
                ? Directory.EnumerateFiles(DumpDir, "*", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch { return 0; }
    }
}
