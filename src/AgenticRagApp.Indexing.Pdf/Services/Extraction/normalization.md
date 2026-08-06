# Text normalization for retrieval

Normalization happens entirely during **indexing/cleaning**, before chunking and
embedding — not at query/retrieval time. `AgenticRagQueryService` hands the raw
question straight to Azure AI Search's agentic knowledge-base retrieval, which
decomposes the query and synthesizes the answer itself; nothing in the query
path normalizes text.

The actual normalization pipeline runs in `PdfCleaner.CleanPageContent`
(`Services/Extraction/PdfCleaner.cs`), in this deliberate order:

1. **Line endings** — collapse `\r\n` / `\r` to `\n` first, so every later regex
   only has to reason about `\n`.
2. **Mojibake repair** (Windows-1252 round-trip) before anything else inspects
   characters, so downstream steps see the *real* text.
3. **Character-level cleanup** — strip control chars, invisible/zero-width
   chars, expand ligatures, NBSP → plain space.
4. **Markdown escape removal** — unescape things like `\-` before hyphenation
   repair needs to match them.
5. **NFC normalization** (`text.Normalize(NormalizationForm.FormC)`) — composed
   vs. decomposed accented characters embed and keyword-match differently,
   which is otherwise silent retrieval noise.
6. **Line-break hyphenation repair** (`"informa-\ntie"` → `"informatie"`) before
   whitespace collapse, since it consumes a `\n`.
7. **Whitespace cleanup last** — trailing line spaces, runs of spaces/tabs
   collapsed to one, 3+ blank lines collapsed to one.

Table HTML → Markdown conversion (`ConvertTablesToMarkdown`) runs separately
afterward — it's a structural change, not a character repair/strip, so it
doesn't need to precede or follow any of the passes above.

## Design boundary

Every transform here either repairs extraction damage, strips characters that
add embedding/search noise without carrying meaning, or normalizes DI's raw
HTML structure back into plain markdown. Nothing rewrites or paraphrases
actual content. Header/footer/boilerplate stripping is explicitly out of
scope: Contoso's PDF conventions aren't confirmed yet, and a wrong regex here
would silently delete real content — worse for RAG than leaving a repeated
footer in. Document Intelligence can already exclude `pageHeader`/`pageFooter`
roles at extraction time; that's the preferred fix.

CSV indexing (`AgenticRagApp.Indexing.Csv/Services/Extraction/DataCleaner.cs`)
has an analogous but simpler pass: line-ending normalization, stripping
literal HTML tags, and re-normalizing `\r` produced by entity decoding.
