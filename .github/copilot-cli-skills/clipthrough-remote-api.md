# Clipthrough Remote API skill

When the user wants to inspect, capture, or delete Clipthrough clips programmatically and Clipthrough is already running with the Remote API enabled, use this HTTP interface instead of trying to automate the UI.

## When to use it

- Read clipboard history from a local tool or agent.
- Capture a new **text** clip without touching the desktop UI.
- Delete a clip by id.

The API is **read + capture only**. It deliberately exposes no way to run
scripts, invoke AI prompts, or otherwise transform a clip — see the note at
the end of *Endpoints*. Use the desktop UI for transforms.

Do **not** assume the API can yet create image/file/rich-text clips directly. `POST /clips` is text-only today.

## Discovery

- Default base URL: `http://127.0.0.1:53117`
- The bind address and port are configurable in **Settings -> Remote API**
- Swagger/OpenAPI (**both require the bearer token**):
  - `GET /docs` -> Swagger UI
  - `GET /openapi/v1.json` -> OpenAPI JSON
- Health probe:
  - `GET /health` -> `{ "ok": true, "version": "x.y.z" }`

## Authentication

- `GET /health` is the **only** unauthenticated endpoint.
- Every other endpoint — including `GET /docs` and `GET /openapi/v1.json`,
  which expose the API surface — requires:

```http
Authorization: Bearer <TOKEN>
```

- The bearer token comes from **Settings -> Remote API -> Bearer token**.
- If the token is missing or wrong, the API returns `401 { "error": "unauthorized" }`.
- If Remote API is enabled but the token is blank, protected routes return `503 { "error": "remote_api_token_not_configured" }`.

## Endpoints

### `GET /clips?query=<text>&limit=<n>&offset=<n>`

Search or page through clips.

- `query` defaults to empty string
- `limit` defaults to `100`
- `offset` defaults to `0`

Response shape:

```json
{
  "total": 123,
  "items": [ /* clip DTOs */ ]
}
```

### `GET /clips/{id}`

Fetch one clip by id.

- `404` if the clip does not exist

### `POST /clips`

Capture a new **plain-text** clip.

Request body:

```json
{
  "text": "hello from automation",
  "sourceApp": "copilot-cli"
}
```

Notes:

- `text` is required
- created clips are stored as:
  - `contentType = "Text"`
  - `format = "PlainText"`
- `sourceApp` is optional

### `DELETE /clips/{id}`

Delete a clip by id.

- Returns `204 No Content`

### `POST /clips/{id}/transform` — **removed, do not use**

This endpoint no longer exists and returns **404**. It was removed
deliberately: the `kind=script` and `kind=ai` branches were remote
code-execution surfaces, and `kind=builtin` was dropped with them so the
remote API exposes only read + capture
(`RemoteControlService.cs:238`).

Do not reintroduce it, and do not tell the user to call it. Apply transforms
through the desktop UI instead — see `clipthrough-transforms.md`.

## Clip DTO

Each clip is serialized as:

```json
{
  "id": 123,
  "content": "example text",
  "contentType": "Text",
  "format": "PlainText",
  "sourceApp": "devenv.exe",
  "sourceWindowTitle": "Example.cs - Rider",
  "sourceUrl": null,
  "isFavorite": false,
  "isSensitive": false,
  "isPinned": false,
  "isPasted": false,
  "copyCount": 1,
  "byteSize": 42,
  "capturedAt": "2024-01-01T00:00:00+00:00"
}
```

Important details:

- `contentType` is currently one of: `Text`, `Image`, `RichText`, `Files`
- `format` is currently one of: `PlainText`, `Html`, `Rtf`, `Bitmap`, `FileList`
- image clips return `"content": null`

## Safe usage guidance for agents

- Prefer `GET /health` first if you are not sure the API is up.
- Prefer `GET /docs` or `GET /openapi/v1.json` if you need to inspect the live contract.
- Do not log or echo the bearer token back to the user.
- Treat the API as local-machine automation, not a public network service.
- If the API is bound to anything other than loopback, be explicit with the user about the exposure.

## Examples

```bash
curl http://127.0.0.1:53117/health

curl -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:53117/clips?query=api&limit=20&offset=0"

curl -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"text":"hello","sourceApp":"copilot-cli"}' \
  http://127.0.0.1:53117/clips

# /docs and /openapi need the token too -- without it they return 401
curl -H "Authorization: Bearer $TOKEN" \
  http://127.0.0.1:53117/openapi/v1.json
```
