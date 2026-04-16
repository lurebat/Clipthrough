# Clipthrough Remote API skill

When the user wants to query, capture, or transform clipboard entries programmatically and Clipthrough is running on their machine with the Remote API enabled, use this HTTP skill.

## Discovery

- Default base URL: `http://127.0.0.1:53117` (configurable via Settings → Remote API).
- Auth: every request MUST include `Authorization: Bearer <TOKEN>`. The user pastes the token from Settings → Remote API → Bearer token.
- Health probe: `GET /health` → `{ "ok": true, "version": "x.y.z" }`.

## Endpoints

- `GET /clips?query=<text>&limit=<n>&offset=<n>` — list/search clips. Response: `{ total, items: [...] }`.
- `GET /clips/{id}` — fetch a single clip by id. 404 if missing.
- `POST /clips` — capture a new text clip. Body: `{ "text": "…", "sourceApp": "<optional>" }`.
- `DELETE /clips/{id}` — soft-delete a clip.
- `POST /clips/{id}/transform` — transform a clip and capture the result as a new clip. Body:
  - `{ "kind": "builtin", "name": "<TextTransformation enum>" }` — e.g. `UpperCase`, `LowerCase`, `TrimLines`, `JsonPretty`, `JsonMinify`, `Base64Encode`, `Base64Decode`, `UrlEncode`, `UrlDecode`, `HtmlEncode`, `HtmlDecode`, `ReverseText`, `RemoveDuplicateLines`, `SortLinesAscending`, `SortLinesDescending`.
  - `{ "kind": "script", "code": "Input.ToUpperInvariant()" }` — evaluates C# scripting (Roslyn) with `Input` in scope.
  - `{ "kind": "ai", "prompt": "Summarize this" }` — requires AI enabled in settings.

## DTO

Each clip is serialized as:

```json
{
  "id": 123,
  "content": "…",
  "contentType": "Text|RichText|Image|File|Other",
  "format": "PlainText|Html|Rtf|Png|…",
  "sourceApp": "devenv.exe",
  "sourceWindowTitle": "…",
  "sourceUrl": "https://…",
  "isFavorite": false,
  "isSensitive": false,
  "isPinned": false,
  "isPasted": false,
  "copyCount": 1,
  "byteSize": 42,
  "capturedAt": "2024-01-01T00:00:00+00:00"
}
```

## Examples

```bash
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:53117/clips?query=api
curl -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"text":"hello"}' http://127.0.0.1:53117/clips
curl -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"kind":"builtin","name":"UpperCase"}' \
     http://127.0.0.1:53117/clips/42/transform
```
