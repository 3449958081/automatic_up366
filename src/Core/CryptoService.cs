using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxwExtract.Core;

/// <summary>
/// 天学网本地加密文件解密。四套方案全部对照客户端 <c>dist/main/index.js</c> 逐字节核对（见
/// <c>_research/DOM可行性研究结论.md</c> 第十节）：
///
///  ① <b>.u3enc V1</b> —— AES-128-CBC，无魔数头：[16B IV][密文]，密钥固定 <see cref="KeyV1"/>。
///  ② <b>.u3enc V2</b> —— AES-128-CBC：[12B 魔数][32B 采样 md5][16B IV][密文]，密钥 <see cref="KeyV2"/>。
///     两者都由客户端函数 <c>Zg(A,e,t,r)</c> 处理，两个密钥都是它的 <c>r</c> 参数默认值：
///     <c>i = Buffer.from(r || "dv8UqXZ…")</c> → 读到魔数则按 V2 走；否则 <c>i</c> 被改写为 <c>"QJBNiBmV…"</c> 走 V1。
///     ⚠️ V1/V2 密钥不同，混用必然 <c>bad decrypt</c>。
///  ③ <b>.u3c / 内容资源</b> —— AES-128-GCM（IV 12B + TAG 16B），密钥不在文件里：由同目录（或祖先目录）
///     的 <c>indexes.css.u3enc</c>（本身是 ② 格式）解出 <c>{extTime, config}</c>，再经 <c>decryptEdk</c>
///     推出内容密钥（32 位 hex）。见 <see cref="DeriveContentKey"/>。
///  ④ <b>账号/参数小文件</b> —— 客户端自定义 <c>DL</c> 流密码（rounds=20）或原生 asar codec；
///     JS 层无密钥，走客户端自身原生模块（见 <see cref="U3eDumper"/>）。
///
/// 密钥获取分四级（由弱到强）：内置常量 → 用户 keys.json → 客户端文件静态抽取
/// （<see cref="ClientKeyScanner"/>）→ 客户端进程内存扫描（<see cref="KeyFinder"/>）。
/// </summary>
public static class CryptoService
{
    /// <summary>V1 内置密钥（.u3enc 无魔数头格式）。客户端 <c>Zg()</c> 的 V1 分支硬编码值。</summary>
    public const string KeyV1 = "QJBNiBmV55PDrewyne3GsA==";

    /// <summary>V2 内置密钥（.u3enc 12B 魔数头格式）。客户端 <c>Zg(A,e,t,r)</c> 的 <c>r</c> 参数默认值。</summary>
    public const string KeyV2 = "dv8UqXZbadxSEllGtpOlKQ==";

    /// <summary>内置密钥（V2 放前：考试模式等新格式占比更高，先试命中率更高）。
    /// 用户新增密钥存 keys.json。</summary>
    public static readonly List<string> BuiltinKeys = new() { KeyV2, KeyV1 };

    /// <summary>V2 文件的 12 字节魔数头：<c>base64("EluBMWJoaRIev40b")</c>。
    /// 布局 [12B 魔数][32B 文件完整性哈希][16B IV][AES-CBC 密文]。
    /// 实测：IV 在偏移 44（旧实现误把哈希前 16 字节当 IV 在 12，CBC 下仅首块乱码，
    /// LooksPlain 对首块噪声的随机通过/拒绝表现为"部分文件解不开"的假象）。
    /// </summary>
    public static readonly byte[] MagicV2 = { 0x12, 0x5B, 0x81, 0x31, 0x62, 0x68, 0x69, 0x12, 0x1E, 0xBF, 0x8D, 0x1B };

    /// <summary>内容资源（AES-GCM）方案的字符串异或密钥（客户端 <c>Ws.XOR_KEY</c>）。</summary>
    public const string GcmXorKey = "Up366@2025#Obf";

    /// <summary>客户端字符串混淆 <c>xD(hex, e=90)</c> 的默认异或字节。</summary>
    public const int StrXor = 90;

    // ---------------- 密钥环 ----------------

    static List<string> _keys = new();
    static readonly Dictionary<string, int> _fmt = new(StringComparer.Ordinal);   // 密钥 → 实测成功的格式（1=V1 / 2=V2）
    static int _lastKeyIdx = 0;

    public static IReadOnlyList<string> Keys => _keys;

