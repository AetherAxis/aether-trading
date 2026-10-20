using QuantConnect.Lean.Engine.Results;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// BacktestingResultHandler 子类：捕获 RuntimeError/ErrorMessage 的最后一条消息，
/// 供 LeanBacktestRunner 在回测未到 Completed 时取出清晰错误原因（而非只看 AlgorithmManager.State="Running"）。
/// 在 ConfigureLean 里把 result-handler 设为本类型全名，LEAN Composer 据此实例化。
/// </summary>
public class CapturingBacktestResultHandler : BacktestingResultHandler
{
    /// <summary>最近一次运行时/错误消息（RuntimeError 优先，其次 ErrorMessage）。</summary>
    public string? LastError { get; private set; }

    /// <summary>跳过 ResultsAnalyzer（它硬编码 SPY benchmark，本地无 SPY 数据会 .First() on empty 崩）。
    /// equity curve 仍从 Charts 取（MapEquityCurve），统计仍从 GenerateStatisticsResults 取，不受影响。</summary>
    public void DisableResultsAnalysis() => RunResultsAnalysis = false;

    public override void RuntimeError(string message, string stacktrace = "")
    {
        LastError = message;
        base.RuntimeError(message, stacktrace);
    }

    public override void ErrorMessage(string message, string stacktrace = "")
    {
        // ErrorMessage 可能是非致命警告；仅在没有 RuntimeError 时记录，避免被噪声覆盖
        LastError ??= message;
        base.ErrorMessage(message, stacktrace);
    }
}
