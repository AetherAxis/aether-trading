using Aether.Trading.Application;
using Aether.Trading.Application.DependencyInjection;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;
using Aether.Trading.Runtime.Lean.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Aether.Trading.Tests;

/// <summary>
/// Verifies the DI wiring of the backtest path (no Lean engine run, no CTP/Quotes needed —
/// LeanBacktestRunner's IAnalysisManager is optional). Confirms AddTradingRuntimeLean registers
/// the runtime port, and AddTradingApplication wires the orchestration service.
/// 策略不再内置注册——由 Aether.Trading.Scripting 从笔记动态加载。
/// </summary>
public class RuntimeWiringTests
{
    [Fact]
    public void AddTradingRuntimeLean_registers_backtest_runtime()
    {
        var services = new ServiceCollection();
        services.AddTradingRuntimeLean();
        var sp = services.BuildServiceProvider();

        var runtime = sp.GetService<IBacktestRuntime>();
        Assert.NotNull(runtime);
    }

    [Fact]
    public void AddTradingApplication_wires_backtest_application_service_over_runtime()
    {
        var services = new ServiceCollection();
        services.AddTradingRuntimeLean();
        services.AddTradingApplication();
        var sp = services.BuildServiceProvider();

        var app = sp.GetService<IBacktestApplicationService>();
        Assert.NotNull(app);
    }

    [Fact]
    public void BacktestRuntimeRequest_is_Lean_neutral_no_config_fields()
    {
        // Smoke: the runtime request exposes only business fields, not Lean-specific ones.
        var req = new BacktestRuntimeRequest
        {
            Symbol = "ag2610",
            StrategyName = "MyStrategy",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 12, 31),
            InitialCash = 100_000m,
            FastPeriod = 10,
            SlowPeriod = 30,
            Resolution = BarResolution.Daily,
        };
        Assert.Equal("ag2610", req.Symbol);
        Assert.Equal(BarResolution.Daily, req.Resolution);
    }

    [Fact]
    public void AddTradingRuntimeLean_registers_historical_bar_source()
    {
        // 信号探针用的轻量 bar 源（不起 Lean）。IAnalysisManager 可选——未注册时优雅失败。
        var services = new ServiceCollection();
        services.AddTradingRuntimeLean();
        var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IHistoricalBarSource>());
    }

    [Fact]
    public void AddTradingApplication_wires_strategy_signal_service()
    {
        // 信号探针应用服务：依赖 IHistoricalBarSource（由 AddTradingRuntimeLean 注册）。
        var services = new ServiceCollection();
        services.AddTradingRuntimeLean();
        services.AddTradingApplication();
        var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IStrategySignalApplicationService>());
    }
}
