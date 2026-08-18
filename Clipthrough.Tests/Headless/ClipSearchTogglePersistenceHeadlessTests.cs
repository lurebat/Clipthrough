using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Clipthrough.Models;

using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The two clip-search toggles survive a restart.
///
/// They used to exist twice on <see cref="AppSettings"/>: as
/// <c>UseFuzzyClipSearch</c> / <c>UseSemanticClipSearch</c>, which
/// <c>SaveSettingsAsync</c> wrote on every save and nothing ever read, and as
/// <c>LastUseFuzzyClipSearch</c> / <c>LastUseSemanticClipSearch</c>, which is
/// the pair that actually round-trips. One concept, two names, one of them
/// inert - and the inert one is what a reader meets first, because it has the
/// obvious name.
///
/// The dead pair is gone. These pin the surviving mechanism, so removing the
/// wrong one later fails here rather than quietly losing the toggles.
/// </summary>
public sealed class ClipSearchTogglePersistenceHeadlessTests
{
    /// <summary>
    /// Disposal is the deterministic seam: filter state is normally written by a
    /// 500 ms throttle, and <c>Dispose</c> performs the blocking flush that
    /// exists to catch the change made just before closing.
    /// </summary>
    private static TestSettingsService PersistAfterSetting(bool fuzzy, bool semantic)
    {
        var harness = MainWindowTestHarness.Create();
        var settings = harness.Settings;

        harness.ViewModel.UseFuzzyClipSearch = fuzzy;
        harness.ViewModel.UseSemanticClipSearch = semantic;
        Dispatcher.UIThread.RunJobs();

        harness.Dispose();
        return settings;
    }

    [AvaloniaFact]
    public void BothTogglesAreRememberedWhenTurnedOn()
    {
        var settings = PersistAfterSetting(fuzzy: true, semantic: true);

        Assert.True(settings.Current.LastUseFuzzyClipSearch);
        Assert.True(settings.Current.LastUseSemanticClipSearch);
    }

    /// <summary>
    /// Off has to be recorded as deliberately off, not merely left at a default.
    /// Asserting false alone would pass against a flush that wrote nothing at
    /// all - both fields already default to false - so this requires the save to
    /// have actually happened.
    /// </summary>
    [AvaloniaFact]
    public void TurningThemOffIsWrittenRatherThanLeftAtTheDefault()
    {
        var settings = PersistAfterSetting(fuzzy: false, semantic: false);

        Assert.True(settings.SaveCallCount > 0, "nothing was persisted, so 'false' here means nothing");
        Assert.False(settings.Current.LastUseFuzzyClipSearch);
        Assert.False(settings.Current.LastUseSemanticClipSearch);
    }
}