namespace Aether.Trading.Runtime.Domain;

/// <summary>策略笔记源端口：提供策略候选笔记（由 host 实现，桥接 Notes 上下文）。
/// 让 Trading.Scripting 不直接依赖 Notes.Application，通过 Domain 端口隔离。</summary>
public interface IStrategyNoteSource
{
    /// <summary>获取所有策略笔记（host 已按节点类型预过滤：CodeStrategy → Code、Strategy → Yaml）。</summary>
    Task<IReadOnlyList<StrategyNote>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>保存策略文件（按 name 定位；同目录同名覆盖）。<paramref name="kind"/> 决定文件目录/扩展名。
    /// 返回 Id（labs 相对路径，如 <c>codestrategies/foo.cs</c>）。</summary>
    Task<string> SaveAsync(string name, string content, StrategyKind kind, CancellationToken cancellationToken = default);
}

/// <summary>策略种类：Code=C# 源码（Roslyn 编译），Yaml=声明式 YAML 配方。</summary>
public enum StrategyKind
{
    /// <summary>C# IStrategy 源码，Roslyn 编译。</summary>
    Code,
    /// <summary>声明式 YAML 策略配方，StrategyProgramYaml 解析。</summary>
    Yaml,
}

/// <summary>策略候选笔记（从 Notes 桥接的最小字段）。Content：Code=C# 源码，Yaml=YAML 配方。</summary>
public sealed record StrategyNote(string Id, string Title, string Content, StrategyKind Kind = StrategyKind.Code);
