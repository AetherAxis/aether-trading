using System.Collections.Generic;

namespace Aether.Trading.Domain.Indicators;

// —— 单输出指标基类（Outputs = ["value"]，主输出 = _value）——

internal abstract class SingleOutputIndicator : IIndicator
{
    private static readonly IReadOnlyList<string> _outputs = new[] { "value" };
    public IReadOnlyList<string> Outputs => _outputs;
    public int RequiredBars { get; }
    public bool IsReady { get; protected set; }
    protected decimal Current { get; set; }
    public decimal Value => IsReady ? Current : 0m;
    public decimal GetValue(string? output) => string.IsNullOrEmpty(output) ? Value : throw new ArgumentException($"指标 {GetType().Name} 无输出 '{output}'", nameof(output));
    protected SingleOutputIndicator(int requiredBars) { RequiredBars = requiredBars; }
    public abstract void Update(MarketBar bar);
}

/// <summary>简单移动平均（Simple Moving Average）。</summary>
internal sealed class SmaIndicator : SingleOutputIndicator
{
    private readonly int _period;
    private readonly Queue<decimal> _window = new();
    private decimal _sum;
    public SmaIndicator(int period) : base(period > 0 ? period : 1) { _period = period > 0 ? period : 1; }
    public override void Update(MarketBar bar)
    {
        _window.Enqueue(bar.Close);
        _sum += bar.Close;
        if (_window.Count > _period) _sum -= _window.Dequeue();
        if (_window.Count >= _period) { Current = _sum / _period; IsReady = true; }
    }
}

/// <summary>指数移动平均（EMA）。种子 = 前 period 根的 SMA，之后递推 EMA。</summary>
internal sealed class EmaIndicator : SingleOutputIndicator
{
    private readonly int _period;
    private readonly decimal _k;
    private readonly Queue<decimal> _seed = new();
    public EmaIndicator(int period) : base(period > 0 ? period : 1)
    {
        _period = period > 0 ? period : 1;
        _k = 2m / (_period + 1m);
    }
    public override void Update(MarketBar bar)
    {
        if (!IsReady)
        {
            _seed.Enqueue(bar.Close);
            if (_seed.Count >= _period)
            {
                decimal sum = 0; foreach (var v in _seed) sum += v;
                Current = sum / _period;
                IsReady = true;
            }
        }
        else
        {
            Current = bar.Close * _k + Current * (1m - _k);
        }
    }
}

/// <summary>相对强弱指数（RSI，Wilder 平滑）。</summary>
internal sealed class RsiIndicator : SingleOutputIndicator
{
    private readonly int _period;
    private decimal _avgGain, _avgLoss;
    private decimal _prevClose;
    private int _count;
    private bool _seeded;
    public RsiIndicator(int period) : base((period > 0 ? period : 14) + 1) { _period = period > 0 ? period : 14; }
    public override void Update(MarketBar bar)
    {
        if (_count == 0) { _prevClose = bar.Close; _count = 1; return; }
        decimal change = bar.Close - _prevClose;
        _prevClose = bar.Close;
        decimal gain = change > 0 ? change : 0m;
        decimal loss = change < 0 ? -change : 0m;

        if (!_seeded)
        {
            _avgGain += gain; _avgLoss += loss;
            _count++;
            if (_count > _period)
            {
                _avgGain /= _period; _avgLoss /= _period;
                _seeded = true; IsReady = true;
                Current = ComputeRsi();
            }
        }
        else
        {
            _avgGain = (_avgGain * (_period - 1) + gain) / _period;
            _avgLoss = (_avgLoss * (_period - 1) + loss) / _period;
            Current = ComputeRsi();
        }
    }
    private decimal ComputeRsi()
    {
        if (_avgLoss == 0m) return 100m;
        decimal rs = _avgGain / _avgLoss;
        return 100m - 100m / (1m + rs);
    }
}

/// <summary>平均真实波幅（ATR，Wilder 平滑）。</summary>
internal sealed class AtrIndicator : SingleOutputIndicator
{
    private readonly int _period;
    private decimal _prevClose;
    private decimal _trSum;
    private int _count;
    private bool _seeded;
    public AtrIndicator(int period) : base((period > 0 ? period : 14) + 1) { _period = period > 0 ? period : 14; }
    public override void Update(MarketBar bar)
    {
        decimal tr;
        if (_count == 0) { tr = bar.High - bar.Low; }
        else { tr = Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - _prevClose), Math.Abs(bar.Low - _prevClose))); }
        _prevClose = bar.Close;

        if (!_seeded)
        {
            _trSum += tr; _count++;
            if (_count >= _period)
            {
                Current = _trSum / _period;
                _seeded = true; IsReady = true;
            }
        }
        else
        {
            Current = (Current * (_period - 1) + tr) / _period;
        }
    }
}

