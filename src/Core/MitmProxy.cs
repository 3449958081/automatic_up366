using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace TxwExtract.Core;

/// <summary>
/// 内置 HTTP(S) MITM 代理：记录天学网客户端的内容包下载地址。
/// - 普通 HTTP：透明转发并记录。
/// - CONNECT：命中域名列表(up366/qlib-cdn 等)则用内置证书做 TLS 中间人，记录请求/响应大小与关键路径；
///   未命中域名则纯隧道直连。
/// 与 Node 版 capStart 行为一致，仅记录不修改流量。
/// </summary>
public sealed class MitmProxy
{
    static readonly Regex Focus = new(@"up366\.cn|up366\.com|qlib-cdn|cdn-ws|fs-v2|aliyuncs", RegexOptions.IgnoreCase);
    static readonly Regex FileLike = new(@"xot|\.zip|file|download|pack", RegexOptions.IgnoreCase);

    static readonly byte[] KeyPem = Convert.FromBase64String(
        "LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0tCk1JSUV2UUlCQURBTkJna3Foa2lHOXcwQkFRRUZBQVNDQktjd2dnU2pBZ0VBQW9JQkFRRE80MHNabTl4U2JVbnQKV1dsR3psMFRxL09IWVl5aGJ6enltd0ttaFR2L2pSUEY3S3JPL0JTWlRIdkN0eGtjVDN4d1FRVER2RG1RRllpZgpwemxSWEQ2RjNzaVBNSVZtZnMxU0l3OU1PS2tQYjllQmZDYjJlT0RJU2k5THEyOXEzWFRxdGlvb3p4aUJKNWZLCktWZ21tdzNoTlZMSzZxVFZlYUYzaUh1dDBGenlyQS9WZlNsTzNYSTA3Q0xnNU9ab2VNUklIRkVnd3pEd29tRWhLCtVN 29CNXc4eDQ4UW4xUnhrQ2lEWG1Zdmx4d3M1dGxHOUU0UjFNY3FjYWVpM3FPanN6cHpnVjZZTkJURk9Td0oKNEtGNW53NllQZHIwR3ZiUzJiVWFrRklodWJwOXNEU1Q3VW5ITnZ2Z09qeTBiYXJHS0dOdTB4bVNIa1NyV3FpCmcxc3BXMzJGQWdNQkFBRUNnZ0VBSGEzQ2xNSmtxVmh2UGdMUkZPeStzbmM4NE1Od01xcHNBbHVWVmtFUHVkbkoKcDkrTEkxVkxPVENkSW5JMHduaHVvQVhid3A2S1hXNlJZbUhSV2FnVGVnM2JGSnQvejVQS0xZbkZCSWl1Uy80ZAoyci91TVBablBLUlR3NVdzenBaRlV2UmQrT3U0bHJLUTFtQmdVeEdBZlF0MDlrMFcwRnZGZUE0NmRiVnRxUlNPCmFteFdiZXdMSU94OUpJbWYxWHpaTzNhZUdOR2duU1ZjenZyRnhhRzJZSWVBYVB0RUpZV2xzZHA0TWRSZWZxaTYKaXJWc0hHeXNmbkpIbFQvN2hSWGxqeWlnRWxiQzVNQi9vYXVkMnRNTnFmc2lCS1h5R3JTOVA4emE0SjROU2VNWApMS0RRMGhidUZCZmN2QU9GM1crcFBMVWhzb0FQRkNoZnMxYnhzaG1VVVFLQmdRRC9DajNhMC8zbi8rTTU5cHRyCmxhOU9oYXFNS1A2YmZwUHo2MEhvcEM1RjBlR1BYd0lWcm5zSkZYSmt0Wk1VV3BBMFo4M3VKV2V0V3VBK1ljclcKUFJXWHArT2NiMDVaNWZPb0MrNHNRMmt4T0FjeVROd1VEdEFNYlZ6bENyZVVRSmZ3Rkg4bnBhTzlqc3FrUnJ6SQpoc3pMZVZKUUpqYndxY21iQWFuWnNxTWFDUUtCZ1FEUHFxYm8wb2RrcnhaYUI4Zm1zcit3MFFGamZxNldheGI0Ck5BRHR0djZwSzlXWU5USnJhVEIwbXBaMlhaWVJkYzVNcWF2MzcvL0dtM0ZqWXFuVXBGLy9YL1Zua1VsM1RTSDQKYXpLdHVSNDhCSzh3aU0rVzhMY1lwOFk3UkZ4TEtWYnh3R05jOGJVRStNcWRFQ3ZPZGcxOU9CeWZURkNidVNvUgpTWE8wMzBuV25RS0JnUURNQzVTYlMvb2JNRFhLZlF1eGttdFVSa3JCb2xhNWJ4Yk9FczJEWkQrRktycnNxdFdQCkpTNVlnU2twZTcvMWk5Tk5xak11c2d4MXZId21UTFVzbkdoM0VpSmZXUW4xa0sxVktGNWdXWHFDbjFIYW8zVjgKTXJHdkQ1dy92MGhLdXpjVUpFSHJKWEdRU2ZyRVhiZlNNMDhNQjcrY1VrYW9XeDdwL2ZXM0wxMmdpUUtCZ0Rwcwpvb1RDSmtGWFdReC9QK2hSeGNoekpOYmZIek5HY2JIbXY1UWhkY2dXZ3dOTmhCL2YramZ6L2Z6VEc1TlI5M1p1CkRlbTFaZHAwaFJRVy8vekpPaERZNkd2NDNoaG9aUFJGQkg4SG84L3k2VzdZTHI4aWZnQzd3dk9OcWdHalljaWwKL2M1NldobEovWWJ1czhSa1JpdENqQnJ0RjRpWU1aT25mSndZYmVlcEFvR0FRalE5L1l0eHZ4QVFOM2tveEIyUApTbE1sTmlWdGI2ZXRnTGFveEsrMFB5eldOODdXOERUVlZ3ejhHZWg3ZWpKblBlTGw5TFgzcWNNQWpINE5IMnJlCnJ1Wk40U2pPM2w3ZFdGcU9RUnIwUTdyNjFLQTVLeWpIYUNYa1pvV2ZFNkdZcjExSy84VjB4bFhWbldIT0QwTGYKRVEyOUFmOTV2NTZ5RXM1OXc2MERrdGc9Ci0tLS0tRU5EIFBSSVZBVEUgS0VZLS0tLS0K".Replace(" ", ""));
    static readonly byte[] CertPem = Convert.FromBase64String(
        "LS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tCk1JSURPakNDQWlLZ0F3SUJBZ0lVTk1VdXJTWWZhSnVpRDVlWThLcHltSGh5TnFjd0RRWUpLb1pJaHZjTkFRRUwKQlFBd0hERWFNQmdHQTFVRUF3d1JWMjl5YTBKMVpHUjVJRTFKVkUwZ1EwRXdIaGNOTWpZd09ERTNNakV4TXpVeQpXaGNOTXpZd09ERTBNakV4TXpVeVdqQVZNUk13RVFZRFZRUUREQW9xTG5Wd016WTJMbU51TUlJQklqQU5CZ2txCmhraUc5dzBCQVFFRkFBT0NBUThBTUlJQkNnS0NBUUVBenVOTEdadmNVbTFKN1ZscFJzNWRFNnZ6aDJHTW9XODgKOHBzQ3BvVTcvNDBUeGV5cXp2d1VtVXg3d3JjWkhFOThjRUVFdzd3NWtCV0luNmM1VVZ3K2hkN0lqekNGWm43TgpVaU1QVERpcEQyL1hnWHdtOW5qZ3lFb3ZTNnR2YXQxMDZyWXFLTThZZ1NlWHlsWUpwc040VFZTeXVxazFYbWhkCjRoN3JkQmM4cXdQMVgwcFR0MXlOT3dpNE9UbWFIakVSeUJSSU1NdzhLSmhJU2xPNkFlY01mTWVQRUo5VWNaQW8KZzE1bUw1Y2NMT2JaUnZST0VkVEhLbkdub3Q2am83TTZjNEZlbURRVXhUa3NDU2VDaGVaOE9tRDNhOUJyMjB0bQoxR3BCU0libTZmYkEwaysxSnh6Yjc0RG84dEcycXhpaGpidE1aa2g1RXExcW9vTmJLVnQ5aFFJREFRQUJvM3N3CmVUQTNCZ05WSFJFRU1EQXVnZ29xTG5Wd016WTJMbU51Z2doMWNETTJOaTVqYm9JTEtpNTFjRE0yTmk1amIyMkMKQ1hWd016WTJMbU52YlRBZEJnTlZIUTRFRmdRVUc4bW94Q3pzbUpadDQ4N0RCTmQwcTdxQ1BsOHdId1lEVlIwagpCQmd3Rm9BVVVpSXNHTWxsY2VJZk1QaFF5dzhsSDd2LzhCc3dEUVlKS29aSWh2Y05BUUVMQlFBRGdnRUJBQVhpCjBKMVdwTXJhVlhqbk94cjZjTENTWGRhOWZlRUd6blBSYU93L1o0bkFsdGwza0dabTJiY2VyUGRxZUFMOXN5RWkKZVo2ZEhHTnBMbmNMZlRENGF5MktyMFVOK2RoMk85TldsaXVRd2pHb2xENUt2ZEJ2MUtFdEFmY2o2bmJhY2t0KwpRbkZ5cDhiNmU4N2I1VW81T2w2NU5PcGFxREVLYmUvMEdWdmxaSzN3ZEx4QWZZak9iRGJiZHNDaGQ1Vk9yK2tlCkpyMzAzV0hQdXcvVERnUFdCd25HbDQ5cjdGckM1NkJsaVJ1VnZYWisrK1I2bFEybzhCdTBzRnV2cXNORXNzODIKYkVzZWxwaGZ2MitycnkrRDIxcXllbW5mNjdQZVd3dGdjTlhrRC9mUlRIVVJhK3dBWXlSNkxETnQwU1F0OWI4YwpEMlBxeEZteEl3am1hU1lOaXpzPQotLS0tLUVORCBDRVJUSUZJQ0FURS0tLS0tCg==".Replace(" ", ""));

