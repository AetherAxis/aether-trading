using Ctp.Net;
using Aether.Trading.Runtime.Lean;
using QuantConnect;
using Xunit;

namespace Aether.Trading.Tests;

/// <summary>P2 持仓回填映射 + 持仓快照缓存单测：方向符号、零仓过滤、未订阅跳过、均价 fallback、Trade 增量维护。
/// <para>（2026-10-23 迁移适配：ApplyTrade 改显式传 direction/offsetFlag——⑤ 成交推送的方向/开平槽为捕获
/// verbatim 恒 BuyOpen（2026-10-20 双 bot 根因）；测试传 CtpTrade 自身字段即等价钉原语义。）</para></summary>
public class CtpAccountMappersTests
{
    private static readonly Symbol AgSymbol = Symbol.Create("ag2610", SecurityType.Base, Market.USA);

    private static CtpPosition Position(string id = "ag2610", string exchange = "SHFE",
        CtpPosiDirection dir = CtpPosiDirection.Long, int total = 3, int today = 1, int yd = 2) => new()
    {
        InstrumentId = id,
        ExchangeId = exchange,
        PosiDirection = dir,
        TotalPosition = total,
        TodayPosition = today,
        YdPosition = yd,
    };

    [Fact]
    public void Long_maps_positive_short_maps_negative()
    {
        var (holdings, skipped) = CtpAccountMappers.ToHoldings(
            new[] { Position(dir: CtpPosiDirection.Long, total: 3), Position(id: "rb2510", dir: CtpPosiDirection.Short, total: 2) },
            id => id == "ag2610" ? AgSymbol : Symbol.Create(id, SecurityType.Base, Market.USA),
            _ => 7001m);

        Assert.Empty(skipped);
        Assert.Equal(2, holdings.Count);
        Assert.Equal(3, holdings[0].Quantity);      // 多 → +
        Assert.Equal(-2, holdings[1].Quantity);     // 空 → −
    }

    [Fact]
    public void Zero_position_and_unsubscribed_are_filtered()
    {
        var (holdings, skipped) = CtpAccountMappers.ToHoldings(
            new[] { Position(total: 0), Position(id: "rb2510", total: 5) },
            id => id == "ag2610" ? AgSymbol : null,   // rb2510 未订阅
            _ => 0m);

        Assert.Empty(holdings);                       // 零仓过滤 + 未订阅跳过
        Assert.Equal(new[] { "rb2510" }, skipped);    // 未订阅的进 skipped 供 log
    }

    [Fact]
    public void Average_price_uses_fallback_chain()
    {
        var (holdings, _) = CtpAccountMappers.ToHoldings(
            new[] { Position() },
            _ => AgSymbol,
            id => id == "ag2610" ? 7001m : 0m);

        Assert.Equal(7001m, holdings[0].AveragePrice);
    }

    [Fact]
    public void Cache_replace_aggregates_long_short_records()
    {
        var cache = new CtpPositionCache();
        cache.Replace(new[]
        {
            Position(dir: CtpPosiDirection.Long, total: 5, today: 2, yd: 3),
            Position(dir: CtpPosiDirection.Short, total: 1, today: 1, yd: 0),
        });

        var snap = cache.Get("ag2610");
        Assert.NotNull(snap);
        Assert.Equal("SHFE", snap.ExchangeId);
        Assert.Equal(4, snap.Net);                    // 5 多 − 1 空
        Assert.Equal(3, snap.LongYd);
        Assert.Equal(2, snap.LongToday);
        Assert.Equal(1, snap.ShortToday);
        Assert.False(cache.IsStale(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Cache_is_stale_after_interval()
    {
        var cache = new CtpPositionCache();
        Assert.True(cache.IsStale(TimeSpan.FromSeconds(2)));    // 从未刷新 → stale
        cache.Replace(Array.Empty<CtpPosition>());
        Assert.False(cache.IsStale(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ApplyTrade_open_long_adds_today_long()
    {
        var cache = new CtpPositionCache();
        cache.Replace(new[] { Position(dir: CtpPosiDirection.Long, total: 2, today: 0, yd: 2) });

        var trade = new CtpTrade
        {
            InstrumentId = "ag2610", Direction = CtpDirection.Buy,
            OffsetFlag = CtpOffsetFlag.Open, Volume = 3, Price = 7001,
        };
        cache.ApplyTrade(trade, trade.Direction, trade.OffsetFlag);

        var snap = cache.Get("ag2610")!;
        Assert.Equal(5, snap.LongTotal);
        Assert.Equal(3, snap.LongToday);
        Assert.Equal(2, snap.LongYd);
    }

    [Fact]
    public void ApplyTrade_close_yesterday_reduces_yd_only()
    {
        var cache = new CtpPositionCache();
        cache.Replace(new[] { Position(dir: CtpPosiDirection.Long, total: 5, today: 2, yd: 3) });

        var trade = new CtpTrade
        {
            InstrumentId = "ag2610", Direction = CtpDirection.Sell,
            OffsetFlag = CtpOffsetFlag.CloseYesterday, Volume = 2, Price = 7001,
        };
        cache.ApplyTrade(trade, trade.Direction, trade.OffsetFlag);

        var snap = cache.Get("ag2610")!;
        Assert.Equal(3, snap.LongTotal);
        Assert.Equal(1, snap.LongYd);                 // 只减昨仓
        Assert.Equal(2, snap.LongToday);
    }

    [Fact]
    public void ApplyTrade_plain_close_reduces_today_first()
    {
        var cache = new CtpPositionCache();
        cache.Replace(new[] { Position(dir: CtpPosiDirection.Long, total: 5, today: 2, yd: 3) });

        var trade = new CtpTrade
        {
            InstrumentId = "ag2610", Direction = CtpDirection.Sell,
            OffsetFlag = CtpOffsetFlag.Close, Volume = 3, Price = 7001,
        };
        cache.ApplyTrade(trade, trade.Direction, trade.OffsetFlag);

        var snap = cache.Get("ag2610")!;
        Assert.Equal(2, snap.LongTotal);
        Assert.Equal(0, snap.LongToday);              // 先扣今 2
        Assert.Equal(2, snap.LongYd);                 // 再扣昨 1
    }

    [Fact]
    public void ApplyTrade_close_short_reduces_short_side()
    {
        var cache = new CtpPositionCache();
        cache.Replace(new[] { Position(dir: CtpPosiDirection.Short, total: 4, today: 1, yd: 3) });

        var trade = new CtpTrade
        {
            InstrumentId = "ag2610", Direction = CtpDirection.Buy,
            OffsetFlag = CtpOffsetFlag.Close, Volume = 2, Price = 7001,
        };
        cache.ApplyTrade(trade, trade.Direction, trade.OffsetFlag);

        var snap = cache.Get("ag2610")!;
        Assert.Equal(2, snap.ShortTotal);
        Assert.Equal(0, snap.ShortToday);
        Assert.Equal(2, snap.ShortYd);
    }

    [Fact]
    public void ApplyTrade_unknown_instrument_is_ignored()
    {
        var cache = new CtpPositionCache();
        cache.Replace(Array.Empty<CtpPosition>());
        var trade = new CtpTrade { InstrumentId = "zzz", Direction = CtpDirection.Buy, OffsetFlag = CtpOffsetFlag.Open, Volume = 1 };
        cache.ApplyTrade(trade, trade.Direction, trade.OffsetFlag);
        Assert.Null(cache.Get("zzz"));
    }
}
