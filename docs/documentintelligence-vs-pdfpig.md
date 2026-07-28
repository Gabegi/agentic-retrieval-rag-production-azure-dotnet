# Decision report: Commit to Document Intelligence — stop investing in PdfPig

## Verdict

Adopt `DocumentIntelligenceExtractor` as the production PDF backend and freeze PdfPig
feature work. This isn't a close call. Your own codebase is the strongest evidence: you
built a careful, well-engineered PdfPig pipeline, and the artifact of that effort is a
trail of eight deferred, rejected, or placeholder features — five of which Document
Intelligence solves natively, out of the box, in code you've already written. The one
thing I'd still do before flipping the switch is a single `PdfBackendComparisonRunner`
run over real Cordaan samples (weighted toward table-heavy docs) as a confirmation
gate, not a decision input.

**Confidence: high.** The one finding that could reverse it: if the comparison run
showed Document Intelligence mangling Dutch text or tables on your actual documents —
which external evidence makes very unlikely (see below).

## The case, part 1: PdfPig's gaps are structural, and your repo proves it

The pattern in your code isn't "tuning needed" — it's "capability absent." Here is the
complete list of what you had to hold off, with receipts:

| # | Deferred/rejected in PdfPig | Where | Does DI solve it? |
|---|---|---|---|
| 1 | Table extraction — dropped entirely (Tabula rejected) | `PdfPageContentExtractor.cs:37-39` | ✅ Native, ML table model with row/column indices — you already render real pipe tables (`DocumentIntelligenceExtractor.cs:159-186`) |
| 2 | Header/footer stripping on 1–2 page docs — no fix, just a red flag | `PdfPigExtractor.cs:22`, `PdfPipelineValidator.cs:60-70` | ✅ `ParagraphRole.PageHeader`/`PageFooter`/`PageNumber` works per page, any document length (`DocumentIntelligenceExtractor.cs:97-100`) |
| 3 | Top-of-page position cut for headers — rejected (can't distinguish header from title) | design decision | ✅ Same — role classification is ML, not position heuristics |
| 4 | Cross-document repetition detection — rejected as too big an engineering lift | design decision | ✅ Not needed at all |
| 5 | Known-sections heading vocabulary — empty, blocked on collecting 20–30 real sample docs | `PdfDocumentBaselineCalculator.cs:29-31` | ✅ `ParagraphRole.Title`/`SectionHeading` needs no vocabulary (`DocumentIntelligenceExtractor.cs:104-107`) |
| 6 | Bookmark-based headings — distrusted, additive-only, due to upstream bug | `PdfDocumentBaselineCalculator.cs:87-94` | ✅ Not needed |
| 7 | Title metadata — regex hardcoded to `LCI-richtlijn` branding | `PdfMetadataExtraction.cs:25-26` | ⚠️ Solvable with a small change: use `ParagraphRole.Title` to populate the metadata field, bypassing the regex. PdfPig has no equivalent escape hatch — only more regex |
| 8 | Scanned/image-only pages — warning emitted, content silently lost | `PdfPigExtractor.cs:87-93` | ✅ DI's layout model runs on a full OCR engine; scanned pages are read, not skipped |

Two of these deserve emphasis:

**Tables (#1) are the decisive gap.** Dutch care protocols realistically contain dosage
tables and decision matrices. PdfPig reads those cells as prose in reading order —
every reindex, every time, unfixably. You knew this while building: `PdfPipelineValidator`
has a dedicated `TableFlatteningCheck` (`PdfPipelineValidator.cs:254-273`) whose only
job is to detect the damage after the fact. A validator check that exists to catch a
backend's known failure mode is the codebase telling you the backend is wrong for the
data. And this is confirmed upstream, not just locally: PdfPig's own README explicitly
lists table extraction as unsupported, and the PdfPig-adjacent DocumentLayoutAnalysis
project points users to separate libraries (Tabula-sharp/Camelot-sharp) for tables —
the exact path you already evaluated and rejected.

**Title metadata (#7) is the highest-stakes field.** Per your own instrumentation:
"Missing title is the worst: it is prepended to every chunk and is the primary BM25
signal" (`Instrumentation.cs:66`, `IndexRunReport.cs:64-66`). Right now both backends
share the same brittle `PdfMetadataExtraction.Parse`, so this weakness looks "equal" —
but it's only fixable on the DI side, where an ML-classified `ParagraphRole.Title` is
already sitting in the response, currently used for `##` rendering and thrown away for
metadata. On the PdfPig side, the ceiling is per-document-family regexes you'd
maintain forever.

**On PdfPig's maturity:** it's a genuinely good library for what it does (text from
digital-born PDFs), but it's pre-1.0 by its own declaration — "While the version is
below 1.0.0 minor versions will change the public API without warning (SemVer will not
be followed until 1.0.0)" — and the layout-analysis namespace you depend on (segmenter,
reading order, decoration classifier) is the experimental part. Your repo pins 0.1.9
(`AgenticRag.csproj:25`); upstream is at 0.1.15. One small correction to the
brief's premise: issue #736 (`TryGetBookmarks` returning `true` with zero entries) has
since been closed upstream via PR #930 — but the fix postdates your pinned version, and
bookmarks were only ever an additive signal anyway.

## The case, part 2: what committing to Document Intelligence actually buys and costs

**Capability, externally verified.** The `prebuilt-layout` model combines OCR with ML
models for text, tables, selection marks, and logical roles (title, section heading,
page header/footer/number) — exactly the structural signals your chunking and metadata
need. Dutch printed text is a supported language. Independent 2026 benchmarking still
places Azure DI at the top tier for printed-text accuracy (~96%) and layout analysis
(comparison, IntuitionLabs analysis). Honesty note: newest LLM-based OCR offerings
(e.g. Mistral OCR) now beat classic services on complex tables in some benchmarks — but
that's a different build-out with its own risks, and between the two backends actually
implemented in this repo, the gap is entirely one-sided.

**Cost — two corrections you need even though cost isn't the decider:**

- **Your cost constant is wrong by 10×.** `DocumentIntelligenceExtractor.cs:19` says
  `CostPerPage = 0.001m` ($1/1,000 pages). The actual `prebuilt-layout` price is $10
  per 1,000 pages = $0.01/page (confirmed). If your "cost is pretty low" intuition came
  from the comparison runner's output, it was reading a 10×-understated number.
- **The orchestrator re-extracts the entire corpus every run.**
  `PdfExtractionOrchestrator.ExtractAllFilesAsync` (`PdfExtractionOrchestrator.cs:108-136`)
  downloads and extracts every blob before diffing happens downstream. At your scale —
  thousands of docs, say 2,000 × ~8 pages — that's ~$160 per indexing run, which at a
  nightly cadence is ~$4,800/month. With a skip-unchanged-blobs check (blob
  `LastModified`/ETag vs. last run — the data is already captured in
  `lastModifiedByBlob`), recurring cost collapses to only new/changed documents:
  effectively pocket change. So cost is genuinely low, conditional on that one
  optimization, which is cheap to build and worth doing regardless of backend for
  run-time reasons.

Also: the F0 free tier (500 pages/month) only analyzes the first 2 pages of each
request, so it's unusable even for dev on multi-page protocols — budget for S0 in dev
too (a sample batch of 30 docs costs ~$2.50).

**Operational risks, all manageable:**

- **External dependency / latency:** real, but weak in context — this pipeline already
  lives or dies on Azure (Functions, Blob, AI Search, embeddings). DI's default is 15
  TPS on S0, files up to 500 MB / 2,000 pages; your extractor calls it synchronously
  one file at a time, which is slow for thousands of docs but becomes a non-issue once
  only changed blobs are extracted.
- **Data residency** (relevant for a Dutch care org): DI processes data within the
  deployed resource's region — deploy in West Europe and processing stays in the EEA,
  GDPR/AVG-compatible. These are guideline protocols, not patient data, which lowers
  the stakes further.
- **Lock-in:** your architecture already defuses this. `IPdfExtractor` is a clean seam,
  and the PdfPig implementation plus `PdfBackendComparisonRunner` remain as a free,
  offline regression baseline. Committing to DI doesn't burn the boat — it just stops
  you rowing two of them.

## What "keeping PdfPig" would actually cost

To reach production parity, PdfPig would need: table extraction built from scratch or
via a revived Tabula integration (already rejected once), cross-document decoration
detection (already rejected as a real engineering lift), a heading vocabulary curated
from 20–30 sample docs and maintained as document formats drift, per-family title
regexes, and an answer for scanned pages that a text-extraction library fundamentally
cannot give (no OCR, per its own docs). That's weeks of speculative heuristic
engineering to approximate what the DI extractor in your repo does today — and
heuristics degrade silently as new document formats arrive, whereas Microsoft retrains
the layout model for you. Given the PDF pipeline is about to become your only
production source, betting it on hand-tuned pre-1.0 heuristics is the riskier path by a
wide margin.

## Recommended follow-ups (in order, none executed — nothing was committed)

1. **Confirmation run:** point `PdfBackendComparisonRunner` at a real Cordaan sample
   batch weighted toward table-heavy and 1–2-page docs; expect table-flattening
   warnings and missing-title counts to separate the backends decisively.
2. **Fix `CostPerPage` to `0.01m`** so all future cost telemetry is honest.
3. **DI-specific title extraction:** populate the title metadata field from the first
   `ParagraphRole.Title` paragraph, falling back to the current filename-derived title
   — this kills the `LCI-richtlijn` regex dependency for your primary BM25 signal.
4. **Skip unchanged blobs before extraction** (compare blob `LastModified`/ETag against
   stored state) — caps recurring DI cost at changed-docs-only and shrinks run time.
5. **Flip the DI registration** in `program.cs:197-204` from `Name == "PdfPig"` to the
   DI backend, failing loudly if no endpoint is configured.
6. **Keep PdfPig frozen** as the dev-only comparison baseline; don't delete it, don't
   extend it.

**Sources:** Azure DI pricing · pricing Q&A · Layout model docs · Markdown output
elements · Language support (Dutch) · Service limits · EU residency · PdfPig README ·
PdfPig issue #736 · DocumentLayoutAnalysis (tables via Tabula-sharp) · OCR comparison
2026 · AI OCR model comparison
