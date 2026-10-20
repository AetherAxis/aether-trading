using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;
using Microsoft.Extensions.Logging;

namespace Aether.Trading.Scripting;

/// <summary>策略脚本加载服务实现。扫描策略笔记 → 编译 → Register。
/// 通过 IStrategyNoteSource 端口读笔记（不直接依赖 Notes.Application）；host 订阅 NotesChanged 后调 Reload()。</summary>
public sealed class StrategyScriptingService : IStrategyScriptingService
{
    private readonly IStrategyNoteSource _source;
    private readonly ILogger<StrategyScriptingService> _logger;
    private readonly Dictionary<string, string> _loadErrors = new();
    private readonly HashSet<string> _registeredNames = new(); // 本服务动态注册的策略名，重加载时 unregister
    private readonly object _lock = new();
    private int _loading; // 防并发重入
    private CancellationTokenSource? _debounceCts;

    public event Action? StrategiesLoaded;

    public IReadOnlyDictionary<string, string> LoadErrors
    {
        get { lock (_lock) return new Dictionary<string, string>(_loadErrors); }
    }

    public StrategyScriptingService(IStrategyNoteSource source, ILogger<StrategyScriptingService> logger)
    {
        _source = source;
        _logger = logger;
    }

    /// <summary>host 订阅 NotesChanged 后调用此方法触发防抖重加载（500ms）。</summary>
    public void Reload()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;
        _ = Task.Delay(500, ct).ContinueWith(_ => LoadAsync(ct), TaskScheduler.Default);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 防并发重入
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return;
        try
        {
            var all = await _source.GetAllAsync(cancellationToken);
            var strategyNotes = all.ToList();

            // 先 unregister 上次动态注册的（清理已删除/改名的策略，避免选择器残留失效策略）
            foreach (var name in _registeredNames) StrategyRegistry.Unregister(name);
            _registeredNames.Clear();
            lock (_lock) _loadErrors.Clear();

            // 本轮编译的 Code 策略源码 hash 集合——扫完后调 UnloadStale 回收已删除/改名/改坏的旧版本 ALC。
            var currentHashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var note in strategyNotes)
            {
                if (string.IsNullOrWhiteSpace(note.Content))
                {
                    lock (_lock) _loadErrors[note.Title] = "策略源码为空";
                    continue;
                }

                if (note.Kind == StrategyKind.Yaml)
                {
                    // 声明式 YAML 策略：解析成 StrategyProgram → YamlStrategy。Code 路径不受影响。
                    if (StrategyProgramYaml.TryDeserialize(note.Content, out var program, out var parseErr) && program is not null)
                    {
                        try
                        {
                            var prog = program;
                            StrategyRegistry.Register(note.Title, () => new YamlStrategy(prog));
                            _registeredNames.Add(note.Title);
                            _logger.LogInformation("已加载 YAML 策略：{Name}", note.Title);
                        }
                        catch (Exception ex) // IndicatorFactory 未知类型 / 缺参等
                        {
                            lock (_lock) _loadErrors[note.Title] = ex.Message;
                            _logger.LogWarning("YAML 策略构建失败 {Name}：{Err}", note.Title, ex.Message);
                        }
                    }
                    else
                    {
                        lock (_lock) _loadErrors[note.Title] = parseErr ?? "YAML 策略解析失败";
                        _logger.LogWarning("YAML 策略解析失败 {Name}：{Err}", note.Title, parseErr);
                    }
                }
                else
                {
                    // Code 策略：Roslyn 编译 IStrategy 源码。命中缓存复用既有程序集，免每次 NotesChanged 全量重编译。
                    currentHashes.Add(StrategyScriptCompiler.HashOf(note.Content!));
                    if (StrategyScriptCompiler.TryCompile(note.Content!, out var instance, out var err) && instance is not null)
                    {
                        var type = instance.GetType();
                        var name = note.Title;
                        StrategyRegistry.Register(name, () => (IStrategy)Activator.CreateInstance(type)!);
                        _registeredNames.Add(name);
                        _logger.LogInformation("已加载策略脚本：{Name}", name);
                    }
                    else
                    {
                        lock (_lock) _loadErrors[note.Title] = err ?? "未知编译错误";
                        _logger.LogWarning("策略脚本编译失败 {Name}：{Err}", note.Title, err);
                    }
                }
            }
            // 回收本轮源码集合之外的旧编译产物（可卸载 ALC）——防长会话反复改策略致默认 ALC 无界堆积。
            StrategyScriptCompiler.UnloadStale(currentHashes);
            StrategiesLoaded?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }
}
