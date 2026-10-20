using System;
using System.Collections.Generic;

namespace Aether.Trading.Domain.Indicators.Conditions;

/// <summary>
/// 布尔条件契约（镜像 QuantConnect Lean「条件即布尔指标」：一个 rule 的 when 是一个布尔组合指标）。
/// 继承 <see cref="IIndicator"/> 以统一对待；额外暴露 <see cref="IsTrue"/>。组合而非解析——加新算子 = 加一个类。
/// </summary>
public interface ICondition : IIndicator
{
    /// <summary>当前 bar 上条件是否成立（= Value != 0）。</summary>
    bool IsTrue { get; }
}

/// <summary>操作数：每根 bar 解析出一个 decimal 值。</summary>
public abstract class Operand
{
    /// <summary>当前值。</summary>
    public abstract decimal Value { get; }
    /// <summary>底层指标是否就绪（字面量恒 true）。</summary>
    public abstract bool IsReady { get; }
}

/// <summary>指标引用操作数：取某指标的某输出。</summary>
internal sealed class IndicatorOperand : Operand
{
    private readonly IIndicator _indicator;
    private readonly string? _output;
    public IndicatorOperand(IIndicator indicator, string? output) { _indicator = indicator; _output = output; }
    public override decimal Value => _indicator.GetValue(_output);
    public override bool IsReady => _indicator.IsReady;
}

/// <summary>字面量操作数：固定数值。</summary>
internal sealed class LiteralOperand : Operand
{
    private readonly decimal _value;
    public LiteralOperand(decimal value) { _value = value; }
    public override decimal Value => _value;
    public override bool IsReady => true;
}

/// <summary>从对象构造操作数：字符串→指标引用（name 或 name.output）或数字字面；数字→字面。</summary>
internal static class OperandResolver
{
    public static Operand Resolve(object arg, IReadOnlyDictionary<string, IIndicator> indicators)
    {
        if (arg is string s)
        {
            string name = s; string? output = null;
            int dot = s.IndexOf('.');
            if (dot >= 0) { name = s.Substring(0, dot); output = s.Substring(dot + 1); if (output.Length == 0) output = null; }
            if (indicators.TryGetValue(name, out var ind)) return new IndicatorOperand(ind, output);
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lit)) return new LiteralOperand(lit);
            throw new ArgumentException($"无法解析操作数 '{s}'（既非已声明指标，也非数字）");
        }
        if (arg is long l) return new LiteralOperand(l);
        if (arg is double d) return new LiteralOperand((decimal)d);
        if (arg is decimal de) return new LiteralOperand(de);
        if (arg is int i) return new LiteralOperand(i);
        throw new ArgumentException($"不支持的操作数类型 {arg?.GetType().Name ?? "null"}");
    }
}

internal enum ComparisonOp { Gt, Lt, Gte, Lte }

/// <summary>比较条件：left op right。</summary>
internal sealed class ComparisonCondition : ICondition
{
    private static readonly IReadOnlyList<string> _outputs = new[] { "value" };
    private readonly Operand _left;
    private readonly Operand _right;
    private readonly ComparisonOp _op;
    public IReadOnlyList<string> Outputs => _outputs;
    public int RequiredBars => 0;
    public bool IsReady => _left.IsReady && _right.IsReady;
    public bool IsTrue { get; private set; }
    public decimal Value => IsTrue ? 1m : 0m;
    public decimal GetValue(string? output) => string.IsNullOrEmpty(output) ? Value : throw new ArgumentException("条件无命名输出", nameof(output));
    public ComparisonCondition(Operand left, ComparisonOp op, Operand right) { _left = left; _op = op; _right = right; }
    public void Update(MarketBar bar)
    {
        if (!IsReady) { IsTrue = false; return; }
        decimal l = _left.Value, r = _right.Value;
        IsTrue = _op switch
        {
            ComparisonOp.Gt => l > r,
            ComparisonOp.Lt => l < r,
            ComparisonOp.Gte => l >= r,
            ComparisonOp.Lte => l <= r,
            _ => false,
        };
    }
}

