using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aether.Quotes.Domain;

public interface IIndicatorCalculator
{
    Task<string> CalculateAsCsvAsync(List<Quote> quotes, string indicators, int rowCount = 60);
}
