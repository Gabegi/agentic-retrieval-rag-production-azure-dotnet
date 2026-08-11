namespace AgenticRagApp.Indexing.Pdf.Models;

// The four document-level chunking lanes - docs/2608/260811/chunkRoutes.md step 1 for
// Picture (unchanged by B6), chunking-signals-map.md §4 for the Large/Medium/Small split
// (B6 - pre-chunking-action-items.md). Always recomputed per pipeline run from
// CharsPerPage/BytesPerChar/EstimatedTokens (see ChunkRoutingHelper) - never a hardcoded
// document list, so a new document lands in the right lane automatically without a code
// change.
//
// Big/Normal (page-count-based: >=80 pages) is gone - chunking-signals-map.md §4 found
// page count an unreliable size proxy across a 13x density range (IGJ Toetsingskader: 5
// pages, denser than 12-page Gedragscode medewerkers) and replaced it with an estimated-
// token-count rule instead.
public enum ChunkRoute
{
    // CharsPerPage < 1,000 or BytesPerChar >= 100 - sparse text density or high
    // extraction loss, meaning content likely lives in images, not extractable text.
    // Candidate for the fallback / Content Understanding branch. Checked first, same as
    // before B6 - decided entirely by density/extraction-loss, so page count or token
    // count never move a document out of this route.
    Picture,

    // Not Picture, EstimatedTokens >= 50,000 - the largest gap in the corpus's own token
    // distribution (~25,100 -> ~90,900, nothing in between), not a round number chosen for
    // readability. Currently Hygiene Code + the 3 CAO agreements.
    Large,

    // Not Picture, 4,000 <= EstimatedTokens < 50,000 - too big to return the whole
    // document as the retrieval unit, but well short of Large.
    Medium,

    // Not Picture, EstimatedTokens < 4,000 - the "can the whole document BE the
    // retrieval unit" line: below it, returning the document whole costs about what
    // returning one generous chunk costs, so a parent/child hierarchy buys nothing.
    Small,
}
