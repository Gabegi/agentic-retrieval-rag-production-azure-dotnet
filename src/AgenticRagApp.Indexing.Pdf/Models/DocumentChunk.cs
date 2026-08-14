using AgenticRagApp.Common.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Models;

// One chunk of a PDF page, embedded and uploaded to Azure AI Search. Renamed from
// ProtocolDocument - that name was Zenya/CSV-era ("care protocols" specifically); this
// project only ever handles PDFs now (see docs/plan210726.md's "no generic" note).
//
// Implements ISnapshotSource/IChunkStatsSource so Observability's SnapshotService and
// ChunkingStageMetrics.Compute can work generically without referencing this (or any other
// doc-type's) chunk type directly - see docs/260721 for why.
//
// Mutable class, not a record: EmbedAndUploadActivity assigns Vector onto an already-built
// chunk after the embedding call returns, so the type needs a setter (or every caller would
// need a `with`-expression rewrite at the one place that actually needs a field written
// post-construction).
public class DocumentChunk : ISnapshotSource, IChunkStatsSource
{
    // ── Search-indexed fields (IndexService's schema) ───────────────────────

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("document_id")]
    public string DocumentId { get; set; } = "";

    // ── Two-grain identity and position (action-plan.md §4.6) ───────────────
    // Naming rule: *_id names a thing, *_index names a position within an explicitly
    // stated scope. Replaces the old ChunkIndex (position-within-PAGE), which collided in
    // meaning with PdfExtractionDocument.Ordinal (page) and TextChunk.Index
    // (position-within-strategy-output).
    //
    // SectionId/SectionIndex/Grain have no producer until the section grain exists - they
    // are defined now so the index schema migrates once rather than once per item.

    // The parent section this unit belongs to. On a parent unit this equals Id, so
    // "everything in this section" is a single filter with no special case.
    [JsonPropertyName("section_id")]
    public string? SectionId { get; set; }

    [JsonPropertyName("section_index")]
    public int SectionIndex { get; set; }

    // Position of this child within its section. 0 on a parent unit.
    [JsonPropertyName("child_index")]
    public int ChildIndex { get; set; }

    // "document" | "parent" | "child". Explicit rather than inferred from SectionId == Id:
    // Q3 option 2 (parents indexed but not embedded) filters parents out of ranking on
    // exactly this field, and inference would make that filter depend on a convention.
    [JsonPropertyName("grain")]
    public string Grain { get; set; } = ChunkGrain.Child;

    // The whole parent section's text, materialized here rather than fetched at query time
    // ("materialize, don't assemble"). Fan-out measured at ~1.25 in Phase A, so this costs
    // roughly 2.7 MB across the corpus. Stored but deliberately NOT indexed - see
    // IndexService.
    [JsonPropertyName("parent_text")]
    public string? ParentText { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("last_modified_date")]
    public DateTimeOffset? LastModifiedDate { get; set; }

    // Native PDF Info-dictionary facts (PdfNativeMetadataExtractor via ExtractionDocument)
    // - CreatedAt/ModDate are the file's own authored/last-edited timestamps, distinct from
    // LastModifiedDate above (blob re-upload timing). ModDate is the real "is this policy
    // current" signal for citations in this HR-compliance app.
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("mod_date")]
    public DateTimeOffset? ModDate { get; set; }

    [JsonPropertyName("page_count")]
    public int? PageCount { get; set; }

    // Zenya's own identity/lifecycle facts (see ExtractionDocument/ZenyaMetadata) - null
    // until whoever uploads this chunk's PDF sets the corresponding blob metadata. A null
    // zenya_document_id here is what marks a passage as untraceable back to Zenya.
    [JsonPropertyName("zenya_document_id")]
    public string? ZenyaDocumentId { get; set; }

    [JsonPropertyName("zenya_version")]
    public string? ZenyaVersion { get; set; }

    [JsonPropertyName("zenya_status")]
    public string? ZenyaStatus { get; set; }

    [JsonPropertyName("zenya_url")]
    public string? ZenyaUrl { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    // ── Heading context ─────────────────────────────────────────────────────
    // Was "Heading". Renamed so the leaf heading and the full chain are two clearly
    // separate concepts rather than one field that means whichever the caller assumed.

    // This unit's own heading, leaf only.
    [JsonPropertyName("heading_text")]
    public string? HeadingText { get; set; }

    // The full heading chain ("Hoofdstuk 3 > 3.2 Dosering"). Searchable in the index, not
    // just a label - it is the context §1.6 wants contributing to scoring.
    [JsonPropertyName("heading_path")]
    public string? HeadingPath { get; set; }

    // H1-H6 nesting level (Heading.Depth). 0 when unknown.
    [JsonPropertyName("heading_depth")]
    public int HeadingDepth { get; set; }

    // Which signal produced the heading - see ChunkHeadingSource. Breadcrumbs (bookmark
    // outline) and DI headings have different provenance, and DI's own nested sections are
    // a third; one field beats three half-populated ones.
    [JsonPropertyName("heading_source")]
    public string? HeadingSource { get; set; }

    // ── Pages ───────────────────────────────────────────────────────────────
    // A unit can span pages once sections are the grain, so a single page number is not
    // enough. Replaces the old PageNumber.
    [JsonPropertyName("page_start")]
    public int PageStart { get; set; }

    [JsonPropertyName("page_end")]
    public int PageEnd { get; set; }

    [JsonPropertyName("content_vector")]
    public float[]? ContentVector { get; set; }

    // Ratio-estimated token count of the exact text embedded/stored (EmbeddingText below) -
    // the low-level TextChunk.EstimatedTokens for this chunk's own body, plus the same
    // estimate for the title/heading prefix ChunkingService prepends (see ChunkingService,
    // which sets this). Distinct from TokenEstimate further down: that one is a coarse
    // word-count proxy already used for the IsOversized/IsUndersized QA gates and left
    // untouched here, not something this field replaces.
    [JsonPropertyName("token_count")]
    public int TokenCount { get; set; }

    // Stored alongside TokenCount, not derived from it: chars/token is not constant
    // (prose ~3.1-3.3, table markdown ~1.9-2.8), so neither reconstructs the other.
    [JsonPropertyName("char_count")]
    public int CharCount => Content.Length;

    // ── Quality flags ───────────────────────────────────────────────────────

    // This child carries overlap from a sibling - lets retrieval de-duplicate without
    // re-comparing text.
    [JsonPropertyName("is_overlap")]
    public bool IsOverlap { get; set; }

    // False when this unit's heading came from a fallback rather than a confident match.
    // The per-chunk form of the heading locator's failure counter: the aggregate says how
    // many failed, this says which chunks to distrust. Defaults true so a unit that never
    // needed locating is not reported as suspect.
    [JsonPropertyName("heading_located")]
    public bool HeadingLocated { get; set; } = true;

    // Set when this unit's pages include figure-only / zero-word pages. The document-level
    // extraction gate cannot see a mixed document - a 134-page file with 20 image-only
    // pages passes chars/page comfortably and loses that content with nothing marking it.
    [JsonPropertyName("page_extraction_flag")]
    public bool PageExtractionFlag { get; set; }

    // Target population (LVB/MVB and similar). A different axis from DomainTag: sector says
    // which care sector, population says which client group. No producer yet.
    [JsonPropertyName("population")]
    public string? Population { get; set; }

    // "nl"/"en" from DI's own AnalyzeResult.Languages. Computed at extraction today but
    // never carried this far - the one English document's ~4:1 chars/token ratio makes
    // every character-derived ceiling wrong for it.
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    // ── Derived Search-indexed fields (Tier 2) ──────────────────────────────
    // Computed from the raw structural fields below, the same way TokenEstimate/IsEmpty
    // etc. further down are computed from Content - simple scalars/collections Search can
    // actually store, derived from the richer objects Search can't.

    [JsonPropertyName("table_count")]
    public int TableCount => Structure.Tables.Count;

    [JsonPropertyName("has_table")]
    public bool HasTable => Structure.Tables.Count > 0;

    // Sourced only from DI's own structured Figure.Caption (PdfDocumentIntelligenceAnalyzer.GetFigures) -
    // expect this to be empty on most current documents, since none of them contain figures
    // yet and DI's own caption detection is inconsistent even when they do (see the
    // FiguresWithoutCaption warning in PdfDocumentIntelligenceAnalyzer.StructureWarnings). PdfCleaner
    // separately extracts a figure's figcaption/alt text into the page's Content text (see
    // ConvertFigures) - that's deliberately not threaded back into this structured field
    // today; revisit once real figure-bearing documents exist to validate against.
    [JsonPropertyName("figure_captions")]
    public IReadOnlyList<string> FigureCaptions => Structure.Figures
        .Where(f => !string.IsNullOrWhiteSpace(f.Caption))
        .Select(f => f.Caption!)
        .ToList();

    // ── Everything else extraction produced ─────────────────────────────────
    // Not in the Search schema (no simple/collection field shape fits these - nested
    // objects like TableInfo's cells, or file-level data like Bookmarks/Sections) - but
    // NOT [JsonIgnore]'d. That attribute is type-level, not call-site-level: it would
    // strip these fields from every serialization of DocumentChunk, not just the Search
    // upload one - including the ChunkActivity -> EmbedAndUploadActivity blob hand-off
    // (chunks.json) and the Stage 2 archive, silently losing this data before it could
    // ever reach either. See SearchUploadChunk for the actual Search-only projection,
    // built right before the upload call instead.
    //
    // Author is the Word-exporting user's login (e.g. "mherbst"), not a real policy
    // owner - kept for traceability/debugging but deliberately not Search-indexed.

    public string? Author { get; set; }

    // Bookmarks and Sections USED to be carried here - the whole document's outline and DI
    // section tree, copied onto every chunk of that document. Nothing ever read either one,
    // and once extraction started populating SectionInfo.ResolvedElements the cost showed up
    // hard: 3,046 chunks serialized to 772 MB against a 16 MB extraction artifact for the
    // same corpus, and EmbedAndUploadActivity ran out of memory deserializing it.
    //
    // Both live once per document on PdfExtractionDocument, which is where a consumer that
    // needs them should read them. Do not reintroduce them here: per-document data attached
    // per chunk is quadratic in document size, and nothing surfaces that until a blob read
    // fails (action-plan.md §3.2 recorded the same shape of problem in the extraction blob).

    public string? Breadcrumb { get; set; }

    // Family/domain identity resolved once per document by DocumentIdentityResolver, before
    // chunking, then carried onto every chunk of that document - same pattern as
    // Title/Breadcrumb. Not Search-indexed yet: nothing filters/routes on family or domain
    // today (chunking strategy itself is deliberately out of scope for pre-chunking action
    // items - docs/2608/260811/pre-chunking-action-items.md), same reasoning TokenCount's
    // sibling fields already follow.
    [JsonPropertyName("family_id")]
    public string? FamilyId { get; set; }

    [JsonPropertyName("domain_tag")]
    public string? DomainTag { get; set; }

    // SourceIds of other documents this one's title is lexically close to but NOT clustered
    // with by FamilyId (e.g. Medido/Medimo - see DocumentIdentityResolver's C3 check) - a possible-
    // confusion flag, not a family relationship. SourceIds rather than titles so a consumer
    // can look the other document up directly, same identifier shape as FamilyId itself.
    public IReadOnlyList<string> ConfusableWith { get; set; } = [];

    // The page-level structural payload, grouped rather than spread across seven loose
    // properties - see ChunkStructure for why it is carried but not Search-indexed.
    public ChunkStructure Structure { get; set; } = ChunkStructure.Empty;

    [JsonIgnore] public int  TokenEstimate => Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    [JsonIgnore] public bool IsEmpty       => string.IsNullOrWhiteSpace(Content);
    [JsonIgnore] public bool IsOversized   => TokenEstimate > 1024;
    [JsonIgnore] public bool IsUndersized  => TokenEstimate < 20;

    // Sentence boundary proxies — a coherent chunk starts and ends at natural boundaries
    [JsonIgnore] public bool StartsClean => Content.Length > 0 && (char.IsUpper(Content[0]) || char.IsDigit(Content[0]));
    [JsonIgnore] public bool EndsClean   => Content.Length > 0 && ".!?:)\"'".Contains(Content[^1]);
    [JsonIgnore] public bool IsCoherent  => StartsClean && EndsClean;

    // Title and Breadcrumb/Heading are already prepended into Content by ChunkingService,
    // so this is just Content - kept as a named property (rather than every caller reading
    // Content directly) so "what gets embedded" and "what gets stored/searched" stay two
    // separately named concepts, even though they're identical today.
    [JsonIgnore] public string EmbeddingText => Content;

    // Hash of the exact text sent to the embedding API - a match means the embedding would
    // come back byte-identical, so EmbeddingService can skip the call and reuse the cached
    // vector instead. [JsonIgnore] for the same reason as the fields above - no matching
    // Search schema field.
    [JsonIgnore] public string ContentHash =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EmbeddingText)));
}
