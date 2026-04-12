using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Clipthrough.Services.Platform;

/// <summary>
/// Windows-specific data protection using DPAPI (DataProtectionScope.CurrentUser).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDataProtectionService : IDataProtectionService
{
    public byte[] Protect(byte[] data)
    {
        return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] data)
    {
        return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    }
}
