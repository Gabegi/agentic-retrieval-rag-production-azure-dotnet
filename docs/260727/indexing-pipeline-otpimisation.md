# Indexing pipeline optimization — findings (2026-07-27)

Review of the current PDF indexing pipeline across its three stages —
extraction, chunking, embedding — with concrete fixes for the two upstream
stages and a clean bill of health for embedding.

## Extraction

- **Convert tables to markdown/plain text when building `Content`.** Raw
  `<table>`/`<tr>`/`<td>`/`<th>` tags are currently left in the `Content`
  field as-is. This is the root cause feeding bad table shapes into
  chunking — fix it once here and chunking inherits clean input, rather
  than each downstream stage having to work around HTML in the middle of
  a text field.
- **Replace bare `<figure>` placeholders.** Right now figure tags carry
  zero information. Use the source's alt text or caption if one exists;
  strip the tag entirely if it doesn't. An empty placeholder is worse
  than no tag at all — it wastes chunk/embedding budget on nothing.
- **Spot check the 0.94 word-confidence page** (Gedragscode, page 2).
  Worth a manual look to determine whether this is a scan/image quality
  issue that justifies reprocessing, or within normal variance.

## Chunking

- **Populate `figure_captions`, or drop the figure tags.** Same principle
  as the extraction fix above — don't let empty tags sit in embedded
  text. If extraction starts supplying real captions, thread them through
  here; if not, strip at this stage instead of passing them further down.
- **Decide the purpose of `content_vector` in this schema.** Either
  populate it during chunking and remove the separate embedding step, or
  remove the field so its presence doesn't imply something that isn't
  happening. Leaving it empty and unexplained is misleading to anyone
  reading the index schema cold.
- **Investigate the 6 chunks with empty `heading`.** Confirm whether this
  is expected (e.g. content that appears before the first heading on a
  page) or an actual gap in heading assignment.
- **No change needed to chunk size cap or table handling** — both are
  working well as-is.

## Embedding

No fixes needed. ID integrity, dimension consistency, and hash uniqueness
are all clean. This stage just embeds whatever text it's given, so once
the `Content`/`figure_captions` quality issues above are fixed upstream,
embedding quality improves automatically with no changes required here.
