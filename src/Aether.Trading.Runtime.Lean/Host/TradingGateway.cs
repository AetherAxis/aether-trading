namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 共享算法的静态日志/停止网关。回测与实盘共用同一算法（TradingHostAlgorithm），算法需日志输出与停止信号；
/// 实盘侧由 LeanLiveTradingService 启动引擎前注入 LogSink + StopRequested，回测侧留空（回测日志走 Lean ResultHandler）。
/// 与 CtpLeanGateway（CTP 会话专属）分开——本类只管算法侧通用需求，不依赖 CTP。
/// </summary>
public static class TradingGateway
{
    /// <summary>日志接收口（实盘 service 转 UI）。为 null 时算法静默。</summary>
    public static Action<string>? LogSink { get; set; }

    /// <summary>停止标志——实盘算法 OnData 检测后调 Quit() 退出引擎。回测不检查（LiveMode=false）。</summary>
    public static volatile bool StopRequested;
    public static volatile bool VerboseLog;

    /// <summary>
    /// 宿主级强停钩子（<see cref="LeanLiveRunner"/> 注入 = <c>algorithmManager.SetStatus(AlgorithmStatus.Stopped)</c>，
    /// 镜像 <c>algorithm.Quit()</c> 的内部路径：算法状态翻转 → Run 循环下个 time-pulse break → Engine 正常
    /// teardown（brokerage 断连/行情退订/handler Exit））。用于算法<b>不协作</b>的停止——bot 算法
    /// （labs 编译的 QCAlgorithm 子类）没有 StopRequested 检查，协作路径对它们恒无效（2026-10-14 实测
    /// 「停止 30s 超时→状态硬置 Stopped 但引擎线程仍在跑」的桌面/控制台双症状根因）。引擎退出时随 Reset 清空。
    /// </summary>
    public static Action? ForceStop { get; set; }

    /// <summary>触发宿主级强停（钩子未注入/抛异常都返 false——调用方据此如实报告）。</summary>
    public static bool RequestForceStop()
    {
        var hook = ForceStop;
        if (hook is null) return false;
        try { hook(); return true; }
        catch { return false; }
    }

    public static void Log(string message)
    {
        try { LogSink?.Invoke(message); }
        catch { /* 日志不应影响引擎 */ }
    }

    public static void Reset()
    {
        LogSink = null;
        StopRequested = false;
        VerboseLog = false;
        ForceStop = null;
    }
}
