using System;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    bool HasSavedSettings { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

