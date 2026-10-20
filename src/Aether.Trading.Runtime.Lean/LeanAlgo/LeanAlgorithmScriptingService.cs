using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;
using Microsoft.Extensions.Logging;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// Lean 算法脚本加载服务实现。扫描 Lean 算法笔记 → Roslyn 编译 QCAlgorithm 子类 → 注册到 <see cref="LeanAlgorithmRegistry"/>。
/// 与 <c>StrategyScriptingService</c> 平行、互不耦合。通过 <see cref="ILeanAlgorithmNoteSource"/> 端口读笔记（不直接依赖 Notes.Application）。
/// </summary>
public sealed class LeanAlgorithmScriptingService : ILeanAlgorithmScriptingService
{
    private readonly ILeanAlgorithmNoteSource _source;
    private readonly string _cacheDir;
    private readonly ILogger<LeanAlgorithmScriptingService> _logger;
    private readonly Dictionary<string, string> _loadErrors = new();
    private readonly HashSet<string> _registeredNames = new();
    private readonly object _lock = new();
    private int _loading;
    private CancellationTokenSource? _debounceCts;

    public event Action? Loaded;

    public IReadOnlyDictionary<string, string> LoadErrors
    {
        get { lock (_lock) return new Dictionary<string, string>(_loadErrors); }
    }

    public LeanAlgorithmScriptingService(
        ILeanAlgorithmNoteSource source,
        string cacheDir,
        ILogger<LeanAlgorithmScriptingService> logger)
    {
        _source = source;
        _cacheDir = cacheDir;
        _logger = logger;
    }

    public void Reload()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;
        _ = Task.Delay(500, ct).ContinueWith(_ => LoadAsync(ct), TaskScheduler.Default);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return;
        try
        {
            var all = (await _source.GetAllAsync(cancellationToken)).ToList();

            foreach (var name in _registeredNames) LeanAlgorithmRegistry.Unregister(name);
            _registeredNames.Clear();
            lock (_lock) _loadErrors.Clear();

            // 本轮 Lean 算法（名+源码）——扫完后调 CleanupStaleDlls 清已删除/改名/改坏的旧 dll。
            var currentAlgos = new List<(string Name, string Source)>();
            foreach (var note in all)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(note.Content))
                    {
                        lock (_lock) _loadErrors[note.Title] = "算法源码为空";
                        continue;
                    }

                    currentAlgos.Add((note.Title, note.Content!));
                    if (LeanAlgorithmScriptCompiler.TryCompile(note.Content!, note.Title, _cacheDir, out var type, out _, out var err) && type is not null)
                    {
                        var paramDefs = LeanAlgorithmScriptCompiler.ReadParameterDefinitions(type);
                        LeanAlgorithmRegistry.Register(note.Title, type, type.Assembly.Location, paramDefs);
                        _registeredNames.Add(note.Title);
                        lock (_lock) _loadErrors.Remove(note.Title);
                        _logger.LogInformation("已加载 Lean 算法：{Name}", note.Title);
                    }
                    else
                    {
                        lock (_lock) _loadErrors[note.Title] = err ?? "未知编译错误";
                        _logger.LogWarning("Lean 算法编译失败 {Name}：{Err}", note.Title, err);
                    }
                }
                catch (Exception ex)
                {
                    // 单条笔记异常不致整批加载中断。
                    lock (_lock) _loadErrors[note.Title] = "加载异常：" + ex.Message;
                    _logger.LogError(ex, "Lean 算法加载异常 {Name}", note.Title);
                }
            }
            // 清理本轮源码集合之外的旧落盘 dll——防反复改算法致 cacheDir 无界堆积（dll 只增不删）。
            LeanAlgorithmScriptCompiler.CleanupStaleDlls(_cacheDir, currentAlgos);
            Loaded?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    public IReadOnlyList<string> GetRegisteredNames() => LeanAlgorithmRegistry.GetRegisteredNames();

    public IReadOnlyList<StrategyParameterDef> GetParameterDefinitions(string name)
        => LeanAlgorithmRegistry.GetParameterDefinitions(name);

    public (bool Success, string? Error) TryCompile(string source, string name)
    {
        // 复用 LoadAsync 的编译路径（含落盘 dll + LoadFrom 找 QCAlgorithm 子类 + 依赖解析），
        // 确保前馈校验与真实加载一致。不注册——仅返回错误供 CreateLeanAlgorithm 工具决策。
        if (LeanAlgorithmScriptCompiler.TryCompile(source, name, _cacheDir, out var type, out _, out var err) && type is not null)
            return (true, null);
        return (false, err);
    }
}
