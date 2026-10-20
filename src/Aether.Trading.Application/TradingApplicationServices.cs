using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;

namespace Aether.Trading.Application;

/// <summary>
/// <see cref="IBacktestApplicationService"/> 实现：薄编排层，把 UI 的 <see cref="BacktestRunInput"/>
/// 映射成 Runtime 的 <see cref="BacktestRuntimeRequest"/>（Lean-中性），转发给 <see cref="IBacktestRuntime"/>。
/// Lean 参数键的拼装在 Runtime.Lean runner 内，本层不感知 Lean。
/// </summary>
public sealed class BacktestApplicationService : IBacktestApplicationService
{
    private readonly IBacktestRuntime _runtime;

    public BacktestApplicationService(IBacktestRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <inheritdoc />
    public Task<BacktestResult> RunAsync(BacktestRunInput input, CancellationToken ct = default)
    {
        var request = new BacktestRuntimeRequest
        {
            StrategyName = input.StrategyName,
            Symbol = input.Symbol,
            StartDate = input.StartDate,
            EndDate = input.EndDate,
            InitialCash = input.InitialCash,
            FastPeriod = input.FastPeriod,
            StrategyParameters = input.StrategyParameters,
            SlowPeriod = input.SlowPeriod,
            Resolution = input.Resolution,
        };
        return _runtime.RunAsync(request, ct);
    }
}

/// <summary>
/// <see cref="ILiveTradingApplicationService"/> 实现：薄编排层，映射 <see cref="LiveTradingRunInput"/>
/// 成 <see cref="LiveTradingRuntimeRequest"/> 转发给 <see cref="ILiveTradingRuntime"/>，并代理状态/日志事件。
/// </summary>
public sealed class LiveTradingApplicationService : ILiveTradingApplicationService
{
    private readonly ILiveTradingRuntime _runtime;

    public LiveTradingApplicationService(ILiveTradingRuntime runtime)
    {
        _runtime = runtime;
        _runtime.StatusChanged += (s, st) => StatusChanged?.Invoke(s, st);
        _runtime.LogReceived += (s, msg) => LogReceived?.Invoke(s, msg);
    }

    public LiveTradingRuntimeStatus Status => _runtime.Status;
    public bool IsRunning => _runtime.IsRunning;
    public event EventHandler<LiveTradingRuntimeStatus>? StatusChanged;
    public event EventHandler<string>? LogReceived;

    public Task<(bool Success, string? Error)> StartAsync(LiveTradingRunInput input, CancellationToken ct = default)
    {
        var request = new LiveTradingRuntimeRequest
        {
            Instruments = input.Instruments,
            Engine = input.Engine,
            StrategyName = input.StrategyName,
            InitialCash = input.InitialCash,
            FastPeriod = input.FastPeriod,
            StrategyParameters = input.StrategyParameters,
            SlowPeriod = input.SlowPeriod,
            Resolution = input.Resolution,
            MarketDataConnect = input.MarketDataConnect,
            VerboseLog = input.VerboseLog,
            EnableRealOrders = input.EnableRealOrders,
            MaxOrderVolume = input.MaxOrderVolume,
        };
        return _runtime.StartAsync(request, ct);
    }

    public Task StopAsync(CancellationToken ct = default) => _runtime.StopAsync(ct);
}

/// <summary>
/// <see cref="IStrategySignalApplicationService"/> 实现：编排「取 bar → StrategyRegistry 建策略 → 逐根 EvaluateSignal → 摘要」。
/// <b>不起 Lean 引擎</b>——bar 经 <see cref="IHistoricalBarSource"/>（Lean 中立端口）取，策略是纯 Domain 的 <see cref="IStrategy"/>。
/// 故与 <see cref="BacktestApplicationService"/>（经 IBacktestRuntime 起 Lean）平行但轻量得多。镜像 <c>run_rule</c>：循环在进程内跑，LLM 只收摘要。
/// </summary>
public sealed class StrategySignalApplicationService : IStrategySignalApplicationService
{
    private readonly IHistoricalBarSource _barSource;

    public StrategySignalApplicationService(IHistoricalBarSource barSource)
    {
        _barSource = barSource;
    }

    /// <inheritdoc />
    public async Task<StrategySignalResult> ProbeAsync(StrategySignalProbeInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.StrategyName))
            return Failed(input, "策略名不能为空");
        if (string.IsNullOrWhiteSpace(input.Symbol))
            return Failed(input, "标的代码不能为空");
        if (input.BarCount <= 0)
            return Failed(input, "取数数量须大于 0");

        var (bars, error) = await _barSource.GetBarsAsync(input.Symbol, input.Resolution, input.BarCount, ct);
        if (bars is null || bars.Count == 0)
            return Failed(input, error ?? "无历史 K 线数据");

        var parameters = new StrategyParameters(input.StrategyParameters);
        var strategy = StrategyRegistry.Create(input.StrategyName, parameters);
        if (strategy is null)
            return Failed(input, $"策略「{input.StrategyName}」未注册");

        // 逐根求值，收集信号序列 + 翻转点
        var signals = new List<SignalDirection>(bars.Count);
        var changes = new List<SignalChange>();
        SignalDirection prev = SignalDirection.Hold;
        bool first = true;
        int longCount = 0, flatCount = 0, holdCount = 0;

        foreach (var bar in bars)
        {
            var sig = strategy.EvaluateSignal(bar);
            signals.Add(sig);
            if (first || sig != prev)
            {
                changes.Add(new SignalChange(bar.Time, sig));
                prev = sig;
                first = false;
            }
            switch (sig)
            {
                case SignalDirection.Long: longCount++; break;
                case SignalDirection.Flat: flatCount++; break;
                default: holdCount++; break;
            }
        }

        return new StrategySignalResult
        {
            IsSuccess = true,
            StrategyName = input.StrategyName,
            Symbol = input.Symbol,
            WarmUpBars = strategy.WarmUpBars,
            EvaluatedBars = bars.Count,
            LastSignal = signals[^1],
            LastBarDate = bars[^1].Time,
            LongCount = longCount,
            FlatCount = flatCount,
            HoldCount = holdCount,
            Changes = changes,
        };
    }

    private static StrategySignalResult Failed(StrategySignalProbeInput input, string error) => new()
    {
        IsSuccess = false,
        Error = error,
        StrategyName = input.StrategyName,
        Symbol = input.Symbol,
    };
}
