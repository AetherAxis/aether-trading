namespace Aether.Trading.Domain.Indicators;

/// <summary>
/// 增量指标契约（Lean 中立——只镜像 QuantConnect <c>IndicatorBase</c> 的 API 形状，不引 <c>QuantConnect.*</c>）。
/// 每根 K 线调一次 <see cref="Update"/>，指标内部增量更新；<see cref="IsReady"/> 表示样本已够；
/// <see cref="Value"/> 为主输出，<see cref="GetValue"/> 取命名输出（多输出指标如 MACD/Bollinger）。
/// <para>纯域计算，无 infrastructure 依赖；与 <see cref="IStrategy"/> 同层，CodeStrategy 与 YAML 策略共享。</para>
/// </summary>
public interface IIndicator
{
    /// <summary>用当前 K 线增量更新指标状态。每根 bar 调一次（含预热期）。</summary>
    void Update(MarketBar bar);

    /// <summary>样本是否足够（如 SMA 需 period 根）。预热期内为 false。</summary>
    bool IsReady { get; }

    /// <summary>主输出值（= <see cref="GetValue"/>(null)）。未就绪时返回 0。</summary>
    decimal Value { get; }

    /// <summary>取命名输出；null/空串 → 主输出。未就绪返回 0。未知输出名抛 <see cref="ArgumentException"/>。</summary>
    decimal GetValue(string? output);

    /// <summary>该指标的所有可用输出名（如 MACD = ["macd","signal","hist"]；单输出 = ["value"]）。</summary>
    IReadOnlyList<string> Outputs { get; }

    /// <summary>预热所需 bar 数（样本达到此数即 <see cref="IsReady"/>）。YamlStrategy 据此推导 WarmUpBars。</summary>
    int RequiredBars { get; }
}
