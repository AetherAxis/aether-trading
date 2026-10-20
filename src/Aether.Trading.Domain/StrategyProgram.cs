using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace Aether.Trading.Domain;

/// <summary>声明式策略程序（YAML 策略的数据模型，纯域对象）。</summary>
/// <param name="Params">参数定义（UI 据此渲染输入框）。</param>
/// <param name="Indicators">命名指标声明（name → 类型 + 参数）。</param>
/// <param name="Rules">规则列表：top-down 首匹配。</param>
/// <param name="Default">无规则匹配时的信号。</param>
public sealed record StrategyProgram(
    IReadOnlyList<StrategyParameterDef> Params,
    IReadOnlyDictionary<string, IndicatorDef> Indicators,
    IReadOnlyList<StrategyRuleDef> Rules,
    SignalDirection Default)
{
    public static StrategyProgram Empty { get; } = new(
        Array.Empty<StrategyParameterDef>(),
        new Dictionary<string, IndicatorDef>(),
        Array.Empty<StrategyRuleDef>(),
        SignalDirection.Hold);
}

/// <summary>指标声明：类型 + 参数（值为字符串，可含 {ParamKey} 模板，运行时替换）。</summary>
public sealed record IndicatorDef(string Type, IReadOnlyDictionary<string, string> Args);

/// <summary>规则：命中条件时发出信号。</summary>
public sealed record StrategyRuleDef(SignalDirection Signal, ConditionDef When);

/// <summary>
/// 条件 AST 节点。<paramref name="Op"/> ∈ gt/lt/gte/lte/cross_above/cross_below/and/or/not；
/// <paramref name="Args"/> 元素为叶子（string 指标引用/字面，或数字）或嵌套 <see cref="ConditionDef"/>。
/// <para>结构化 AST——无表达式解析器；加新算子 = 加一个 <c>ICondition</c> 类 + <see cref="Conditions.ConditionCompiler"/> 分支，老 YAML 不动。</para>
/// </summary>
public sealed record ConditionDef(string Op, IReadOnlyList<object> Args)
{
    /// <summary>
    /// 从已规整的对象图（IDictionary/IList/标量，YamlDotNet 或任何来源产出）解析条件 AST。
    /// 纯 .NET 类型操作，不依赖 YamlDotNet——故可在 Domain.Tests 脱库测试。
    /// 输入形如 <c>{ cross_above: [fast, slow] }</c> 或 <c>{ and: [ {gt:[fast,slow]}, {lt:[rsi,70]} ] }</c>。
    /// </summary>
    public static bool TryParse(object? raw, out ConditionDef? condition)
    {
        condition = null;
        if (raw is not IDictionary d) return false;
        if (d.Count != 1) return false;
        var en = d.GetEnumerator();
        if (!en.MoveNext()) return false;
        string op = en.Key?.ToString() ?? "";
        if (string.IsNullOrEmpty(op)) return false;
        if (!TryParseArgs(en.Value, out var args)) return false;
        condition = new ConditionDef(op, args);
        return true;
    }

    private static bool TryParseArgs(object? raw, out IReadOnlyList<object> args)
    {
        args = Array.Empty<object>();
        if (raw is IList list)
        {
            var result = new List<object>(list.Count);
            foreach (var item in list)
            {
                if (item is IDictionary nd)
                {
                    if (!TryParse(nd, out var nested)) return false;
                    result.Add(nested!);
                }
                else
                {
                    result.Add(NormalizeScalar(item));
                }
            }
            args = result;
            return true;
        }
        // 单参数（非列表）：包装成单元素列表（如 not 的单 arg 可能写成内联）
        if (raw is IDictionary nd2)
        {
            if (!TryParse(nd2, out var nested)) return false;
            args = new List<object> { nested! };
            return true;
        }
        args = new List<object> { NormalizeScalar(raw) };
        return true;
    }

    private static object NormalizeScalar(object? v) => v switch
    {
        null => "",
        bool or string or long or int or double or float or decimal => v,
        _ => v.ToString() ?? "",
    };
}
