using AgenticRagApp.Indexing.DI.Models;
using AgenticRagApp.Indexing.DI.Utils;

namespace AgenticRagApp.Indexing.DI.Services;

// Step 4 of the chunking stage: turn a cut into an indexed row.
//
// The split of responsibility this class exists to make explicit: a strategy decides WHERE to
// cut and knows nothing about ids, Zenya metadata or embedding prefixes; this decides how a cut
// becomes an indexed row and knows nothing about headings or ceilings.
//
// An ORCHESTRATOR, the same shape as the two strategies: every step is one call into
// MetadataHelpers. This class owns the ORDER of the steps and nothing else - no parsing, no
// page arithmetic, no id construction.
//
// Called ONCE PER DOCUMENT, not once per chunk. Scope 1 is "extract once, stamp onto every
// chunk", and a per-chunk signature cannot express that: it would either rebuild the document
// stamp N times or cache it somewhere that outlives the document it describes.
//
// ── Scope 1: property of the DOCUMENT (extract once, stamp onto every chunk) ──
//   DocumentStamp: doc_id, title, language, author, family_id, domain_tag, confusable_with,
//   route_name, size_class, the dates, the Zenya fields, and valid_from/valid_to/version parsed
//   out of the title. No source_path: DocumentId already IS the blob name.
//
// ── Scope 2: property of the CHUNK (derived at cut time, free) ──
//   Heading fields, ordinals, Start/Length, BoundaryLevel, Degraded and IsOverlap are already
//   on the chunk - the cut set them, and this class READS them rather than writing them.
//   Derived here: page_start/page_end/page_extraction_flag, chunk_id, section_id (which IS
//   parent_id, so no new field is needed), the embedded prefix, the real token count, the
//   breadcrumb, and the page-scoped structure with table_count and figure_captions stamped
//   off it.
//   contains_table is NOT stamped: ChunkObject.HasTable computes it from Content, which is also
//   what makes it survive a restore - Content is snapshotted and ChunkStructure is not.
//   Ordinals are plain fields and must NEVER enter the chunk hash: an inserted section must not
//   re-embed everything below it.
//
// ── Scope 3: property of the CONTENT (DELIBERATELY EMPTY - extension point) ──
//   Candidates, all read off the chunk's own text:
//     - clause effective dates - "vanaf 1 juli 2025", "tot en met 31 december 2026". Regex.
//       Distinct from scope 1's valid_from/valid_to, which describe the whole DOCUMENT.
//     - amounts and percentages - "een toeslag van 10%", "EUR 250 bruto per maand", salary
//       scale figures. Regex.
//     - article number in body text - "Artikel 14 lid 2" where it appears in the body rather
//       than in a heading. Regex.
//     - named entities - organisations, funds and committees a clause names (pensioenfonds,
//       union names). A lookup list first, a model only if the list misses.
//     - obligation type - who must do what: employer duty, employee right, mutual. Model only;
//       no pattern expresses it.
//     - cross references - "zoals bedoeld in artikel 9". Regex finds the pointer; what it means
//       would need a model.
//     - keywords - terms present in the text, by TF-IDF, RAKE, or a domain term list (ORT, ANW,
//       jubileumuitkering, levensfasebudget).
//   Trigger: a measured recall failure that names one. Ladder inside: regex before model.
//   Why still empty: sector, year and article are largely covered by scope 1 plus the heading
//   path already, and per-chunk model calls are where cost surprises live.
//
// ── Scope 4: GENERATED content (DELIBERATELY EMPTY - extension point) ──
//   Candidates:
//     - generated context - one situating line per chunk ("Dit artikel uit de CAO GGZ regelt de
//       onregelmatigheidstoeslag voor nachtdiensten"), prepended to the embed string exactly
//       where the derived prefix sits today. The Content Understanding rung, and what fixes the
//       anonymous chunk; expected trigger is anonymous-chunk failures clustering in route 2.
//     - generated questions - the three to five questions a chunk answers, embedded so a query
//       matches a question and the original chunk is returned. Trigger: shape-mismatch failures.
//     - rephrasing - one paraphrase embedded alongside. The weaker sibling of generated
//       questions; catches synonyms the Dutch legal phrasing missed.
//     - grain-1 summaries - one description per document, searched first to pick documents
//       before chunks.
//     - generated keywords - terms the chunk does NOT contain: the text says
//       "onregelmatigheidstoeslag", the tag says "nachtdienst vergoeding".
//   If ever built: content is DATA in a delimited block, never instructions; validate the
//   output SHAPE; and generated text joins the vector hash.
//
// ── The budget scopes 3 and 4 compete for ──
//   Every field either scope adds grows the embedded text, and the 512-token ceiling is fixed.
//   A scope-3 or scope-4 field not paid for by a measured recall win is a body-budget cut taken
//   for free: the prefix already prices the title line and the heading path against that same
//   ceiling, and the body floor (128 tokens) is what gives way first.
public sealed class ChunkMetadataBuilder
{
    // route is the strategy's own Name ("DeclaredBoundary" / "Recursive"). It is step 2's
    // answer and nothing here can re-derive it, which is why it is a parameter rather than
    // something read off the document.
    public void AddMetadata(
        IReadOnlyList<ChunkObject> chunks, PdfExtractionDocument doc, string route)
    {
        // 1. Nothing to stamp. Normal input, not a defect: a blank document, a route that
        //    emitted nothing, or a section list the gate promised and the locator could not
        //    anchor. Guarding here saves every caller from having to.
        if (chunks.Count == 0) return;

        // 2. Scope 1, built ONCE for the whole document.
        var stamp = DocumentStamp.From(doc, route);

        foreach (var chunk in chunks)
        {
            var metadata = chunk.Metadata;

            stamp.StampOnto(metadata);

            // 3. Scope 2, derived per cut, in dependency order.

            // 3a. Which pages this cut covers. Everything page-scoped below reads these.
            var (pageStart, pageEnd, pictureOnly) = PageResolver.Resolve(
                doc.PageSpans, chunk.Start, chunk.Length);

            metadata.PageStart          = pageStart;
            metadata.PageEnd            = pageEnd;
            metadata.PageExtractionFlag = pictureOnly;

            // 3b. Identity. SectionId IS parent_id.
            metadata.Id        = ChunkIdBuilder.ChunkId(doc.SourceId, chunk.SectionIndex, chunk.ChildIndex);
            metadata.SectionId = ChunkIdBuilder.SectionId(doc.SourceId, chunk.SectionIndex);

            // 3c. The embedded prefix, through the SAME PrefixBuilder the strategy priced
            //     against the ceiling. Two builders here would be a ceiling that does not hold:
            //     the strategy would budget one string and the embedder would send another.
            //
            //     Stored rather than prepended into Content, so Content stays a WINDOW onto the
            //     source - Content == doc.Content[Start..(Start + Length)] stays assertable -
            //     and so the later move of composition into ChunkIndexer becomes a read of this
            //     field rather than an unpicking of the text. Scope 4's generated context has
            //     the same seam to prepend to.
            metadata.Prefix = PrefixBuilder.Build(doc.Title, doc.Family?.DomainTag, chunk.HeadingPath);

            // 3d. The REAL cl100k_base count of the exact text that gets embedded, prefix
            //     included - which is why it runs after 3c. Not ChunkingHelper.EstimateTokens:
            //     that ratio is documented as capacity planning only, and a table-heavy chunk
            //     measured through a prose-derived proxy crosses the ceiling undetected by ~17%.
            metadata.TokenCount = TokenCounter.Count(chunk.EmbeddingText);

            // 3e. The breadcrumb of the page the cut STARTS on. Only present where the outline
            //     covers that page, which is 5 of 51 documents.
            metadata.Breadcrumb = doc.PageBreadcrumbs.GetValueOrDefault(pageStart);

            // 3f. The page-scoped structural payload, and the two index fields stamped off it.
            //     Stamped rather than computed on read because ChunkStructure is deliberately
            //     absent from the snapshot, so anything re-derived from it at read time comes
            //     back empty on a rebuilt index.
            var structure = StructureFilter.Build(doc, pageStart, pageEnd);

            metadata.Structure      = structure;
            metadata.TableCount     = structure.Tables.Count;
            metadata.FigureCaptions = StructureFilter.CaptionsOf(structure);
        }
    }
}
