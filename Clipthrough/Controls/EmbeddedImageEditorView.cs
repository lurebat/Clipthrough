using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

        // Pre-initialize the editor so the first image open doesn't stutter
        Loaded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(EnsureEditor, Avalonia.Threading.DispatcherPriority.Background);
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

        // Show fallback preview immediately
        try
        {
            using var previewStream = new MemoryStream(imageBytes, writable: false);
            _fallbackImage.Source = new Bitmap(previewStream);
            _fallbackBorder.IsVisible = true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Image fallback preview failed: {ex.Message}");
            _fallbackImage.Source = null;
            _fallbackBorder.IsVisible = false;
        }

        // Try to initialize editor
        EnsureEditor();
        if (_editorViewModel is null || _editorView is null)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            using var bitmap = SKBitmap.Decode(stream);
            if (bitmap is null)
            {
                Trace.TraceWarning("Image editor: SKBitmap.Decode returned null");
                return;
            }

            _editorViewModel.UpdatePreview(bitmap.Copy(), clearAnnotations: true);
            _editorViewModel.IsDirty = false;
            _editorView.IsVisible = true;
            _fallbackBorder.IsVisible = false;
            _editorReady = true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Image editor load failed, using fallback: {ex.Message}");
            // Fallback stays visible; editor stays hidden
        }
    }
}
