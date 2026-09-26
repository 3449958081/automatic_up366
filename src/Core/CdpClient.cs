using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TxwExtract.Core;

/// <summary>
/// 轻量 Chrome DevTools Protocol（CDP）客户端：仅实现本工具需要的
/// 「枚举页面目标 + Runtime.evaluate 页面内 JS 求值」。
/// 对接天学网客户端（魔改 Electron）暴露的调试端口 —— 启动参数
/// --remote-debugging-port-u3=9222（客户端源码级改名开关，见 _research/DOM可行性研究结论.md）。
/// </summary>
public sealed class CdpClient : IDisposable
{
    public const int DefaultPort = 9222;

    private ClientWebSocket? _ws;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private Task? _pump;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _nextId;

    public bool Connected => _ws?.State == WebSocketState.Open;

    public sealed record DevTarget(string Id, string Type, string Title, string Url, string WsUrl);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>枚举调试端口下的目标。pagesOnly=false 时含 Node inspector（type="node"，主进程 9229）等全部目标。</summary>
    public static async Task<List<DevTarget>> ListTargetsAsync(int port, bool pagesOnly = true)
    {
        var list = new List<DevTarget>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var arr = await Http.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{port}/json/list", cts.Token);
            foreach (var t in arr.EnumerateArray())
            {
                string type = t.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                if (pagesOnly && type != "page") continue;
                string url = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new DevTarget(
                    t.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                    type,
                    t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                    url,
                    t.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() ?? "" : ""));
            }
        }
        catch { }
        return list;
    }

    /// <summary>枚举调试端口下的可附页面目标（排除 devtools 自身与后台页）。</summary>
    public static Task<List<DevTarget>> ListPagesAsync(int port) => ListTargetsAsync(port, pagesOnly: true);

    /// <summary>连接到某页面目标的 WebSocket（webSocketDebuggerUrl）。</summary>
    public async Task ConnectAsync(string wsUrl)
    {
        DisposeSocket();
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
        _ws = ws;
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>通用 CDP 调用：发送 method/params 并等待按 id 匹配的响应，返回其 result 对象。</summary>
    public async Task<JsonElement> CallAsync(string method, string? paramsJson, int timeoutMs = 10000)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) throw new InvalidOperationException("CDP 未连接");
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = "{\"id\":" + id + ",\"method\":\"" + method + "\"" +
                  (string.IsNullOrEmpty(paramsJson) ? "" : ",\"params\":" + paramsJson) + "}";
        var bytes = Encoding.UTF8.GetBytes(msg);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? new CancellationToken());
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await _sendLock.WaitAsync(timeoutCts.Token);
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, timeoutCts.Token); }
            finally { _sendLock.Release(); }

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, timeoutCts.Token));
            if (done != tcs.Task) throw new TimeoutException("CDP 调用超时（" + method + " " + timeoutMs + "ms）");
            var root = await tcs.Task;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
            {
                string text = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : err.ToString();
                throw new InvalidOperationException(method + " 失败: " + Trunc(text, 200));
            }
            return root.TryGetProperty("result", out var r) ? r : root;
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            timeoutCts.Cancel();   // 释放 Task.Delay 定时器
        }
    }

    /// <summary>在页面上下文求值 JS：returnByValue + awaitPromise + userGesture。
    /// 可选 contextId：在指定执行上下文（如某 iframe 的隔离世界）中求值。
    /// 返回 Runtime.evaluate 的 result.value（页面脚本自行 JSON.stringify 时为字符串）。</summary>
    public async Task<JsonElement> EvaluateAsync(string expression, int timeoutMs = 15000, long? contextId = null)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) throw new InvalidOperationException("CDP 未连接");
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = "{\"id\":" + id + ",\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":"
                  + JsonSerializer.Serialize(expression)
                  + ",\"returnByValue\":true,\"awaitPromise\":true,\"userGesture\":true"
                  + (contextId.HasValue && contextId.Value > 0 ? ",\"contextId\":" + contextId.Value : "") + "}}";
        var bytes = Encoding.UTF8.GetBytes(msg);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? new CancellationToken());
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await _sendLock.WaitAsync(timeoutCts.Token);
            try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, timeoutCts.Token); }
            finally { _sendLock.Release(); }

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, timeoutCts.Token));
            if (done != tcs.Task) throw new TimeoutException("CDP 求值超时（" + timeoutMs + "ms）");
            var root = await tcs.Task;   // = {id, result:{result:{type,value}, exceptionDetails?}}

            // v1.3.7 修复：Runtime.evaluate 响应为双层 result（外层命令结果容器，内层 RemoteObject）。
            // v1.3.4 重构消息泵时只剥了外层，导致探针 JSON 外一直包着一层壳 → 解析出的 questions 恒为空，
            // 且 exceptionDetails 在内层从未被检测到（脚本异常被静默吞）、PingAsync 类型判定恒 false（每轮重附加）。
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var rc))
            {
                if (rc.ValueKind == JsonValueKind.Object && rc.TryGetProperty("exceptionDetails", out var ex))
                {
                    string text = ex.TryGetProperty("exception", out var e2) &&
                                  e2.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ex.ToString();
                    throw new InvalidOperationException("页面脚本异常: " + Trunc(text, 300));
                }
                if (rc.ValueKind == JsonValueKind.Object && rc.TryGetProperty("result", out var rr))
                    return rr;
                return rc;
            }
            return root;
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            timeoutCts.Cancel();   // 释放 Task.Delay 定时器
        }
    }

    /// <summary>求值便捷封装：从 RemoteObject 取 value 反序列化为字符串（页面脚本返回 JSON.stringify 的结果）。
    /// v1.3.7：配合双层 result 修复 —— EvaluateAsync 返回的是 RemoteObject（{type,value}），需取其 value 字段。</summary>
    public async Task<string> EvaluateStringAsync(string expression, int timeoutMs = 15000, long? contextId = null, bool userGesture = false)
    {
        var r = await EvaluateAsync(expression, timeoutMs, contextId);
        if (r.ValueKind == JsonValueKind.Object)
        {
            if (r.TryGetProperty("value", out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
                if (v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined) return "";
                return v.GetRawText();
            }
            return "";
        }
        if (r.ValueKind == JsonValueKind.String) return r.GetString() ?? "";
        if (r.ValueKind == JsonValueKind.Null || r.ValueKind == JsonValueKind.Undefined) return "";
        return r.GetRawText();
    }

    public sealed record FrameInfo(string Id, string Url);

    /// <summary>Page.getFrameTree：递归收集页面内全部 frame（主 frame + 各级子 iframe，跨域含内）。</summary>
    public async Task<List<FrameInfo>> GetFramesAsync()
    {
        var res = await CallAsync("Page.getFrameTree", null, 8000);
        var frames = new List<FrameInfo>();
        // ⚠ CDP 返回形如 { "frameTree": { "frame": {...}, "childFrames": [...] } }——
        //   frame/childFrames 在 frameTree 之下。旧版直接从 result 取，恒为空 → 帧级探测被静默禁用。
        //   此处兼容两种形态（有 frameTree 用它，没有就当 result 本身即 frameTree）。
        var root = res.ValueKind == JsonValueKind.Object && res.TryGetProperty("frameTree", out var ft) ? ft : res;
        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.TryGetProperty("frame", out var f))
            {
                string id = f.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                string url = f.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (id.Length > 0 && !url.StartsWith("devtools://")) frames.Add(new FrameInfo(id, url));
            }
            if (node.TryGetProperty("childFrames", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var c in ch.EnumerateArray()) Walk(c);
        }
        Walk(root);
        return frames;
    }

    /// <summary>Page.createIsolatedWorld：为某 frame 创建隔离执行世界（跨域 iframe 也能在其上 evaluate DOM）。</summary>
    public async Task<long> CreateIsolatedWorldAsync(string frameId)
    {
        var res = await CallAsync("Page.createIsolatedWorld",
            "{\"frameId\":" + JsonSerializer.Serialize(frameId) + "}", 8000);
        return res.TryGetProperty("executionContextId", out var c) ? c.GetInt64() : -1;
    }

    /// <summary>轻量连通性探测：求值 "1"，任何异常视为连接失效。
    /// v1.3.7：适配双层 result —— 检查 RemoteObject.value 是否为 Number（v1.3.4 起此处恒 false 导致每轮重附加）。</summary>
    public async Task<bool> PingAsync()
    {
        try
        {
            var r = await EvaluateAsync("1", 4000);
            if (r.ValueKind == JsonValueKind.Number) return true;
            return r.ValueKind == JsonValueKind.Object &&
                   r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number;
        }
        catch { return false; }
    }

    /// <summary>接收泵：读 CDP 消息，按 id 派发给等待者（事件消息忽略）。</summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        var ws = _ws;
        if (ws == null) return;
        var buf = new byte[256 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("CDP 连接被关闭");
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);

                if (ms.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
                    var root = doc.RootElement.Clone();
                    if (root.TryGetProperty("id", out var idEl))
                    {
                        int id = idEl.GetInt32();
                        if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(root);
                    }
                    // 事件消息（method 属性）本引擎不需要，忽略
                }
                catch { /* 单条消息解析失败不影响泵 */ }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 连接异常：让下一次 Ping/Evaluate 感知并触发重连 */ }
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private void DisposeSocket()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _ws = null; _pump = null; _cts = null;
        foreach (var kv in _pending) kv.Value.TrySetCanceled();
        _pending.Clear();
    }

    public void Dispose() => DisposeSocket();
}
