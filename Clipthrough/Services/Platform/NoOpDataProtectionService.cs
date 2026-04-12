namespace Clipthrough.Services.Platform;

/// <summary>
/// Fallback data protection that performs no encryption.
/// Used on non-Windows platforms until a platform-specific implementation is available.
/// </summary>
public sealed class NoOpDataProtectionService : IDataProtectionService
{
    public byte[] Protect(byte[] data) => data;
    public byte[] Unprotect(byte[] data) => data;
}
