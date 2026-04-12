using System;
using System.Reactive;
using System.Threading.Tasks;
using Clipthrough.Models;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class AppNotificationActionViewModel : ViewModelBase
{
    public AppNotificationActionViewModel(AppNotificationAction action, Action? afterExecute = null)
    {
        Label = action.Label;
        ExecuteCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            await action.ExecuteAsync();
            afterExecute?.Invoke();
        });
    }

    public string Label { get; }

    public ReactiveCommand<Unit, Unit> ExecuteCommand { get; }
}
