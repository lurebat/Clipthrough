using ReactiveUI;

namespace Clipthrough.Models;

public sealed class UserScriptDraft : ReactiveObject
{
    private string _name = string.Empty;
    private string _code = string.Empty;

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public string Code
    {
        get => _code;
        set => this.RaiseAndSetIfChanged(ref _code, value);
    }
}
