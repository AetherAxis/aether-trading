using Aether.Trading.Domain;

namespace Aether.Trading.Runtime.Domain;

/// <summary>
/// 历史 K 线数据源端口（Lean 中立）：取某标的最近 <paramref name="count"/> 根 bar，映射成纯领域
/// <see cref="MarketBar"/>。供不需要起 Lean 引擎的轻量策略分析（如信号探针）消费。
/// 实现位于 <c>Aether.Trading.Runtime.Lean</c>（桥接 Quotes 上下文的 <c>IAnalysisManager</c>）。
/// 与 <see cref="IBacktestRuntime"/> 平行——后者起整套引擎做回测，本端口只取原始 bar。
/// </summary>
public interface IHistoricalBarSource
{
    /// <summary>
    /// 取最近 <paramref name="count"/> 根 bar（cache-first，与回测同一行情数据链）。
    /// 成功 Error=null；失败 Bars=null 且 Error=原因。
    /// </summary>
    Task<(IReadOnlyList<MarketBar>? Bars, string? Error)> GetBarsAsync(
        string symbol,
        BarResolution resolution,
        int count,
        CancellationToken cancellationToken = default);
}
