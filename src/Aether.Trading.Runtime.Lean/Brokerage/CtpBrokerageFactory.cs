using Ctp.Net;
using QuantConnect.Brokerages;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// CTP brokerage 工厂（Lean 按 <c>live-mode-brokerage=CtpBrokerage</c> 经 Composer 解析本工厂）。
/// <see cref="CreateBrokerage"/> 创建 <see cref="CtpBrokerage"/> 后，<c>Composer.Instance.AddPart((IDataQueueHandler)brokerage)</c>
/// 让 <c>DataQueueHandlerManager</c> 按 typeName 解析 <c>data-queue-handler=CtpBrokerage</c> 时命中同一实例
/// （GetExportedValueByTypeName path 1 查 _exportedValues[IDataQueueHandler]，不走 MEF 无参构造）——
/// 故交易与行情共用一个 CtpBrokerage，ICtpSession 事件只订阅一次。
/// </summary>
[BrokerageFactory(typeof(CtpBrokerage))]
public class CtpBrokerageFactory : BrokerageFactory
{
    public CtpBrokerageFactory() : base(typeof(CtpBrokerage)) { }

    public override Dictionary<string, string> BrokerageData => new();

    public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new CtpBrokerageModel();

    public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
    {
        var session = CtpLeanGateway.Session
            ?? throw new InvalidOperationException("ICtpSession 未注入 CtpLeanGateway（service 启动前应注入）");
        var brokerage = new CtpBrokerage(algorithm, session);
        // 关键：注册为 IDataQueueHandler，让 data feed 解析到同一实例（单实例承担交易+行情）
        Composer.Instance.AddPart((IDataQueueHandler)brokerage);
        return brokerage;
    }

    public override void Dispose() { }
}
