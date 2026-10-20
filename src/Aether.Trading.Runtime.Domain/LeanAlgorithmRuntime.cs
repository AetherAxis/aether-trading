using Aether.Trading.Domain;

namespace Aether.Trading.Runtime.Domain;

/// <summary>
/// Lean 算法回测运行请求（Lean-中性 DTO）。与 <see cref="BacktestRuntimeRequest"/> 平行、互不耦合——
/// 该请求的 <see cref="StrategyName"/> 指向 <c>LeanAlgorithmRegistry</c>（非 <c>StrategyRegistry</c>），
/// 由 <c>LeanAlgorithmBacktestRunner</c>（Trading.Runtime.Lean）消费：直接加载用户 <c>QCAlgorithm</c> 子类跑 Lean 引擎。
/// </summary>
public sealed class LeanAlgorithmBacktestRequest
{
    /// <summary>Lean 算法名（LeanAlgorithmRegistry 注册的 key = 笔记标题）。</summary>
    public string StrategyName { get; set; } = "";

    /// <summary>回测标的（期货合约/股票代码，如 ag2610 / 600000），走 IAnalysisManager 取历史 K 线。</summary>
    public string Symbol { get; set; } = "";

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    /// <summary>初始资金，默认 100000。</summary>
    public decimal InitialCash { get; set; } = 100_000m;

    /// <summary>K 线分辨率，默认 Daily。</summary>
    public BarResolution Resolution { get; set; } = BarResolution.Daily;

    /// <summary>用户算法参数（透传进 Lean Config "parameters"，算法经 GetParameter / LeanAlgorithmBase.ConfigureFromConfig 读）。</summary>
    public Dictionary<string, string>? StrategyParameters { get; set; }
}

/// <summary>
/// Lean 算法回测运行时契约：跑一次 Lean 引擎回测，用户算法为 <c>QCAlgorithm</c> 子类（非 IStrategy），
/// 返回领域结果 <see cref="BacktestResult"/>（与 IStrategy 路径同结果类型，UI 复用展示）。
/// 实现位于 Aether.Trading.Runtime.Lean（LeanAlgorithmBacktestRunner）——与 <see cref="IBacktestRuntime"/> 平行、互不耦合。
/// </summary>
public interface ILeanAlgorithmRuntime
{
    /// <summary>运行一次回测。引擎同步阻塞，实现内部不卸载线程——调用方自行 Task.Run。</summary>
    Task<BacktestResult> RunAsync(LeanAlgorithmBacktestRequest request, CancellationToken ct = default);
}
