using Avalonia.Media;
using Clipthrough.Localization;
using Clipthrough.Models;

namespace Clipthrough.ViewModels;

public sealed class SessionLogEntryViewModel : ViewModelBase
{
    private static readonly IBrush s_infoBackground = new SolidColorBrush(Color.Parse("#1B2737"));
    private static readonly IBrush s_infoBorder = new SolidColorBrush(Color.Parse("#334155"));
    private static readonly IBrush s_warningBackground = new SolidColorBrush(Color.Parse("#3A2807"));
    private static readonly IBrush s_warningBorder = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush s_errorBackground = new SolidColorBrush(Color.Parse("#3B0D18"));
    private static readonly IBrush s_errorBorder = new SolidColorBrush(Color.Parse("#E11D48"));

    public SessionLogEntryViewModel(SessionLogEntry entry)
    {
        Entry = entry;
    }

    public SessionLogEntry Entry { get; }

    public string TimestampText => Entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", AppText.CurrentCulture);

    public string LevelText => AppText.GetLogLevelLabel(Entry.Level);

    public string Message => Entry.Message;

    public IBrush LevelBackground => Entry.Level switch
    {
        AppNotificationLevel.Warning => s_warningBackground,
        AppNotificationLevel.Error => s_errorBackground,
        _ => s_infoBackground,
    };

    public IBrush LevelBorderBrush => Entry.Level switch
    {
        AppNotificationLevel.Warning => s_warningBorder,
        AppNotificationLevel.Error => s_errorBorder,
        _ => s_infoBorder,
    };
}
