namespace Aether.Trading.Runtime.Domain;

/// <summary>
/// CTP 行情前置连接参数（Lean/CTP-中性，纯字符串）。Runtime.Lean 映射到 <c>Ctp.Net.CtpMarketDataConnect</c>。
/// 实盘行情前置是 CTP 独立连接（不依赖交易登录），故显式传入。
/// </summary>
public sealed class MarketDataConnectInput
{
    public string? MdFrontAddress { get; set; }   // 行情前置 tcp://ip:port
    public string? BrokerId { get; set; }
    public string? UserId { get; set; }
    public string? Password { get; set; }
}

/// <summary>实盘策略运行状态。引擎生命周期镜像于此，UI 据此显示。</summary>
public enum LiveTradingRuntimeStatus
{
    Idle, Starting, Running, Stopping, Stopped, Error,
}

/// <summary>实盘策略引擎（2026-09-28 起 LeanAlgorithm 可实盘——bots 即策略路线；此前仅 IStrategy 壳）。</summary>
public enum LiveStrategyEngine
{
    /// <summary>IStrategy 壳（TradingHostAlgorithm 包装 StrategyRegistry 策略——Lean 中立）。</summary>
    IStrategy = 0,

    /// <summary>LeanAlgorithm 直写（LeanAlgorithmRegistry 编译注册的 QCAlgorithm 子类——同回测第三条路径）。</summary>
    LeanAlgorithm = 1,
}

/// <summary>
/// 实盘运行请求（Lean/CTP-中性）。合约以代码给出（如 ag2610），CTP 自行解析。
/// 交易凭据由 DI 注入的 <c>ICtpSession</c>（CtpView 登录后共享）提供，不在请求里。
/// </summary>
public sealed class LiveTradingRuntimeRequest
{
    /// <summary>订阅合约代码列表（如 ag2610 / rb2510）。</summary>
    public IReadOnlyList<string> Instruments { get; set; } = Array.Empty<string>();

    /// <summary>策略引擎（默认 IStrategy 壳）。</summary>
    public LiveStrategyEngine Engine { get; set; } = LiveStrategyEngine.IStrategy;

    /// <summary>策略名（按 Engine 分别指 StrategyRegistry / LeanAlgorithmRegistry 注册的 key）。</summary>
    public string StrategyName { get; set; } = "SmaCross";

    /// <summary>初始资金（CNY）。实盘账户真实资金由 brokerage.GetCashBalance 查询覆盖。</summary>
    public decimal InitialCash { get; set; } = 1_000_000m;

    public int FastPeriod { get; set; } = 10; /// 保留兼容（StrategyParameters 优先）
    public Dictionary<string, string>? StrategyParameters { get; set; }
    public int SlowPeriod { get; set; } = 30;

    /// <summary>K 线分辨率，默认 Minute（tick 聚合）。</summary>
    public BarResolution Resolution { get; set; } = BarResolution.Minute;

    /// <summary>CTP 行情前置连接参数（独立于交易登录）。</summary>
    public MarketDataConnectInput? MarketDataConnect { get; set; }

    /// <summary>详细日志（true=输出 OnData/Bar/Hold 等调试日志；false=只输出交易信号）。默认 false。</summary>
    public bool VerboseLog { get; set; }

    /// <summary>
    /// P2 真实下单开关：false=仅信号日志（P1 行为，brokerage 拦截全部下单）；true=经 ICtpSession 真实报单。
    /// 默认 false——真实下单不可逆，须用户显式打开。
    /// </summary>
    public bool EnableRealOrders { get; set; }

    /// <summary>单笔开仓手数上限（保险丝，默认 1）。平仓不受限——否则仓位平不干净。</summary>
    public int MaxOrderVolume { get; set; } = 1;
}

/// <summary>
/// 实盘运行时契约：进程内启停引擎实时引擎，引擎经 CtpBrokerage 桥接 <c>ICtpSession</c>（交易+行情一体）。
/// 实现位于 Aether.Trading.Runtime.Lean（LeanLiveTradingService : ILiveTradingRuntime）——Lean 源码只在该叶子内引用。
/// 调用方不依赖 Lean 类型。前置：ICtpSession 已登录。
/// </summary>
public interface ILiveTradingRuntime
{
    LiveTradingRuntimeStatus Status { get; }
    bool IsRunning { get; }

    event EventHandler<LiveTradingRuntimeStatus>? StatusChanged;
    /// <summary>日志/信号输出（引擎日志 + 策略信号 + CTP 行情回报），一行一条。</summary>
    event EventHandler<string>? LogReceived;

    /// <summary>启动实盘引擎。前置：ICtpSession 已登录；否则失败。</summary>
    Task<(bool Success, string? Error)> StartAsync(LiveTradingRuntimeRequest request, CancellationToken ct = default);

    /// <summary>停止实盘引擎（触发算法 Quit，等待后台线程结束）。</summary>
    Task StopAsync(CancellationToken ct = default);
}
