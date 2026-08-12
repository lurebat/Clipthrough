using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Unit;

public sealed class AppSettingsTests
{
    [Fact]
    public void DefaultMaxClipSize_Is2048Kilobytes()
    {
        Assert.Equal(2_048 * 1_024, AppSettings.Default.MaxClipSizeBytes);
    }

    [Fact]
    public void Defaults_EnableAutoUpdateWithGitHubReleaseFeed()
    {
        Assert.True(AppSettings.Default.EnableAutoUpdate);
        Assert.Equal(
            "https://github.com/lurebat/Clipthrough/releases/latest/download",
            AppSettings.Default.UpdateFeedUrl);
    }

    [Fact]
    public void Defaults_AutoApplyUpdatesOnStartup_IsOffByDefault()
    {
        // The aggressive "silently restart on next launch when an update was
        // previously downloaded" behavior is opt-in only; the friendly default
        // surfaces a notification with explicit user consent.
        Assert.False(AppSettings.Default.AutoApplyUpdatesOnStartup);
    }

    [Fact]
    public void Normalize_AllowsDefaultMaxClipSize()
    {
        var settings = new AppSettings
        {
            MaxClipSizeBytes = 2_048 * 1_024,
        };

        var normalized = settings.Normalize();

        Assert.Equal(2_048 * 1_024, normalized.MaxClipSizeBytes);
    }

    /// <summary>
    /// Every input here is round-tripped through <c>Normalize</c>, because that
    /// is what the real save path does and it is where the interesting hazard
    /// lives: Normalize rebuilds AiPresets and CustomHotkeys into fresh lists
    /// every time, so two saves of identical settings hold reference-unequal
    /// collections. A gate that leaned on the record's synthesized equality
    /// would report "changed" forever and buy nothing. Hand-built instances
    /// would hide that.
    /// </summary>
    private static AppSettings Saved(AppSettings settings) => settings.Normalize();

    [Fact]
    public void OnlyViewStateChanged_FilterToggleSave_IsViewStateOnly()
    {
        var before = Saved(AppSettings.Default);
        var after = Saved(before with
        {
            LastShowFavoritesOnly = true,
            LastUseRegexSearch = true,
            LastContentTypeFilters = new[] { ContentType.Image },
        });

        Assert.True(AppSettings.OnlyViewStateChanged(before, after));
    }

    /// <summary>
    /// The regression that motivates the whole gate: an unchanged save must
    /// still compare equal even though Normalize handed back new list
    /// instances. If this fails the gate never fires and the hotkeys are
    /// re-registered on every filter click again.
    /// </summary>
    [Fact]
    public void OnlyViewStateChanged_TwoSavesOfTheSameSettings_AreEqualDespiteRebuiltLists()
    {
        var withCollections = AppSettings.Default with
        {
            AiPresets = new[] { new AiPreset { Name = "Summarise", Prompt = "Summarise this" } },
            CustomHotkeys = new[] { new CustomHotkeyBinding { Id = "a", Gesture = "Ctrl+1", Target = "builtin:paste" } },
        };

        var before = Saved(withCollections);
        var after = Saved(withCollections);

        Assert.NotSame(before.AiPresets, after.AiPresets);
        Assert.NotSame(before.CustomHotkeys, after.CustomHotkeys);
        Assert.True(AppSettings.OnlyViewStateChanged(before, after));
    }

    [Fact]
    public void OnlyViewStateChanged_ReboundHotkey_IsNotViewStateOnly()
    {
        var before = Saved(AppSettings.Default);
        // Not a bare Alt+<letter>: Normalize migrates those back to the default,
        // so such a value would arrive here unchanged and prove nothing.
        var after = Saved(before with { ToggleWindowHotkey = "Ctrl+Shift+Q" });

        Assert.NotEqual(before.ToggleWindowHotkey, after.ToggleWindowHotkey);
        Assert.False(AppSettings.OnlyViewStateChanged(before, after));
    }

    [Fact]
    public void OnlyViewStateChanged_DisabledHotkey_IsNotViewStateOnly()
    {
        var before = Saved(AppSettings.Default);
        var after = Saved(before with { EnableToggleWindowHotkey = false });

        Assert.False(AppSettings.OnlyViewStateChanged(before, after));
    }

    [Fact]
    public void OnlyViewStateChanged_EditedCustomHotkey_IsNotViewStateOnly()
    {
        var before = Saved(AppSettings.Default with
        {
            CustomHotkeys = new[] { new CustomHotkeyBinding { Id = "a", Gesture = "Ctrl+1", Target = "builtin:paste" } },
        });
        var after = Saved(before with
        {
            CustomHotkeys = new[] { new CustomHotkeyBinding { Id = "a", Gesture = "Ctrl+2", Target = "builtin:paste" } },
        });

        Assert.False(AppSettings.OnlyViewStateChanged(before, after));
    }

    [Fact]
    public void OnlyViewStateChanged_EditedAiPreset_IsNotViewStateOnly()
    {
        var before = Saved(AppSettings.Default with
        {
            AiPresets = new[] { new AiPreset { Name = "Summarise", Prompt = "Summarise this" } },
        });
        var after = Saved(before with
        {
            AiPresets = new[] { new AiPreset { Name = "Summarise", Prompt = "Shorten this" } },
        });

        Assert.False(AppSettings.OnlyViewStateChanged(before, after));
    }

    /// <summary>
    /// StartWithWindows is not view state, so it must not be swallowed by the
    /// gate even though its own caller compares it separately.
    /// </summary>
    [Fact]
    public void OnlyViewStateChanged_StartWithWindows_IsNotViewStateOnly()
    {
        var before = Saved(AppSettings.Default);
        var after = Saved(before with { StartWithWindows = !before.StartWithWindows });

        Assert.False(AppSettings.OnlyViewStateChanged(before, after));
    }
}
