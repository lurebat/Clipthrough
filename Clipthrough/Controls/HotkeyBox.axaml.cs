using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Clipthrough.Models;

namespace Clipthrough.Controls;

public partial class HotkeyBox : UserControl
{
    public static readonly StyledProperty<string> HotkeyProperty =
        AvaloniaProperty.Register<HotkeyBox, string>(
            nameof(Hotkey),
            defaultValue: string.Empty,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay,
            coerce: (_, v) => v ?? string.Empty);

    private TextBox? _text;
    private Button? _record;
    private Button? _clear;
    private bool _recording;

    public HotkeyBox()
    {
        Focusable = true;
        InitializeComponent();
    }

    public string Hotkey
    {
        get => GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _text = this.FindControl<TextBox>("PART_Text");
        _record = this.FindControl<Button>("PART_Record");
        _clear = this.FindControl<Button>("PART_Clear");

        if (_text is not null)
        {
            _text.Text = Hotkey;
            _text.TextChanged += (_, _) =>
            {
                if (!_recording && _text.Text is { } t && t != Hotkey)
                {
                    Hotkey = t;
                }
            };
        }

        if (_record is not null)
        {
            _record.Click += OnRecordClick;
        }

        if (_clear is not null)
        {
            _clear.Click += OnClearClick;
        }

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        this.GetObservable(HotkeyProperty).Subscribe(v =>
        {
            if (_text is not null && _text.Text != v)
            {
                _text.Text = v ?? string.Empty;
            }
        });
    }

    private void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        _recording = true;
        if (_record is not null)
        {
            _record.Content = "Press keys…";
        }
        Focus();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        Hotkey = string.Empty;
        if (_text is not null)
        {
            _text.Text = string.Empty;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        var key = e.Key;
        // Ignore pure modifier presses; wait for a real key
        if (IsPureModifier(key))
        {
            return;
        }

        if (key == Key.Escape)
        {
            StopRecording();
            e.Handled = true;
            return;
        }

        var gesture = new HotkeyGesture(key, e.KeyModifiers);
        Hotkey = gesture.ToString();
        if (_text is not null)
        {
            _text.Text = Hotkey;
        }
        StopRecording();
        e.Handled = true;
    }

    private void StopRecording()
    {
        _recording = false;
        if (_record is not null)
        {
            _record.Content = "Record";
        }
    }

    private static bool IsPureModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
        or Key.None;
}
