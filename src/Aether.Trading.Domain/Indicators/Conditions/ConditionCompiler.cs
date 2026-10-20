using System;
using System.Linq;

namespace Aether.Trading.Domain.Indicators.Conditions;

/// <summary>
/// 条件编译器：<see cref="ConditionDef"/> AST → <see cref="ICondition"/>（可执行的布尔组合指标）。
/// 递归编译——比较/穿越取叶子操作数，and/or/not 取嵌套子条件。未知 op 抛 <see cref="ArgumentException"/>。
/// </summary>
internal static class ConditionCompiler
{
    /// <summary>编译条件树。indicators 为已声明的指标实例（操作数引用解析用）。</summary>
    public static ICondition Compile(ConditionDef def, IReadOnlyDictionary<string, IIndicator> indicators)
        => CompileNode(def, indicators);

    private static ICondition CompileNode(ConditionDef def, IReadOnlyDictionary<string, IIndicator> indicators)
    {
        return def.Op switch
        {
            "gt" => new ComparisonCondition(Leaf(def, 0, indicators), ComparisonOp.Gt, Leaf(def, 1, indicators)),
            "lt" => new ComparisonCondition(Leaf(def, 0, indicators), ComparisonOp.Lt, Leaf(def, 1, indicators)),
            "gte" or "ge" => new ComparisonCondition(Leaf(def, 0, indicators), ComparisonOp.Gte, Leaf(def, 1, indicators)),
            "lte" or "le" => new ComparisonCondition(Leaf(def, 0, indicators), ComparisonOp.Lte, Leaf(def, 1, indicators)),
            "cross_above" or "crossabove" or "xabove" => new CrossAboveCondition(Leaf(def, 0, indicators), Leaf(def, 1, indicators)),
            "cross_below" or "crossbelow" or "xbelow" => new CrossBelowCondition(Leaf(def, 0, indicators), Leaf(def, 1, indicators)),
            "and" => new AndCondition(def.Args.Select(a => Child(a, indicators)).ToArray()),
            "or" => new OrCondition(def.Args.Select(a => Child(a, indicators)).ToArray()),
            "not" => new NotCondition(Child(def.Args[0], indicators)),
            _ => throw new ArgumentException($"未知条件算子 '{def.Op}'"),
        };
    }

    /// <summary>取第 index 个叶子操作数（比较/穿越算子用）。</summary>
    private static Operand Leaf(ConditionDef def, int index, IReadOnlyDictionary<string, IIndicator> indicators)
    {
        if (index >= def.Args.Count) throw new ArgumentException($"算子 '{def.Op}' 需要 {index + 1} 个操作数，实际 {def.Args.Count} 个");
        return OperandResolver.Resolve(def.Args[index], indicators);
    }

    /// <summary>取子条件（and/or/not 算子用）；arg 必须是嵌套 ConditionDef。</summary>
    private static ICondition Child(object arg, IReadOnlyDictionary<string, IIndicator> indicators)
        => arg is ConditionDef nested ? CompileNode(nested, indicators) : throw new ArgumentException("期望嵌套条件，得到叶子操作数");
}
