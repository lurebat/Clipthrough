using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Controls;
using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// Whether UI motion is on: the Motion setting, with System following the OS
/// "Animation effects" switch and kept current as it changes.
/// </summary>
public interface IMotionService
{
    /// <summary>Whether motion is on right now.</summary>
    bool IsMotionEnabled { get; }

    /// <summary>The current value on subscribe, then every change.</summary>
    IObservable<bool> MotionEnabled { get; }

    /// <summary>
    /// Starts listening for the OS setting changing, on <paramref name="window"/>'s
    /// message loop. Only the first call has an effect.
    /// </summary>
    void Attach(Window window);
}

public sealed class MotionService : IMotionService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ISystemInteractionService _systemInteraction;
    private readonly BehaviorSubject<bool> _enabled;
    private IDisposable? _osWatch;

    public MotionService(ISettingsService settings, ISystemInteractionService systemInteraction)
    {
        _settings = settings;
        _systemInteraction = systemInteraction;
        _enabled = new BehaviorSubject<bool>(Resolve());
        _settings.SettingsChanged += OnSettingsChanged;
    }

    public bool IsMotionEnabled => _enabled.Value;

    public IObservable<bool> MotionEnabled => _enabled.DistinctUntilChanged();

    /// <summary>The policy itself: On and Off are absolute, System is the OS's answer.</summary>
    public static bool IsEnabled(MotionPreference preference, bool osAnimationsEnabled) => preference switch
    {
        MotionPreference.On => true,
        MotionPreference.Off => false,
        _ => osAnimationsEnabled,
    };

    public void Attach(Window window)
    {
        _osWatch ??= _systemInteraction.WatchOsAnimationSetting(window, Update);
        Update();
    }

    private void OnSettingsChanged(object? sender, AppSettings e) => Update();

    private bool Resolve() => IsEnabled(_settings.Current.Motion, _systemInteraction.AreOsAnimationsEnabled());

    private void Update()
    {
        var value = Resolve();
        if (value != _enabled.Value)
        {
            _enabled.OnNext(value);
        }
    }

    public void Dispose()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        _osWatch?.Dispose();
        _enabled.Dispose();
    }
}
