using System.Text.Json;

namespace TxwExtract.Core;

/// <summary>
/// 中央路由表（单一事实源）：登记全部 /api/* 接口的 path、参数名清单（供审计/文档）与处理委托。
/// 由 <c>MainForm</c> 用 <c>_bridge</c>/<c>_cfg</c> 实例构建委托字典，<c>Dispatch</c> 只做查表分发；
/// 未登记的 path 一律拒绝（由调用方返回「未知接口」，不做任何隐式兜底）。
/// 无重依赖、无反射：纯字典 + 委托，行为与旧 switch 逐字节等价。
/// </summary>
public sealed class RouteRegistry
{
    /// <summary>单条路由：path + 参数名清单（审计/文档用，与前端白名单对照）+ 处理委托（返回 JSON 字符串）。</summary>
    public sealed record Route(string Path, string[] ParamNames, Func<JsonElement, string> Handle);

    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);

    /// <summary>
    /// 登记一条路由。重复登记视为编程错误直接抛出（避免静默覆盖导致路由行为漂移）。
    /// </summary>
    public void Add(string path, string[] paramNames, Func<JsonElement, string> handle)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("路由 path 不能为空", nameof(path));
        _routes.Add(path, new Route(path, paramNames, handle));
    }

    /// <summary>
    /// 查表分发：命中则执行处理委托并返回 true；未登记 path 返回 false（调用方负责拒绝）。
    /// 委托内部异常不在本层吞掉 —— 交由调用方统一记录并回错误响应（与旧行为一致）。
    /// </summary>
    public bool TryDispatch(string path, JsonElement args, out string response)
    {
        if (_routes.TryGetValue(path, out var r))
        {
            response = r.Handle(args);
            return true;
        }
        response = "";
        return false;
    }

    /// <summary>全部已登记路由（审计/文档用）。</summary>
    public IReadOnlyCollection<Route> Routes => _routes.Values;
}
