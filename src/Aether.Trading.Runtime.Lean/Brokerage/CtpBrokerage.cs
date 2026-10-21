using Ctp.Net;
using QuantConnect;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// CTP 期货 Lean 实盘 brokerage：单类同时实现 <see cref="Brokerage"/>（交易）+ <see cref="IDataQueueHandler"/>（行情），
/// 镜像 LeanSharedFuturesBrokerage 的 SharedFuturesBrokerage 架构（一个类承担两个关注点，按 partial 文件拆分）。
/// 交易+行情桥接同一个 <see cref="ICtpSession"/>（与 CtpView 共享的 Singleton），不另开 CTP 连接。<para/>
/// 工厂 <see cref="CtpBrokerageFactory"/> 创建本实例后，<c>Composer.Instance.AddPart((IDataQueueHandler)brokerage)</c>
/// 让 Lean 的 DataQueueHandlerManager 按 typeName 解析时拿到<strong>同一实例</strong>（path 1 命中 _exportedValues，
/// 不走 MEF 无参构造）——故交易与行情共用一个 CtpBrokerage，事件只订阅一次。
/// </summary>
public partial class CtpBrokerage : Brokerage, IDataQueueHandler
{
    private readonly ICtpSession _session;
    private readonly IAlgorithm? _algorithm;
    private IDataAggregator? _aggregator;
    private EventBasedDataQueueHandlerSubscriptionManager? _subscriptionManager;
    private LiveNodePacket? _job;
    private bool _eventsWired;
    private bool _ctpMdSubscribed;

    /// <summary>
    /// 会话最新连接状态（<see cref="OnConnectionStateChanged"/> 维护）。前置每 F0 推送序号 240 条会
    /// <b>主动断开换会话</b>（SDK 自动重连）——重连窗口内报单必然失败，而 LEAN 把 Brokerage Error
    /// 视为致命错误直接停引擎。报单前查本字段决定是否等重连（2026-10-21 双 bot 实测 5 分钟即死）。
    /// </summary>
    private volatile CtpConnectionStatus _status = CtpConnectionStatus.Connected;

    public CtpBrokerage(IAlgorithm? algorithm, ICtpSession session) : base("CtpFutures")
    {
        _algorithm = algorithm;
        _session = session;
    }

    public override bool AccountInstantlyUpdated => true;
    // P1 统一用 USD 计价（账户/SPDB/资金），彻底绕开 Lean 的 USD↔CNY 换汇路径（CTP 无 FX 数据会抛）。
    // CTP 真实账户是 CNY——这里把 CNY 金额按 USD 标签上报，P1 只验证 tick→bar→信号链路，币种标签不影响信号。
    // P2 接交易时再正经处理 CNY（提供 USDCNY 换汇或自定义 ICurrencyConverter）。
    public override string AccountBaseCurrency => "USD";
    public override bool IsConnected => _session.IsConnected;

