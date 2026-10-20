using Aether.Quotes.Domain;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aether.Trading.Runtime.Lean.DependencyInjection;

/// <summary>
/// DI entry point for the Lean runtime adapter (the engine impl). Registers the
/// backtest + live trading runtime ports backed by the in-process QuantConnect LEAN engine.
/// Lean source (externals/Lean) is referenced only by this project; QuantConnect.* types stay here.
/// </summary>
/// <remarks>
/// <b>Consumes</b> (does NOT register): <c>ICtpSession</c> from <c>AddCtpInfrastructure</c>,
/// <c>IAnalysisManager</c> from <c>AddQuotesApplication</c>. Call this after those.
/// </remarks>
public static class TradingRuntimeLeanExtensions
{
    /// <param name="defaultDataFolder">Optional override for the LEAN data-folder (else BacktestEnvironment locates externals/Lean/Data).</param>
    public static IServiceCollection AddTradingRuntimeLean(
        this IServiceCollection services,
        string? defaultDataFolder = null)
    {
        // 策略由 Aether.Trading.Scripting 从笔记动态加载注册到 StrategyRegistry（静态表，Lean 在 DI 外创建 algorithm，host 据此解析）
        services.AddSingleton<IBacktestRuntime>(sp => new LeanBacktestRunner(
            analysisManager: sp.GetService<IAnalysisManager>(),
            logger: sp.GetService<ILogger<LeanBacktestRunner>>(),
            defaultDataFolder: defaultDataFolder));

        services.AddSingleton<ILiveTradingRuntime, LeanLiveTradingService>();

        // 轻量历史 K 线源（不起 Lean 引擎）：桥接 IAnalysisManager，供信号探针等不需要回测管线的场景消费。
        services.AddSingleton<IHistoricalBarSource>(sp => new HistoricalBarSource(
            sp.GetService<IAnalysisManager>()));
        return services;
    }
}
