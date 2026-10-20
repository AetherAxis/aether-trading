using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Aether.Trading.Domain.Indicators;

/// <summary>
/// 指标工厂：type 字符串 + 参数（值可含 {ParamKey} 模板）+ 运行参数 → <see cref="IIndicator"/> 实例。
/// YAML 策略编译器与 C# 作者共用。未知 type 抛 <see cref="ArgumentException"/>（上层捕获 → 加载错误）。
/// </summary>
public static class IndicatorFactory
{
    /// <summary>按 type 创建指标。args 值先对 parameters 做 {ParamKey} 模板替换，再解析为 int/decimal。</summary>
    public static IIndicator Create(string type, IReadOnlyDictionary<string, string> args, StrategyParameters parameters)
    {
        string Resolved(string key)
        {
            if (args.TryGetValue(key, out var raw)) return Substitute(raw, parameters);
            throw new ArgumentException($"指标 '{type}' 缺少参数 '{key}'");
        }
        int Int(string key) => int.Parse(Resolved(key), CultureInfo.InvariantCulture);
        int IntOr(string key, int fallback)
            => args.TryGetValue(key, out var raw) ? int.Parse(Substitute(raw, parameters), CultureInfo.InvariantCulture) : fallback;
        decimal DecOr(string key, decimal fallback)
            => args.TryGetValue(key, out var raw) ? decimal.Parse(Substitute(raw, parameters), CultureInfo.InvariantCulture) : fallback;

        return type switch
        {
            "sma" => new SmaIndicator(Int("period")),
            "ema" => new EmaIndicator(Int("period")),
            "rsi" => new RsiIndicator(Int("period")),
            "atr" => new AtrIndicator(Int("period")),
            "macd" => new MacdIndicator(Int("fast"), Int("slow"), Int("signal")),
            "bollinger" or "bb" => new BollingerBandsIndicator(Int("period"), DecOr("stdDev", 2m)),
            "stochastic" or "stoch" or "kdj" => new StochasticIndicator(IntOr("kPeriod", 14), IntOr("dPeriod", 3)),
            _ => throw new ArgumentException($"未知指标类型 '{type}'"),
        };
    }

    /// <summary>{ParamKey} 模板替换：把参数键包在大括号里的占位符替换为运行参数值。无匹配保留原样。</summary>
    public static string Substitute(string raw, StrategyParameters parameters)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('{') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        int i = 0;
        while (i < raw.Length)
        {
            int open = raw.IndexOf('{', i);
            if (open < 0) { sb.Append(raw, i, raw.Length - i); break; }
            sb.Append(raw, i, open - i);
            int close = raw.IndexOf('}', open + 1);
            if (close < 0) { sb.Append(raw, open, raw.Length - open); break; }
            string key = raw.Substring(open + 1, close - open - 1);
            string? val = parameters.Get(key);
            sb.Append(val ?? raw.Substring(open, close - open + 1));
            i = close + 1;
        }
        return sb.ToString();
    }
}
