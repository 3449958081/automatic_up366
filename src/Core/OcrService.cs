using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TxwExtract.Core;

public sealed record OcrLine(string Text, int X, int Y, int W, int H);

/// <summary>
/// OCR 服务：仅内置 Tesseract（v2.1.14 起彻底移除 WinRT，按用户要求）。
/// Process 调 tesseract.exe（随安装包内置，无需联网），--psm 3 -l chi_sim+eng tsv，
/// 输出坐标即原图像素。失败直接抛异常（上层日志可见），无任何静默回退。
///
/// TSV 解析要点（实测踩坑）：
///   - Tesseract 5.x 的 TESSDATA_PREFIX 必须指向【直接包含 .traineddata 的 tessdata 目录本身】；
///   - `tsv` 输出依赖 tessdata/configs/tsv 配置，打包必须带上 tessdata/configs/，否则
///     报 "read_params_file: Can't open tsv" 且静默无输出；
///   - level=4(line) 行 text 列为空，文本在 level=5(word) 行；按 (block,par,line) 分组聚合，
///     word 间以间距启发式决定是否加空格（英文单词间加、中文连续字不加）。
/// </summary>
public static class OcrService
{
    private static string? _tessExe;
    private static bool _tessProbed;
    private static readonly object _tessLock = new();

    /// <summary>tesseract.exe 预期位置（随安装包内置，位于程序目录下 tesseract/）。</summary>
    public static string TessExePath => Path.Combine(AppContext.BaseDirectory, "tesseract", "tesseract.exe");

    public static bool TesseractAvailable
    {
        get { ProbeTess(); return _tessExe != null; }
    }

    /// <summary>兼容旧调用方（曾返回 WinRT 可用性）；现仅表示内置 Tesseract 是否可用。</summary>
    public static bool Available => TesseractAvailable;

    private static void ProbeTess()
    {
        lock (_tessLock)
        {
            if (_tessProbed) return;
            _tessProbed = true;
            _tessExe = File.Exists(TessExePath) ? TessExePath : null;
        }
    }

    public static async Task<List<OcrLine>> RecognizeAsync(Bitmap bmp)
    {
        if (bmp == null) return new List<OcrLine>();
        ProbeTess();
        if (_tessExe == null)
            throw new InvalidOperationException("内置 Tesseract 引擎缺失：" + TessExePath + "（安装包不完整，请重新安装）");
        return await Task.Run(() => TessRecognize(bmp, _tessExe, Path.GetDirectoryName(_tessExe)!));
    }

    private sealed record Word(string Text, int Left, int Top, int Right, int Bottom, int Block, int Par, int Line);

    private static List<OcrLine> TessRecognize(Bitmap bmp, string tessExe, string tessDir)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "txw_ocr_" + Guid.NewGuid().ToString("N") + ".png");
        string outBase = Path.ChangeExtension(tmp, null);   // 去掉 .png，tesseract 写 outBase.tsv
        try
        {
            bmp.Save(tmp, ImageFormat.Png);
            var psi = new ProcessStartInfo
            {
                FileName = tessExe,
                Arguments = $"\"{tmp}\" \"{outBase}\" --psm 3 -l chi_sim+eng tsv",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            // 关键：Tesseract 5.x 的 TESSDATA_PREFIX 指向"直接含 .traineddata 的 tessdata 目录本身"
            psi.EnvironmentVariables["TESSDATA_PREFIX"] = Path.Combine(tessDir, "tessdata");

            using var proc = Process.Start(psi)!;
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException("Tesseract 执行失败: " + err.Trim());

            string tsv = outBase + ".tsv";
            if (!File.Exists(tsv))
                throw new InvalidOperationException("Tesseract 未生成结果: " + err.Trim());

            // 解析 TSV：仅 word 级（level=5），按 (block,par,line) 聚合为整行 + 包围盒
            var words = new List<Word>();
            foreach (var raw in File.ReadAllLines(tsv))
            {
                var cols = raw.Split('\t');
                if (cols.Length < 12) continue;
                if (cols[0] != "5") continue;               // word 级，text 非空
                string text = cols[11];
                if (text.Length == 0) continue;
                int l = int.Parse(cols[6], CultureInfo.InvariantCulture);
                int t = int.Parse(cols[7], CultureInfo.InvariantCulture);
                int w = int.Parse(cols[8], CultureInfo.InvariantCulture);
                int h = int.Parse(cols[9], CultureInfo.InvariantCulture);
                words.Add(new Word(text, l, t, l + w, t + h,
                    int.Parse(cols[2], CultureInfo.InvariantCulture),
                    int.Parse(cols[3], CultureInfo.InvariantCulture),
                    int.Parse(cols[4], CultureInfo.InvariantCulture)));
            }

            var lines = new List<OcrLine>();
            foreach (var g in words.GroupBy(x => (x.Block, x.Par, x.Line)))
            {
                var ws = g.OrderBy(x => x.Left).ToList();
                var sb = new StringBuilder();
                Word? prev = null;
                foreach (var wd in ws)
                {
                    // 词间空格启发式：
                    //   相邻都是非中文(英文/数字)且空隙>2px → 空格（英文词边界，如 "What benefit"）
                    //   中英文交界 → 空格（如 "专项训练 : 听力" 的冒号两侧）
                    //   都含中文 → 无空格（消除 "专 项 训 练" 伪空格）
                    if (prev != null)
                    {
                        bool pCjk = ContainsCjk(prev.Text), cCjk = ContainsCjk(wd.Text);
                        bool need = !pCjk && !cCjk ? (wd.Left - prev.Right > 2) : pCjk != cCjk;
                        if (need) sb.Append(' ');
                    }
                    sb.Append(wd.Text);
                    prev = wd;
                }
                string text = CollapseSpaces(sb.ToString());
                if (text.Length == 0) continue;
                int x = ws.Min(v => v.Left), y = ws.Min(v => v.Top);
                int right = ws.Max(v => v.Right), bottom = ws.Max(v => v.Bottom);
                lines.Add(new OcrLine(text, x, y, Math.Max(1, right - x), Math.Max(1, bottom - y)));
            }
            lines.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));

            try { File.Delete(tsv); } catch { }
            try { File.Delete(outBase + ".txt"); } catch { }
            return lines;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static readonly Regex _multiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>文本是否含 CJK 统一表意文字（用于词间空格启发式）。</summary>
    private static bool ContainsCjk(string s)
    {
        foreach (var c in s)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        return false;
    }

    private static string CollapseSpaces(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        return _multiSpace.Replace(s, " ").Trim();
    }
}
