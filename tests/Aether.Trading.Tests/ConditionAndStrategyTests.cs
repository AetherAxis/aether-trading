using System.Collections.Generic;
using Aether.Trading.Domain;
using Aether.Trading.Domain.Indicators;
using Aether.Trading.Domain.Indicators.Conditions;
using Xunit;

namespace Aether.Trading.Tests;

public class ConditionAndStrategyTests
{
    private static MarketBar Bar(decimal close) => new(DateTime.MinValue, close, close, close, close, 0);
    private static StrategyParameters NoParams => new(null);
    private static IIndicator Sma(int period)
        => IndicatorFactory.Create("sma", new Dictionary<string, string> { ["period"] = period.ToString() }, NoParams);
    private static Dictionary<string, IIndicator> Inds(params (string name, IIndicator ind)[] pairs)
    {
        var d = new Dictionary<string, IIndicator>();
        foreach (var (n, i) in pairs) d[n] = i;
        return d;
    }

    [Fact]
    public void CrossAbove_true_only_on_crossing_bar()
    {
        var fast = Sma(1); var slow = Sma(1);
        var cond = ConditionCompiler.Compile(
            new ConditionDef("cross_above", new object[] { "fast", "slow" }),
            Inds(("fast", fast), ("slow", slow)));

        fast.Update(Bar(10)); slow.Update(Bar(20)); cond.Update(Bar(0));
        Assert.False(cond.IsTrue); // 首根就绪，无 prev

        fast.Update(Bar(25)); slow.Update(Bar(15)); cond.Update(Bar(0));
        Assert.True(cond.IsTrue); // 10<=20 && 25>15 → 上穿

        fast.Update(Bar(26)); slow.Update(Bar(14)); cond.Update(Bar(0));
        Assert.False(cond.IsTrue); // 已在上穿之后，不再触发
    }

    [Fact]
    public void Comparison_gt_lt_evaluate()
    {
        var fast = Sma(1);
        var cond = ConditionCompiler.Compile(
            new ConditionDef("gt", new object[] { "fast", 15 }),
            Inds(("fast", fast)));
        fast.Update(Bar(20)); cond.Update(Bar(0));
        Assert.True(cond.IsTrue);
        fast.Update(Bar(10)); cond.Update(Bar(0));
        Assert.False(cond.IsTrue);
    }

    [Fact]
    public void And_or_not_compose()
    {
        var fast = Sma(1); var slow = Sma(1);
        var inds = Inds(("fast", fast), ("slow", slow));

        var and = ConditionCompiler.Compile(
            new ConditionDef("and", new object[]
            {
                new ConditionDef("gt", new object[] { "fast", 15 }),
                new ConditionDef("lt", new object[] { "slow", 20 }),
            }), inds);
        fast.Update(Bar(20)); slow.Update(Bar(10)); and.Update(Bar(0));
        Assert.True(and.IsTrue);
        fast.Update(Bar(10)); slow.Update(Bar(10)); and.Update(Bar(0));
        Assert.False(and.IsTrue); // gt false

        var not = ConditionCompiler.Compile(
            new ConditionDef("not", new object[] { new ConditionDef("gt", new object[] { "fast", 15 }) }), inds);
        fast.Update(Bar(10)); not.Update(Bar(0));
        Assert.True(not.IsTrue);
        fast.Update(Bar(20)); not.Update(Bar(0));
        Assert.False(not.IsTrue);
    }

    [Fact]
    public void YamlStrategy_sma_cross_signal_sequence()
    {
        var program = new StrategyProgram(
            Params: new[]
            {
                new StrategyParameterDef("FastPeriod", "快线周期", "2"),
                new StrategyParameterDef("SlowPeriod", "慢线周期", "3"),
            },
            Indicators: new Dictionary<string, IndicatorDef>
            {
                ["fast"] = new IndicatorDef("sma", new Dictionary<string, string> { ["period"] = "2" }),
                ["slow"] = new IndicatorDef("sma", new Dictionary<string, string> { ["period"] = "3" }),
            },
            Rules: new[]
            {
                new StrategyRuleDef(SignalDirection.Long, new ConditionDef("cross_above", new object[] { "fast", "slow" })),
                new StrategyRuleDef(SignalDirection.Flat, new ConditionDef("cross_below", new object[] { "fast", "slow" })),
            },
            Default: SignalDirection.Hold);

        var strategy = new YamlStrategy(program);
        strategy.Initialize(NoParams);
        Assert.Equal(3, strategy.WarmUpBars);
        Assert.Equal(2, strategy.ParameterDefinitions.Count);

        // closes: 10,10,10,20,20,5 → 信号 Hold,Hold,Hold,Long,Hold,Flat
        var closes = new decimal[] { 10, 10, 10, 20, 20, 5 };
        var expected = new[] { SignalDirection.Hold, SignalDirection.Hold, SignalDirection.Hold,
                               SignalDirection.Long, SignalDirection.Hold, SignalDirection.Flat };
        for (int i = 0; i < closes.Length; i++)
        {
            var sig = strategy.EvaluateSignal(Bar(closes[i]));
            Assert.Equal(expected[i], sig);
        }
    }

    [Fact]
    public void YamlStrategy_param_templating()
    {
        var program = new StrategyProgram(
            Params: new[] { new StrategyParameterDef("FastPeriod", "快线周期", "5") },
            Indicators: new Dictionary<string, IndicatorDef>
            {
                ["fast"] = new IndicatorDef("sma", new Dictionary<string, string> { ["period"] = "{FastPeriod}" }),
            },
            Rules: Array.Empty<StrategyRuleDef>(),
            Default: SignalDirection.Hold);

        var strategy = new YamlStrategy(program);
        strategy.Initialize(new StrategyParameters(new Dictionary<string, string> { ["FastPeriod"] = "7" }));
        Assert.Equal(7, strategy.WarmUpBars);
    }

    [Fact]
    public void ConditionDef_parse_flat_and_nested()
    {
        var flat = new Dictionary<string, object> { ["gt"] = new List<object> { "fast", 20 } };
        Assert.True(ConditionDef.TryParse(flat, out var c));
        Assert.Equal("gt", c!.Op);
        Assert.Equal(2, c.Args.Count);

        var nested = new Dictionary<string, object>
        {
            ["and"] = new List<object>
            {
                new Dictionary<string, object> { ["gt"] = new List<object> { "fast", "slow" } },
                new Dictionary<string, object> { ["lt"] = new List<object> { "rsi", 70 } },
            },
        };
        Assert.True(ConditionDef.TryParse(nested, out var c2));
        Assert.Equal("and", c2!.Op);
        Assert.Equal(2, c2.Args.Count);
        Assert.IsType<ConditionDef>(c2.Args[0]);
        Assert.IsType<ConditionDef>(c2.Args[1]);
    }
}
