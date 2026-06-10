namespace Clipthrough.Services.Platform;

/// <summary>
/// Fallback data protection that performs no encryption.
/// Used on non-Windows platforms until a platform-specific implementation is available.
/// </summary>
public sealed class NoOpDataProtectionService : IDataProtectionService
{
    /// <summary>
    /// Returns false: on platforms without a real keystore, secrets must be
    /// kept in-memory only and never written to disk.
    /// </summary>
    public bool CanPersistSecrets => false;

    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}
