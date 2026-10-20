namespace Aether.Trading.Domain;

/// <summary>
/// 回测统计指标（从 Lean BacktestResult.Statistics 裁剪映射）。
/// 字段为 string 以匹配 Lean 原始输出（统计值多为格式化字符串）。映射在 Runtime.Lean 完成。
/// </summary>
public class BacktestStatistics
{
    public string? TotalReturn { get; set; }
    public string? Cagr { get; set; }
    public string? SharpeRatio { get; set; }
    public string? SortinoRatio { get; set; }
    public string? MaxDrawdown { get; set; }
    public string? WinRate { get; set; }
    public string? TotalTrades { get; set; }
    public string? NetProfit { get; set; }
    public string? Alpha { get; set; }
    public string? Beta { get; set; }
    public string? AnnualStandardDeviation { get; set; }
    public string? AnnualVariance { get; set; }
}

/// <summary>权益曲线点（时间, 权益）。</summary>
public class EquityPoint
{
    public DateTime Time { get; set; }
    public decimal Value { get; set; }
}

/// <summary>
/// 回测结果（业务领域，Lean-中性）。Runtime.Lean 把 Lean 产物映射成此类型，
/// 经 Application 层返回给 UI/AI 工具。不含任何 QuantConnect 类型。
/// </summary>
public class BacktestResult
{
    public bool IsSuccess { get; set; }

    public string? Error { get; set; }

    /// <summary>Lean 算法结束状态（Completed/Stopped/RuntimeError 等）。</summary>
    public string? AlgorithmStatus { get; set; }

    public BacktestStatistics? Statistics { get; set; }

    /// <summary>权益曲线（时间, 权益）。Lean BacktestResult.Charts["Strategy Equity"].Series["Equity"]。</summary>
    public IReadOnlyList<EquityPoint> EquityCurve { get; set; } = Array.Empty<EquityPoint>();

    /// <summary>算法运行日志（K线数据、信号结果等）。</summary>
    public IReadOnlyList<string> Logs { get; set; } = Array.Empty<string>();
}
