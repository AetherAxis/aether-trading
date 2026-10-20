using Aether.Trading.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.Trading.Application.DependencyInjection;

/// <summary>
/// DI entry point for the Trading Application layer. Registers the backtest + live trading
/// orchestration services (thin wrappers over the Runtime abstraction ports). Runtime.Lean
/// (the engine impl) is registered separately via <c>AddTradingRuntimeLean</c> at the composition root.
/// </summary>
public static class TradingApplicationExtensions
{
    public static IServiceCollection AddTradingApplication(this IServiceCollection services)
    {
        services.AddSingleton<IBacktestApplicationService, BacktestApplicationService>();
        services.AddSingleton<ILiveTradingApplicationService, LiveTradingApplicationService>();
        // 信号探针（不起 Lean）：依赖 IHistoricalBarSource（由 AddTradingRuntimeLean 注册）。
        services.AddSingleton<IStrategySignalApplicationService, StrategySignalApplicationService>();
        return services;
    }
}
