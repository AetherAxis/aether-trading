namespace Aether.Trading.Runtime.Domain;

/// <summary>
/// Lean 算法笔记源端口：host（Stockapp）实现，按 <c>NoteNodeType.LeanAlgorithm</c> 过滤笔记并映射成
/// <see cref="LeanAlgorithmNote"/>。由 <see cref="ILeanAlgorithmScriptingService"/> 消费（不直接依赖 Notes.Application）。
/// 与 <see cref="IStrategyNoteSource"/> 平行、互不耦合。
/// </summary>
public interface ILeanAlgorithmNoteSource
{
    Task<IReadOnlyList<LeanAlgorithmNote>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>保存 Lean 算法文件（按 name 定位；同名覆盖）。返回 Id（labs 相对路径）。</summary>
    Task<string> SaveAsync(string name, string content, CancellationToken cancellationToken = default);
}

/// <summary>Lean 算法笔记桥接 DTO（Id/Title/Content）。host 预过滤节点类型；Content 为 QCAlgorithm 子类 C# 源码。</summary>
public sealed record LeanAlgorithmNote(string Id, string Title, string Content);