    /// <summary>最近一次解密成功的密钥（前端"当前生效"标记用；null = 尚未成功解密过）。</summary>
    public static string? ActiveKey => _lastKeyIdx >= 0 && _lastKeyIdx < _keys.Count ? _keys[_lastKeyIdx] : null;

    /// <summary>最近一次成功解密的格式（1=V1 / 2=V2 / 0=未知）。</summary>
    public static int ActiveFormat { get; private set; }

    /// <summary>把内置密钥的已知格式归属写进 <c>_fmt</c>（尝试顺序优化用）。</summary>
    static void SeedKnownFormats()
    {
        _fmt[KeyV1] = 1;
        _fmt[KeyV2] = 2;
    }

    /// <summary>从 keys.json 读取用户自定义密钥（格式 {"keys":[...]}，与 Node 版一致）。</summary>
    public static List<string> LoadCustomKeys()
    {
        try
        {
            if (File.Exists(AppPaths.KeysPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.KeysPath));
                if (doc.RootElement.TryGetProperty("keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    return arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                             .Select(x => x.GetString() ?? "").ToList();
            }
        }
        catch { }
        return new List<string>();
    }

    public static void SaveCustomKeys(IEnumerable<string> custom)
    {
        try
        {
            var obj = new { keys = custom.ToList() };
            File.WriteAllText(AppPaths.KeysPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>把一批新密钥并入 keys.json（去重、剔除内置与非法项），返回实际新增数。</summary>
    public static int MergeCustomKeys(IEnumerable<string> found)
    {
        var custom = LoadCustomKeys();
        var seen = new HashSet<string>(custom, StringComparer.Ordinal);
        int added = 0;
        foreach (var raw in found)
        {
            var k = (raw ?? "").Trim();
            if (k.Length == 0 || BuiltinKeys.Contains(k) || !IsValidKey(k) || !seen.Add(k)) continue;
            custom.Add(k); added++;
        }
        if (added > 0) { SaveCustomKeys(custom); Reload(); }
        return added;
    }

    /// <summary>合法 .u3enc 密钥：base64 解码后恰好 16 字节。</summary>
    public static bool IsValidKey(string b64)
    {
        try { return Convert.FromBase64String((b64 ?? "").Trim()).Length == 16; }
        catch { return false; }
    }

    public static void LoadKeys(IEnumerable<string> builtin, IEnumerable<string> custom)
    {
        _keys = builtin.Concat(custom)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Where(IsValidKey)
            .Distinct().ToList();
        _fmt.Clear();
        SeedKnownFormats();
    }

    public static void Reload()
    {
        LoadKeys(BuiltinKeys, LoadCustomKeys());
        if (_lastKeyIdx >= _keys.Count) _lastKeyIdx = 0;
    }

    /// <summary>按文件格式排出的密钥尝试顺序：实测同格式 → 未定格式 → 实测他格式（避免拿 V1 密钥空耗 V2 文件）。</summary>
    static IEnumerable<int> KeyOrder(int ver)
    {
        int last = _lastKeyIdx >= 0 && _lastKeyIdx < _keys.Count ? _lastKeyIdx : 0;
        return Enumerable.Range(0, _keys.Count)
            .OrderBy(i => i == last ? 0
                        : _fmt.TryGetValue(_keys[i], out var f) ? (f == ver ? 1 : 3) : 2)
            .ThenBy(i => i)
            .ToList();
    }

    // ---------------- 基础：AES-CBC / AES-GCM ----------------

    const int Block = 16;

    static byte[] AesCbcBytes(byte[] key, byte[] iv, byte[] data, bool decrypt)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var tf = decrypt ? aes.CreateDecryptor() : (ICryptoTransform)aes.CreateEncryptor();
        return tf.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>AES-128-GCM（客户端 <c>Ws</c> 方案）：IV 12B、TAG 16B。</summary>
    static byte[] AesGcmBytes(byte[] key, byte[] iv, byte[] cipher, byte[] tag, bool decrypt)
    {
        using var gcm = new AesGcm(key, 16);
        var outp = new byte[cipher.Length];
        if (decrypt) gcm.Decrypt(iv, cipher, tag, outp);
        else gcm.Encrypt(iv, cipher, tag, outp);
        return outp;
    }

    // ---------------- 明文/内容判定 ----------------

    /// <summary>启发式判断解密结果是否为合理明文（控制字符极少）。</summary>
    static bool LooksPlain(byte[] b)
    {
        int n = Math.Min(b.Length, 200), bad = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c < 0x09 || (c > 0x0d && c < 0x20)) bad++;
        }
        return bad < 3;
    }

    /// <summary>更强的"确实是课程内容"判定：可打印率 ≥ 90% 且出现内容语法字符。
    /// 用于静态扫描时的误报抑制（随机密钥偶尔能通过 LooksPlain，但很难同时满足此条件）。</summary>
    static bool LooksLikeContent(byte[] b)
    {
        int n = Math.Min(b.Length, 512);
        if (n == 0) return false;
        int ok = 0; bool sig = false;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c == 9 || c == 10 || c == 13 || (c >= 32 && c <= 126) || c >= 0x80) ok++;
            if (c is (byte)'<' or (byte)'{' or (byte)'=' or (byte)'(' or (byte)';' or (byte)',') sig = true;
        }
        return ok >= n * 0.9 && sig;
    }