/// <summary>Bollinger Bands（middle=SMA，upper/lower=middle±stdDev·σ）。Outputs = upper/middle/lower；主输出=middle。</summary>
internal sealed class BollingerBandsIndicator : IIndicator
{
    private static readonly IReadOnlyList<string> _outputs = new[] { "upper", "middle", "lower" };
    private readonly int _period;
    private readonly decimal _stdDev;
    private readonly Queue<decimal> _window = new();
    private decimal _sum;
    public IReadOnlyList<string> Outputs => _outputs;
    public int RequiredBars { get; }
    public bool IsReady { get; private set; }
    public decimal Upper { get; private set; }
    public decimal Middle { get; private set; }
    public decimal Lower { get; private set; }
    public decimal Value => IsReady ? Middle : 0m;
    public BollingerBandsIndicator(int period, decimal stdDev)
    {
        _period = period > 0 ? period : 20;
        _stdDev = stdDev > 0 ? stdDev : 2m;
        RequiredBars = _period;
    }
    public decimal GetValue(string? output)
    {
        if (!IsReady) return 0m;
        return output switch
        {
            null or "" or "middle" => Middle,
            "upper" => Upper,
            "lower" => Lower,
            _ => throw new ArgumentException($"BollingerBands 无输出 '{output}'", nameof(output)),
        };
    }
    public void Update(MarketBar bar)
    {
        _window.Enqueue(bar.Close);
        _sum += bar.Close;
        if (_window.Count > _period) _sum -= _window.Dequeue();
        if (_window.Count >= _period)
        {
            Middle = _sum / _period;
            decimal sqSum = 0; foreach (var v in _window) sqSum += (v - Middle) * (v - Middle);
            decimal sigma = (decimal)Math.Sqrt((double)(sqSum / _period));
            Upper = Middle + _stdDev * sigma;
            Lower = Middle - _stdDev * sigma;
            IsReady = true;
        }
    }
}

/// <summary>MACD（macd=EMA(fast)−EMA(slow)，signal=EMA(signal) of macd，hist=macd−signal）。主输出=macd。</summary>
internal sealed class MacdIndicator : IIndicator
{
    private static readonly IReadOnlyList<string> _outputs = new[] { "macd", "signal", "hist" };
    private readonly EmaIndicator _fastEma;
    private readonly EmaIndicator _slowEma;
    private readonly EmaIndicator _signalEma;
    public IReadOnlyList<string> Outputs => _outputs;
    public int RequiredBars { get; }
    public bool IsReady { get; private set; }
    public decimal Macd { get; private set; }
    public decimal Signal { get; private set; }
    public decimal Hist { get; private set; }
    public decimal Value => IsReady ? Macd : 0m;
    public MacdIndicator(int fast, int slow, int signal)
    {
        _fastEma = new EmaIndicator(fast > 0 ? fast : 12);
        _slowEma = new EmaIndicator(slow > 0 ? slow : 26);
        _signalEma = new EmaIndicator(signal > 0 ? signal : 9);
        RequiredBars = _slowEma.RequiredBars + _signalEma.RequiredBars;
    }
    public decimal GetValue(string? output)
    {
        if (!IsReady) return 0m;
        return output switch
        {
            null or "" or "macd" => Macd,
            "signal" => Signal,
            "hist" => Hist,
            _ => throw new ArgumentException($"MACD 无输出 '{output}'", nameof(output)),
        };
    }
    public void Update(MarketBar bar)
    {
        _fastEma.Update(bar);
        _slowEma.Update(bar);
        if (_fastEma.IsReady && _slowEma.IsReady)
        {
            Macd = _fastEma.Value - _slowEma.Value;
            // signal EMA 喂 macd 值：构造一根仅 Close 有值的合成 bar
            _signalEma.Update(new MarketBar(bar.Time, Macd, Macd, Macd, Macd, 0m));
            if (_signalEma.IsReady)
            {
                Signal = _signalEma.Value;
                Hist = Macd - Signal;
                IsReady = true;
            }
        }
    }
}

/// <summary>随机指标（%K + %D=%K 的 SMA）。主输出=k。</summary>
internal sealed class StochasticIndicator : IIndicator
{
    private static readonly IReadOnlyList<string> _outputs = new[] { "k", "d" };
    private readonly int _kPeriod;
    private readonly int _dPeriod;
    private readonly Queue<decimal> _kWindow = new();
    private decimal _kSum;
    public IReadOnlyList<string> Outputs => _outputs;
    public int RequiredBars { get; }
    public bool IsReady { get; private set; }
    public decimal K { get; private set; }
    public decimal D { get; private set; }
    public decimal Value => IsReady ? K : 0m;
    public StochasticIndicator(int kPeriod, int dPeriod)
    {
        _kPeriod = kPeriod > 0 ? kPeriod : 14;
        _dPeriod = dPeriod > 0 ? dPeriod : 3;
        RequiredBars = _kPeriod + _dPeriod - 1;
    }
    public decimal GetValue(string? output)
    {
        if (!IsReady) return 0m;
        return output switch
        {
            null or "" or "k" => K,
            "d" => D,
            _ => throw new ArgumentException($"Stochastic 无输出 '{output}'", nameof(output)),
        };
    }
    public void Update(MarketBar bar)
    {
        // 滚动窗口存最近 kPeriod 根的 high/low，计算 %K
        _recentHighs.Enqueue(bar.High);
        _recentLows.Enqueue(bar.Low);
        if (_recentHighs.Count > _kPeriod) { _recentHighs.Dequeue(); _recentLows.Dequeue(); }
        if (_recentHighs.Count >= _kPeriod)
        {
            decimal highest = MaxQueue(_recentHighs);
            decimal lowest = MinQueue(_recentLows);
            decimal range = highest - lowest;
            K = range == 0m ? 50m : 100m * (bar.Close - lowest) / range;
            _kWindow.Enqueue(K);
            _kSum += K;
            if (_kWindow.Count > _dPeriod) _kSum -= _kWindow.Dequeue();
            if (_kWindow.Count >= _dPeriod)
            {
                D = _kSum / _dPeriod;
                IsReady = true;
            }
        }
    }
    private readonly Queue<decimal> _recentHighs = new();
    private readonly Queue<decimal> _recentLows = new();
    private static decimal MaxQueue(Queue<decimal> q) { decimal m = decimal.MinValue; foreach (var v in q) if (v > m) m = v; return m; }
    private static decimal MinQueue(Queue<decimal> q) { decimal m = decimal.MaxValue; foreach (var v in q) if (v < m) m = v; return m; }
}
