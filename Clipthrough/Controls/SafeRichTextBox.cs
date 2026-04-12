using System;
using System.Diagnostics;
using Avalonia.Input;
using AvRichTextBoxControl = AvRichTextBox.RichTextBox;

namespace Clipthrough.Controls;

/// <summary>
/// Wraps AvRichTextBox with exception-safe input handling.
/// AvRichTextBox has internal bugs (e.g. MoveSelectionRight throws ArgumentOutOfRangeException
/// after Ctrl+A then type). Since the library's handlers run inside OnTextInput/OnKeyDown,
/// we catch exceptions here to prevent app crashes, then degrade to read-only.
/// </summary>
public class SafeRichTextBox : AvRichTextBoxControl
{
    protected override void OnTextInput(TextInputEventArgs e)
    {
        try
        {
            base.OnTextInput(e);
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                $"AvRichTextBox TextInput error (blocks={FlowDocument?.Blocks.Count}): {ex.Message}");
            IsReadOnly = true;
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        try
        {
            base.OnKeyDown(e);
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                $"AvRichTextBox KeyDown error (blocks={FlowDocument?.Blocks.Count}): {ex.Message}");
            IsReadOnly = true;
            e.Handled = true;
        }
    }
}
