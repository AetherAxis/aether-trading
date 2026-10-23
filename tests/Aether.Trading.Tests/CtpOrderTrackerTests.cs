using Ctp.Net;
using Aether.Trading.Runtime.Lean;
using QuantConnect;
using QuantConnect.Orders;
using Xunit;

namespace Aether.Trading.Tests;

/// <summary>P2 报单跟踪表 + CTP 状态码映射单测：OrderRef↔Lean 映射、TradeId 去重、终态旗标原子仲裁、watchdog 幽灵腿检测。</summary>
public class CtpOrderTrackerTests
{
    private static Order NewOrder(int id, decimal quantity = 2m)
    {
        var order = new MarketOrder(Symbol.Create("ag2610", SecurityType.Base, Market.USA), quantity, DateTime.UtcNow);
        typeof(Order).GetProperty("Id")!.SetValue(order, id);   // Id 是 internal set——测试经反射赋值
        return order;
    }

    private static CtpOrderRequest Req(int volume = 2) => new()
    {
        BrokerId = "9999", InvestorId = "u", InstrumentId = "ag2610",
        Direction = CtpDirection.Buy, OffsetFlag = CtpOffsetFlag.Open,
        PriceType = CtpOrderPriceType.Limit, LimitPrice = 7002, Volume = volume,
    };

    [Fact]
    public void CtpStatusMapper_maps_all_known_codes()
    {
        Assert.Equal((OrderStatus.Filled, true), AsTuple('0'));
        Assert.Equal((OrderStatus.PartiallyFilled, false), AsTuple('1'));
        Assert.Equal((OrderStatus.Canceled, true), AsTuple('2'));
        Assert.Equal((OrderStatus.Submitted, false), AsTuple('3'));
        Assert.Equal((OrderStatus.Canceled, true), AsTuple('4'));
        Assert.Equal((OrderStatus.Canceled, true), AsTuple('5'));
        Assert.Equal((OrderStatus.Submitted, false), AsTuple('a'));
        Assert.Equal((OrderStatus.Submitted, false), AsTuple('b'));
        Assert.Equal((OrderStatus.Submitted, false), AsTuple('c'));
        // 未知码 → null（仅记录）
        var unknown = CtpStatusMapper.Map((byte)'z');
        Assert.Null(unknown.Status);
        Assert.False(unknown.Terminal);

        static (OrderStatus, bool) AsTuple(char c)
        {
            var m = CtpStatusMapper.Map((byte)c);
            return (m.Status!.Value, m.Terminal);
        }
    }

    [Fact]
    public void Add_and_lookup_roundtrip()
    {
        var tracker = new CtpOrderTracker();
        var order = NewOrder(7);
        tracker.Add(order, new[] { Req() }, new[] { "000000000001" }, frontId: 1, sessionId: 2);

        Assert.True(tracker.TryGetByOrderRef("000000000001", out var child, out var tracked));
        Assert.Equal(7, child.LeanOrderId);
        Assert.Same(order, tracked.Order);
        Assert.True(tracker.TryGetByLeanId(7, out var byId));
        Assert.Same(order, byId.Order);
        Assert.False(tracker.TryGetByOrderRef("nope", out _, out _));
    }

    [Fact]
    public void RecordTrade_dedupes_by_trade_id()
    {
        var tracker = new CtpOrderTracker();
        tracker.Add(NewOrder(1), new[] { Req(2) }, new[] { "r1" }, 1, 2);

        Assert.True(tracker.RecordTrade("r1", "t1", 1, out var f1));
        Assert.Equal(1, f1);
        // 同一 TradeId 重放 → 拒绝
        Assert.False(tracker.RecordTrade("r1", "t1", 1, out _));
        // 新 TradeId → 累计
        Assert.True(tracker.RecordTrade("r1", "t2", 1, out var f2));
        Assert.Equal(2, f2);
        // 未知 OrderRef → 拒绝
        Assert.False(tracker.RecordTrade("unknown", "t3", 1, out _));
    }

