using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Aether.Quotes.Domain;

public interface IQuoteProvider
{
    bool IsSupported(string code, string freq, string type, string exchange);
    Task<List<Quote>> GetKlineAsync(string code, string freq, string type, string exchange, CancellationToken ct = default);
}
