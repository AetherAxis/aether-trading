using Aether.Quotes.Domain;
using Aether.Trading.Application;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;
using Aether.Trading.Runtime.Lean;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aether.Trading.Tests;

/// <summary>
/// 策略信号探针（<see cref="IStrategySignalApplicationService"/>）测试。
/// 用假 <see cref="IHistoricalBarSource"/> + 临时注册的 <see cref="IStrategy"/> 验证：
/// 逐根信号序列、翻转点、分布统计、末根信号，以及未知策略/空数据等错误路径。
/// 不起 Lean、不算 PnL——只验证信号摘要逻辑。
/// </summary>
public class StrategySignalProbeTests
{
    /// <summary>测试策略：close &gt; 阈值→Long，否则 Flat；WarmUpBars=0（无预热）。</summary>
    private sealed class ThresholdStrategy : IStrategy
    {
        private decimal _threshold;
        public int WarmUpBars => 0;
        public IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
        {
            new StrategyParameterDef("Threshold", "阈值", "15"),
        };
        public void Initialize(StrategyParameters parameters) => _threshold = parameters.Get("Threshold", 15m);
        public SignalDirection EvaluateSignal(MarketBar bar) =>
            bar.Close > _threshold ? SignalDirection.Long : SignalDirection.Flat;
    }

    private sealed class FakeBarSource : IHistoricalBarSource
    {
        private readonly IReadOnlyList<MarketBar> _bars;
        public FakeBarSource(IReadOnlyList<MarketBar> bars) => _bars = bars;
        public Task<(IReadOnlyList<MarketBar>? Bars, string? Error)> GetBarsAsync(
            string symbol, BarResolution resolution, int count, CancellationToken cancellationToken = default)
            => Task.FromResult<(IReadOnlyList<MarketBar>? Bars, string? Error)>((_bars, null));
    }

    private static MarketBar Bar(DateTime d, decimal close) => new(d, close, close, close, close, 0);

    [Fact]
    public async Task Probe_returns_signal_summary_and_changes()
    {
        var name = "ProbeThreshold_" + Guid.NewGuid().ToString("N")[..8];
        StrategyRegistry.Register(name, () => new ThresholdStrategy());
        try
        {
            // closes 10,20,20,5 → Flat,Long,Long,Flat（阈值 15）
            var bars = new List<MarketBar>
            {
                Bar(new DateTime(2026, 1, 1), 10),
                Bar(new DateTime(2026, 1, 2), 20),
                Bar(new DateTime(2026, 1, 3), 20),
                Bar(new DateTime(2026, 1, 4), 5),
            };
            var svc = new StrategySignalApplicationService(new FakeBarSource(bars));

            var r = await svc.ProbeAsync(new StrategySignalProbeInput
            { StrategyName = name, Symbol = "test", BarCount = 4 });

            Assert.True(r.IsSuccess);
            Assert.Equal(SignalDirection.Flat, r.LastSignal);
            Assert.Equal(4, r.EvaluatedBars);
            Assert.Equal(0, r.WarmUpBars);
            Assert.Equal(2, r.LongCount);
            Assert.Equal(2, r.FlatCount);
            Assert.Equal(0, r.HoldCount);
            Assert.Equal(new DateTime(2026, 1, 4), r.LastBarDate);

            // 翻转点：首根 Flat，第2根 Long，第4根 Flat（第3根同为 Long 不变）
            Assert.Equal(3, r.Changes.Count);
            Assert.Equal(SignalDirection.Flat, r.Changes[0].Signal);
            Assert.Equal(SignalDirection.Long, r.Changes[1].Signal);
            Assert.Equal(SignalDirection.Flat, r.Changes[2].Signal);
        }
        finally { StrategyRegistry.Unregister(name); }
    }

    [Fact]
    public async Task Probe_passes_strategy_parameters()
    {
        var name = "ProbeParams_" + Guid.NewGuid().ToString("N")[..8];
        StrategyRegistry.Register(name, () => new ThresholdStrategy());
        try
        {
            // 阈值改为 25：close 10,20,30 → Flat,Flat,Long
            var bars = new List<MarketBar>
            {
                Bar(new DateTime(2026, 1, 1), 10),
                Bar(new DateTime(2026, 1, 2), 20),
                Bar(new DateTime(2026, 1, 3), 30),
            };
            var svc = new StrategySignalApplicationService(new FakeBarSource(bars));

            var r = await svc.ProbeAsync(new StrategySignalProbeInput
            {
                StrategyName = name, Symbol = "test", BarCount = 3,
                StrategyParameters = new Dictionary<string, string> { ["Threshold"] = "25" },
            });

            Assert.True(r.IsSuccess);
            Assert.Equal(1, r.LongCount);
            Assert.Equal(2, r.FlatCount);
            Assert.Equal(SignalDirection.Long, r.LastSignal);
        }
        finally { StrategyRegistry.Unregister(name); }
    }

    [Fact]
    public async Task Probe_unknown_strategy_fails()
    {
        var svc = new StrategySignalApplicationService(new FakeBarSource(new List<MarketBar> { Bar(DateTime.MinValue, 1) }));
        var r = await svc.ProbeAsync(new StrategySignalProbeInput
        { StrategyName = "NoSuchStrategy_xyz", Symbol = "test", BarCount = 5 });
        Assert.False(r.IsSuccess);
        Assert.Contains("未注册", r.Error ?? "");
    }

    [Fact]
    public async Task Probe_empty_bars_fails()
    {
        var name = "ProbeEmpty_" + Guid.NewGuid().ToString("N")[..8];
        StrategyRegistry.Register(name, () => new ThresholdStrategy());
        try
        {
            var svc = new StrategySignalApplicationService(new FakeBarSource(new List<MarketBar>()));
            var r = await svc.ProbeAsync(new StrategySignalProbeInput
            { StrategyName = name, Symbol = "test", BarCount = 5 });
            Assert.False(r.IsSuccess);
            Assert.Contains("无历史", r.Error ?? "");
        }
        finally { StrategyRegistry.Unregister(name); }
    }

    [Fact]
    public async Task HistoricalBarSource_returns_null_when_no_analysis_manager()
    {
        // 无 IAnalysisManager 时优雅失败（Mac/未配行情），不崩
        var src = new HistoricalBarSource(analysis: null);
        var (bars, error) = await src.GetBarsAsync("ag2610", BarResolution.Daily, 10);
        Assert.Null(bars);
        Assert.NotNull(error);
    }
}
