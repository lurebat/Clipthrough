using System;
using System.Text;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Clipthrough.Models;
using Clipthrough.ViewModels;
using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// The clip context menu's Transform submenu must only appear when it would
/// contain something.
/// </summary>
/// <remarks>
/// Review finding A18 said this in as many words - "the AXAML copy gates text
/// transforms on CanAiTransform - wrong predicate, empty menu on image clips" -
/// and it was closed on its other half. The tracker entry had been retitled
/// "Transform registry triplicated", which is the first clause only, so the
/// second one became invisible the moment it was renamed.
///
/// The mechanism: CanAiTransform is true for any image clip, because an image
/// can be transformed *by AI*. The text groups inside are each gated on
/// CanTransform, which is false for images. So with no AI configured, an image
/// clip offered a Transform menu containing nothing at all.
///
/// HasTransformableTarget already computed the right answer - text targets, or
/// image targets when AI is configured - so the fix was to gate on the property
/// that knows whether the menu will have contents.
/// </remarks>
public sealed class TransformMenuVisibilityHeadlessTests
{
    private static ClipItemViewModel ImageClip() => new(new ClipEntry
    {
        Id = 900,
        Content = "image",
        ContentBytes = Encoding.UTF8.GetBytes("not-a-real-png"),
        ContentType = ContentType.Image,
        ContentFormat = ClipContentFormat.Bitmap,
        SourceApp = "Tests",
        Hash = "hash-image",
        LastCopiedAt = DateTimeOffset.UtcNow,
        FirstCopiedAt = DateTimeOffset.UtcNow,
    });

    private static ClipItemViewModel TextClip() => new(new ClipEntry
    {
        Id = 901,
        Content = "some text",
        ContentBytes = Encoding.UTF8.GetBytes("some text"),
        ContentType = ContentType.Text,
        ContentFormat = ClipContentFormat.PlainText,
        SourceApp = "Tests",
        Hash = "hash-text",
        LastCopiedAt = DateTimeOffset.UtcNow,
        FirstCopiedAt = DateTimeOffset.UtcNow,
    });

    /// <summary>
    /// An image clip with no AI configured has nothing to transform, so the menu
    /// that would be empty must not be offered.
    /// </summary>
    [AvaloniaFact]
    public void AnImageClipWithoutAi_OffersNoTransformMenu()
    {
        using var harness = MainWindowTestHarness.Create();
        var image = ImageClip();
        harness.ViewModel.Clips.Add(image);
        harness.ViewModel.SelectedClip = image;
        Dispatcher.UIThread.RunJobs();

        Assert.False(
            harness.ViewModel.IsAiMenuVisible,
            "setup failed: AI is configured in this harness, so the empty-menu case cannot occur");

        // The old gate. Still true, which is exactly why it was the wrong one:
        // an image is AI-transformable in principle, whether or not AI exists.
        Assert.True(image.CanAiTransform);
        Assert.False(image.CanTransform);

        Assert.False(
            harness.ViewModel.HasTransformableTarget,
            "the Transform menu is gated on this, and it would contain nothing here");

        // The assertion that actually defends the fix. The two above are true
        // whichever property the AXAML binds, so on their own they would pass
        // against a revert.
        Assert.False(TransformMenuIsVisible(harness), "the Transform menu is showing with nothing in it");
    }

    /// <summary>
    /// Reads the real menu item out of the shared context menu, so this fails if
    /// the binding is pointed back at the wrong property.
    /// </summary>
    private static bool TransformMenuIsVisible(MainWindowTestHarness harness)
    {
        var menu = harness.ClipList.ContextMenu;
        Assert.True(menu is not null, "the clip list has no context menu any more");

        MenuItem? transform = null;
        foreach (var item in menu!.Items)
        {
            if (item is MenuItem candidate && candidate.Header as string == "Transform")
            {
                transform = candidate;
                break;
            }
        }

        Assert.True(transform is not null, "no Transform entry in the context menu - the header or structure changed");
        Dispatcher.UIThread.RunJobs();
        return transform!.IsVisible;
    }

    /// <summary>
    /// The control: a text clip must still get its Transform menu. Without this
    /// the fix could be "never show the menu" and still pass above.
    /// </summary>
    [AvaloniaFact]
    public void ATextClip_StillOffersItsTransformMenu()
    {
        using var harness = MainWindowTestHarness.Create();
        var text = TextClip();
        harness.ViewModel.Clips.Add(text);
        harness.ViewModel.SelectedClip = text;
        Dispatcher.UIThread.RunJobs();

        Assert.True(text.CanTransform);
        Assert.True(
            harness.ViewModel.HasTransformableTarget,
            "a text clip lost its Transform menu");
        Assert.True(TransformMenuIsVisible(harness), "a text clip lost its Transform menu in the AXAML");
    }
}
