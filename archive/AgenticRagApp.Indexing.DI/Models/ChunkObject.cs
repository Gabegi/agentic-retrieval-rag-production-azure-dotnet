using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AgenticRagApp.Common.Models;
using AgenticRagApp.Indexing.DI.Utils;

namespace AgenticRagApp.Indexing.DI.Models;

// One indexed chunk, end to end: the cut a strategy made, the metadata step 4 stamped on it,
// and the vector the embedder attached. The single chunk type in this pipeline - DocumentChunk
// was folded into this one, because two types describing the same row is how family_id came to
// be wired into the index schema at one end and into the strategies at the other with nothing
// joining them in the middle.
//
// Three halves, filled by three different steps and never by each other:
//   - the CUT (Content, Start/Length, ordinals, heading fields) comes from the strategy in
//     step 3. A strategy decides WHERE to cut and knows nothing about ids or page attribution.
//   - the METADATA comes from ChunkMetadataBuilder in step 4. It knows nothing about headings
//     or ceilings.
//   - the VECTOR is attached by EmbeddingService after the embedding call returns.
//
// Implements ISnapshotSource/IChunkStatsSource so Observability's SnapshotService and
// ChunkingStageMetrics.Compute work generically, without referencing this (or any other
// doc-type's) chunk type - see docs/260721 for why.
//
// Mutable class, not a record: ContentVector is assigned onto an already-built chunk after the
// embedding call, and step 4 writes metadata onto a chunk step 3 already produced.
//
// Start/Length are CLEANED-text coordinates: they index into PdfExtractionDocument.Content, the
// same space PageSpan.Offset uses. DI's own Heading.Offset addresses raw content and is not
// comparable - it orders headings, it never slices.
public sealed class ChunkObject : ISnapshotSource, IChunkStatsSource
{
    // ── The cut (step 3) ────────────────────────────────────────────────────

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("chunk_start")]
    public int Start  { get; set; }

    [JsonPropertyName("chunk_length")]
    public int Length { get; set; }

    // Position of the section this cut came from, and of this cut within that section.
    // Together with the document id, the chunk's identity.
    [JsonPropertyName("section_index")]
    public int SectionIndex { get; set; }

    [JsonPropertyName("child_index")]
    public int ChildIndex   { get; set; }

    // The whole parent section's text, materialized onto the child rather than fetched at
    // query time. No producer under the two-strategy design - draft §5.5 replaced it with
    // parent_id + ordinals and a structural window that slices the source at query time - but
    // kept as a field so a snapshot written before that change still round-trips.
    [JsonPropertyName("parent_text")]
    public string? ParentText { get; set; }

    // This cut's own heading, leaf only.
    [JsonPropertyName("heading_text")]
    public string? HeadingText { get; set; }

    // The full chain ("Hoofdstuk 3 > 3.2 Dosering"). Searchable in the index, not just a
    // label - it is context that contributes to scoring.
    [JsonPropertyName("heading_path")]
    public string? HeadingPath { get; set; }

    // H1-H6 nesting level. 0 when unknown.
    [JsonPropertyName("heading_depth")]
    public int HeadingDepth { get; set; }

    // Which signal produced the heading - see ChunkHeadingSource. "none" on a cut no heading
    // covers, which is every cut on the recursive route.
    [JsonPropertyName("heading_source")]
    public string HeadingSource { get; set; } = ChunkHeadingSource.None;

    // Whether the heading was actually found in the cleaned text. False whenever HeadingSource
    // is "none" - true there would read as a successful location in any aggregate. Note this
    // defaults FALSE, where DocumentChunk defaulted true: an unset value now means "not
    // located", which is what the aggregate should assume until something claims otherwise.
    [JsonPropertyName("heading_located")]
    public bool HeadingLocated { get; set; }

    // This cut carries overlap from its predecessor - lets retrieval de-duplicate without
    // re-comparing text.
    [JsonPropertyName("is_overlap")]
    public bool IsOverlap { get; set; }

    // Which boundary this cut was made on - carried over from the ContentPiece the strategy
    // produced. The fall-through metric: a chunk that came back HardCut had no usable separator
    // anywhere in it, which almost always means extraction produced none rather than that the
    // text is genuinely unbreakable. Defaults to None, which is also the honest value for a
    // section or block that fitted whole - the 83-87% path.
    [JsonPropertyName("boundary_level")]
    public BoundaryLevel BoundaryLevel { get; set; } = BoundaryLevel.None;

    // The ceiling was breached by choice rather than met. Set where the alternative was worse
    // than an oversized chunk - a single table row that alone exceeds the budget, a key-value
    // pair that cannot be split without separating a value from its label. Reported, not
    // silently absorbed: it is the one flag that says a token count above the ceiling was
    // deliberate.
    [JsonPropertyName("degraded")]
    public bool Degraded { get; set; }

    // ── The metadata (step 4) ───────────────────────────────────────────────

    [JsonPropertyName("metadata")]
    public ChunkMetadata Metadata { get; set; } = new();

    // ── The vector (attached after embedding) ───────────────────────────────

    [JsonPropertyName("content_vector")]
    public float[]? ContentVector { get; set; }

    // ── IChunk / ISnapshotSource / IChunkStatsSource ────────────────────────
    // Read through the metadata rather than duplicated: these are stamped in step 4, and a
    // second copy is a second thing to keep in sync.

    [JsonIgnore] public string          Id               => Metadata.Id;
    [JsonIgnore] public string          DocumentId       => Metadata.DocumentId;
    [JsonIgnore] public int             PageStart        => Metadata.PageStart;
    [JsonIgnore] public string?         Title            => Metadata.Title;
    [JsonIgnore] public DateTimeOffset? LastModifiedDate => Metadata.LastModifiedDate;

    // The rest of what ISnapshotSource requires. A snapshot is what the index is rebuilt FROM,
    // so it has to carry every field the index holds - see ISnapshotSource. Pass-throughs, not
    // copies, for the same reason as the block above: these are stamped in step 4 and a second
    // copy is a second thing to keep in sync.
    [JsonIgnore] public string?         SectionId          => Metadata.SectionId;
    [JsonIgnore] public string          Grain              => Metadata.Grain;
    [JsonIgnore] public int             PageEnd            => Metadata.PageEnd;
    [JsonIgnore] public bool            PageExtractionFlag => Metadata.PageExtractionFlag;
    [JsonIgnore] public int             TokenCount         => Metadata.TokenCount;

    [JsonIgnore] public string?               FamilyId       => Metadata.FamilyId;
    [JsonIgnore] public string?               DomainTag      => Metadata.DomainTag;
    [JsonIgnore] public IReadOnlyList<string> ConfusableWith => Metadata.ConfusableWith;
    [JsonIgnore] public string?               Population     => Metadata.Population;
    [JsonIgnore] public string?               Language       => Metadata.Language;

    // Prefix travels because ContentHash is computed FROM it. A restore that rebuilt a chunk
    // without it would recompute a different hash than the one it just resolved the vector by,
    // and nothing downstream could tell.
    [JsonIgnore] public string          Prefix    => Metadata.Prefix;
    [JsonIgnore] public DateTimeOffset? ValidFrom => Metadata.ValidFrom;
    [JsonIgnore] public DateTimeOffset? ValidTo   => Metadata.ValidTo;
    [JsonIgnore] public string?         Version   => Metadata.Version;

    [JsonIgnore] public DateTimeOffset? CreatedAt       => Metadata.CreatedAt;
    [JsonIgnore] public DateTimeOffset? ModDate         => Metadata.ModDate;
    [JsonIgnore] public int?            PageCount       => Metadata.PageCount;
    [JsonIgnore] public string?         ZenyaDocumentId => Metadata.ZenyaDocumentId;
    [JsonIgnore] public string?         ZenyaVersion    => Metadata.ZenyaVersion;
    [JsonIgnore] public string?         ZenyaStatus     => Metadata.ZenyaStatus;
    [JsonIgnore] public string?         ZenyaUrl        => Metadata.ZenyaUrl;

    // ── Derived from Content ────────────────────────────────────────────────

    // Stored alongside Metadata.TokenCount, not derived from it: chars/token is not constant
    // (prose ~3.1-3.3, table markdown ~1.9-2.8), so neither reconstructs the other.
    [JsonIgnore] public int CharCount => Content.Length;

    // A coarse word-count proxy, distinct from the real tokenizer count on the metadata.
    // Feeds the IsOversized/IsUndersized QA gates only.
    [JsonIgnore] public int  TokenEstimate => Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    [JsonIgnore] public bool IsEmpty       => string.IsNullOrWhiteSpace(Content);
    [JsonIgnore] public bool IsOversized   => TokenEstimate > 1024;
    [JsonIgnore] public bool IsUndersized  => TokenEstimate < 20;

    // Sentence-boundary proxies - a coherent chunk starts and ends at natural boundaries.
    // '|' counts as a clean end: a chunk closing on a complete table row ended at exactly the
    // boundary TableCutter cut on, and without it every table chunk read as incoherent - 505
    // of the 260818 run's 2,997 chunks, a fifth of the CoherentChunks shortfall.
    [JsonIgnore] public bool StartsClean => Content.Length > 0 && (char.IsUpper(Content[0]) || char.IsDigit(Content[0]));
    [JsonIgnore] public bool EndsClean   => Content.Length > 0 && ".!?:)\"'|".Contains(Content[^1]);
    [JsonIgnore] public bool IsCoherent  => StartsClean && EndsClean;

    // ── The structural index fields ─────────────────────────────────────────
    // Simple scalars/collections Search can store, standing in for the richer objects it can't.
    //
    // Split by what they are properties OF, which is what decides how each one survives a
    // restore. HasTable is a property of this chunk's own text, so it is computed from Content -
    // and because Content is snapshotted, it comes back correct for free. The other two are
    // properties of the document's PAGES, so they are stamped once in step 4 and carried:
    // ChunkStructure is deliberately absent from the snapshot, so anything recomputed from it at
    // read time can only ever restore as empty.

    // Does this chunk's text contain a markdown table? Note this is a narrower claim than the
    // page-scoped one it replaced ("a table exists on the pages this chunk covers"), which was
    // true of any prose chunk sharing a page with a table.
    [JsonIgnore] public bool HasTable => ChunkingHelper.ContainsTable(Content);

    [JsonIgnore] public int                   TableCount     => Metadata.TableCount;
    [JsonIgnore] public IReadOnlyList<string> FigureCaptions => Metadata.FigureCaptions;

    // ── The embedding seam ──────────────────────────────────────────────────

    // What gets embedded, composed from the two pieces that are stored separately: the derived
    // prefix (title line, sector tag, capped heading path) and the body.
    //
    // Content is BODY ONLY, deliberately. Prepending the prefix into it would cost the slice
    // invariant - Content == doc.Content[Start..(Start + Length)] - which is what makes offsets
    // assertable and what a query-time structural window slices with. Keeping the two apart also
    // means the later move of composition into ChunkIndexer is a read of Metadata.Prefix rather
    // than an unpicking of the stored text, and it gives scope 4's generated context somewhere
    // to be inserted that is not the retrievable body.
    //
    // The composition itself is not a free choice: this exact joiner is what the old ToChunk
    // path produced, and changing it changes every vector and forces a full re-embed.
    [JsonIgnore] public string EmbeddingText =>
        Metadata.Prefix.Length > 0 ? $"{Metadata.Prefix}\n\n{Content}" : Content;

    // Hash of the exact text sent to the embedding API - a match means the embedding would come
    // back byte-identical, so EmbeddingService can skip the call and reuse the cached vector.
    //
    // Covers the embedded text ONLY. It cannot see a family move: family_id is not in the
    // embedded text, so a document changing families hashes identically and the diff skips it.
    // That is what IdentityResolutionResult.FamilyMoves exists to signal.
    [JsonIgnore] public string ContentHash =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EmbeddingText)));

    // IChunkStatsSource.StatsText - the string ChunkingStageMetrics measures sizes and duplicates
    // on. EmbeddingText, not Content, and the reason is on the interface: Content stopped being
    // prefix + body, so leaving the stats on it would shift every band down by the prefix length
    // and collapse two sections with identical bodies under different headings into a duplicate.
    //
    // IsCoherent above deliberately does NOT follow it. StartsClean/EndsClean ask whether the
    // chunk begins and ends at sentence boundaries, and the prefix is a title line that always
    // starts with a capital - measured on EmbeddingText, StartsClean would be true by
    // construction, which is what it silently was before the split. On the bare body it asks the
    // question it was written to ask. Expect CoherentChunks to drop against pre-refactor runs for
    // that reason, and read the drop as the measurement being repaired rather than the chunker
    // regressing.
    [JsonIgnore] public string StatsText => EmbeddingText;
}

