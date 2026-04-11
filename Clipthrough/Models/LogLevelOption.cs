using Clipthrough.Localization;

namespace Clipthrough.Models;

public sealed class LogLevelOption
{
    public LogLevelOption(AppNotificationLevel? value)
    {
        Value = value;
    }

    public AppNotificationLevel? Value { get; }

    public string Label => AppText.GetLogLevelLabel(Value);

    public override string ToString() => Label;
}
