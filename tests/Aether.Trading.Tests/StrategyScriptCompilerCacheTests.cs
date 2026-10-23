using Aether.Trading.Domain;
using Aether.Trading.Scripting;
using Xunit;

namespace Aether.Trading.Tests;

/// <summary>
/// <see cref="StrategyScriptCompiler"/> 源码 hash 缓存 + 可卸载 ALC 回归守卫。
/// 同一源码二次编译应复用既有程序集（不重 Roslyn、不新装 ALC）——消除「每次 NotesChanged
/// 全量重编译 → Assembly.Load 进默认 ALC 永不卸载致无界堆积」泄漏。修复前每次 <c>Assembly.Load</c>
/// 新装程序集，二次结果 <c>Assembly</c> 引用不等；修复后命中缓存引用相等。
/// </summary>
public class StrategyScriptCompilerCacheTests
{
    private const string Source = @"using Aether.Trading.Domain;
public class CacheProbeStrategy : IStrategy
{
    public int WarmUpBars => 0;
    public void Initialize(StrategyParameters parameters) { }
    public SignalDirection EvaluateSignal(MarketBar bar) => SignalDirection.Hold;
}";

    [Fact]
    public void Same_source_reuses_cached_assembly_instead_of_reloading()
    {
        // 强制 Trading.Domain 程序集加载，确保编译引用集（AppDomain.GetAssemblies）含之。
        GC.KeepAlive(typeof(IStrategy));

        Assert.True(StrategyScriptCompiler.TryCompile(Source, out var first, out var err1), err1 ?? "(编译失败)");
        Assert.NotNull(first);
        Assert.True(StrategyScriptCompiler.TryCompile(Source, out var second, out var err2), err2 ?? "(编译失败)");
        Assert.NotNull(second);

        // 命中缓存 → 同一 Assembly 实例（修复前 Assembly.Load 每次新装，二者引用不等）。
        Assert.Same(first!.GetType().Assembly, second!.GetType().Assembly);
    }

    [Fact]
    public void Different_sources_load_distinct_assemblies()
    {
        GC.KeepAlive(typeof(IStrategy));

        var other = Source.Replace("CacheProbeStrategy", "CacheProbeStrategy2");
        Assert.True(StrategyScriptCompiler.TryCompile(Source, out var first, out _), "(编译失败)");
        Assert.True(StrategyScriptCompiler.TryCompile(other, out var second, out _), "(编译失败)");

        // 不同源码 → 不同 hash → 不同 ALC/程序集。
        Assert.NotSame(first!.GetType().Assembly, second!.GetType().Assembly);
    }

    [Fact]
    public void Ctor_throwing_strategy_returns_error_not_exception()
    {
        // 回归守卫：TryCompile 契约「失败 out error，不抛异常」。修复前策略类 ctor 抛异常会从
        // Activator.CreateInstance 直接逃逸——一条坏笔记中断整轮 LoadAsync 循环（后续策略不注册、
        // StrategiesLoaded 不触发、异常经 ContinueWith 未观察）。
        GC.KeepAlive(typeof(IStrategy));

        var source = @"using Aether.Trading.Domain;
public class CtorThrowProbeStrategy : IStrategy
{
    public CtorThrowProbeStrategy() => throw new System.InvalidOperationException(""init boom"");
    public int WarmUpBars => 0;
    public void Initialize(StrategyParameters parameters) { }
    public SignalDirection EvaluateSignal(MarketBar bar) => SignalDirection.Hold;
}";

        var ok = StrategyScriptCompiler.TryCompile(source, out var strategy, out var error);

        Assert.False(ok);
        Assert.Null(strategy);
        Assert.NotNull(error);
        Assert.Contains("CtorThrowProbeStrategy", error!);
        Assert.Contains("init boom", error!);
    }
}
