using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface ISensitivityService
{
    IReadOnlyList<SensitivityRule> GetDefaultRules();

    Task<IReadOnlyList<SensitivityRule>> GetRulesAsync(CancellationToken cancellationToken = default);

    Task SaveRulesAsync(IReadOnlyList<SensitivityRule> rules, CancellationToken cancellationToken = default);

    Task ReloadAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<SensitivityMatch> Scan(string? content);
}

