using ReactiveUI;

namespace Clipthrough.Models;

public sealed class CustomHotkeyDraft : ReactiveObject
{
    private string _id = System.Guid.NewGuid().ToString();
    private string _gesture = string.Empty;
    private string _target = string.Empty;
    private bool _pasteAfter = true;

    public string Id
    {
        get => _id;
        set => this.RaiseAndSetIfChanged(ref _id, value);
    }

    public string Gesture
    {
        get => _gesture;
        set => this.RaiseAndSetIfChanged(ref _gesture, value);
    }

    public string Target
    {
        get => _target;
        set => this.RaiseAndSetIfChanged(ref _target, value);
    }

    public bool PasteAfter
    {
        get => _pasteAfter;
        set => this.RaiseAndSetIfChanged(ref _pasteAfter, value);
    }

    public CustomHotkeyBinding ToBinding() => new()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? System.Guid.NewGuid().ToString() : Id,
        Gesture = Gesture?.Trim() ?? string.Empty,
        Target = Target?.Trim() ?? string.Empty,
        PasteAfter = PasteAfter,
    };

    public static CustomHotkeyDraft From(CustomHotkeyBinding binding) => new()
    {
        Id = string.IsNullOrWhiteSpace(binding.Id) ? System.Guid.NewGuid().ToString() : binding.Id,
        Gesture = binding.Gesture ?? string.Empty,
        Target = binding.Target ?? string.Empty,
        PasteAfter = binding.PasteAfter,
    };
}
