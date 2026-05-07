using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShareX.ImageEditor.Hosting;
using ShareX.ImageEditor.Presentation.ViewModels;
using ShareX.ImageEditor.Presentation.Views;
using SkiaSharp;

namespace Clipthrough.Controls;

public sealed class EmbeddedImageEditorView : UserControl
{
    public static readonly StyledProperty<byte[]?> SourceImageBytesProperty =
        AvaloniaProperty.Register<EmbeddedImageEditorView, byte[]?>(nameof(SourceImageBytes));

    private MainViewModel? _editorViewModel;
    private EditorView? _editorView;
    private readonly Grid _root;
    private readonly Border _fallbackBorder;
    private readonly Image _fallbackImage;
    private bool _editorReady;
    private int _loadVersion;

    public EmbeddedImageEditorView()
    {
        _fallbackImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _fallbackBorder = new Border
        {
            Child = _fallbackImage,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _root = new Grid { ClipToBounds = true };
        _root.Children.Add(_fallbackBorder);
        Content = _root;

        this.GetObservable(SourceImageBytesProperty).Subscribe(LoadImage);
        this.GetObservable(IsVisibleProperty).Subscribe(isVisible =>
        {
            if (isVisible && !_editorReady && SourceImageBytes is { Length: > 0 } imageBytes)
            {
                LoadImage(imageBytes);
            }
        });
    }

    public byte[]? SourceImageBytes
    {
        get => GetValue(SourceImageBytesProperty);
        set => SetValue(SourceImageBytesProperty, value);
    }

    public bool IsEditorReady => _editorReady;

    public byte[]? GetEditedImageBytes()
    {
        if (!_editorReady || _editorView is null)
        {
            return SourceImageBytes;
        }

        try
        {
            using var snapshot = _editorView.GetSnapshot();
            if (snapshot is null)
            {
                return SourceImageBytes;
            }

            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Image editor snapshot failed: {ex.Message}");
            return SourceImageBytes;
        }
    }

    public void Reset() => LoadImage(SourceImageBytes);

    private void EnsureEditor()
    {
        if (_editorView is not null)
        {
            return;
        }

        try
        {
            AvaloniaIntegration.Initialize();

            _editorViewModel = new MainViewModel(new ImageEditorOptions
            {
                ShowExitConfirmation = false,
                ZoomToFitOnOpen = true,
                AutoCopyImageToClipboard = false,
                AutoCloseEditorOnTask = false,
            })
            {
                ImageEditorMode = true,
            };

            _editorView = new EditorView
            {
                DataContext = _editorViewModel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsVisible = false,
            };

            _root.Children.Add(_editorView);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Image editor initialization failed: {ex.Message}");
            _editorView = null;
            _editorViewModel = null;
        }
    }

    private void LoadImage(byte[]? imageBytes)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        _editorReady = false;

        if (imageBytes is not { Length: > 0 })
        {
            _fallbackImage.Source = null;
            _fallbackBorder.IsVisible = false;
            if (_editorView is not null)
            {
                _editorView.IsVisible = false;
            }
            return;
        }

        _fallbackImage.Source = null;
        _fallbackBorder.IsVisible = true;
        if (_editorView is not null)
        {
            _editorView.IsVisible = false;
        }

        if (!IsVisible)
        {
            return;
        }

        _ = LoadImageAsync(imageBytes, version);
    }

    private async Task LoadImageAsync(byte[] imageBytes, int version)
    {
        Bitmap? preview = null;
        try
        {
            preview = await Task.Run(() =>
            {
                using var previewStream = new MemoryStream(imageBytes, writable: false);
                return new Bitmap(previewStream);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Image fallback preview failed: {ex.Message}");
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _loadVersion)
            {
                preview?.Dispose();
                return;
            }

            _fallbackImage.Source = preview;
            _fallbackBorder.IsVisible = preview is not null;
        });

        SKBitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(imageBytes, writable: false);
                return SKBitmap.Decode(stream);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Image editor decode failed: {ex.Message}");
        }

        if (bitmap is null)
        {
            Trace.TraceWarning("Image editor: SKBitmap.Decode returned null");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _loadVersion)
            {
                bitmap.Dispose();
                return;
            }

            EnsureEditor();
            if (_editorViewModel is null || _editorView is null)
            {
                bitmap.Dispose();
                return;
            }

            var editorBitmap = bitmap.Copy();
            bitmap.Dispose();
            _editorViewModel.UpdatePreview(editorBitmap, clearAnnotations: true);
            _editorViewModel.IsDirty = false;
            _editorView.IsVisible = true;
            _fallbackBorder.IsVisible = false;
            _editorReady = true;
        }, DispatcherPriority.Background);
    }
}
