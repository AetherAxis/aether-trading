using Ctp.Net;
using Aether.Trading.Runtime.Lean;
using QuantConnect;
using QuantConnect.Data;
using Xunit;

namespace Aether.Trading.Tests;

/// <summary>
/// 实盘数据面单测（FakeCtpSession 驱动 tick → BarBuilder → aggregator 枚举）：
/// 2026-09-28 修复的回归——tick/second 分辨率下**首个 tick 必须立即成棒**。
/// 交易所模式行情是事件驱动（挂单/撤单/成交才有 tick），旧语义「等下一 tick 关上一根棒」会让订阅快照
/// （空闲市场的唯一一帧）永远不成棒 ⇒ OnData 不触发 ⇒ 策略不下单 ⇒ 再无事件——市场自举死锁
/// （实测：双 bot 启动后零交易）。
/// 注意：ScannableEnumerator.MoveNext 恒返 true（Lean 语义——空集也 true），判据是 Current 是否为棒。
/// </summary>
public class CtpBrokerageDataTests : IDisposable
{
    private readonly CtpBrokerageTradingTests.FakeCtpSession _session = new();
    private readonly CtpBrokerage _brokerage;
    private readonly List<string> _logs = new();

    public CtpBrokerageDataTests()
    {
        CtpLeanGateway.LogSink = m => _logs.Add(m);
        CtpLeanGateway.Instruments = new[] { "ag2610" };
        CtpLeanGateway.MarketDataConnect = new CtpMarketDataConnect
        {
            MdFrontAddress = "tcp://127.0.0.1:40002", BrokerId = "9999", UserId = "user01", Password = "pw",
        };
        _session.Ticks.Clear();   // fake 预置了映射器用的行情——数据面测试自控 tick（快照种子只在该测里注入）
        _brokerage = new CtpBrokerage(null, _session);
        _brokerage.Connect();   // WireSessionEvents（fake IsConnected=true）
    }

    public void Dispose()
    {
        CtpLeanGateway.Instruments = Array.Empty<string>();
        CtpLeanGateway.MarketDataConnect = null;
        CtpLeanGateway.LogSink = null;
    }

    private static SubscriptionDataConfig Config(Resolution resolution)
    {
        var symbol = Symbol.Create("ag2610", SecurityType.Base, Market.USA);
        return new SubscriptionDataConfig(typeof(CtpBar), symbol, resolution,
            TimeZones.NewYork, TimeZones.NewYork, false, false, false, isCustom: true);
    }

    private static CtpTick Tick(double last, string time) => new()
    {
        InstrumentId = "ag2610", LastPrice = last, UpdateTime = time, UpdateMillisec = 0,
        ActionDay = "20260928", TradingDay = "20260928",
    };

    /// <summary>固定盖戳时钟（LiveTickClock 注入——分钟边界测试须可控时间，真实 UtcNow 会跨边界踩不准）。</summary>
    private void FixClock(DateTime t) => _brokerage.LiveTickClock = () => t;

    [Fact]
    public void 首个tick立即成棒_交易所模式自举()
    {
        var enumerator = _brokerage.Subscribe(Config(Resolution.Second), (_, _) => { });

        // 空闲市场唯一的一帧：订阅快照 tick——必须立即产出 bar（不等下一 tick）
        _session.RaiseTick(Tick(7000, "10:00:00"));

        enumerator.MoveNext();
        var bar = Assert.IsType<CtpBar>(enumerator.Current);   // Current=null ⇒ IsType 失败（自举死锁会挂这里）
        Assert.Equal(7000m, bar.Close);
    }

    [Fact]
    public void 增量订阅no_op时_会话缓存快照种子自举首棒()
    {
        // 2026-10-03 双 bot 实测回归：期货页行情板先于引擎订阅 ⇒ CtpSessionService 增量订阅对同合约
        // 是 no-op（不再发订阅帧 ⇒ 前置不再推订阅快照），且原快照 tick 在 brokerage 挂监听前已被
        // 消费——静态市场（做市商挂单等噪声来吃）无新 TickProduced 事件 ⇒ 策略 Price 恒 0
        // 「等待首帧行情」死锁。修复 = Subscribe 从会话最新 tick 缓存回放一帧（快照种子）。
        _session.Ticks["ag2610"] = Tick(7000, "10:00:00");   // 行情板时代到达、缓存在会话里的快照

        var enumerator = _brokerage.Subscribe(Config(Resolution.Second), (_, _) => { });

        enumerator.MoveNext();
        var bar = Assert.IsType<CtpBar>(enumerator.Current);   // 种子回放成首棒——无任何 wire 事件驱动
        Assert.Equal(7000m, bar.Close);
    }

