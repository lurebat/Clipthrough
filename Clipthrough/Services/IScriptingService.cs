using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IScriptingService
{
    Task<string> EvaluateAsync(string code, string input, CancellationToken cancellationToken = default);
}
