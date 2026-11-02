using Clipthrough.Localization;
using Clipthrough.Views;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The Re-encrypt dialog's new-password rules (OD-12a, S42). A password with
/// leading or trailing whitespace is refused rather than silently trimmed:
/// the user chose those characters, and keying the file with something other
/// than what they typed is how F-02 locked people out.
/// </summary>
public sealed class RekeyDialogValidationTests
{
    [Theory]
    [InlineData("correct horse ")]
    [InlineData(" correct horse")]
    [InlineData("correct horse\t")]
    [InlineData("\ncorrect horse")]
    public void NewPasswordWithEdgeWhitespace_IsRefused(string password)
    {
        Assert.Equal(AppText.RekeyPasswordEdgeWhitespace, RekeyDialog.ValidateNewPassword(password, password));
    }

    [Theory]
    [InlineData("correct horse")]
    [InlineData("a b c")]
    // Empty means "remove encryption", which is a legitimate request.
    [InlineData("")]
    public void OrdinaryNewPassword_IsAccepted(string password)
    {
        Assert.Null(RekeyDialog.ValidateNewPassword(password, password));
    }

    [Fact]
    public void MismatchedConfirmation_IsRefused()
    {
        Assert.Equal(AppText.RekeyPasswordMismatch, RekeyDialog.ValidateNewPassword("correct horse", "correct hors"));
    }
}
