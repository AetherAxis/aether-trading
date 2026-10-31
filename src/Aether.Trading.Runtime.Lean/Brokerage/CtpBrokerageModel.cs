using QuantConnect.Brokerages;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// CTP 期货券商模型：Margin 账户（期货保证金交易）。P1 用默认规则；P3 在此加 T+1/不可做空/期货费用等 CTP 专属规则。
/// <para>
/// 买入力检查在 <see cref="LeanAlgorithmBase.AddCtpData"/> 里按合约关掉（2026-10-31，见那处注释）：
/// Lean 的期货保证金模型与交易所的「平仓单冻结 0」算法不一致，会把小额账户先算成 Free Margin 0，
/// 再用本地风控毙掉策略的每一张新单。券商模型这层不重写（本版 Lean 的 GetBuyingPowerModel 不可重写）。
/// </para>
/// </summary>
public class CtpBrokerageModel : DefaultBrokerageModel
{
    public CtpBrokerageModel() : base(QuantConnect.AccountType.Margin) { }
}
