using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.Quotes.Domain;

public interface IAnalysisManager
{
    Task<TechnicalAnalysisResult> CalculateAsync(
        string keyword,
        string freq,
        string indicators,
        int rowCount = 60,
        CancellationToken ct = default);

    /// <summary>
    /// 按 keyword（代码或名称）+ freq 取历史 K 线（原始 OHLCV）。
    /// cache-first：SQLite 缓存优先，仅当缓存缺失/过期才从行情 provider 补充并回写缓存。
    /// 返回 (Quotes, SecurityRef, Error)：成功 Error=null（Quotes/Security 非 null）；失败 Quotes=null 且 Error=原因。
    /// </summary>
    Task<(List<Quote>? Quotes, SecurityRef? Security, string? Error)> GetKlineAsync(string keyword, string freq, CancellationToken ct = default);
}
