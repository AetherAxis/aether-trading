using Ctp.Net;
using Aether.Trading.Runtime.Lean;
using QuantConnect;
using QuantConnect.Orders;
using Xunit;

namespace Aether.Trading.Tests;

/// <summary>
/// P2 brokerage 级单测（FakeCtpSession 零真实 CTP——镜像 tests/Acme.Ctp.AI.Tests 的 Fake 模式）：
/// 开关分流（关=P1 行为）、报单+Submitted、Trade/Order 回报→OrderEvent 翻译、SHFE 拆腿、拒单。
/// CtpBrokerage(algorithm:null, session:fake) 可测——交易路径不依赖 aggregator/订阅管理器。
/// </summary>
public class CtpBrokerageTradingTests : IDisposable
{
    private readonly CtpLiveTradingOptions _savedOptions = CtpLeanGateway.TradingOptions;
    private readonly FakeCtpSession _session = new();
    private readonly CtpBrokerage _brokerage;
    private readonly List<OrderEvent> _events = new();
    private readonly List<string> _logs = new();

    public CtpBrokerageTradingTests()
    {
        CtpLeanGateway.LogSink = m => _logs.Add(m);
        // Lean Symbol.Create 会把 ticker 大写化（AG2610）——brokerage 经订阅合约列表解析回 CTP 原始大小写
        CtpLeanGateway.Instruments = new[] { "ag2610" };
        _brokerage = new CtpBrokerage(null, _session);
        _brokerage.OrdersStatusChanged += (_, events) => _events.AddRange(events);
        _brokerage.Connect();   // WireSessionEvents（fake IsConnected=true）
    }

    public void Dispose()
    {
        CtpLeanGateway.TradingOptions = _savedOptions;   // 静态选项恢复，防测试间泄漏
        CtpLeanGateway.Instruments = Array.Empty<string>();
        CtpLeanGateway.LogSink = null;
    }

    private static Order NewOrder(decimal quantity) => new MarketOrder(
        Symbol.Create("ag2610", SecurityType.Base, Market.USA), quantity, DateTime.UtcNow);

    private static CtpOrder OrderReport(string orderRef, byte status, int volumeTraded = 0, string exchange = "SHFE") => new()
    {
        InstrumentId = "ag2610", ExchangeId = exchange, OrderRef = orderRef,
        Direction = CtpDirection.Buy, OffsetFlag = CtpOffsetFlag.Open,
        VolumeTotalOriginal = 2, VolumeTraded = volumeTraded, OrderStatusRaw = status,
    };

    private static CtpTrade TradeReport(string orderRef, string tradeId, int volume, double price = 7002) => new()
    {
        InstrumentId = "ag2610", OrderRef = orderRef, TradeId = tradeId,
        Direction = CtpDirection.Buy, OffsetFlag = CtpOffsetFlag.Open,
        Volume = volume, Price = price,
    };

    [Fact]
    public void Disconnected_session_logs_not_ready_but_still_submits_the_order()
    {
        // 前置每 F0 推送序号 240 条会主动换会话（SDK 自动重连），重连窗口内报单必然失败；
        // 契约是「只记日志 + 不发 Brokerage Error」（LEAN 视 Error 为致命会停引擎），且本单照常提交
        // 让交易所侧的拒单回报成为唯一一次失败（不吞单）。断线/闭市立即判不就绪——不白等 5s 超时。
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        _session.RaiseConnectionState(CtpConnectionStatus.Disconnected);

        var ok = _brokerage.PlaceOrder(NewOrder(2m));

        Assert.True(ok, string.Join("\n", _logs));
        Assert.Contains(_logs, l => l.Contains("报单前连接未就绪（Disconnected）"));
        Assert.Single(_session.InsertCalls);
    }

    [Fact]
    public void Toggle_off_keeps_P1_behavior()
    {
        CtpLeanGateway.TradingOptions = CtpLiveTradingOptions.Default;   // EnableRealOrders=false

        var ok = _brokerage.PlaceOrder(NewOrder(2m));

        Assert.False(ok);                          // P1 拦截
        Assert.Empty(_session.InsertCalls);        // 零报单
        Assert.Empty(_events);                     // 无事件（Invalid 由 transaction handler 发）
    }

