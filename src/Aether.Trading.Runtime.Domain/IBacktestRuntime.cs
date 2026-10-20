using Aether.Trading.Domain;

namespace Aether.Trading.Runtime.Domain;

/// <summary>
/// K 线分辨率（Lean-中性枚举）。Runtime.Lean 映射到 Lean 的 <c>Resolution</c>。
/// Trading/Application 层用此枚举，不接触 QuantConnect 类型。
/// </summary>
public enum BarResolution
{
    Daily,
    Hour,
    Minute,
    Second,
    Tick,
}

/// <summary>
/// 回测运行请求（Lean-中性）。仅含业务字段——Lean 专属（algorithm-type-name、object-store-root、
/// handler 链、Config 键、Parameters 字典、DataFolder、AlgorithmAssemblyPath）全部收敛在 Runtime.Lean 的 runner 内部，
/// 由本请求字段组装。Trading/Application 透传本类型，不感知 Lean。
/// </summary>
public sealed class BacktestRuntimeRequest
{
    /// <summary>策略名（StrategyRegistry 注册的 key，如 "SmaCross"）。TradingHostAlgorithm 据此解析 IStrategy。</summary>
    public string StrategyName { get; set; } = "SmaCross";

    /// <summary>回测标的（期货合约代码，如 "ag2610"）。</summary>
    public string Symbol { get; set; } = "SPY";

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    /// <summary>初始资金。</summary>
    public decimal InitialCash { get; set; } = 100_000m;

    public int FastPeriod { get; set; } = 10; /// 保留兼容（StrategyParameters 优先）
    public Dictionary<string, string>? StrategyParameters { get; set; }
    public int SlowPeriod { get; set; } = 30;

    /// <summary>K 线分辨率，默认 Daily。</summary>
    public BarResolution Resolution { get; set; } = BarResolution.Daily;
}

/// <summary>
/// 回测运行时契约：跑一次引擎回测，返回领域结果 <see cref="BacktestResult"/>。
/// 实现位于 Aether.Trading.Runtime.Lean（LeanBacktestRunner : IBacktestRuntime）——Lean 源码只在该叶子内引用。
/// 引擎是同步阻塞 CPU 活，实现内部用 Task.Run 卸载到后台线程。
/// </summary>
public interface IBacktestRuntime
{
    Task<BacktestResult> RunAsync(BacktestRuntimeRequest request, CancellationToken ct = default);
}
