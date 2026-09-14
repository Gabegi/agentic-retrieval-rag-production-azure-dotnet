# Zenya `Documents` API — what we ask for, what we get, what we keep

Code-facing companion to the client in this folder. It answers one question: **for a Zenya
document, which fields exist on the wire, which of them does our code actually request, and where
does each one end up?**

- The full v5 swagger surface (every tag, every operation, the complete `GET /documents` contract)
  is `docs/2609/260911/zenya-api-v5-surface.md` (D187). This file does not repeat it.
- The container layout and the reasoning behind the metadata contract: `docs/2609/260911/zenya-sync-build.md` (D185).
- Identities, registrations and configuration: `README.md` next to this file.

**Evidence rule used throughout.** ✔ *measured* = seen on `contoso.zenya.work`. ○ *spec only* =
read from the public swagger (`swagger.zenya-dev.nl`, the stand-in for our tenant's inaccessible
swagger) and never exercised. The two are never merged.

## 1. Why every document costs two calls

`GET /documents` returns a deliberately thin row. It carries **no mime type, no document type, no
last-modified and no download flags** — not even with every `include_*` flag switched on; those
fields are absent from the listing schema entirely. So:

```
GET /documents?limit=1000&offset=…&envelope=true&include_total=true     once per 1000 documents
  └─ per row: version is the ONLY change signal the listing gives
       └─ version already stored?  → unchanged, stop here (no second call, no download)
       └─ otherwise GET /documents/{id}   ← the routing flags and all real metadata live here
            └─ can_download_binary → GET /documents/{id}/v{version}/download → blob
```

The `unchanged` shortcut is the whole reason the listing is worth calling at all: on a steady-state
run it collapses to one call per 1000 documents. On the 2026-09-14 dry run the container was empty,
so all 1,286 documents took the second call — 1,286 metadata calls in 2m21s.

![Zenya sync flow: listing → container lookup → per-document metadata → download → blob + metadata](../../../../docs/media/zenya-api-flow.jpeg)

*`docs/media/zenya-api-flow.jpeg`, 2026-09-14. Not drawn: the removal pass at the end of a run
(stored `zenya_document_id`s Zenya no longer lists → blobs deleted), and the split behind the
`can_download_binary = No` arrow into* authored-skipped *vs* not-downloadable *(the table in the
diagram has it; §3 below has the rule).*

## 2. `GET /documents` — the listing row

Requested by `ZenyaClient.ListDocumentsAsync`. We send `limit=1000&offset=…&envelope=true&include_total=true`
and **no `include_*` flag**, so we receive the five ungated fields and nothing else.

| Wire field | We ask for it | Mapped to | Used for |
|---|---|---|---|
| `document_id` | ✔ always | `ZenyaDocumentListItem.DocumentId` | the identity of everything: blob name, `zenya_document_id`, the removal pass's key |
| `version` | ✔ always | `.Version` | the change signal — compared against the blob's `zenya_version` |
| `title` | ✔ always | `.Title` | logging and failure rows only; the *blob's* title comes from the per-document call |
| `published_date_time` | ✔ always | `.PublishedDateTime` | **read, never used** — parsed by nothing, written to no blob |
| `summary` | ✔ always | `.Summary` | **read, never used** — see §5 |
| `involved_persons` (authors, authorizers, document administrators, writers group) | ✗ `include_involved_persons` not sent | — | — |
| `custom_field_values[]` (`field_id`, `field_type`, `name`, `value`) | ✗ `include_custom_fields` not sent | — | — |
| `check_info` (`check_date`, `last_checked_date_time`, `last_checked_by_user`, `check_task_delegated_to_user`) | ✗ `include_check_info` not sent | — | — |
| `read_roles[]` (`read_role_id`, `name`) | ✗ `include_read_roles` not sent | — | — |
| `writer_invitation` | ✗ `include_writer_invitations` not sent | — | — |

Envelope: `{ data[], extra_data{}, pagination{ limit, offset, returned, total } }`. `extra_data` is
a free-form string map; our `ZenyaDocumentPage` does not model it and System.Text.Json drops it.

**Listing defaults we rely on without sending them** ○: `active=true`, `archived=false`, and
Zenya's own default state set. `ListDocumentsAsync(states: null)` is what the sync passes, so the
corpus is "whatever Zenya considers active and non-archived". Which states the corpus *should*
contain is still an open product question (D175) — `states` is a supported array parameter
(`draft`, `review`, `waiting_for_publication`, `published`, `expired`, `archived`, `deleted`,
`revised`, `temp_revision`, `invisible`) and the client already forwards it.

## 3. `GET /documents/{id}` — the per-document DTO

Requested by `ZenyaClient.GetDocumentAsync`. This is the **legacy** DTO, a different shape from the
listing row, and the only place the routing discriminators exist.

