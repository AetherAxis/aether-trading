using Aether.Trading.Domain;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 把 <see cref="CtpBar"/>（LEAN 自定义 K 线）翻译成纯领域 <see cref="MarketBar"/>，供 IStrategy 消费。
/// 集中翻译，策略不接触 QuantConnect 类型。回测/实盘共用（两路都产 CtpBar）。
/// </summary>
internal static class MarketBarMapper
{
    public static MarketBar FromCtpBar(CtpBar bar) =>
        new(bar.Time, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
}
