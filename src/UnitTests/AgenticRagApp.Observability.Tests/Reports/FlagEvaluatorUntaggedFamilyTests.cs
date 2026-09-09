using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

// The rule that was missing (2026-08-27). Per-document DomainTag had been in the chunking
// artifact all along, so when 2 of 3 CAO documents lost their sector tag under Content
// Understanding the evidence was already in the report - and it still took a manual review,
// prompted by an eval regression, to see it. These tests pin the evaluation, not the data.
[TestClass]
public class FlagEvaluatorUntaggedFamilyTests
{
    private static PdfIndexRunReport ReportWith(IReadOnlyList<string> untagged) => new()
    {
        Run = new RunIdentity(
            InstanceId:   "test-instance",
            StartedAt:    DateTimeOffset.UnixEpoch,
            FinishedAt:   DateTimeOffset.UnixEpoch,
            ForceReindex: true,
            Success:      true,
            ErrorMessage: null),
        Chunking = ChunkingStageMetrics.Empty("TwoAxisChunking") with
        {
            UntaggedFamilyMemberIds = untagged,
        },
    };

    private static IReadOnlyList<ReportFlag> Evaluate(PdfIndexRunReport report) =>
        FlagEvaluator.Evaluate(report, fileFacts: null, previous: null, calibrationMode: false);

    [TestMethod]
    public void RaisesAWarningNamingTheDocuments()
    {
        var flags = Evaluate(ReportWith(["CAO GHZ (Versie 4).pdf", "CAO VVT (Versie 6).pdf"]));

        var flag = flags.SingleOrDefault(f => f.Metric == "Chunking.UntaggedFamilyMemberIds");
        Assert.IsNotNull(flag, "the untagged-family rule did not fire");
        Assert.AreEqual(FlagSeverity.Warning, flag!.Severity);
        // Naming them is the point - FlagEvaluator's own actionability rule. A count alone is a
        // metrics row, not something a reader can act on.
        StringAssert.Contains(flag.Observed, "CAO GHZ (Versie 4).pdf");
        StringAssert.Contains(flag.Observed, "CAO VVT (Versie 6).pdf");
    }

    [TestMethod]
    public void StaysSilentWhenEveryFamilyMemberIsTagged()
    {
        var flags = Evaluate(ReportWith([]));

        Assert.IsFalse(flags.Any(f => f.Metric == "Chunking.UntaggedFamilyMemberIds"));
    }

    [TestMethod]
    public void IsNotSuppressedByCalibrationMode()
    {
        // Sourced, not awaiting calibration: there is no rate to tune. One untagged member of a
        // near-duplicate family already means that family cannot be disambiguated by sector, so
        // this must survive the mode that suppresses guessed thresholds.
        var report = ReportWith(["CAO GHZ (Versie 4).pdf"]);

        var flags = FlagEvaluator.Evaluate(
            report, fileFacts: null, previous: null, calibrationMode: true);

        var flag = flags.SingleOrDefault(f => f.Metric == "Chunking.UntaggedFamilyMemberIds");
        Assert.IsNotNull(flag);
        Assert.IsFalse(flag!.AwaitingCalibration);
    }

    [TestMethod]
    public void ANullChunkingStageProducesNoFlag()
    {
        // "A null stage produces no flags" - absence of measurement is not a passing measurement.
        var report = ReportWith([]) with { Chunking = null };

        var flags = Evaluate(report);

        Assert.IsFalse(flags.Any(f => f.Metric == "Chunking.UntaggedFamilyMemberIds"));
    }
}
