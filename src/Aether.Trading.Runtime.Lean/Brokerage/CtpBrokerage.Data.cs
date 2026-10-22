using Ctp.Net;
using System.Globalization;
using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// <see cref="CtpBrokerage"/> 的行情 partial：实现 <see cref="QuantConnect.Interfaces.IDataQueueHandler"/>。
/// Subscribe 经 <c>_aggregator.Add</c> 拿 enumerator + <c>_subscriptionManager.Subscribe</c> 记账（首次触发 CTP 行情订阅），
/// 并捕获每个订阅的 Lean Symbol + 分辨率（建 <see cref="BarBuilder"/>）。
/// CTP <c>TickReceived</c> → 按 instrumentId 查捕获的 Symbol → BarBuilder 聚合成 <see cref="CtpBar"/> → <c>_aggregator.Update</c> 推算法。
/// 回测 CtpBar 走 CSV，实盘 CtpBar 走 tick 聚合——同一数据类型，算法无感知（Lean backtest=live）。
/// </summary>
public partial class CtpBrokerage
{
    /// <summary>instrumentId → Lean Symbol（Subscribe 捕获，大小写不敏感——Lean 可能大写 ticker，CTP 用原始大小写）。</summary>
    private readonly Dictionary<string, Symbol> _symbolByInstrument = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Lean Symbol → BarBuilder（按订阅分辨率聚合 tick→bar）。</summary>
    private readonly Dictionary<Symbol, BarBuilder> _barBuilders = new();

    public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
    {
        EnsureInitialized();
        if (_aggregator == null)
        {
            Log.Error("CtpBrokerage.Subscribe: aggregator 未就绪");
            return null!;
        }
        var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);

        // 先登记 Symbol/聚合器，再触发 CTP 订阅（镜像服务端订阅 op 的顺序修复）：
        // 订阅应答与订阅快照 tick 几乎同拍到达，快照在接收线程触发 OnTickReceived——
        // 若登记晚一拍（旧序：先 _subscriptionManager.Subscribe 再写字典），空闲市场的唯一一帧
        // 会被 TryGetValue 丢弃 ⇒ OnData 永不触发 ⇒ 策略不下单 ⇒ 市场自举死锁（2026-09-28 实测第二轮）。
        var sym = dataConfig.Symbol;
        _symbolByInstrument[sym.Value] = sym;
        _barBuilders[sym] = new BarBuilder(dataConfig.Increment);

        _subscriptionManager!.Subscribe(dataConfig);

        // 快照种子：CtpSessionService 增量订阅对已订合约是 no-op（不再发订阅帧 ⇒ 前置不再推订阅快照）——
        // 期货页行情板先于引擎订阅时，快照 tick 在本类挂 TickReceived 监听之前就到达并被消费。静态市场
        // （做市商挂单等噪声来吃——无新 TickProduced 事件）下策略 Price 恒 0「等待首帧行情」死锁
        // （2026-10-03 双 bot 实测：MM 靠 68s 后断线重建才自举，noise 无重建永等）。从会话最新 tick
        // 缓存回放一帧自举首个 bar；fresh 连接路径不受影响（快照在挂监听后经事件正常到达）。
        // Lean 大写 ticker（如 AG2610）→ CTP 原始 instrumentId（ag2610）——会话 tick 缓存按 CTP 大小写键控。
        var seed = _session.GetLatestTick(ResolveCtpInstrument(sym.Value));
        if (seed is not null)
            OnTickReceived(this, seed);

