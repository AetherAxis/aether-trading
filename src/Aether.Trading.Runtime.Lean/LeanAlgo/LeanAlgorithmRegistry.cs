using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Aether.Trading.Domain;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// Lean 算法注册表（静态，Lean 叶子项目内）。与 <see cref="Aether.Trading.Domain.StrategyRegistry"/> 平行、互不耦合——
/// 存用户编译出的 <see cref="QCAlgorithm"/> 子类类型 + 磁盘 dll 路径 + 参数定义。
/// <see cref="LeanAlgorithmBacktestRunner"/> 按 name 取 type/dll 路径配置 Lean Config（algorithm-type-name/algorithm-location）。
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>：脚本服务后台线程 LoadAsync 写 + Lean runner/UI 读并发安全（镜像 StrategyRegistry）。
/// </summary>
public static class LeanAlgorithmRegistry
{
    private static readonly ConcurrentDictionary<string, Entry> _entries = new();

    public static void Register(string name, Type type, string assemblyPath, IReadOnlyList<StrategyParameterDef> paramDefs)
        => _entries[name] = new Entry(type, assemblyPath, paramDefs);

    public static bool Unregister(string name) => _entries.TryRemove(name, out _);

    public static Type? GetType(string name) => _entries.TryGetValue(name, out var e) ? e.Type : null;

    public static string? GetAssemblyPath(string name) => _entries.TryGetValue(name, out var e) ? e.AssemblyPath : null;

    public static IReadOnlyList<string> GetRegisteredNames() => _entries.Keys.ToArray();

    public static IReadOnlyList<StrategyParameterDef> GetParameterDefinitions(string name)
        => _entries.TryGetValue(name, out var e) ? e.ParamDefs : Array.Empty<StrategyParameterDef>();

    private sealed record Entry(Type Type, string AssemblyPath, IReadOnlyList<StrategyParameterDef> ParamDefs);
}