    [Fact]
    public void Toggle_on_places_opposite_side_limit_and_emits_submitted()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };

        var order = NewOrder(2m);
        var ok = _brokerage.PlaceOrder(order);

        Assert.True(ok, string.Join("\n", _logs));
        var req = Assert.Single(_session.InsertCalls);
        Assert.Equal(CtpDirection.Buy, req.Direction);
        Assert.Equal(CtpOffsetFlag.Open, req.OffsetFlag);
        Assert.Equal(CtpOrderPriceType.Limit, req.PriceType);
        Assert.Equal(FakeCtpSession.AskPrice + 0.02, req.LimitPrice, 5);   // 买用 Ask + 跨越余量（价格槽量化防护）
        Assert.Equal(2, req.Volume);
        Assert.Equal(CtpTimeCondition.IOC, req.TimeCondition);   // 市价模拟 = 吃单语义：残量立即作废不挂簿
        Assert.Contains(_session.NextOrderRef, order.BrokerId);
        Assert.Equal(OrderStatus.Submitted, Assert.Single(_events).Status);
    }

    [Fact]
    public void Limit_order_maps_typed_limit_price()
    {
        // 2026-09-28 实测第五层修复：Lean 限价单价格在 LimitOrder.LimitPrice（Order.Price 恒 0）——
        // 旧代码传 order.Price 给映射器 ⇒ 限价恒 0 ⇒ 拒「限价单价格无效」（做市策略双边全被拒）。
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };

        var order = new LimitOrder(Symbol.Create("ag2610", SecurityType.Base, Market.USA), 2m, 7001m, DateTime.UtcNow);
        var ok = _brokerage.PlaceOrder(order);

        Assert.True(ok, string.Join("\n", _logs));
        var req = Assert.Single(_session.InsertCalls);
        Assert.Equal(CtpOrderPriceType.Limit, req.PriceType);
        Assert.Equal(7001, req.LimitPrice);   // 类型化限价——不是 0
        Assert.Equal(CtpTimeCondition.GFD, req.TimeCondition);   // 真限价单照常挂簿
    }

    [Fact]
    public void Fuse_caps_open_volume()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 1 };

        var ok = _brokerage.PlaceOrder(NewOrder(10m));

        Assert.True(ok);
        Assert.Equal(1, Assert.Single(_session.InsertCalls).Volume);   // 10 手封顶到 1
    }

    [Fact]
    public void Trade_reports_drive_fill_events_to_filled()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        var order = NewOrder(2m);
        _brokerage.PlaceOrder(order);
        _events.Clear();

        _session.RaiseTrade(TradeReport(_session.NextOrderRef, "t1", 1, price: 7001.5));
        _session.RaiseTrade(TradeReport(_session.NextOrderRef, "t2", 1, price: 7002.5));

        Assert.Equal(3, _events.Count);
        Assert.Equal(OrderStatus.PartiallyFilled, _events[0].Status);
        Assert.Equal(7001.5m, _events[0].FillPrice);
        Assert.Equal(1, _events[0].FillQuantity);        // 有符号增量（买为正）
        Assert.Equal(OrderStatus.PartiallyFilled, _events[1].Status);
        Assert.Equal(OrderStatus.Filled, _events[2].Status);   // 累计满量 → 终态 Filled
    }

    [Fact]
    public void Filled_order_report_backfills_missing_trades()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        var order = NewOrder(2m);
        _brokerage.PlaceOrder(order);
        _events.Clear();

        // 全成回报先到、Trade 回报乱序未到 → 补齐缺口
        _session.RaiseOrder(OrderReport(_session.NextOrderRef, (byte)'0', volumeTraded: 2));

        var fill = Assert.Single(_events);
        Assert.Equal(OrderStatus.Filled, fill.Status);
        Assert.Equal(2, fill.FillQuantity);              // 缺口 2 手按限价补齐
    }

    [Fact]
    public void Canceled_order_report_emits_canceled()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        _brokerage.PlaceOrder(NewOrder(2m));
        _events.Clear();

        _session.RaiseOrder(OrderReport(_session.NextOrderRef, (byte)'5'));   // 撤单

        var e = Assert.Single(_events);
        Assert.Equal(OrderStatus.Canceled, e.Status);
    }

    [Fact]
    public void Rejected_order_report_emits_invalid()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        _brokerage.PlaceOrder(NewOrder(2m));
        _events.Clear();

        _session.RaiseOrder(OrderReport(_session.NextOrderRef, (byte)'4'));   // 未成交不排队（被拒）

        var e = Assert.Single(_events);
        Assert.Equal(OrderStatus.Invalid, e.Status);
    }

    [Fact]
    public void Unknown_order_ref_reports_do_not_emit_events()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        _brokerage.PlaceOrder(NewOrder(2m));
        _events.Clear();

        // 期货页手动单（引擎外 OrderRef）→ 仅记录，无事件
        _session.RaiseOrder(OrderReport("999999999999", (byte)'0', volumeTraded: 5));
        _session.RaiseTrade(TradeReport("999999999999", "tx", 5));

        Assert.Empty(_events);
    }

    [Fact]
    public void Insert_failure_returns_false()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        _session.InsertException = new InvalidOperationException("CTP 未登录");

        var ok = _brokerage.PlaceOrder(NewOrder(2m));

        Assert.False(ok);
    }

    [Fact]
    public void No_tick_rejects_order()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        _session.Ticks.Clear();   // 无行情 → 对手价无法确定

        var ok = _brokerage.PlaceOrder(NewOrder(2m));

        Assert.False(ok);
        Assert.Empty(_session.InsertCalls);
    }

    [Fact]
    public void Shfe_close_splits_into_two_legs_and_fills_to_one_terminal()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        // SHFE 多 5（昨 3 今 2），卖 4 → CloseYesterday(3) + CloseToday(1)
        _session.Positions = new List<CtpPosition>
        {
            new() { InstrumentId = "ag2610", ExchangeId = "SHFE", PosiDirection = CtpPosiDirection.Long,
                    TotalPosition = 5, TodayPosition = 2, YdPosition = 3 },
        };
        _brokerage.PlaceOrder(NewOrder(-4m));   // 触发缓存刷新（stale）

        Assert.Equal(2, _session.InsertCalls.Count);
        Assert.Equal(CtpOffsetFlag.CloseYesterday, _session.InsertCalls[0].OffsetFlag);
        Assert.Equal(3, _session.InsertCalls[0].Volume);
        Assert.Equal(CtpOffsetFlag.CloseToday, _session.InsertCalls[1].OffsetFlag);
        Assert.Equal(1, _session.InsertCalls[1].Volume);

        // 两腿各自全成 → 合计 4 手 → 单个终态 Filled
        _events.Clear();
        _session.RaiseTrade(TradeReport(_session.OrderRefs[0], "t1", 3));
        _session.RaiseTrade(TradeReport(_session.OrderRefs[1], "t2", 1));
        _session.RaiseOrder(OrderReport(_session.OrderRefs[0], (byte)'0', volumeTraded: 3));
        _session.RaiseOrder(OrderReport(_session.OrderRefs[1], (byte)'0', volumeTraded: 1));

        Assert.Equal(OrderStatus.Filled, _events[^1].Status);
        Assert.Single(_events, e => e.Status == OrderStatus.Filled);   // 终态只发一次
    }

    [Fact]
    public void Partial_fill_leg_plus_cancelled_leg_emits_canceled_no_phantom_backfill()
    {
        // 2026-10-03 双 bot 实测回归：SellClose 1（成交）+ SellOpen 4（撤单 0 成）的两腿单——
        // 旧版聚合见「任一腿成交 + Filled < 预期」即把撤腿残量当缺口回补幻影成交
        // （净持仓凭空 -4、Lean Portfolio 与核心账本漂移）。正确语义 = 部分成交后撤：
        // fill 保留（Trade 已计 1）、终态 Canceled、无补齐。
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };
        // 多 1 → 卖 5 = 反转：CloseYesterday(1) + Open(4)（保险丝 10 不裁）
        _session.Positions = new List<CtpPosition>
        {
            new() { InstrumentId = "ag2610", ExchangeId = "SHFE", PosiDirection = CtpPosiDirection.Long,
                    TotalPosition = 1, TodayPosition = 0, YdPosition = 1 },
        };
        _brokerage.PlaceOrder(NewOrder(-5m));
        Assert.Equal(2, _session.InsertCalls.Count);
        _events.Clear();

        // 平仓腿成交 1（Trade + '0'）；开仓腿撤单（'5'，0 成）
        _session.RaiseTrade(TradeReport(_session.OrderRefs[0], "t1", 1, price: 7002));
        _session.RaiseOrder(OrderReport(_session.OrderRefs[0], (byte)'0', volumeTraded: 1));
        _session.RaiseOrder(OrderReport(_session.OrderRefs[1], (byte)'5'));

        // 终态 = Canceled（部分成交后撤），无 Filled、无按限价补齐的幻影腿
        Assert.Equal(OrderStatus.PartiallyFilled, _events[0].Status);
        Assert.Equal(OrderStatus.Canceled, _events[^1].Status);
        Assert.DoesNotContain(_events, e => e.Status == OrderStatus.Filled);
        Assert.Equal(1, _events.Sum(e => Math.Abs(e.FillQuantity)));   // 真实成交恰 1 手（撤腿不计）
    }

    [Fact]
    public void Cancel_unknown_order_returns_false()
    {
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions { EnableRealOrders = true, MaxOrderVolume = 10 };

        var order = NewOrder(2m);
        typeof(Order).GetProperty("Id")!.SetValue(order, 42);

        Assert.False(_brokerage.CancelOrder(order));   // 不在跟踪表
    }

    [Fact]
    public void GetAccountHoldings_maps_positions_and_skips_unsubscribed()
    {
        // algorithm=null → symbolLookup 恒 null → 全部跳过（映射逻辑细节由 CtpAccountMappersTests 覆盖）
        _session.Positions = new List<CtpPosition>
        {
            new() { InstrumentId = "ag2610", ExchangeId = "SHFE", PosiDirection = CtpPosiDirection.Long,
                    TotalPosition = 3, TodayPosition = 1, YdPosition = 2 },
        };

        var holdings = _brokerage.GetAccountHoldings();

        Assert.Empty(holdings);   // 无算法 symbol 表 → 未订阅合约全跳过（Base 类型 setup 保护）
    }

    // ═══ FakeCtpSession（记录调用 + 可配置异常 + 可触发回报事件）═══

    public sealed class FakeCtpSession : ICtpSession
    {
        public const double BidPrice = 7000, AskPrice = 7002;

        public List<CtpOrderRequest> InsertCalls { get; } = new();
        public List<string> OrderRefs { get; } = new();
        public List<CtpOrderRef> CancelCalls { get; } = new();
        public Exception? InsertException { get; set; }
        public IReadOnlyList<CtpPosition> Positions { get; set; } = Array.Empty<CtpPosition>();
        public Dictionary<string, CtpTick> Ticks { get; } = new()
        {
            ["ag2610"] = new CtpTick { InstrumentId = "ag2610", BidPrice = BidPrice, AskPrice = AskPrice, LastPrice = 7001, PreSettlement = 6990 },
        };

        private int _refCounter;

        public string NextOrderRef => OrderRefs.Count > 0 ? OrderRefs[^1] : "";

        public bool IsConnected => true;
        public bool IsMdSubscribed => false;
        public CtpLoginResult? LoginResult { get; } = new("20260924", "09:00:00", FrontId: 1, SessionId: 2, MaxOrderRef: "0");
        public string? BrokerId => "9999";
        public string? UserId => "user01";

        public event EventHandler<CtpOrder>? OrderReceived;
        public event EventHandler<CtpTrade>? TradeReceived;
        public event EventHandler<CtpTick>? TickReceived;
        public event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;

        public void RaiseOrder(CtpOrder order) => OrderReceived?.Invoke(this, order);
        public void RaiseTrade(CtpTrade trade) => TradeReceived?.Invoke(this, trade);
        public void RaiseTick(CtpTick tick) => TickReceived?.Invoke(this, tick);
        public void RaiseConnectionState(CtpConnectionStatus status) => ConnectionStateChanged?.Invoke(this, status);

        public Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default)
            => Task.FromResult(LoginResult!);

        public void Logout() { }

        public Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default)
            => Task.FromResult(new CtpAccount { AccountId = "9999", Balance = 100_000 });

        public Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default)
            => Task.FromResult(instrumentId == null ? Positions : Positions.Where(p => p.InstrumentId == instrumentId).ToList());

        public Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CtpOrder>>(Array.Empty<CtpOrder>());
        public Task<IReadOnlyList<CtpInstrument>> QueryInstrumentsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CtpInstrument>>(Array.Empty<CtpInstrument>());

        public Task<CtpOrderInsertResult> OrderInsertAsync(CtpOrderRequest request, CancellationToken ct = default)
        {
            if (InsertException != null) throw InsertException;
            InsertCalls.Add(request);
            var orderRef = (++_refCounter).ToString("D12");
            OrderRefs.Add(orderRef);
            return Task.FromResult(new CtpOrderInsertResult(orderRef, _refCounter));
        }

        public Task OrderActionAsync(CtpOrderRef orderRef, CancellationToken ct = default)
        {
            CancelCalls.Add(orderRef);
            return Task.CompletedTask;
        }

        /// <summary>订阅钩子（测试注入：模拟服务端在订阅往返期间同步推送订阅快照 tick——竞态回归用）。</summary>
        public Func<CtpMarketDataConnect, IReadOnlyList<string>, Task>? OnSubscribe { get; set; }

        public Task SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct = default)
            => OnSubscribe?.Invoke(connect, symbols) ?? Task.CompletedTask;

        public void UnsubscribeMarketData() { }

        public CtpTick? GetLatestTick(string instrumentId)
            => Ticks.TryGetValue(instrumentId, out var t) ? t : null;

        public IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks() => Ticks;
    }
}
