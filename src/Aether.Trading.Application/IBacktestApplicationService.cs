using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;

namespace Aether.Trading.Application;

/// <summary>
/// 回测应用层输入（面向 UI/工具的强类型参数）。不含 Lean 的 Parameters 字典——
/// Lean 参数键的拼装收敛在 Runtime.Lean 的 runner 里，避免 Lean 细节泄漏到 UI 层。
/// </summary>
public sealed class BacktestRunInput
{
    /// <summary>回测标的（期货合约代码，如 ag2610），走 IAnalysisManager 取历史 K 线。</summary>
    public string Symbol { get; set; } = "ag2610";

    /// <summary>策略名（StrategyRegistry 注册的 key，如 "SmaCross"）。</summary>
    public string StrategyName { get; set; } = "SmaCross";

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    /// <summary>初始资金，默认 100000。</summary>
    public decimal InitialCash { get; set; } = 100_000m;

    public int FastPeriod { get; set; } = 10;
    public int SlowPeriod { get; set; } = 30;

    /// <summary>策略参数（键值对，透传给策略 Initialize）。与 FastPeriod/SlowPeriod 二选一：优先用 StrategyParameters。</summary>
    public Dictionary<string, string>? StrategyParameters { get; set; }

    /// <summary>K 线分辨率，默认 Daily。</summary>
    public BarResolution Resolution { get; set; } = BarResolution.Daily;
}

/// <summary>
/// 回测应用编排服务契约：把 UI/工具层的强类型输入编排成一次回测，返回领域结果 <see cref="BacktestResult"/>。
/// 实现薄封装 Runtime 的 <see cref="IBacktestRuntime"/>——UI 层经此契约访问回测，不直接依赖 Runtime 契约。
/// 未来 lean-trading 式编排（参数扫描/多策略对比/结果落库）在本层扩展。
/// </summary>
public interface IBacktestApplicationService
{
    /// <summary>运行一次回测。引擎同步阻塞，实现内部不卸载线程——调用方自行 Task.Run。</summary>
    Task<BacktestResult> RunAsync(BacktestRunInput input, CancellationToken ct = default);
}
