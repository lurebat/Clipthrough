using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface ISearchHistoryService
{
    Task SaveSearchAsync(string query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetRecentSearchesAsync(int limit = 20, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
