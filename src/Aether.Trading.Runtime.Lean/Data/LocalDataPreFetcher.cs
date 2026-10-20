using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Aether.Quotes.Domain;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 回测本地数据预取器（期货）。复用 IAnalysisManager.GetKlineAsync（cache-first：
/// SQLite 缓存优先，不足才从行情 provider 补充并回写缓存）取历史 K 线，按 [start,end] 过滤写本地 CSV
/// 供 LEAN CtpBar 读。不重复实现取数逻辑——与 Quotes 的指标计算共用同一条数据链，不走 LEAN 自带数据、不走外部 API。
/// 依赖 IAnalysisManager（Quotes 契约，由 AddQuotesApplication 注册）。
/// </summary>
internal sealed class LocalDataPreFetcher
{
    private readonly IAnalysisManager? _analysisManager;

    public LocalDataPreFetcher(IAnalysisManager? analysisManager)
    {
        _analysisManager = analysisManager;
    }

    /// <summary>能否预取（依赖是否注入）。未接 Quotes 时返 false。</summary>
    public bool IsAvailable => _analysisManager != null;

    /// <summary>
    /// 取数并写 CSV。返回（csvPath, barCount, error）。
    /// </summary>
    /// <param name="keyword">期货合约代码或名称（如 ag2610 / 白银）</param>
    /// <param name="freq">freq：101=日K, 60=小时, 1=1分钟</param>
    /// <param name="start">回测开始日期</param>
    /// <param name="end">回测结束日期</param>
    /// <param name="csvDir">CSV 输出目录</param>
    public async Task<(string CsvPath, int BarCount, string? Error)> FetchAndWriteAsync(
        string keyword, string freq, DateTime start, DateTime end, string csvDir)
    {
        if (!IsAvailable)
            return ("", 0, "行情服务未接入（IAnalysisManager 未注册，需 AddQuotesApplication）");

        var (quotes, security, error) = await _analysisManager!.GetKlineAsync(keyword, freq);
        if (quotes == null || security == null)
            return ("", 0, error ?? "无法获取行情数据");

        if (quotes.Count == 0)
            return ("", 0, $"行情数据为空: {security.Code} freq={freq}");

        // 按回测区间过滤
        var filtered = quotes
            .Where(q => q.Date.Date >= start.Date && q.Date.Date <= end.Date)
            .OrderBy(q => q.Date)
            .ToList();

        if (filtered.Count == 0)
            return ("", 0, $"区间内无行情数据（共 {quotes.Count} 根，最早 {quotes.Min(q => q.Date):yyyy-MM-dd HH:mm}，最晚 {quotes.Max(q => q.Date):yyyy-MM-dd HH:mm}），请调整回测日期范围");

        Directory.CreateDirectory(csvDir);
        var csvPath = Path.Combine(csvDir, $"{keyword}_{freq}.csv");
        // 日K写日期（yyyy-MM-dd），分钟/小时写完整时间戳——保留 intraday 时分秒，否则 LEAN 会把同日 bar 去重成 1 根
        var isDaily = freq == "101";
        var timeFmt = isDaily ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss";
        await using (var writer = new StreamWriter(csvPath))
        {
            writer.WriteLine("time,open,high,low,close,volume");
            foreach (var q in filtered)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{q.Date.ToString(timeFmt, CultureInfo.InvariantCulture)},{q.Open},{q.High},{q.Low},{q.Close},{q.Volume}"));
            }
        }

        return (csvPath, filtered.Count, null);
    }
}
