using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RagApp.Evaluation.Tests.Models;

namespace RagApp.Evaluation.Tests;

/// <summary>
/// Lints testdata/golden-questions.json. No Azure, no model calls - this is the only class in
/// the assembly that runs without an environment, and it runs before any of it is spent.
/// </summary>
/// <remarks>
/// A dataset defect costs a whole eval run. A row whose ExpectedSources names a document that
/// is not in the corpus scores CitationMatch 0 forever and reads as a retrieval regression;
/// a Refusal row without a RefusalReason gives the refusal judge nothing to grade against;
/// a duplicate Name silently overwrites a row in any per-scenario comparison between runs.
/// None of that is visible in the run summary, which is why it is asserted here instead.
///
/// The Capability/MinSubQueries checks are the same idea applied to the agentic-retrieval
/// labels: those two fields are what the per-capability report is grouped by, so a row that
/// claims MinSubQueries 1 while being labelled MultiHop would quietly land in the wrong
/// comparison and make planning look better or worse than it is.
/// </remarks>
[TestClass]
public class GoldenQuestionsDatasetTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string DatasetPath =
        Path.Combine(AppContext.BaseDirectory, "testdata", "golden-questions.json");

    private static TestQuery[] Load() =>
        JsonSerializer.Deserialize<TestQuery[]>(File.ReadAllText(DatasetPath), JsonOptions)
        ?? throw new InvalidOperationException($"{DatasetPath} deserialized to null.");

    [TestMethod]
    public void Dataset_Deserializes()
    {
        var rows = Load();

        Assert.IsTrue(rows.Length > 0, "golden-questions.json contains no rows.");
        Assert.IsTrue(rows.All(r => !string.IsNullOrWhiteSpace(r.Query)),
            "Every row needs a Query - RagEvaluationTests.LoadFile silently drops rows without one.");
    }

    [TestMethod]
    public void ScenarioNames_AreUnique()
    {
        var duplicates = Load()
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.IsTrue(duplicates.Count == 0,
            "Duplicate scenario names, which collide when two runs are compared per scenario: " +
            string.Join(", ", duplicates));
    }

    [TestMethod]
    public void RefusalRows_HaveARefusalReason()
    {
        var missing = Load()
            .Where(r => r.Type == ScenarioType.Refusal && string.IsNullOrWhiteSpace(r.RefusalReason))
            .Select(r => r.Name)
            .ToList();

        Assert.IsTrue(missing.Count == 0,
            "RefusalEvaluator grades against RefusalReason, so a blank one leaves the judge " +
            "guessing what the row is testing: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void AnswerRows_HaveAnExpectedAnswer()
    {
        var missing = Load()
            .Where(r => r.Type == ScenarioType.Answer && string.IsNullOrWhiteSpace(r.ExpectedAnswer))
            .Select(r => r.Name)
            .ToList();

        Assert.IsTrue(missing.Count == 0,
            "Equivalence is scored against ExpectedAnswer: " + string.Join(", ", missing));
    }

    // The corpus this dataset is written against, as the ids the index actually reports:
    // testdata/corpus-manifest.txt, one blob name per line, '#' lines are provenance comments.
    //
    // Until 2026-09-23 this read a listing of data/chatbot-51pdf-documenten, which worked while a
    // document id was a human-readable PDF filename. Under Zenya an id is 'pdf/<guid>.pdf', so a
    // directory of named PDFs can no longer stand in for the corpus - and because that directory
    // is still on disk, the old check would not have reported itself inconclusive, it would have
    // failed every row. A manifest also carries the run it was generated from, which a directory
    // listing never did.
    //
    // Regenerate from a run's extraction artifact when the corpus changes; if the file is absent
    // (the pipeline agent has the repo, so it is there, but a packaged run may not) the check
    // reports itself skipped rather than passing silently.
    [TestMethod]
    public void ExpectedSources_NameDocumentsThatExistInTheCorpus()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "testdata", "corpus-manifest.txt");

        if (!File.Exists(manifestPath))
        {
            Assert.Inconclusive($"Corpus manifest not found at {manifestPath} - cannot verify ExpectedSources.");
            return;
        }

        // Ids can carry a decomposed diaeresis while the dataset is typed with the precomposed
        // form, exactly as RagEvaluator.ComputeCitationMatch normalizes both sides before
        // comparing. Same normalization here, or this check would fail on every 'cliënten'
        // document while the real scoring passes.
        var inCorpus = File.ReadLines(manifestPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Normalize(System.Text.NormalizationForm.FormC))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(inCorpus.Count > 0, $"{manifestPath} lists no document ids.");

        // EquivalentSources (2026-09-23) is scored by the same comparison, so a typo there is the
        // same silent miss and gets the same lint.
        var unknown = Load()
            .SelectMany(r => SplitIds(r.ExpectedSources).Concat(SplitIds(r.EquivalentSources))
                .Select(s => (r.Name, Source: s)))
            .Where(x => !inCorpus.Contains(x.Source))
            .Select(x => $"{x.Name} -> {x.Source}")
            .ToList();

        Assert.IsTrue(unknown.Count == 0,
            "ExpectedSources / EquivalentSources must match an indexed document id exactly (CitationMatch " +
            "compares document IDs, and a document ID is the blob name). These do not: " +
            Environment.NewLine + string.Join(Environment.NewLine, unknown));
    }

    // An id in both sets would be counted twice on a hit (once as an expected document, once as
    // the equivalent family) and the row could score above 1. Either it is required (expected)
    // or one acceptable alternative among several (equivalent); it cannot be both.
    [TestMethod]
    public void EquivalentSources_DoNotRepeatExpectedSources()
    {
        var overlapping = Load()
            .Select(r => (r.Name, Both: SplitIds(r.ExpectedSources).Intersect(SplitIds(r.EquivalentSources), StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(x => x.Both.Count > 0)
            .Select(x => $"{x.Name} -> {string.Join("; ", x.Both)}")
            .ToList();

        Assert.IsTrue(overlapping.Count == 0,
            "A document id may be in ExpectedSources (required) or EquivalentSources (any-of), not both: " +
            Environment.NewLine + string.Join(Environment.NewLine, overlapping));
    }

    // RelabelledOn is what CompareEvalRuns reads to drop a row from a carried-over comparison;
    // the "Relabelled ..." Trap prefix is what a human reads. Neither may exist without the other,
    // or the exclusion silently stops matching the history the Trap tells.
    [TestMethod]
    public void RelabelledOn_MatchesTheTrapPrefix_AndIsAnIsoDate()
    {
        var mismatched = Load()
            .Where(r => r.Trap.StartsWith("Relabelled", StringComparison.Ordinal) != (r.RelabelledOn.Length > 0))
            .Select(r => $"{r.Name}: Trap starts with 'Relabelled' = {r.Trap.StartsWith("Relabelled", StringComparison.Ordinal)}, RelabelledOn = '{r.RelabelledOn}'")
            .ToList();
        Assert.IsTrue(mismatched.Count == 0,
            "A row is relabelled in both places or in neither: " + Environment.NewLine + string.Join(Environment.NewLine, mismatched));

        var badDates = Load()
            .Where(r => r.RelabelledOn.Length > 0
                        && !DateOnly.TryParseExact(r.RelabelledOn, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _))
            .Select(r => $"{r.Name} -> '{r.RelabelledOn}'")
            .ToList();
        Assert.IsTrue(badDates.Count == 0,
            "RelabelledOn must be yyyy-MM-dd (CompareEvalRuns compares it against run timestamps): " +
            Environment.NewLine + string.Join(Environment.NewLine, badDates));
    }

    private static IEnumerable<string> SplitIds(string sources) =>
        sources.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
               .Select(s => s.Normalize(System.Text.NormalizationForm.FormC));

    [TestMethod]
    public void MultiSourceRows_ClaimMoreThanOneSubQuery()
    {
        AgenticCapability[] multiSource =
        [
            AgenticCapability.Decomposition,
            AgenticCapability.MultiHop,
            AgenticCapability.CrossDocCompare,
            AgenticCapability.DistantSections,
            AgenticCapability.ConflictDetection,
        ];

        var inconsistent = Load()
            .Where(r => multiSource.Contains(r.Capability) && r.MinSubQueries < 2)
            .Select(r => $"{r.Name} ({r.Capability}, MinSubQueries={r.MinSubQueries})")
            .ToList();

        Assert.IsTrue(inconsistent.Count == 0,
            "A row labelled with a multi-source capability but needing only one search would " +
            "count as evidence that planning was unnecessary: " + string.Join(", ", inconsistent));
    }

    [TestMethod]
    public void MinSubQueries_IsAtLeastOne()
    {
        var invalid = Load().Where(r => r.MinSubQueries < 1).Select(r => r.Name).ToList();

        Assert.IsTrue(invalid.Count == 0,
            "MinSubQueries is a lower bound on searches needed; below 1 is meaningless: " +
            string.Join(", ", invalid));
    }

    // Not a correctness rule, a design one: without control rows there is nothing to compare
    // the multi-source rows against, and the whole per-capability report loses its baseline.
    [TestMethod]
    public void Dataset_KeepsSingleLookupControlRows()
    {
        var controls = Load().Count(r => r.Capability == AgenticCapability.SingleLookup);

        Assert.IsTrue(controls >= 3,
            $"Only {controls} SingleLookup control row(s). The per-capability report needs a " +
            "baseline of questions one search can answer, or an improvement in the multi-source " +
            "rows cannot be told apart from a better synthesis model.");
    }
}
