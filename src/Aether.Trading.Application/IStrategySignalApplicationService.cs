using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;

namespace Aether.Trading.Application;

/// <summary>
/// 策略信号探针输入（面向 UI/工具的强类型参数）。不含任何 Lean 细节——
/// 只描述「拿哪个标的最近多少根 bar、跑哪个策略、看信号」。
/// </summary>
public sealed class StrategySignalProbeInput
{
    /// <summary>策略名（StrategyRegistry 注册的 key）。</summary>
    public string StrategyName { get; set; } = "SmaCross";

    /// <summary>标的代码（期货合约/股票代码，走 IAnalysisManager 取 K 线）。</summary>
    public string Symbol { get; set; } = "ag2610";

    /// <summary>取最近多少根 bar 喂给策略求值（含预热期）。默认 100。</summary>
    public int BarCount { get; set; } = 100;

    /// <summary>K 线分辨率，默认 Daily。</summary>
    public BarResolution Resolution { get; set; } = BarResolution.Daily;

    /// <summary>策略参数（透传给 IStrategy.Initialize）。null 则用策略自身默认值。</summary>
    public Dictionary<string, string>? StrategyParameters { get; set; }
}

/// <summary>
/// 策略信号探针应用契约：取最近 <paramref name="BarCount"/> 根 bar，进程内直接 <see cref="IStrategy.EvaluateSignal"/>
/// 逐根求值，返回信号摘要。<b>不起 Lean 引擎、不算 PnL</b>——只回答「策略出什么信号」。
/// 与 <see cref="IBacktestApplicationService"/>（起整套 Lean 做回测、回答「表现如何」）平行。
/// 镜像规则引擎的 <c>run_rule</c>：LLM 调一次，内部跑循环，返回压缩结果，免起重引擎。
/// </summary>
public interface IStrategySignalApplicationService
{
    /// <summary>探针：进程内逐根求信号，返回摘要（不含 PnL）。</summary>
    Task<StrategySignalResult> ProbeAsync(StrategySignalProbeInput input, CancellationToken ct = default);
}
