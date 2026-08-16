using Avalonia.Controls;
using Avalonia.Input;

namespace Vellum.Avalonia;

/// <summary>
/// The menu a right-click puts up.
/// </summary>
/// <remarks>
/// Built in code rather than declared in a theme, for the same reason the editor's default template
/// is: a control that only works once someone remembers to include a resource dictionary is a
/// control that does not work. Replacing <see cref="global::Avalonia.Controls.Control.ContextFlyout"/>
/// with a richer menu is a single assignment, so nothing here forecloses that.
/// </remarks>
public partial class RichTextView
{
    private MenuItem? _cutItem;
    private MenuItem? _copyItem;
    private MenuItem? _pasteItem;

    private MenuFlyout BuildContextFlyout()
    {
        _cutItem = Item("Cut", "Ctrl+X", () => _ = CutAsync());
        _copyItem = Item("Copy", "Ctrl+C", () => _ = CopyAsync());
        _pasteItem = Item("Paste", "Ctrl+V", () => _ = PasteAsync());

        var selectAll = Item("Select All", "Ctrl+A", () => SelectAll());
        var flyout = new MenuFlyout
        {
            ItemsSource = new object[] { _cutItem, _copyItem, _pasteItem, new Separator(), selectAll },
        };

        flyout.Opening += (_, _) => SyncContextFlyout();

        return flyout;
    }

    /// <summary>
    /// Greys out what cannot work. Read when the menu opens, because the selection changes
    /// constantly and a menu that always offers everything informs nobody.
    /// </summary>
    internal void SyncContextFlyout()
    {
        var hasSelection = !_state.Selection.IsEmpty;

        if (_cutItem is not null)
        {
            _cutItem.IsEnabled = hasSelection;
        }

        if (_copyItem is not null)
        {
            _copyItem.IsEnabled = hasSelection;
        }

        if (_pasteItem is not null)
        {
            _pasteItem.IsEnabled = TopLevel.GetTopLevel(this)?.Clipboard is not null;
        }
    }

    private static MenuItem Item(string header, string gesture, Action invoke)
    {
        var item = new MenuItem { Header = header, InputGesture = KeyGesture.Parse(gesture) };

        item.Click += (_, _) => invoke();

        return item;
    }
}