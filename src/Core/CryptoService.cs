using System.Security.Cryptography;
using System.Text;

namespace TxwExtract.Core;

/// <summary>
/// 天学网 .u3enc 解密：AES-128-CBC，IV = 文件前 16 字节，密钥 = base64(16B)。
/// 与 Node 版行为一致：逐个密钥尝试，用"明文可打印性"判定是否解对。
/// </summary>
public static class CryptoService
{
    /// <summary>内置密钥（与 Node 版 BUILTIN_KEYS 一致）。用户新增密钥存 keys.json。</summary>
    public static readonly List<string> BuiltinKeys = new() { "QJBNiBmV55PDrewyne3GsA==" };

    private static List<string> _keys = new();
    private static int _lastKeyIdx = 0;

    public static IReadOnlyList<string> Keys => _keys;

    /// <summary>最近一次解密成功的密钥（前端"当前生效"标记用；null = 尚未成功解密过）。</summary>
    public static string? ActiveKey => _lastKeyIdx >= 0 && _lastKeyIdx < _keys.Count ? _keys[_lastKeyIdx] : null;

    /// <summary>从 keys.json 读取用户自定义密钥（格式 {"keys":[...]}，与 Node 版一致）。</summary>
    public static List<string> LoadCustomKeys()
    {
        try
        {
            if (File.Exists(AppPaths.KeysPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.KeysPath));
                if (doc.RootElement.TryGetProperty("keys", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    return arr.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String)
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
            File.WriteAllText(AppPaths.KeysPath, System.Text.Json.JsonSerializer.Serialize(obj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void LoadKeys(IEnumerable<string> builtin, IEnumerable<string> custom)
    {
        _keys = builtin.Concat(custom)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Where(k => { try { return Convert.FromBase64String(k).Length == 16; } catch { return false; } })
            .Distinct().ToList();
    }

    public static void Reload()
    {
        LoadKeys(BuiltinKeys, LoadCustomKeys());
    }

    /// <summary>启发式判断解密结果是否为合理明文（控制字符极少）。</summary>
    private static bool LooksPlain(byte[] b)
    {
        int n = Math.Min(b.Length, 200), bad = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c < 0x09 || (c > 0x0d && c < 0x20)) bad++;
        }
        return bad < 3;
    }

    /// <summary>解密字节（逐个密钥尝试，命中后记住该密钥以加速后续解密）。</summary>
    public static string Decrypt(byte[] buf)
    {
        if (buf.Length < 32) throw new InvalidDataException("文件过小");
        var iv = buf[..16];
        var ct = buf[16..];

        // 上次成功的密钥优先
        var order = Enumerable.Range(0, _keys.Count).OrderBy(i => i == _lastKeyIdx ? -1 : 0);
        Exception? firstErr = null;

        foreach (int i in order)
        {
            try
            {
                using var aes = Aes.Create();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = Convert.FromBase64String(_keys[i]);
                aes.IV = iv;
                using var dec = aes.CreateDecryptor();
                var plain = dec.TransformFinalBlock(ct, 0, ct.Length);
                if (LooksPlain(plain)) { _lastKeyIdx = i; return Encoding.UTF8.GetString(plain); }
            }
            catch (Exception e) { firstErr ??= e; }
        }
        throw new CryptographicException(
            $"所有已知密钥（{_keys.Count} 个）均解密失败——天学网可能已更换密钥。请用「密钥管理 → 从客户端自动查找」获取新密钥。（{firstErr?.Message}）");
    }

    public static string DecryptFile(string path) => Decrypt(File.ReadAllBytes(path));

    /// <summary>从客户端二进制中扫描 base64 候选密钥，用真实样本试解（对应 Node 版 findKeysInClient）。</summary>
    public static List<string> DiscoverKeys(string clientExe, IEnumerable<string> sampleFiles)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(clientExe) || !File.Exists(clientExe)) return found;

        byte[] bin;
        try { bin = File.ReadAllBytes(clientExe); } catch { return found; }
        var text = Encoding.ASCII.GetString(bin);

        var samples = sampleFiles.Where(File.Exists).Take(6).ToList();
        var tried = new HashSet<string>();

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, "[A-Za-z0-9+/]{24}="))
        {
            string cand = m.Value;
            if (!tried.Add(cand)) continue;
            byte[] key;
            try { key = Convert.FromBase64String(cand); } catch { continue; }
            if (key.Length != 16) continue;

            int ok = 0, total = 0;
            foreach (var s in samples)
            {
                total++;
                if (TryKey(File.ReadAllBytes(s), key)) ok++;
            }
            if (total > 0 && (double)ok / total >= 0.8) found.Add(cand);
        }
        return found;
    }

    private static bool TryKey(byte[] buf, byte[] key)
    {
        try
        {
            if (buf.Length < 32) return false;
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            aes.Key = key; aes.IV = buf[..16];
            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(buf, 16, buf.Length - 16);
            return LooksPlain(plain);
        }
        catch { return false; }
    }
}
