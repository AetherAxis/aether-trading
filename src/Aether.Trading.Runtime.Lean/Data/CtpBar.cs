using System.Globalization;
using QuantConnect;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 本地自定义历史/实时 K 线数据源（LEAN BaseData + <see cref="IBar"/>）。回测与实盘共用——
/// <para/>回测：<c>CtpBar.Reader</c> 解析 LeanBacktestRunner 预取的本地 CSV（IAnalysisManager cache-first 取 K 线写就），
/// <c>GetSource</c> 指向该本地文件（SubscriptionTransportMedium.LocalFile）。CSV 列：time,open,high,low,close,volume。
/// <para/>实盘：<c>GetSource</c>/<c>Reader</c> 不被调用——CtpBrokerage 把 CTP tick 聚合成 CtpBar 后经
/// <c>IDataAggregator.Update</c> 直接推 LiveTradingDataFeed，算法 <c>slice.Get&lt;CtpBar&gt;()</c> 取到。
/// <para/>故同一算法 + 同一数据类型 + 同一策略，回测走 CSV、实盘走 CTP tick 聚合，两边代码一致（Lean 的 backtest=live 承诺）。
/// <para/><b>必须实现 <see cref="IBar"/>（2026-09-28 修，自举死锁第三层）</b>：<see cref="QuantConnect.Securities.SecurityCache"/>
/// 对非 tick 数据经 <c>data as IBar</c> 更新 <c>Price = Close</c>——不实现接口 ⇒ <c>Security.Price</c> 恒 0 ⇒
/// 策略的 Price 守卫拦死一切下单（无任何报错）。接口只需 Open/High/Low/Close 只读属性，本类天然满足。
/// </summary>
public class CtpBar : BaseData, IBar
{
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }

    public override DateTime EndTime { get; set; }

    /// <summary>回测：返回预取的本地 CSV 路径。实盘：返回 Streaming medium，让 Lean ShouldStreamSubscription=true
    /// → 走 data-queue-handler.Subscribe（CtpBrokerage），而非 DataProvider 读文件。</summary>
    public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
    {
        if (isLiveMode)
            return new SubscriptionDataSource(string.Empty, SubscriptionTransportMedium.Streaming);

        var key = $"local-data-file:{config.Symbol.Value}";
        var path = Config.Get(key);
        if (string.IsNullOrWhiteSpace(path))
        {
            var lowerKey = $"local-data-file:{config.Symbol.Value.ToLowerInvariant()}";
            path = Config.Get(lowerKey);
        }
        return new SubscriptionDataSource(path, SubscriptionTransportMedium.LocalFile);
    }

    /// <summary>回测：解析一行 CSV → CtpBar。支持日线（yyyy-MM-dd）与分钟/小时（yyyy-MM-dd HH:mm:ss）。</summary>
    public override BaseData Reader(SubscriptionDataConfig config, string line, DateTime date, bool isLiveMode)
    {
        if (string.IsNullOrWhiteSpace(line)) return null!;
        var cols = line.Split(',');
        if (cols.Length < 6) return null!;
        if (!DateTime.TryParseExact(cols[0], new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return null!;

        try
        {
            return new CtpBar
            {
                Symbol = config.Symbol,
                Time = d,
                EndTime = d,
                Open = ParseDecimal(cols[1]),
                High = ParseDecimal(cols[2]),
                Low = ParseDecimal(cols[3]),
                Close = ParseDecimal(cols[4]),
                Volume = ParseDecimal(cols[5]),
                Value = ParseDecimal(cols[4]),
            };
        }
        catch
        {
            return null!;
        }
    }

    private static decimal ParseDecimal(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        return decimal.Parse(s, CultureInfo.InvariantCulture);
    }
}
