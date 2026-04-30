# Clipthrough transforms & custom hotkeys skill

Use this reference when the user wants to add, modify, or bind a clip transformation. It saves you from re-reading source to discover what's available.

## Built-in text transformations

All values live in the `Clipthrough.Models.TextTransformation` enum and are applied via `Clipthrough.Services.TextTransformationService.Apply(kind, input)`. Pure, no I/O, safe to unit-test.

| Group      | Enum value              | Notes                                                         |
| ---------- | ----------------------- | ------------------------------------------------------------- |
| Case       | `UpperCase`             |                                                               |
| Case       | `LowerCase`             |                                                               |
| Case       | `TitleCase`             |                                                               |
| Case       | `SentenceCase`          |                                                               |
| Case       | `UpperCamelCase`        | PascalCase                                                    |
| Case       | `LowerCamelCase`        |                                                               |
| Case       | `FromCamelCase`         | Splits camel/Pascal into space-separated words                |
| Whitespace | `TrimWhitespace`        | Per-line trim                                                 |
| Whitespace | `CollapseWhitespace`    | Collapses runs of whitespace inside each line                 |
| Whitespace | `TabsToSpaces`          | 4-space tab stop                                              |
| Whitespace | `SpacesToTabs`          | Leading runs of 4 spaces → tab                                |
| Whitespace | `NormalizeEol`          | Converts CRLF/CR → LF                                         |
| Lines      | `SortLines`             | Ordinal, ascending                                            |
| Lines      | `ReverseLines`          |                                                               |
| Lines      | `RemoveEmptyLines`      |                                                               |
| Lines      | `RemoveDuplicateLines`  | Keeps first occurrence                                        |
| Lines      | `LinesToJsonArray`      | One line → one JSON string element                            |
| Lines      | `JoinWithDelimiter`     | Default `, `; configurable in code only                       |
| Tables     | `BoxTableToHtml`        | See below                                                     |

### `BoxTableToHtml` semantics

- Recognises three table flavours, mixed freely in a single input:
  - Box-drawing (`┌─┬─┐│├─┼─┤└─┴─┘` etc.)
  - Markdown pipe tables (`| a | b |` with a `|---|---|` separator row)
  - ASCII bordered tables (`+----+----+` borders + `| ... |` rows)
- Multiple tables in the same input are converted independently.
- Non-table text around tables is preserved, HTML-escaped, and emitted as `<div>...<br>...</div>` so Teams/Outlook keep paragraph breaks.
- Returns the original input verbatim if no table block is detected.

To add a new transformation:
1. Add an enum value to `Clipthrough/Models/TextTransformation.cs`.
2. Implement it in `Clipthrough/Services/Ai/TextTransformationService.cs#Apply`.
3. Add a display label in `Clipthrough/Converters/TextTransformationDisplayConverter.cs`.
4. Add an entry to `s_transformMenuEntries` in `Clipthrough/Views/MainWindow.axaml.cs` (drives the top menu and toolbar flyout).
5. Add a matching `<MenuItem ... CommandParameter="{x:Static models:TextTransformation.YourValue}"/>` to the right-click context menu in `Clipthrough/Views/MainWindow.axaml`.
6. Add unit tests in `Clipthrough.Tests/Unit/TextTransformationServiceTests.cs`.

The transform menu auto-collapses single-entry groups into top-level items, so a group with one transform is fine.

## Auto-copy contract

When exactly one target is transformed (single selected clip or a selected text slice), the result is also placed on the OS clipboard:

- HTML-producing transforms (currently `BoxTableToHtml`) → `ISystemInteractionService.CopyRichContentAsync(html, plainFallback, ClipContentFormat.Html)` (writes CF_HTML on Windows).
- All others → `CopyTextAsync(result)`.
- Always preceded by `_clipboardMonitorService.SuppressNext()` so the new clip isn't captured twice.

Multi-clip batch transforms intentionally skip auto-copy — only the final clip would be on the clipboard anyway, and the user almost certainly wants to leave their clipboard alone.

## User scripts (C# scripting)

- Stored on `AppSettings.UserScripts` (`UserScript { Name, Code }`).
- Executed by `IScriptingService.EvaluateAsync(code, input)` via Roslyn `CSharpScript`. Globals expose `Input` (string). Return value is coerced to string.
- Defaults are seeded by `ScriptingService.GetDefaultScripts()` (JSON helpers, URL/Base64 encode-decode, etc.) and added by **Edit → Load default scripts**.

## AI transforms

- Service: `IAiTransformService.TransformAsync(systemPrompt, input)`.
- Configured in **Settings → AI** (base URL, API key, model) with env-var fallback `OPENAI_BASE_URL` / `OPENAI_API_KEY`.
- Presets live on `AppSettings.AiPresets` (`AiPreset { Name, Prompt, Kind }`).

## Custom hotkey bindings

`Models/CustomHotkeyBinding`:

```csharp
record CustomHotkeyBinding {
    string Id;
    string Gesture;       // e.g. "Ctrl+Alt+U"
    string Target;        // "kind:value", see below
    bool   PasteAfter;    // true → simulate Ctrl+V into foreground window
}
```

`Target` is a `kind:value` string. Recognised kinds (handled in `App.axaml.cs#ExecuteCustomHotkey`):

| Kind      | `value`                          | Behaviour                                                            |
| --------- | -------------------------------- | -------------------------------------------------------------------- |
| `builtin` | `TextTransformation` enum name   | Runs `TextTransformationService.Apply(kind, latestClip)`.            |
| `script`  | User script `Name`               | Runs the matching `UserScript.Code` via `IScriptingService`.         |
| `ai`      | AI preset `Name`                 | Runs `IAiTransformService.TransformAsync(preset.Prompt, latestClip)`.|
| `prompt`  | Free-form prompt text            | Runs `IAiTransformService.TransformAsync(value, latestClip)` directly — handy for one-off prompts without creating a preset. |

After producing `output`, the handler calls `_clipboardMonitorService.SuppressNext()`, copies via `CopyTextAsync`, and (when `PasteAfter` is true) simulates Ctrl+V after a short delay.

The `Target` string is currently entered free-form in **Settings → Custom hotkey actions**. If you add a new kind, update both `ExecuteCustomHotkey` and the help text in `Clipthrough/Views/SettingsWindow.axaml`.
