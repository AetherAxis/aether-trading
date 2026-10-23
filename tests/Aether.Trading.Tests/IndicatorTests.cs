using System.Collections.Generic;
using Aether.Trading.Domain;
using Aether.Trading.Domain.Indicators;
using Xunit;

namespace Aether.Trading.Tests;

public class IndicatorTests
{
    private static MarketBar Bar(decimal close) => new(DateTime.MinValue, close, close, close, close, 0);
    private static MarketBar Bar(decimal h, decimal l, decimal c) => new(DateTime.MinValue, c, h, l, c, 0);
    private static StrategyParameters NoParams => new(null);
    private static IIndicator Sma(int period)
        => IndicatorFactory.Create("sma", new Dictionary<string, string> { ["period"] = period.ToString() }, NoParams);

    [Fact]
    public void Sma_not_ready_until_period_bars()
    {
        var sma = Sma(3);
        sma.Update(Bar(10));
        sma.Update(Bar(20));
        Assert.False(sma.IsReady);
        sma.Update(Bar(30));
        Assert.True(sma.IsReady);
        Assert.Equal(20m, sma.Value);
    }

    [Fact]
    public void Sma_rolls_window()
    {
        var sma = Sma(3);
        foreach (var c in new[] { 10m, 20m, 30m, 40m }) sma.Update(Bar(c));
        // (20+30+40)/3 = 30
        Assert.Equal(30m, sma.Value);
    }

    [Fact]
    public void Ema_seeds_with_sma_then_recurses()
    {
        var ema = IndicatorFactory.Create("ema", new Dictionary<string, string> { ["period"] = "3" }, NoParams);
        // k = 2/(3+1) = 0.5；种子 = SMA(10,20,30) = 20
        foreach (var c in new[] { 10m, 20m, 30m }) ema.Update(Bar(c));
        Assert.True(ema.IsReady);
        Assert.Equal(20m, ema.Value);
        // 下一根 40 → 40*0.5 + 20*0.5 = 30
        ema.Update(Bar(40));
        Assert.Equal(30m, ema.Value);
    }

    [Fact]
    public void Rsi_is_100_for_all_gains()
    {
        var rsi = IndicatorFactory.Create("rsi", new Dictionary<string, string> { ["period"] = "14" }, NoParams);
        // 单调递增：每根 +1 → 全为增益，avgLoss=0 → RSI=100
        for (int i = 1; i <= 15; i++) rsi.Update(Bar(i));
        Assert.True(rsi.IsReady);
        Assert.Equal(100m, rsi.Value);
    }

    [Fact]
    public void Rsi_is_0_for_all_losses()
    {
        var rsi = IndicatorFactory.Create("rsi", new Dictionary<string, string> { ["period"] = "14" }, NoParams);
        for (int i = 20; i >= 5; i--) rsi.Update(Bar(i));
        Assert.True(rsi.IsReady);
        Assert.Equal(0m, rsi.Value);
    }

    [Fact]
    public void Bollinger_bands_equal_closes_zero_sigma()
    {
        var bb = IndicatorFactory.Create("bollinger", new Dictionary<string, string> { ["period"] = "5", ["stdDev"] = "2" }, NoParams);
        for (int i = 0; i < 5; i++) bb.Update(Bar(10));
        Assert.True(bb.IsReady);
        Assert.Equal(10m, bb.GetValue("middle"));
        Assert.Equal(10m, bb.GetValue("upper"));
        Assert.Equal(10m, bb.GetValue("lower"));
    }

    [Fact]
    public void Atr_wilder_seed()
    {
        var atr = IndicatorFactory.Create("atr", new Dictionary<string, string> { ["period"] = "3" }, NoParams);
        // B1: TR=H-L=5（无前收）；B2 prevC=12 → TR=max(5,|16-12|,|11-12|)=5；B3 prevC=13 → TR=max(6,5,1)=6
        atr.Update(Bar(15, 10, 12));
        atr.Update(Bar(16, 11, 13));
        atr.Update(Bar(18, 12, 15));
        Assert.True(atr.IsReady);
        Assert.Equal((5m + 5m + 6m) / 3m, atr.Value); // ≈5.333
    }

    [Fact]
    public void Stochastic_k100_when_close_at_high()
    {
        var stoch = IndicatorFactory.Create("stochastic", new Dictionary<string, string> { ["kPeriod"] = "2", ["dPeriod"] = "1" }, NoParams);
        // B1: H=20,L=10,C=20；B2: H=25,L=10,C=25 → 最高25 最低10 收25 → %K=100
        stoch.Update(Bar(20, 10, 20));
        stoch.Update(Bar(25, 10, 25));
        Assert.True(stoch.IsReady);
        Assert.Equal(100m, stoch.GetValue("k"));
        Assert.Equal(100m, stoch.GetValue("d"));
    }

    [Fact]
    public void Macd_hist_equals_macd_minus_signal()
    {
        var macd = IndicatorFactory.Create("macd", new Dictionary<string, string> { ["fast"] = "3", ["slow"] = "5", ["signal"] = "2" }, NoParams);
        for (int i = 1; i <= 20; i++) macd.Update(Bar(i));
        Assert.True(macd.IsReady);
        Assert.Equal(macd.GetValue("macd") - macd.GetValue("signal"), macd.GetValue("hist"));
        Assert.Throws<ArgumentException>(() => macd.GetValue("bogus"));
    }

    [Fact]
    public void Factory_unknown_type_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            IndicatorFactory.Create("nope", new Dictionary<string, string>(), NoParams));
    }
}
