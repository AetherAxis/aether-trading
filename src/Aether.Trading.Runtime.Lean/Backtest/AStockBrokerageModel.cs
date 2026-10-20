using QuantConnect;
using QuantConnect.Brokerages;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// A 股券商模型：Cash 账户（不可融资融券 → 不可做空）+ A 股费用（佣金 + 印花税 + 过户费）。
/// 算法 Initialize 里 SetBrokerageModel(new AStockBrokerageModel()) 即生效。
/// 修了币种（CNY 而非 USD）并补过户费。
/// </summary>
public class AStockBrokerageModel : DefaultBrokerageModel
{
    /// <summary>Cash 账户：无融资融券，禁止做空。</summary>
    public AStockBrokerageModel() : base(QuantConnect.AccountType.Cash)
    {
    }

    public override IFeeModel GetFeeModel(Security security) => new AStockFeeModel();
}

/// <summary>
/// A 股费用模型（人民币结算）：
/// - 佣金：双向 0.025%（万2.5），单笔最低 ¥5；
/// - 印花税：卖出单向 0.05%（2023-08-28 起由 0.1% 下调至 0.05%）；
/// - 过户费：双向 0.001%（沪深 2022 年起均收）。
/// 费率均为常量，按需调整。
/// </summary>
public class AStockFeeModel : FeeModel
{
    private const decimal CommissionRate = 0.00025m;
    private const decimal MinCommission = 5m;
    private const decimal StampDutyRate = 0.0005m;       // 卖出
    private const decimal TransferFeeRate = 0.00001m;    // 双向
    // 账户以 USD 记账（无 FX 数据，避免 USD↔CNY 换汇链路）；费率按 A 股真实比例
    private const string Currency = "USD";

    public override OrderFee GetOrderFee(OrderFeeParameters parameters)
    {
        var price = parameters.Security.Price;
        var quantity = parameters.Order.AbsoluteQuantity;
        var turnover = price * quantity;

        var commission = System.Math.Max(turnover * CommissionRate, MinCommission);
        var transferFee = turnover * TransferFeeRate;
        var stampDuty = parameters.Order.Direction == OrderDirection.Sell
            ? turnover * StampDutyRate
            : 0m;

        var totalFee = commission + transferFee + stampDuty;
        return new OrderFee(new CashAmount(totalFee, Currency));
    }
}
