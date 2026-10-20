using System;
using System.Collections.Generic;
using Aether.Trading.Domain.Indicators;
using Aether.Trading.Domain.Indicators.Conditions;

namespace Aether.Trading.Domain;

/// <summary>
/// YAML 声明式策略执行器：把 <see cref="StrategyProgram"/> 编译成指标 + 条件树，每根 bar 增量求值。
/// 纯域、Lean 中立——是又一个 <see cref="IStrategy"/> 实现，经 <see cref="StrategyRegistry"/> 注册，
/// 与 CodeStrategy 享同一契约/同一引擎可替换性。
/// </summary>
public sealed class YamlStrategy : IStrategy
{
    private readonly StrategyProgram _program;
    private Dictionary<string, IIndicator> _indicators = new();
    private List<(SignalDirection Signal, ICondition Cond)> _rules = new();

    public int WarmUpBars { get; private set; }
    public IReadOnlyList<StrategyParameterDef> ParameterDefinitions => _program.Params;

    public YamlStrategy(StrategyProgram program) => _program = program;

    public void Initialize(StrategyParameters parameters)
    {
        _indicators = new Dictionary<string, IIndicator>(StringComparer.OrdinalIgnoreCase);
        int warmup = 0;
        foreach (var (name, def) in _program.Indicators)
        {
            var ind = IndicatorFactory.Create(def.Type, def.Args, parameters);
            _indicators[name] = ind;
            if (ind.RequiredBars > warmup) warmup = ind.RequiredBars;
        }

        _rules = new List<(SignalDirection, ICondition)>();
        foreach (var rule in _program.Rules)
        {
            var cond = ConditionCompiler.Compile(rule.When, _indicators);
            _rules.Add((rule.Signal, cond));
        }

        WarmUpBars = warmup;
    }

    public SignalDirection EvaluateSignal(MarketBar bar)
    {
        // 1. 更新所有指标（条件操作数据此读取当前值）
        foreach (var ind in _indicators.Values) ind.Update(bar);
        // 2. top-down 首匹配规则
        foreach (var (signal, cond) in _rules)
        {
            cond.Update(bar);
            if (cond.IsTrue) return signal;
        }
        return _program.Default;
    }
}
