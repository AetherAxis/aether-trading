using Ctp.Net;
using QuantConnect;
using QuantConnect.Orders;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// CTP 持仓 → Lean <see cref="Holding"/> 纯映射 + 持仓快照缓存。
/// 持仓回填只回填算法已订阅的合约——CtpBar 是 SecurityType.Base（不在 Lean _supportedSecurityTypes），
/// 未订阅合约的持仓进 GetOrAddUnrequestedSecurity 会让引擎 setup 直接失败，必须跳过（期货页手动管理）。
/// </summary>
public static class CtpAccountMappers
{
    /// <summary>
    /// CtpPosition 列表 → Holding 列表。symbolLookup=instrumentId → 算法已订阅 Symbol（大小写不敏感匹配，
    /// 由 brokerage 从 _algorithm.Securities 提供）；priceFallback=近似均价（CtpPosition 无均价字段——用最新价，
    /// 只影响 Lean 内部盈亏显示）。未匹配/零仓的合约进 skipped（调用方 log）。
    /// </summary>
    public static (List<Holding> Holdings, List<string> Skipped) ToHoldings(
        IReadOnlyList<CtpPosition> positions,
        Func<string, Symbol?> symbolLookup,
        Func<string, decimal> priceFallback)
    {
        var holdings = new List<Holding>();
        var skipped = new List<string>();
        foreach (var p in positions)
        {
            if (p.TotalPosition == 0) continue;
            var symbol = symbolLookup(p.InstrumentId);
            if (symbol == null)
            {
                skipped.Add(p.InstrumentId);
                continue;
            }

            // PosiDirection: Long=净多记录 / Short=净空记录；Net（极少见）按正负归边（防御）
            decimal quantity = p.PosiDirection switch
            {
                CtpPosiDirection.Long => p.TotalPosition,
                CtpPosiDirection.Short => -p.TotalPosition,
                _ => p.TotalPosition >= 0 ? p.TotalPosition : -p.TotalPosition,
            };
            if (quantity == 0) continue;

            holdings.Add(new Holding
            {
                Symbol = symbol,
                Quantity = quantity,
                AveragePrice = priceFallback(p.InstrumentId),
            });
        }
        return (holdings, skipped);
    }
}

/// <summary>
/// 持仓快照缓存（offset 决策的数据源）。启动全量 Replace（QueryPositionAsync）；
/// 平时 Trade 回报增量维护；PlaceOrder 时节流刷新（CTP 查询流控 ~1 次/秒）。
/// </summary>
public sealed class CtpPositionCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CtpPositionSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public CtpPositionSnapshot? Get(string instrumentId)
    {
        lock (_lock)
        {
            return _snapshots.TryGetValue(instrumentId, out var s) ? s : null;
        }
    }

    public bool IsStale(TimeSpan maxAge)
    {
        lock (_lock)
        {
            return DateTime.UtcNow - _lastRefreshUtc > maxAge;
        }
    }

    /// <summary>全量替换（QueryPositionAsync 结果）。CTP 按合约×方向返回多条记录，聚合成净额快照。</summary>
    public void Replace(IEnumerable<CtpPosition> positions)
    {
        var aggregated = new Dictionary<string, (int LongT, int LongToday, int LongYd, int ShortT, int ShortToday, int ShortYd, string Exchange)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in positions)
        {
            if (p.TotalPosition == 0) continue;
            if (!aggregated.TryGetValue(p.InstrumentId, out var entry))
                entry = (0, 0, 0, 0, 0, 0, p.ExchangeId);
            if (p.PosiDirection == CtpPosiDirection.Short)
                aggregated[p.InstrumentId] = (entry.LongT, entry.LongToday, entry.LongYd,
                    entry.ShortT + p.TotalPosition, entry.ShortToday + p.TodayPosition, entry.ShortYd + p.YdPosition, p.ExchangeId);
            else
                aggregated[p.InstrumentId] = (entry.LongT + p.TotalPosition, entry.LongToday + p.TodayPosition, entry.LongYd + p.YdPosition,
                    entry.ShortT, entry.ShortToday, entry.ShortYd, p.ExchangeId);
        }

        lock (_lock)
        {
            _snapshots.Clear();
            foreach (var (id, a) in aggregated)
                _snapshots[id] = new CtpPositionSnapshot(a.Exchange, a.LongT, a.LongToday, a.LongYd, a.ShortT, a.ShortToday, a.ShortYd);
            _lastRefreshUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Trade 回报增量维护。Open 加对应方向今仓；Close* 减对侧（CloseToday→今、CloseYesterday→昨、Close→先今后昨）。</summary>
    public void ApplyTrade(CtpTrade trade)
    {
        lock (_lock)
        {
            if (!_snapshots.TryGetValue(trade.InstrumentId, out var s)) return;   // 未缓存合约（引擎未交易过）不维护
            var (ex, lt, lTo, lYd, st, sTo, sYd) = (s.ExchangeId, s.LongTotal, s.LongToday, s.LongYd, s.ShortTotal, s.ShortToday, s.ShortYd);
            var vol = trade.Volume;

            if (trade.OffsetFlag == CtpOffsetFlag.Open)
            {
                if (trade.Direction == CtpDirection.Buy) { lt += vol; lTo += vol; }
                else { st += vol; sTo += vol; }
            }
            else
            {
                // 平多=卖、平空=买；Close 减仓先扣今再扣昨（非 SHFE 交易所自动分配，本地近似）
                if (trade.Direction == CtpDirection.Sell)   // 平多
                {
                    lt -= vol;
                    if (trade.OffsetFlag == CtpOffsetFlag.CloseYesterday) lYd = Math.Max(0, lYd - vol);
                    else if (trade.OffsetFlag == CtpOffsetFlag.CloseToday) lTo = Math.Max(0, lTo - vol);
                    else
                    {
                        var fromToday = Math.Min(lTo, vol);
                        lTo -= fromToday;
                        lYd = Math.Max(0, lYd - (vol - fromToday));
                    }
                }
                else                                            // 平空
                {
                    st -= vol;
                    if (trade.OffsetFlag == CtpOffsetFlag.CloseYesterday) sYd = Math.Max(0, sYd - vol);
                    else if (trade.OffsetFlag == CtpOffsetFlag.CloseToday) sTo = Math.Max(0, sTo - vol);
                    else
                    {
                        var fromToday = Math.Min(sTo, vol);
                        sTo -= fromToday;
                        sYd = Math.Max(0, sYd - (vol - fromToday));
                    }
                }
            }

            _snapshots[trade.InstrumentId] = new CtpPositionSnapshot(ex, lt, lTo, lYd, st, sTo, sYd);
        }
    }
}
