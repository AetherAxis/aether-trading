using System.Threading;
using System.Threading.Tasks;
using Ctp.Net;
using Aether.Trading.Runtime.Domain;
using Microsoft.Extensions.Logging;
using QuantConnect;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// <see cref="ILiveTradingRuntime"/> 实现：注入 <see cref="ICtpSession"/> 到 <see cref="CtpLeanGateway"/>，
/// 后台线程跑 <see cref="LeanLiveRunner"/>（Lean 实时引擎阻塞 CPU 活）。引擎经 CtpBrokerage 桥接同一 session。
/// 前置：CTP 已登录（ICtpSession.IsConnected）；否则 StartAsync 直接返回错误，不启动引擎。
/// </summary>
public sealed class LeanLiveTradingService : ILiveTradingRuntime
{
    private readonly ICtpSession _session;
    private readonly ILogger<LeanLiveTradingService>? _logger;
    private readonly LeanLiveRunner _runner;
    private LiveTradingRuntimeStatus _status = LiveTradingRuntimeStatus.Idle;
    private Task? _runTask;

    public LeanLiveTradingService(ICtpSession session, ILogger<LeanLiveTradingService>? logger = null)
    {
        _session = session;
        _logger = logger;
        _runner = new LeanLiveRunner(logger);
    }

    public LiveTradingRuntimeStatus Status => _status;
    public bool IsRunning => _status == LiveTradingRuntimeStatus.Running;
    public event EventHandler<LiveTradingRuntimeStatus>? StatusChanged;
    public event EventHandler<string>? LogReceived;

    public Task<(bool Success, string? Error)> StartAsync(LiveTradingRuntimeRequest request, CancellationToken ct = default)
    {
        if (_status == LiveTradingRuntimeStatus.Running || _status == LiveTradingRuntimeStatus.Starting)
            return Task.FromResult((false, (string?)"实盘引擎已在运行"));

        if (!_session.IsConnected)
            return Task.FromResult((false, (string?)"CTP 未登录——请先登录后再启动实盘引擎"));
        var mdConnect = LeanLiveRunner.MapConnect(request.MarketDataConnect);
        if (mdConnect == null)
            return Task.FromResult((false, (string?)"未提供行情前置连接参数（MdFrontAddress/BrokerId/UserId/Password）"));
        if (request.Instruments.Count == 0)
            return Task.FromResult((false, (string?)"未指定订阅合约"));

        // 注入网关：CtpLeanGateway（brokerage 用：session/md/instruments/日志/P2 交易选项）+ TradingGateway（共享算法用：日志/停止）
        CtpLeanGateway.Reset();
        CtpLeanGateway.Session = _session;
        CtpLeanGateway.MarketDataConnect = mdConnect;
        CtpLeanGateway.Instruments = request.Instruments;
        CtpLeanGateway.LogSink = msg => LogReceived?.Invoke(this, msg);
        CtpLeanGateway.TradingOptions = new CtpLiveTradingOptions
        {
            EnableRealOrders = request.EnableRealOrders,
            MaxOrderVolume = Math.Max(1, request.MaxOrderVolume),
        };
        TradingGateway.Reset();
        TradingGateway.VerboseLog = request.VerboseLog;
        TradingGateway.LogSink = msg => LogReceived?.Invoke(this, msg);

        SetStatus(LiveTradingRuntimeStatus.Starting);
        EmitLog("实盘引擎启动中…");

        _runTask = Task.Run(() =>
        {
            SetStatus(LiveTradingRuntimeStatus.Running);
            EmitLog("实盘引擎运行中（消费 CTP 行情）");
            try
            {
                var (status, error) = _runner.Run(request);
                EmitLog($"实盘引擎退出：{status}{(error != null ? " / " + error : "")}");
                SetStatus(status == AlgorithmStatus.Completed || status == AlgorithmStatus.Stopped
                    ? LiveTradingRuntimeStatus.Stopped
                    : LiveTradingRuntimeStatus.Error);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "实盘引擎异常");
                EmitLog($"实盘引擎异常: {ex.Message}");
                SetStatus(LiveTradingRuntimeStatus.Error);
            }
            finally
            {
                CtpLeanGateway.Reset();
                TradingGateway.Reset();
            }
        }, ct);

        return Task.FromResult((true, (string?)null));
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_status != LiveTradingRuntimeStatus.Running && _status != LiveTradingRuntimeStatus.Starting) return;
        SetStatus(LiveTradingRuntimeStatus.Stopping);
        TradingGateway.StopRequested = true;   // 协作路径：TradingHostAlgorithm 的 Schedule/OnData 检测后 Quit
        EmitLog("停止请求已发出，等待引擎退出…");
        if (_runTask != null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(5), ct); }
            catch (TimeoutException)
            {
                // 5s 未自行退出 = 算法不协作（bot 算法无 StopRequested 检查）⇒ 宿主级强停：
                // AlgorithmManager.SetStatus(Stopped) 镜像 Quit() 内部路径，引擎正常 teardown（brokerage 断连）。
                if (TradingGateway.RequestForceStop())
                    EmitLog("引擎 5s 未自行退出——已发出宿主级强制停止，等待引擎退出…");
            }
            catch (OperationCanceledException) { }
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(60), ct); }
            catch (TimeoutException) { EmitLog("引擎 60s 内仍未退出——引擎线程可能仍在运行（状态如实保持 Stopping）"); }
            catch (OperationCanceledException) { }
        }
        // 只在引擎任务真正结束时才宣告 Stopped——旧版超时也硬置 Stopped = 「显示已停止但引擎仍在跑」
        // （2026-10-14 桌面/控制台双实测：僵尸引擎继续交易 + LeanGlobalLock 被占 → 再启动恒挂）。
        if (_status == LiveTradingRuntimeStatus.Stopping && _runTask?.IsCompleted == true)
            SetStatus(LiveTradingRuntimeStatus.Stopped);
    }

    private void SetStatus(LiveTradingRuntimeStatus s)
    {
        _status = s;
        StatusChanged?.Invoke(this, s);
    }

    private void EmitLog(string msg) => LogReceived?.Invoke(this, msg);
}
