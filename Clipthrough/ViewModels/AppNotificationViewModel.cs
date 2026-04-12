using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Clipthrough.Localization;
using Clipthrough.Models;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class AppNotificationViewModel : ViewModelBase
{
    private static readonly IBrush s_infoBackground = new SolidColorBrush(Color.Parse("#11263F"));
    private static readonly IBrush s_infoBorder = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush s_warningBackground = new SolidColorBrush(Color.Parse("#3A2807"));
    private static readonly IBrush s_warningBorder = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush s_errorBackground = new SolidColorBrush(Color.Parse("#3B0D18"));
    private static readonly IBrush s_errorBorder = new SolidColorBrush(Color.Parse("#E11D48"));

    public AppNotificationViewModel(AppNotification notification, Action<AppNotificationViewModel>? dismiss = null)
    {
        Notification = notification;
        if (notification.Actions.Count > 0)
        {
            foreach (var action in notification.Actions)
            {
                Actions.Add(new AppNotificationActionViewModel(action, () => dismiss?.Invoke(this)));
            }
        }
    }

    public AppNotification Notification { get; }

    public string Title => Notification.Title;

    public string Message => Notification.Message;

    public string TimestampText => Notification.CreatedAt.ToLocalTime().ToString("HH:mm:ss", AppText.CurrentCulture);

    public string LevelText => AppText.GetLogLevelLabel(Notification.Level);

    public IBrush Background => Notification.Level switch
    {
        AppNotificationLevel.Warning => s_warningBackground,
        AppNotificationLevel.Error => s_errorBackground,
        _ => s_infoBackground,
    };

    public IBrush BorderBrush => Notification.Level switch
    {
        AppNotificationLevel.Warning => s_warningBorder,
        AppNotificationLevel.Error => s_errorBorder,
        _ => s_infoBorder,
    };

    public bool HasActions => Actions.Count > 0;

    public ObservableCollection<AppNotificationActionViewModel> Actions { get; } = [];
}
