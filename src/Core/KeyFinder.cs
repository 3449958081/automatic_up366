using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TxwExtract.Core;

/// <summary>
/// 从客户端进程内存里提取 .u3enc 密钥（「密钥管理→从客户端自动查找」的强化版）。
/// 背景：V2 加密文件（带 12 字节魔数头，如考试模式单元）使用独立密钥，密钥不在任何本地
/// 文件里，只在客户端进程内存中。客户端是提权进程，因此必须在提权环境下运行（主程序自提权）。
/// </summary>
public static class KeyFinder
{
    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_PRIVATE = 0x20000;

    [StructLayout(LayoutKind.Sequential)]
    struct MBI
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI mbi, uint len);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

    static readonly uint[] ReadableProtect = { 0x02, 0x04, 0x20, 0x40 };  // READONLY, READWRITE, EXECUTE_READ, EXECUTE_READWRITE

    // 16 字节密钥的 base64 只有两种合法形态：22 字符 + "=="，或 24 字符无填充。
    // 旧正则里的 {23}= 对应 17 字节，永远过不了 IsValidKey，是死分支。
    static readonly Regex B64 = new("[A-Za-z0-9+/]{22}==|[A-Za-z0-9+/]{24}", RegexOptions.Compiled);

    public sealed record Result(int ProcessesScanned, int ProcessesReadable, long BytesScanned, int Candidates, List<string> FoundKeys, string Msg);

    /// <summary>客户端进程名（客户端为提权进程，读取其内存需要本程序也提权）。</summary>
    static readonly string[] ProcNames = { "up366", "天学网学生端", "txwstudent" };

    /// <summary>
    /// 收集解密样本：优先 V2（12 字节魔数头，如考试模式单元），其次 V1；题目数据排最前。
    /// V2 密钥可能按单元/书各自独立，所以样本要传多份、候选逐个对每份试。
    /// </summary>
    public static List<string> CollectSamples(string dataRoot, int cap = 120)
    {
        var v2 = new List<string>();
        var v1 = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot)) return new();
            foreach (var f in Directory.EnumerateFiles(dataRoot, "*.u3enc", SearchOption.AllDirectories))
            {
                if (v2.Count + v1.Count >= cap * 3) break;
                try
                {
                    using var fs = File.OpenRead(f);
                    var head = new byte[12];
                    bool isV2 = fs.Read(head, 0, 12) == 12 && head.AsSpan().SequenceEqual(CryptoService.MagicV2);
                    bool prefer = Path.GetFileName(f).StartsWith("questionData", StringComparison.OrdinalIgnoreCase);
                    (isV2 ? v2 : v1).Insert(prefer ? 0 : (isV2 ? v2.Count : v1.Count), f);
                }
                catch { }
            }
        }
        catch { }
        return v2.Concat(v1).Take(cap).ToList();
    }

    /// <summary>扫描所有 up366 进程内存，找出能解开任一 sampleFile 的密钥并自动并入 keys.json。
    /// V2 密钥可能按单元/书各自独立，所以样本传多份、候选逐个对每份试。</summary>
    public static Result HuntAndSave(IReadOnlyList<string> sampleFiles, Action<string>? log = null)
    {
        int procs = 0, readable = 0;
        long bytes = 0;
        var cands = new HashSet<string>();
        var found = new List<string>();

        var samples = sampleFiles.Where(File.Exists).Select(File.ReadAllBytes).ToList();
        if (samples.Count == 0) return new Result(0, 0, 0, 0, found, "样本文件不存在");

        var existing = new HashSet<string>(CryptoService.BuiltinKeys, StringComparer.Ordinal);
        foreach (var k in CryptoService.LoadCustomKeys()) existing.Add(k);

        foreach (var name in ProcNames)
            foreach (var p in Process.GetProcessesByName(name))
            {
                procs++;
                var before = bytes;
                try { ScanProcess(p.Id, cands, ref bytes); } catch { }
                if (bytes > before) readable++;
            }

        log?.Invoke($"已扫描 {procs} 个客户端进程（可读 {readable} 个）/ {bytes / 1024 / 1024} MB，候选 {cands.Count} 个");

        foreach (var c in cands)
        {
            if (existing.Contains(c)) continue;
            byte[] key;
            try { key = Convert.FromBase64String(c); } catch { continue; }
            if (key.Length != 16) continue;
            foreach (var s in samples)
            {
                if (CryptoService.TryDecryptSampleFormat(s, key, out int ver))
                {
                    found.Add(c);
                    log?.Invoke($"★ 命中新密钥 {c[..6]}…（可解 {(ver == 2 ? "V2" : "V1")} 格式）");
                    break;
                }
            }
        }

        CryptoService.MergeCustomKeys(found);

        string msg = $"扫描 {procs} 个进程（可读 {readable}）/ {bytes / 1024 / 1024} MB，候选 {cands.Count} 个，命中 {found.Count} 把新密钥";
        return new Result(procs, readable, bytes, cands.Count, found, msg);
    }

    static void ScanProcess(int pid, HashSet<string> cands, ref long bytes)
    {
        IntPtr h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return;
        try
        {
            var mbi = new MBI();
            uint mbiSize = (uint)Marshal.SizeOf<MBI>();
            long addr = 0;
            const long MAX_ADDR = 0x7FFFFFFEFFFF;
            const long CHUNK = 8L * 1024 * 1024;
            const long REGION_CAP = 512L * 1024 * 1024;
            var buf = new byte[CHUNK + 64];

            while (addr < MAX_ADDR)
            {
                if (VirtualQueryEx(h, (IntPtr)addr, out mbi, mbiSize) == IntPtr.Zero) break;
                long size = mbi.RegionSize.ToInt64();
                if (size <= 0) break;
                long next = mbi.BaseAddress.ToInt64() + size;
                if (next <= addr) break;

                if (mbi.State == MEM_COMMIT && mbi.Type == MEM_PRIVATE && IsReadable(mbi.Protect))
                {
                    long take = Math.Min(size, REGION_CAP);
                    long off = 0;
                    while (off < take)
                    {
                        int n = (int)Math.Min(CHUNK + 64, take - off);
                        if (!ReadProcessMemory(h, (IntPtr)(mbi.BaseAddress.ToInt64() + off), buf, (IntPtr)n, out IntPtr read)) break;
                        int got = (int)read.ToInt64();
                        if (got <= 0) break;
                        bytes += got;
                        Collect(buf, got, cands);
                        off += CHUNK;
                    }
                }
                addr = next;
            }
        }
        finally { CloseHandle(h); }
    }

    static bool IsReadable(uint protect)
    {
        foreach (uint p in ReadableProtect) if (protect == p) return true;
        return false;
    }

    static void Collect(byte[] buf, int len, HashSet<string> cands)
    {
        string s = Encoding.Latin1.GetString(buf, 0, len);
        foreach (Match m in B64.Matches(s)) cands.Add(m.Value);
    }
}
