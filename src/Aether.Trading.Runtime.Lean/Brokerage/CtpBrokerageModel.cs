using QuantConnect.Brokerages;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// CTP 期货券商模型：Margin 账户（期货保证金交易）。P1 用默认规则；P3 在此加 T+1/不可做空/期货费用等 CTP 专属规则。
/// </summary>
public class CtpBrokerageModel : DefaultBrokerageModel
{
    public CtpBrokerageModel() : base(QuantConnect.AccountType.Margin) { }
}
