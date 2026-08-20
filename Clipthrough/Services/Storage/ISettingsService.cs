using System;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    bool HasSavedSettings { get; }

    /// <summary>
    /// Describes an unreadable settings file found during load, or null when the
    /// settings came from where they were supposed to.
    /// </summary>
    /// <remarks>
    /// A corrupt settings.json does not stop startup - the service falls back to
    /// a legacy copy in the database and then to defaults. Callers must surface
    /// this, because a silent settings reset leaves the user to rediscover their
    /// configuration one feature at a time.
    /// </remarks>
    string? LoadFault { get; }

    event EventHandler<AppSettings>? SettingsChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the whole settings record. Only safe when the caller genuinely
    /// owns every field; anything that changes a few fields on top of
    /// <see cref="Current"/> must use <see cref="UpdateAsync"/> instead, or it
    /// will roll back whatever else was saved since it read that snapshot.
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="mutate"/> to the settings inside the write gate
    /// and persists the result.
    ///
    /// Read-modify-write against <see cref="Current"/> followed by
    /// <see cref="SaveAsync"/> is not atomic: several places build a full record
    /// from a snapshot and save it, so two overlapping saves that started from
    /// the same snapshot both succeed and the later one silently reverts the
    /// earlier one's field. The gate only serialized the writes, so it prevented
    /// a torn file, never a lost update.
    ///
    /// <paramref name="mutate"/> runs while the gate is held, on whichever thread
    /// reached it, and may be called from a thread pool thread. It must be pure
    /// and must not read view-model or other UI-thread state — snapshot those
    /// values into locals before calling.
    /// </summary>
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default);
}

