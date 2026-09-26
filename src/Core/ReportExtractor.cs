using System.Text;
using System.Text.Json;

namespace TxwExtract.Core;

/// <summary>
/// 作文报告提取器（手动触发）。
///
/// 背景：客户端里的「试题报告」是一个远端 Web App（<c>https://report.up366.cn/</c>），
/// 当账号的「写作」权益在服务端是试用型（<c>authType=3</c>）时，前端会判定为"未授权"，
/// 给结果区/评语区套上 <c>.blur-mask</c>（马赛克）并加 <c>.no-user-select</c>（禁点击）——
/// **内容本身是完整的，只是被纯 CSS 挡住**（详见 <c>_research/_report_probe/FINDINGS.md</c>）。
///
/// 本类只做两件**运行时**操作（不写客户端任何文件、不改客户端代码、不碰服务端状态）：
///   ① 摘掉遮罩：移除 <c>.blur-mask</c> / <c>.no-user-select</c>，隐藏解锁提示条；
///   ② 临时揭示被 <c>display:none</c> 折叠的区块（如「范文示例」），取到文本后**同一 JS tick 内还原**，
///      因此既不会上屏闪烁，也不改变页面持久状态。
///
/// 前置条件：客户端必须由本工具以「调试模式」拉起（<c>--remote-debugging-port-u3=9222</c>），
/// 且用户已在客户端里打开报告页。目标在 CDP 里的类型是 <c>webview</c>（不是 <c>page</c>）。
/// </summary>
public static class ReportExtractor
{
    public sealed record Result(
        bool Ok, string Msg,
        string Text, string HtmlPath, string TxtPath,
        string Url, int Chars, int BlurRemoved, int Revealed, int TipsHidden,
        List<string> Candidates);

    private const string ReportHostPrefix = "https://report.up366.cn/";