| Wire field | Model | Used for |
|---|---|---|
| `document_id`, `version` | `DocumentId`, `Version` | blob name, `zenya_document_id`, `zenya_version`, and the `/download` path's `v{version}` |
| `can_download_binary` | `CanDownloadBinary` | **the routing decision.** `true` → download and write. Anything else → not written |
| `can_download_content` | `CanDownloadContent` | only consulted when `can_download_binary` is not true: `true` → counted as *authored-skipped*, `false`/absent → *not-downloadable* |
| `mime_type` | `MimeType` | `zenya_mime_type`, and the last fallback for the file extension |
| `download_binary_extension` | `DownloadBinaryExtension` | second choice for the file extension |
| `download_as_pdf` | `DownloadAsPdf` | **read, never used** — the response's own `Content-Type` decides the extension instead, which covers this case without trusting the flag |
| `title` | `Title` | `zenya_title` (percent-encoded) |
| `type` | `Type` | `zenya_type` (percent-encoded) |
| `document_type` → `{ id, name }` | `DocumentType` | `zenya_document_type` = the **name** only; the id is dropped. Modelled as an object after a live payload rejected a string model (D185 §5) ✔ |
| `quick_code` | `QuickCode` | `zenya_quick_code` (percent-encoded) — Zenya's *Snelcode* |
| `state` | `State` | `zenya_status`, falling back to the literal `published` when absent ○ |
| `last_modified_datetime` | `LastModifiedDateTime` | `zenya_last_modified`, stored raw as Zenya's string |
| `revision` | `Revision` | **read, never used** |
| `active` | `Active` | **read, never used** |

Every field is nullable: Zenya omits null attributes, and unknown fields are ignored by default, so
a tenant that returns more than this deserialises fine — the extra data is simply invisible.

**Timestamp shapes** ○: Zenya encodes datetimes as `yyyyMMddHHmmss` and dates as `yyyyMMdd`,
both as JSON *strings* (the swagger's 14-digit and 8-digit example values match that split). We
never parse them — they are carried through to blob metadata verbatim, so a format surprise cannot
fail a sync.

## 4. What reaches the blob

`ZenyaBlobLayout`. Blob name is `pdf/{document_id}.pdf` or `docs/{document_id}.{ext}` — the id, never
the title, so a rename in Zenya is not a new document. The extension comes from the download
response's `Content-Type` first, `download_binary_extension` second, `mime_type` third, `bin` last.

| Blob metadata key | Source | Encoding |
|---|---|---|
| `zenya_document_id` | `document_id` | raw — the removal pass keys on it; a blob without it is never touched |
| `zenya_version` | `version` | raw |
| `zenya_status` | `state`, else literal `published` | raw |
| `zenya_quick_code` | `quick_code` | percent-encoded |
| `zenya_title` | `title` | percent-encoded |
| `zenya_type` | `type` | percent-encoded |
| `zenya_document_type` | `document_type.name` | percent-encoded |
| `zenya_mime_type` | response `Content-Type`, else `mime_type` | raw (ASCII-checked) |
| `zenya_last_modified` | `last_modified_datetime` | raw (ASCII-checked) |
| `zenya_synced_at` | our clock | raw, ISO-8601 UTC |

Free text is percent-encoded because Azure blob metadata values must be ASCII and Dutch titles are
not; readers use `Uri.UnescapeDataString`. There is deliberately **no `zenya_url`** — no field in
either DTO gives one, and inventing one was rejected (D185 §2).

## 5. Available and unclaimed

Everything below exists per the swagger ○ and is not requested by any code we have. Listed so the
next person does not have to re-read the spec to find out what is on the table.

| Not requested | What it would give us | Cost |
|---|---|---|
| `include_custom_fields` on the listing | `field_id` / `field_type` / `name` / `value` per document — the obvious source for index facets and filters (department, expiry, process) | one flag; no extra call |
| `include_read_roles` | `read_role_id` + `name` per document — the input any future security-trimmed search would need | one flag |
| `include_check_info` | `check_date`, `last_checked_date_time` — "is this policy still current", which a QMS corpus plausibly wants to surface | one flag |
| `include_involved_persons` | authors / authorizers / document administrators | one flag |
| `summary`, `published_date_time` | already arriving on every row, deserialised, and thrown away | zero |
| `GET /documents/{id}/fields` | the document's fields as their own route | one call per document |
| `GET /documents/{id}/hyperlinks` | outbound links — a document graph | one call per document |
| `GET /documents/{id}/mediaitems` | media items of authored documents | one call per document |
| `POST /documents/filter` + `GET /documents/filter/{filter_id}`, and `$filter` | server-side filtering instead of listing everything | changes the listing shape |
| `GET /documents/{id}/contents` | the authored-content route — **already built** as `GetContentsAsync`, never called by the sync | one call per authored document |
| `GET /documents/{id}/icon`, `POST /documents/{id}/hits`, `/objects/{id}/documents`, `/card_files/cards/{id}/documents` | not relevant to ingestion | — |

Whether any `include_*` block is actually populated for our service user is **unmeasured** — a
field the CAP-kennisbank user cannot see comes back absent, not denied. One listing call with the
flags on would settle it.

Write routes (`POST /documents`, `PATCH`, `POST /version`, `PUT /upload`, `POST /checks`) exist and
are **out of scope by design**: `IZenyaClient` is read-only and nothing in this integration writes
to Zenya.

## 6. Measured against our tenant

| What | Value | When |
|---|---|---|
| `users/me` | CAP-kennisbank service user, `user_type service_principal` | 2026-09-11 (D184) |
| `GET /documents` total | 1,286 | 2026-09-11, unchanged 2026-09-14 |
| Documents with a binary (`can_download_binary`) | 1,267 | 2026-09-14 dry run |
| Documents with authored content only | 19 | 2026-09-14 dry run — **not ingested by anything yet** (the A9 route) |
| Documents exposing neither | 0 | 2026-09-14 dry run |
| `GET /documents/{id}` failures over 1,286 calls | 0 | 2026-09-14 dry run |
| `/download` behaviour, real PDF bytes, per-extension split | **not yet measured** — a dry run downloads nothing | — |

Change log: 2026-09-14 — created from the v5 swagger plus the first successful dry run (D185 §5).
