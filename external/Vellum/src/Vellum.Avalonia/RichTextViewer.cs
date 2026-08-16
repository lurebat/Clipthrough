using Avalonia;
using Avalonia.Input;

namespace Vellum.Avalonia;

/// <summary>
/// Presents a document. No caret, no editing, no history.
/// </summary>
/// <remarks>
/// <para>
/// For showing rich text the reader is not going to change: a clipboard history, a message
/// list, a preview pane, a log of pasted snippets. It renders exactly what
/// <see cref="RichTextView"/> renders — same block views, same text layout, same virtualization —
/// and shares that code through <see cref="DocumentPresenter"/>, so a document does not shift by
/// a pixel when it is moved from one control to the other.
/// </para>
/// <para>
/// It is a separate control rather than a flag on the editor because the difference is real and
/// measured: constructing a <see cref="RichTextView"/> allocates 19.7 KiB before it lays out a
/// single line — a blink timer, an input-method client and a context menu — which is 50% of the
/// allocation of a laid-out view of a clipboard-sized document. A list showing fifty history
/// entries would spend a megabyte on editing machinery no reader can reach. It is also a
/// smaller thing to hold: nothing on this control can change the document.
/// </para>
/// <para>
/// Text is not selectable yet. A reader who cannot copy out of a viewer will paste the whole
/// entry and delete the rest, so this is a real gap; it is recorded here rather than hidden
/// because closing it means sharing the editor's pointer handling and clipboard write, which is
/// a larger change than this control.
/// </para>
/// </remarks>
public class RichTextViewer : DocumentPresenter
{
    /// <summary>Defines the <see cref="Document"/> property.</summary>
    public static readonly StyledProperty<DocumentNode> DocumentProperty =
        AvaloniaProperty.Register<RichTextViewer, DocumentNode>(
            nameof(Document), DocumentNode.Empty, coerce: CoerceDocument);

    /// <summary>Creates the viewer.</summary>
    public RichTextViewer()
    {
        // Not focusable, and this is the whole point: a viewer that takes the keyboard puts a
        // tab stop in front of the reader that leads nowhere and does nothing.
        Focusable = false;

        // Arrow, not I-beam. The editor switches to an I-beam over text because a click there
        // places a caret; nothing here does, so an I-beam would promise an insertion point that
        // does not exist.
        Cursor = ArrowCursor;
    }

    /// <summary>The document to present.</summary>
    /// <remarks>
    /// Never null: assigning null coerces to <see cref="DocumentNode.Empty"/> rather than
    /// throwing, because a viewer bound to a selection that has nothing selected is the ordinary
    /// case and should show nothing rather than fail.
    /// </remarks>
    public DocumentNode Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <inheritdoc/>
    protected internal override DocumentNode PresentedDocument => Document;

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);

        base.OnPropertyChanged(change);

        if (change.Property == DocumentProperty)
        {
            // Realized eagerly, for the same reason the editor does it: a geometry question
            // asked between the assignment and the next layout pass would otherwise be answered
            // about the document that was there before.
            if (Blocks.Count > 0)
            {
                Realize();
            }

            // Only the measure. A document change always changes what the control asks for or
            // what it draws, and the relayout that follows repaints it either way — verified by
            // recolouring a document without changing its size, which still produced a different
            // frame with no InvalidateVisual here at all.
            InvalidateMeasure();
        }
    }

    private static DocumentNode CoerceDocument(AvaloniaObject o, DocumentNode value) =>
        value ?? DocumentNode.Empty;
}
