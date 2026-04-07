using System.Collections.Generic;
using AvaloniaApplication1.Models;

namespace AvaloniaApplication1.Services;

public interface ISensitivityService
{
    IReadOnlyList<SensitivityRule> GetDefaultRules();

    IReadOnlyList<SensitivityMatch> Scan(string? content);
}

