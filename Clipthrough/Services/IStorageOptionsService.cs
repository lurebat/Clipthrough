using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IStorageOptionsService
{
    StorageOptions Current { get; }

    bool HasSavedConfig { get; }

    bool DatabaseExists { get; }

    Task SaveAsync(StorageOptions options, CancellationToken cancellationToken = default);
}
