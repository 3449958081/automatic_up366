using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Web.WebView2.Core;
using TxwExtract.Core;
using TxwExtract.UI;

namespace TxwExtract;

internal static class Program
{
    /// <summary>单实例互斥量（静态持有，防止 GC 回收导致互斥失效）。</summary>
    static Mutex? _single;

    [STAThread]
    internal static void Main(string[] args)
    {
        // 每次启动清空重建「本次运行」的会话日志（%LOCALAPPDATA%\TxwExtract\logs\session.log）。
        // 必须在任何日志写入之前调用，否则会追加到上一轮的文件里；CLI 开关分支（下面的 return）同样享有本文件。
        LogFiles.StartSession($"v{typeof(Program).Assembly.GetName().Version?.ToString(3)} args=[{string.Join(" ", args)}]");

        // 开发/QA：内存密钥搜索 —— 扫描客户端进程内存，找出能解开样本的密钥并入 keys.json。
        // 需要提权（客户端是提权进程）。结果写 %LOCALAPPDATA%\TxwExtract\keyhunt.json
        if (args.Any(a => a.Equals("--keyhunt", StringComparison.OrdinalIgnoreCase)))
        {
            string outFile = Path.Combine(AppPaths.DataDir, "keyhunt.json");
            object result;
            try
            {
                CryptoService.Reload();
                var cfgK = AppPaths.Load();
                string dir = !string.IsNullOrWhiteSpace(cfgK.ScanDir) ? cfgK.ScanDir : DefaultScanDir();
                string root = DataRootOf(dir);

                // 样本：V2（魔数头）优先，其次 V1 —— 两类都可能换密钥
                var samples = KeyFinder.CollectSamples(root);

                if (samples.Count == 0)
                {
                    result = new { ok = false, msg = "没找到 .u3enc 样本——请先在客户端打开过课程/练习" };
                }
                else
                {
                    // 轮询等待：密钥只在客户端打开对应练习时存在于内存。
                    // 每轮扫完若未命中，等 30s 再扫，最多 20 分钟（给用户留出打开练习的时间）。
                    var deadline = DateTime.UtcNow.AddMinutes(20);
                    var r = KeyFinder.HuntAndSave(samples, Console.WriteLine);
                    while (r.FoundKeys.Count == 0 && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(30_000);
                        r = KeyFinder.HuntAndSave(samples, Console.WriteLine);
                    }
                    result = new
                    {
                        ok = r.FoundKeys.Count > 0,
                        sampleCount = samples.Count,
                        procs = r.ProcessesScanned,
                        readable = r.ProcessesReadable,
                        scannedMB = r.BytesScanned / 1024 / 1024,
                        candidates = r.Candidates,
                        found = r.FoundKeys,
                        msg = r.Msg
                    };
                }
            }
            catch (Exception e) { result = new { ok = false, msg = e.Message }; }
            try
            {
                File.WriteAllText(outFile, System.Text.Json.JsonSerializer.Serialize(result,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch { }
            return;
        }

        // 开发/QA：静态密钥扫描（不需要客户端运行）—— 扫描客户端文件里能读到的全部 base64 候选并试解样本。
        //   --keyscan      正常扫描，命中的新密钥并入 keys.json
        //   --keyscan all  自检模式：把内置密钥也纳入试解范围，验证"抽取+试解"链路（只报告，不写盘）
        if (args.Any(a => a.Equals("--keyscan", StringComparison.OrdinalIgnoreCase)))
        {
            bool selfTest = args.Any(a => a.Equals("all", StringComparison.OrdinalIgnoreCase));
            string outFile = Path.Combine(AppPaths.DataDir, selfTest ? "keyscan_selftest.json" : "keyscan.json");
            object result;
            try
            {
                CryptoService.Reload();
                var cfgS = AppPaths.Load();
                string dir = !string.IsNullOrWhiteSpace(cfgS.ScanDir) ? cfgS.ScanDir : DefaultScanDir();
                var exe = ClientService.Find(cfgS) ?? ClientService.ConfigClientExe(cfgS);
                var files = ClientKeyScanner.ScanFiles(exe ?? "");
                Console.WriteLine($"扫描文件 {files.Count} 个：");
                Console.Write(ClientKeyScanner.Describe(files));
                var r = ClientKeyScanner.Scan(exe ?? "", DataRootOf(dir), Console.WriteLine, testBuiltin: selfTest);
                // 诊断补充：某把密钥"抽到了但没命中"，常见原因是本机没有对应格式的样本可试解
                // （例如客户端数据里清一色 V1、一个 V2 都没有 → V2 密钥无法被验证，属正常，不影响内置使用）
                var samples = KeyFinder.CollectSamples(DataRootOf(dir));
                int v2Samples = 0;
                foreach (var sf in samples)
                {
                    try
                    {
                        using var fs = File.OpenRead(sf);
                        var head = new byte[12];
                        if (fs.Read(head, 0, 12) == 12 && head.AsSpan().SequenceEqual(CryptoService.MagicV2)) v2Samples++;
                    }
                    catch { }
                }
                if (v2Samples == 0)
                    Console.WriteLine($"提示：样本 {samples.Count} 份中没有 V2 格式文件（12 字节魔数头），" +
                                      "因此 V2 密钥即使被抽到也无法试解验证——这不是抽取失败，内置 V2 密钥仍照常使用。");
                result = new { ok = r.Found.Count > 0, selfTest, files = r.FilesScanned, candidates = r.Candidates, found = r.Found, msg = r.Msg, exe, dir, samples = samples.Count, v2Samples };
            }
            catch (Exception e) { result = new { ok = false, msg = e.Message }; }
            try
            {
                File.WriteAllText(outFile, System.Text.Json.JsonSerializer.Serialize(result,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch { }
            return;
        }

        // 开发/QA：一键自动客户端解密 —— 确保调试口 → 导出（磁盘 .u3e + app.u3a 内部）→ 抽密钥 → 验证。
        // 与界面「自动解密客户端并提取密钥」按钮同源。结果写 %LOCALAPPDATA%\TxwExtract\keyrepair.json
        if (args.Any(a => a.Equals("--keyrepair", StringComparison.OrdinalIgnoreCase)))
        {
            string outFile = Path.Combine(AppPaths.DataDir, "keyrepair.json");
            object result;
            try
            {
                CryptoService.Reload();
                var cfgR = AppPaths.Load();
                string dir = !string.IsNullOrWhiteSpace(cfgR.ScanDir) ? cfgR.ScanDir : DefaultScanDir();
                Console.WriteLine("=== 一键自动客户端解密 ===");
                // 同样必须排除安装包：坏值会让解密/扫描作用在安装包上
                var r = KeyRepair.AutoRepairAsync(ClientService.ConfigClientExe(cfgR), DataRootOf(dir), Console.WriteLine)
                                  .GetAwaiter().GetResult();
                Console.WriteLine(r.Msg);
                result = new
                {
                    ok = r.Ok, newKeys = r.GotNewKeys, count = r.NewKeys.Count, dumped = r.Dumped,
                    candidates = r.Candidates, samplesOk = r.SamplesOk, samplesTotal = r.SamplesTotal,
                    keys = r.NewKeys, msg = r.Msg,
                };
            }
            catch (Exception e) { result = new { ok = false, msg = e.Message }; }
            try
            {
                File.WriteAllText(outFile, System.Text.Json.JsonSerializer.Serialize(result,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch { }
            return;
        }

        // 开发/QA：.u3e/.u3a 原生 codec 解密（需客户端以 --inspect=9229 在运行）
        //   --u3edump all              批量导出（磁盘 .u3e + app.u3a 内部条目）
        //   --u3edump list <app.u3a>   递归列出 app.u3a 虚拟目录条目
        //   --u3edump read <src> <dst> 解码单个路径（含 app.u3a 虚拟路径）
        if (args.Length >= 2 && args.Any(a => a.Equals("--u3edump", StringComparison.OrdinalIgnoreCase)))
        {
            string mode = args[1].ToLowerInvariant();
            object result;
            try
            {
                if (mode is "list" or "read")
                {
                    // 旧版缺参时静默掉进"全量 dump"分支（代价高且不是用户想要的），现在显式报错
                    int need = mode == "list" ? 3 : 4;
                    if (args.Length < need)
                    {
                        Console.WriteLine($"--u3edump {mode} 缺参数：{mode} 需要 {(need - 2)} 个参数（见用法注释）");
                        return;
                    }
                }
                if (mode == "list")
                {
                    var items = U3eDumper.ListVirtualTreeAsync(args[2]).GetAwaiter().GetResult();
                    Console.WriteLine($"条目 {items.Count} 个：");
                    foreach (var it in items) Console.WriteLine("  " + it);
                    result = new { ok = items.Count > 0, count = items.Count, items };
                }
                else if (mode == "read" && args.Length >= 4)
                {
                    var (ok, msg) = U3eDumper.DumpOneAsync(args[2], args[3]).GetAwaiter().GetResult();
                    Console.WriteLine(msg);
                    result = new { ok, msg, src = args[2], dst = args[3] };
                }
                else
                {
                    var cfgU = AppPaths.Load();
                    var exe = ClientService.Find(cfgU) ?? ClientService.ConfigClientExe(cfgU);
                    var r = U3eDumper.DumpAllAsync(exe ?? "", Console.WriteLine).GetAwaiter().GetResult();
                    result = new { ok = r.Ok, count = r.Count, fail = r.Fail, outDir = r.OutDir, msg = r.Msg };
                }
            }
            catch (Exception e) { result = new { ok = false, msg = e.Message }; }
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.DataDir, "u3edump.json"),
                    System.Text.Json.JsonSerializer.Serialize(result,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch { }
            return;
        }

        // 开发/QA：证书期限体检（--certcheck[ renew]）——
        // 打印 CA/叶子证书的有效期、剩余天数、关键扩展（AKI 是 Chromium 的硬要求）与受信任根安装状态；
        // 加 renew 参数则强制执行一次「临期自动续期」检查（等价于运行时自动补偿的那一步）。
        if (args.Any(a => a.Equals("--certcheck", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Any(a => a.Equals("renew", StringComparison.OrdinalIgnoreCase)))
            {
                var r = CertAuthority.EnsureCaFresh();
                Console.WriteLine("续期检查：" + r.Msg);
            }
            Console.WriteLine($"CA: {CertAuthority.CaSubject}");
            Console.WriteLine($"  CA 到期: {CertAuthority.Ca.NotAfter:yyyy-MM-dd}（剩余 {CertAuthority.CaRemainingDays} 天）");
            Console.WriteLine($"  已装入受信任根: {CertAuthority.IsInstalled()}");
            var leaf = CertAuthority.GetLeaf("fs-v2.up366.cn");
            Console.WriteLine($"叶子: {leaf.Subject}");
            Console.WriteLine($"  到期: {leaf.NotAfter:yyyy-MM-dd}（剩余 {(int)(leaf.NotAfter - DateTime.Now).TotalDays} 天，Chromium 上限 398）");
            bool hasAki = leaf.Extensions.Any(e => e.Oid?.Value == "2.5.29.35");
            bool hasSki = leaf.Extensions.Any(e => e.Oid?.Value == "2.5.29.14");
            bool hasSan = leaf.Extensions.Any(e => e.Oid?.Value == "2.5.29.17");
            Console.WriteLine($"  AKI(2.5.29.35): {hasAki}   SKI(2.5.29.14): {hasSki}   SAN(2.5.29.17): {hasSan}");
            Console.WriteLine(hasAki ? "判定：扩展齐备，Chromium 可构建信任链" : "判定：❌ 缺 AKI —— 客户端会报 Missing Authority Key Identifier");
            return;
        }

        // 开发/QA：证书自测（--certtest [端口]）——打印新签叶子证书的全部扩展（重点看 AKI），
        // 并在指定端口起一个临时 TLS 服务（60 秒），便于用严格校验的客户端（Chromium/Python）验证信任链。
        if (args.Any(a => a.Equals("--certtest", StringComparison.OrdinalIgnoreCase)))
        {
            int port = args.Length >= 2 && int.TryParse(args[1], out var cp) ? cp : 8443;
            var leaf = CertAuthority.GetLeaf("fs-v2.up366.cn");
            Console.WriteLine("叶子证书: " + leaf.Subject);
            Console.WriteLine($"有效期: {leaf.NotBefore:yyyy-MM-dd} ~ {leaf.NotAfter:yyyy-MM-dd}（剩 {(leaf.NotAfter - DateTime.Now).Days} 天，Chromium 要求 ≤398）");
            Console.WriteLine("扩展:");
            foreach (var e in leaf.Extensions)
                Console.WriteLine($"  - {e.Oid?.FriendlyName} ({e.Oid?.Value}) critical={e.Critical}");
            Console.WriteLine("指纹: " + leaf.Thumbprint);
            Console.WriteLine("CA 已安装: " + CertAuthority.IsInstalled());

            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var tcp = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            using (tcp)
                            using (var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false))
                            {
                                ssl.AuthenticateAsServer(leaf);
                                var buf = new byte[8192];
                                try { ssl.Read(buf, 0, buf.Length); } catch { }
                                var body = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
                                ssl.Write(System.Text.Encoding.ASCII.GetBytes(
                                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                                ssl.Write(body); ssl.Flush();
                            }
                        }
                        catch { }
                    });
                }
            });
            Console.WriteLine($"临时 TLS 服务已启动 127.0.0.1:{port}（保持 60 秒）");
            System.Threading.Thread.Sleep(60000);
            return;
        }

        // 开发/QA：CDP 连通性自测（--cdptest <端口>）——枚举目标、连接、读帧树、ping
        //   用于验证 Page.getFrameTree 的解析（frame/childFrames 在 result.frameTree 之下）是否正确。
        if (args.Any(a => a.Equals("--cdptest", StringComparison.OrdinalIgnoreCase)))
        {
            int port = args.Length >= 2 && int.TryParse(args[1], out var p) ? p : CdpClient.DefaultPort;
            var targets = CdpClient.ListPagesAsync(port).GetAwaiter().GetResult();
            Console.WriteLine($"目标 {targets.Count} 个：");
            foreach (var t in targets) Console.WriteLine($"  [{t.Type}] {t.Title} {t.Url}");
            if (targets.Count == 0) { Console.WriteLine("无可用目标，终止"); return; }
            var cdp = new CdpClient();
            cdp.ConnectAsync(targets[0].WsUrl).GetAwaiter().GetResult();
            Console.WriteLine("已连接: " + cdp.Connected);
            var frames = cdp.GetFramesAsync().GetAwaiter().GetResult();
            Console.WriteLine($"帧 {frames.Count} 个（修正后应能取到 iframe）：");
            foreach (var f in frames) Console.WriteLine($"  {f.Id}  {f.Url}");
            Console.WriteLine("Ping: " + cdp.PingAsync().GetAwaiter().GetResult());
            return;
        }

        // 开发/QA：接管服务端到端自测（--mcselftest [下载ID]）
        //   真跑一遍：本机起 443 接管服务 → 以客户端同样的 SNI 与 URL 发起请求 → 转发真实 CDN → 解析 zip 答案。
        //   配合 TXW_HOSTS_TEST=<临时文件> 可避免改动系统 hosts，因此**不需要管理员权限**。
        if (args.Any(a => a.Equals("--mcselftest", StringComparison.OrdinalIgnoreCase)))
        {
            string id = args.Length >= 2 && args[1].Length > 0 ? args[1] : "2085171186282532906";
            // 可选第三个参数：把自测服务起在非 443 端口（这样即使本机已有实例在 443 接管也能验证）
            int port = args.Length >= 3 && int.TryParse(args[2], out var pp) ? pp : 443;
            var mc = new MiniCloudService { ListenPort = port };
            Console.WriteLine("Start: " + mc.Start());
            if (!mc.Running) { Console.WriteLine("接管未启动（权限不足或端口被占用），终止自测"); return; }

            string FixPort(string u) => port == 443 ? u
                : System.Text.RegularExpressions.Regex.Replace(u, @"^(https://[^/:]+)(:\d+)?", "$1:" + port);

            var handler = new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                // 强制连本机对应端口（模拟 hosts 劫持后的客户端行为），但 URL/SNI 保持真实域名
                ConnectCallback = async (ctx, ct) =>
                {
                    var t = new System.Net.Sockets.TcpClient();
                    await t.ConnectAsync(System.Net.IPAddress.Loopback, ctx.DnsEndPoint.Port, ct);
                    return t.GetStream();
                }
            };
            using var hc = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(180) };
            try
            {
                var url = FixPort("https://fs-v2.up366.cn/download/" + id);
                Console.WriteLine($"GET {url}");
                using var resp = hc.GetAsync(url).GetAwaiter().GetResult();
                var body = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                Console.WriteLine($"  → HTTP {(int)resp.StatusCode}，{body.Length} 字节，isZip={(body.Length > 2 && body[0] == 'P' && body[1] == 'K')}");
                var loc = resp.Headers.Location?.ToString();
                Console.WriteLine($"  → Location：{loc ?? "(无)"}");
                if (!string.IsNullOrEmpty(loc))
                {
                    var u2 = loc.StartsWith("/") ? "https://cdn-ws.up366.cn" + loc : loc;
                    u2 = FixPort(u2);
                    Console.WriteLine($"GET {u2}  （客户端 302 后会走这里 —— /cn/files/ 分支）");
                    using var resp2 = hc.GetAsync(u2).GetAwaiter().GetResult();
                    var body2 = resp2.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    Console.WriteLine($"  → HTTP {(int)resp2.StatusCode}，{body2.Length} 字节，isZip={(body2.Length > 2 && body2[0] == 'P' && body2[1] == 'K')}");
                }

                // 顺带验证「兜底透传」：白名单之外的接口不该再被我们伪造成 404
                foreach (var (m, host, path) in new[]
                {
                    ("GET",  "words-v2-api.up366.cn", "/client/common/dict/list"),
                    ("POST", "wordsbtl-api.up366.cn", "/client/wordsbtl/student/record"),
                })
                {
                    var u3 = FixPort($"https://{host}{path}");
                    var rq = new HttpRequestMessage(new HttpMethod(m), u3);
                    if (m == "POST")
                    {
                        rq.Content = new System.Net.Http.ByteArrayContent("{\"courseId\":8868146}"u8.ToArray());
                        rq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                    }
                    using var r3 = hc.SendAsync(rq).GetAwaiter().GetResult();
                    Console.WriteLine($"  [透传] {m} {host}{path} → HTTP {(int)r3.StatusCode}（真实服务器应为 302/200，不该是本地伪造的 404）");
                }
            }
            catch (Exception e) { Console.WriteLine("请求异常：" + e.Message); }

            Console.WriteLine("--- 接管服务日志 ---");
            Console.WriteLine(mc.LogText);
            Console.WriteLine("--- 停止 ---");
            Console.WriteLine(mc.Stop());
            return;
        }

        // 开发/QA：假私有云答案解析自测 —— --mcparse <抓包.bin>，验证 C# 版 questionObj 解析
        if (args.Length >= 2 && args.Any(a => a.Equals("--mcparse", StringComparison.OrdinalIgnoreCase)))
        {
            var mc = new MiniCloudService();
            try
            {
                if (!File.Exists(args[1])) { Console.WriteLine("文件不存在：" + args[1]); return; }
                var answers = mc.ParseZipAnswers(File.ReadAllBytes(args[1]));
                Console.WriteLine($"answers={answers.Count}");
                var merged = BankService.MergeExternal(answers.Select(a =>
                    (a.TryGetValue("qt", out var q0) ? q0 : "", a.TryGetValue("ans", out var a0) ? a0 : "")));
                Console.WriteLine($"merged={merged} bankTotal={BankService.Count} answerTexts={BankService.AnswerTexts().Count}");
                foreach (var a in answers.Take(8))
                    Console.WriteLine("  [" + (a.TryGetValue("qt", out var q) ? q : "") + "] => " + (a.TryGetValue("ans", out var an) ? an : ""));
            }
            catch (Exception e) { Console.WriteLine("解析失败：" + e.Message); }
            return;
        }

        // 开发/QA：单词PK 匹配自检（--pkcheck "题面" "选项1" "选项2" ...）
        //   用途：拿到真实 PK 页面的题面与选项，直接看词库能不能匹配上、分数多少、候选/义项是什么。
        if (args.Any(a => a.Equals("--pkcheck", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = args.SkipWhile(x => !x.Equals("--pkcheck", StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
            if (rest.Count < 2) { Console.WriteLine("用法：--pkcheck \"<题面>\" \"选项1\" \"选项2\" ..."); return; }
            string q = rest[0];
            var opts = rest.Skip(1).ToList();
            Console.WriteLine($"词库 {WordBank.Count} 词");
            Console.WriteLine($"题面: {q}");
            Console.WriteLine($"选项: {string.Join(" | ", opts)}");
            bool isCn = System.Text.RegularExpressions.Regex.IsMatch(q, "[\\u4e00-\\u9fff]");
            Console.WriteLine($"方向: {(isCn ? "中文 → 选英文单词" : "英文 → 选中文释义")}");
            if (isCn)
            {
                var cands = WordBank.MatchMeaningToWords(q, 8);
                Console.WriteLine("候选词: " + (cands.Count == 0 ? "(无)" : string.Join(", ", cands.Select(c => $"{c.word}({c.score:0})"))));
            }
            else
            {
                var ms = WordBank.MeaningsOf(q);
                Console.WriteLine("义项: " + (ms.Count == 0 ? "(词库无此词)" : string.Join(" / ", ms)));
            }
            var r = WordBank.MatchChoice(q, opts);
            Console.WriteLine(r == null ? "结果: ❌ 未命中" : $"结果: ✅ 选 “{r.Value.answer}”（{r.Value.score:0} 分）");
            return;
        }

        // 开发/QA：hosts 接管闭环自测（--mctest）
        //   需先设置环境变量 TXW_HOSTS_TEST 指向一个可写的临时文件，自测不会碰真实 hosts。
        //   验证四件事：① 启动写入属主令牌且自检通过 ② 停止完全还原 ③ 僵尸残留自愈 ④ 他人接管不被误删
        if (args.Any(a => a.Equals("--mctest", StringComparison.OrdinalIgnoreCase)))
        {
            string hp = Environment.GetEnvironmentVariable("TXW_HOSTS_TEST") ?? "";
            if (hp.Length == 0) { Console.WriteLine("请先设置 TXW_HOSTS_TEST=<临时 hosts 文件>"); return; }
            File.WriteAllText(hp, "127.0.0.1 localhost\n");
            bool Has() => File.ReadAllText(hp).Contains("TXW-MC-BEGIN");
            var mc = new MiniCloudService();
            int pass = 0, fail = 0;
            void Check(string name, bool ok, string extra = "")
            {
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} {extra}");
                if (ok) pass++; else fail++;
            }

            Console.WriteLine("① 启动（写入属主令牌 + 自检 127.0.0.1:443）");
            var r1 = mc.Start();
            Console.WriteLine("    Start 返回: " + r1);
            var txt1 = File.ReadAllText(hp);
            Check("hosts 已写入劫持块", Has());
            Check("劫持块含属主令牌", txt1.Contains("# TXW-MC-OWNER "));
            Check("劫持块含 4 个域名", MiniCloudService.Domains.All(d => txt1.Contains(d)));
            Check("Start 报成功", r1.Contains("已启动"));

            Console.WriteLine("② 停止（必须完全还原）");
            Console.WriteLine("    Stop 返回: " + mc.Stop());
            Check("hosts 已还原", !Has());

            Console.WriteLine("③ 僵尸残留自愈（上次异常退出：块还在、本机 443 无人监听）");
            File.WriteAllLines(hp, new[] { "# TXW-MC-BEGIN", "# TXW-MC-OWNER dead-12345678",
                "127.0.0.1\tfs-v2.up366.cn", "127.0.0.1\tcdn-ws.up366.cn", "# TXW-MC-END" });
            mc.RecoverStaleMarker();
            Check("僵尸块已自愈清除", !Has());

            Console.WriteLine("④ 他人实例接管中（本机 443 有监听）不应被误删");
            var mock = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 443);
            mock.Start();
            File.WriteAllLines(hp, new[] { "# TXW-MC-BEGIN", "# TXW-MC-OWNER other-87654321",
                "127.0.0.1\tfs-v2.up366.cn", "# TXW-MC-END" });
            mc.RecoverStaleMarker();
            Check("他实例的劫持被保留（未误删）", Has());
            mock.Stop();
            mc.Stop(true);
            Check("收尾还原", !Has());

            Console.WriteLine("⑤ 443 被占用时必须如实失败（旧版会盲报成功 → 界面显示已启动、实际没接住）");
            System.Net.Sockets.TcpListener? mock2 = null;
            string r5;
            try
            {
                mock2 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 443);
                mock2.ExclusiveAddressUse = true;
                mock2.Start();
                r5 = mc.Start();
            }
            catch (Exception ex) { r5 = "(占用模拟失败，跳过该用例：" + ex.Message + ")"; }
            Console.WriteLine("    Start 返回: " + r5);
            Check("Start 如实报错", !r5.Contains("已启动"), "→ " + r5);
            Check("失败后 hosts 不留残留", !Has());
            mock2.Stop();

            Console.WriteLine($"\n结果：PASS {pass} / FAIL {fail}");
            return;
        }

        // 开发/QA：题库构建自测 —— 直接解密客户端数据建库，打印统计与样例，用于验证"未找到待作答选项"类问题
        if (args.Any(a => a.Equals("--banktest", StringComparison.OrdinalIgnoreCase)))
        {
            string outFile = Path.Combine(AppPaths.DataDir, "banktest.json");
            try
            {
                CryptoService.Reload();
                var cfgBank = AppPaths.Load();
                string dir = !string.IsNullOrWhiteSpace(cfgBank.ScanDir) ? cfgBank.ScanDir : DefaultScanDir();
                int n = BankService.Build(dir);
                var items = BankService.Items;
                int withAns = items.Count(x => !string.IsNullOrWhiteSpace(x.Ans));
                int withOpts = items.Count(x => x.Opts != null && x.Opts.Count >= 2);
                var samples = items.Take(3).Select(x => new
                {
                    qt = x.Qt.Length > 60 ? x.Qt[..60] : x.Qt,
                    opts = x.Opts.Take(4).ToList(),
                    ans = x.Ans,
                    src = x.Src
                }).ToList();
                // 按来源分组统计，便于看出是哪一类题进/没进库
                var bySrc = items.GroupBy(x => x.Src).Select(g => new { src = g.Key, n = g.Count() }).ToList();
                var obj = new { dir, total = n, withAns, withOpts, bySrc, samples };
                File.WriteAllText(outFile,
                    System.Text.Json.JsonSerializer.Serialize(obj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch (Exception e)
            {
                try { File.WriteAllText(outFile, "{\"error\":\"" + e.Message.Replace("\"", "'") + "\"}"); } catch { }
            }
            return;
        }

        ApplicationConfiguration.Initialize();

        // 开发/QA：DPI/坐标口径诊断（--dpiinfo）
        //   判断 Screen.PrimaryScreen.Bounds 在本进程（PerMonitorV2）里是物理还是逻辑像素，
        //   用于确认 SendInput 绝对坐标 0..65535 的归一化分母是否正确（点不准的根因排查）。
        if (args.Any(a => a.Equals("--dpiinfo", StringComparison.OrdinalIgnoreCase)))
        {
            int smx = WinApi.GetSystemMetricsWrapper(0), smy = WinApi.GetSystemMetricsWrapper(1);   // SM_CXSCREEN / SM_CYSCREEN
            var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var pm = System.Windows.Forms.SystemInformation.PrimaryMonitorSize;
            Console.WriteLine($"SM_CXSCREEN/SM_CYSCREEN (WinApi P/Invoke) : {smx} x {smy}");
            Console.WriteLine($"Screen.PrimaryScreen.Bounds              : {b.Width} x {b.Height}");
            Console.WriteLine($"SystemInformation.PrimaryMonitorSize     : {pm.Width} x {pm.Height}");
            Console.WriteLine($"判定：Screen.Bounds == SM_CXSCREEN ? {(b.Width == smx ? "是（物理像素口径，归一化分母正确）" : "否（逻辑像素口径 → 非 100% 缩放下点击会偏移）")}");
            Console.WriteLine($"缩放系数（物理/逻辑）≈ {(double)smx / Math.Max(1, b.Width):0.##}");
            return;
        }


        // v2.1.18 修 740：manifest 已改回 asInvoker（进程必然能启动，杜绝 CreateProcess 阶段
        // 的 ERROR_ELEVATION_REQUIRED），提权改在主入口做——非管理员则用 runas 重新启动自身：
        // 弹 UAC 提框，用户点"是"后新进程以管理员身份运行（才能向管理员运行的天学网客户端
        // 注入 SendInput/PostMessage，否则 UIPI 静默拦截"截图正常但点击无效"）。
        // 若用户拒绝 UAC（点"否"），则以非管理员身份继续运行，仅弹一次提示（自动回答点击
        // 可能被 UIPI 拦截，搜题/扫描等功能不受影响）。
        if (!IsRunningAsAdmin())
        {
            if (TryRelaunchAsAdmin(args)) return;   // 提权成功，新进程已在跑，当前进程立即退出
            WarnNoAdmin();
        }

        // 密钥 + 配置初始化
        CryptoService.Reload();

        // 单实例保护（历史上缺失，是"接管显示已启动却不生效"的成因之一）：
        //   第二个实例的 McStart() 会先清掉第一个实例写入的 hosts 劫持块，且 443 已被占用导致 bind 失败，
        //   于是出现"界面说已启动、实际本机没接住"的假象。此处直接用互斥量阻止多开。
        Mutex? single = null;
        try
        {
            single = new Mutex(true, AppPaths.SingleInstanceMutex, out bool isFirst);
            _single = single;                      // 静态持有，避免 GC 提前回收导致互斥失效
            if (!isFirst)
            {
                System.Windows.Forms.MessageBox.Show(
                    "天学网答案提取 已经在运行了。\n\n多开会导致两个实例互相清除 hosts 接管（表现为下载失败 / 接管无效），已为你阻止。",
                    "天学网答案提取", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
                return;
            }
        }
        catch (Exception) { single = null; }   // 互斥量不可用时降级：不阻止启动（不影响功能）

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

        MainForm? main = null;

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
        LogFiles.AppendCapped(Path.Combine(AppPaths.DataDir, "startup_state.txt"), $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        LogFiles.Session("boot | " + msg);
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
    static string QuoteArg(string a) => a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a;

    private static bool TryRelaunchAsAdmin(string[] args)
    {
        try
        {
            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            var psi = new ProcessStartInfo(exe) { Verb = "runas", UseShellExecute = true };
            // 必须转发命令行参数：否则带 --keyscan / --mctest 等开关启动时，提权后的新进程
            // 会丢失参数而误入 GUI 主界面（表现为"我明明用了 CLI 却打开了窗口"）
            if (args.Length > 0) psi.Arguments = string.Join(" ", args.Select(QuoteArg));
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

    /// <summary>scanDir 指向 .../resources 时取其父目录（客户端数据根），否则原样返回。</summary>
    internal static string DataRootOf(string scanDir)
    {
        if (string.IsNullOrWhiteSpace(scanDir)) return "";
        string d = scanDir.TrimEnd('\\', '/');
        try
        {
            if (Path.GetFileName(d).Equals("resources", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(d) ?? d;
        }
        catch { }
        return d;
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
