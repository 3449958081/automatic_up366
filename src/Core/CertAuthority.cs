using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TxwExtract.Core;

/// <summary>
/// 运行时根 CA + 动态叶子证书签发（DOM 注入方案的基础设施）。
///
/// 为什么不用固定的叶子证书：MITM 任意 host 时需要「按 host 现签」的证书，
/// 只有持有 CA 私钥才能签。这里首次运行生成一张自签 CA（持久化到数据目录），
/// 之后对每个被拦截的 host 现签一张叶子证书并缓存。
///
/// 线程安全：叶子缓存用 ConcurrentDictionary；CA 用 lock 保护懒加载。
/// 注意：安装到 LocalMachine\Root 需要管理员权限（主程序已自提权）。
/// </summary>
public static class CertAuthority
{
    /// <summary>根 CA 的显示名（出现在证书存储里，卸载时按此名清理）。</summary>
    public const string CaSubject = "CN=TxwExtract Local CA, O=TxwExtract";

    static readonly object _caLock = new();
    static X509Certificate2? _ca;
    static readonly ConcurrentDictionary<string, X509Certificate2> _leaves = new(StringComparer.OrdinalIgnoreCase);

    static string CaPfxPath => Path.Combine(AppPaths.DataDir, "mitm", "ca.pfx");
    const string PfxPassword = "txw-extract-ca";

    /// <summary>根 CA（含私钥）。首次访问时生成并落盘。</summary>
    public static X509Certificate2 Ca
    {
        get
        {
            if (_ca != null) return _ca;
            lock (_caLock)
            {
                if (_ca != null) return _ca;
                _ca = LoadOrCreateCa();
                return _ca;
            }
        }
    }

