using Ctp.Net;
using QuantConnect.Orders;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// CTP 持仓快照（净额形态，供 offset 决策）。CTP 按多/空方向分别记录今昨仓——快照聚合后提供 <see cref="Net"/>。
/// </summary>
public sealed record CtpPositionSnapshot(
    string ExchangeId,
    int LongTotal, int LongToday, int LongYd,
    int ShortTotal, int ShortToday, int ShortYd)
{
    /// <summary>净持仓：正=净多，负=净空，0=空仓或锁仓对冲后净零。</summary>
    public int Net => LongTotal - ShortTotal;

    public bool IsFlat => LongTotal == 0 && ShortTotal == 0;
}

/// <summary>
/// Lean Order → CtpOrderRequest 纯映射（P2 真实下单核心决策）。
/// 入参全部注入（无 session/网关依赖），可脱离 CTP 单测。<para/>
/// 规则（用户拍板）：
/// ①市价单（Market/MarketOnOpen）→ 对手价限价单（买 Ask/卖 Bid，fallback LastPrice→PreSettlement→拒单）；
/// ②开仓封顶 <see cref="MapInput.MaxOrderVolume"/>（保险丝），平仓绝不封顶；
/// ③平仓 offset：SHFE/INE 平昨优先（CloseYesterday 不足再 CloseToday），其他交易所直接 Close；
/// ④反转单（平仓+开仓混合）拒绝——本仓策略只走 Long↔Flat，不应出现。
/// </summary>
public static class CtpOrderRequestMapper
{
    /// <summary>上期所系（须显式平今/平昨）。</summary>
    private static bool IsShfeFamily(string exchangeId)
        => exchangeId.Equals("SHFE", StringComparison.OrdinalIgnoreCase)
           || exchangeId.Equals("INE", StringComparison.OrdinalIgnoreCase);

    public sealed record MapInput(
        OrderDirection Direction,
        OrderType OrderType,
        decimal LimitPrice,          // Limit 单用（order.Price）
        decimal Quantity,            // 有符号：正=买、负=卖
        string InstrumentId,
        CtpPositionSnapshot? Position,
        CtpTick? LatestTick,
        string BrokerId,
        string InvestorId,
        int MaxOrderVolume,
        bool PostOnly = false,      // Tag "post-only" ⇒ TC='4'（BTC 对标 2026-10-05，仅限价单）
        double PriceTick = 0.01,    // 市价转对手价限价的跨越余量（见 Map 内注释）
        int RestingCloseVolume = 0); // 本账户该合约该方向**已挂在簿上**的平仓手数（交易所预留的可平量，见 Map 内注释）

    public sealed record MapResult(IReadOnlyList<CtpOrderRequest> Requests, string? Error);

