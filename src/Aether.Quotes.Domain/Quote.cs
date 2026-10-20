using System;

namespace Aether.Quotes.Domain;

public class Quote
{
    public int Id { get; private set; }
    public int SecurityId { get; private set; }
    public string Freq { get; private set; } = "101";
    public DateTime Date { get; private set; }
    public decimal Open { get; private set; }
    public decimal High { get; private set; }
    public decimal Low { get; private set; }
    public decimal Close { get; private set; }
    public decimal Volume { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    protected Quote() { }

    public Quote(int securityId, DateTime date, decimal open, decimal high, decimal low, decimal close, decimal volume, string freq = "101")
    {
        SecurityId = securityId;
        SetFreq(freq);
        SetDate(date);
        SetPrice(open, high, low, close);
        SetVolume(volume);
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetFreq(string freq)
    {
        if (string.IsNullOrWhiteSpace(freq))
            throw new ArgumentException("K线周期不能为空", nameof(freq));
        Freq = freq;
    }

    public void SetDate(DateTime date)
    {
        if (date == default)
            throw new ArgumentException("日期不能为空", nameof(date));
        Date = date;
    }

    public void SetPrice(decimal open, decimal high, decimal low, decimal close)
    {
        if (open < 0 || high < 0 || low < 0 || close < 0)
            throw new ArgumentException("价格不能为负数");

        if (high < low)
            throw new ArgumentException("最高价不能小于最低价");

        if (close < low || close > high)
            throw new ArgumentException("收盘价必须在最高价和最低价之间");

        if (open < low || open > high)
            throw new ArgumentException("开盘价必须在最高价和最低价之间");

        Open = open;
        High = high;
        Low = low;
        Close = close;
    }

    public void SetVolume(decimal volume)
    {
        if (volume < 0)
            throw new ArgumentException("成交量不能为负数", nameof(volume));
        Volume = volume;
    }

    public void SetSecurityId(int securityId)
    {
        if (securityId <= 0)
            throw new ArgumentException("证券ID无效", nameof(securityId));
        SecurityId = securityId;
    }

    public void UpdatePrice(decimal open, decimal high, decimal low, decimal close)
    {
        SetPrice(open, high, low, close);
        UpdatedAt = DateTime.UtcNow;
    }

    public decimal GetChange()
    {
        return Close - Open;
    }

    public decimal GetChangePercent()
    {
        if (Open == 0)
            return 0;
        return (Close - Open) / Open * 100;
    }
}
