using System.IO;
using Ctp.Net;
using Aether.Trading.Runtime.Domain;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using QuantConnect;
using QuantConnect.Configuration;
using QuantConnect.Lean.Engine;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Util;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 进程内启动 QuantConnect LEAN 实时引擎，镜像 LeanBacktestRunner 的 Launcher 编排（Initializer→SystemHandlers→
/// JobQueue.NextJob→AlgorithmHandlers→AlgorithmManager→Engine.Run），差异在 live handler 链 + LiveNodePacket。
/// 同步阻塞（live engine.Run 持续运行直到算法 Quit），调用方（LeanLiveTradingService）应 Task.Run 卸载。
/// 关键：<see cref="Globals.Reset"/> 在 <see cref="Initializer.Start"/> 前显式调用，以拾取 Config 的 live-mode=true
/// （Globals.LiveMode 仅在静态构造/Reset 时读 Config，若同进程先跑过回测会被锁定为 false）。
/// </summary>
internal sealed class LeanLiveRunner
{
    private readonly ILogger? _logger;
    private readonly string? _defaultDataFolder;

    public LeanLiveRunner(ILogger? logger = null, string? defaultDataFolder = null)
    {
        _logger = logger;
        _defaultDataFolder = defaultDataFolder;
    }

    /// <returns>(算法结束状态, 错误信息)</returns>
    public (AlgorithmStatus Status, string? Error) Run(LiveTradingRuntimeRequest request)
    {
        // P0 守卫：与 LeanBacktestRunner/LeanAlgorithmBacktestRunner 共用 LeanGlobalLock，三入口互斥。
        // 本方法同步阻塞（调用方 LeanLiveTradingService 已 Task.Run 卸载到后台线程），故用阻塞 Wait()。
        LeanGlobalLock.Wait();
        try
        {
            return RunCore(request);
        }
        finally
        {
            LeanGlobalLock.Release();
        }
    }

    private (AlgorithmStatus Status, string? Error) RunCore(LiveTradingRuntimeRequest request)
    {
        var resultsDir = Path.Combine(Path.GetTempPath(), "AetherCtpLean", Guid.NewGuid().ToString("N"));

        // 算法解析（引擎分支）：IStrategy 壳 = TradingHostAlgorithm（本程序集）；LeanAlgorithm = 注册表编译类型
        // （镜像 LeanAlgorithmBacktestRunner 的 algorithm-type-name/location 机制——同一 QCAlgorithm 子类回测/实盘两用）
        string algoTypeName, algoLocation;
        if (request.Engine == LiveStrategyEngine.LeanAlgorithm)
        {
            var algoType = LeanAlgorithmRegistry.GetType(request.StrategyName);
            var algoPath = LeanAlgorithmRegistry.GetAssemblyPath(request.StrategyName);
            if (algoType is null || algoPath is null)
                return (AlgorithmStatus.RuntimeError, $"未注册的 Lean 算法: {request.StrategyName}（检查 labs/leanalgorithms 编译错误）");
            algoTypeName = algoType.FullName!;
            algoLocation = algoPath;
        }
        else
        {
            algoTypeName = "Aether.Trading.Runtime.Lean.TradingHostAlgorithm";
            algoLocation = typeof(TradingHostAlgorithm).Assembly.Location;
        }

        WorkerThread.Instance = new FreshWorkerThread();
        Composer.Instance.Reset();

        ConfigureLeanLive(request, resultsDir, algoTypeName, algoLocation);
        // AddData<CtpBar>（Base 自定义数据）无需 market 注册/SPDB/交易时段（回测同此）——统一走自定义数据类型

        // 关键：在 Initializer.Start（首次触碰 Globals 触发静态构造）前显式 Reset，拾取 live-mode=true
        Globals.Reset();
        Initializer.Start();
        // 包一层日志转发：Lean 的 Initialize/Setup 错误经 Log.Error 输出，转发到 UI 才看得到
        Log.LogHandler = new CtpLeanLogForwarder(Log.LogHandler, CtpLeanGateway.Log);

        var systemHandlers = Initializer.GetSystemHandlers();
        var job = systemHandlers.JobQueue.NextJob(out var assemblyPath) as LiveNodePacket;
        if (job == null)
            return (AlgorithmStatus.RuntimeError, "LEAN JobQueue 未能产出 LiveNodePacket（检查 Config live-mode/live-mode-brokerage）");

        var algorithmHandlers = Initializer.GetAlgorithmHandlers();
        var algorithmManager = new AlgorithmManager(Globals.LiveMode, job);
        // 宿主级强停钩子：算法不协作（bot 算法无 StopRequested 检查）时，StopAsync 经此直接翻转
        // AlgorithmManager 状态——镜像 algorithm.Quit() 内部路径，引擎走正常 teardown 而非僵尸滞留。
        TradingGateway.ForceStop = () => algorithmManager.SetStatus(AlgorithmStatus.Stopped);
        systemHandlers.LeanManager.Initialize(systemHandlers, algorithmHandlers, job, algorithmManager);

        string? error = null;
        AlgorithmStatus status = AlgorithmStatus.RuntimeError;
        try
        {
            var engine = new Engine(systemHandlers, algorithmHandlers, Globals.LiveMode);
            engine.Run(job, algorithmManager, assemblyPath, WorkerThread.Instance);
            status = algorithmManager.State;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LEAN 实时引擎运行异常");
            error = ex.Message;
            status = algorithmManager?.State ?? AlgorithmStatus.RuntimeError;
        }
        finally
        {
            if (status != AlgorithmStatus.Completed && status != AlgorithmStatus.Stopped && error == null)
                error = "实盘引擎已退出";
            systemHandlers.DisposeSafely();
            algorithmHandlers.DisposeSafely();
        }

        return (status, error);
    }

