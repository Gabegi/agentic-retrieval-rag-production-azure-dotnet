namespace RagApp.Evaluation.Tests.Models;

// Answer = judged against ExpectedAnswer with the normal metric suite (Groundedness/
// Relevance/Coherence/Equivalence/Retrieval/F1). Refusal = the corpus/policy requires the
// assistant to decline (prompt injection, medical/legal advice, privacy, over-extraction,
// buiten_scope, ...) - judged instead by RefusalEvaluator against RefusalReason.
public enum ScenarioType { Answer, Refusal }

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
    string RefusalReason = "");  // "Waarom duidelijk weigeren" column — extra judge context for Refusal scenarios, blank for Answer scenarios
