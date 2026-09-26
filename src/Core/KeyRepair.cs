using System.Text.Json;

namespace TxwExtract.Core;

/// <summary>
/// 「自动客户端解密」—— 一键把 <b>未经过任何手工逆向/解密</b> 的电脑上的客户端变成"可抽密钥"的状态。
///
/// <para><b>为什么需要它（纯净机器上的链路断点）：</b></para>
/// <list type="number">
/// <item>内置 V1/V2 密钥让 <c>.u3enc</c> 解密开箱即用 —— 主功能在纯净机器上本来就能跑。</item>
/// <item>但"客户端换了密钥后自愈"这条链路在纯净机器上是断的：静态扫描（<see cref="ClientKeyScanner"/>）
/// 唯一能拿到<b>明文 JS</b>的目录是本工具自己的 <c>u3edump</c>，而纯净机器上它是空的；
/// 真正含 V1/V2 密钥的主进程 bundle 在 <c>app.u3a</c> <b>容器内部</b>，磁盘通配符扫不到。</item>
/// <item>导出它需要客户端以 <c>--inspect=9229</c> 在运行 —— 而普通用户很可能只是双击打开的客户端，
/// 没有调试口。</item>
/// </list>
///
/// <para>本类把这条链路串成一步：<b>确保调试口 → 全量导出（磁盘 .u3e + app.u3a 内部）
/// → 静态抽取密钥 → 验证样本可解密</b>。全程只读客户端、产物只写本工具目录。</para>
/// </summary>
public static class KeyRepair
{
    public sealed record Result(bool Ok, bool GotNewKeys, string Msg, int Dumped, int Candidates,
                                List<string> NewKeys, int SamplesOk, int SamplesTotal);

    /// <summary>客户端调试模式启动参数（与「启动客户端（调试模式）」一致）。</summary>
    public const string DebugArgs = "--remote-debugging-port-u3=9222 --inspect=9229";

    /// <summary>
    /// 执行一键自愈。返回结构化结论；详细过程走 <paramref name="log"/>。
    /// </summary>
    public static async Task<Result> AutoRepairAsync(string? clientExe, string dataRoot, Action<string>? log = null)
    {
        void L(string s) { try { log?.Invoke(s); } catch { } }

        // ---------- 0) 定位客户端 ----------
        string exe = clientExe ?? "";
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            var found = ClientService.Find(AppPaths.Load());
            if (!string.IsNullOrWhiteSpace(found)) exe = found;
        }
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return new Result(false, false, "未找到天学网客户端 —— 请在「自动回答」页填写客户端位置后重试", 0, 0, new(), 0, 0);
        L("客户端：" + exe);

        // ---------- 1) 确保主进程调试口（9229）就绪 ----------
        bool ready = await U3eDumper.PortReadyAsync(U3eDumper.InspectorPort, 800);
        if (!ready)
        {
            if (ClientService.AnyRunning())
                return new Result(false, false,
                    "检测到客户端正在运行，但它不是由本工具以调试模式拉起的（端口 9229 未监听）。" +
                    "请先【完全退出客户端】（右键托盘图标退出），再点本按钮自动以调试模式重启。",
                    0, 0, new(), 0, 0);

            L("客户端未运行，正在以调试模式启动…");
            L(ClientService.Launch(exe, DebugArgs));
            L("等待主进程调试口就绪（最多 60 秒，Electron 冷启动较慢）…");
            if (!await U3eDumper.PortReadyAsync(U3eDumper.InspectorPort, 60000))
                return new Result(false, false,
                    "客户端已启动但 60 秒内仍未开出调试口 —— 请手动确认客户端窗口是否正常打开后重试",
                    0, 0, new(), 0, 0);
        }
        else L("主进程调试口已就绪（127.0.0.1:" + U3eDumper.InspectorPort + "）");

        // ---------- 2) 全量导出（磁盘 .u3e + app.u3a 内部条目）----------
        L("开始导出客户端脚本与资源…");
        var dump = await U3eDumper.DumpAllAsync(exe, L);
        if (!dump.Ok)
            return new Result(false, false, "导出客户端资源失败：" + dump.Msg, dump.Count, 0, new(), 0, 0);
        L($"导出完成：{dump.Count} 个条目（失败 {dump.Fail}）→ {dump.OutDir}");

        // ---------- 3) 静态抽取新密钥并写入 keys.json ----------
        if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot))
            L("⚠ 未找到客户端数据目录（.u3enc 样本），将跳过密钥抽取环节");
        var scan = ClientKeyScanner.Scan(exe, dataRoot ?? "", L);

        // ---------- 4) 验证：样本现在能不能解开 ----------
        var (okN, totalN) = VerifySamples(dataRoot ?? "");
        L($"样本验证：{okN}/{totalN} 可解密");

        bool gotNew = scan.Found.Count > 0;
        string msg;
        if (gotNew)
            msg = $"✅ 已获取 {scan.Found.Count} 把新密钥并生效（导出 {dump.Count} 个条目）。" +
                  $"样本验证 {okN}/{totalN} 可解密。";
        else if (totalN > 0 && okN == totalN)
            msg = $"✅ 未发现新密钥，但现有密钥完全可解（样本 {okN}/{totalN}）—— 说明当前版本密钥未变，无需自愈。";
        else if (totalN > 0 && okN > 0)
            msg = $"⚠ 已导出 {dump.Count} 个条目，未抽到新密钥，且样本仅 {okN}/{totalN} 可解密 —— " +
                  "可能存在按单元独立的新密钥，建议改用「扫描客户端内存」或在客户端打开对应练习后再试。";
        else
            msg = $"⚠ 已导出 {dump.Count} 个条目，但未抽到新密钥、样本也全部解不开 —— " +
                  "请把本次日志反馈（可能客户端改用了新的加密方案）。";

        return new Result(true, gotNew, msg, dump.Count, scan.Candidates, scan.Found, okN, totalN);
    }

    /// <summary>用当前密钥环试解若干 .u3enc 样本，返回 (可解密数, 总数)。用于判断"密钥是否真的有效"。</summary>
    static (int Ok, int Total) VerifySamples(string dataRoot)
    {
        int ok = 0, total = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot)) return (0, 0);
            foreach (var f in ClientKeyScanner.CollectSamples(dataRoot, cap: 8))
            {
                total++;
                try
                {
                    var txt = CryptoService.DecryptFile(f);
                    if (!string.IsNullOrEmpty(txt)) ok++;
                }
                catch { }
            }
        }
        catch { }
        return (ok, total);
    }

    /// <summary>把结论序列化成给前端的 JSON（供 UiBridge 直接返回）。</summary>
    public static string ToJson(Result r) => JsonSerializer.Serialize(new
    {
        ok = r.Ok,
        newKeys = r.GotNewKeys,
        count = r.NewKeys.Count,
        dumped = r.Dumped,
        candidates = r.Candidates,
        samplesOk = r.SamplesOk,
        samplesTotal = r.SamplesTotal,
        keys = r.NewKeys,
        msg = r.Msg,
    });
}