    private void ConfigureLeanLive(LiveTradingRuntimeRequest request, string resultsDir, string algoTypeName, string algoLocation)
    {
        var dataFolder = !string.IsNullOrWhiteSpace(_defaultDataFolder) ? _defaultDataFolder : BacktestEnvironment.LocateLeanDataFolder();

        Config.Set("environment", "live");
        Config.Set("live-mode", "true");
        Config.Set("object-store-root", Path.Combine(resultsDir, "storage"));
        Config.Set("algorithm-type-name", algoTypeName);
        Config.Set("algorithm-language", "CSharp");
        Config.Set("algorithm-location", algoLocation);
        Config.Set("data-folder", dataFolder);
        Config.Set("results-destination-folder", resultsDir);
        Config.Set("log-handler", "QuantConnect.Logging.CompositeLogHandler");
        Config.Set("messaging-handler", "QuantConnect.Messaging.Messaging");
        Config.Set("job-queue-handler", "QuantConnect.Queues.JobQueue");
        Config.Set("api-handler", "QuantConnect.Api.Api");
        // live handler 链
        Config.Set("setup-handler", "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler");
        Config.Set("result-handler", "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler");
        Config.Set("data-feed-handler", "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed");
        Config.Set("data-queue-handler", "CtpBrokerage");
        Config.Set("real-time-handler", "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler");
        Config.Set("transaction-handler", "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler");
        Config.Set("live-mode-brokerage", "CtpBrokerage");
        Config.Set("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager");
        Config.Set("data-provider", "QuantConnect.Lean.Engine.DataFeeds.DefaultDataProvider");
        Config.Set("data-channel-provider", "DataChannelProvider");
        Config.Set("object-store", "QuantConnect.Lean.Engine.Storage.LocalObjectStore");
        Config.Set("map-file-provider", "QuantConnect.Data.Auxiliary.LocalDiskMapFileProvider");
        Config.Set("factor-file-provider", "QuantConnect.Data.Auxiliary.LocalDiskFactorFileProvider");

        var parameters = new Dictionary<string, string>
        {
            ["instruments"] = string.Join(",", request.Instruments),
            ["strategy"] = request.StrategyName,
            ["cash"] = request.InitialCash.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["fast"] = request.FastPeriod.ToString(),
            ["slow"] = request.SlowPeriod.ToString(),
            ["resolution"] = ResolutionToLeanString(request.Resolution),
        };
        // LeanAlgorithm 引擎：算法经 GetParameter("symbol") 订阅（同回测的 request.Symbol 契约）——首个合约即主合约
        if (request.Engine == LiveStrategyEngine.LeanAlgorithm)
            parameters["symbol"] = request.Instruments.FirstOrDefault() ?? "";
        // StrategyParameters（策略参数元数据）优先覆盖 fast/slow
        if (request.StrategyParameters != null)
            foreach (var kv in request.StrategyParameters)
                parameters[kv.Key] = kv.Value;
        Config.Set("parameters", JObject.FromObject(parameters));
    }

    /// <summary>
    /// MarketDataConnectInput（纯字符串）→ CtpMarketDataConnect（Ctp.Domain）。实盘行情前置独立连接。
    /// 一律前置端口（2026-10-01 起）：本地柜台 = tcp://127.0.0.1:17802，真实前置同链路——四字段非空
    /// 校验通过才连（缺失返 null，调用方报「行情前置连接参数未注入」）。旧 "sim" 哨兵分支已随 sim
    /// JSON-RPC 直连退役删除。
    /// </summary>
    internal static CtpMarketDataConnect? MapConnect(MarketDataConnectInput? input)
    {
        if (input == null) return null;

        if (string.IsNullOrWhiteSpace(input.MdFrontAddress)
            || string.IsNullOrWhiteSpace(input.BrokerId)
            || string.IsNullOrWhiteSpace(input.UserId)
            || string.IsNullOrWhiteSpace(input.Password))
            return null;

        return new CtpMarketDataConnect
        {
            MdFrontAddress = input.MdFrontAddress!,
            BrokerId = input.BrokerId!,
            UserId = input.UserId!,
            Password = input.Password!,
        };
    }

    private static string ResolutionToLeanString(BarResolution r) => r switch
    {
        BarResolution.Daily => "Daily",
        BarResolution.Hour => "Hour",
        BarResolution.Minute => "Minute",
        BarResolution.Second => "Second",
        BarResolution.Tick => "Tick",
        _ => "Minute",
    };
}
