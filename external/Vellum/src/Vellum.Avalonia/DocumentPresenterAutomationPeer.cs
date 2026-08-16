using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace Vellum.Avalonia;

/// <summary>
/// What a screen reader sees when it reaches a <see cref="DocumentPresenter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12 has no UIA text pattern: <c>Avalonia.Automation.Provider</c> contains
/// <see cref="IValueProvider"/>, <see cref="IInvokeProvider"/>, <see cref="IScrollProvider"/>,
/// selection, expand/collapse, range and toggle, and nothing for text. <c>TextBox</c> is in the
/// same position and answers it the same way — <c>TextBoxAutomationPeer</c> implements
/// <see cref="IValueProvider"/> and nothing else. So this peer reports the document's plain text
/// as one value.
/// </para>
/// <para>
/// The consequence is worth stating plainly rather than hiding: <b>caret position, selection
/// extent and per-run formatting cannot be announced.</b> A screen reader can read the document
/// and can be told when it changes, but it cannot follow the caret through it, because there is
/// no platform interface on which to say so. That is a gap in Avalonia, not in this control, and
/// closing it means implementing <c>ITextProvider</c> upstream.
/// </para>
/// <para>
/// The control type is <see cref="AutomationControlType.Document"/> for both the viewer and the
/// editor, with <see cref="IsReadOnly"/> carrying the difference. Reporting the editor as
/// <see cref="AutomationControlType.Edit"/> was rejected: <c>Edit</c> tells an assistive
/// technology to expect a single-line field it can navigate with the text pattern, which is
/// exactly the promise this peer cannot keep.
/// </para>
/// </remarks>
public class DocumentPresenterAutomationPeer : ControlAutomationPeer, IValueProvider
{
    private DocumentNode? _valueFor;
    private string _value = string.Empty;

    /// <summary>Creates a peer for <paramref name="owner"/>.</summary>
    /// <param name="owner">The presenter this peer describes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
    public DocumentPresenterAutomationPeer(DocumentPresenter owner)
        : base(owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        owner.PropertyChanged += OnOwnerPropertyChanged;
    }

    /// <summary>The presenter this peer describes.</summary>
    public new DocumentPresenter Owner => (DocumentPresenter)base.Owner;

    /// <inheritdoc/>
    public bool IsReadOnly => !Owner.SupportsTextInput;

    /// <inheritdoc/>
    /// <remarks>
    /// Cached against the document it was computed from, and the only place that cache is
    /// filled — the change notification below asks this property rather than recomputing beside
    /// it. A document is immutable, so an edit produces a new instance and a reference comparison
    /// is a complete invalidation test; there is no subscription to forget to unhook. Without the
    /// cache an assistive technology polling this property would walk every block of the document
    /// each time it asked.
    /// </remarks>
    public string? Value
    {
        get
        {
            var doc = Owner.PresentedDocument;

            if (!ReferenceEquals(_valueFor, doc))
            {
                _value = DocumentText.Of(doc);
                _valueFor = doc;
            }

            return _value;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Replacing the whole document with plain text is destructive, and it is nonetheless the
    /// contract: this is what a UI-automation client calls to fill a field, and refusing it on a
    /// writable control would make the editor untestable by every automation framework. It is one
    /// undo, because selecting is not an edit.
    /// </remarks>
    /// <exception cref="ElementNotEnabledException">The presenter is read-only.</exception>
    public void SetValue(string? value)
    {
        if (IsReadOnly)
        {
            throw new ElementNotEnabledException();
        }

        Owner.SetTextFromAutomation(value ?? string.Empty);
    }

    /// <inheritdoc/>
    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Document;

    /// <inheritdoc/>
    /// <remarks>
    /// A document carries content in its own right rather than merely labelling something else,
    /// so it stays in the content view of the automation tree.
    /// </remarks>
    protected override bool IsContentElementCore() => true;

    /// <inheritdoc/>
    /// <remarks>
    /// The editor takes focus and the viewer does not, and an assistive technology that is told
    /// otherwise will try to move focus somewhere it cannot go.
    /// </remarks>
    protected override bool IsKeyboardFocusableCore() => Owner.Focusable && Owner.IsEffectivelyEnabled;

    /// <summary>
    /// Notices that the document behind <see cref="Value"/> has been replaced.
    /// </summary>
    /// <remarks>
    /// Driven off any property change rather than a named one because the two presenters carry
    /// their document differently — the viewer in a styled <c>Document</c> property, the editor
    /// inside its <c>State</c> — and a peer that knew which was which would have to be told again
    /// for every future presenter. The test is a reference comparison against the document the
    /// cached value came from, so watching everything costs one comparison per change.
    /// </remarks>
    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // The cheap test first: most property changes are not document changes, and asking for
        // Value would walk the whole document to discover that.
        if (ReferenceEquals(_valueFor, Owner.PresentedDocument))
        {
            return;
        }

        var previous = _value;
        var current = Value;

        // A new document is not new text. Every formatting command replaces the document, and a
        // screen reader told the value changed re-reads the whole thing.
        if (!string.Equals(previous, current, StringComparison.Ordinal))
        {
            RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previous, current);
        }
    }
}