    public static MapResult Map(MapInput input)
    {
        var empty = new MapResult(Array.Empty<CtpOrderRequest>(), null);

        // —— 数量：非零整数（CTP 手数）——
        var qty = input.Quantity;
        if (qty == 0 || qty != Math.Floor(qty))
            return new MapResult(Array.Empty<CtpOrderRequest>(), $"CTP 手数须为非零整数: {qty}");
        var vol = (int)Math.Abs(qty);
        var isBuy = qty > 0;
        if ((isBuy && input.Direction != OrderDirection.Buy) || (!isBuy && input.Direction != OrderDirection.Sell))
            return new MapResult(Array.Empty<CtpOrderRequest>(), $"方向不一致: Quantity={qty} Direction={input.Direction}");

        // —— 价格：市价（含 MOO——Base 类型不拦 MOO，当市价处理）→ 对手价限价；Limit → 指定价 ——
        double price;
        var timeCondition = CtpTimeCondition.GFD;
        switch (input.OrderType)
        {
            case OrderType.Market or OrderType.MarketOnOpen:
                var tick = input.LatestTick;
                if (tick == null)
                    return new MapResult(Array.Empty<CtpOrderRequest>(), "无最新行情（tick 为空），拒单——对手价限价单需要行情");
                price = isBuy ? tick.AskPrice : tick.BidPrice;
                if (price <= 0) price = tick.LastPrice;
                if (price <= 0) price = tick.PreSettlement;
                if (price <= 0)
                    return new MapResult(Array.Empty<CtpOrderRequest>(), "行情 Ask/Bid/Last/PreSettlement 均无效，拒单");
                // 跨越余量（2026-10-17 本地长跑定案）：wire 价格槽 3B 量化在 [64,128) 区间 ulp=1/64 ⇒
                // 对手价回显有 ≤半 ulp（≈0.0078）失真——买 99.96 量化成 99.953125 ⇒ 限价低于对手价
                // 永不交叉 ⇒ IOC 全作废 ⇒ 市场僵死（NT 吃不到 MM 挂单、无成交无 tick）。加 max(PriceTick,0.02)
                // 余量保证量化后仍跨越对手价；成交按 maker 挂单价（限价只是上限）——无滑点代价。
                var pad = Math.Max(input.PriceTick, 0.02);
                price = isBuy ? price + pad : price - pad;
                // 吃单语义：IOC——未交叉的残量由交易所立即作废（'2' 部分成交不在队列），不挂簿。
                // GFD 会把没吃到的"市价"单滞留成挂单（实测：噪声账户自己的买卖单同价互挂、自成交跳过、永不成交）。
                timeCondition = CtpTimeCondition.IOC;
                break;
            case OrderType.Limit:
                if (input.LimitPrice <= 0)
                    return new MapResult(Array.Empty<CtpOrderRequest>(), "限价单价格无效");
                price = (double)input.LimitPrice;
                break;
            default:
                return new MapResult(Array.Empty<CtpOrderRequest>(), $"不支持的订单类型: {input.OrderType}");
        }

        // post-only（BTC 对标 2026-10-05）：只入簿不吃单——交易所插入时会交叉即整单拒（90018），
        // 保证永远付 maker 费。仅限价单支持（与市价映射的 IOC 吃单语义互斥）。
        if (input.PostOnly)
        {
            if (input.OrderType != OrderType.Limit)
                return new MapResult(Array.Empty<CtpOrderRequest>(), "post-only 仅支持限价单");
            timeCondition = CtpTimeCondition.PostOnly;
        }

        var direction = isBuy ? CtpDirection.Buy : CtpDirection.Sell;
        var pos = input.Position;

        // —— Offset 决策 ——
        // 持仓快照缺失 / 空仓 / 净方向与订单同向（加仓）→ 开仓（封顶保险丝）
        if (pos == null || pos.IsFlat || (pos.Net > 0 && isBuy) || (pos.Net < 0 && !isBuy))
        {
            var capped = vol;
            if (input.MaxOrderVolume > 0 && capped > input.MaxOrderVolume)
                capped = input.MaxOrderVolume;
            if (capped <= 0)
                return new MapResult(Array.Empty<CtpOrderRequest>(), "手数上限封顶后为 0，拒单");
            return new MapResult(new[] { BuildRequest(input, direction, CtpOffsetFlag.Open, price, capped, timeCondition) }, null);
        }

        // —— 反向：平仓（绝不封顶）；超量 = 反转（平净仓 + 开超额，2026-09-28 起支持拆腿）——
        // Lean 策略以净仓位思考（持多 3 卖 5 = 净空 2）——映射到 CTP 须拆「平 3 + 开 2」两腿。
        // 旧版直接拒反转单：做市策略被动吃单攒出净仓后，反向报价腿全被拒 ⇒ 簿单边化、市场退化。
        var net = Math.Abs(pos.Net);

        // —— 可平量扣减（2026-10-31，做市商「平仓腿全被拒」的根因）——
        // 交易所对**在挂平仓腿是预留可平量的**（SimTradingCore.ReservedCloseVolume 把该方向所有在挂
        // 平仓单的 Remaining 全部计走）。做市商在净空 N 手时，每档买腿都映射成 BuyClose，第一档就
        // 占了 N 手里的 1 手，剩下的腿全部撞 `[90006] 无可平持仓`（实测 2.5 分钟 13 次，腿表涨到 38 条）。
        // 正解就一条：可平量 = |净仓| − 已在挂的平仓手数；扣到 0 就把这条腿**改开仓**。
        // 经济含义不变（净空下买入就是减敞口，开仓腿同样在减净头寸），但交易所不会预留、不会拒单；
        // 真做市商也是这么干的：两侧挂开仓腿报双边，净仓靠减仓腿慢慢收。
        var closable = Math.Max(0, net - Math.Max(0, input.RestingCloseVolume));
        var closeWanted = Math.Min(vol, closable);
        var reversalOpen = vol - closeWanted;
        if (reversalOpen > 0 && input.MaxOrderVolume > 0 && reversalOpen > input.MaxOrderVolume)
            reversalOpen = input.MaxOrderVolume;   // 开腿受保险丝封顶（平仓腿绝不封顶）；封顶后 actual<requested 有日志

        // 平多用 Long 侧记录，平空用 Short 侧
        var (yd, today) = pos.Net > 0 ? (pos.LongYd, pos.LongToday) : (pos.ShortYd, pos.ShortToday);
        var closeVolume = closeWanted;   // 不再按 |净仓| 报满：先扣掉已挂在簿上的平仓腿（见上）

        if (IsShfeFamily(pos.ExchangeId))
        {
            // 平昨优先：closeVolume <= Yd → 单腿 CloseYesterday；否则拆 CloseYesterday(Yd)+CloseToday(余)
            var ydUsed = Math.Min(closeVolume, yd);
            var todayUsed = closeVolume - ydUsed;
            if (todayUsed > today)
                return new MapResult(Array.Empty<CtpOrderRequest>(),
                    $"持仓今昨数据不一致（需平今 {todayUsed} > 实际今仓 {today}），拒单防错腿");

            var list = new List<CtpOrderRequest>(3);
            if (ydUsed > 0) list.Add(BuildRequest(input, direction, CtpOffsetFlag.CloseYesterday, price, ydUsed, timeCondition));
            if (todayUsed > 0) list.Add(BuildRequest(input, direction, CtpOffsetFlag.CloseToday, price, todayUsed, timeCondition));
            if (reversalOpen > 0) list.Add(BuildRequest(input, direction, CtpOffsetFlag.Open, price, reversalOpen, timeCondition));
            if (list.Count == 0)
                return new MapResult(Array.Empty<CtpOrderRequest>(), "平仓量计算为 0，拒单");
            return new MapResult(list, null);
        }

        // 非 SHFE 系（DCE/CZCE/CFFEX/GFEX/SIM）：直接 Close（交易所自动分配今昨）+ 反转开腿
        var requests = new List<CtpOrderRequest>(2);
        if (closeVolume > 0) requests.Add(BuildRequest(input, direction, CtpOffsetFlag.Close, price, closeVolume, timeCondition));
        if (reversalOpen > 0) requests.Add(BuildRequest(input, direction, CtpOffsetFlag.Open, price, reversalOpen, timeCondition));
        if (requests.Count == 0)
            return new MapResult(Array.Empty<CtpOrderRequest>(), "平仓量计算为 0，拒单");
        return new MapResult(requests, null);
    }

    private static CtpOrderRequest BuildRequest(MapInput input, CtpDirection direction, CtpOffsetFlag offset, double price, int volume,
        CtpTimeCondition timeCondition) => new()
    {
        BrokerId = input.BrokerId,
        InvestorId = input.InvestorId,
        InstrumentId = input.InstrumentId,
        Direction = direction,
        OffsetFlag = offset,
        PriceType = CtpOrderPriceType.Limit,
        LimitPrice = price,
        Volume = volume,
        TimeCondition = timeCondition,
    };
}
