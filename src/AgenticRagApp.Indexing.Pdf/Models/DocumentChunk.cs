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
// ChunkingResults.Compute can work generically without referencing this (or any other
// doc-type's) chunk type directly - see docs/260721 for why.
public class DocumentChunk : ISnapshotSource, IChunkStatsSource
{
    // ── Search-indexed fields (IndexService's schema) ───────────────────────

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("document_id")]
    public string DocumentId { get; set; } = "";

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

    // Real content now (Breadcrumb, or the first DI-detected heading) - previously always
    // null, since nothing ever set TextChunk.Heading. See ChunkingService.
    [JsonPropertyName("heading")]
    public string? Heading { get; set; }

    [JsonPropertyName("page_number")]
    public int PageNumber { get; set; }

    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("content_vector")]
    public float[]? ContentVector { get; set; }

    // ── Derived Search-indexed fields (Tier 2) ──────────────────────────────
    // Computed from the raw structural fields below, the same way TokenEstimate/IsEmpty
    // etc. further down are computed from Content - simple scalars/collections Search can
    // actually store, derived from the richer objects Search can't.

    [JsonPropertyName("table_count")]
    public int TableCount => Tables.Count;

    [JsonPropertyName("has_table")]
    public bool HasTable => Tables.Count > 0;

    // Sourced only from DI's own structured Figure.Caption (PDFDocumentAnalyzer.GetFigures) -
    // expect this to be empty on most current documents, since none of them contain figures
    // yet and DI's own caption detection is inconsistent even when they do (see the
    // FiguresWithoutCaption warning in PdfDocumentAnalyzer.StructureWarnings). PdfCleaner
    // separately extracts a figure's figcaption/alt text into the page's Content text (see
    // ConvertFigures) - that's deliberately not threaded back into this structured field
    // today; revisit once real figure-bearing documents exist to validate against.
    [JsonPropertyName("figure_captions")]
    public IReadOnlyList<string> FigureCaptions => Figures
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

    public string?         Author { get; set; }
    public IReadOnlyList<Bookmark>    Bookmarks { get; set; } = [];
    public IReadOnlyList<SectionInfo> Sections  { get; set; } = [];

    public string? Breadcrumb { get; set; }
    public IReadOnlyList<Heading>           Headings       { get; set; } = [];
    public IReadOnlyList<Heading>           Boilerplate    { get; set; } = [];
    public IReadOnlyList<TableInfo>         Tables         { get; set; } = [];
    public PageDimensions?                  Dimensions     { get; set; }
    public IReadOnlyList<SelectionMarkInfo> SelectionMarks { get; set; } = [];
    public IReadOnlyList<FigureInfo>        Figures        { get; set; } = [];
    public IReadOnlyList<LineInfo>          Lines          { get; set; } = [];

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
