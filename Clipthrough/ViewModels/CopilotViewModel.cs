using System;
using System.Diagnostics;
using System.Reactive;
using System.Threading.Tasks;
using Clipthrough.Services;
using ReactiveUI;

namespace Clipthrough.ViewModels;

/// <summary>
/// Owns the GitHub Copilot device-code sign-in flow, extracted from
/// <see cref="MainWindowViewModel"/>. Self-contained (no clip-list coupling);
/// when sign-in state changes it raises its own properties and invokes
/// <c>onAiMenuVisibilityChanged</c> so the host can refresh AI-menu visibility
/// (signing in/out changes whether AI is configured).
/// </summary>
public sealed class CopilotViewModel : ViewModelBase
{
    private readonly ICopilotAuthService? _copilotAuthService;
    private readonly ISystemInteractionService _systemInteractionService;
    private readonly IClipboardMonitorService _clipboardMonitorService;
    private readonly Action _onAiMenuVisibilityChanged;

    public CopilotViewModel(
        ICopilotAuthService? copilotAuthService,
        ISystemInteractionService systemInteractionService,
        IClipboardMonitorService clipboardMonitorService,
        Action onAiMenuVisibilityChanged)
    {
        _copilotAuthService = copilotAuthService;
        _systemInteractionService = systemInteractionService;
        _clipboardMonitorService = clipboardMonitorService;
        _onAiMenuVisibilityChanged = onAiMenuVisibilityChanged;

        if (_copilotAuthService is not null)
        {
            _copilotAuthService.SignedInChanged += OnCopilotSignedInChanged;
        }

        CopilotSignInCommand = ReactiveCommand.CreateFromTask(CopilotSignInAsync);
        CopilotSignOutCommand = ReactiveCommand.Create(CopilotSignOut);
        CopyCopilotUserCodeCommand = ReactiveCommand.CreateFromTask(CopyCopilotUserCodeAsync);
    }

    public ReactiveCommand<Unit, Unit> CopilotSignInCommand { get; }
    public ReactiveCommand<Unit, Unit> CopilotSignOutCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCopilotUserCodeCommand { get; }

    private string _copilotSignInStatus = string.Empty;
    public string CopilotSignInStatus
    {
        get => _copilotSignInStatus;
        private set => this.RaiseAndSetIfChanged(ref _copilotSignInStatus, value);
    }

    private string _copilotUserCode = string.Empty;

    /// <summary>The short device-flow user code the user pastes into GitHub.</summary>
    public string CopilotUserCode
    {
        get => _copilotUserCode;
        private set
        {
            this.RaiseAndSetIfChanged(ref _copilotUserCode, value);
            this.RaisePropertyChanged(nameof(HasCopilotUserCode));
        }
    }

    public bool HasCopilotUserCode => !string.IsNullOrWhiteSpace(_copilotUserCode);

    private string _copilotVerificationUri = string.Empty;
    public string CopilotVerificationUri
    {
        get => _copilotVerificationUri;
        private set => this.RaiseAndSetIfChanged(ref _copilotVerificationUri, value);
    }

    private bool _isCopilotSigningIn;
    public bool IsCopilotSigningIn
    {
        get => _isCopilotSigningIn;
        private set => this.RaiseAndSetIfChanged(ref _isCopilotSigningIn, value);
    }

    public bool IsCopilotSignedIn => _copilotAuthService?.IsSignedIn == true;

    private void OnCopilotSignedInChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.RaisePropertyChanged(nameof(IsCopilotSignedIn));
            _onAiMenuVisibilityChanged();
        });
    }

    private async Task CopilotSignInAsync()
    {
        if (_copilotAuthService is null)
        {
            CopilotSignInStatus = "Copilot auth service not available.";
            return;
        }

        IsCopilotSigningIn = true;
        CopilotSignInStatus = "Starting device code flow\u2026";
        CopilotUserCode = string.Empty;
        CopilotVerificationUri = string.Empty;
        try
        {
            var deviceCode = await Task.Run(() => _copilotAuthService.StartDeviceCodeFlowAsync()).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                CopilotUserCode = deviceCode.UserCode;
                CopilotVerificationUri = deviceCode.VerificationUri;
                CopilotSignInStatus = $"Enter code {deviceCode.UserCode} at {deviceCode.VerificationUri} (code copied to clipboard).";
                try
                {
                    // Auto-copy the device code so the user can paste it into
                    // the GitHub verification page without retyping it.
                    _clipboardMonitorService.SuppressNext();
                    await _systemInteractionService.CopyTextAsync(deviceCode.UserCode);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Copilot sign-in: failed to auto-copy device code: {ex.Message}");
                }
                await _systemInteractionService.OpenUrlAsync(deviceCode.VerificationUri);
            });

            var success = await Task.Run(() => _copilotAuthService.PollForAuthorizationAsync(deviceCode)).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                CopilotUserCode = string.Empty;
                CopilotVerificationUri = string.Empty;
                if (success)
                {
                    CopilotSignInStatus = "Signed in to GitHub Copilot.";
                    this.RaisePropertyChanged(nameof(IsCopilotSignedIn));
                }
                else
                {
                    CopilotSignInStatus = "Sign-in expired or denied.";
                }
            });
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Copilot sign-in failed: {ex}");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                CopilotUserCode = string.Empty;
                CopilotVerificationUri = string.Empty;
                CopilotSignInStatus = $"Sign-in failed: {ex.Message}";
            });
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsCopilotSigningIn = false);
        }
    }

    private async Task CopyCopilotUserCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(CopilotUserCode))
        {
            return;
        }

        try
        {
            _clipboardMonitorService.SuppressNext();
            await _systemInteractionService.CopyTextAsync(CopilotUserCode);
            CopilotSignInStatus = $"Code {CopilotUserCode} copied. Paste it at {CopilotVerificationUri}.";
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Copy Copilot device code failed: {ex.Message}");
            CopilotSignInStatus = $"Could not copy code: {ex.Message}";
        }
    }

    private void CopilotSignOut()
    {
        _copilotAuthService?.SignOut();
        CopilotUserCode = string.Empty;
        CopilotVerificationUri = string.Empty;
        CopilotSignInStatus = "Signed out.";
        this.RaisePropertyChanged(nameof(IsCopilotSignedIn));
    }
}
