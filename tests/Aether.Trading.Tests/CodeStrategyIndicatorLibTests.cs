using Aether.Trading.Domain;
using Aether.Trading.Domain.Indicators;
using Aether.Trading.Scripting;
using Xunit;

namespace Aether.Trading.Tests;

/// <summary>
/// 验证 CodeStrategy（Roslyn 编译路径）能复用增量指标库——即 StrategyScriptCompiler 解析得到
/// Aether.Trading.Domain.Indicators 类型，且模板风格的源码能编译、初始化、求值。
/// 这保证 C# 策略与 YAML 策略底层同源（同一套 IndicatorFactory / SmaIndicator）。
/// </summary>
public class CodeStrategyIndicatorLibTests
{
    [Fact]
    public void TryCompile_compiles_strategy_using_indicator_factory()
    {
        // 强制 Trading.Domain 程序集加载，确保 StrategyScriptCompiler 的 AppDomain.GetAssemblies() 含之
        var _ = typeof(IndicatorFactory);
        GC.KeepAlive(_);

        var source = @"using System;
using System.Collections.Generic;
using Aether.Trading.Domain;
using Aether.Trading.Domain.Indicators;

public class IndicatorLibStrategy : IStrategy
{
    private IIndicator _fast = null!;
    private IIndicator _slow = null!;
    public int WarmUpBars { get; private set; }
    public IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
    {
        new StrategyParameterDef(""FastPeriod"", ""快线"", ""2""),
        new StrategyParameterDef(""SlowPeriod"", ""慢线"", ""3""),
    };
    public void Initialize(StrategyParameters parameters)
    {
        _fast = IndicatorFactory.Create(""sma"", new Dictionary<string, string> { [""period""] = ""2"" }, parameters);
        _slow = IndicatorFactory.Create(""sma"", new Dictionary<string, string> { [""period""] = ""3"" }, parameters);
        WarmUpBars = Math.Max(_fast.RequiredBars, _slow.RequiredBars);
    }
    public SignalDirection EvaluateSignal(MarketBar bar)
    {
        _fast.Update(bar);
        _slow.Update(bar);
        if (!_fast.IsReady || !_slow.IsReady) return SignalDirection.Hold;
        return _fast.Value > _slow.Value ? SignalDirection.Long : SignalDirection.Flat;
    }
}";

        Assert.True(StrategyScriptCompiler.TryCompile(source, out var strategy, out var err), err ?? "(无错误信息)");
        Assert.NotNull(strategy);

        strategy!.Initialize(new StrategyParameters(null));
        Assert.Equal(3, strategy.WarmUpBars);

        // 预热期内 Hold
        var sig = strategy.EvaluateSignal(new MarketBar(DateTime.MinValue, 10, 10, 10, 10, 0));
        Assert.Equal(SignalDirection.Hold, sig);
    }
}
