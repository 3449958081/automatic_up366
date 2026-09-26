using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxwExtract.Core;

/// <summary>
/// 「无痕下载接管」服务（抓题库答案 + 单词PK词库）：
///   - 通过 hosts 标记块把 fs-v2.up366.cn / cdn-ws.up366.cn / words-v2-api.up366.cn / wordsbtl-api.up366.cn 指向 127.0.0.1（停止即删标记，客户端零感知）
///   - 本地 443 HTTPS 服务用 TxwExtract CA 现签叶子证书（客户端已实测信任本 CA、无证书固定）
///   - 客户端照常发起下载 → 流量落进本服务 → 抓取明文 Pc.zip 解析答案并入题库（不修改客户端任何文件、不注入脚本）
///   - 词书接口（bucket/detail-info、wordsbtl/student/start、entry/list-by-book）：转发真实服务器的同时抓取响应，
///     解析「单词↔释义」并入 PK 词库（WordBank），供 DOM 引擎在单词PK页面自动作答；443(HTTPS) 与 80(明文) 双通道覆盖
///   - 日志仅存内存；进程内访问真实 CDN 走启动时记录的真实 IP（绕开本机 hosts）
/// </summary>
public sealed class MiniCloudService : IDisposable
{
    public static readonly string[] Domains =
        { "fs-v2.up366.cn", "cdn-ws.up366.cn", "words-v2-api.up366.cn", "wordsbtl-api.up366.cn" };

    /// <summary>词书接口路径特征（按包含匹配；来源：客户端 PK 词库下发接口）。</summary>
    static readonly string[] PkApiPaths =
    {
        "/client/sync/teaching/bucket/detail-info",
        "/client/wordsbtl/student/start",
        "/client/entry/list-by-book",
    };
    const string HostsBegin = "# TXW-MC-BEGIN";
    const string HostsEnd = "# TXW-MC-END";
    /// <summary>标记块内的「属主令牌」行前缀。用于区分本进程写的劫持 与 上次异常退出残留/其它实例写的劫持。</summary>
    const string HostsOwnerPrefix = "# TXW-MC-OWNER ";
    /// <summary>本进程属主令牌（pid + 随机串）。进程级唯一，重启即变。</summary>
    static readonly string OwnerToken =
        Environment.ProcessId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
    /// <summary>
    /// hosts 文件路径。设置环境变量 TXW_HOSTS_TEST 可指向临时文件（仅供 --mctest 自测使用，
    /// 避免自测污染真实 hosts；正常运行时该变量不存在，走系统目录）。
    /// </summary>
    static readonly string HostsPath =
        Environment.GetEnvironmentVariable("TXW_HOSTS_TEST") is { Length: > 0 } tp ? tp
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    static readonly string Ua = "PC-Up366-Student 6.13.0";

    readonly HttpClient _real;                       // 直连真实 IP（绕开本机 hosts）
    readonly Dictionary<string, IPAddress> _realIp = new();
    TcpListener? _listener;
    TcpListener? _listenerHttp;                      // 80 明文通道（词书接口部分走 http://）
    CancellationTokenSource? _cts;
    readonly object _lock = new();
    readonly List<string> _log = new();
    DateTime _lastEofLog = DateTime.MinValue;   // 「客户端预连接后断开」日志限频用

    public bool Running { get; private set; }
    public bool Hijacked { get; private set; }

    /// <summary>
    /// 本地 HTTPS 监听端口（默认 443，即被劫持域名的真实端口）。
    /// 仅供自测把服务起在其它端口上（不占用 443 → 可以在实例正在运行时做端到端验证）。
    /// </summary>
    public int ListenPort { get; set; } = 443;

    public string CaptureDir { get; } = Path.Combine(AppPaths.DataDir, "minicloud", "capture");
    public string AnswersPath { get; } = Path.Combine(AppPaths.DataDir, "minicloud", "answers.json");

