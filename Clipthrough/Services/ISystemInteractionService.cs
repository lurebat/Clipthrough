using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface ISystemInteractionService
{
    Task CopyTextAsync(string text);

    Task OpenPathAsync(string path);

    Task OpenContainingDirectoryAsync(string path);
}