    [Fact]
    public void MarkReport_sets_terminal_and_status()
    {
        var tracker = new CtpOrderTracker();
        tracker.Add(NewOrder(1), new[] { Req() }, new[] { "r1" }, 1, 2);

        tracker.MarkReport("r1", (byte)'3');   // 未成交排队 → 非终态
        Assert.True(tracker.TryGetByOrderRef("r1", out var child, out _));
        Assert.True(child.FirstReportSeen);
        Assert.False(child.Terminal);

        tracker.MarkReport("r1", (byte)'0');   // 全成 → 终态
        Assert.True(child.Terminal);
        Assert.Equal(OrderStatus.Filled, child.LastStatus);
    }

    [Fact]
    public void AllChildrenTerminal_requires_every_leg()
    {
        var tracker = new CtpOrderTracker();
        // SHFE 拆腿：两腿请求
        var leg1 = Req(3);
        var leg2 = Req(1);
        tracker.Add(NewOrder(1), new[] { leg1, leg2 }, new[] { "r1", "r2" }, 1, 2);

        tracker.MarkReport("r1", (byte)'0');
        Assert.True(tracker.TryGetByLeanId(1, out var tracked));
        Assert.False(tracker.AllChildrenTerminal(tracked));    // 一腿终态一腿未决

        tracker.MarkReport("r2", (byte)'0');
        Assert.True(tracker.AllChildrenTerminal(tracked));
    }

    [Fact]
    public void Terminal_flags_are_arbitrated_atomically()
    {
        var tracker = new CtpOrderTracker();
        tracker.Add(NewOrder(1), new[] { Req() }, new[] { "r1" }, 1, 2);
        tracker.TryGetByLeanId(1, out var tracked);

        // 首个到达者获准，第二个（竞态方）被拒
        Assert.True(tracker.TryMarkFilled(tracked));
        Assert.False(tracker.TryMarkCanceled(tracked));
        Assert.False(tracker.TryMarkFilled(tracked));
    }

    [Fact]
    public void StaleChildren_finds_unreported_legs_only()
    {
        var tracker = new CtpOrderTracker();
        tracker.Add(NewOrder(1), new[] { Req(), Req(1) }, new[] { "r1", "r2" }, 1, 2);

        // 刚创建 → 未超龄
        Assert.Empty(tracker.StaleChildren(TimeSpan.FromSeconds(10)));

        tracker.MarkReport("r1", (byte)'3');   // r1 已有首报

        // 模拟超龄：把 r2 创建时间拨回 11s 前（经反射改内部状态不可行——改用零容差测试）
        var stale = tracker.StaleChildren(TimeSpan.Zero);   // 零容差 → 所有无首报腿都算 stale
        Assert.Single(stale);
        Assert.Equal("r2", stale[0].Child.OrderRef);
    }

    [Fact]
    public void Watchdog_mark_is_atomic_against_late_report()
    {
        var tracker = new CtpOrderTracker();
        tracker.Add(NewOrder(1), new[] { Req() }, new[] { "r1" }, 1, 2);

        // 无首报 → watchdog 判无效成功
        Assert.True(tracker.TryMarkWatchdogInvalid("r1"));
        // 回报抢先到达的场景 → 第二次判无效失败（防幽灵事件）
        Assert.False(tracker.TryMarkWatchdogInvalid("r1"));

        // 已有首报的单 → watchdog 不碰
        tracker.Add(NewOrder(2), new[] { Req() }, new[] { "r2" }, 1, 2);
        tracker.MarkReport("r2", (byte)'3');
        Assert.False(tracker.TryMarkWatchdogInvalid("r2"));
    }

    [Fact]
    public void SetExchangeId_fills_once()
    {
        var tracker = new CtpOrderTracker();
        tracker.Add(NewOrder(1), new[] { Req() }, new[] { "r1" }, 1, 2);

        tracker.SetExchangeId("r1", "SHFE");
        tracker.SetExchangeId("r1", "XXXX");   // 第二次不覆盖
        tracker.TryGetByOrderRef("r1", out var child, out _);
        Assert.Equal("SHFE", child.ExchangeId);
    }
}
