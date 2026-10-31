using Ctp.Net;
using QuantConnect;
using QuantConnect.Brokerages;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// <see cref="CtpBrokerage"/> 的交易 partial（P2 真实下单）。开关关 = P1 拦截行为不变；开关开 = 经
/// <c>ICtpSession.OrderInsertAsync</c> 真实报单 + 报单/成交回报翻译回 Lean OrderEvent。<para/>
/// 事件契约（Lean Brokerage）：PlaceOrder true 后由本类发 Submitted → PartiallyFilled/Filled/Canceled/Invalid；
/// PlaceOrder false 由 transaction handler 自己发 Invalid。Trade 回报驱动成交事件（真实成交价），
/// Order 回报驱动生命周期终态（vnpy 模式分工）；前置静默拒单（ICtpSession 不上浮 OnRspOrderInsert）
/// 由 <see cref="_watchdog"/> 10s 兜底转 Invalid。
/// </summary>
public partial class CtpBrokerage
{
    /// <summary>持仓缓存刷新节流（CTP 查询流控 ~1 次/秒，留余量）。</summary>
    private static readonly TimeSpan PositionRefreshInterval = TimeSpan.FromSeconds(2);
    /// <summary>下单后迟迟未收到首次报单回报 → 判无效（前置拒单不可见的兜底）。</summary>
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 与交易所对账的周期（open-issues #25）。2026-10-30 事故：交易所 17:46 重启把在挂委托全部作废，
    /// 直连 bot 的会话重连了却**没人告诉它委托没了**——MM 的档位图一直显示「在挂 多12/空13」，
    /// 73 分钟零报单、零成交，直到人工重启 bot。查报单是唯一能看见「交易所侧还有没有这张单」的通道，
    /// 而它只在查询时才看得见 ⇒ 必须周期查，不能只靠连接状态事件（经前前置时上游重启对客户端完全静默）。
    /// </summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(20);
    /// <summary>对账宽限：刚报出去的单可能还没落进交易所的查询结果，别把新单当幽灵。
    /// 非 readonly 是为了让单测把它压到 0（否则要真等 3s 才能驱动对账）。</summary>
    internal static TimeSpan ReconcileGrace = TimeSpan.FromSeconds(3);

    private readonly CtpOrderTracker _tracker = new();
    private readonly CtpPositionCache _positionCache = new();
    private System.Threading.Timer? _watchdog;
    private System.Threading.Timer? _reconciler;
    /// <summary>起点清场（open-issues #29）已跑过的轮数：连接后最多清几轮就收手，避免把「另一个进程刚下的单」反复清。</summary>
    private int _orphanSweeps;
    /// <summary>回填持仓用的兜底价缓存（每合约只等一次第一笔行情）。</summary>
    private readonly Dictionary<string, decimal> _fallbackPrice = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>起点清场的轮数上限（交易所查询一次最多回 200 行，#28——大堆孤儿要清几轮）。</summary>
    private const int OrphanSweepMaxPasses = 10;

