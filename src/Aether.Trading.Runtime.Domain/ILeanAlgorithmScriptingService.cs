using Aether.Trading.Domain;

namespace Aether.Trading.Runtime.Domain;

/// <summary>
/// Lean 算法脚本加载服务契约：扫描 Lean 算法笔记 → Roslyn 编译 QCAlgorithm 子类 → 注册到 LeanAlgorithmRegistry。
/// 与 <c>IStrategyScriptingService</c> 平行、互不耦合。host 订阅 NotesChanged 后调 <see cref="Reload"/>。
/// </summary>
/// <remarks>
/// VM 不能引 Trading.Runtime.Lean（runtime-agnostic），故把 catalog 查询（<see cref="GetRegisteredNames"/>/
/// <see cref="GetParameterDefinitions"/>）折进本接口——VM 经此拿到 Lean 算法列表/参数定义，不直接碰静态 LeanAlgorithmRegistry。
/// </remarks>
public interface ILeanAlgorithmScriptingService
{
    /// <summary>扫描 Lean 算法笔记并编译注册。</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>触发防抖重加载（500ms）。host 订阅 NotesChanged 后调用。</summary>
    void Reload();

    /// <summary>编译失败的算法名→错误（供 UI/log 查看；成功的算法不在内）。</summary>
    IReadOnlyDictionary<string, string> LoadErrors { get; }

    /// <summary>Lean 算法加载完成（LeanAlgorithmRegistry 已更新）时触发，供 UI 刷新策略选择器。</summary>
    event Action? Loaded;

    /// <summary>已注册 Lean 算法名（供 UI 下拉）。</summary>
    IReadOnlyList<string> GetRegisteredNames();

    /// <summary>获取 Lean 算法的参数定义（UI 据此动态渲染参数输入框）。算法不存在或无参数返回空列表。</summary>
    IReadOnlyList<StrategyParameterDef> GetParameterDefinitions(string name);

    /// <summary>
    /// 即时编译验证 Lean 算法源码（不落库、不注册），返回编译错误（前馈传感器，供 CreateLeanAlgorithm 工具校验：
    /// AI 据此在落库前修订源码，避免笔记里堆坏代码）。成功的源码随后由 <see cref="LoadAsync"/>（NotesChanged 触发）
    /// 编译并注册到 LeanAlgorithmRegistry。编译含 QCAlgorithm 子类查找 + 依赖解析，与 <see cref="LoadAsync"/> 路径一致。
    /// </summary>
    (bool Success, string? Error) TryCompile(string source, string name);
}
