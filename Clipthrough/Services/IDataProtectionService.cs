namespace Clipthrough.Services;

/// <summary>
/// Protects and unprotects sensitive data (e.g. database passwords).
/// Implementations should use platform-specific mechanisms (DPAPI on Windows, keychain on macOS, etc.).
/// </summary>
public interface IDataProtectionService
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}