/// <summary>穿越条件基类：仅穿越当根为 true（镜像 Lean CrossAbove/CrossBelow）。</summary>
internal abstract class CrossCondition : ICondition
{
    private static readonly IReadOnlyList<string> _outputs = new[] { "value" };
    private readonly Operand _left;
    private readonly Operand _right;
    private bool _hasPrev;
    private decimal _prevLeft, _prevRight;
    public IReadOnlyList<string> Outputs => _outputs;
    public int RequiredBars => 0;
    public bool IsReady => _hasPrev;
    public bool IsTrue { get; private set; }
    public decimal Value => IsTrue ? 1m : 0m;
    public decimal GetValue(string? output) => string.IsNullOrEmpty(output) ? Value : throw new ArgumentException("条件无命名输出", nameof(output));
    protected CrossCondition(Operand left, Operand right) { _left = left; _right = right; }
    protected abstract bool Crosses(decimal prevL, decimal prevR, decimal curL, decimal curR);
    public void Update(MarketBar bar)
    {
        IsTrue = false;
        if (_left.IsReady && _right.IsReady)
        {
            decimal curL = _left.Value, curR = _right.Value;
            if (_hasPrev) IsTrue = Crosses(_prevLeft, _prevRight, curL, curR);
            _prevLeft = curL; _prevRight = curR; _hasPrev = true;
        }
        else
        {
            _hasPrev = false; // 未就绪重置，确保首个就绪 bar 不误判穿越
        }
    }
}

internal sealed class CrossAboveCondition : CrossCondition
{
    public CrossAboveCondition(Operand left, Operand right) : base(left, right) { }
    // 之前 <= 且现在 > ：上穿
    protected override bool Crosses(decimal prevL, decimal prevR, decimal curL, decimal curR) => prevL <= prevR && curL > curR;
}

internal sealed class CrossBelowCondition : CrossCondition
{
    public CrossBelowCondition(Operand left, Operand right) : base(left, right) { }
    protected override bool Crosses(decimal prevL, decimal prevR, decimal curL, decimal curR) => prevL >= prevR && curL < curR;
}

/// <summary>逻辑与：所有子条件成立。</summary>
internal sealed class AndCondition : ICondition
{
    private static readonly IReadOnlyList<string> _outputs = new[] { "value" };
    private readonly IReadOnlyList<ICondition> _conditions;
    public IReadOnlyList<string> Outputs => _outputs;
    public int RequiredBars => 0;
    public bool IsReady { get; private set; }
    public bool IsTrue { get; private set; }
    public decimal Value => IsTrue ? 1m : 0m;
    public decimal GetValue(string? output) => string.IsNullOrEmpty(output) ? Value : throw new ArgumentException("条件无命名输出", nameof(output));
    public AndCondition(IReadOnlyList<ICondition> conditions) { _conditions = conditions; }
    public void Update(MarketBar bar)
    {
        bool all = true, ready = _conditions.Count > 0;
        foreach (var c in _conditions) { c.Update(bar); if (!c.IsReady) ready = false; if (!c.IsTrue) all = false; }
        IsReady = ready; IsTrue = ready && all;
    }
}

/// <summary>逻辑或：任一子条件成立。</summary>
internal sealed class OrCondition : ICondition
{
    private static readonly IReadOnlyList<string> _outputs = new[] { "value" };
    private readonly IReadOnlyList<ICondition> _conditions;
    public IReadOnlyList<string> Outputs => _outputs;
    public int RequiredBars => 0;
    public bool IsReady { get; private set; }
    public bool IsTrue { get; private set; }
    public decimal Value => IsTrue ? 1m : 0m;
    public decimal GetValue(string? output) => string.IsNullOrEmpty(output) ? Value : throw new ArgumentException("条件无命名输出", nameof(output));
    public OrCondition(IReadOnlyList<ICondition> conditions) { _conditions = conditions; }
    public void Update(MarketBar bar)
    {
        bool any = false, ready = _conditions.Count > 0;
        foreach (var c in _conditions) { c.Update(bar); if (!c.IsReady) ready = false; if (c.IsTrue) any = true; }
        IsReady = ready; IsTrue = ready && any;
    }
}

/// <summary>逻辑非：子条件不成立。</summary>
internal sealed class NotCondition : ICondition
{
    private static readonly IReadOnlyList<string> _outputs = new[] { "value" };
    private readonly ICondition _inner;
    public IReadOnlyList<string> Outputs => _outputs;
    public int RequiredBars => 0;
    public bool IsReady => _inner.IsReady;
    public bool IsTrue { get; private set; }
    public decimal Value => IsTrue ? 1m : 0m;
    public decimal GetValue(string? output) => string.IsNullOrEmpty(output) ? Value : throw new ArgumentException("条件无命名输出", nameof(output));
    public NotCondition(ICondition inner) { _inner = inner; }
    public void Update(MarketBar bar) { _inner.Update(bar); IsTrue = _inner.IsReady && !_inner.IsTrue; }
}
