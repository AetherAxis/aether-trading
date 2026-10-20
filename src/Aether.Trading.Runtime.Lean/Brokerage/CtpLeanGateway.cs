using Ctp.Net;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// DI 世界与 Lean Composer 世界之间的静态桥。<para/>
/// Lean 用 MEF/反射按 typeName 实例化算法宿主、用工厂创建 brokerage—— brokerage 工厂由 Composer
/// 用<strong>无参构造</strong>实例化，拿不到 DI 容器里的 <c>ICtpSession</c>（Singleton）。
/// 故 <c>LeanLiveTradingService</c> 在启动引擎前把 session + 行情连接参数 + 合约列表 + 日志口 + 停止标志
/// 注入本静态网关，工厂/算法从网关取依赖。回测 handler 读 Config，无需 DI（本网关不参与回测）。
/// </summary>
public static class CtpLeanGateway
{
    /// <summary>CTP 会话（交易+行情一体），与 CtpView 共享同一 Singleton。引擎启动前由 service 注入。</summary>
    public static ICtpSession? Session { get; set; }

    /// <summary>行情前置连接参数（CTP 行情独立连接）。data queue handler 首次订阅时用。</summary>
    public static CtpMarketDataConnect? MarketDataConnect { get; set; }

    /// <summary>订阅合约代码列表（如 ag2610），无需交易所后缀。</summary>
    public static IReadOnlyList<string> Instruments { get; set; } = Array.Empty<string>();

    /// <summary>日志/信号接收口（service 转 UI）。为 null 时桥接类静默。</summary>
    public static Action<string>? LogSink { get; set; }

    /// <summary>P2 实盘真实下单选项（开关 + 手数保险丝）。引擎启动前由 service 注入，brokerage PlaceOrder 读取。</summary>
    public static CtpLiveTradingOptions TradingOptions { get; set; } = CtpLiveTradingOptions.Default;

    /// <summary>停止标志——算法 OnData 检测到后调 Quit() 退出引擎。</summary>
    public static volatile bool StopRequested;

    /// <summary>向 service/UI 推一条日志。</summary>
    public static void Log(string message)
    {
        try { LogSink?.Invoke(message); }
        catch { /* 日志不应影响引擎 */ }
    }

    /// <summary>引擎停止后清理引用（避免持有旧 session 影响下次启动）。</summary>
    public static void Reset()
    {
        Session = null;
        MarketDataConnect = null;
        Instruments = Array.Empty<string>();
        LogSink = null;
        TradingOptions = CtpLiveTradingOptions.Default;
        StopRequested = false;
    }
}
