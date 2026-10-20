using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Aether.Trading.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aether.Trading.Scripting;

/// <summary>Roslyn 动态编译策略源码 → IStrategy 实例。编译失败返回错误（前馈传感器信号）。</summary>
public static class StrategyScriptCompiler
{
    // 源码 hash → 已编译程序集（装在可卸载 ALC 内）。重加载热路径命中即复用：
    // 既免重复 Roslyn 编译（NotesChanged 500ms 防抖触发的全量重扫），又免 Assembly.Load 进默认 ALC
    // 永不卸载导致的无界程序集堆积。可卸载 ALC 让已删除/改名策略的旧版本可被回收。
    private static readonly ConcurrentDictionary<string, LoadedAssembly> _cache = new();

    /// <summary>编译源码并创建 IStrategy 实例。成功 out instance；失败 out error。
    /// 命中缓存复用既有程序集（不重编译、不新装 ALC）；未命中则编译进可卸载 ALC 并缓存。</summary>
    public static bool TryCompile(string source, out IStrategy? instance, out string? error)
    {
        instance = null;
        error = null;

        var hash = HashOf(source);
        if (_cache.TryGetValue(hash, out var cached))
            return ExtractInstance(cached.Assembly, out instance, out error);

        if (!Compile(source, out var loaded, out error))
            return false;
        _cache[hash] = loaded;
        return ExtractInstance(loaded.Assembly, out instance, out error);
    }

    /// <summary>卸载当前源码 hash 集合之外的所有缓存程序集（重加载后清已删除/改名的旧版本）。
    /// <see cref="AssemblyLoadContext.Unload"/> 是异步的——运行中的旧实例（如回测持有的 IStrategy）
    /// 保持其 ALC 存活直到自身释放，不会被提前作废；旧工厂被新一轮 Register 覆盖后 ALC 才真正回收。</summary>
    public static void UnloadStale(IReadOnlySet<string> currentHashes)
    {
        foreach (var kv in _cache)
        {
            if (currentHashes.Contains(kv.Key)) continue;
            if (!_cache.TryRemove(kv.Key, out var removed)) continue;
            try { removed.Alc.Unload(); }
            catch { /* 已在卸载中/竞态，忽略——下一轮会重编译替换 */ }
        }
    }

    /// <summary>源码 SHA256 hash（缓存键）。供脚本服务算本轮 currentHashes 集合调 <see cref="UnloadStale"/>。</summary>
    internal static string HashOf(string source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private static bool Compile(string source, out LoadedAssembly loaded, out string? error)
    {
        loaded = default;
        error = null;

        // 引用当前 AppDomain 已加载程序集（含 Aether.Trading.Domain.dll + System.Runtime/Linq 等）
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Distinct()
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "StrategyScript_" + System.Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            error = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            return false;
        }

        ms.Position = 0;
        // 装进可卸载 ALC（而非 Assembly.Load 进默认 ALC）——旧版本可经 UnloadStale 回收，不永久驻留。
        var alc = new CollectibleAlc();
        var asm = alc.LoadFromStream(ms);
        loaded = new LoadedAssembly(asm, alc);
        return true;
    }

    private static bool ExtractInstance(Assembly asm, out IStrategy? instance, out string? error)
    {
        instance = null;
        error = null;
        Type? strategyType;
        try
        {
            strategyType = asm.GetTypes().FirstOrDefault(t =>
                typeof(IStrategy).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 类型加载失败也走「编译失败」路径（TryCompile 契约：失败 out error，不抛异常）——
            // 否则一条坏笔记的异常会逃逸到调用方，中断整轮 LoadAsync 循环（后续策略不注册、StrategiesLoaded 不触发）。
            error = "类型加载失败: " + string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message ?? e!.GetType().Name));
            return false;
        }
        if (strategyType is null)
        {
            error = "源码中未找到实现 IStrategy 的非抽象类";
            return false;
        }

        try
        {
            instance = (IStrategy)Activator.CreateInstance(strategyType)!;
            return true;
        }
        catch (Exception ex)
        {
            // ctor 抛异常同理——按编译失败处理并反馈错误，不让调用方整轮中断。
            // Activator.CreateInstance 把 ctor 异常包进 TargetInvocationException（Message 是通用包装
            // 文案），拆包取 InnerException 反馈真实原因。
            var cause = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
            error = $"实例化 {strategyType.Name} 失败: {cause.Message}";
            return false;
        }
    }

    // 可卸载 ALC：isCollectible=true 让 Unload() 可回收其程序集。Load 返回 null →
    // 全部依赖（System.*、Aether.Trading.Domain 等）走默认 ALC 解析，保证 IStrategy 类型身份跨 ALC 一致
    // （IsAssignableFrom 成立）；只有编译出的策略程序集本身在本 ALC，卸载干净不牵连共享依赖。
    private sealed class CollectibleAlc : AssemblyLoadContext
    {
        public CollectibleAlc() : base(isCollectible: true) { }
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    private readonly record struct LoadedAssembly(Assembly Assembly, CollectibleAlc Alc);
}