    static bool IsV2(byte[] buf)
    {
        if (buf.Length <= 28) return false;
        for (int i = 0; i < MagicV2.Length; i++) if (buf[i] != MagicV2[i]) return false;
        return true;
    }

    /// <summary>文件格式：1=V1（无头）、2=V2（魔数头）、0=长度不足无法判断。</summary>
    public static int FormatOf(byte[] buf)
        => buf.Length < 32 ? 0 : IsV2(buf) ? 2 : 1;

    static bool SliceFor(byte[] buf, int ver, out byte[] iv, out byte[] ct)
    {
        iv = Array.Empty<byte>(); ct = Array.Empty<byte>();
        if (ver == 2) { if (buf.Length < 60) return false; iv = buf[44..60]; ct = buf[60..]; }
        else { if (buf.Length < 32) return false; iv = buf[..16]; ct = buf[16..]; }
        return ct.Length > 0 && ct.Length % Block == 0;
    }

    // ---------------- 解密 ----------------

    /// <summary>解密字节（按格式排出的密钥顺序逐个尝试，命中后记住该密钥与格式以加速后续解密）。</summary>
    public static string Decrypt(byte[] buf) => Decrypt(buf, out _, out _);

    /// <summary>解密字节，并回报实际使用的密钥与格式（供 UI 展示与统计）。</summary>
    public static string Decrypt(byte[] buf, out string usedKey, out int ver)
    {
        usedKey = ""; ver = FormatOf(buf);
        if (ver == 0) throw new InvalidDataException("文件过小");
        if (!SliceFor(buf, ver, out var iv, out var ct))
            throw new InvalidDataException("密文长度不是 16 的倍数（未知格式）");

        Exception? firstErr = null;
        foreach (int i in KeyOrder(ver))
        {
            try
            {
                var plain = AesCbcBytes(Convert.FromBase64String(_keys[i]), iv, ct, decrypt: true);
                if (!LooksPlain(plain)) continue;
                _lastKeyIdx = i; ActiveFormat = ver;
                usedKey = _keys[i];
                _fmt[usedKey] = ver;          // 实测归属，优化后续顺序
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception e) { firstErr ??= e; }
        }
        throw new CryptographicException(
            $"所有已知密钥（{_keys.Count} 个）均解密失败——天学网可能已更换密钥。请用「密钥管理 → 从客户端源码查找 / 扫描内存」获取新密钥。（{firstErr?.Message}）");
    }

    public static string DecryptFile(string path) => Decrypt(File.ReadAllBytes(path));

    /// <summary>用指定密钥试解样本（自动识别 V1/V2 格式）。供密钥搜索验证命中。</summary>
    public static bool TryDecryptSample(byte[] buf, byte[] key) => TryDecryptSampleFormat(buf, key, out _);

    /// <summary>用指定密钥试解样本，并回报命中的格式（1=V1 / 2=V2）。</summary>
    public static bool TryDecryptSampleFormat(byte[] buf, byte[] key, out int ver)
    {
        ver = FormatOf(buf);
        if (ver == 0 || key.Length != 16) return false;
        if (!SliceFor(buf, ver, out var iv, out var ct)) return false;
        try { return LooksPlain(AesCbcBytes(key, iv, ct, decrypt: true)); }
        catch { return false; }
    }

