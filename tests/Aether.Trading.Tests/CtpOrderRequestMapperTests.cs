using Ctp.Net;
using Aether.Trading.Runtime.Lean;
using QuantConnect.Orders;
using Xunit;

namespace Aether.Trading.Tests;

/// <summary>
/// P2 真实下单核心映射规则单测（纯函数，零 CTP 依赖）：
/// 对手价限价 / 开仓保险丝（平仓不封顶）/ SHFE 平昨优先拆腿 / 反转单拒绝 / 无行情拒单。
/// </summary>
public class CtpOrderRequestMapperTests
{
    private static CtpTick Tick(double bid = 7000, double ask = 7002, double last = 7001, double preSettle = 6990) => new()
    {
        InstrumentId = "ag2610",
        BidPrice = bid,
        AskPrice = ask,
        LastPrice = last,
        PreSettlement = preSettle,
    };

    private static CtpPositionSnapshot Pos(string exchange = "DCE",
        int longT = 0, int longToday = 0, int longYd = 0,
        int shortT = 0, int shortToday = 0, int shortYd = 0)
        => new(exchange, longT, longToday, longYd, shortT, shortToday, shortYd);

    private static CtpOrderRequestMapper.MapInput Input(
        decimal quantity,
        CtpPositionSnapshot? pos = null,
        CtpTick? tick = null,
        int maxVolume = 1,
        OrderType type = OrderType.Market,
        OrderDirection? dir = null,
        decimal limitPrice = 0m,
        bool missingTick = false)
        => new(
            dir ?? (quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell),   // 默认按数量符号推导
            type, limitPrice, quantity, "ag2610", pos,
            missingTick ? null : tick ?? Tick(),
            "9999", "user01", maxVolume);