    public MiniCloudService()
    {
        Directory.CreateDirectory(CaptureDir);
        var h = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectCallback = ConnectRealAsync
        };
        _real = new HttpClient(h);
        _real.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
        _real.Timeout = TimeSpan.FromSeconds(300);
    }

    async ValueTask<Stream> ConnectRealAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        IPAddress? ip;
        lock (_realIp) { _realIp.TryGetValue(host, out ip); }
        if (ip == null)
        {
            ip = await ResolveRealAsync(host, ct);
            if (ip == null) throw new IOException($"无法解析 {host} 的真实 IP（hosts 已被劫持且 DoH 兜底失败）");
            lock (_realIp) _realIp[host] = ip;
        }
        var tcp = new TcpClient();
        await tcp.ConnectAsync(ip, ctx.DnsEndPoint.Port, ct);
        return tcp.GetStream();
    }

    /// <summary>
    /// 解析域名的**真实** IP（用于向真实服务器转发）。
    /// ⚠️ 关键：本工具自己就靠 hosts 把域名指向 127.0.0.1，所以系统 DNS 解析会返回环回地址——
    /// 若直接采用，转发就会打到本机自己（自环：表现为本机 404、或另一实例的响应）。
    /// 因此这里必须剔除环回地址，并在系统 DNS 只给出环回/失败时改用 DNS-over-HTTPS 兜底。
    /// </summary>
    async Task<IPAddress?> ResolveRealAsync(string host, CancellationToken ct)
    {
        // 字面量 IP 直接用
        if (IPAddress.TryParse(host, out var lit)) return lit;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct);
            var real = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (real != null) return real;
        }
        catch { }
        return await ResolveViaDohAsync(host, ct);
    }

    /// <summary>DoH 兜底解析（阿里 DNS / Cloudflare，均为 IP 字面量访问，不受本机 hosts 影响）。</summary>
    async Task<IPAddress?> ResolveViaDohAsync(string host, CancellationToken ct)
    {
        foreach (var tpl in new[] { "https://223.5.5.5/resolve?name={0}&type=A", "https://1.1.1.1/dns-query?name={0}&type=A" })
        {
            try
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                hc.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/dns-json");
                var txt = await hc.GetStringAsync(string.Format(tpl, host), ct);
                using var doc = JsonDocument.Parse(txt);
                if (doc.RootElement.TryGetProperty("Answer", out var ans) && ans.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in ans.EnumerateArray())
                    {
                        if (a.TryGetProperty("type", out var t) && t.GetInt32() == 1 &&
                            a.TryGetProperty("data", out var d) &&
                            IPAddress.TryParse(d.GetString(), out var ip) && !IPAddress.IsLoopback(ip))
                        {
                            Log($"DNS 兜底（DoH）解析 {host} → {ip}");
                            return ip;
                        }
                    }
                }
            }
            catch { }
        }
        Log($"⚠ 无法取得 {host} 的真实 IP（系统 DNS 返回环回且 DoH 兜底失败）");
        return null;
    }

    // ---------------- 启停 ----------------

    public string Start()
    {
        if (Running) return "已在运行";
        try
        {
            // 【期限自动补偿】CA 临期（<90 天）先自动续期，再确认它已装入受信任根
            var caRenew = CertAuthority.EnsureCaFresh();
            if (caRenew.Renewed) Log(caRenew.Msg);
            if (!CertAuthority.IsInstalled())
            {
                var r = CertAuthority.Install();
                if (!r.Ok) { Log("证书安装失败：" + r.Msg); return "证书安装失败：" + r.Msg; }
            }
            // ① 自愈：上次异常退出残留的劫持块（本机 443 无人监听）必须先清掉，
            //    否则客户端请求会被送到 127.0.0.1:443 → ERR_CONNECTION_REFUSED，表现为"劫持失效/下载失败"。
            RecoverStaleMarker();
            // ② 若残留块仍在且是别的进程写的（443 有监听），说明已有实例在接管，本实例不抢。
            var owner = ReadHostsOwner();
            if (owner != null && owner.Length > 0 && owner != OwnerToken)
                return $"已有另一个实例正在接管（属主 {owner}），请先关闭它再启动";
            var ips = ResolveRealIps();               // 必须在写 hosts 之前
            if (ips.Count == 0) { Log("域名解析失败，请检查网络"); return "域名解析失败，请检查网络"; }
            lock (_realIp) { foreach (var kv in ips) _realIp[kv.Key] = kv.Value; }
            WriteHostsMarker();

            _listener = new TcpListener(IPAddress.Any, ListenPort);
            _listener.ExclusiveAddressUse = true;     // 禁止多实例同绑 443（否则连接归属随机 → 时好时坏）
            _listener.Start();
            try
            {
                _listenerHttp = new TcpListener(IPAddress.Any, 80);
                _listenerHttp.Start();
            }
            catch (Exception e80) { _listenerHttp = null; Log($"80 端口监听失败（明文词书接口不可抓，HTTPS 通道不受影响）：{e80.Message}"); }
            _cts = new CancellationTokenSource();
            Running = true; Hijacked = true;
            _ = Task.Run(() => Loop(_cts.Token));
            if (_listenerHttp != null) _ = Task.Run(() => LoopHttp(_cts.Token));
            StartHeal();                          // 运行中周期自检 + 自动补偿

            // ③ 自检：hosts 已指向 127.0.0.1，本地 443 必须接得住，否则立刻回滚并如实报错，
            //    绝不返回"已启动"（旧版这里盲报成功，残留 hosts 会把客户端拖成 REFUSED 却无人知晓）。
            if (!LocalPortListening(ListenPort))
            {
                Stop();
                Log($"启动自检失败：127.0.0.1:{ListenPort} 无法连通（hosts 已回滚）");
                return $"启动失败：本地 {ListenPort} 自检未通过（hosts 已还原，请重试或以管理员权限运行）";
            }
            Log($"服务启动（{ListenPort} · 劫持 {string.Join("/", Domains)} · 属主 {OwnerToken}）");
            return $"已启动：已劫持 {Domains.Length} 个域名（含单词PK词库接口；客户端无需任何设置，重启客户端后生效）";
        }
        catch (Exception e)
        {
            Stop();
            // 给出可操作的归因，而不是把原始 socket 异常甩给用户
            string hint = LocalPortListening(ListenPort)
                ? $"{ListenPort} 端口已被其它程序占用（多为另一个本工具实例，或 IIS/nginx/VMware 等），请关闭后重试"
                : "权限不足或端口不可用，请右键「以管理员身份运行」后重试";
            Log($"启动失败：{e.Message} —— {hint}（hosts 已还原）");
            return "启动失败：" + hint;
        }
    }

    /// <summary>
    /// 停止接管并还原 hosts。force=true 时连「其它实例写的」劫持块也一并清除（仅用于显式自愈/卸载清理）。
    /// </summary>
    public string Stop(bool force = false)
    {
        if (!Running)
        {
            // 未运行但有残留（上次异常退出）：走自愈，能清则清，不能清就如实说明
            var r = CleanHostsMarker(force);
            Hijacked = ReadHostsOwner() != null;
            return force || r == "无残留" || r == "已清理"
                ? "未在运行（hosts 残留已清理）" : "未在运行（" + r + "）";
        }
        try { _cts?.Cancel(); _listener?.Stop(); _listenerHttp?.Stop(); } catch { }
        StopHeal();
        Running = false; Hijacked = false;
        CleanHostsMarker(force);
        Log("服务已停止，hosts 劫持已移除（客户端零感知）");
        return "已停止，hosts 已还原";
    }

    /// <summary>Dispose/进程退出兜底：确保 hosts 一定被还原。</summary>
    public void Dispose() { try { Stop(); } catch { } }

    public string LogText { get { lock (_lock) return string.Join("\n", _log.TakeLast(200)); } }

    void Log(string m)
    {
        lock (_lock) _log.Add($"[{DateTime.Now:HH:mm:ss}] {m}");
        LogFiles.Session("mini | " + m);   // 落盘（logs\session.log，每次启动自动清除）
    }

    // ---------------- hosts ----------------

    Dictionary<string, IPAddress> ResolveRealIps()
    {
        var d = new Dictionary<string, IPAddress>();
        foreach (var dom in Domains)
        {
            try
            {
                // 走 ResolveRealAsync：剔除环回（hosts 劫持会把域名解析到自己），必要时 DoH 兜底
                var ip = ResolveRealAsync(dom, CancellationToken.None).GetAwaiter().GetResult();
                if (ip != null) d[dom] = ip;
                else Log($"解析 {dom} 失败：未取得真实 IP");
            }
            catch (Exception e) { Log($"解析 {dom} 失败：{e.Message}"); }
        }
        return d;
    }

    List<string> ReadHostsLines() => File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();

    void WriteHostsMarker()
    {
        var lines = ReadHostsLines();
        RemoveMarker(lines);
        lines.Add(HostsBegin);
        lines.Add(HostsOwnerPrefix + OwnerToken);      // 属主令牌：退出时可精确识别"这劫持是不是我写的"
        foreach (var dom in Domains) lines.Add($"127.0.0.1\t{dom}");
        lines.Add(HostsEnd);
        File.WriteAllLines(HostsPath, lines);
        FlushDns();
    }

    /// <summary>
    /// 读取 hosts 中劫持块的属主。
    /// 返回 null = 无劫持块；"" = 有块但无属主行（旧版本写的，可安全清理）；否则为属主令牌。
    /// </summary>
    string? ReadHostsOwner()
    {
        bool inBlock = false, found = false;
        string? owner = null;
        foreach (var l in ReadHostsLines())
        {
            var t = l.Trim();
            if (t == HostsBegin) { inBlock = true; found = true; continue; }
            if (t == HostsEnd) { inBlock = false; continue; }
            if (inBlock && t.StartsWith(HostsOwnerPrefix, StringComparison.Ordinal))
                owner = t.Substring(HostsOwnerPrefix.Length).Trim();
        }
        return found ? (owner ?? "") : null;
    }

    /// <summary>
    /// 自愈：若 hosts 里残留着劫持块，而本机 443 根本没人监听（说明上次进程已死但没清 hosts），
    /// 则强制清除 —— 这是「客户端下载全部 ERR_CONNECTION_REFUSED」的头号成因。
    /// </summary>
    public void RecoverStaleMarker()
    {
        try
        {
            var owner = ReadHostsOwner();
            if (owner == null) return;                       // 干净，无需处理
            if (owner == OwnerToken) return;                 // 本次自己写的
            bool alive = LocalPortListening(443);
            if (owner.Length == 0)                           // 旧版本残留（无属主行）
            {
                if (alive) { Log("检测到旧版残留的 hosts 劫持，但本机 443 有监听，未清理"); return; }
                CleanHostsMarker(true); Log("已自愈清理旧版残留的 hosts 劫持");
            }
            else if (alive)                                  // 别的进程确实在接管 → 不抢不乱清
            { Log($"检测到其他实例（{owner}）正在接管，本机 443 有监听，未清理"); }
            else
            {
                CleanHostsMarker(true);
                Log($"已自愈清理僵尸劫持（属主 {owner}，本机 443 无监听）—— 客户端下载失败多由此引起");
            }
        }
        catch (Exception e) { Log("自愈检查失败：" + e.Message); }
        finally { Hijacked = HasHostsMarker(); }
    }

    /// <summary>hosts 中当前是否存在劫持标记块（不论属主）。</summary>
    public bool HasHostsMarker() { try { return ReadHostsOwner() != null; } catch { return false; } }

    /// <summary>
    /// 清理 hosts 劫持块。默认只清「本进程写的」或「无属主的旧格式」，避免误删其它实例的劫持；
    /// force=true 时无条件清（自愈僵尸残留 / 用户显式还原）。
    /// </summary>
    public string CleanHostsMarker(bool force = false)
    {
        try
        {
            var owner = ReadHostsOwner();
            if (owner == null) return "无残留";
            if (!force && !(owner.Length == 0 || owner == OwnerToken))
                return $"已有其他实例接管中（属主 {owner}）";
            var lines = ReadHostsLines();
            RemoveMarker(lines);
            File.WriteAllLines(HostsPath, lines);
            FlushDns();
            return "已清理";
        }
        catch (Exception e) { Log("清理 hosts 失败：" + e.Message); return "清理失败：" + e.Message; }
    }

    /// <summary>本机 127.0.0.1:port 是否有人在接（用于自检与僵尸判定）。</summary>
    public static bool LocalPortListening(int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync("127.0.0.1", port);
            return t.Wait(1500) && c.Connected;
        }
        catch { return false; }
    }

    // ---------------- 运行中周期自检 + 自动补偿 ----------------
    //
    // 设计原则（用户明确要求）：任何"修正"都要问「它会不会过期 / 会不会被外部改动」，
    // 到期或失效时必须有自动补偿，而不是等用户遇到问题再来排查。
    // 本方法每 60 秒跑一次，逐项检查接管的四个前提，缺失即自动补：
    //   ① hosts 劫持块（可能被安全软件/网络优化工具清理）→ 补回
    //   ② 受信任根里的 CA（可能被证书清理/系统重置移除）→ 重装
    //   ③ CA 自身有效期（10 年，临期 90 天自动续期，否则到期所有叶子一起失效）→ 续期
    //   ④ 本地 443 监听（是否还活着）→ 掉了就如实停止，绝不假装"接管中"

    System.Threading.Timer? _heal;
    void StartHeal()
    {
        StopHeal();
        _heal = new System.Threading.Timer(_ => SafeHeal(), null,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }
    void StopHeal() { try { _heal?.Dispose(); } catch { } _heal = null; }

    void SafeHeal()
    {
        try
        {
            if (!Running) return;
            // ① hosts 劫持被外部清理 → 自动补回
            if (!HasHostsMarker())
            {
                WriteHostsMarker();
                Log("⚠ hosts 劫持块丢失（可能被安全软件/优化工具清理），已自动补回");
            }
            // ② CA 被移出受信任根 → 自动重装
            if (!CertAuthority.IsInstalled())
            {
                var r = CertAuthority.Install();
                Log(r.Ok ? "⚠ CA 证书被移出受信任根，已自动重装" : "⚠ CA 证书被移除且重装失败：" + r.Msg);
            }
            // ③ CA 临期 → 自动续期（到期会导致所有已签叶子同时失效）
            if (CertAuthority.CaRemainingDays < 90)
            {
                var rr = CertAuthority.EnsureCaFresh();
                Log("⚠ " + rr.Msg);
            }
            // ④ 443 监听掉了 → 停止接管并如实记录（不盲报）
            if (!LocalPortListening(ListenPort))
            {
                Log($"⚠ 本地 {ListenPort} 已不再监听，自动结束接管（hosts 将还原）");
                Stop();
            }
        }
        catch (Exception e) { Log("周期自检异常：" + e.Message); }
    }

    static void RemoveMarker(List<string> lines)
    {
        bool inBlock = false;
        lines.RemoveAll(l =>
        {
            if (l.Trim() == HostsBegin) { inBlock = true; return true; }
            if (l.Trim() == HostsEnd) { inBlock = false; return true; }
            return inBlock;
        });
    }

    static void FlushDns()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }

    // ---------------- 443 HTTPS / 80 明文 ----------------

    async void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => SafeHandle(tcp, ct), ct);
        }
    }

    /// <summary>80 端口明文通道：词书接口部分走 http://（明文直接抓），其余透明转发。</summary>
    async void LoopHttp(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listenerHttp!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => SafeHandleHttp(tcp, ct), ct);
        }
    }

    async Task SafeHandle(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            using (tcp)
            using (var ssl = new SslStream(tcp.GetStream(), false))
            {
                var opt = new SslServerAuthenticationOptions
                {
                    ClientCertificateRequired = false,
                    ServerCertificateSelectionCallback = (s, host) =>
                        CertAuthority.GetLeaf(string.IsNullOrEmpty(host) ? Domains[0] : host)
                };
                await ssl.AuthenticateAsServerAsync(opt, ct);
                await RouteAsync(ssl, ct);
            }
        }
        catch (Exception e)
        {
            // Chromium 会对可能用到的域名做 preconnect（预连接）：连上后若判定不需要就直接关闭，
            // 服务端看到的就是 "Received an unexpected EOF or 0 bytes from the transport stream."。
            // 这是正常现象（既不影响转发也不影响抓取），但会刷屏 → 限频记录一次并说明清楚。
            if (e is IOException && e.Message.Contains("EOF", StringComparison.OrdinalIgnoreCase))
            {
                if ((DateTime.Now - _lastEofLog).TotalSeconds > 60)
                {
                    _lastEofLog = DateTime.Now;
                    Log("客户端预连接后断开（Chromium 正常行为，无害；本类日志已限频，每分钟最多一条）");
                }
                return;
            }
            Log("连接处理异常：" + e.Message);
        }
    }

    async Task SafeHandleHttp(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            using (tcp)
            using (var ns = tcp.GetStream())
            {
                ns.ReadTimeout = 30000;
                await RouteAsync(ns, ct);
            }
        }
        catch (Exception e) { Log("HTTP 连接处理异常：" + e.Message); }
    }

    sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Body);

    /// <summary>路径规范化（仅用于判定分支，不改变向上游转发的原始路径）：URL 解码 + 折叠重复斜杠。</summary>
    static string NormPath(string p)
    {
        try { p = Uri.UnescapeDataString(p); } catch { }
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        return p;
    }

    /// <summary>读完整 HTTP 请求（请求行+头+按 Content-Length 读体），再按路径分流。</summary>
    async Task RouteAsync(Stream io, CancellationToken ct)
    {
        var req = await ReadRequest(io, ct);
        if (req == null) return;
        Log($"请求 {req.Method} {req.Path}{req.Query}");
        if (IsPkApi(req.Path)) { await HandlePkApi(io, req, ct); return; }
        // 判定用「规范化路径」：URL 解码 + 折叠重复斜杠 + 忽略大小写。
        // 历史坑：旧版用 Ordinal 前缀比较，`/Download/`、`//download/`、`/%64ownload/` 都会漏进透传分支
        // （客户端功能不受影响，但答案不会被抓取）。转发仍用原始 req.Path，不改上游请求。
        var np = NormPath(req.Path);
        if (io is SslStream ssl && (np.StartsWith("/download/", StringComparison.OrdinalIgnoreCase)
                                 || np.StartsWith("/cn/files/", StringComparison.OrdinalIgnoreCase)))
        { await HandleDownload(ssl, req.Path, req.Query, ct); return; }

        // 兜底：被劫持域名下的**其它一切路径**都原样透传到真实服务器。
        // ⚠️ 绝不能再直接回 404（旧版就是回 404）——那会让客户端拿到假的 404：
        //   实测 POST /client/wordsbtl/student/record（战绩）与 GET /client/common/dict/list（词典类型）
        //   经本机是 404，而直连真实服务器是 302/200 → 客户端弹「请求失败，状态码：404」。
        //   兜底透传同时保证「客户端将来新增的接口」自动可用，无需再维护白名单。
        await Passthrough(io, req, ct);
    }

    /// <summary>原样透传（方法 / 请求头 / 请求体）到真实服务器，响应原样回给客户端；不介入抓取。</summary>
    async Task Passthrough(Stream io, Request req, CancellationToken ct)
    {
        var host = req.Headers.TryGetValue("Host", out var hh) ? hh.Split(':')[0] : Domains[0];
        try
        {
            using var msg = new HttpRequestMessage(new HttpMethod(req.Method), $"https://{host}{req.Path}{req.Query}");
            if (req.Body.Length > 0)
            {
                msg.Content = new ByteArrayContent(req.Body);
                if (req.Headers.TryGetValue("Content-Type", out var ctm))
                    msg.Content.Headers.TryAddWithoutValidation("Content-Type", ctm);
            }
            CopyHeaders(msg, req.Headers);
            using var resp = await _real.SendAsync(msg, ct);
            var data = await resp.Content.ReadAsByteArrayAsync(ct);
            var ctype = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var loc = resp.Headers.Location?.ToString();
            if (!string.IsNullOrEmpty(loc) && loc.StartsWith("/")) loc = $"https://{host}{loc}";
            await Reply(io, (int)resp.StatusCode, data, ctype, loc, ct);
        }
        catch (Exception e)
        {
            Log($"透传失败 {req.Method} {host}{req.Path}：{e.Message}");
            try { await Reply(io, 502, "{\"code\":1,\"msg\":\"upstream error\"}"u8.ToArray(), "application/json", null, ct); } catch { }
        }
    }

    /// <summary>
    /// 把客户端请求头原样搬到上游请求（关键：鉴权类头 Authorization/Cookie/Referer/Origin 必须带上，
    /// 否则上游会把我们当成未登录请求返回 302/401，导致接口数据抓不到、客户端功能也异常）。
    /// 跳过逐跳头与 Content-Type（后者由 HttpContent 承载）。
    /// </summary>
    static void CopyHeaders(HttpRequestMessage msg, Dictionary<string, string> headers)
    {
        foreach (var kv in headers)
        {
            switch (kv.Key.ToLowerInvariant())
            {
                case "host":
                case "content-length":
                case "connection":
                case "transfer-encoding":
                case "keep-alive":
                case "proxy-connection":
                case "upgrade":
                case "te":
                case "trailer":
                case "content-type":
                    continue;
            }
            if (!msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                msg.Content?.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
    }

    static bool IsPkApi(string path) =>
        PkApiPaths.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>词书接口：转发真实服务器（方法/体原样），响应回传客户端的同时抓取解析并入 PK 词库。</summary>
    async Task HandlePkApi(Stream io, Request req, CancellationToken ct)
    {
        var host = req.Headers.TryGetValue("Host", out var hh) ? hh.Split(':')[0] : "words-v2-api.up366.cn";
        try
        {
            using var msg = new HttpRequestMessage(new HttpMethod(req.Method), $"https://{host}{req.Path}{req.Query}");
            if (req.Body.Length > 0)
            {
                msg.Content = new ByteArrayContent(req.Body);
                if (req.Headers.TryGetValue("Content-Type", out var ctm))
                    msg.Content.Headers.TryAddWithoutValidation("Content-Type", ctm);
            }
            CopyHeaders(msg, req.Headers);            // 鉴权头必须带上，否则上游 302/401 → 抓不到词表
            using var resp = await _real.SendAsync(msg, ct);
            var data = await resp.Content.ReadAsByteArrayAsync(ct);
            var ctype = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            await Reply(io, (int)resp.StatusCode, data, ctype, null, ct);

            // 抓词库：仅解析 JSON 成功的响应
            var text = Encoding.UTF8.GetString(data);
            if (text.TrimStart().StartsWith("{") && WordBank.MergeFromJson(text) is var added && added > 0)
                Log($"★ 抓到单词PK词库：新增 {added} 词条（{host}{req.Path}），词库共 {WordBank.Count} 词");
        }
        catch (Exception e)
        {
            Log("词书接口转发失败：" + e.Message);
            try { await Reply(io, 502, "{\"code\":1,\"msg\":\"upstream error\"}"u8.ToArray(), "application/json", null, ct); } catch { }
        }
    }

    static async Task<Request?> ReadRequest(Stream io, CancellationToken ct)
    {
        var buf = new byte[8192];
        var ms = new MemoryStream();
        int headEnd = -1;
        while (headEnd < 0)
        {
            if (ms.Length > 256 * 1024) return null;
            var n = await io.ReadAsync(buf, ct);
            if (n <= 0) return null;
            ms.Write(buf, 0, n);
            headEnd = ms.ToArray().AsSpan().IndexOf("\r\n\r\n"u8);
        }
        var all = ms.ToArray();
        var head = Encoding.ASCII.GetString(all, 0, headEnd);
        var lines = head.Split("\r\n");
        var rl = lines[0];                            // GET /path HTTP/1.1
        var parts = rl.Split(' ');
        if (parts.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in lines.Skip(1))
        {
            var c = h.IndexOf(':');
            if (c > 0) headers[h[..c].Trim()] = h[(c + 1)..].Trim();
        }
        // 按 Content-Length 读体（词书接口可能 POST）
        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) && len > 0)
        {
            if (len > 8 * 1024 * 1024) return null;
            var bodyStart = headEnd + 4;
            var got = all.Length - bodyStart;
            var bodyMs = new MemoryStream();
            if (got > 0) bodyMs.Write(all, bodyStart, Math.Min(got, len));
            while (bodyMs.Length < len)
            {
                var n = await io.ReadAsync(buf, ct);
                if (n <= 0) break;
                bodyMs.Write(buf, 0, n);
            }
            body = bodyMs.ToArray();
        }
        // Transfer-Encoding: chunked 的请求体：旧版只认 Content-Length → 体会被丢弃且残留字节污染连接。
        else if (headers.TryGetValue("Transfer-Encoding", out var te)
                 && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedAsync(io, all, headEnd + 4, buf, ct);
        }

        var pathWithQuery = parts[1];
        var sp = pathWithQuery.IndexOf('?');
        var path = sp < 0 ? pathWithQuery : pathWithQuery[..sp];
        var query = sp < 0 ? "" : pathWithQuery[sp..];
        return new Request(parts[0], path, query, headers, body);
    }

    /// <summary>读 chunked 请求体（含已在缓冲里的部分），上限 8MB。</summary>
    static readonly byte[] CrLf = { 13, 10 };
    static async Task<byte[]> ReadChunkedAsync(Stream io, byte[] pre, int start, byte[] buf, CancellationToken ct)
    {
        var body = new MemoryStream();
        var cur = new MemoryStream();
        if (pre.Length > start) cur.Write(pre, start, pre.Length - start);
        async Task<string?> LineAsync()
        {
            while (true)
            {
                var arr = cur.ToArray();
                int idx = arr.AsSpan().IndexOf(CrLf);
                if (idx >= 0)
                {
                    var rest = arr[(idx + 2)..];
                    cur.SetLength(0);
                    if (rest.Length > 0) cur.Write(rest);
                    return Encoding.ASCII.GetString(arr, 0, idx);
                }
                if (cur.Length > 64 * 1024) return null;
                var n = await io.ReadAsync(buf, ct);
                if (n <= 0) return null;
                cur.Write(buf, 0, n);
            }
        }
        while (body.Length < 8 * 1024 * 1024)
        {
            var line = await LineAsync();
            if (line == null) break;
            var sizeHex = line.Split(';')[0].Trim();
            if (!int.TryParse(sizeHex, System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0) break;
            if (size == 0) { await LineAsync(); break; }          // 结尾块（后跟 trailer）
            while (cur.Length < size + 2)
            {
                var n = await io.ReadAsync(buf, ct);
                if (n <= 0) break;
                cur.Write(buf, 0, n);
            }
            var chunk = cur.ToArray();
            body.Write(chunk, 0, Math.Min(size, chunk.Length));
            var rest2 = chunk.Length > size + 2 ? chunk[(size + 2)..] : Array.Empty<byte>();
            cur.SetLength(0);
            if (rest2.Length > 0) cur.Write(rest2);
        }
        return body.ToArray();
    }

    async Task HandleDownload(SslStream ssl, string path, string query, CancellationToken ct)
    {
        try
        {
            var np = NormPath(path);   // 与 RouteAsync 判定保持一致（忽略大小写/编码变体）
            if (np.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
            {
                // fs-v2.up366.cn/download/<id>：真实取 302 Location → 下载 zip
                var realUrl = $"https://fs-v2.up366.cn{path}{query}";
                Log($"下载转发 → {realUrl}");
                using var resp = await _real.GetAsync(realUrl, ct);
                var loc = resp.Headers.Location?.ToString();
                var data = await resp.Content.ReadAsByteArrayAsync(ct);
                var isZip = data.Length > 2 && data[0] == (byte)'P' && data[1] == (byte)'K';
                var tag = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(realUrl)))[..12].ToLower();
                if (isZip) CaptureAndParse(data, tag);
                if (!string.IsNullOrEmpty(loc))
                {
                    if (loc.StartsWith("/")) loc = "https://cdn-ws.up366.cn" + loc;
                    await Reply(ssl, 302, null, "text/plain", loc, ct);
                    return;
                }
                await ReplyBytes(ssl, data, ct);
                return;
            }
            if (np.StartsWith("/cn/files/", StringComparison.OrdinalIgnoreCase))
            {
                // cdn-ws 劫持请求：转发真实 CDN（注入包查表已随注入方案下线）
                // ⚠ 这里同样要解析答案：客户端走 fs-v2/download → 302 → 本域取 Pc.zip（或 ?back=1 回源）
                // 时，真正的 zip 就是从这条路径回来的；旧版只转发不解析 → 答案漏抓。
                var realUrl = $"https://cdn-ws.up366.cn{path}{query}";
                Log($"CDN 转发 → {realUrl}");
                var data = await _real.GetByteArrayAsync(realUrl, ct);
                var tag = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(realUrl)))[..12].ToLower();
                CaptureAndParse(data, tag);
                await ReplyBytes(ssl, data, ct);
                return;
            }
            // 注意：此分支目前**不可达** —— RouteAsync 只会把 /download/ 与 /cn/files/ 两条前缀交给本方法，
            // 其余一切路径都已由 RouteAsync 的 Passthrough 兜底透传。保留仅为防御。
            // 这里刻意回 502（"上游异常"）而不是 404（"资源不存在"）——「未知即拒绝」是 v1.5.5 修的 bug 的根源，
            // 不给自己留任何"再犯一次"的机会。
            Log($"⚠ 未预期的下载路径（应由 RouteAsync 兜底透传）：{path}");
            await Reply(ssl, 502, Encoding.UTF8.GetBytes("{\"code\":1,\"msg\":\"unexpected path\"}"), "application/json", null, ct);
        }
        catch (Exception e)
        {
            Log("下载处理异常：" + e.Message);
            try { await Reply(ssl, 502, Encoding.UTF8.GetBytes("{\"code\":1,\"msg\":\"upstream error\"}"), "application/json", null, ct); } catch { }
        }
    }

    /// <summary>
    /// 若数据是 ZIP 则：落盘到抓包目录 → 解析 questionData.js → 答案入库。
    /// 非 ZIP 直接返回（普通素材/接口响应）。fs-v2/download 与 cdn-ws/cn/files 两条路径共用。
    /// </summary>
    int CaptureAndParse(byte[] data, string tag)
    {
        var isZip = data.Length > 2 && data[0] == (byte)'P' && data[1] == (byte)'K';
        if (!isZip) return 0;
        try
        {
            var cap = Path.Combine(CaptureDir, $"{tag}_{DateTime.Now:HHmmss}.bin");
            File.WriteAllBytes(cap, data);
            Log($"抓到 {data.Length} 字节 ZIP → {Path.GetFileName(cap)}");
            var found = ParseZipAnswers(data);
            if (found.Count > 0) { SaveAnswers(found); Log($"★ 解析到 {found.Count} 条答案，已并入题库"); }
            else Log("该包内未发现 questionData.js（模块/素材包，非课程包）");
            return found.Count;
        }
        catch (Exception e) { Log("ZIP 解析异常：" + e.Message); return 0; }
    }

    static async Task Reply(Stream io, int code, byte[]? body, string ctype, string? location, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(code == 200 ? "OK" : code == 302 ? "Found" : "Error").Append("\r\n");
        if (location != null) sb.Append("Location: ").Append(location).Append("\r\n");
        body ??= Array.Empty<byte>();
        sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await io.WriteAsync(head, ct);
        if (body.Length > 0) await io.WriteAsync(body, ct);
        await io.FlushAsync(ct);
    }

    static Task ReplyBytes(Stream ssl, byte[] body, CancellationToken ct) =>
        Reply(ssl, 200, body, "application/octet-stream", null, ct);

    // ---------------- 课程缓存清理 ----------------

    public string DeleteFlipbooks()
    {
        try
        {
            var cfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "up366", "config.json");
            string? saveDir = null;
            if (File.Exists(cfg))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(cfg));
                if (d.RootElement.TryGetProperty("saveDir", out var v)) saveDir = v.GetString();
            }
            if (string.IsNullOrWhiteSpace(saveDir))
                return "未找到客户端缓存目录（%APPDATA%\\up366\\config.json 的 saveDir）";
            var fb = Path.Combine(saveDir, "flipbooks");
            if (!Directory.Exists(fb)) return "flipbooks 目录不存在（无需清理）";
            Directory.Delete(fb, true);
            Log($"已清理课程缓存：{fb}");
            return "已清理 flipbooks 课程缓存（" + fb + "）";
        }
        catch (Exception e)
        {
            return "清理失败：" + e.Message + "（客户端正占用文件？请先退出客户端再试）";
        }
    }

    // ---------------- 答案解析 ----------------

    public List<Dictionary<string, string>> ParseZipAnswers(byte[] zip)
    {
        var answers = new List<Dictionary<string, string>>();
        var seen = new HashSet<string>();
        using var arc = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        foreach (var e in arc.Entries)
        {
            if (!e.FullName.EndsWith("questionData.js", StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            using (var r = new StreamReader(e.Open(), Encoding.UTF8)) text = r.ReadToEnd();
            text = Regex.Replace(text, "<[^>]+>", "");
            var qobj = ExtractJsObject(text, "questionObj");
            if (qobj == null) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(qobj); }
            catch
            {
                try { doc = JsonDocument.Parse(JsToJson(qobj)); }
                catch (Exception ex) { Log($"解析失败 {e.FullName}: {ex.Message}"); continue; }
            }
            Collect(doc.RootElement, answers, seen);
        }
        return answers;
    }

    /// <summary>
    /// 保存抓到的答案：**累积合并**写 answers.json（按题干归一化键去重，用户不清空就永久保留，题库越抓越大），
    /// 并实时并入内存题库（视觉引擎每轮读 BankService.AnswerTexts()，自动回答无需重启即可用上）。
    /// </summary>
    void SaveAnswers(List<Dictionary<string, string>> answers)
    {
        try
        {
            var all = LoadPersistedPairs();
            var seen = new HashSet<string>(all.Select(p => BankService.NormKey(p.Item1)));
            foreach (var a in answers)
            {
                var qt = a.TryGetValue("qt", out var q) ? q.Trim() : "";
                var ans = a.TryGetValue("ans", out var v) ? v.Trim() : "";
                if (qt.Length < 4 || ans.Length == 0 || !seen.Add(BankService.NormKey(qt))) continue;
                all.Add((qt, ans));
            }
            var payload = all.Select(p => new Dictionary<string, string> { ["qt"] = p.Item1, ["ans"] = p.Item2 }).ToList();
            File.WriteAllText(AnswersPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        try
        {
            var n = BankService.MergeExternal(
                answers.Select(a => (a.TryGetValue("qt", out var q) ? q : "", a.TryGetValue("ans", out var v) ? v : "")));
            if (n > 0) Log($"已把 {n} 条答案并入「自动回答」题库（题库共 {BankService.Count} 条，自动回答可直接使用）");
        }
        catch (Exception e) { Log("并入自动回答题库失败：" + e.Message); }
    }

    /// <summary>读已持久化的答案缓存（容错：文件缺失/损坏返回空表）。供启动回读与累积合并。</summary>
    public List<(string qt, string ans)> LoadPersistedPairs()
    {
        var list = new List<(string, string)>();
        try
        {
            if (!File.Exists(AnswersPath)) return list;
            using var d = JsonDocument.Parse(File.ReadAllText(AnswersPath));
            if (d.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in d.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var qt = el.TryGetProperty("qt", out var q) ? q.GetString() ?? "" : "";
                var ans = el.TryGetProperty("ans", out var a) ? a.GetString() ?? "" : "";
                if (qt.Length >= 4 && ans.Length > 0) list.Add((qt, ans));
            }
        }
        catch { }
        return list;
    }

    /// <summary>清空答案缓存（用户主动删除的唯一入口）。</summary>
    public string ClearAnswers()
    {
        try
        {
            if (File.Exists(AnswersPath)) { File.Delete(AnswersPath); Log("已清空答案缓存（answers.json）"); return "已清空答案缓存"; }
            return "缓存本就是空的";
        }
        catch (Exception e) { return "清空失败：" + e.Message; }
    }

    void Collect(JsonElement q, List<Dictionary<string, string>> outList, HashSet<string> seen)
    {
        if (q.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in q.EnumerateArray()) Collect(it, outList, seen);
            return;
        }
        if (q.ValueKind != JsonValueKind.Object) return;

        if (q.TryGetProperty("questions_list", out var sub) && sub.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sub.EnumerateArray())
                AddItem(Str(s, "question_text"), ResolveAnswer(s), outList, seen);
            return;
        }
        AddItem(Str(q, "question_text"), ResolveAnswer(q), outList, seen);
    }

    void AddItem(string qt, string ans, List<Dictionary<string, string>> outList, HashSet<string> seen)
    {
        qt = Clean(qt);
        if (qt.Length < 4 || ans.Length == 0) return;
        var key = qt + "|" + ans;
        if (!seen.Add(key)) return;
        outList.Add(new Dictionary<string, string> { ["qt"] = qt, ["ans"] = ans });
    }

    static string Clean(string s) => Regex.Replace(Regex.Replace(s ?? "", @"\([^)]*\)", " "), "_", "").Trim();

    static string Str(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object) return "";
        foreach (var k in new[] { name, "question", "stem" })
            if (o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
        return "";
    }

    static string ResolveAnswer(JsonElement q)
    {
        string raw = "";
        // 实测 29 个解密样本：answer_text/answer/ans/result 全空时 analysis 也为空，
        // 故不再把「解析/讲解」(analysis) 当答案兜底——那是讲解文本，填进去就是错答。
        foreach (var k in new[] { "answer_text", "answer", "ans", "result" })
            if (q.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0) { raw = v.GetString()!; break; }
        if (raw.Length == 0) return "";
        if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in opts.EnumerateArray())
            {
                string oid = "";
                if (o.TryGetProperty("id", out var iv)) oid = iv.ValueKind == JsonValueKind.String ? iv.GetString() ?? "" : iv.GetRawText();
                else if (o.TryGetProperty("option_id", out var iv2)) oid = iv2.ValueKind == JsonValueKind.String ? iv2.GetString() ?? "" : iv2.GetRawText();
                if (oid == raw && o.TryGetProperty("content", out var cv) && cv.ValueKind == JsonValueKind.String)
                    return cv.GetString() ?? raw;
            }
        }
        return raw;
    }

    // ---------------- JS 对象 → JSON（参考项目逻辑移植） ----------------

    static string? ExtractJsObject(string content, string keyName)
    {
        var idx = content.IndexOf(keyName, StringComparison.Ordinal);
        if (idx < 0) return null;
        var colon = content.IndexOf(':', idx);
        if (colon < 0) return null;
        var i = colon + 1;
        while (i < content.Length && char.IsWhiteSpace(content[i])) i++;
        if (i >= content.Length || content[i] != '{') return null;
        var start = i;
        var stack = new Stack<char>();
        bool inS = false, inD = false, esc = false;
        for (var j = i; j < content.Length; j++)
        {
            var ch = content[j];
            if (esc) { esc = false; continue; }
            if (ch == '\\') { esc = true; continue; }
            if (inS) { if (ch == '\'') inS = false; continue; }
            if (inD) { if (ch == '"') inD = false; continue; }
            if (ch == '\'') { inS = true; continue; }
            if (ch == '"') { inD = true; continue; }
            if (ch == '{') stack.Push('{');
            else if (ch == '}')
            {
                if (stack.Count == 0) return null;
                stack.Pop();
                if (stack.Count == 0) return content.Substring(start, j - start + 1);
            }
        }
        return null;
    }

    static string JsToJson(string js)
    {
        var sb = new StringBuilder(js.Length + 64);
        bool inS = false, inD = false, esc = false;
        for (var i = 0; i < js.Length; i++)
        {
            var ch = js[i];
            if (esc) { sb.Append(ch); esc = false; continue; }
            if (ch == '\\') { sb.Append(ch); esc = true; continue; }
            if (inS)
            {
                if (ch == '\'') { inS = false; sb.Append('"'); }
                else sb.Append(ch == '"' ? "\\\"" : ch);
                continue;
            }
            if (inD) { if (ch == '"') { inD = false; sb.Append("\\\""); } else sb.Append(ch); continue; }
            if (ch == '\'') { inS = true; sb.Append('"'); continue; }
            if (ch == '"') { inD = true; sb.Append('"'); continue; }
            if (ch == '{' || ch == ',')
            {
                sb.Append(ch);
                var k = i + 1;
                while (k < js.Length && char.IsWhiteSpace(js[k])) k++;
                if (k < js.Length && Regex.IsMatch(js[k].ToString(), "[A-Za-z_$]"))
                {
                    var m = k;
                    while (m < js.Length && Regex.IsMatch(js[m].ToString(), "[A-Za-z0-9_$]")) m++;
                    var w = m;
                    while (w < js.Length && char.IsWhiteSpace(js[w])) w++;
                    if (w < js.Length && js[w] == ':')
                    {
                        sb.Append(js.Substring(i + 1, k - i - 1));
                        sb.Append('"').Append(js.Substring(k, m - k)).Append('"');
                        i = m - 1;
                    }
                }
                continue;
            }
            sb.Append(ch);
        }
        return Regex.Replace(sb.ToString(), @",\s*(?=[}\]])", "");
    }
}
