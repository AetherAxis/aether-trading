using Ctp.Net;
using QuantConnect.Orders;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// CTP 报单状态码（THOST_FTDC_OST_*，char 码强转 byte）→ Lean <see cref="OrderStatus"/> 映射。
/// '0'全部成交 '1'部分成交排队 '2'部分成交不排队(IOC 残余撤销) '3'未成交排队 '4'未成交不排队(被拒/已撤)
/// '5'撤单 'a'未知(在途首报) 'b'未触发 'c'已触发。
/// </summary>
public static class CtpStatusMapper
{
    public readonly record struct Mapped(OrderStatus? Status, bool Terminal);

    public static Mapped Map(byte raw) => raw switch
    {
        (byte)'0' => new(OrderStatus.Filled, true),
        (byte)'1' => new(OrderStatus.PartiallyFilled, false),
        (byte)'2' => new(OrderStatus.Canceled, true),
        (byte)'3' => new(OrderStatus.Submitted, false),
        (byte)'4' => new(OrderStatus.Canceled, true),
        (byte)'5' => new(OrderStatus.Canceled, true),
        (byte)'a' or (byte)'b' or (byte)'c' => new(OrderStatus.Submitted, false),
        _ => new(null, false),
    };
}

/// <summary>
/// Lean Order ↔ CTP 报单跟踪表（P2 OrderStateManager）。一 Lean 单可拆 1..2 CTP 子单（SHFE 平昨+平今拆腿）。<para/>
/// 线程安全：CTP 回报在 SDK 回调线程、PlaceOrder 在 transaction-handler 线程并发到达，全部经 lock。
/// 只管状态，不发事件——事件发射（OnOrderEvent）留在 brokerage，保持本类可单测。
/// </summary>
public sealed class CtpOrderTracker
{
    /// <summary>一腿 CTP 子单。</summary>
    public sealed class Child
    {
        public string OrderRef = "";
        public int LeanOrderId;
        public CtpOrderRequest Request = null!;
        public int FrontId, SessionId;
        public string ExchangeId = "";
        public int VolumeTraded;          // 本腿累计成交（Trade 回报累加）
        public bool Terminal;             // 终态（'0'/'2'/'4'/'5' 或 watchdog 判无效）
        public OrderStatus? LastStatus;   // 最近一次回报的 Lean 状态（终态聚合判断用）
        public bool Rejected;             // 原始码 '4'（未成交被拒）——零成交终态时区分 Invalid vs Canceled
        public bool FirstReportSeen;      // 收到首次报单回报（watchdog 据此判幽灵单）
        public DateTime CreatedUtc;
    }

    /// <summary>一个 Lean Order 的聚合跟踪。</summary>
    public sealed class Tracked
    {
        public Order Order = null!;
        public List<Child> Children = new();
        public int ExpectedVolume;                    // 全腿预期总量（= Lean 单映射到 CTP 的手数和——封顶后可能 < |Order.Quantity|）
        public decimal Filled;                        // 全腿累计成交（绝对值）
        public bool FilledEmitted, CanceledEmitted;   // 终态事件防双发（末笔 Trade 与 '0' 回报竞态）
        public HashSet<string> SeenTradeIds = new();  // CTP 成交回报重放去重
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Child> _byOrderRef = new();
    private readonly Dictionary<int, Tracked> _byLeanId = new();

    public void Add(Order order, IReadOnlyList<CtpOrderRequest> requests, IReadOnlyList<string> orderRefs, int frontId, int sessionId)
    {
        if (requests.Count != orderRefs.Count)
            throw new InvalidOperationException($"报单请求/OrderRef 数量不一致: {requests.Count} vs {orderRefs.Count}");
        lock (_lock)
        {
            var tracked = new Tracked { Order = order, ExpectedVolume = requests.Sum(r => r.Volume) };
            for (var i = 0; i < orderRefs.Count; i++)
            {
                var child = new Child
                {
                    OrderRef = orderRefs[i],
                    LeanOrderId = order.Id,
                    Request = requests[i],
                    FrontId = frontId,
                    SessionId = sessionId,
                    CreatedUtc = DateTime.UtcNow,
                };
                tracked.Children.Add(child);
                _byOrderRef[child.OrderRef] = child;
            }
            _byLeanId[order.Id] = tracked;
        }
    }

