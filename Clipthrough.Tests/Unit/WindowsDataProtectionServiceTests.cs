using System;
using System.Text;
using Clipthrough.Services.Platform;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Real-DPAPI round-trip tests for <see cref="WindowsDataProtectionService"/>.
/// These exercise the actual Windows Data Protection API (CurrentUser scope) on
/// this machine. Cross-machine / cross-user non-portability of the protected
/// blob cannot be asserted here (a single process always shares one user
/// context) and is covered by docs/manual-tests.md (MT-1.2).
/// </summary>
public sealed class WindowsDataProtectionServiceTests
{
    private static readonly WindowsDataProtectionService Service = new();

    [Fact]
    public void CanPersistSecrets_IsTrue()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(Service.CanPersistSecrets);
    }

    [Fact]
    public void Protect_Unprotect_RoundTripsExactBytes()
    {
        if (!OperatingSystem.IsWindows()) return;

        var secret = Encoding.UTF8.GetBytes("p@ss-w!th-'quote-and-unicode-\u00e9\u2603");
        var protectedBytes = Service.Protect(secret);
        var recovered = Service.Unprotect(protectedBytes);

        Assert.Equal(secret, recovered);
    }

    [Fact]
    public void Protect_DoesNotLeakPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;

        var secret = Encoding.UTF8.GetBytes("sk-super-secret-token-12345");
        var protectedBytes = Service.Protect(secret);

        // Ciphertext must differ from plaintext and not contain it verbatim.
        Assert.NotEqual(secret, protectedBytes);
        Assert.DoesNotContain("sk-super-secret-token-12345", Encoding.UTF8.GetString(protectedBytes));
    }

    [Fact]
    public void Protect_EmptyInput_RoundTrips()
    {
        if (!OperatingSystem.IsWindows()) return;

        var recovered = Service.Unprotect(Service.Protect(Array.Empty<byte>()));
        Assert.Empty(recovered);
    }

    [Fact]
    public void Unprotect_TamperedBlob_Throws()
    {
        if (!OperatingSystem.IsWindows()) return;

        var protectedBytes = Service.Protect(Encoding.UTF8.GetBytes("original"));
        // Corrupt the blob so the integrity check fails — this is the condition the
        // load paths catch to drop the key rather than crash.
        protectedBytes[protectedBytes.Length / 2] ^= 0xFF;

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => Service.Unprotect(protectedBytes));
    }
}
