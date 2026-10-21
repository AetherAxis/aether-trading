using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Statistics;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 用户 Lean 算法策略的基类（<see cref="QCAlgorithm"/> 子类）。第三种策略写法——直接复用 Lean 全部能力
/// （原生指标 SMA/RSI/MACD 链式组合、Portfolio 仓位、SetHoldings、Schedule、OnEndOfDay、History 等），
/// 绕过 <see cref="TradingHostAlgorithm"/> + <see cref="Aether.Trading.Domain.IStrategy"/> 那层壳。
/// </summary>
/// <remarks>
/// <b>数据约定（重要）</b>：本地无 Lean 原生行情数据，必须用 <see cref="AddCtpData"/> 订阅本地 CtpBar
/// （回测读预取 CSV，实盘读 CTP tick 聚合）。写 <c>AddSecurity</c>/<c>AddEquity</c> 会拿不到数据。
/// <para/>
/// <b>参数透传</b>：runner 把 start/end/cash/symbol/resolution + 用户参数设进 Lean Config "parameters"，
/// 算法经 <c>GetParameter("key")</c> 读。<see cref="ConfigureFromConfig"/> 封装了 start/end/cash 的读取 + 设置——
/// 在 <c>Initialize</c> 开头调一次即可，免记 key 名。用户自定义参数直接 <c>GetParameter("MyParam")</c>。
/// <para/>
/// <b>参数定义</b>：UI 据静态 <c>ParameterDefinitions</c> 渲染输入框（静态约定，免实例化 QCAlgorithm）——
/// 在子类声明 <c>public static IReadOnlyList&lt;StrategyParameterDef&gt; ParameterDefinitions =&gt; new[]{ ... }</c>。
/// </remarks>
public abstract class LeanAlgorithmBase : QCAlgorithm
{
    /// <summary>
    /// 以线程安全的 <see cref="LockedTradeBuilder"/> 替换默认 TradeBuilder——Lean 2.5.18193 的
    /// SetMarketPrice（算法线程）与 ProcessFill（成交线程）无锁并发，高频成交下 NRE 引擎即死
    /// （2026-10-21 实测 85 秒即死，堆栈钉死 TradeBuilder.SetMarketPrice）。基类构造器统一替换，
    /// 策略无感。分组/撮合参数与 QCAlgorithm 默认一致（FillToFill + FIFO）。
    /// </summary>
    protected LeanAlgorithmBase()
        => SetTradeBuilder(new LockedTradeBuilder(FillGroupingMethod.FillToFill, FillMatchingMethod.FIFO));

    /// <summary>
    /// 订阅本地 CtpBar 数据（回测读预取 CSV / 实盘读 CTP tick 聚合）。等价
    /// <c>AddData&lt;CtpBar&gt;(ticker, resolution, TimeZones.Utc)</c>。
    /// <para/>
    /// <b>必须传显式时区（2026-09-28 修，自举死锁第四层）</b>：<c>AddDataImpl</c> 只在 timeZone 非空时
    /// <c>MarketHoursDatabase.SetEntryAlwaysOpen</c>——不传则合约落默认交易所时段（纽约 NYSE）：
    /// ①非美交易时段的 tick 被市场时段过滤器拦掉；②数据时区默认纽约，而实盘 tick 由
    /// <see cref="CtpBrokerage"/> 以<b>客户端 UTC 时钟</b>盖戳——时区对不上会把数据标到未来/过去，
    /// 未来数据被 <c>FrontierAwareEnumerator</c> 扣住永不发射（实测：棒已生成但算法永远收不到）。
    /// UTC + 始终开市 = tick 盖戳即发射。
    /// </para>
    /// </summary>
    public Symbol AddCtpData(string ticker, Resolution resolution = Resolution.Daily)
        => AddData<CtpBar>(ticker, resolution, TimeZones.Utc).Symbol;

    /// <summary>
    /// 直通实盘日志（同步、即时可见）：绕过 Lean 结果处理器的异步 Debug 队列（延迟数秒且可能被吞），
    /// 直接写 <see cref="CtpLeanGateway"/>.Log → 实盘 tab 日志。机器人策略的关键动作（初始化/首帧/挂撤单）用它。
    /// </summary>
    protected void BotLog(string message) => CtpLeanGateway.Log(message);

    /// <summary>从 Lean Config parameters 读 start/end/cash 并设置（在 Initialize 开头调一次）。</summary>
    protected void ConfigureFromConfig()
    {
        if (GetParameter("cash") is { Length: > 0 } c && decimal.TryParse(c, out var cash))
            SetCash(cash);
        else
            SetCash(100_000m);

        if (GetParameter("start") is { Length: > 0 } startStr && DateTime.TryParse(startStr, out var start))
            SetStartDate(start);
        else
            SetStartDate(2024, 1, 1);

        if (GetParameter("end") is { Length: > 0 } endStr && DateTime.TryParse(endStr, out var end))
            SetEndDate(end);
        else
            SetEndDate(DateTime.Today);
    }
}
