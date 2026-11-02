using System;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.Services.Platform;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// "Open source URL" (F-20). It called OpenPathAsync, which ShellExecutes
/// whatever it is given: a source URL of \\attacker\share\x.exe leaked the
/// user's NetNTLM hash over SMB or launched a local script, and every real
/// https:// source URL failed with FileNotFoundException.
/// </summary>
public sealed partial class MainWindowViewModelHeadlessTests
{
    [AvaloniaFact]
    public async Task OpenSourceUrl_UsesTheSchemeCheckedOpener()
    {
        using var scope = new TemporaryDatabaseScope();
        await PrepareInitializedScopeAsync(scope);
        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "from the web",
            ContentBytes = System.Text.Encoding.UTF8.GetBytes("from the web"),
            SourceUrl = "https://example.com/article",
        });

        var interaction = new TestSystemInteractionService();
        using var viewModel = CreateViewModel(scope, new TestClipboardMonitorService(), interaction, new TestSessionLogService());
        await viewModel.InitializeAsync();
        viewModel.SetMainWindowVisible(true);
        for (var i = 0; i < 200 && viewModel.SelectedClip is null; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(viewModel.SelectedClip);

        await viewModel.OpenSelectedClipSourceUrlCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("https://example.com/article", interaction.LastOpenedUrl);
        Assert.Equal(0, interaction.OpenPathCount);
    }
}