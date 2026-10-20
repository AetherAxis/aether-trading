using System.Collections.Generic;
using System.Linq;
using Aether.Trading.Domain;
using QuantConnect;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Statistics;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 把 LEAN 的回测产物（BacktestingResultHandler 的统计摘要 + 权益曲线 Chart）映射到领域 <see cref="BacktestResult"/>。
/// LEAN Summary 是 Dictionary&lt;string,string&gt;（值多为格式化字符串），按 key 裁剪映射。
/// </summary>
internal static class BacktestResultMapper
{
    /// <summary>LEAN Summary 里用到的 key（与 LEAN StatisticsResults.Summary 一致）。</summary>
    private static string? Get(Dictionary<string, string> summary, string key)
        => summary.TryGetValue(key, out var v) ? v : null;

    public static BacktestStatistics MapStatistics(StatisticsResults stats)
    {
        var s = stats?.Summary ?? new Dictionary<string, string>();
        // Key 取自 LEAN PerformanceMetrics 常量（QuantConnect.Common/Statistics/PerformanceMetrics.cs）
        return new BacktestStatistics
        {
            TotalReturn = Get(s, "Total Net Profit"),
            Cagr = Get(s, "Compounding Annual Return"),
            SharpeRatio = Get(s, "Sharpe Ratio"),
            SortinoRatio = Get(s, "Sortino Ratio"),
            MaxDrawdown = Get(s, "Drawdown"),
            WinRate = Get(s, "Win Rate"),
            TotalTrades = Get(s, "Total Orders"),
            NetProfit = Get(s, "Net Profit"),
            Alpha = Get(s, "Alpha"),
            Beta = Get(s, "Beta"),
            AnnualStandardDeviation = Get(s, "Annual Standard Deviation"),
            AnnualVariance = Get(s, "Annual Variance"),
        };
    }

    /// <summary>从 BacktestingResultHandler.Charts["Strategy Equity"].Series["Equity"] 取权益曲线。</summary>
    public static IReadOnlyList<EquityPoint> MapEquityCurve(BacktestingResultHandler resultHandler)
    {
        try
        {
            if (!resultHandler.Charts.TryGetValue("Strategy Equity", out var chart)) return System.Array.Empty<EquityPoint>();
            if (!chart.Series.TryGetValue("Equity", out var series)) return System.Array.Empty<EquityPoint>();

            return series.Values
                .Select(p =>
                {
                    // equity series 是 CandlestickSeries，Values 是 Candlestick（不是 ChartPoint）
                    // Candlestick.Close = portfolio value；回退 ChartPoint.Y 兼容旧版
                    var value = (p as Candlestick)?.Close ?? (p as ChartPoint)?.Y ?? 0m;
                    return new EquityPoint { Time = p.Time, Value = value };
                })
                .ToList();
        }
        catch
        {
            return System.Array.Empty<EquityPoint>();
        }
    }
}
