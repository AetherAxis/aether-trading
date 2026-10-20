namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// P2 实盘真实下单选项（经 <see cref="CtpLeanGateway.TradingOptions"/> 静态注入——brokerage 工厂经 Composer 无参构造实例化，
/// 拿不到 DI 容器，与 Session/MarketDataConnect 同一注入通道）。
/// </summary>
public sealed record CtpLiveTradingOptions
{
    /// <summary>false=仅信号日志（P1 行为，brokerage 拦截下单）；true=经 ICtpSession 真实报单。</summary>
    public bool EnableRealOrders { get; init; }

    /// <summary>单笔开仓手数上限（保险丝）。平仓不受限——否则仓位平不干净。</summary>
    public int MaxOrderVolume { get; init; } = 1;

    public static CtpLiveTradingOptions Default { get; } = new();
}