    /// <summary>用指定密钥试解样本，并判断解出的内容确实像课程内容（静态扫描用，抑制误报）。</summary>
    public static bool TryDecryptSampleStrict(byte[] buf, byte[] key)
    {
        int ver = FormatOf(buf);
        if (ver == 0 || key.Length != 16) return false;
        if (!SliceFor(buf, ver, out var iv, out var ct)) return false;
        try { return LooksLikeContent(AesCbcBytes(key, iv, ct, decrypt: true)); }
        catch { return false; }
    }

    // ---------------- 加密（对称，供自测/回写校验） ----------------

    /// <summary>
    /// 加密（与 <see cref="Decrypt"/> 的 V1 分支严格对称）：AES-128-CBC + PKCS7，随机 IV 前置 16 字节。
    /// 密钥取当前生效密钥（最近一次解密成功者），无则用第一个可用密钥。
    /// ⚠️ 只产出 V1 形态；客户端 V2 文件另有 32 字节采样哈希头，回写 V1 形态会触发客户端的完整性检查。
    /// </summary>
    public static byte[] Encrypt(string plain)
    {
        string? key = ActiveKey;
        if (string.IsNullOrEmpty(key)) key = _keys.Count > 0 ? _keys[0] : null;
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("没有可用密钥（请先执行一次解密或补充密钥）");

        var iv = RandomNumberGenerator.GetBytes(16);
        var data = Encoding.UTF8.GetBytes(plain ?? "");
        var ct = AesCbcBytes(Convert.FromBase64String(key), iv, data, decrypt: false);
        var outp = new byte[16 + ct.Length];
        Buffer.BlockCopy(iv, 0, outp, 0, 16);
        Buffer.BlockCopy(ct, 0, outp, 16, ct.Length);
        return outp;
    }

    public static void EncryptToFile(string path, string plain) => File.WriteAllBytes(path, Encrypt(plain));

    // ---------------- 客户端字符串混淆 / GCM / EDK ----------------

    /// <summary>客户端字符串混淆解码：<c>xD(hex, e=90)</c> —— 十六进制字面量按字节异或 <paramref name="xor"/>。
    /// 实测字面量：<c>"742f693f"</c>→.u3e、<c>"742f693f3439"</c>→.u3enc、<c>"742f6939"</c>→.u3c、
    /// <c>"33343e3f223f2974392929742f693f3439"</c>→indexes.css.u3enc。</summary>
    public static string XorHexString(string hex, int xor = StrXor)
    {
        if (string.IsNullOrEmpty(hex)) return "";
        var sb = new StringBuilder(hex.Length / 2);
        for (int i = 0; i + 1 < hex.Length; i += 2)
            sb.Append((char)(Convert.ToInt32(hex.Substring(i, 2), 16) ^ xor));
        return sb.ToString();
    }

    /// <summary>客户端 <c>Ws.obfuscate</c>：与 <see cref="GcmXorKey"/> 循环异或 → base64 → 整串反转。</summary>
    public static string Obfuscate(string s)
    {
        var raw = Encoding.UTF8.GetBytes(s ?? "");
        var kb = Encoding.UTF8.GetBytes(GcmXorKey);
        for (int i = 0; i < raw.Length; i++) raw[i] ^= kb[i % kb.Length];
        return new string(Convert.ToBase64String(raw).Reverse().ToArray());
    }

