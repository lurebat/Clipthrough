using System;
using System.Collections.Generic;
using System.Linq;

namespace Clipthrough.Services;

/// <summary>
/// Thrown when settings were persisted but one or more protected credentials
/// could not be written to (or removed from) their sidecar files.
/// <para>
/// The non-secret settings have already been saved when this is thrown; only the
/// named credentials are missing from disk. Callers should surface this to the
/// user rather than reporting an unqualified save success, because the affected
/// credentials will not survive a restart.
/// </para>
/// </summary>
public sealed class SecretPersistenceException : Exception
{
    public SecretPersistenceException(IReadOnlyList<string> secretNames)
        : base($"Settings were saved, but these credentials could not be stored securely: {string.Join(", ", secretNames)}.")
    {
        SecretNames = secretNames;
    }

    /// <summary>Display names of the credentials that failed to persist.</summary>
    public IReadOnlyList<string> SecretNames { get; }

    /// <summary>Comma-separated <see cref="SecretNames"/>, for status messages.</summary>
    public string SecretNameList => string.Join(", ", SecretNames.DefaultIfEmpty("credential"));
}
