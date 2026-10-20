using System.Globalization;

namespace Aether.Trading.Domain;

/// <summary>策略信号方向。Hold=不动，Long=满仓做多，Flat=清仓。</summary>
public enum SignalDirection
{
    Hold,
    Long,
    Flat,
}

/// <summary>
/// 纯领域 K 线（OHLCV + 时间）。不依赖 QuantConnect，供 <see cref="IStrategy"/> 消费。
/// Runtime.Lean 把 Lean 的 CtpBar（自定义 BaseData）映射成此类型，策略不接触 QuantConnect 类型。
/// 回测/实盘共用——同一数据类型，算法无感知（Lean backtest=live）。
/// </summary>
public readonly record struct MarketBar(
    DateTime Time,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>策略参数定义（UI 据此动态渲染输入框）。Key=参数键，Label=显示名，DefaultValue=默认值。</summary>
public sealed record StrategyParameterDef(string Key, string Label, string DefaultValue);

/// <summary>
/// 策略参数（字符串键值，策略自行按需读取/类型转换）。从运行输入透传。
/// 用字符串而非强类型，让新策略可不改契约加参数；策略在 Initialize 里解析。
/// </summary>
public sealed class StrategyParameters
{
    private readonly IDictionary<string, string> _values;

    public StrategyParameters(IDictionary<string, string>? values)
        => _values = values ?? new Dictionary<string, string>();

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public T Get<T>(string key, T defaultValue)
    {
        if (!_values.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v)) return defaultValue;
        try { return (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture); }
        catch { return defaultValue; }
    }
}

/// <summary>
/// 回测/实盘策略契约：纯领域，不依赖 QuantConnect。Host 每根 K 线调一次 EvaluateSignal，
/// 策略内部维护指标状态并返回信号。可在 Domain.Tests 脱离 Lean 单测。
/// 回测与实盘共用同一策略逻辑（TradingHostAlgorithm 按 strategy-name 解析）。
/// </summary>
public interface IStrategy
{
    /// <summary>指标预热所需 bar 数。Host 据此 SetWarmUp，保证首个实盘 bar 前指标就绪。</summary>
    int WarmUpBars { get; }

    /// <summary>初始化策略（读参数、建指标）。Host 在 Initialize 末尾调一次。</summary>
    void Initialize(StrategyParameters parameters);

    /// <summary>评估当前 bar 的信号。每根 bar（含预热期）调一次，策略内部更新指标状态。</summary>
    SignalDirection EvaluateSignal(MarketBar bar);

    /// <summary>策略参数定义（UI 据此动态渲染输入框）。默认空（无参数策略）。策略可选覆盖。</summary>
    IReadOnlyList<StrategyParameterDef> ParameterDefinitions => Array.Empty<StrategyParameterDef>();
}