    /// <summary>客户端 <c>Ws.deobfuscate</c>：整串反转 → base64 解码 → 与 <see cref="GcmXorKey"/> 循环异或。</summary>
    public static string Deobfuscate(string s)
    {
        var rev = new string((s ?? "").Reverse().ToArray());
        var raw = Convert.FromBase64String(rev);
        var kb = Encoding.UTF8.GetBytes(GcmXorKey);
        var outp = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++) outp[i] = (byte)(raw[i] ^ kb[i % kb.Length]);
        return Encoding.UTF8.GetString(outp);
    }

    /// <summary>客户端 <c>Ws.decryptText</c>：输入 <c>base64(IV12 | 密文 | TAG16)</c>，密钥为 base64(16B)。</summary>
    public static string DecryptGcmText(string b64, string keyB64)
    {
        var data = Convert.FromBase64String(b64 ?? "");
        if (data.Length < 28) throw new InvalidDataException("GCM 数据过短");
        return Encoding.UTF8.GetString(AesGcmBytes(Convert.FromBase64String(keyB64), data[..12], data[12..^16], data[^16..], true));
    }

    /// <summary>客户端 <c>Ws.decryptFile</c>：文件 = <c>IV12 | 密文 | TAG16</c>，密钥为 32 位 hex（16B）。</summary>
    public static byte[] DecryptGcmFile(byte[] buf, string keyHex)
    {
        if (buf.Length < 28) throw new InvalidDataException("GCM 文件过短");
        var key = Convert.FromHexString((keyHex ?? "").Trim());
        if (key.Length != 16) throw new InvalidDataException("GCM 密钥应为 16 字节（32 位 hex）");
        return AesGcmBytes(key, buf[..12], buf[12..^16], buf[^16..], true);
    }

    /// <summary>客户端 <c>Ws.decryptEdk(extTime, config)</c>：<c>config</c> 去混淆得到内容密钥（base64），
    /// 校验 <c>extTime</c> 的 md5 前缀后解出明文（即内容文件的 32 位 hex 密钥）。</summary>
    public static string DecryptEdk(string extTime, string config)
    {
        string keyB64 = Deobfuscate(config);
        string md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(keyB64))).ToLowerInvariant();
        if (string.IsNullOrEmpty(extTime) || !extTime.StartsWith(md5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EDK 校验失败（config 与 extTime 不匹配）");
        return DecryptGcmText(extTime[md5.Length..], keyB64);
    }

    /// <summary>
    /// 内容资源密钥推导（对应客户端 <c>dZ()</c>）：从 <paramref name="filePath"/> 所在目录逐级向上找
    /// <c>indexes.css.u3enc</c>，用已解出的 ② 密钥解开它得到 <c>{extTime, config}</c>，
    /// 再经 <see cref="DecryptEdk"/> 得到内容密钥（32 位 hex）。找不到 / 解不开返回 null。
    /// </summary>
    public static string? DeriveContentKey(string filePath)
    {
        try
        {
            var d = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".");
            for (; d != null; d = d.Parent)
            {
                string idx = Path.Combine(d.FullName, "indexes.css.u3enc");
                if (File.Exists(idx))
                {
                    using var doc = JsonDocument.Parse(DecryptFile(idx));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("extTime", out var et) && r.TryGetProperty("config", out var cf) &&
                        cf.ValueKind == JsonValueKind.String)
                        return DecryptEdk(et.GetString() ?? "", cf.GetString() ?? "");
                    return null;
                }
                // 到盘符根后 info.Parent 为 null，自动结束
            }
        }
        catch { }
        return null;
    }

    // ---------------- 静态候选密钥抽取 ----------------

    /// <summary>客户端 JS 里密钥的常见写法：<c>Buffer.from("&lt;base64&gt;", "base64")</c>，
    /// 也覆盖 <c>Buffer.from(r || "&lt;base64&gt;", "base64")</c> 这种默认参数形式（旧版正则漏了这一类）。</summary>
    static readonly Regex RxB64Literal = new(
        @"Buffer\.from\(\s*[^""'\r\n]{0,48}[""']([A-Za-z0-9+/]{16,64}={0,2})[""']\s*,\s*[""']base64[""']\s*\)",
        RegexOptions.Compiled);

    /// <summary>任意 16 字节 base64 段的宽松匹配（22 字符+"==" / 24 字符无填充）。</summary>
    static readonly Regex RxB64Any = new(
        @"[A-Za-z0-9+/]{22}==|[A-Za-z0-9+/]{24}",
        RegexOptions.Compiled);

    /// <summary>
    /// 从任意文本（已解密的客户端 JS / 二进制 latin1 视图 / 配置文件）抽取"可能是 16 字节 AES 密钥"的
    /// base64 候选。优先精确抽取 <c>Buffer.from(...,"base64")</c>，再补一遍宽松扫描。
    /// </summary>
    public static List<string> ExtractCandidateKeys(string text)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Try(string s)
        {
            if (s.Length < 22 || s.Length > 64 || !seen.Add(s)) return;
            if (!IsValidKey(s)) return;
            found.Add(s);
        }
        if (string.IsNullOrEmpty(text)) return found;
        foreach (Match m in RxB64Literal.Matches(text)) Try(m.Groups[1].Value);
        foreach (Match m in RxB64Any.Matches(text)) Try(m.Value);
        return found;
    }

    // ---------------- 文件级密钥发现 ----------------

    /// <summary>从客户端二进制中扫描 base64 候选密钥，用真实样本试解（保留旧签名，转调多文件版本）。</summary>
    public static List<string> DiscoverKeys(string clientExe, IEnumerable<string> sampleFiles)
        => DiscoverKeysInFiles(new[] { clientExe }, sampleFiles, out _);

    /// <summary>多文件静态扫描（不关心抽取总数的调用方用这个）。</summary>
    public static List<string> DiscoverKeysInFiles(IEnumerable<string> files, IEnumerable<string> sampleFiles)
        => DiscoverKeysInFiles(files, sampleFiles, out _);

    /// <summary>
    /// 多文件静态扫描：把每个文件当文本抽候选，再用样本试解。判定规则（抑制误报）：
    /// ① 至少一份样本解出"像内容"的明文；② 同一格式组的成功率 ≥ 80%（组内只有 1 份时要求该份命中）。
    /// <paramref name="candidates"/> 回报实际抽取到的候选总数（含已内置/已存在的，便于诊断抽取是否生效）。
    /// <paramref name="testBuiltin"/>=true 时不排除内置密钥 —— 用于自检"抽取+试解链路是否真的能命中已知密钥"。
    /// </summary>
    public static List<string> DiscoverKeysInFiles(IEnumerable<string> files, IEnumerable<string> sampleFiles,
                                                   out int candidates, bool testBuiltin = false)
    {
        var found = new List<string>();
        candidates = 0;
        var samples = new List<byte[]>();
        foreach (var f in sampleFiles.Where(File.Exists).Distinct().Take(200))
        {
            var b = TryReadAll(f);
            if (b != null) samples.Add(b);
        }
        if (samples.Count == 0) return found;

        var existing = new HashSet<string>(StringComparer.Ordinal);
        // 自检（testBuiltin）时不排除任何已知密钥：目的是验证"抽取+试解"整条链路能命中已知正确的密钥，
        // 若把 keys.json 里已有的那把也排除掉，就会出现"明明抽到了却只命中 1 把"的假象。
        if (!testBuiltin)
        {
            foreach (var k in BuiltinKeys) existing.Add(k);
            foreach (var k in LoadCustomKeys()) existing.Add(k);
        }

        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files.Where(File.Exists).Distinct().Take(600))
        {
            string text = ReadAsText(f);
            if (text.Length == 0) continue;
            foreach (var c in ExtractCandidateKeys(text)) all.Add(c);
            if (all.Count > 30000) break;   // 保险上限
        }
        candidates = all.Count;

        foreach (var c in all)
        {
            if (existing.Contains(c) || !IsValidKey(c)) continue;
            var key = Convert.FromBase64String(c);
            if (AcceptKey(key, samples)) found.Add(c);
        }
        return found;
    }

    /// <summary>样本验收：见 <see cref="DiscoverKeysInFiles"/> 的判定规则。</summary>
    static bool AcceptKey(byte[] key, List<byte[]> samples)
    {
        int v1ok = 0, v1n = 0, v2ok = 0, v2n = 0; bool content = false;
        foreach (var buf in samples)
        {
            int ver = FormatOf(buf);
            if (ver == 0) continue;
            if (ver == 2) v2n++; else v1n++;
            if (!TryDecryptSampleFormat(buf, key, out int hit)) continue;
            if (hit == 2) v2ok++; else v1ok++;
            if (!content && TryDecryptSampleStrict(buf, key)) content = true;
        }
        if (!content) return false;
        if (v1n > 0 && v1ok >= Math.Max(1, (int)Math.Ceiling(v1n * 0.8))) return true;
        if (v2n > 0 && v2ok >= Math.Max(1, (int)Math.Ceiling(v2n * 0.8))) return true;
        return false;
    }

    /// <summary>把文件读成文本：先按 UTF-8 严格解（失败再按 latin1，保证二进制也能扫到 base64 串）。</summary>
    public static string ReadAsText(string file, int maxBytes = 24 * 1024 * 1024)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length == 0) return "";
            int take = (int)Math.Min(fi.Length, maxBytes);
            byte[] buf = new byte[take];
            using (var fs = File.OpenRead(file))
            {
                int off = 0, n;
                while (off < take && (n = fs.Read(buf, off, take - off)) > 0) off += n;
                if (off < take) Array.Resize(ref buf, off);
            }
            try { return new UTF8Encoding(false, true).GetString(buf); }
            catch { return Encoding.Latin1.GetString(buf); }
        }
        catch { return ""; }
    }

    static byte[]? TryReadAll(string file)
    {
        try { return File.ReadAllBytes(file); } catch { return null; }
    }
}