    [Fact]
    public void Buy_on_flat_position_opens_at_ask()
    {
        var r = CtpOrderRequestMapper.Map(Input(+2m, pos: null, maxVolume: 10));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpDirection.Buy, req.Direction);
        Assert.Equal(CtpOffsetFlag.Open, req.OffsetFlag);
        Assert.Equal(CtpOrderPriceType.Limit, req.PriceType);
        Assert.Equal(7002.02, req.LimitPrice);       // 买用卖一（Ask）+ 跨越余量（量化防护，见 Pad钉）
        Assert.Equal(2, req.Volume);
        Assert.Equal("9999", req.BrokerId);
        Assert.Equal("user01", req.InvestorId);
    }

    // —— post-only（BTC 对标 2026-10-05：Tag "post-only" ⇒ TC='4'，仅限价单）——

    [Fact]
    public void PostOnly_limit_order_maps_to_TC4()
    {
        var r = CtpOrderRequestMapper.Map(Input(+1m, type: OrderType.Limit, limitPrice: 6990m, maxVolume: 10) with { PostOnly = true });

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpTimeCondition.PostOnly, req.TimeCondition);
        Assert.Equal(6990, req.LimitPrice);
    }

    [Fact]
    public void PostOnly_market_order_rejected()
    {
        var r = CtpOrderRequestMapper.Map(Input(+1m, type: OrderType.Market, maxVolume: 10) with { PostOnly = true });

        Assert.NotNull(r.Error);
        Assert.Contains("post-only", r.Error);
        Assert.Empty(r.Requests);
    }

    [Fact]
    public void Default_TC_remains_GFD_when_not_post_only()
    {
        var r = CtpOrderRequestMapper.Map(Input(+1m, type: OrderType.Limit, limitPrice: 6990m, maxVolume: 10));

        Assert.Null(r.Error);
        Assert.Equal(CtpTimeCondition.GFD, Assert.Single(r.Requests).TimeCondition);
    }

    [Fact]
    public void Sell_close_long_non_shfe_uses_close_at_bid()
    {
        var r = CtpOrderRequestMapper.Map(Input(-3m,
            pos: Pos("DCE", longT: 5, longToday: 2, longYd: 3), maxVolume: 1));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpDirection.Sell, req.Direction);
        Assert.Equal(CtpOffsetFlag.Close, req.OffsetFlag);   // 非 SHFE 系直接 Close
        Assert.Equal(6999.98, req.LimitPrice);               // 卖用买一（Bid）− 跨越余量
        Assert.Equal(3, req.Volume);                         // 平仓不受手数上限（maxVolume=1 仍 3 手）
    }

    [Fact]
    public void Sell_close_long_shfe_splits_yd_first_then_today()
    {
        // 昨 3 今 2，平 4 → 平昨 3 + 平今 1（平昨优先）
        var r = CtpOrderRequestMapper.Map(Input(-4m,
            pos: Pos("SHFE", longT: 5, longToday: 2, longYd: 3)));

        Assert.Null(r.Error);
        Assert.Equal(2, r.Requests.Count);
        Assert.Equal(CtpOffsetFlag.CloseYesterday, r.Requests[0].OffsetFlag);
        Assert.Equal(3, r.Requests[0].Volume);
        Assert.Equal(CtpOffsetFlag.CloseToday, r.Requests[1].OffsetFlag);
        Assert.Equal(1, r.Requests[1].Volume);
    }

    [Fact]
    public void Sell_close_long_shfe_all_yesterday_single_leg()
    {
        var r = CtpOrderRequestMapper.Map(Input(-2m,
            pos: Pos("INE", longT: 2, longToday: 0, longYd: 2)));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpOffsetFlag.CloseYesterday, req.OffsetFlag);
        Assert.Equal(2, req.Volume);
    }

    [Fact]
    public void Buy_closes_short_position()
    {
        var r = CtpOrderRequestMapper.Map(Input(+2m,
            pos: Pos("DCE", shortT: 2, shortToday: 1, shortYd: 1), dir: OrderDirection.Buy));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpDirection.Buy, req.Direction);
        Assert.Equal(CtpOffsetFlag.Close, req.OffsetFlag);
        Assert.Equal(2, req.Volume);
    }

    [Fact]
    public void Same_direction_adds_to_position_as_open()
    {
        // 已持多 2 手，再买 1 → 加仓（Open），走保险丝
        var r = CtpOrderRequestMapper.Map(Input(+1m,
            pos: Pos("DCE", longT: 2, longToday: 2), maxVolume: 5));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpOffsetFlag.Open, req.OffsetFlag);
        Assert.Equal(1, req.Volume);
    }

    [Fact]
    public void Open_volume_is_capped_by_fuse()
    {
        var r = CtpOrderRequestMapper.Map(Input(+10m, pos: null, maxVolume: 1));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpOffsetFlag.Open, req.OffsetFlag);
        Assert.Equal(1, req.Volume);              // 10 手封顶到 1
    }

    [Fact]
    public void Partial_close_only_closes_requested_volume()
    {
        // 持多 5，卖 2 → 只平 2（Close，非反转）
        var r = CtpOrderRequestMapper.Map(Input(-2m,
            pos: Pos("DCE", longT: 5, longToday: 5)));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpOffsetFlag.Close, req.OffsetFlag);
        Assert.Equal(2, req.Volume);
    }

    [Fact]
    public void Reversal_order_splits_into_close_and_open_legs()
    {
        // 2026-09-28 修：Lean 策略以净仓位思考（持多 3 卖 5 = 净空 2）——映射到 CTP 拆「平 3 + 开 2」两腿。
        // 旧版直接拒反转单：做市策略被动吃单攒出净仓后，反向报价腿全被拒 ⇒ 簿单边化。
        // 非 SHFE 系（DCE）：平仓单腿 Close + 开腿 Open。
        var r = CtpOrderRequestMapper.Map(Input(-5m,
            pos: Pos("DCE", longT: 3, longToday: 3, longYd: 0), maxVolume: 10));

        Assert.Null(r.Error);
        Assert.Equal(2, r.Requests.Count);
        Assert.Equal(CtpOffsetFlag.Close, r.Requests[0].OffsetFlag);
        Assert.Equal(3, r.Requests[0].Volume);              // 平全部净仓（不封顶）
        Assert.Equal(CtpOffsetFlag.Open, r.Requests[1].OffsetFlag);
        Assert.Equal(2, r.Requests[1].Volume);              // 开超额
    }

    [Fact]
    public void Reversal_order_shfe_splits_yd_today_and_open()
    {
        // SHFE 系反转：平昨/平今拆腿 + 开腿（三腿全拆）
        var r = CtpOrderRequestMapper.Map(Input(-5m,
            pos: Pos("SHFE", longT: 3, longToday: 1, longYd: 2), maxVolume: 10));   // 多3 = 昨2+今1

        Assert.Null(r.Error);
        Assert.Equal(3, r.Requests.Count);
        Assert.Equal(CtpOffsetFlag.CloseYesterday, r.Requests[0].OffsetFlag);
        Assert.Equal(2, r.Requests[0].Volume);
        Assert.Equal(CtpOffsetFlag.CloseToday, r.Requests[1].OffsetFlag);
        Assert.Equal(1, r.Requests[1].Volume);
        Assert.Equal(CtpOffsetFlag.Open, r.Requests[2].OffsetFlag);
        Assert.Equal(2, r.Requests[2].Volume);
    }

    [Fact]
    public void Reversal_open_leg_respects_fuse()
    {
        // 反转开腿受保险丝封顶（平仓腿绝不封顶）：持多 3 卖 10、保险丝 2 ⇒ 平 3 + 开 2
        var r = CtpOrderRequestMapper.Map(Input(-10m,
            pos: Pos("DCE", longT: 3, longToday: 3, longYd: 0), maxVolume: 2));

        Assert.Null(r.Error);
        Assert.Equal(2, r.Requests.Count);
        Assert.Equal(3, r.Requests[0].Volume);              // 平腿不受限
        Assert.Equal(CtpOffsetFlag.Open, r.Requests[1].OffsetFlag);
        Assert.Equal(2, r.Requests[1].Volume);              // 开腿封顶到 2
    }

    [Fact]
    public void MarketOnOpen_is_treated_as_market()
    {
        // Base 类型不拦 MOO（exchange hours 与 CTP 夜盘可能不匹配）——当市价处理走对手价
        var r = CtpOrderRequestMapper.Map(Input(+1m, type: OrderType.MarketOnOpen, maxVolume: 5));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(7002.02, req.LimitPrice);
    }

    [Fact]
    public void Limit_order_uses_order_price()
    {
        var r = CtpOrderRequestMapper.Map(Input(+1m, type: OrderType.Limit, limitPrice: 7050m, maxVolume: 5));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(7050, req.LimitPrice);
    }

    [Fact]
    public void 市价转对手价限价_加跨越余量_覆盖价格槽量化失真()
    {
        // 2026-10-17 本地长跑双 bot 定案：wire 价格槽 3B 量化在 [64,128) 区间 ulp=1/64 ⇒ 对手价
        // 回显 ≤半 ulp（≈0.0078）失真——买 99.96 量化成 99.953125 ⇒ 限价低于对手价永不交叉 ⇒
        // IOC 全作废 ⇒ 市场僵死（NT 吃不到 MM 挂单、无成交无 tick、双 bot 静止）。修复 = 对手价
        // ±max(PriceTick, 0.02) 余量（2× 最大半 ulp，限价只是上限、成交按 maker 挂单价——无滑点代价）。
        var r = CtpOrderRequestMapper.Map(Input(+1m, tick: Tick(bid: 99.94, ask: 99.96, last: 99.95), maxVolume: 3));
        Assert.Null(r.Error);
        Assert.Equal(99.98, r.Requests[0].LimitPrice, 5);   // 99.96 + 0.02——量化后（99.98→99.984375）仍 ≥ 99.96 ⇒ 交叉

        var s = CtpOrderRequestMapper.Map(Input(-1m, tick: Tick(bid: 99.94, ask: 99.96, last: 99.95), maxVolume: 3));
        Assert.Null(s.Error);
        Assert.Equal(99.92, s.Requests[0].LimitPrice, 5);   // 99.94 − 0.02——量化后仍 ≤ 99.94 ⇒ 交叉

        // PriceTick 更大时用 PriceTick（zx001 tick=1 场景）
        var z = CtpOrderRequestMapper.Map(Input(+1m, tick: Tick(bid: 3799, ask: 3801, last: 3800), maxVolume: 3)
            with { PriceTick = 1.0 });
        Assert.Null(z.Error);
        Assert.Equal(3802, z.Requests[0].LimitPrice);
    }

    [Fact]
    public void Missing_tick_rejects()
    {
        var r = CtpOrderRequestMapper.Map(Input(+1m, missingTick: true));

        Assert.NotNull(r.Error);
        Assert.Contains("行情", r.Error);
    }

    [Fact]
    public void Price_falls_back_to_last_then_presettlement()
    {
        // Ask/Bid 无效 → LastPrice
        var r1 = CtpOrderRequestMapper.Map(Input(+1m, tick: Tick(bid: 0, ask: 0, last: 7001), maxVolume: 5));
        Assert.Null(r1.Error);
        Assert.Equal(7001.02, r1.Requests[0].LimitPrice);

        // Last 也无效 → PreSettlement
        var r2 = CtpOrderRequestMapper.Map(Input(+1m, tick: Tick(bid: 0, ask: 0, last: 0, preSettle: 6990), maxVolume: 5));
        Assert.Null(r2.Error);
        Assert.Equal(6990.02, r2.Requests[0].LimitPrice);

        // 全无效 → 拒单
        var r3 = CtpOrderRequestMapper.Map(Input(+1m, tick: Tick(bid: 0, ask: 0, last: 0, preSettle: 0), maxVolume: 5));
        Assert.NotNull(r3.Error);
    }

    [Fact]
    public void Fractional_or_zero_quantity_rejects()
    {
        Assert.NotNull(CtpOrderRequestMapper.Map(Input(0m)).Error);
        Assert.NotNull(CtpOrderRequestMapper.Map(Input(1.5m)).Error);
    }

    [Fact]
    public void Unsupported_order_type_rejects()
    {
        var r = CtpOrderRequestMapper.Map(Input(+1m, type: OrderType.StopMarket));

        Assert.NotNull(r.Error);
        Assert.Contains("不支持的订单类型", r.Error);
    }

    [Fact]
    public void Direction_mismatch_rejects()
    {
        // 数量正（买）但 Direction 声明 Sell → 拒绝（防映射错乱）
        var r = CtpOrderRequestMapper.Map(Input(+1m, dir: OrderDirection.Sell));

        Assert.NotNull(r.Error);
    }
}
