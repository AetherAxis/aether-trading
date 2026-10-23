using System;
using System.IO;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Lean;
using QuantConnect.Algorithm;
using Xunit;

namespace Aether.Trading.Tests;

/// <summary>
/// 验证实验室「Lean 算法」默认模板（NoteNodeTypeRegistry.LeanAlgorithmTemplate）能通过
/// LeanAlgorithmScriptCompiler 编译——锁定模板与 Lean/Acme API 签名的一致（尤其 <c>_fast &gt; _slow</c>
/// 这种 IndicatorBase 比较、ConfigureFromConfig/AddCtpData/SetHoldings 等）。
/// </summary>
public class LeanAlgorithmTemplateCompileTests
{
    // 与 apps/Desktop/Acme.Desktop.Mvvm/NoteNodeTypeRegistry.cs 的 LeanAlgorithmTemplate 保持一致。
    private const string Template = @"using System.Collections.Generic;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Indicators;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Lean;

public class SmaCrossLeanAlgo : LeanAlgorithmBase
{
    private Symbol _symbol;
    private SimpleMovingAverage _fast;
    private SimpleMovingAverage _slow;

    public static IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
    {
        new StrategyParameterDef(""FastPeriod"", ""快线周期"", ""5""),
        new StrategyParameterDef(""SlowPeriod"", ""慢线周期"", ""20""),
    };

    public override void Initialize()
    {
        ConfigureFromConfig();

        int fast = GetParameter(""FastPeriod"") is { Length: > 0 } f && int.TryParse(f, out var fv) ? fv : 5;
        int slow = GetParameter(""SlowPeriod"") is { Length: > 0 } s && int.TryParse(s, out var sv) ? sv : 20;

        _symbol = AddCtpData(GetParameter(""symbol"") ?? ""600000"", Resolution.Daily);
        _fast = SMA(_symbol, fast, Resolution.Daily);
        _slow = SMA(_symbol, slow, Resolution.Daily);
        SetBenchmark(_symbol);
    }

    public override void OnData(Slice slice)
    {
        if (!(_fast.IsReady && _slow.IsReady)) return;
        if (_fast > _slow && !Portfolio.Invested)
            SetHoldings(_symbol, 1.0m);
        else if (_fast < _slow && Portfolio.Invested)
            Liquidate(_symbol);
    }
}";

    [Fact]
    public void Template_compiles()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "lean-algo-template-test");
        Directory.CreateDirectory(cacheDir);
        var ok = LeanAlgorithmScriptCompiler.TryCompile(Template, "SmaCrossLeanAlgo", cacheDir, out var type, out _, out var err);
        Assert.True(ok, err ?? "(无错误信息)");
        Assert.NotNull(type);
        Assert.True(typeof(QCAlgorithm).IsAssignableFrom(type));
    }
}