    public bool TryGetByOrderRef(string orderRef, out Child child, out Tracked tracked)
    {
        lock (_lock)
        {
            if (_byOrderRef.TryGetValue(orderRef, out child!))
            {
                tracked = _byLeanId[child.LeanOrderId];
                return true;
            }
            child = null!;
            tracked = null!;
            return false;
        }
    }

    public bool TryGetByLeanId(int leanOrderId, out Tracked tracked)
    {
        lock (_lock)
        {
            return _byLeanId.TryGetValue(leanOrderId, out tracked!);
        }
    }

    /// <summary>记录一笔成交（TradeId 去重）。返回 false=未知 OrderRef 或重复 TradeId（调用方跳过）；filledOut=该 Lean 单累计成交绝对量。</summary>
    public bool RecordTrade(string orderRef, string tradeId, int volume, out decimal filledOut)
    {
        lock (_lock)
        {
            filledOut = 0;
            if (!_byOrderRef.TryGetValue(orderRef, out var child)) return false;
            var tracked = _byLeanId[child.LeanOrderId];
            if (!tracked.SeenTradeIds.Add(tradeId)) return false;
            child.VolumeTraded += volume;
            tracked.Filled += volume;
            filledOut = tracked.Filled;
            return true;
        }
    }

    /// <summary>标记收到报单回报：首报置 FirstReportSeen、记录最近状态、终态置 Terminal。volumeTraded 由 Trade 回报维护，此处不覆盖。</summary>
    public void MarkReport(string orderRef, byte statusRaw)
    {
        var mapped = CtpStatusMapper.Map(statusRaw);
        lock (_lock)
        {
            if (!_byOrderRef.TryGetValue(orderRef, out var child)) return;
            child.FirstReportSeen = true;
            if (mapped.Status != null) child.LastStatus = mapped.Status;
            if (statusRaw == (byte)'4') child.Rejected = true;
            if (mapped.Terminal) child.Terminal = true;
        }
    }

    /// <summary>watchdog 原子标记：仍无首报且未终态 → 判 Invalid 终态。返回 false=已被正常回报抢先（勿发幽灵事件）。</summary>
    public bool TryMarkWatchdogInvalid(string orderRef)
    {
        lock (_lock)
        {
            if (!_byOrderRef.TryGetValue(orderRef, out var child)) return false;
            if (child.FirstReportSeen || child.Terminal) return false;
            child.FirstReportSeen = true;
            child.Terminal = true;
            child.LastStatus = OrderStatus.Invalid;
            return true;
        }
    }

    /// <summary>终态事件发射资格（原子）：首个到达者获准（防末笔 Trade 与 '0' 回报竞态双发）。</summary>
    public bool TryMarkFilled(Tracked tracked)
    {
        lock (_lock)
        {
            if (tracked.FilledEmitted || tracked.CanceledEmitted) return false;
            tracked.FilledEmitted = true;
            return true;
        }
    }

    /// <summary>终态事件发射资格（原子）：同 <see cref="TryMarkFilled"/>。</summary>
    public bool TryMarkCanceled(Tracked tracked)
    {
        lock (_lock)
        {
            if (tracked.FilledEmitted || tracked.CanceledEmitted) return false;
            tracked.CanceledEmitted = true;
            return true;
        }
    }

    /// <summary>回填 ExchangeId（撤单 CtpOrderRef 需要；CTP 回报里带，首次回报时写入）。</summary>
    public void SetExchangeId(string orderRef, string exchangeId)
    {
        lock (_lock)
        {
            if (_byOrderRef.TryGetValue(orderRef, out var child) && string.IsNullOrEmpty(child.ExchangeId))
                child.ExchangeId = exchangeId;
        }
    }

    public bool AllChildrenTerminal(Tracked tracked)
    {
        lock (_lock)
        {
            return tracked.Children.Count > 0 && tracked.Children.All(c => c.Terminal);
        }
    }

    /// <summary>watchdog 用：下单后迟迟未收到首次回报的幽灵腿（前置静默拒单不可见——ICtpSession 不上浮 OnRspOrderInsert）。</summary>
    public List<(Child Child, Tracked Tracked)> StaleChildren(TimeSpan maxAge)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            return _byOrderRef.Values
                .Where(c => !c.FirstReportSeen && !c.Terminal && now - c.CreatedUtc > maxAge)
                .Select(c => (c, _byLeanId[c.LeanOrderId]))
                .ToList();
        }
    }
}
