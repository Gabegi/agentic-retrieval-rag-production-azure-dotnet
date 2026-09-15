using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

// The identity-text tripwire, evaluated (2026-09-15). The 80% check itself had existed since B1
// and wrote into the chunking artifact every run; nothing read it, so its crossing on 260909/1
// (7,108 of 8,191) was found by hand. These pin the evaluation, not the data.
[TestClass]
public class FlagEvaluatorIdentityTokenTests
{
    private static IdentityTokenMetrics Identity(int max, params (string Id, int Tokens)[] nearing) => new(
        Limit:             8191,
        WarningThreshold:  6552,
        Max:               max,
        TotalEmbedded:     max,
        TotalThisRun:      max,
        NearingLimitCount: nearing.Length,
        NearingLimit:      nearing.Select(n => new IdentityDocTokens(n.Id, n.Tokens)).ToList());

    private static PdfIndexRunReport Report(IdentityTokenMetrics? identity) => new()
    {
        Run = new RunIdentity(
            InstanceId:   "test-instance",
            StartedAt:    DateTimeOffset.UnixEpoch,
            FinishedAt:   DateTimeOffset.UnixEpoch,
            ForceReindex: true,
            Success:      true,
            ErrorMessage: null),
        Chunking = ChunkingStageMetrics.Empty("TwoAxisChunking") with { IdentityTokens = identity },
    };

    private static IReadOnlyList<ReportFlag> Evaluate(PdfIndexRunReport report, bool calibrationMode = false) =>
        FlagEvaluator.Evaluate(report, fileFacts: null, previous: null, calibrationMode: calibrationMode);

    [TestMethod]
    public void NearingTheLimit_IsAWarningNamingTheDocumentAndItsTokens()
    {
        var flags = Evaluate(Report(Identity(7108, ("Hygienecode.pdf", 7108))));

        var flag = flags.SingleOrDefault(f => f.Metric == "Chunking.IdentityTokens.NearingLimit");
        Assert.IsNotNull(flag, "the tripwire crossed and nothing said so");
        Assert.AreEqual(FlagSeverity.Warning, flag!.Severity);
        StringAssert.Contains(flag.Observed, "Hygienecode.pdf");
        StringAssert.Contains(flag.Observed, "7108");
        StringAssert.Contains(flag.Meaning, "1083", "headroom is the number a reader acts on");
        Assert.IsFalse(flags.Any(f => f.Metric == "Chunking.IdentityTokens.Max"), "under the limit is not over it");
    }

    [TestMethod]
    public void OverTheLimit_IsCritical_AndReplacesTheWarning()
    {
        var flags = Evaluate(Report(Identity(9000, ("Huge.pdf", 9000), ("Big.pdf", 7000))));

        var flag = flags.SingleOrDefault(f => f.Metric == "Chunking.IdentityTokens.Max");
        Assert.IsNotNull(flag);
        Assert.AreEqual(FlagSeverity.Critical, flag!.Severity);
        Assert.AreEqual("9000 tokens", flag.Observed);
        Assert.IsFalse(flags.Any(f => f.Metric == "Chunking.IdentityTokens.NearingLimit"),
            "one condition, one flag - the Critical already covers the list");
    }

    [TestMethod]
    public void UnderTheTripwire_ProducesNoFlag()
    {
        var flags = Evaluate(Report(Identity(6009)));

        Assert.IsFalse(flags.Any(f => f.Metric.StartsWith("Chunking.IdentityTokens")));
    }

    [TestMethod]
    public void NotMeasured_ProducesNoFlag()
    {
        // Reports before 2026-09-15 read back with IdentityTokens = null: no measurement, no flag.
        var flags = Evaluate(Report(identity: null));

        Assert.IsFalse(flags.Any(f => f.Metric.StartsWith("Chunking.IdentityTokens")));
    }

    [TestMethod]
    public void IsNotSuppressedByCalibrationMode()
    {
        // Sourced: the limit is the model's and the 80% line is the code's own constant. There is
        // no rate to tune, so the mode that suppresses guessed thresholds must leave this alone.
        var flags = Evaluate(Report(Identity(7108, ("Hygienecode.pdf", 7108))), calibrationMode: true);

        var flag = flags.SingleOrDefault(f => f.Metric == "Chunking.IdentityTokens.NearingLimit");
        Assert.IsNotNull(flag);
        Assert.IsFalse(flag!.AwaitingCalibration);
    }
}
