namespace Aether.Trading.Domain;

/// <summary>
/// 策略信号探针结果（<see cref="IStrategy.EvaluateSignal"/> 在一段历史 bar 上的逐根信号摘要）。
/// 纯领域、Lean 中立——只含信号，<b>不含 PnL/权益/回撤</b>（那是 <c>BacktestResult</c> 的职责）。
/// 用于「LLM 想看策略出什么信号」的轻量路径，免起整套 Lean 引擎。
/// </summary>
public sealed class StrategySignalResult
{
    public bool IsSuccess { get; set; }
    public string? Error { get; set; }

    /// <summary>策略名（StrategyRegistry key）。</summary>
    public string? StrategyName { get; set; }

    /// <summary>标的代码。</summary>
    public string? Symbol { get; set; }

    /// <summary>策略预热所需 bar 数（前这么多根信号恒为 Hold）。</summary>
    public int WarmUpBars { get; set; }

    /// <summary>实际喂入求值的 bar 数。</summary>
    public int EvaluatedBars { get; set; }

    /// <summary>最后一根 bar 的信号（=「策略当前会说啥」）。</summary>
    public SignalDirection LastSignal { get; set; }

    /// <summary>信号分布统计。</summary>
    public int LongCount { get; set; }
    public int FlatCount { get; set; }
    public int HoldCount { get; set; }

    /// <summary>最后一根 bar 的日期。</summary>
    public DateTime? LastBarDate { get; set; }

    /// <summary>信号翻转点（信号不同于前一根的 bar），按时间升序。紧凑回答「策略何时翻多/翻空」。</summary>
    public IReadOnlyList<SignalChange> Changes { get; set; } = Array.Empty<SignalChange>();
}

/// <summary>一个信号翻转点：某根 bar 上信号变为 <see cref="Signal"/>（不同于前一根）。</summary>
public sealed record SignalChange(DateTime Date, SignalDirection Signal);
