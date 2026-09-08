namespace RagApp.Evaluation.Tests.Models;

// Answer = judged against ExpectedAnswer with the normal metric suite (Groundedness/
// Relevance/Coherence/Equivalence/Retrieval/F1). Refusal = the corpus/policy requires the
// assistant to decline (prompt injection, medical/legal advice, privacy, over-extraction,
// buiten_scope, ...) - judged instead by RefusalEvaluator against RefusalReason.
public enum ScenarioType { Answer, Refusal }

// What the question demands of RETRIEVAL, as opposed to how hard the answer is to phrase.
// This is the axis the agentic-retrieval experiment is about: the knowledge base plans and
// issues its own searches, so it can only beat a single vector search on questions that
// actually need more than one. Labelling each row lets a run be read per capability instead
// of as one mean - and makes the null result visible too, since SingleLookup rows are the
// control group where planning can only add latency and tokens.
//
// Set on the DATASET side (what the question needs); compare it against the run's measured
// SubQueryCount/DistinctDocumentsCited (EvalRow) to see what the planner actually did.
public enum AgenticCapability
{
    // Control group: one fact, one passage, no planning needed. Any extra planning cost here
    // is pure overhead - that is the point of keeping these rows in the set.
    SingleLookup,

    // Two or more INDEPENDENT sub-questions in one turn. A single search has to pick one.
    Decomposition,

    // Sub-question 2 can only be formulated once sub-question 1 is answered (the bridging
    // fact - which cao applies, which phase 36 months falls in - is itself retrieved).
    MultiHop,

    // The same fact exists in several documents with different values per sector/target
    // group; a complete answer contrasts them rather than picking one.
    CrossDocCompare,

    // Two passages of the SAME document, far apart (body vs appendix, article vs summary).
    // Chunk-level top-k tends to return one neighbourhood; planning can ask twice.
    DistantSections,

    // The sources genuinely disagree. Answering at all means noticing that, so the failure
    // mode is a confident answer, not a missing one.
    ConflictDetection,

    // The question's wording barely overlaps the corpus lexically - colloquial phrasing, an
    // acronym only, or a Dutch question whose answer lives in an English document.
    VocabularyGap,

    // The query deliberately omits the discriminator (sector, target group, product), so the
    // correct response enumerates the variants or asks back instead of choosing one.
    Disambiguation,

    // The retrieved document says of itself that it is superseded/dated. Answering from its
    // body without carrying that forward cites withdrawn policy.
    Staleness,

    // The corpus does not contain the answer, and a near-miss document does. The failure
    // mode is substitution, not silence.
    Abstention,

    // The request must be declined (or partly declined) regardless of what retrieval finds.
    Guard,
}

public record TestQuery(
    string Name,            // short id for the scenario, e.g. "vilans-01" or "gq-refuse-015"
    string Department,      // golden-questions PDF's "Bronlijst" group, e.g. "Vilans Protocollen", "Refusals", "Inschaling", "D&I", "Gedragscode"
    string Query,           // Vraag
    string ExpectedAnswer,  // Antwoord — for Refusal scenarios this is the PDF's fallback text/category label, not scored for text similarity
    string ExpectedSources, // Bron
    string Difficulty,      // Lastigheid — Low/Medium/High (or blank)
    string Value,           // Waarde — business-case notes; not used in scoring, just carried for docs
    bool   AnswerableFromCorpus = true,  // false = known corpus gap: expect abstention, skip F1
    ScenarioType Type = ScenarioType.Answer,
    string Category = "",        // Categorie column: protocol / buiten_scope / medisch_advies / promptinjectie / autorisatie / privacy / juridisch_advies / misbruik / security / overmatige_extractie / ...
    string RefusalReason = "",   // "Waarom duidelijk weigeren" column — extra judge context for Refusal scenarios, blank for Answer scenarios
    string Sector = "",          // CAO/sector this question is scoped to, e.g. "GGZ", "GHZ", "VVT" — blank if sector-agnostic, "ambiguous" if the query deliberately omits the sector to test disambiguation
    string Role = "",            // Job role the question is asked from, e.g. "Zorgmedewerker", "Schoonmaker" — blank if not role-specific
    string ClientGroup = "",     // Client group the question concerns, e.g. "Ouderenzorg", "GGZ-cliënten" — blank if not client-group-specific
    string Version = "",         // Document/policy version the answer is scoped to, e.g. "vóór 1-1-2025" / "vanaf 1-1-2025" — blank if version-agnostic

    // What retrieval has to do to answer this at all (see AgenticCapability). Defaults to
    // SingleLookup so an unlabelled row counts as a control rather than silently as a win.
    AgenticCapability Capability = AgenticCapability.SingleLookup,

    // Lower bound on the number of DISTINCT searches a complete answer needs, judged from
    // the corpus by hand. Compared against the run's measured SubQueryCount: fewer means the
    // planner did not decompose far enough to have seen everything the answer requires, so a
    // high score on such a row was luck or memory rather than retrieval.
    int MinSubQueries = 1,

    // Why this row is in the set at all — which specific failure it is built to provoke.
    // Not scored; it is what makes a regression readable six months from now.
    string Trap = "");
