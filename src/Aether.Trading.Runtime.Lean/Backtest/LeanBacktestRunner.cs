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

// 消歧：Aether.Trading.Domain.BacktestResult 与 QuantConnect.Packets.BacktestResult 同名，本文件用的是领域结果类型。
using BacktestResult = Aether.Trading.Domain.BacktestResult;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 进程内运行一次 QuantConnect LEAN 回测，实现 <see cref="IBacktestRuntime"/>。
/// 镜像 lean-trading Launcher/Program.cs 的编排：Initializer.Start → GetSystemHandlers → JobQueue.NextJob →
/// GetAlgorithmHandlers → AlgorithmManager → Engine.Engine.Run。同步阻塞，调用方应自行 Task.Run 卸载。
/// 数据链路：engine.Run 之前用 <see cref="LocalDataPreFetcher"/> 预取 IAnalysisManager 历史 K 线写本地 CSV，
/// 供 CtpBar 自定义数据源读取——复用 Quotes 行情数据链，不走 LEAN 自带数据、不走外部 API。
/// </summary>
public sealed class LeanBacktestRunner : IBacktestRuntime
{
    private readonly ILogger<LeanBacktestRunner>? _logger;
    private readonly string? _defaultDataFolder;
    private readonly LocalDataPreFetcher _preFetcher;

    // Lean 全局只初始化一次（同进程多次回测复用）。per-run 状态都在 RunAsync 局部，避免并发串数据。
    private static bool _leanInitialized;
    private static readonly object _initLock = new();

    public LeanBacktestRunner(
        IAnalysisManager? analysisManager = null,
        ILogger<LeanBacktestRunner>? logger = null,
        string? defaultDataFolder = null)
    {
        _logger = logger;
        _defaultDataFolder = defaultDataFolder;
        _preFetcher = new LocalDataPreFetcher(analysisManager);
    }

