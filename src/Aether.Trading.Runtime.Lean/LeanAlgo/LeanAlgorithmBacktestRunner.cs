using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Aether.Quotes.Domain;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using QuantConnect;
using QuantConnect.Configuration;
using QuantConnect.Lean.Engine;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Util;

// 消歧：领域结果类型 vs Lean BacktestResult。
using BacktestResult = Aether.Trading.Domain.BacktestResult;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// Lean 算法回测 runner（<see cref="ILeanAlgorithmRuntime"/> 实现）。与 <see cref="LeanBacktestRunner"/> 平行、互不耦合——
/// 直接加载用户 <c>QCAlgorithm</c> 子类（绕过 <see cref="TradingHostAlgorithm"/> + IStrategy 壳）。
/// 复用同程序集 internal 工具：<see cref="LocalDataPreFetcher"/>（CSV 预取）、<see cref="BacktestResultMapper"/>（结果映射）、
/// <see cref="BacktestLogForwarder"/>（日志捕获）。<b>不碰 <see cref="TradingGateway"/></b>（静态状态紧耦合 IStrategy 路径）。
/// </summary>
public sealed class LeanAlgorithmBacktestRunner : ILeanAlgorithmRuntime
{
    private readonly IAnalysisManager? _analysisManager;
    private readonly ILogger<LeanAlgorithmBacktestRunner>? _logger;
    private readonly string? _defaultDataFolder;
    private readonly LocalDataPreFetcher _preFetcher;

    private static bool _leanInitialized;
    private static readonly object _initLock = new();

    public LeanAlgorithmBacktestRunner(
        IAnalysisManager? analysisManager = null,
        ILogger<LeanAlgorithmBacktestRunner>? logger = null,
        string? defaultDataFolder = null)
    {
        _analysisManager = analysisManager;
        _logger = logger;
        _defaultDataFolder = defaultDataFolder;
        _preFetcher = new LocalDataPreFetcher(analysisManager);
    }

    public async Task<BacktestResult> RunAsync(LeanAlgorithmBacktestRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // P0 守卫：与 LeanBacktestRunner/LeanLiveRunner 共用 LeanGlobalLock，三入口互斥（Lean 全局静态进程级）。
        await LeanGlobalLock.WaitAsync(ct);
        try
        {
            return await RunCoreAsync(request, ct);
        }
        finally
        {
            LeanGlobalLock.Release();
        }
    }

    private async Task<BacktestResult> RunCoreAsync(LeanAlgorithmBacktestRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var algoType = LeanAlgorithmRegistry.GetType(request.StrategyName);
        var algoPath = LeanAlgorithmRegistry.GetAssemblyPath(request.StrategyName);
        if (algoType is null || algoPath is null)
            return Failed(request, $"未注册的 Lean 算法: {request.StrategyName}");

        var origOut = Console.Out;
        var origErr = Console.Error;

        var resultsDir = Path.Combine(Path.GetTempPath(), "AetherLeanAlgoBacktests", Guid.NewGuid().ToString("N"));
        var logs = new List<string>();

        WorkerThread.Instance = new FreshWorkerThread();
        Composer.Instance.Reset();

        string? preFetchError = await PrepareLocalDataAsync(request, resultsDir, logs);
        if (preFetchError != null) return Failed(request, preFetchError, logs);

        ConfigureLean(request, resultsDir, algoType.FullName!, algoPath);
        Globals.Reset();

        lock (_initLock)
        {
            if (!_leanInitialized)
            {
                Initializer.Start();
                _leanInitialized = true;
            }
        }

        Log.LogHandler = new BacktestLogForwarder(Log.LogHandler, msg => logs.Add(msg));

        var systemHandlers = Initializer.GetSystemHandlers();
        var job = systemHandlers.JobQueue.NextJob(out var assemblyPath) as BacktestNodePacket;
        if (job == null) return Failed(request, "LEAN JobQueue 未能产出 BacktestNodePacket", logs);

        var algorithmHandlers = Initializer.GetAlgorithmHandlers();
        var algorithmManager = new AlgorithmManager(Globals.LiveMode, job);
        systemHandlers.LeanManager.Initialize(systemHandlers, algorithmHandlers, job, algorithmManager);

        var resultHandler = algorithmHandlers.Results as CapturingBacktestResultHandler;
        resultHandler?.DisableResultsAnalysis();   // 跳过 SPY 硬编码 benchmark 分析（本地无 SPY 数据）

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
            _logger?.LogError(ex, "Lean 算法回测运行异常");
            error = ex.Message;
            status = algorithmManager?.State ?? AlgorithmStatus.RuntimeError;
        }
        finally
        {
            if (status != AlgorithmStatus.Completed && error == null)
                error = resultHandler?.LastError;
            systemHandlers.DisposeSafely();
            algorithmHandlers.DisposeSafely();
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }

        if (resultHandler == null)
            return new BacktestResult { IsSuccess = false, Error = error ?? "未能创建 BacktestingResultHandler", AlgorithmStatus = status.ToString(), Logs = logs };