    static X509Certificate2 LoadOrCreateCa()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(CaPfxPath)!); } catch { }

        // 已存在 → 复用（保证证书指纹稳定，不必每次重装）
        if (File.Exists(CaPfxPath))
        {
            try
            {
                var existing = new X509Certificate2(CaPfxPath, PfxPassword,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                if (existing.HasPrivateKey && existing.NotAfter > DateTime.Now.AddDays(1))
                    return existing;
            }
            catch { /* 陈旧/损坏 → 重建 */ }
        }

        X509Certificate2 created = CreateCa();
        try { File.WriteAllBytes(CaPfxPath, created.Export(X509ContentType.Pfx, PfxPassword)); } catch { }
        return created;
    }

    static X509Certificate2 CreateCa()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(CaSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // 转成带「可导出私钥 + 临时密钥集」的实例，避免污染用户密钥容器
        return new X509Certificate2(cert.Export(X509ContentType.Pfx, PfxPassword), PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>手写 AKI 扩展的 DER：AuthorityKeyIdentifier ::= SEQUENCE { [0] KeyIdentifier }。</summary>
    static byte[] MakeAkiDer(X509SubjectKeyIdentifierExtension ski)
    {
        byte[] kid = ski.SubjectKeyIdentifierBytes.Span.ToArray();
        // OCTET STRING(04 len kid) → [0](A0 len ...) → SEQUENCE(30 len ...)
        var oct = new List<byte> { 0x04, (byte)kid.Length };
        oct.AddRange(kid);
        var tag0 = new List<byte> { 0xA0, (byte)oct.Count };
        tag0.AddRange(oct);
        var seq = new List<byte> { 0x30, (byte)tag0.Count };
        seq.AddRange(tag0);
        return seq.ToArray();
    }

    /// <summary>CA 剩余有效天数（负数表示已过期）。</summary>
    public static int CaRemainingDays
    {
        get { try { return (int)(Ca.NotAfter - DateTime.Now).TotalDays; } catch { return -1; } }
    }

    /// <summary>
    /// 清空叶子证书缓存（下一位 host 会重新现签）。
    /// 用于：CA 被重建/重装、叶子临近过期、或怀疑证书被客户端缓存了错误状态时。
    /// </summary>
    public static void InvalidateLeaves() => _leaves.Clear();

    /// <summary>
    /// 【期限自动补偿】CA 临期/过期时自动续期：重建 CA → 卸载旧 CA → 重新装入受信任根 → 清叶子缓存。
    /// 这是必须的：CA 一旦过期，它签过的所有叶子立刻全部失效，而旧实现只在「剩余不足 1 天」才重建，
    /// 且重建后不会重新安装（新指纹不在 Root 区）→ 到那天接管会整体失效且难以定位。
    /// <paramref name="minDays"/> 提前续期阈值（默认 90 天）。
    /// </summary>
    public static (bool Renewed, string Msg) EnsureCaFresh(int minDays = 90)
    {
        try
        {
            int left = CaRemainingDays;
            if (left >= minDays) return (false, $"CA 有效，剩余 {left} 天");
            // 先卸载旧 CA（按旧指纹），再重建并安装
            int removed = Uninstall();
            lock (_caLock) { _ca = null; InvalidateLeaves(); }
            var oldPfx = CaPfxPath;
            try { if (File.Exists(oldPfx)) File.Copy(oldPfx, oldPfx + ".bak", true); } catch { }
            try { if (File.Exists(oldPfx)) File.Delete(oldPfx); } catch { }

            var fresh = Ca;                       // 触发重建（新的 10 年）
            try { File.WriteAllBytes(CaPfxPath, fresh.Export(X509ContentType.Pfx, PfxPassword)); } catch { }
            var r = Install();
            return (true, $"CA 已自动续期（原剩余 {left} 天 → 新 CA 到 {fresh.NotAfter:yyyy-MM-dd}，"
                          + $"已移除旧 CA {removed} 份；重装：{r.Msg}）");
        }
        catch (Exception e) { return (false, "CA 续期失败：" + e.Message); }
    }

    /// <summary>取（或现签）host 对应的叶子证书；host 为不含端口的域名。</summary>
    public static X509Certificate2 GetLeaf(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) host = "localhost";
        if (_leaves.TryGetValue(host, out var cached))
        {
            // 【期限自动补偿】叶子临近过期（<7 天）时丢弃重签，避免长期运行下证书静默失效
            if (cached.NotAfter > DateTime.Now.AddDays(7)) return cached;
            _leaves.TryRemove(host, out _);
        }
        return _leaves.GetOrAdd(host, CreateLeaf);
    }

    static X509Certificate2 CreateLeaf(string host)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}, O=TxwExtract", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));   // serverAuth

        var san = new SubjectAlternativeNameBuilder();
        // 通配 host（如 *.up366.cn）按字面加入；同时补一条同名 DNS，保证匹配
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        // ⚠️ 关键：Chromium/Chrome 强制要求叶子证书带 AKI（Authority Key Identifier），
        // 否则无法把叶子链到根 → 客户端报
        //   net::ERR_CERT_... / CERTIFICATE_VERIFY_FAILED: Missing Authority Key Identifier
        // 表现为「hosts 已劫持、本机 443 也在监听，但客户端下载全部失败」。
        // .NET 的 CertificateRequest.Create() 不会自动补 AKI，必须显式加。
        try
        {
            req.CertificateExtensions.Add(
                X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        }
        catch
        {
            // 兜底：手写 DER（SEQUENCE { [0] keyIdentifier }）
            var ski = Ca.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault();
            if (ski != null) req.CertificateExtensions.Add(new X509Extension("2.5.29.35", MakeAkiDer(ski), false));
        }

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);

        // Chromium 另一个硬性要求：证书有效期不得超过 398 天（超了直接判无效），留余量取 395 天
        var notAfter = Ca.NotAfter.AddDays(-1);
        if (notAfter > DateTime.Now.AddDays(395)) notAfter = DateTime.Now.AddDays(395);

        var leafNoKey = req.Create(Ca, DateTimeOffset.UtcNow.AddDays(-1), notAfter, serial);
        using var leafWithKey = leafNoKey.CopyWithPrivateKey(rsa);
        // 导出为 PFX 再载入，得到「带私钥、可被 SslStream 使用」的实例。
        // 注意：**不能用 EphemeralKeySet** —— Windows 平台不支持用临时密钥做服务端 TLS，
        // SslStream 会报 "the platform does not support ephemeral keys"（实测踩坑）。
        return new X509Certificate2(leafWithKey.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    /// <summary>CA 是否已在受信任根证书区（本机或当前用户其一）。</summary>
    public static bool IsInstalled() => InStore(StoreLocation.LocalMachine) || InStore(StoreLocation.CurrentUser);

    static bool _userOnly;
    /// <summary>是否只装到了当前用户（未及本机）。</summary>
    public static bool UserOnly => _userOnly;

    static bool InStore(StoreLocation loc)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, loc);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            string want = Ca.Thumbprint;
            foreach (var c in store.Certificates)
                if (string.Equals(c.Thumbprint, want, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 把 CA 装入受信任根证书区。优先当前用户（免管理员），失败再试本机（需管理员）。
    /// 返回 (成功, 说明)。
    /// </summary>
    public static (bool Ok, string Msg) Install()
    {
        if (InStore(StoreLocation.CurrentUser)) { _userOnly = true; return (true, "CA 证书已在本机受信任根证书区（当前用户）"); }
        if (InStore(StoreLocation.LocalMachine)) { _userOnly = false; return (true, "CA 证书已在本机受信任根证书区（所有用户）"); }

        if (TryAdd(StoreLocation.CurrentUser))
        {
            _userOnly = true;
            return (true, "CA 证书已安装（当前用户，无需管理员）");
        }
        if (TryAdd(StoreLocation.LocalMachine))
        {
            _userOnly = false;
            return (true, "CA 证书已安装（所有用户）");
        }
        return (false, "安装 CA 失败：当前用户/本机证书区都写入失败");
    }

    static bool TryAdd(StoreLocation loc)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, loc);
            store.Open(OpenFlags.ReadWrite);
            using var pub = new X509Certificate2(Ca.Export(X509ContentType.Cert));
            store.Add(pub);
            store.Close();
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (Exception) { return false; }
    }

    /// <summary>从受信任根证书区移除本工具的 CA（本机 + 当前用户）。返回移除数量。</summary>
    public static int Uninstall()
    {
        int removed = 0;
        foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, loc);
                store.Open(OpenFlags.ReadWrite);
                string want = Ca.Thumbprint;
                var hit = store.Certificates.Cast<X509Certificate2>()
                    .Where(c => string.Equals(c.Thumbprint, want, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var c in hit) { store.Remove(c); removed++; }
                store.Close();
            }
            catch { }
        }
        return removed;
    }
}
