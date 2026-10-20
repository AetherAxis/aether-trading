using QuantConnect.Logging;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 把 Lean 的 <see cref="Log"/> 转发到 <see cref="CtpLeanGateway"/>.Log（UI 可见），同时委托给原 handler（保留 console/file）。
/// 用于诊断实盘引擎启动错误——<c>BrokerageSetupHandler</c> 吞掉的 Initialize 异常会经
/// <c>Log.Error("Engine.Run(): ...")</c> 输出，否则看不到（状态仍显 Running，因为本地 algorithmManager.State 未被置 RuntimeError）。
/// </summary>
internal sealed class CtpLeanLogForwarder : ILogHandler
{
    private readonly ILogHandler _inner;
    private readonly Action<string> _forward;

    public CtpLeanLogForwarder(ILogHandler inner, Action<string> forward)
    {
        _inner = inner;
        _forward = forward;
    }

    public void Error(string text)
    {
        _inner.Error(text);
        _forward("[LEAN-E] " + text);
    }

    public void Debug(string text) => _inner.Debug(text);

    public void Report(string text)
    {
        _inner.Report(text);
        _forward("[LEAN-R] " + text);
    }

    public void Trace(string text)
    {
        _inner.Trace(text);
        // Trace 极多，只转发关键行（含错误/初始化/桥接/数据流关键字），避免刷屏
        if (text != null && (text.Contains("Error") || text.Contains("error") || text.Contains("Exception")
            || text.Contains("Engine.Run") || text.Contains("Initialize") || text.Contains("Setup")
            || text.Contains("Brokerage") || text.Contains("Ctp") || text.Contains("DataFeed")
            || text.Contains("Subscribe") || text.Contains("algorithm")))
        {
            _forward("[LEAN] " + text);
        }
    }

    public void Dispose() => _inner.Dispose();
}