// Everything step 4 stamps onto a cut: what the DOCUMENT is (extracted once, copied onto every
// chunk of it) and what is derived from the cut for free.
//
// Mutable by design - ChunkMetadataBuilder.AddMetadata writes onto an already-built chunk.
public sealed class ChunkMetadata
{
    // ── Identity ────────────────────────────────────────────────────────────

    // Carries no page number: the id is scoped to the document and the cut's position within
    // it, so inserting a page no longer shifts every subsequent id - and an id change is a
    // delete-plus-insert in the index, not an update.
    [JsonPropertyName("id")]
    public string Id         { get; set; } = "";

    [JsonPropertyName("document_id")]
    public string DocumentId { get; set; } = "";

    // The parent section this cut belongs to. A grouping key - de-duplicating children of one
    // section, or fetching the rest of it - so nothing needs to exist for it to identify.
    [JsonPropertyName("section_id")]
    public string? SectionId { get; set; }

    // "document" | "parent" | "child". Explicit rather than inferred from SectionId == Id, so
    // a filter that excludes parents from ranking does not depend on a convention.
    [JsonPropertyName("grain")]
    public string Grain { get; set; } = ChunkGrain.Child;

    // ── Property of the document ────────────────────────────────────────────

    [JsonPropertyName("title")]
    public string? Title    { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    // The Word-exporting user's login (e.g. "mherbst"), not a real policy owner - kept for
    // traceability, deliberately not Search-indexed.
    public string? Author   { get; set; }

    // Target population (LVB/MVB and similar). A different axis from DomainTag: sector says
    // which care sector, population says which client group. No producer yet.
    [JsonPropertyName("population")]
    public string? Population { get; set; }

    // Which route ran (step 2's answer) and how the document was sized. On the report row this
    // is how "why did this document take this route" stays answerable from the report alone.
    [JsonPropertyName("route_name")]
    public string? Route     { get; set; }

    [JsonPropertyName("size_class")]
    public string? SizeClass { get; set; }

    // ── Validity, parsed out of the TITLE ───────────────────────────────────
    // The retrieval failure these address is a confident answer quoted from a superseded CAO -
    // the same shape of failure domain_tag exists to prevent, on the time axis instead of the
    // sector axis. In this corpus the title is the only machine-readable statement of it
    // ("CAO GGZ 2024 2026").
    //
    // Null is "the title did not say", never "valid forever". A bare year normalizes to 1 Jan /
    // 31 Dec so a Search range filter can answer "in force on this date" at all - lossy on
    // purpose, and only ever as precise as the title was.

    [JsonPropertyName("valid_from")]
    public DateTimeOffset? ValidFrom { get; set; }

    [JsonPropertyName("valid_to")]
    public DateTimeOffset? ValidTo   { get; set; }

    // The document's own version string ("v2.1"). Distinct from ZenyaVersion below, which comes
    // from blob metadata - a document can carry both, and they can disagree.
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    // ── Family identity (DocumentIdentityResolver, step 1) ──────────────────
    // All three ride in on doc.Family and are stamped onto every chunk of the document.

    // Not decoration: the knowledge agent is handed domain_tag and family_id explicitly,
    // because the dangerous failure in this corpus is a well-formed, on-topic answer from the
    // WRONG sector's CAO - three CAOs give three different vakantietoeslag figures for the same
    // question - and no similarity score can flag that. Left null, the filter breaks and the
    // model stops seeing which population a passage is about.
    //
    // DomainTag is ALSO inside the embedded text, rendered into Prefix below. Both on purpose:
    // in the vector it pushes the signal into similarity, as a field it makes the deterministic
    // filter possible.
    [JsonPropertyName("family_id")]
    public string? FamilyId  { get; set; }

    [JsonPropertyName("domain_tag")]
    public string? DomainTag { get; set; }

    // SourceIds of other documents this one's title is lexically close to but NOT clustered
    // with by FamilyId (Medido/Medimo) - a possible-confusion flag, not a family relationship.
    [JsonPropertyName("confusable_with")]
    public IReadOnlyList<string> ConfusableWith { get; set; } = [];

    // ── Dates and lifecycle ─────────────────────────────────────────────────

    // ModDate is when the content was actually last edited - the real "is this policy current"
    // signal, distinct from LastModifiedDate (blob re-upload timing).
    [JsonPropertyName("last_modified_date")]
    public DateTimeOffset? LastModifiedDate { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt        { get; set; }

    [JsonPropertyName("mod_date")]
    public DateTimeOffset? ModDate          { get; set; }

    [JsonPropertyName("page_count")]
    public int?            PageCount        { get; set; }

    // Zenya's own identity/lifecycle facts, from custom blob metadata. Null until whoever
    // uploads the PDF sets it - a null zenya_document_id marks a passage as untraceable.
    [JsonPropertyName("zenya_document_id")]
    public string? ZenyaDocumentId { get; set; }

    [JsonPropertyName("zenya_version")]
    public string? ZenyaVersion    { get; set; }

    [JsonPropertyName("zenya_status")]
    public string? ZenyaStatus     { get; set; }

    [JsonPropertyName("zenya_url")]
    public string? ZenyaUrl        { get; set; }

    public string? Breadcrumb { get; set; }

    // ── Derived from the cut ────────────────────────────────────────────────

    // A cut that starts inside page 4 and runs into page 5 reports (4, 5) - the reason
    // page_start/page_end replaced a single page number.
    [JsonPropertyName("page_start")]
    public int PageStart { get; set; }

    [JsonPropertyName("page_end")]
    public int PageEnd   { get; set; }

    // The cut's pages include figure-only / zero-word pages. The document-level extraction gate
    // cannot see a mixed document - a 134-page file with 20 image-only pages passes chars/page
    // comfortably and loses that content with nothing marking it.
    [JsonPropertyName("page_extraction_flag")]
    public bool PageExtractionFlag { get; set; }

    // The derived context this chunk carries into its own embedding: title line, sector tag,
    // and (route 1 only) the capped heading path. Built by PrefixBuilder, the same call the
    // strategy priced against the ceiling before cutting.
    //
    // Stored rather than prepended into Content - see ChunkObject.EmbeddingText, which composes
    // the two. It is on the row rather than recomputed because the vector cache key is derived
    // from the composed text: a prefix rebuilt from slightly different inputs at read time would
    // silently re-embed the corpus.
    [JsonPropertyName("prefix")]
    public string Prefix { get; set; } = "";

    // Real tokenizer count of the exact text that gets embedded, not the ratio estimate the
    // ceiling is budgeted with.
    [JsonPropertyName("token_count")]
    public int TokenCount { get; set; }

    // How many tables extraction found on the pages this cut covers, and their figure captions.
    //
    // Stamped from Structure in step 4 rather than computed from it on every read. Structure is
    // deliberately excluded from the snapshot - Tables alone is 36.3 KB per document - so a value
    // recomputed from it at read time restores as zero/empty on a rebuilt index, and both of
    // these are index fields (table_count is filterable). Stamping is what lets them travel.
    //
    // Page-scoped by definition, and kept that way: "a table exists on the pages this chunk
    // covers" is a genuinely different question from "this chunk contains a table", which is
    // what ChunkObject.HasTable now answers off Content.
    public int TableCount { get; set; }

    // Sourced only from DI's own structured Figure.Caption - expect this empty on most current
    // documents. PdfCleaner separately extracts a figure's caption into the page text, which is
    // deliberately not threaded back into this structured field today.
    public IReadOnlyList<string> FigureCaptions { get; set; } = [];

    // The page-scoped structural payload (tables, figures, boilerplate on the pages this cut
    // covers). Carried, not Search-indexed - see ChunkStructure. Bookmarks and Sections are
    // deliberately absent: per-document data attached per chunk is quadratic in document size,
    // and it once produced 772 MB of chunks against a 16 MB extraction artifact.
    public ChunkStructure Structure { get; set; } = ChunkStructure.Empty;
}