    static X509Certificate2? _cert;
    static X509Certificate2 Cert()
    {
        if (_cert != null) return _cert;
        _cert = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(CertPem), Encoding.ASCII.GetString(KeyPem));
        return _cert;
    }

    TcpListener? _listener;
    CancellationTokenSource? _cts;
    StreamWriter? _log;
    readonly object _lock = new();
    int _port;
    public bool Running => _listener != null;
    public int Port => _port;
    public string LogPath => Path.Combine(AppPaths.MitmDir, "captured.log");

    public string Start(int port)
    {
        if (_listener != null) return "代理已在运行（端口 " + _port + "）";
        try { Directory.CreateDirectory(AppPaths.MitmDir); } catch { }
        try { _log = new StreamWriter(LogPath, append: true) { AutoFlush = true }; } catch { }

        int p = port <= 0 ? 8899 : port;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, p);
            _listener.Start();
        }
        catch (Exception e)
        {
            _listener = null;
            return "代理启动失败（端口 " + p + " 被占用或无权监听）: " + e.Message;
        }
        _port = p;
        _cts = new CancellationTokenSource();
        Log("MITM proxy listening on " + p + "（内嵌模式，证书已内置；请把系统/客户端代理指向 127.0.0.1:" + p + "）");
        _ = Task.Run(() => StartTask(_cts.Token));
        return "代理已启动，端口 " + p + "，日志 " + LogPath;
    }

    public string Stop()
    {
        if (_listener == null) return "代理未运行";
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _listener = null; _port = 0;
        lock (_lock) { try { _log?.Dispose(); } catch { } _log = null; }
        return "代理已停止";
    }

    void Log(string msg)
    {
        string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg;
        lock (_lock) { try { _log?.WriteLine(line); } catch { } }
    }

    async Task StartTask(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            stream.ReadTimeout = 30000;

            // 读第一行判断 CONNECT / 普通 HTTP
            var firstLine = await ReadLineAsync(stream, ct);
            if (firstLine == null) return;
            if (firstLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
            {
                var target = firstLine.Substring(8).Split(' ')[0];
                var hp = target.Split(':');
                string host = hp[0]; int port = hp.Length > 1 && int.TryParse(hp[1], out var pp) ? pp : 443;
                Log("CONNECT " + host + ":" + port + (Focus.IsMatch(host) ? "  [MITM]" : "  [tunnel]"));
                // 读取剩余头部（直到空行），丢弃
                string line;
                do { line = await ReadLineAsync(stream, ct) ?? ""; } while (line.Length > 0);

                if (!Focus.IsMatch(host))
                {
                    await TunnelAsync(client, stream, host, port, ct);
                    return;
                }
                // 响应 200 后做 TLS MITM
                await WriteAsciiAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct);
                await HandleMitmAsync(client, stream, host, ct);
                return;
            }

            // 普通 HTTP：转发并记录
            await HandlePlainHttpAsync(client, stream, firstLine, ct);
        }
        catch { }
        finally { try { client.Dispose(); } catch { } }
    }

    async Task HandlePlainHttpAsync(TcpClient client, Stream stream, string firstLine, CancellationToken ct)
    {
        // 读头部
        var head = new StringBuilder(firstLine + "\r\n");
        string? line;
        while (!string.IsNullOrEmpty(line = await ReadLineAsync(stream, ct))) head.AppendLine(line);
        string host = HeaderValue(head.ToString(), "host");
        if (host.Length == 0) return;
        Log(">> HTTP " + MethodOf(firstLine) + " " + host + UriOf(firstLine));

        using var upstream = new TcpClient();
        string uhost = host.Split(':')[0];
        int uport = host.Contains(':') && int.TryParse(host.Split(':')[1], out var up) ? up : 80;
        try { await upstream.ConnectAsync(uhost, uport, ct); } catch { return; }
        var us = upstream.GetStream();
        await WriteAsciiAsync(us, head.ToString(), ct);
        await PumpAsync(stream, us, ct);   // 请求体 → 上游
        await PumpAsync(us, stream, ct);   // 响应 → 客户端
    }

    async Task HandleMitmAsync(TcpClient client, Stream rawStream, string host, CancellationToken ct)
    {
        var ssl = new SslStream(rawStream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = Cert(),
            ClientCertificateRequired = false,
        }, ct);

        string? reqLine = await ReadLineAsync(ssl, ct);
        while (!string.IsNullOrEmpty(reqLine))
        {
            var head = new StringBuilder(reqLine + "\r\n");
            string? line;
            while (!string.IsNullOrEmpty(line = await ReadLineAsync(ssl, ct))) head.AppendLine(line);
            await ForwardHttpsAsync(ssl, head.ToString(), host, ct);
            reqLine = await ReadLineAsync(ssl, ct);
        }
    }

    async Task ForwardHttpsAsync(Stream clientStream, string headText, string connectHost, CancellationToken ct)
    {
        string[] lines = headText.Split("\r\n");
        string reqLine = lines[0];
        var m = Regex.Match(reqLine, @"^(\S+) (\S+) HTTP");
        if (!m.Success) return;
        string method = m.Groups[1].Value, fullPath = m.Groups[2].Value;
        string host = connectHost;
        foreach (var l in lines.Skip(1))
        {
            if (l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) { host = l.Substring(5).Trim(); break; }
        }
        bool focused = Focus.IsMatch(host);
        if (focused) Log(">> " + method + " " + host + fullPath);

        using var upstream = new TcpClient();
        string uhost = host.Split(':')[0];
        int uport = host.Contains(':') && int.TryParse(host.Split(':')[1], out var up) ? up : 443;
        try { await upstream.ConnectAsync(uhost, uport, ct); } catch { return; }
        var sslUp = new SslStream(upstream.GetStream(), leaveInnerStreamOpen: false,
            (_, _, _, _) => true); // 信任上游证书（含自签名）
        await sslUp.AuthenticateAsClientAsync(uhost);
        await WriteAsciiAsync(sslUp, headText, ct);

        // 请求体：Content-Length 定长，或 chunked 分块（两者都不支持会导致 POST 请求被截断、上游一直等 body）
        long bodyLen = ContentLength(headText);
        bool reqChunked = HeaderValue(headText, "transfer-encoding").Contains("chunked", StringComparison.OrdinalIgnoreCase);
        if (bodyLen > 0)
        {
            var buf = new byte[bodyLen];
            int got = 0;
            while (got < bodyLen)
            {
                int n = await clientStream.ReadAsync(buf.AsMemory(got, (int)(bodyLen - got)), ct);
                if (n <= 0) break;
                got += n;
            }
            if (got > 0) await sslUp.WriteAsync(buf.AsMemory(0, got), ct);
        }
        else if (reqChunked)
        {
            await PumpChunkedAsync(clientStream, sslUp, ct);
        }

        // 读响应头
        var resHead = new StringBuilder();
        string? rl;
        while (!string.IsNullOrEmpty(rl = await ReadLineAsync(sslUp, ct))) resHead.AppendLine(rl);
        if (resHead.Length == 0) return;

        // 回给客户端：状态行原样保留（含 reason phrase），剥 Connection；
        // chunked 响应必须保留 Transfer-Encoding 头，否则客户端无法解析分块体（此前 bug：body 整体丢失）
        string resHeadText = resHead.ToString();
        bool resChunked = HeaderValue(resHeadText, "transfer-encoding").Contains("chunked", StringComparison.OrdinalIgnoreCase);
        var status = Regex.Match(resHeadText, @"^(HTTP/[\d.]+\s+\d+[^\r\n]*)");
        await WriteAsciiAsync(clientStream, (status.Success ? status.Groups[1].Value : "HTTP/1.1 200") + "\r\n", ct);
        foreach (var l in resHeadText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            string lower = l.ToLowerInvariant();
            if (lower.StartsWith("connection")) continue;
            if (lower.StartsWith("transfer-encoding") && !resChunked) continue;
            await WriteAsciiAsync(clientStream, l + "\r\n", ct);
        }
        await WriteAsciiAsync(clientStream, "Connection: close\r\n\r\n", ct);

        // 响应体（gzip 等按原始字节转发；记录关键包）
        long resBodyLen = ContentLength(resHeadText);
        if (resBodyLen > 0)
        {
            var body = new byte[resBodyLen];
            int got = 0;
            while (got < resBodyLen)
            {
                int n = await sslUp.ReadAsync(body.AsMemory(got, (int)(resBodyLen - got)), ct);
                if (n <= 0) break;
                got += n;
            }
            await clientStream.WriteAsync(body.AsMemory(0, got), ct);
            if (focused && FileLike.IsMatch(fullPath))
            {
                string snip = Snip(body, got);
                Log("<< " + host + fullPath + " [" + got + "B] " + snip);
            }
        }
        else if (resChunked)
        {
            // chunked 响应：逐帧原样转发（帧结构自解释，读终止帧 0\r\n\r\n 后正常结束，
            // 不依赖任一端关闭连接——用 PumpAsync 会挂在上游 keep-alive 的读上导致连接泄漏）
            await PumpChunkedAsync(sslUp, clientStream, ct);
            if (focused && FileLike.IsMatch(fullPath))
                Log("<< " + host + fullPath + " [chunked] 已转发");
        }
        else if (focused && FileLike.IsMatch(fullPath))
        {
            Log("<< " + host + fullPath + " [无body]");
        }
    }

    /// <summary>
    /// HTTP chunked 逐帧转发：size 行（hex）+ 数据 + CRLF，直到 0 终止帧（含 trailer，读到空行收尾）。
    /// 读取/转发的每一部分都写回对端，帧格式原样保持，对端无需额外头部即可解析。
    /// </summary>
    static async Task PumpChunkedAsync(Stream from, Stream to, CancellationToken ct)
    {
        while (true)
        {
            string? sizeLine = await ReadLineAsync(from, ct);
            if (sizeLine == null) return;                       // 对端断开
            await WriteAsciiAsync(to, sizeLine + "\r\n", ct);
            if (!int.TryParse(sizeLine.Trim().Split(';')[0], System.Globalization.NumberStyles.HexNumber, null, out int size))
                return;                                          // 异常帧，放弃
            if (size == 0)
            {
                // 终止帧：后续是 trailer 头（可为空），以空行结束
                string? tl;
                do
                {
                    tl = await ReadLineAsync(from, ct);
                    if (tl == null) return;
                    await WriteAsciiAsync(to, tl + "\r\n", ct);
                } while (tl.Length > 0);
                return;
            }
            var buf = new byte[size];
            int got = 0;
            while (got < size)
            {
                int n = await from.ReadAsync(buf.AsMemory(got, size - got), ct);
                if (n <= 0) return;
                got += n;
            }
            await to.WriteAsync(buf.AsMemory(0, size), ct);
            var crlf = new byte[2];
            int r = 0;
            while (r < 2)
            {
                int n = await from.ReadAsync(crlf.AsMemory(r, 2 - r), ct);
                if (n <= 0) return;
                r += n;
            }
            await to.WriteAsync(crlf, ct);
        }
    }

    static async Task TunnelAsync(TcpClient client, Stream clientStream, string host, int port, CancellationToken ct)
    {
        using var up = new TcpClient();
        try { await up.ConnectAsync(host, port, ct); } catch { return; }
        await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct);
        var upStream = up.GetStream();
        var t1 = PumpAsync(clientStream, upStream, ct);
        var t2 = PumpAsync(upStream, clientStream, ct);
        await Task.WhenAny(t1, t2);
    }

    static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buf = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await from.ReadAsync(buf, ct);
                if (n <= 0) break;
                await to.WriteAsync(buf.AsMemory(0, n), ct);
            }
        }
        catch { }
    }

    // ---------- 小工具 ----------
    static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        int b;
        var buf = new byte[1];
        while ((b = await s.ReadAsync(buf, ct)) > 0)
        {
            byte c = buf[0];
            if (c == '\n') return sb.ToString();
            if (c != '\r') sb.Append((char)c);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    static async Task WriteAsciiAsync(Stream s, string text, CancellationToken ct)
    {
        var b = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(b, ct);
        await s.FlushAsync(ct);
    }

    static string HeaderValue(string head, string name)
    {
        foreach (var l in head.Split("\r\n"))
            if (l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) return l.Substring(name.Length + 1).Trim();
        return "";
    }

    static long ContentLength(string head)
    {
        var v = HeaderValue(head, "Content-Length");
        return long.TryParse(v, out var n) ? n : 0;
    }

    static string MethodOf(string firstLine) => firstLine.Split(' ')[0];
    static string UriOf(string firstLine) => firstLine.Split(' ').Length > 1 ? firstLine.Split(' ')[1] : "/";

    static string Snip(byte[] body, int len)
    {
        int take = Math.Min(len, 200);
        var sb = new StringBuilder(take);
        for (int i = 0; i < take; i++)
        {
            char c = (char)body[i];
            sb.Append(c >= 0x20 && c <= 0x7e ? c : '.');
        }
        return sb.ToString();
    }
}
