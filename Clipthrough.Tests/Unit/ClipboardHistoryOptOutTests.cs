using System;
using System.Threading.Tasks;
using Avalonia.Input;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// Clipboard-history opt-out (F-03, OD-10b, S39). Chrome's and Edge's
/// password managers, and Bitwarden desktop, publish a copied password with
/// CanIncludeInClipboardHistory = 0 and neither of the two formats the monitor
/// honoured, so the password was captured, indexed and kept.
/// </summary>
public sealed class ClipboardHistoryOptOutTests
{
    private const string OptOutFormat = "CanIncludeInClipboardHistory";

    private static DataTransfer PasswordCopy(byte[]? optOut)
    {
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText("hunter2-password-from-chrome");
        if (optOut is not null)
        {
            item.Set(DataFormat.CreateBytesPlatformFormat(OptOutFormat), optOut);
        }

        transfer.Add(item);
        return transfer;
    }

    private static readonly byte[] DwordZero = [0, 0, 0, 0];
    private static readonly byte[] DwordOne = [1, 0, 0, 0];

    [Fact]
    public async Task OptedOutContent_IsIgnored_WhenTheSettingIsOn()
        => Assert.True(await ClipboardMonitorService.ShouldIgnoreClipboardAsync(PasswordCopy(DwordZero), honorHistoryOptOut: true));

    [Fact]
    public async Task OptedOutContent_IsCaptured_WhenTheSettingIsOff()
        => Assert.False(await ClipboardMonitorService.ShouldIgnoreClipboardAsync(PasswordCopy(DwordZero), honorHistoryOptOut: false));

    [Fact]
    public async Task ContentThatAllowsHistory_IsCaptured()
        => Assert.False(await ClipboardMonitorService.ShouldIgnoreClipboardAsync(PasswordCopy(DwordOne), honorHistoryOptOut: true));

    [Fact]
    public async Task ContentWithoutTheFormat_IsCaptured()
        => Assert.False(await ClipboardMonitorService.ShouldIgnoreClipboardAsync(PasswordCopy(null), honorHistoryOptOut: true));

    [Fact]
    public async Task AMalformedValue_IsCaptured()
        => Assert.False(await ClipboardMonitorService.ShouldIgnoreClipboardAsync(PasswordCopy([0, 0]), honorHistoryOptOut: true));

    [Fact]
    public void TheSettingDefaultsOn_IncludingForASettingsFileWrittenBeforeIt()
    {
        Assert.True(AppSettings.Default.HonorClipboardHistoryOptOut);
        Assert.True(new AppSettings().Normalize().HonorClipboardHistoryOptOut);

        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            "{\"themeMode\":1}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.True(legacy.Normalize().HonorClipboardHistoryOptOut);
    }
}