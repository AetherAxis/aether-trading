using QuantConnect;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Statistics;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// Lean 2.5.18193 <see cref="ITradeBuilder"/> 的加锁包装——修实盘线程安全竞态。
/// 默认 <see cref="TradeBuilder"/> 的 <c>SetMarketPrice</c>（算法线程：AlgorithmManager 数据循环逐笔迭代
/// PendingTrades 更新回撤）与 <c>ProcessFill</c>（成交线程：transaction handler 处理 OrderEvent 增删同一
/// 列表）<b>无锁并发</b>——高频成交下 <c>PendingTrades[i]</c> 读到并发 Add 未赋值的槽位返回 null →
/// NullReferenceException → 引擎 RuntimeError 直接退出。
/// 2026-10-21 实测：MM 高频成交（~3 笔/秒）85 秒即死，堆栈钉死
/// <c>QuantConnect.Statistics.TradeBuilder.SetMarketPrice</c>；回测单线程无此问题，纯实盘病。
/// <see cref="LeanAlgorithmBase"/> 构造器以本类替换默认实例（<c>QCAlgorithm.SetTradeBuilder</c>）。
/// 全公共方法经同一把锁串行化——统计路径量级微小（每笔 fill/tick 一次字典与短列表操作），锁竞争可忽略。
/// </summary>
public sealed class LockedTradeBuilder : ITradeBuilder
{
    private readonly TradeBuilder _inner;
    private readonly object _gate = new();

    public LockedTradeBuilder(FillGroupingMethod groupingMethod, FillMatchingMethod matchingMethod)
        => _inner = new TradeBuilder(groupingMethod, matchingMethod);

    public List<Trade> ClosedTrades { get { lock (_gate) return _inner.ClosedTrades; } }

    public bool HasOpenPosition(Symbol symbol) { lock (_gate) return _inner.HasOpenPosition(symbol); }

    public void SetSecurityManager(SecurityManager securities) { lock (_gate) _inner.SetSecurityManager(securities); }

    public void SetLiveMode(bool live) { lock (_gate) _inner.SetLiveMode(live); }

    public void SetMarketPrice(Symbol symbol, decimal price) { lock (_gate) _inner.SetMarketPrice(symbol, price); }

    public void ApplySplit(Split split, bool liveMode, DataNormalizationMode dataNormalizationMode)
    { lock (_gate) _inner.ApplySplit(split, liveMode, dataNormalizationMode); }

    public void ProcessFill(OrderEvent fill, decimal securityConversionRate, decimal feeInAccountCurrency, decimal multiplier = 1.0m)
    { lock (_gate) _inner.ProcessFill(fill, securityConversionRate, feeInAccountCurrency, multiplier); }
}
