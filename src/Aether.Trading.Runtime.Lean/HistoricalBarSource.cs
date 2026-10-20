using Aether.Quotes.Domain;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// <see cref="IHistoricalBarSource"/> 实现：桥接 Quotes 上下文的 <see cref="IAnalysisManager"/>，
/// 取历史 K 线映射成纯领域 <see cref="MarketBar"/>。<b>不起 Lean 引擎</b>——只取原始 bar。
/// 与 <see cref="LeanBacktestRunner"/> 共用同一行情数据链（cache-first），但跳过 CSV/引擎/回测管线。
/// </summary>
public sealed class HistoricalBarSource : IHistoricalBarSource
{
    private readonly IAnalysisManager? _analysis;

    public HistoricalBarSource(IAnalysisManager? analysis)
    {
        _analysis = analysis;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<MarketBar>? Bars, string? Error)> GetBarsAsync(
        string symbol,
        BarResolution resolution,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (_analysis is null)
            return (null, "行情数据源未配置（IAnalysisManager 未注册）");
        if (count <= 0)
            return (null, "取数数量须大于 0");

        var freq = ResolutionToFreq(resolution);
        var (quotes, _, error) = await _analysis.GetKlineAsync(symbol, freq);
        if (quotes is null || quotes.Count == 0)
            return (null, error ?? $"无 {symbol} 的历史 K 线数据");

        // 取最近 count 根，保持时间升序
        var taken = quotes.Count <= count ? quotes : quotes.GetRange(quotes.Count - count, count);
        var bars = new List<MarketBar>(taken.Count);
        foreach (var q in taken)
            bars.Add(new MarketBar(q.Date, q.Open, q.High, q.Low, q.Close, q.Volume));
        return (bars, null);
    }

    /// <summary>BarResolution → IAnalysisManager freq 码（与 LeanBacktestRunner 一致：101=日K, 60=小时, 1=1分钟）。</summary>
    private static string ResolutionToFreq(BarResolution r) => r switch
    {
        BarResolution.Daily => "101",
        BarResolution.Hour => "60",
        BarResolution.Minute => "1",
        BarResolution.Second => "1",
        BarResolution.Tick => "1",
        _ => "101",
    };
}