    /// <inheritdoc />
    public async Task<BacktestResult> RunAsync(BacktestRuntimeRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // P0 守卫：Lean 全局静态（Config/Globals/Composer/WorkerThread/Log.LogHandler/Console）进程级，
        // 回测/实盘/LeanAlgo 三入口互斥（见 LeanGlobalLock）。锁覆盖整个 Lean 会话（setup→run→handler 释放）；
        // 结果映射读本地对象，在 RunCoreAsync 内完成。UI CanRun/CanStartLive 互查是早拒绝，本锁硬兜底
        // （AI BacktestTool 等绕过 UI 的调用者也受保护）。
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

    private async Task<BacktestResult> RunCoreAsync(BacktestRuntimeRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Lean 的算法/结果处理器在 setup 阶段可能 Console.SetOut 把算法的 Console 输出收进日志存储，
        // 导致回测后宿主进程的 Console 输出写到 Lean 的 TextWriter（而非原 stdout），输出"丢失"。
        // 保存原 stdout/stderr，在 finally 里恢复，保证回测后 Console 仍指向真实终端/重定向目标。
        var origOut = Console.Out;
        var origErr = Console.Error;

        // —— per-run 状态（局部）——
        var resultsDir = Path.Combine(Path.GetTempPath(), "AetherBacktests", Guid.NewGuid().ToString("N"));
        var logs = new List<string>();

        WorkerThread.Instance = new FreshWorkerThread();
        Composer.Instance.Reset();

        TradingGateway.Reset();
        TradingGateway.LogSink = msg => logs.Add(msg);

        string? preFetchError = await PrepareLocalDataAsync(request, resultsDir, logs);
        if (preFetchError != null)
        {
            TradingGateway.Reset();
            return Failed(request, preFetchError, logs);
        }

        ConfigureLean(request, resultsDir);
        // 实盘后回测：Globals.LiveMode 可能残留 true（实盘 Globals.Reset 拾取的），需重置拾取 live-mode=false
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
        if (job == null)
        {
            TradingGateway.Reset();
            return Failed(request, "LEAN JobQueue 未能产出 BacktestNodePacket（检查 Config algorithm-type-name/algorithm-location）", logs);
        }

        var algorithmHandlers = Initializer.GetAlgorithmHandlers();
        var algorithmManager = new AlgorithmManager(Globals.LiveMode, job);
        systemHandlers.LeanManager.Initialize(systemHandlers, algorithmHandlers, job, algorithmManager);

        var resultHandler = algorithmHandlers.Results as CapturingBacktestResultHandler;
        // 跳过 ResultsAnalyzer：它硬编码 SPY benchmark，本地 data-folder 的 SPY 数据不覆盖回测日期
        // → History(SPY) 空 → benchmarkSeries.First() on empty 崩。跳过后 equity curve 仍从 Charts 取。
        resultHandler?.DisableResultsAnalysis();
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
            _logger?.LogError(ex, "LEAN 回测运行异常");
            error = ex.Message;
            status = algorithmManager?.State ?? AlgorithmStatus.RuntimeError;
        }
        finally
        {
            if (status != AlgorithmStatus.Completed && error == null)
            {
                error = resultHandler?.LastError;
            }
            systemHandlers.DisposeSafely();
            algorithmHandlers.DisposeSafely();
            TradingGateway.Reset();
            // 恢复 Lean setup 可能重定向过的 Console 输出，避免宿主回测后 Console 输出丢失。
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }

        if (resultHandler == null)
        {
            return new BacktestResult
            {
                IsSuccess = false,
                Error = error ?? "未能创建 BacktestingResultHandler",
                AlgorithmStatus = status.ToString(),
                Logs = logs,
            };
        }

        // 分开 try/catch：StatisticsResults 可能因 equity curve 稀疏崩（ReadEquityCurve.First() on empty），
        // 但不影响从 Charts 取权益曲线。两者独立，一个崩不影响另一个。
        BacktestStatistics? stats = null;
        IReadOnlyList<EquityPoint> equity = System.Array.Empty<EquityPoint>();
        string? analysisError = null;
        try
        {
            var sr = resultHandler.StatisticsResults();
            stats = BacktestResultMapper.MapStatistics(sr);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LEAN 统计分析失败（equity curve 稀疏），统计可能缺失");
            analysisError = $"统计分析部分失败: {ex.Message}";
        }
        try
        {
            equity = BacktestResultMapper.MapEquityCurve(resultHandler);
        }
        catch
        {
            // equity curve 取失败，曲线空（不影响统计）
        }

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

    private static BacktestResult Failed(BacktestRuntimeRequest request, string error, List<string> logs) =>
        new()
        {
            IsSuccess = false,
            Error = error,
            AlgorithmStatus = AlgorithmStatus.RuntimeError.ToString(),
            Logs = logs,
        };

    /// <summary>
    /// 本地数据预取：用 LocalDataPreFetcher 取 IAnalysisManager 历史 K 线（期货，cache-first）写本地 CSV，
    /// 并把路径写入 LEAN Config（key=local-data-file:&lt;symbol&gt;，CtpBar.GetSource 据此读）。
    /// 返回错误信息（null 表示成功）。
    /// </summary>
    private async Task<string?> PrepareLocalDataAsync(BacktestRuntimeRequest request, string resultsDir, List<string> logs)
    {
        var freq = ResolutionToLean(request.Resolution).Freq;
        var csvDir = Path.Combine(resultsDir, "local-data");
        _logger?.LogInformation("[LeanBacktestRunner] PrepareLocalData - Symbol: {Symbol}, Resolution: {Resolution}, Freq: {Freq}, Range: {Start}~{End}, CsvDir: {CsvDir}",
            request.Symbol, request.Resolution, freq, request.StartDate, request.EndDate, csvDir);

        var (csvPath, barCount, error) = await _preFetcher.FetchAndWriteAsync(
            request.Symbol, freq, request.StartDate, request.EndDate, csvDir);

        _logger?.LogInformation("[LeanBacktestRunner] PrepareLocalData - CsvPath={CsvPath}, BarCount={BarCount}, Error={Error}",
            csvPath, barCount, error ?? "null");

        if (error != null) return error;

        // key 必须与算法 AddData<CtpBar>(ticker) 的 ticker 一致（= request.Symbol）
        Config.Set($"local-data-file:{request.Symbol}", csvPath);
        _logger?.LogInformation("[LeanBacktestRunner] Config set: local-data-file:{Symbol} = {CsvPath}", request.Symbol, csvPath);
        return null;
    }

    /// <summary>把 BacktestRuntimeRequest 落到 LEAN 全局 Config（JobQueue/Handlers 都从 Config 读）。</summary>
    private void ConfigureLean(BacktestRuntimeRequest request, string resultsDir)
    {
        // 算法程序集：共享宿主在本叶子（回测/实盘共用 TradingHostAlgorithm）
        var algorithmLocation = typeof(TradingHostAlgorithm).Assembly.Location;

        var dataFolder = !string.IsNullOrWhiteSpace(_defaultDataFolder)
            ? _defaultDataFolder
            : BacktestEnvironment.LocateLeanDataFolder();

        var (leanRes, _) = ResolutionToLean(request.Resolution);

        Config.Set("environment", "backtesting");
        Config.Set("live-mode", "false");   // 覆盖实盘残留的 live-mode=true，否则 JobQueue 产 LiveNodePacket 而非 BacktestNodePacket
        // 清除实盘残留的 live 专属 Config（同进程全局静态 Config 共享，实盘跑后残留）
        Config.Set("live-mode-brokerage", "");
        Config.Set("data-queue-handler", "");
        Config.Set("data-feed-handler", "QuantConnect.Lean.Engine.DataFeeds.FileSystemDataFeed");
        // object-store-root 必须设绝对可写路径：LEAN 默认 "./storage" 按进程 cwd 解析，双击启动 cwd="/" 会解析成只读 /storage 崩。
        Config.Set("object-store-root", Path.Combine(resultsDir, "storage"));
        // 通用宿主：algorithm-type-name 恒为 TradingHostAlgorithm（回测/实盘共用，常量无跨 run 泄漏）。
        // strategy 走 per-run parameters（Config "parameters" 每次 run 整体覆盖）。host 用 GetParameter 读。
        Config.Set("algorithm-type-name", "Aether.Trading.Runtime.Lean.TradingHostAlgorithm");
        Config.Set("algorithm-language", "CSharp");
        Config.Set("algorithm-location", algorithmLocation);
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

        // 透传策略参数 + per-run 的 strategy（host 用 GetParameter 读，避免 Config 跨 run 泄漏）。
        // Config.Set 内部走 Newtonsoft JToken 动态赋值，Dictionary 无法隐式转 JToken，必须显式转 JObject。
        var parameters = new Dictionary<string, string>
        {
            ["strategy"] = request.StrategyName,
            ["start"] = request.StartDate.ToString("yyyy-MM-dd"),
            ["end"] = request.EndDate.ToString("yyyy-MM-dd"),
            ["cash"] = request.InitialCash.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["symbol"] = request.Symbol,
            ["resolution"] = leanRes,
            ["fast"] = request.FastPeriod.ToString(),
            ["slow"] = request.SlowPeriod.ToString(),
        };
        // StrategyParameters（策略参数元数据）优先覆盖 fast/slow
        if (request.StrategyParameters != null)
            foreach (var kv in request.StrategyParameters)
                parameters[kv.Key] = kv.Value;
        Config.Set("parameters", JObject.FromObject(parameters));
        Config.Set("parameters", JObject.FromObject(parameters));
    }

    /// <summary>BarResolution → (Lean Resolution 字符串, IAnalysisManager freq)。freq：101=日K, 60=小时, 1=1分钟。</summary>
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

/// <summary>
/// WorkerThread 的 public-ctor 子类（WorkerThread() 是 protected），用于同进程多次回测时重置静态 Instance。
/// </summary>
internal sealed class FreshWorkerThread : WorkerThread
{
    public FreshWorkerThread() : base() { }
}

/// <summary>
/// 把 Lean 的 <see cref="Log"/> 转发到回测日志列表（结果可见），同时委托给原 handler（保留 console/file）。
/// </summary>
internal sealed class BacktestLogForwarder : ILogHandler
{
    private readonly ILogHandler _inner;
    private readonly Action<string> _forward;

    public BacktestLogForwarder(ILogHandler inner, Action<string> forward)
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
        if (text != null && (text.Contains("Error") || text.Contains("error") || text.Contains("Exception")
            || text.Contains("Engine.Run") || text.Contains("Initialize") || text.Contains("Setup")
            || text.Contains("Brokerage") || text.Contains("Ctp") || text.Contains("DataFeed")
            || text.Contains("Subscribe") || text.Contains("algorithm") || text.Contains("JobQueue")
            || text.Contains("Loader") || text.Contains("History") || text.Contains("Storage")))
        {
            _forward("[LEAN] " + text);
        }
    }

    public void Dispose() => _inner.Dispose();
}
