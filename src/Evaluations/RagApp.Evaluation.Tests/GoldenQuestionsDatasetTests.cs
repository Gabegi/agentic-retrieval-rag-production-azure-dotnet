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

    // The corpus this dataset is written against. Kept as a path rather than a hardcoded list
    // so adding a document to data/ is enough; if the directory is not present (the pipeline
    // agent has the repo, so it is, but a packaged run may not) the check reports itself as
    // skipped rather than passing silently.
    [TestMethod]
    public void ExpectedSources_NameDocumentsThatExistInTheCorpus()
    {
        var corpusDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "data", "chatbot-51pdf-documenten"));

        if (!Directory.Exists(corpusDir))
        {
            Assert.Inconclusive($"Corpus directory not found at {corpusDir} - cannot verify ExpectedSources.");
            return;
        }

        // Filenames on disk can carry a decomposed diaeresis while the dataset is typed with
        // the precomposed form, exactly as RagEvaluator.ComputeCitationMatch normalizes both
        // sides before comparing. Same normalization here, or this check would fail on every
        // 'cliënten' document while the real scoring passes.
        var onDisk = Directory.GetFiles(corpusDir, "*.pdf")
            .Select(f => Path.GetFileName(f).Normalize(System.Text.NormalizationForm.FormC))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = Load()
            .SelectMany(r => r.ExpectedSources
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => (r.Name, Source: s.Normalize(System.Text.NormalizationForm.FormC))))
            .Where(x => !onDisk.Contains(x.Source))
            .Select(x => $"{x.Name} -> {x.Source}")
            .ToList();

        Assert.IsTrue(unknown.Count == 0,
            "ExpectedSources must match a corpus filename exactly (CitationMatch compares " +
            "document IDs, and a document ID is the blob name). These do not: " +
            Environment.NewLine + string.Join(Environment.NewLine, unknown));
    }

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
