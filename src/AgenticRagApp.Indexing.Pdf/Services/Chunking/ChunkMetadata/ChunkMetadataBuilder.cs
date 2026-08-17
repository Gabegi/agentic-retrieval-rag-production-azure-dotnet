using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Step 4 of the chunking stage: turn a cut into an indexed row.
//
// The split of responsibility this class exists to make explicit: a strategy decides WHERE to
// cut and knows nothing about ids, Zenya metadata or embedding prefixes; this decides how a cut
// becomes an indexed row and knows nothing about headings or ceilings.
//
// NOT IMPLEMENTED YET. ChunkingService.ToChunk still does this work; step 4 of
// docs/2608/260818/chunking-service-refactor.md moves it here and organises it as the four
// metadata scopes below. Sequenced after step 3 on purpose - moving the mapping while the
// strategies are still empty would leave nothing to map.
//
// ── Scope 1: property of the DOCUMENT (extract once, stamp onto every chunk) ──
//   Today: doc_id, title, family_id, domain_tag, language, Zenya fields, dates, page span.
//   To add: route_name and size_class from the gate verdict; valid_from/valid_to regex-extracted
//   from the title (CAO titles carry the validity period - "CAO GGZ 2024 2026").
//   No source_path: DocumentId already IS the blob name.
//
// ── Scope 2: property of the CHUNK (derived at cut time, free) ──
//   Everything already on ChunkUnit (heading fields, ordinals, Start/Length, IsOverlap), plus
//   chunk_start/chunk_length on the row so the offset round-trip is assertable and the
//   query-time window has something to slice with. parent_id needs no new field - SectionId is
//   already doc_id::sN. contains_table is a cheap pipe regex on the chunk's own Content, under
//   the name the index already has (has_table), whose current derivation answers the weaker
//   question "does a table exist on the pages this chunk covers".
//   Ordinals are plain fields and must NEVER enter the chunk hash: an inserted section must not
//   re-embed everything below it.
//
// ── Scope 3: property of the CONTENT (DELIBERATELY EMPTY - extension point) ──
//   Candidates: clause effective dates, amounts/percentages, article numbers in body text,
//   entities, obligation type, cross references, extracted keywords.
//   Trigger: a measured recall failure that names one. Ladder inside: regex before model.
//   Why empty: sector, year and article are largely covered by scope 1 plus the heading path
//   already, and per-chunk model calls are where cost surprises live.
//
// ── Scope 4: GENERATED content (DELIBERATELY EMPTY - extension point) ──
//   Candidates: generated context (one situating line per chunk - the Content Understanding
//   rung; expected trigger is anonymous-chunk failures clustering in route 2), generated
//   questions (trigger: shape-mismatch failures), rephrasing, document summaries, keywords.
//   If ever built: content is DATA in a delimited block, never instructions; validate the
//   output SHAPE; and generated text joins the vector hash.
public static class ChunkMetadataBuilder
{
}
