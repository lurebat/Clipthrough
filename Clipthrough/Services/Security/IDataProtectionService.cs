namespace Clipthrough.Services;

/// <summary>
/// Protects and unprotects sensitive data (e.g. database passwords).
/// Implementations should use platform-specific mechanisms (DPAPI on Windows, keychain on macOS, etc.).
/// </summary>
public interface IDataProtectionService
{
    /// <summary>
    /// True when this implementation can safely encrypt data for on-disk
    /// persistence (e.g. DPAPI on Windows). False for the no-op fallback on
    /// platforms without a supported keystore — callers must keep secrets
    /// in-memory only and never write them to disk.
    /// </summary>
    bool CanPersistSecrets { get; }

    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}