    /// <summary>
    /// 在客户端已打开的报告页里提取作文报告，并把 <c>.txt</c>（正文）与 <c>.html</c>（自包含报告）
    /// 写入 <paramref name="exportDir"/>。任何一步失败都返回 <c>Ok=false</c> + 可操作的中文原因，绝不抛异常给上层。
    /// </summary>
    public static async Task<Result> ExtractAsync(string exportDir, CancellationToken ct = default)
    {
        List<string> candidates = new();

        // ---- ① 枚举调试端口目标（必须 pagesOnly:false —— 报告页类型是 webview，不是 page）----
        List<CdpClient.DevTarget> targets;
        try
        {
            targets = await CdpClient.ListTargetsAsync(CdpClient.DefaultPort, pagesOnly: false);
        }
        catch (Exception e)
        {
            return Fail("枚举客户端调试端口失败：" + e.Message, candidates);
        }

        if (targets.Count == 0)
        {
            // 兜底：区分"客户端没开"与"开着但没带调试开关"，否则用户会一直点却没线索
            string why = ClientService.AnyRunning()
                ? "客户端正在运行，但不是「调试模式」启动的，未开放调试端口 9222 —— 请先完全退出客户端（含托盘），再从本页点【启动客户端（调试模式）】"
                : "未检测到天学网客户端 —— 请先点【启动客户端（调试模式）】把客户端拉起来";
            return Fail("没连上客户端报告页：" + why, candidates);
        }

        // ---- ② 挑报告页目标（排除 local://res/dialog.html?url=<编码地址> 那层壳页面）----
        var reportTargets = targets
            .Where(t => t.WsUrl.Length > 0 && t.Url.StartsWith(ReportHostPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (reportTargets.Count == 0)
        {
            candidates = targets.Where(t => t.Url.Length > 0)
                .Select(t => "[" + t.Type + "] " + Cut(t.Url, 96)).Take(8).ToList();
            return Fail("客户端里没找到已打开的「试题报告」页 —— 请先在客户端里打开作文报告，再点本按钮"
                        + (candidates.Count > 0 ? "。当前页面：" + string.Join("；", candidates) : ""), candidates);
        }

        // 多个报告页时优先「作文报告」（writing-report），其余按枚举顺序取第一个
        var pick = reportTargets.FirstOrDefault(t => t.Url.Contains("writing-report", StringComparison.OrdinalIgnoreCase))
                   ?? reportTargets[0];
        if (reportTargets.Count > 1)
            candidates = reportTargets.Select(t => Cut(t.Url, 96)).ToList();

        // ---- ③ 附着 → 摘遮罩 → 采集 ----
        using var cdp = new CdpClient();
        try
        {
            await cdp.ConnectAsync(pick.WsUrl);
        }
        catch (Exception e)
        {
            return Fail("连接报告页调试通道失败：" + e.Message, candidates);
        }

        string raw;
        try
        {
            raw = await cdp.EvaluateStringAsync(JsCollect, 25000);
        }
        catch (Exception e)
        {
            return Fail("读取报告页失败：" + e.Message, candidates);
        }

        if (string.IsNullOrWhiteSpace(raw))
            return Fail("报告页已连上，但没有返回内容（页面可能正在加载，请稍后重试）", candidates);

        string text, html, title;
        int blur = 0, revealed = 0, tips = 0;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            text = GetStr(r, "text");
            html = GetStr(r, "html");
            title = GetStr(r, "title");
            if (r.TryGetProperty("log", out var lg) && lg.ValueKind == JsonValueKind.Object)
            {
                blur = GetInt(lg, "blur");
                revealed = GetInt(lg, "revealed");
                tips = GetInt(lg, "tips");
            }
        }
        catch (Exception e)
        {
            return Fail("报告数据解析失败：" + e.Message, candidates);
        }

        text = (text ?? "").Replace("\r\n", "\n");
        if (text.Trim().Length == 0)
            return Fail("报告页已连上，但没读到正文 —— 页面可能还没渲染完（等报告出来后再点一次）", candidates);

        // 防御：异常大的页面不落盘，避免内存/磁盘写爆
        if (html.Length > 24_000_000)
            return Fail("报告 HTML 过大（" + html.Length + " 字符），已放弃写入", candidates);

        // ---- ④ 落盘（.txt 正文 + .html 自包含报告）----
        string txtPath = "", htmlPath = "";
        try
        {
            Directory.CreateDirectory(exportDir);
            string baseName = "作文报告_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            txtPath = UniquePath(Path.Combine(exportDir, baseName + ".txt"));
            htmlPath = UniquePath(Path.Combine(exportDir, baseName + ".html"));
            var utf8Bom = new UTF8Encoding(true);   // 带 BOM：中文在记事本/编辑器里不乱码
            File.WriteAllText(txtPath, text, utf8Bom);
            File.WriteAllText(htmlPath, BuildHtml(raw, html), utf8Bom);
        }
        catch (Exception e)
        {
            LogFiles.Session("report| 落盘失败: " + e.Message);
            return Fail("报告已提取成功，但写入文件失败：" + e.Message
                        + "（可在导出目录设置里换一个可写目录）", candidates) with { Text = text };
        }

        LogFiles.Session("report| 提取成功 " + text.Length + " 字 / 去遮罩 " + blur + " / 揭示 " + revealed
                         + " / html " + html.Length + " → " + txtPath);

        return new Result(true,
            "已提取 " + text.Length + " 字"
            + (revealed > 0 ? "（含 " + revealed + " 个被折叠的区块）" : "")
            + "，正文与报告已保存到导出目录",
            text, htmlPath, txtPath, pick.Url, text.Length, blur, revealed, tips, candidates);
    }

    private static Result Fail(string msg, List<string> cand)
        => new(false, msg, "", "", "", "", 0, 0, 0, 0, cand);

    /// <summary>在报告页上下文执行的采集脚本。摘遮罩 + 临时揭示隐藏区块 + 取文本/HTML/样式。</summary>
    private const string JsCollect = """
(() => {
  const out = { ok: true, log: {} };
  const log = out.log;
  const qa = s => document.querySelectorAll(s);

  // ① 摘遮罩：内容本来就在 DOM 里，只是被纯 CSS 模糊/禁选（详见 FINDINGS.md）
  log.blur = 0;
  qa('.blur-mask').forEach(e => { e.classList.remove('blur-mask'); log.blur++; });
  log.noUserSelect = 0;
  qa('.no-user-select').forEach(e => { e.classList.remove('no-user-select'); log.noUserSelect++; });
  // 解锁提示条（"解锁智能批改…"）不属于报告正文，直接收起
  log.tips = 0;
  qa('.auth-tips-container, .auth-control-tips-dialog-container').forEach(e => { e.style.display = 'none'; log.tips++; });

  const root = document.querySelector('.writing-report-single-new-v2') || document.body;

  // ② 临时揭示"有正文却被 display:none 折叠"的区块（如「范文示例」.essay-section）。
  //    同一 JS tick 内读完即还原 → 浏览器还没绘制，用户看不到闪烁，页面状态也不被持久改变。
  //    但要排除"互斥的另一套占位界面"（.empty-default-area 之类）——那是"没数据时显示什么"的
  //    备选分支，不是被折叠的内容，揭示了只会往正文里掺"您尚未作答…"这类噪声。
  const isPlaceholder = el => {
    for (let n = el; n && n !== root.parentElement; n = n.parentElement) {
      const c = (typeof n.className === 'string' ? n.className : '');
      if (!c) continue;
      if (/(^|[\s-])empty/i.test(c)) return true;
      if (/auth-tips|auth-control-tips/.test(c)) return true;
    }
    return false;
  };
  const saved = [];
  log.revealed = 0; log.skipped = 0;
  try {
    root.querySelectorAll('*').forEach(el => {
      if (window.getComputedStyle(el).display !== 'none') return;
      if ((el.textContent || '').trim().length < 8) return;   // 忽略纯图标/空占位节点
      if (isPlaceholder(el)) { log.skipped++; return; }
      saved.push([el, el.style.cssText]);
      el.style.setProperty('display', 'block', 'important');
    });
    log.revealed = saved.length;
    out.text = root.innerText || '';
  } catch (e) {
    out.err = String(e && e.message ? e.message : e);
  } finally {
    for (const p of saved) { try { p[0].style.cssText = p[1]; } catch (_) {} }
  }

  out.html = root.outerHTML || '';
  out.title = document.title || '';
  out.url = location.href || '';
  out.bodyClass = document.body.className || '';
  out.htmlClass = document.documentElement.className || '';
  out.links = Array.from(qa('link[rel="stylesheet"]')).map(l => l.href).filter(Boolean);
  out.styles = Array.from(qa('style')).map(s => s.textContent || '');
  return JSON.stringify(out);
})()
""";

    /// <summary>拼出自包含报告 HTML：把报告页的原样式（外链 + 内联）带上，包一层可读容器。</summary>
    private static string BuildHtml(string rawJson, string inner)
    {
        var sb = new StringBuilder(inner.Length + 8192);
        string htmlClass = "", bodyClass = "", title = "";
        var links = new List<string>();
        var styles = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var r = doc.RootElement;
            htmlClass = GetStr(r, "htmlClass");
            bodyClass = GetStr(r, "bodyClass");
            title = GetStr(r, "title");
            if (r.TryGetProperty("links", out var la) && la.ValueKind == JsonValueKind.Array)
                foreach (var x in la.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) links.Add(x.GetString() ?? "");
            if (r.TryGetProperty("styles", out var sa) && sa.ValueKind == JsonValueKind.Array)
                foreach (var x in sa.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) styles.Add(x.GetString() ?? "");
        }
        catch { }

        sb.Append("<!doctype html>\n<html class=\"").Append(Esc(htmlClass)).Append("\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
        // 报告样式表都在报告站上，用 <base> 让相对路径/字体仍能解析
        sb.Append("<base href=\"").Append(ReportHostPrefix).Append("\">\n");
        sb.Append("<title>").Append(Esc(title.Length > 0 ? title : "作文报告")).Append("</title>\n");
        foreach (var l in links) sb.Append("<link rel=\"stylesheet\" href=\"").Append(Esc(l)).Append("\">\n");
        foreach (var s in styles)
            sb.Append("<style>").Append(s.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase)).Append("</style>\n");
        sb.Append("<style>html,body{margin:0;padding:0;background:#f5f6f8}body{padding:16px}</style>\n");
        sb.Append("</head>\n<body class=\"").Append(Esc(bodyClass)).Append("\">\n");
        sb.Append(inner);
        sb.Append("\n</body>\n</html>\n");
        return sb.ToString();
    }

    // ---------- 小工具 ----------

    private static string GetStr(JsonElement o, string k)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int GetInt(JsonElement o, string k)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    private static string Esc(string s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>同名文件已存在时追加 (1)(2)…，不静默覆盖用户既有导出。</summary>
    private static string UniquePath(string path)
    {
        try
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; i < 1000; i++)
            {
                string cand = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(cand)) return cand;
            }
        }
        catch { }
        return path;
    }
}