        BacktestStatistics? stats = null;
        IReadOnlyList<EquityPoint> equity = Array.Empty<EquityPoint>();
        string? analysisError = null;
        try { stats = BacktestResultMapper.MapStatistics(resultHandler.StatisticsResults()); }
        catch (Exception ex) { _logger?.LogError(ex, "Lean 算法统计分析失败"); analysisError = $"统计分析部分失败: {ex.Message}"; }
        try { equity = BacktestResultMapper.MapEquityCurve(resultHandler); }
        catch { /* equity 取失败不影响统计 */ }

        return new BacktestResult
        {
            IsSuccess = status == AlgorithmStatus.Completed && error == null,
            Error = error ?? analysisError,
            AlgorithmStatus = status.ToString(),
            Statistics = stats,
            EquityCurve = equity,
            Logs = logs,
        };
    }

    private static BacktestResult Failed(LeanAlgorithmBacktestRequest request, string error, List<string>? logs = null) =>
        new() { IsSuccess = false, Error = error, AlgorithmStatus = AlgorithmStatus.RuntimeError.ToString(), Logs = logs ?? new List<string>() };

    private async Task<string?> PrepareLocalDataAsync(LeanAlgorithmBacktestRequest request, string resultsDir, List<string> logs)
    {
        var freq = ResolutionToLean(request.Resolution).Freq;
        var csvDir = Path.Combine(resultsDir, "local-data");
        var (csvPath, _, error) = await _preFetcher.FetchAndWriteAsync(
            request.Symbol, freq, request.StartDate, request.EndDate, csvDir);
        if (error != null) return error;
        // CtpBar.GetSource 据此 key 读本地 CSV（与 IStrategy 路径同一约定）
        Config.Set($"local-data-file:{request.Symbol}", csvPath);
        return null;
    }

    private void ConfigureLean(LeanAlgorithmBacktestRequest request, string resultsDir, string algoTypeName, string algoLocation)
    {
        var dataFolder = !string.IsNullOrWhiteSpace(_defaultDataFolder)
            ? _defaultDataFolder
            : BacktestEnvironment.LocateLeanDataFolder();

        var (leanRes, _) = ResolutionToLean(request.Resolution);

        Config.Set("environment", "backtesting");
        Config.Set("live-mode", "false");
        Config.Set("live-mode-brokerage", "");
        Config.Set("data-queue-handler", "");
        Config.Set("data-feed-handler", "QuantConnect.Lean.Engine.DataFeeds.FileSystemDataFeed");
        Config.Set("object-store-root", Path.Combine(resultsDir, "storage"));
        // 用户 QCAlgorithm 子类——直接加载，不经 TradingHostAlgorithm
        Config.Set("algorithm-type-name", algoTypeName);
        Config.Set("algorithm-language", "CSharp");
        Config.Set("algorithm-location", algoLocation);
        Config.Set("data-folder", dataFolder);
        Config.Set("results-destination-folder", resultsDir);
        Config.Set("log-handler", "QuantConnect.Logging.CompositeLogHandler");
        Config.Set("messaging-handler", "QuantConnect.Messaging.Messaging");
        Config.Set("job-queue-handler", "QuantConnect.Queues.JobQueue");
        Config.Set("api-handler", "QuantConnect.Api.Api");
        Config.Set("result-handler", "Aether.Trading.Runtime.Lean.CapturingBacktestResultHandler");
        Config.Set("data-provider", "QuantConnect.Lean.Engine.DataFeeds.DefaultDataProvider");
        Config.Set("data-channel-provider", "DataChannelProvider");
        Config.Set("object-store", "QuantConnect.Lean.Engine.Storage.LocalObjectStore");
        Config.Set("map-file-provider", "QuantConnect.Data.Auxiliary.LocalDiskMapFileProvider");
        Config.Set("factor-file-provider", "QuantConnect.Data.Auxiliary.LocalDiskFactorFileProvider");
        Config.Set("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager");
        Config.Set("setup-handler", "QuantConnect.Lean.Engine.Setup.BacktestingSetupHandler");
        Config.Set("history-provider", "QuantConnect.Lean.Engine.HistoricalData.SubscriptionDataReaderHistoryProvider");
        Config.Set("real-time-handler", "QuantConnect.Lean.Engine.RealTime.BacktestingRealTimeHandler");
        Config.Set("transaction-handler", "QuantConnect.Lean.Engine.TransactionHandlers.BacktestingTransactionHandler");

        // 透传 run 参数 + 用户算法参数（算法经 GetParameter / LeanAlgorithmBase.ConfigureFromConfig 读）
        var parameters = new Dictionary<string, string>
        {
            ["start"] = request.StartDate.ToString("yyyy-MM-dd"),
            ["end"] = request.EndDate.ToString("yyyy-MM-dd"),
            ["cash"] = request.InitialCash.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["symbol"] = request.Symbol,
            ["resolution"] = leanRes,
        };
        if (request.StrategyParameters != null)
            foreach (var kv in request.StrategyParameters) parameters[kv.Key] = kv.Value;
        Config.Set("parameters", JObject.FromObject(parameters));
    }

    private static (string LeanRes, string Freq) ResolutionToLean(BarResolution r) => r switch
    {
        BarResolution.Daily => ("Daily", "101"),
        BarResolution.Hour => ("Hour", "60"),
        BarResolution.Minute => ("Minute", "1"),
        BarResolution.Second => ("Second", "1"),
        BarResolution.Tick => ("Tick", "1"),
        _ => ("Daily", "101"),
    };
}
