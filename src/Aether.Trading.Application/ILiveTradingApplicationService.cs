using Aether.Trading.Runtime.Domain;

namespace Aether.Trading.Application;

/// <summary>
/// 实盘应用层输入（面向 UI/工具）。镜像 <see cref="LiveTradingRuntimeRequest"/> 的纯字段，
/// 经 <see cref="ILiveTradingApplicationService"/> 映射成 Runtime 请求。不含 Lean/CTP SDK 类型。
/// </summary>
public sealed class LiveTradingRunInput
{
    public IReadOnlyList<string> Instruments { get; set; } = Array.Empty<string>();

    /// <summary>策略引擎（默认 IStrategy 壳；LeanAlgorithm = labs 笔记直跑——bots 即策略）。</summary>
    public LiveStrategyEngine Engine { get; set; } = LiveStrategyEngine.IStrategy;

    public string StrategyName { get; set; } = "SmaCross";
    public decimal InitialCash { get; set; } = 1_000_000m;
    public int FastPeriod { get; set; } = 10;
    public int SlowPeriod { get; set; } = 30;
    /// <summary>策略参数（键值对，透传给策略 Initialize）。优先于 FastPeriod/SlowPeriod。</summary>
    public Dictionary<string, string>? StrategyParameters { get; set; }
    public BarResolution Resolution { get; set; } = BarResolution.Minute;
    public MarketDataConnectInput? MarketDataConnect { get; set; }

    /// <summary>详细日志（true=输出 OnData/Bar/Hold 等调试日志；false=只输出交易信号）。默认 false 减少开销。</summary>
    public bool VerboseLog { get; set; }

    /// <summary>P2 真实下单开关：false=仅信号日志（P1 行为）；true=经 CTP 真实报单。默认 false。</summary>
    public bool EnableRealOrders { get; set; }

    /// <summary>单笔开仓手数上限（保险丝，默认 1）。平仓不受限。</summary>
    public int MaxOrderVolume { get; set; } = 1;
}

/// <summary>
/// 实盘应用编排服务契约：薄封装 Runtime 的 <see cref="ILiveTradingRuntime"/>，
/// 代理状态/日志事件给 UI。前置：CTP 已登录（ICtpSession 已连接）。
/// </summary>
public interface ILiveTradingApplicationService
{
    LiveTradingRuntimeStatus Status { get; }
    bool IsRunning { get; }
    event EventHandler<LiveTradingRuntimeStatus>? StatusChanged;
    event EventHandler<string>? LogReceived;

    Task<(bool Success, string? Error)> StartAsync(LiveTradingRunInput input, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