        CtpLeanGateway.Log($"[Data] 订阅 {sym.Value} 增量={dataConfig.Increment}{(seed is null ? "" : "（快照种子 last=" + seed.LastPrice + "）")}");
        return enumerator;
    }

    public void Unsubscribe(SubscriptionDataConfig dataConfig)
    {
        _subscriptionManager?.Unsubscribe(dataConfig);
        _aggregator?.Remove(dataConfig);
        _barBuilders.Remove(dataConfig.Symbol);
        CtpLeanGateway.Log($"[Data] 退订 {dataConfig.Symbol.Value}");
    }

    public void SetJob(LiveNodePacket job)
    {
        _job = job;
        EnsureInitialized();
        if (!IsConnected) Connect();
    }

    /// <summary>
    /// 实盘 tick 盖戳时钟（数据时区 = UTC，见 <see cref="LeanAlgorithmBase.AddCtpData"/>）：
    /// 用<b>客户端当前 UTC</b>，不用 host 的 UpdateTime/ActionDay（那是 host 本地墙钟——跨机器/跨时区时
    /// 与引擎 frontier 对不上，未来时间的数据会被 FrontierAwareEnumerator 扣住永不发射——第四层死锁）。
    /// internal 可注入：单测用固定时钟构造跨分钟边界。
    /// </summary>
    public Func<DateTime> LiveTickClock { get; set; } = static () => DateTime.UtcNow;

    /// <summary>CTP Tick 回报 → 查订阅 Symbol → BarBuilder 聚合 → 完成的 CtpBar 推 aggregator。逐跳采样日志（排障仪表）。</summary>
    private void OnTickReceived(object? sender, CtpTick tick)
    {
        LogTickSample(tick);
        if (_aggregator == null) return;
        if (!_symbolByInstrument.TryGetValue(tick.InstrumentId, out var symbol))
        {
            LogTickDrop(tick);   // 仪表：到达但无订阅登记（旧序竞态的指征——正常只出现在真正未订阅的合约上）
            return;
        }
        if (!_barBuilders.TryGetValue(symbol, out var builder)) return;
        try
        {
            var completed = builder.OnTick(LiveTickClock(), (decimal)tick.LastPrice, symbol);
            if (completed != null)
            {
                LogBarSample(tick.InstrumentId, completed);
                _aggregator.Update(completed);
            }
        }
        catch (Exception ex)
        {
            CtpLeanGateway.Log($"[Data] tick 聚合失败 {tick.InstrumentId}: {ex.Message}");
        }
    }

    private int _tickSampleCount;
    private void LogTickSample(CtpTick tick)
    {
        var n = System.Threading.Interlocked.Increment(ref _tickSampleCount);
        if (n <= 5 || n % 200 == 0)
            CtpLeanGateway.Log($"[Tick] {tick.InstrumentId} last={tick.LastPrice} bid={tick.BidPrice}/{tick.BidVolume} ask={tick.AskPrice}/{tick.AskVolume} (#{n})");
    }

    private int _tickDropCount;
    private void LogTickDrop(CtpTick tick)
    {
        var n = System.Threading.Interlocked.Increment(ref _tickDropCount);
        if (n <= 5)
            CtpLeanGateway.Log($"[Tick] 丢弃（无订阅登记）: {tick.InstrumentId} (#{n})——若为已订阅合约说明登记竞态未修复");
    }

    private int _barSampleCount;
    private void LogBarSample(string instrumentId, CtpBar bar)
    {
        var n = System.Threading.Interlocked.Increment(ref _barSampleCount);
        if (n <= 5)
            CtpLeanGateway.Log($"[Bar] {instrumentId} close={bar.Close} time={bar.Time:HH:mm:ss} (#{n})——首棒即自举成功");
    }

    // OnOrderReceived/OnTradeReceived（P2 报单/成交回报 → Lean OrderEvent 翻译）实现在 CtpBrokerage.Trading.cs
    // （旧 ParseTickTime——解析 host 墙钟——已删：跨机器/跨时区下与引擎 frontier 对不上，见 LiveTickClock 注释）

    /// <summary>
    /// tick → CtpBar 聚合器：按订阅分辨率（config.Increment）把 tick 聚成 OHLCV bar。
    /// 回测 CSV 是预聚合 bar，实盘用本类把实时 tick 聚成同形态 bar——算法 slice.Get&lt;CtpBar&gt;() 两边一致。
    /// P1 不跟踪 volume（CTP Volume 是日累计，非 per-bar；SMA 策略只用 close 价）。
    /// <para><b>成棒时机（2026-09-28 修）</b>：tick/second 分辨率（&lt;1 分钟）每 tick 即一根单点 bar，<b>立即外发</b>；
    /// ≥分钟级仍是跨边界关棒（bar 是时间聚合，须等边界过掉）。原因：交易所模式的行情是<b>事件驱动</b>
    /// （挂单/撤单/成交才有 tick）——若等下一 tick 才产出上一根 bar，订阅快照（空闲市场的唯一一帧）永远不成棒
    /// ⇒ OnData 不触发 ⇒ 策略不下单 ⇒ 再无事件——市场自举死锁（实测：双 bot 启动后零交易）。</para>
    /// </summary>
    private sealed class BarBuilder
    {
        private readonly TimeSpan _increment;
        private DateTime _barTime;
        private decimal _open, _high, _low, _close;
        private bool _hasData;

        public BarBuilder(TimeSpan increment) { _increment = increment; }

        public CtpBar? OnTick(DateTime time, decimal price, Symbol symbol)
        {
            var boundary = Truncate(time, _increment);

            // tick/second：单点 bar 立即外发（见类注释——事件驱动行情的自举要求）
            if (_increment < TimeSpan.FromMinutes(1))
            {
                _barTime = boundary; _open = _high = _low = _close = price; _hasData = true;
                return new CtpBar
                {
                    Symbol = symbol, Time = _barTime, EndTime = _barTime.Add(_increment),
                    Open = _open, High = _high, Low = _low, Close = _close, Volume = 0, Value = _close,
                };
            }

            // ≥分钟级：跨边界时关上一根（原有语义）
            CtpBar? completed = null;
            if (_hasData && boundary != _barTime)
            {
                completed = new CtpBar
                {
                    Symbol = symbol, Time = _barTime, EndTime = _barTime.Add(_increment),
                    Open = _open, High = _high, Low = _low, Close = _close, Volume = 0, Value = _close,
                };
            }
            if (!_hasData || boundary != _barTime)
            {
                _barTime = boundary; _open = _high = _low = _close = price; _hasData = true;
            }
            else
            {
                if (price > _high) _high = price;
                if (price < _low) _low = price;
                _close = price;
            }
            return completed;
        }

        private static DateTime Truncate(DateTime t, TimeSpan inc)
        {
            if (inc >= TimeSpan.FromDays(1)) return t.Date;
            if (inc >= TimeSpan.FromHours(1)) return new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0);
            if (inc >= TimeSpan.FromMinutes(1)) return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
            return t;   // tick/second：不截断，每 tick 一根
        }
    }
}