    /// <summary>装配聚合器 + 订阅管理器（MEG/Composer 实例化路径不走 ctor，故延迟到 SetJob/首次 Subscribe）。</summary>
    private void EnsureInitialized()
    {
        if (_subscriptionManager != null) return;
        _aggregator ??= Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
            Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"),
            forceTypeNameOnExisting: false);
        _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager(t => t.ToString());
        _subscriptionManager.SubscribeImpl += (symbols, tickType) => { EnsureCtpMdSubscribed(); return true; };
        _subscriptionManager.UnsubscribeImpl += (_, _) => true;
    }

    public override void Connect()
    {
        if (!_session.IsConnected)
            throw new InvalidOperationException("CTP 未登录——请先在 CTP 页登录后再启动实盘引擎");
        WireSessionEvents();
        // 持仓缓存不在此预热——BrokerageSetupHandler 启动时必调 GetAccountHoldings（先于任何 PlaceOrder），
        // 其中 QueryPositionAsync + Replace 即完成预热；PlaceOrder 侧另有 stale>2s 节流兜底。
        CtpLeanGateway.Log("[Brokerage] CTP 会话已连接，实盘引擎就绪");
        // 数据管线版本标记（排障用）：确认日志里有这行 = 跑的是修复后的二进制（三层自举死锁修复都在内）。
        // 看不到这行 = 旧 .app/dll（dotnet build 不更新已打包的 .app——复制旧包跑的是旧代码）。
        CtpLeanGateway.Log("[Brokerage] 数据管线 2026-09-28.v7（+ 限价取价 + 市价IOC + 反转拆腿）");
    }

    public override void Disconnect()
    {
        StopWatchdog();
        UnwireSessionEvents();
        // 不调 _session.Logout()——CTP 连接留给 CtpView 继续用
    }

    /// <summary>订阅 ICtpSession 事件（OrderReceived/TradeReceived/TickReceived/ConnectionStateChanged），仅订阅一次。</summary>
    private void WireSessionEvents()
    {
        if (_eventsWired) return;
        _session.OrderReceived += OnOrderReceived;
        _session.TradeReceived += OnTradeReceived;
        _session.TickReceived += OnTickReceived;
        _session.ConnectionStateChanged += OnConnectionStateChanged;
        _eventsWired = true;
    }

    private void UnwireSessionEvents()
    {
        if (!_eventsWired) return;
        _session.OrderReceived -= OnOrderReceived;
        _session.TradeReceived -= OnTradeReceived;
        _session.TickReceived -= OnTickReceived;
        _session.ConnectionStateChanged -= OnConnectionStateChanged;
        _eventsWired = false;
    }

    private void OnConnectionStateChanged(object? sender, CtpConnectionStatus e)
    {
        _status = e;
        var msg = e switch
        {
            CtpConnectionStatus.Disconnected => BrokerageMessageType.Warning,
            CtpConnectionStatus.Reconnecting => BrokerageMessageType.Information,
            CtpConnectionStatus.Closed => BrokerageMessageType.Warning,
            _ => BrokerageMessageType.Information,
        };
        OnMessage(new BrokerageMessageEvent(msg, e.ToString(), $"CTP 连接状态: {e}"));
        CtpLeanGateway.Log($"[Brokerage] CTP 连接状态: {e}");
    }

    // —— 交易方法（PlaceOrder/UpdateOrder/CancelOrder/GetOpenOrders/GetAccountHoldings）实现在 CtpBrokerage.Trading.cs ——
    // —— 报单/成交回报处理（OnOrderReceived/OnTradeReceived）同在 Trading partial ——

    /// <summary>
    /// 重连窗口等待（仅盘中断线 <see cref="CtpConnectionStatus.Reconnecting"/> 才等）：前置按 F0 序号上限
    /// 主动换会话，SDK 自动重连 + 重登录通常 1-3s 完成。等不到返 false（调用方按失败处理，但<b>不得</b>
    /// 发 Brokerage Error——那会让 LEAN 停引擎）。闭市 <see cref="CtpConnectionStatus.Closed"/> /
    /// <see cref="CtpConnectionStatus.Disconnected"/> 立即返 false，不白等。
    /// </summary>
    private bool WaitForReconnect(TimeSpan timeout)
    {
        if (_status == CtpConnectionStatus.Connected) return true;
        if (_status is CtpConnectionStatus.Closed or CtpConnectionStatus.Disconnected) return false;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_status == CtpConnectionStatus.Connected) return true;
            if (_status is CtpConnectionStatus.Closed or CtpConnectionStatus.Disconnected) return false;
            Thread.Sleep(100);
        }
        return _status == CtpConnectionStatus.Connected;
    }

    /// <summary>阻塞等待带超时——防半死会话把 sync 契约调用挂死（2026-10-20 实验室实测：重连窗口内
    /// QueryPositionAsync 永不返回，无超时的 GetResult() 无限阻塞 ⇒ 交易/引擎线程冻结、市场假死而引擎
    /// 仍显示 Running）。超时抛 TimeoutException；任务自身故障抛原始异常（不裹 Aggregate）。</summary>
    private static T BlockingCall<T>(Func<Task<T>> call, TimeSpan timeout, string what)
    {
        var task = Task.Run(call);
        if (!task.Wait(timeout))
            throw new TimeoutException($"{what} {timeout.TotalSeconds:0}s 未返回（会话半死？重连窗口内调用挂起）");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>非泛型版（见泛型重载注释）。</summary>
    private static void BlockingCall(Func<Task> call, TimeSpan timeout, string what)
    {
        var task = Task.Run(call);
        if (!task.Wait(timeout))
            throw new TimeoutException($"{what} {timeout.TotalSeconds:0}s 未返回（会话半死？重连窗口内调用挂起）");
        task.GetAwaiter().GetResult();
    }

    public override List<CashAmount> GetCashBalance()
    {
        try
        {
            var acc = BlockingCall(() => _session.QueryAccountAsync(), TimeSpan.FromSeconds(8), "资金查询");
            // P1: CNY 金额按 USD 标签上报（见 AccountBaseCurrency 注释），避免换汇路径缺失
            return new List<CashAmount> { new((decimal)acc.Balance, "USD") };
        }
        catch (Exception ex)
        {
            CtpLeanGateway.Log($"[Brokerage] 查询资金失败: {ex.Message}");
            return new List<CashAmount>();
        }
    }

    public override IEnumerable<BaseData> GetHistory(QuantConnect.Data.HistoryRequest request)
        => throw new NotSupportedException("CTP 实盘不支持历史回填，历史数据请走 Backtest 的 IAnalysisManager");

    /// <summary>首次订阅时连 CTP 行情前置 + 订阅全部合约（CTP 行情一次性订阅全量列表）。</summary>
    private void EnsureCtpMdSubscribed()
    {
        if (_ctpMdSubscribed) return;
        var connect = CtpLeanGateway.MarketDataConnect
            ?? throw new InvalidOperationException("行情前置连接参数未注入（CtpLeanGateway.MarketDataConnect 为空）");
        var instruments = CtpLeanGateway.Instruments;
        BlockingCall(() => _session.SubscribeMarketDataAsync(connect, instruments), TimeSpan.FromSeconds(15), "行情订阅");
        _ctpMdSubscribed = true;
        CtpLeanGateway.Log($"[Brokerage] 已订阅 CTP 行情: {string.Join(",", instruments)}");
    }
}
