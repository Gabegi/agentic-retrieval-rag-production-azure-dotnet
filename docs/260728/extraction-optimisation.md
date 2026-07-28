# PdfDocumentIntelligenceAnalyzer refactor - status and findings

Follow-up to the `PDFDocumentIntelligenceAnalyzer.cs` rewrite (retry/backoff fix,
`Infos` split, `CostInfo`). Tracks the further-refactoring backlog (A-F) and the
corpus findings that inform B, C, and D.

## Status

- **A** - landed. Rewritten analyzer, `DocumentAnalyzedResults.Infos`, test fixes.
- **B** - landed. `TableInfo` gained `Caption`, `Footnotes`, `Regions`
  (`IReadOnlyList<DocumentRegion>`, every `BoundingRegion` - not just the first,
  since a table split across a page break needs every page's geometry, and
  re-acquiring it later means a paid re-analysis, not a re-read). `TableInfo.Offset`/
  `PageNumber` stay the anchor pattern. `TableInfo.Caption`'s doc comment flags that
  whoever writes the chunk-metadata step must carry the caption through - not
  enforced here, since chunk assembly doesn't exist yet.
- **C** - not started. Own PR, gated on the figure-indexing-strategy decision (see
  below). Confirmed technically: `AnalyzeOutputOption.Figures`,
  `Operation<AnalyzeResult>.Id` (already in hand in `SubmitAndPollAsync`), and
  `GetAnalyzeResultFigureAsync(modelId, resultId, figureId, ct)` all exist on the
  SDK, but only the submit call is currently wrapped by `IDocumentAnalysisClient` -
  fetching crops needs a new wrapper method, a test double, and a blob-write path.
  The 24h result retention makes crop-fetching a synchronous dependency inside the
  analyze path - an availability coupling worth naming explicitly in that PR.
- **D** - landed (both D-i and D-ii together). User confirmed a full index
  reindex is happening, removing the rebuild/alias-swap constraint that was
  gating this. Removed: `PageQuality` record, `MinAcceptablePageConfidence`,
  the per-word confidence-averaging loop, `LowPageConfidence`, and every
  downstream carrier - `PdfDocumentStructure.PageQuality`,
  `PdfExtractionDocument.AverageWordConfidence`, `PdfPageContext`'s field and
  `qualityByPage` lookup in `PdfExtractionPipeline`, `DocumentChunk`'s
  `AverageWordConfidence`/computed `PageQuality`, `SearchUploadChunk.PageQuality`,
  and the `page_quality` field in `IndexService.BuildIndexDefinition` (the actual
  Azure Search schema). `GetPageQuality` replaced by a leaner static
  `GetZeroWordWarnings` - `ZeroWordsOnPage` survives unchanged, it's still a real
  signal on this corpus, unlike confidence. Full solution builds and tests pass
  (193/193 in `AgenticRagApp.Indexing.Pdf.Tests`); three unrelated pre-existing
  failures elsewhere (a live-service eval test needing `SEARCH_ENDPOINT`, and two
  `IRunReportWriter` Moq-equality flakes in `FunctionApp.Tests` untouched by this
  change).
- **E** - landed. `ZeroWordsOnPage` message reworded from "likely an
  image-only/scanned page" to "either genuinely blank or entirely vector figure
  content (no OCR-able text)" - independent of C, doesn't need crop-fetching to be
  true, only needs figures to exist in the corpus, which they already do.
- **F** - deferred until D-ii lands (needs the removed signal's replacement, and a
  real per-document baseline from the sampling, not a global threshold).

## D: page-confidence sampling

Source: `docs/260727/Results1/` (`143114483-file-facts.json`,
`143114483-validation-report.json`, `extraction (3).json`) - one prior run, 3
documents, 30 pages, all born-digital Word-produced PDFs.

- `LowPageConfidence` / `ZeroWordsOnPage`: zero occurrences in the validation
  report's `Issues` across all 30 pages.
- Raw `AverageWordConfidence` per page: range 0.942-0.995, tightly clustered near
  0.99. The single lowest value (0.942) is still 0.09 above the 0.85 threshold -
  not a near-miss.

Supports the "dead signal on this corpus" hypothesis, not just in theory but in
the actual numbers - the values never get close to the threshold, not just fail to
cross it. Caveat: small, narrow sample (3 documents, one producer family, no
scans by construction). Worth widening before treating as conclusive, though the
user's own read of the full corpus (no scans expected anywhere) agrees with it.

Practical read for the schema owner conversation: frame as "this field carries no
information today" (easy ask) rather than "please remove a field" (hard ask) -
the rebuild/alias-swap constraint doesn't change, but the case for doing it once a
rebuild is already scheduled gets much easier to make.

## C: figure findings from the same sample

Same 3-document sample, 20 figures total (matches
`FiguresWithoutCaption` warnings: 5 + 3 + 12 = 20).

- **`Caption` is `null` for every single figure - 20/20.** Not a fallback case,
  a non-signal for this corpus. The original C plan's "caption plus the
  paragraphs `f.Elements` points at" strategy loses its primary half here.
- **`Elements` (paragraph pointers) vary wildly in what they represent** - the
  only thing DI gives per figure, and it's not self-describing:
  - Most figures: exactly one element, e.g. `Id: "1.1"`,
    `Elements: ["/paragraphs/0"]` - the recurring page-header/logo image, one
    per page (`Id` follows a consistent `{page}.1` pattern across all 20,
    consistent with a decorative image repeated on nearly every page).
  - At least one figure (page 11, under "Bijlage 2 Culturele diversiteit in
    cijfers" / "Appendix 2: Cultural diversity in numbers") has 26 elements -
    a real content-bearing infographic/data visual, not decoration.

Gap this exposes in the original C plan: the indexing-strategy decision assumed
captions would sometimes be available to lean on. Here they never are, and a
uniform strategy (caption-first, or vision-model-for-everything) would either
burn vision-model calls captioning ~19 copies of the same logo, or skip the one
figure that actually carries information. **Needs a cheap decorative-vs-content
filter before the strategy decision, not as part of it** - element count is a
plausible free heuristic (DI already gives it for free, no extra call), but
untested beyond this one sample. Fold into C's design doc when that PR starts.

## Source data

- `docs/260727/Results1/143114483-file-facts.json` - per-document native metadata.
- `docs/260727/Results1/143114483-validation-report.json` - `Issues` list (warning
  codes, one per finding).
- `docs/260727/Results1/extraction (3).json` - full per-document extraction
  output, including per-page `PageQuality`/`AverageWordConfidence` and per-figure
  `Caption`/`Elements`/`Id`.