    public override bool PlaceOrder(Order order)
    {
        var opts = CtpLeanGateway.TradingOptions;
        if (!opts.EnableRealOrders)
        {
            // P1 行为（默认，信号模式）：拦截下单，Lean transaction handler 会自行发 Invalid 事件
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "P1",
                "CTP 实盘「真实下单」未开启，仅行情/信号（信号日志见下）"));
            CtpLeanGateway.Log($"[Brokerage] 拦截下单(信号模式): {order.Symbol.Value} {order.Quantity} {order.Type}");
            return false;
        }

        try
        {
            RefreshPositionCacheIfStale();
            // 前置换会话窗口（F0 序号 240 主动断开 → SDK 自动重连）：先等重连完成再报，避免必然失败。
            // 等不到也不放弃本单——下面的失败分支只发 Warning（LEAN 视 Brokerage Error 为致命，2026-10-21 实测停引擎）。
            if (!WaitForReconnect(TimeSpan.FromSeconds(5)))
                CtpLeanGateway.Log($"[Brokerage] 报单前连接未就绪（{_status}）——按下单失败处理，引擎继续运行");
            // Lean Symbol.Create 会把 ticker 大写化（AG2610），CTP 接口区分大小写（ag2610）——
            // 报单/查行情/查持仓都须用 CTP 原始 instrumentId（同 _symbolByInstrument 的坑）。
            var instrumentId = ResolveCtpInstrument(order.Symbol.Value);
            var snapshot = _positionCache.Get(instrumentId);
            var tick = _session.GetLatestTick(instrumentId);
            // Lean 限价族的价格在类型化属性（LimitOrder.LimitPrice）——Order.Price 对限价单恒 0
            // （LimitOrder ctor 只设 LimitPrice 不设基类 Price；FillModel 同样读 order.LimitPrice）。
            // 传 order.Price 给限价单 = 传 0 = 映射器拒「限价单价格无效」（2026-09-28 实测第五层）。
            var orderPrice = order.Type == OrderType.Limit ? ((LimitOrder)order).LimitPrice : order.Price;
            // post-only（BTC 对标 2026-10-05）：Lean Tag 含 "post-only" ⇒ 只入簿不吃单（交易所插入时
            // 会交叉即整单拒 90018——做市策略防误吃、保证付 maker 费）。Tag 是 Lean 下单 API 现成用户通道。
            var postOnly = order.Tag?.Contains("post-only", StringComparison.OrdinalIgnoreCase) ?? false;
            var result = CtpOrderRequestMapper.Map(new CtpOrderRequestMapper.MapInput(
                order.Direction, order.Type, orderPrice, order.Quantity, instrumentId,
                snapshot, tick, _session.BrokerId ?? "", _session.UserId ?? "", opts.MaxOrderVolume, postOnly,
                RestingCloseVolume: RestingCloseVolume(instrumentId,
                    order.Direction == OrderDirection.Buy ? CtpDirection.Buy : CtpDirection.Sell)));
            if (result.Error != null)
            {
                CtpLeanGateway.Log($"[Brokerage] 拒单: {result.Error}（{order.Symbol.Value} {order.Quantity} {order.Type}）");
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "Rejected", result.Error));
                return false;
            }

            // 开仓保险丝封顶提示（平仓不封顶；封顶量已由映射器计算）
            var requested = (int)Math.Abs(order.Quantity);
            var actual = result.Requests.Sum(r => r.Volume);
            if (actual < requested)
                CtpLeanGateway.Log($"[Brokerage] 手数封顶: 策略请求 {requested} 手 → 实际下单 {actual} 手（开仓上限 {opts.MaxOrderVolume}，平仓不受限）");

            var login = _session.LoginResult;
            var sentRequests = new List<CtpOrderRequest>(result.Requests.Count);
            var refs = new List<string>(result.Requests.Count);
            foreach (var req in result.Requests)
            {
                try
                {
                    // 阻塞模式同 GetCashBalance（PlaceOrder 是 sync 契约）；SDK 立即返回，回报走事件。
                    // 8s 护栏：重连窗口内会话半死时 OrderInsertAsync 可能永不返回——无护栏会挂死
                    // transaction-handler 线程、冻结全部订单处理（2026-10-20 实验室实测）
                    var r = BlockingCall(() => _session.OrderInsertAsync(req), TimeSpan.FromSeconds(8), $"报单 {req.InstrumentId}");
                    refs.Add(r.OrderRef);
                    sentRequests.Add(req);
                    CtpLeanGateway.Log($"[Brokerage] 已报单: {req.InstrumentId} {req.Direction}{req.OffsetFlag} {req.Volume}手 @{req.LimitPrice} ref={r.OrderRef}");
                }
                catch (Exception ex)
                {
                    CtpLeanGateway.Log($"[Brokerage] 报单失败: {req.InstrumentId} {req.Direction}{req.OffsetFlag} {req.Volume}手 → {ex.Message}");
                }
            }

            if (refs.Count == 0)
            {
                // 不得用 BrokerageMessageType.Error：LEAN 会把它升级为 RuntimeError 直接停引擎
                // （2026-10-21 双 bot 实测——前置换会话的几秒窗口内所有报单失败即团灭）。
                // 本单以 false 返回（LEAN 标记 Invalid），策略在下一个 tick/事件自然重报。
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "OrderInsert", "报单失败（连接重连窗口，详见日志）"));
                return false;
            }
            if (refs.Count < result.Requests.Count)
                CtpLeanGateway.Log($"[Brokerage] 部分报单成功 {refs.Count}/{result.Requests.Count}（失败腿已跳过，剩余腿继续跟踪）");

            order.BrokerId.AddRange(refs);
            _tracker.Add(order, sentRequests, refs, login?.FrontId ?? 0, login?.SessionId ?? 0);
            StartWatchdog();

            // 立即发 Submitted（镜像 BacktestingBrokerage）——OrderInsertAsync 已本地校验 rc，静默失败
            // 模式（前置拒单无任何回报）由 watchdog 兜底；不发则该单在 Lean 侧永远停在 New 状态。
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.Submitted });
            return true;
        }
        catch (Exception ex)
        {
            CtpLeanGateway.Log($"[Brokerage] PlaceOrder 异常: {ex.Message}");
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "PlaceOrder", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// 本账户在该合约、该方向上**当前还挂在簿上**的平仓手数——交易所就是按这个数预留可平量的
    /// （<c>SimTradingCore.ReservedCloseVolume</c> 把该方向所有在挂平仓单的 Remaining 全部计走）。
    /// 做市商在净空 N 手时每档买腿都映射成 Close，第一张就把 N 手里的 1 手占住，后面的腿全部撞
    /// <c>[90006] 无可平持仓</c>——腿表越拒越多（实测 38 条在挂、目标只要 3 档）。映射器收到这个数
    /// 就能改成「先扣已占的可平量，扣完就改开仓腿」，不再硬撞交易所的预留规则。
    /// </summary>
    private int RestingCloseVolume(string instrumentId, CtpDirection direction)
    {
        var sum = 0;
        foreach (var (child, _) in _tracker.LiveChildren(TimeSpan.Zero))
        {
            var req = child.Request;
            if (req.OffsetFlag == CtpOffsetFlag.Open) continue;
            if (req.Direction != direction) continue;
            if (!string.Equals(req.InstrumentId, instrumentId, StringComparison.OrdinalIgnoreCase)) continue;
            sum += Math.Max(0, req.Volume - child.VolumeTraded);
        }
        return sum;
    }

    public override bool UpdateOrder(Order order)
    {
        // CTP 改单（撤单+重报）后续版本再做
        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "P2", "CTP 改单暂不支持——请撤单后重新下单"));
        CtpLeanGateway.Log($"[Brokerage] 拦截改单: {order.Symbol.Value} ref={string.Join(",", order.BrokerId)}");
        return false;
    }

    public override bool CancelOrder(Order order)
    {
        if (!_tracker.TryGetByLeanId(order.Id, out var tracked))
        {
            CtpLeanGateway.Log($"[Brokerage] 撤单失败: Lean 单 {order.Id} 不在跟踪表（引擎外订单）");
            return false;
        }

        foreach (var child in tracked.Children)
        {
            if (child.Terminal) continue;
            if (string.IsNullOrEmpty(child.ExchangeId))
            {
                CtpLeanGateway.Log($"[Brokerage] 撤单跳过 ref={child.OrderRef}: 尚未收到报单回报（无交易所代码），请稍后在期货页手动撤单");
                continue;
            }
            // InstrumentId 必带：DCE 等交易所按「合约 + 报单引用」索引委托，缺它真前置回 25「找不到相应报单」
            // （2026-10-24 SimNow 实测；本地交易所不校验，所以本地跑通 ≠ 真前置能撤）
            var orderRef = new CtpOrderRef(_session.BrokerId ?? "", _session.UserId ?? "",
                child.FrontId, child.SessionId, child.OrderRef, child.ExchangeId, child.Request.InstrumentId);
            // fire-and-forget：OrderActionAsync 最长阻塞 15s，阻塞 transaction-handler 线程会卡住全部订单处理；
            // Canceled 事件由 '5' 回报驱动（Lean 契约：true=请求已发）
            Task.Run(async () =>
            {
                try { await _session.OrderActionAsync(orderRef); }
                catch (Exception ex) { CtpLeanGateway.Log($"[Brokerage] 撤单请求失败 ref={child.OrderRef}: {ex.Message}"); }
            });
            CtpLeanGateway.Log($"[Brokerage] 撤单请求已发: {child.OrderRef}");
        }
        return true;
    }

    /// <summary>
    /// 刻意返回空（已知限制）：引擎外挂单（手动/上次运行残留）不注册进 Lean——CtpBar 是 Base 类型，
    /// 未订阅合约经 GetOrAddUnrequestedSecurity 会让引擎 setup 失败；且其成交回报 OrderRef 不在跟踪表、
    /// Portfolio 永远收不到 fill。引擎停止后请先在期货页手动撤掉残留挂单。
    /// </summary>
    public override List<Order> GetOpenOrders() => new();

    /// <summary>P2 持仓回填：BrokerageSetupHandler 启动时调用（Initialize 之后），直接 seed Lean Portfolio。
    /// 只回填算法已订阅的合约（未订阅的跳过 + log——Base 类型限制，见 <see cref="CtpAccountMappers"/>）。</summary>
    public override List<Holding> GetAccountHoldings()
    {
        try
        {
            var positions = BlockingCall(() => _session.QueryPositionAsync(), TimeSpan.FromSeconds(8), "持仓查询(回填)");
            _positionCache.Replace(positions);   // 一查两用：回填 Lean + 刷 offset 决策用的持仓缓存
            var (holdings, skipped) = CtpAccountMappers.ToHoldings(positions, LookupAlgorithmSymbol, PriceFallback);
            foreach (var id in skipped)
                CtpLeanGateway.Log($"[Brokerage] 跳过未订阅合约持仓 {id}（引擎不管理，请在期货页手动处理）");
            foreach (var h in holdings)
                CtpLeanGateway.Log($"[Brokerage] 回填持仓: {h.Symbol.Value} {h.Quantity}手 均价≈{h.AveragePrice}");
            return holdings;
        }
        catch (Exception ex)
        {
            CtpLeanGateway.Log($"[Brokerage] 持仓查询失败（按空仓启动）: {ex.Message}");
            return new List<Holding>();
        }
    }

    /// <summary>成交回报 → Lean fill 事件（真实成交价）。未知 OrderRef = 期货页手动单，仅记录。</summary>
    private void OnTradeReceived(object? sender, CtpTrade trade)
    {
        CtpLeanGateway.Log($"[Trade] {trade.InstrumentId} {trade.Direction}{trade.OffsetFlag} {trade.Volume}@{trade.Price} ref={trade.OrderRef}");
        if (!_tracker.TryGetByOrderRef(trade.OrderRef, out var child, out var tracked)) return;

        if (!_tracker.RecordTrade(trade.OrderRef, trade.TradeId, trade.Volume, out var filled))
            return;   // 重复 TradeId 或竞态，跳过

        var order = tracked.Order;
        var signed = order.Direction == OrderDirection.Sell ? -trade.Volume : trade.Volume;
        if (filled >= tracked.ExpectedVolume)
        {
            // 全部成交到齐：增量 fill + 终态 Filled 确认（FillQuantity=0 安全——Lean ProcessFill no-op）
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
            {
                Status = OrderStatus.PartiallyFilled,
                FillPrice = (decimal)trade.Price,
                FillQuantity = signed,
            });
            if (_tracker.TryMarkFilled(tracked))
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.Filled });
            HandleAggregateTerminal(tracked, "已全部成交");
        }
        else
        {
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
            {
                Status = OrderStatus.PartiallyFilled,
                FillPrice = (decimal)trade.Price,
                FillQuantity = signed,
            });
        }

        // 持仓缓存增量维护（offset 决策数据源）——方向/开平取本腿报单请求（⑤ 成交推送的方向/开平槽为
        // 捕获 verbatim 恒 BuyOpen——用它维护缓存会令 Long 单调膨胀/Short 恒零 ⇒ 开平映射错判 ⇒ 仓位失控）
        _positionCache.ApplyTrade(trade, child.Request.Direction, child.Request.OffsetFlag);
    }

    /// <summary>报单回报 → 生命周期事件。非终态仅记录（Submitted 已在 PlaceOrder 发过）；终态走聚合判断。</summary>
    private void OnOrderReceived(object? sender, CtpOrder order)
    {
        CtpLeanGateway.Log($"[Order] {order.InstrumentId} {order.Direction}{order.OffsetFlag} {order.VolumeTotalOriginal}@{order.LimitPrice} " +
                           $"状态={(char)order.OrderStatusRaw} 成{order.VolumeTraded} {order.StatusMsg} ref={order.OrderRef}");
        if (!_tracker.TryGetByOrderRef(order.OrderRef, out _, out var tracked)) return;   // 手动单

        _tracker.SetExchangeId(order.OrderRef, order.ExchangeId);
        _tracker.MarkReport(order.OrderRef, order.OrderStatusRaw);
        HandleAggregateTerminal(tracked, order.StatusMsg);
    }

    /// <summary>
    /// 终态聚合（2026-10-03 修正）：全部子单终态后按「成交腿 vs 撤/拒腿」分组发终态事件（SHFE 拆腿两腿全完才算完）。
    /// 在 tracker 的终态旗标原子仲裁下只发一次（末笔 Trade 与 '0' 回报的竞态安全）。<para/>
    /// 旧版缺陷：见「任一腿成交」即走 Filled/补齐分支，把**撤/拒腿的残量**当成交缺口回补幻影成交
    /// （双 bot 实测：SellClose1 成 + SellOpen4 撤 ⇒ 净持仓凭空 -4 ⇒ Lean Portfolio 与核心账本漂移）。
    /// 现按腿终态分组：①成交腿存在且无撤/拒腿 ⇒ Filled（Trade 缺口按限价补齐——'0' 回报量 verbatim
    /// 失真的兜底）；②有成交腿 + 有撤/拒腿 ⇒ 部分成交后撤——fill 保留（Trade 已计），终态 Canceled；
    /// ③零成交+被拒（'4'）⇒ Invalid；④其余（零成全撤）⇒ Canceled。
    /// </summary>
    private void HandleAggregateTerminal(CtpOrderTracker.Tracked tracked, string context)
    {
        if (!_tracker.AllChildrenTerminal(tracked)) return;
        if (tracked.FilledEmitted || tracked.CanceledEmitted) return;

        var order = tracked.Order;
        var claimedFilled = tracked.Children.Where(c => c.LastStatus == OrderStatus.Filled).Sum(c => c.Request.Volume);
        var hasCancelledOrRejected = tracked.Children.Any(c => c.LastStatus is OrderStatus.Canceled or OrderStatus.Invalid);
        if (claimedFilled > 0)
        {
            if (hasCancelledOrRejected)
            {
                // 部分腿成交、其余撤/拒：成交保留、残量不作缺口——终态 Canceled
                if (_tracker.TryMarkCanceled(tracked))
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, context) { Status = OrderStatus.Canceled });
            }
            else if (tracked.Filled >= claimedFilled)
            {
                if (_tracker.TryMarkFilled(tracked))
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.Filled });
            }
            else
            {
                // 回报宣称全成但 Trade 回报乱序未到齐：补齐缺口（价格用该腿限价近似）+ 警示——
                // 缺口只按「成交腿申报量」算（撤/拒腿不贡献）
                var missing = claimedFilled - (int)tracked.Filled;
                var leg = tracked.Children.First(c => c.LastStatus == OrderStatus.Filled);
                var signedMissing = order.Direction == OrderDirection.Sell ? -missing : missing;
                CtpLeanGateway.Log($"[Brokerage] 报单全成但成交回报缺 {missing} 手，按限价 {leg.Request.LimitPrice} 补齐");
                if (_tracker.TryMarkFilled(tracked))
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
                    {
                        Status = OrderStatus.Filled,
                        FillPrice = (decimal)leg.Request.LimitPrice,
                        FillQuantity = signedMissing,
                    });
                }
            }
        }
        else if (tracked.Filled == 0 && tracked.Children.Any(c => c.Rejected))
        {
            // 零成交 + 被拒（'4'，StatusMsg 带拒绝原因）→ Invalid
            if (_tracker.TryMarkCanceled(tracked))
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, context) { Status = OrderStatus.Invalid });
        }
        else
        {
            // 零成全撤（或撤前部成的 fill 已由 Trade 计账）——终态 Canceled
            if (_tracker.TryMarkCanceled(tracked))
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, context) { Status = OrderStatus.Canceled });
        }
    }

    /// <summary>启动对账定时器（Connect 调；Disconnect 停）。</summary>
    private void StartReconciler()
    {
        if (_reconciler != null) return;
        _reconciler = new System.Threading.Timer(_ => ReconcileQuietly(), null, ReconcileInterval, ReconcileInterval);
    }

    private void StopReconciler()
    {
        _reconciler?.Dispose();
        _reconciler = null;
    }

    private void ReconcileQuietly()
    {
        try { ReconcileWithExchange(); }
        catch (Exception ex) { CtpLeanGateway.Log($"[Brokerage] 对账异常（忽略，下轮再试）: {ex.Message}"); }
    }

    /// <summary>
    /// 与交易所对账（open-issues #25）：把自己认为「还挂着」的腿跟交易所实际委托表比对，
    /// 交易所那边没了的腿 ⇒ 补发终态 Canceled，让策略在下一个 tick 重报。
    /// <para>
    /// 三条纪律：
    /// <list type="number">
    /// <item>**查询失败绝不推断**：查报单超时/报错只记日志并整轮跳过——把「查不到」当成「交易所没单」
    /// 会误撤真在挂的腿，那比不撤更危险（真单还在簿上，策略却以为没了 ⇒ 重复挂单）。</item>
    /// <item>**状态以交易所为准**：能查到但状态已是终态（'0'/'2'/'4'/'5'）的腿，说明终态回报丢了，
    /// 用查询结果补一次 <c>MarkReport</c>（成交缺口由 <see cref="HandleAggregateTerminal"/> 既有逻辑兜）。</item>
    /// <item>**宽限 3s**：刚报出去的单可能还没进查询结果。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal void ReconcileWithExchange()
    {
        if (!_session.IsConnected) return;
        var live = _tracker.LiveChildren(ReconcileGrace);
        // 起点清场（#29）也要查报单，所以「本进程没有活腿」不再是提前返回的理由
        var sweep = _orphanSweeps < OrphanSweepMaxPasses;
        if (live.Count == 0 && !sweep)
        {
            RefreshPositionCache();
            return;
        }

        IReadOnlyList<CtpOrder> orders;
        try
        {
            orders = BlockingCall(() => _session.QueryOrderAsync(null, CancellationToken.None),
                TimeSpan.FromSeconds(8), "对账查报单");
        }
        catch (Exception ex)
        {
            CtpLeanGateway.Log($"[Brokerage] 对账跳过：查报单失败（{ex.Message}）——不据此撤腿");
            return;
        }

        var byRef = new Dictionary<string, CtpOrder>(StringComparer.Ordinal);
        foreach (var o in orders) byRef[o.OrderRef] = o;

        // 反向对账（#29）：交易所还有、本进程不认识的活单 ⇒ 撤掉。
        // 不清会怎样（2026-10-31 实测）：交易所按「同向平仓挂单的剩余量」预留可平量
        // （SimTradingCore.ReservedCloseVolume），上一次运行留下的孤儿平仓单把这个账户的可平量占满
        // ⇒ 之后每张平仓单都被 90006「无可平持仓」拒（单账户累计 743 次）⇒ 做市商的减仓侧一张也挂不上、
        // 簿上只剩一侧。孤儿单还白占保证金与簿位。
        if (sweep) SweepOrphanOrders(orders);

        var canceled = 0;
        foreach (var (child, tracked) in live)
        {
            if (byRef.TryGetValue(child.OrderRef, out var exchangeOrder))
            {
                var raw = exchangeOrder.OrderStatusRaw;
                if (raw is (byte)'1' or (byte)'3') continue;   // 还在挂，正常
                _tracker.MarkReport(child.OrderRef, raw);
                CtpLeanGateway.Log($"[Brokerage] 对账：交易所侧状态={raw}（{(char)raw}），补发终态 " +
                                   $"{child.Request.InstrumentId} {child.Request.Direction}{child.Request.OffsetFlag} " +
                                   $"{child.Request.Volume}手 ref={child.OrderRef}（终态回报丢失）");
                canceled++;
                HandleAggregateTerminal(tracked, $"对账：交易所侧状态 {(char)raw}");
            }
            else
            {
                if (!_tracker.TryMarkReconciledCanceled(child.OrderRef)) continue;
                CtpLeanGateway.Log($"[Brokerage] 对账：交易所已无此委托，按撤单处理 " +
                                   $"{child.Request.InstrumentId} {child.Request.Direction}{child.Request.OffsetFlag} " +
                                   $"{child.Request.Volume}手 ref={child.OrderRef}（柜台重启作废/报单未落地），策略应重报");
                canceled++;
                HandleAggregateTerminal(tracked, "对账：交易所已无此委托");
            }
        }

        if (canceled > 0)
            CtpLeanGateway.Log($"[Brokerage] 对账完成：{canceled} 条腿被判定失效（交易所实际委托 {orders.Count} 条）");
        // 顺带刷新持仓：作废的委托对应的冻结额度已释放，offset/仓位决策要用新的
        RefreshPositionCache();
    }

    /// <summary>
    /// 起点清场（open-issues #29）：把交易所簿上**本进程不跟踪**的活单撤掉。
    /// <para>
    /// 为什么必须做：交易所按「同向平仓挂单的剩余量」预留可平量（<c>SimTradingCore.ReservedCloseVolume</c>），
    /// 上一次运行（崩溃 / 被 kill / 直接关窗口）留下的孤儿平仓单会把可平量占满 ⇒ 新进程的平仓单一律被
    /// 90006「无可平持仓」拒（2026-10-31 实测：单账户累计 743 次，做市商的减仓侧一张都挂不上，簿上只剩一侧）。
    /// 孤儿单还白占保证金和簿位。
    /// </para>
    /// 三条边界：
    /// <list type="number">
    /// <item>只清**本进程订阅的合约**（<see cref="_symbolByInstrument"/>）——别的合约上的单可能是人在期货页下的，不碰。</item>
    /// <item>只清**本进程不跟踪**的（<see cref="CtpOrderTracker.TryGetByOrderRef"/>）——自己刚报的腿一根都不动。</item>
    /// <item>轮数上限 <see cref="OrphanSweepMaxPasses"/>：交易所一次查询最多回 200 行（#28），大堆孤儿要清几轮；
    /// 但同一账户若被两个进程同时用（已知限制），不能无限互撤。</item>
    /// </list>
    /// 撤单 fire-and-forget（<c>OrderActionAsync</c> 最长阻塞 15s，卡住对账定时器就看不见活单状态了）。
    /// </summary>
    private void SweepOrphanOrders(IReadOnlyList<CtpOrder> orders)
    {
        _orphanSweeps++;
        var swept = 0;
        foreach (var o in orders)
        {
            if (o.OrderStatusRaw is not ((byte)'1' or (byte)'3')) continue;        // 只清活单
            if (!_symbolByInstrument.ContainsKey(o.InstrumentId)) continue;         // 只清本策略摸的合约
            if (_tracker.TryGetByOrderRef(o.OrderRef, out _, out _)) continue;      // 自己的腿不碰
            var orderRef = new CtpOrderRef(_session.BrokerId ?? "", _session.UserId ?? "",
                o.FrontId, o.SessionId, o.OrderRef, o.ExchangeId, o.InstrumentId);
            Task.Run(async () =>
            {
                try { await _session.OrderActionAsync(orderRef); }
                catch (Exception ex) { CtpLeanGateway.Log($"[Brokerage] 清场撤单失败 ref={o.OrderRef}: {ex.Message}"); }
            });
            swept++;
            if (swept <= 10)   // 明细只打前 10 条，其余给总数
                CtpLeanGateway.Log($"[Brokerage] 清场：撤孤儿单 {o.InstrumentId} {o.Direction}{o.OffsetFlag} " +
                                   $"{o.VolumeTotalOriginal - o.VolumeTraded}/{o.VolumeTotalOriginal}手 @{o.LimitPrice} ref={o.OrderRef}");
        }
        CtpLeanGateway.Log(swept > 0
            ? $"[Brokerage] 清场第 {_orphanSweeps} 轮：撤 {swept} 条上一次运行留下的活单（委托表 {orders.Count} 条）"
            : $"[Brokerage] 清场完成：交易所侧没有本进程不认识的活单（委托表 {orders.Count} 条）");
    }

    /// <summary>持仓缓存节流刷新（CTP 查询流控）。失败容忍——沿用旧快照。</summary>
    private void RefreshPositionCacheIfStale()
    {
        if (!_positionCache.IsStale(PositionRefreshInterval)) return;
        RefreshPositionCache();
    }

    private void RefreshPositionCache()
    {
        try
        {
            // 5s 护栏（重连窗口挂起实测——本调用在 PlaceOrder 热路径上，挂起即冻结全部订单处理）
            var positions = BlockingCall(() => _session.QueryPositionAsync(), TimeSpan.FromSeconds(5), "持仓查询");
            _positionCache.Replace(positions);
        }
        catch (Exception ex)
        {
            CtpLeanGateway.Log($"[Brokerage] 持仓查询失败（沿用缓存快照）: {ex.Message}");
        }
    }

    /// <summary>watchdog：5s 扫描一次，下单 10s 无首次回报的腿判 Invalid（前置静默拒单不可见）。</summary>
    private void StartWatchdog()
    {
        if (_watchdog != null) return;
        _watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                foreach (var (child, tracked) in _tracker.StaleChildren(WatchdogTimeout))
                {
                    if (!_tracker.TryMarkWatchdogInvalid(child.OrderRef)) continue;
                    CtpLeanGateway.Log($"[Brokerage] 下单 {WatchdogTimeout.TotalSeconds:0}s 未收到报单回报，判无效: " +
                                       $"{child.Request.InstrumentId} {child.Request.Direction}{child.Request.OffsetFlag} {child.Request.Volume}手 ref={child.OrderRef}（可能被交易前置拒绝）");
                    HandleAggregateTerminal(tracked, "下单后未收到报单回报（可能被交易前置拒绝）");
                }
            }
            catch { /* watchdog 不应抛 */ }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    /// <summary>instrumentId → 算法已订阅的 Lean Symbol（GetAccountHoldings 时尚未走 Subscribe，不能依赖 _symbolByInstrument）。</summary>
    private Symbol? LookupAlgorithmSymbol(string instrumentId)
    {
        if (_algorithm == null) return null;
        foreach (var kv in _algorithm.Securities)
            if (string.Equals(kv.Key.Value, instrumentId, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return null;
    }

    /// <summary>Lean 大写 ticker → CTP 原始 instrumentId（订阅合约列表大小写不敏感匹配；不在列表则原样返回）。</summary>
    private static string ResolveCtpInstrument(string leanTicker)
    {
        foreach (var i in CtpLeanGateway.Instruments)
            if (string.Equals(i, leanTicker, StringComparison.OrdinalIgnoreCase))
                return i;
        return leanTicker;
    }

    /// <summary>
    /// 近似均价（CtpPosition 没有均价字段——LastPrice → PreSettlement；拿不到就**等**第一笔行情）。
    /// <para>
    /// <b>为什么不能返回 0（2026-10-31 定案）</b>：Lean 用均价算持仓浮盈亏。均价 0 的空头 = 一笔巨额浮盈/浮亏
    /// （实测 probe1 空 12 手 @0、市价 118 ⇒ 假亏 12×118×乘数），买入力当场被算穿 ⇒ 策略的**每一张新单**
    /// 都被 Lean 自己判「Insufficient buying power」拦在报单之前（日志里只有 <c>Order Error ... Initial Margin: -118.15</c>，
    /// 交易所根本收不到）。表现就是「只有一侧能报价」——正是「市场没有对手盘」的来源之一。
    /// </para>
    /// 交易所侧的持仓成本/保证金字段在 CTP 线上还是抓包常量（#5），所以只能拿行情兜：启动时通常还没有 tick，
    /// 这里把行情订阅提前，最多等 3s 拿第一笔（拿不到按 0 继续——不阻塞启动，但要留日志）。
    /// </summary>
    private decimal PriceFallback(string instrumentId)
    {
        lock (_fallbackPrice)
        {
            if (_fallbackPrice.TryGetValue(instrumentId, out var cached)) return cached;
        }
        var price = FirstTickOrWait(instrumentId);
        lock (_fallbackPrice) _fallbackPrice[instrumentId] = price;
        if (price <= 0)
            CtpLeanGateway.Log($"[Brokerage] 回填持仓拿不到 {instrumentId} 的行情价——均价按 0（Lean 的买入力会被浮盈亏算歪，下一单可能被自带风控拦下）");
        return price;
    }

    /// <summary>启动时的一笔价：tick 缓存 → （订阅后）最多等 3s 等第一笔推送。</summary>
    private decimal FirstTickOrWait(string instrumentId)
    {
        for (var i = 0; i < 30; i++)
        {
            var tick = _session.GetLatestTick(instrumentId);
            if (tick is { LastPrice: > 0 }) return (decimal)tick.LastPrice;
            if (tick is { PreSettlement: > 0 }) return (decimal)tick.PreSettlement;
            if (i == 0)
            {
                try { EnsureCtpMdSubscribed(); }
                catch (Exception ex) { CtpLeanGateway.Log($"[Brokerage] 回填持仓前订阅行情失败（均价按 0）: {ex.Message}"); break; }
            }
            Thread.Sleep(100);
        }
        return 0m;
    }
}