    [Fact]
    public void 订阅快照在订阅往返内到达不被丢弃()
    {
        // 竞态回归（2026-09-28 实测第二轮）：服务端在订阅应答同拍推送快照 tick（接收线程触发
        // OnTickReceived）——旧序「先触发 CTP 订阅、后登记 _symbolByInstrument」会把它 TryGetValue 丢弃。
        // 注入钩子让 fake 在 SubscribeMarketDataAsync 返回前同步推一帧 = 确定性复现该时序。
        _session.OnSubscribe = (_, _) =>
        {
            _session.RaiseTick(Tick(7000, "10:00:00"));
            return Task.CompletedTask;
        };

        var enumerator = _brokerage.Subscribe(Config(Resolution.Second), (_, _) => { });

        enumerator.MoveNext();
        var bar = Assert.IsType<CtpBar>(enumerator.Current);   // 快照未被丢弃且立即成棒
        Assert.Equal(7000m, bar.Close);
    }

    [Fact]
    public void 后续tick各成单点棒_无延迟()
    {
        var enumerator = _brokerage.Subscribe(Config(Resolution.Second), (_, _) => { });

        _session.RaiseTick(Tick(7000, "10:00:00"));
        _session.RaiseTick(Tick(7001, "10:00:01"));
        _session.RaiseTick(Tick(6999, "10:00:02"));

        var bars = new List<decimal>();
        for (var i = 0; i < 3; i++)
        {
            enumerator.MoveNext();
            var bar = Assert.IsType<CtpBar>(enumerator.Current);
            bars.Add(bar.Close);
        }
        Assert.Equal(new[] { 7000m, 7001m, 6999m }, bars);   // 每个 tick 一根、即时、不滞后一根
    }

    [Fact]
    public void CtpBar实现IBar_Security价格随数据更新()
    {
        // 自举死锁第三层的修复 pin（2026-09-28 实测第三轮）：SecurityCache.ProcessDataPoint 对非 tick
        // 数据走 data as IBar 更新 Price=Close——CtpBar 未实现 IBar 时 Price 恒 0，
        // 策略的 Price<=0 守卫拦死一切下单（引擎/订阅/回填全正常、零报错——最难排查的一层）。
        var t = new DateTime(2026, 9, 28, 10, 0, 0);
        var cache = new QuantConnect.Securities.SecurityCache();
        cache.AddData(new CtpBar
        {
            Symbol = Symbol.Create("ag2610", SecurityType.Base, Market.USA),
            Time = t, EndTime = t,
            Open = 7000m, High = 7002m, Low = 6999m, Close = 7001m, Value = 7001m,
        });
        Assert.Equal(7001m, cache.Price);
    }

    [Fact]
    public void 分钟级仍是跨边界成棒()
    {
        var enumerator = _brokerage.Subscribe(Config(Resolution.Minute), (_, _) => { });

        // 同一分钟内的两个 tick：不产棒（bar 是时间聚合，须等边界过掉）——盖戳时钟注入固定时间
        FixClock(new DateTime(2026, 9, 28, 2, 0, 1, DateTimeKind.Utc));
        _session.RaiseTick(Tick(7000, "10:00:01"));
        FixClock(new DateTime(2026, 9, 28, 2, 0, 30, DateTimeKind.Utc));
        _session.RaiseTick(Tick(7002, "10:00:30"));
        enumerator.MoveNext();
        Assert.False(enumerator.Current is CtpBar, "分钟级在边界内不应成棒");

        // 跨边界：上一分钟聚合棒产出（O=7000 H=7002 L=7000 C=7002）
        FixClock(new DateTime(2026, 9, 28, 2, 1, 5, DateTimeKind.Utc));
        _session.RaiseTick(Tick(7003, "10:01:05"));
        enumerator.MoveNext();
        var bar = Assert.IsType<CtpBar>(enumerator.Current);
        Assert.Equal(7000m, bar.Open);
        Assert.Equal(7002m, bar.High);
        Assert.Equal(7000m, bar.Low);
        Assert.Equal(7002m, bar.Close);
    }
}
