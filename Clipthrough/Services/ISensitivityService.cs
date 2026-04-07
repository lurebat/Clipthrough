using System.Collections.Generic;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface ISensitivityService
{
    IReadOnlyList<SensitivityRule> GetDefaultRules();

    IReadOnlyList<SensitivityMatch> Scan(string? content);
}

