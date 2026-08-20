---
tags: [storage, viewmodel, clipboard, data-fidelity]
version: v0.17.0
severity: p0
status: active
---

# Rich clips lose their markup on the way back from the list

## Problem

Copying a rich-text clip put plain text on the clipboard. Pasting it into Word
or Outlook produced unformatted text, silently, with no error and nothing in the
log.

## Cause

Two facts that are individually reasonable and jointly wrong.

`ClipListSelectColumns` — the column list behind every list and search query —
selects a literal `NULL` for `content_bytes`, so a refresh of a few hundred rows
does not materialise every blob. Rows arrive without their payload by design,
and `EnsureContentHydratedAsync` exists to fetch it back on demand.

Rich text keeps its HTML/RTF in `content_bytes`. The same column as an image.

But hydration's guard was:

```csharp
var needsImage = Clip.ContentType == ContentType.Image && Clip.ContentBytes is null;
if (!needsImage) return false;
```

So it declined to fetch anything that was not an image. Every rich clip read
back from the list therefore had no markup, and the whole chain downstream
degrades quietly rather than failing:

- `GetRawMarkup` returns `null` when `ContentBytes` is empty.
- `GetRawContentDisplay` then falls back to the plain-text `Content` field.
- `FullContent` is that fallback.
- `TryCopySelectedAsync` hands `FullContent` to `CopyRichContentAsync`, which
  faithfully writes plain text as the "rich" payload.

The rendered preview reads the same property, so it had been showing plain text
for these clips too.

## Why it survived

**The clip you just copied is unaffected.** It reaches the view model through
the capture stream carrying its bytes, so it copies correctly. Only a clip that
has been round-tripped through the list reproduces the bug — which is the exact
opposite of what anyone checks by hand after making a change to copying.

## Fix

Hydrate whenever the bytes are missing *and* the clip is one whose payload lives
there — an image, or a clip whose `ContentFormat` is `Html` or `Rtf`. Plain text
still declines, so an arrow key does not buy a round trip for nothing.

`TryCopySelectedAsync` also awaits hydration rather than racing the
fire-and-forget one that selection starts. Selection kicks hydration off without
waiting, so whether copy saw the bytes depended on how long the clip had been
selected. That race also affected images, where it surfaced loudly as
"the selected image clip could not be decoded for copying".

## Testing notes

Build the fixture by reading a row back through `SearchAsync`, not by
constructing a `ClipEntry` with `ContentBytes` set. A hand-built entry is
hydrated from birth, so a test using one passes against the bug. There is a
companion test asserting the row really does arrive with `ContentBytes == null`,
so the premise cannot quietly stop holding.

These tests must be `[AvaloniaFact]`. `EnsureContentHydratedAsync` completes
through `Dispatcher.UIThread.InvokeAsync`, which never returns without a
dispatcher, so under a plain `[Fact]` the run **hangs rather than fails** — and a
hang leaves no assertion text to read, so it reads as a slow build.

## Mutants

- `hydration-refuses-to-fetch-rich-text-bytes` — the original image-only guard.
- `hydration-fetches-bytes-for-every-clip` — the over-broad fix, which costs a
  round trip and a preview rebuild per arrow key.
