using System;
using System.Collections.Generic;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Unit;

/// <summary>
/// The motion policy: On and Off are absolute, System follows Windows'
/// "Animation effects" - which a Remote Desktop session and anyone who turned
/// it off report as off - and keeps following it while the app runs.
/// </summary>
public sealed class MotionServiceTests
{
    [Theory]
    [InlineData(MotionPreference.System, true, true)]
    [InlineData(MotionPreference.System, false, false)]
    [InlineData(MotionPreference.On, false, true)]
    [InlineData(MotionPreference.Off, true, false)]
    public void Policy(MotionPreference preference, bool osAnimations, bool expected)
        => Assert.Equal(expected, MotionService.IsEnabled(preference, osAnimations));

    [Fact]
    public void TheDefaultIsSystem()
        => Assert.Equal(MotionPreference.System, AppSettings.Default.Motion);

    [Fact]
    public void AnUnknownStoredValue_NormalizesToSystem()
        => Assert.Equal(MotionPreference.System, (AppSettings.Default with { Motion = (MotionPreference)42 }).Normalize().Motion);

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void System_FollowsTheOsSettingAsItChanges()
    {
        var (service, system, _) = Create(MotionPreference.System, osAnimations: false);
        var seen = new List<bool>();
        using var _ = service.MotionEnabled.Subscribe(seen.Add);
        service.Attach(new Avalonia.Controls.Window());

        system.OsAnimationsEnabled = true;
        system.RaiseOsAnimationSettingChanged();

        Assert.True(service.IsMotionEnabled);
        Assert.Equal([false, true], seen);
    }

    [Fact]
    public void OnAndOff_IgnoreTheOsSetting()
    {
        var (service, system, settings) = Create(MotionPreference.Off, osAnimations: true);
        Assert.False(service.IsMotionEnabled);

        settings.SetCurrent(settings.Current with { Motion = MotionPreference.On });
        Assert.True(service.IsMotionEnabled);

        system.OsAnimationsEnabled = false;
        system.RaiseOsAnimationSettingChanged();
        Assert.True(service.IsMotionEnabled);
    }

    [Fact]
    public void ChangingTheSetting_TakesEffectAtOnce()
    {
        var (service, _, settings) = Create(MotionPreference.System, osAnimations: true);
        Assert.True(service.IsMotionEnabled);

        settings.SetCurrent(settings.Current with { Motion = MotionPreference.Off });

        Assert.False(service.IsMotionEnabled);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Attach_WatchesTheOsSettingOnce()
    {
        var (service, system, _) = Create(MotionPreference.System, osAnimations: true);

        service.Attach(new Avalonia.Controls.Window());
        service.Attach(new Avalonia.Controls.Window());

        Assert.Equal(1, system.OsAnimationWatchCount);
    }

    private static (MotionService Service, TestSystemInteractionService System, TestSettingsService Settings) Create(MotionPreference preference, bool osAnimations)
    {
        var settings = new TestSettingsService();
        settings.SetCurrent(AppSettings.Default with { Motion = preference });
        var system = new TestSystemInteractionService { OsAnimationsEnabled = osAnimations };
        return (new MotionService(settings, system), system, settings);
    }
}