using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.Trading.Scripting;

/// <summary>策略脚本加载服务：扫描策略笔记 → Roslyn 编译 → 注册到 StrategyRegistry。</summary>
public interface IStrategyScriptingService
{
    /// <summary>扫描策略笔记并编译注册。策略笔记约定：CodeStrategy 节点类型（host 预过滤），Content 为 IStrategy 实现类 C# 源码。</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>触发防抖重加载（500ms）。host 订阅 NotesChanged 后调用。</summary>
    void Reload();

    /// <summary>编译失败的策略名→错误（供 UI/log 查看；成功的策略不在内）。</summary>
    IReadOnlyDictionary<string, string> LoadErrors { get; }

    /// <summary>策略脚本加载完成（StrategyRegistry 已更新）时触发，供 UI 刷新策略选择器。</summary>
    event Action? StrategiesLoaded;
}
