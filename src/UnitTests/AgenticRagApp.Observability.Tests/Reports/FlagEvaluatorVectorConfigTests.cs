using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

// Live index definition vs configuration (2026-09-15). EnsureIndexAsync is get-or-create, so
// these three disagreements produce wrong results with no error anywhere else. These pin the
// evaluation, not the data.
[TestClass]
public class FlagEvaluatorVectorConfigTests
{
    private static IndexVectorConfig Live(
        int? dimensions = 3072, int configured = 3072, string? metric = "cosine",
        string? vectorizerModel = "text-embedding-3-large", string configuredModel = "text-embedding-3-large",
        bool fieldPresent = true) => new(
        IndexName: "idx", FieldName: "content_vector", FieldPresent: fieldPresent,
        Dimensions: dimensions, ConfiguredDimensions: configured,
        Algorithm: "Hnsw", Metric: metric, M: 4, EfConstruction: 400, EfSearch: 500, Compression: null,
        Vectorizer: "AzureOpenAI", VectorizerModel: vectorizerModel, VectorizerDeployment: "embed",
        ConfiguredModelName: configuredModel, ReadAtUtc: DateTimeOffset.UnixEpoch);

    private static PdfIndexRunReport Report(IndexVectorConfig? config) => new()
    {
        Run = new RunIdentity("test-instance", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, true, true, null),
        VectorConfig = config,
    };

    private static IReadOnlyList<ReportFlag> Evaluate(PdfIndexRunReport report, bool calibrationMode = false) =>
        FlagEvaluator.Evaluate(report, fileFacts: null, previous: null, calibrationMode: calibrationMode);

    [TestMethod]
    public void EverythingAgrees_ProducesNoIndexFlags()
    {
        Assert.IsFalse(Evaluate(Report(Live())).Any(f => f.Metric.StartsWith("Index.")));
    }

    // Warning, not Critical, since D201 - and the severity is the assertion, not a detail.
    //
    // While vectors were validated against OPENAI_EMBEDDING_DIMENSIONS, a config that disagreed
    // with the index meant the next upload was already doomed, so Critical was right. Validation
    // now happens against the live index width read at preflight, so this run is fine; what the
    // flag reports is that the NEXT index creation would build a different index. A latent
    // footgun, not a broken run. Raising it back to Critical would make a healthy run read as
    // failing.
    [TestMethod]
    public void LiveWidthDiffersFromConfiguration_IsWarning_BecauseTheRunItselfIsUnaffected()
    {
        var flags = Evaluate(Report(Live(dimensions: 1536, configured: 3072)));

        var flag = flags.SingleOrDefault(f => f.Metric == "Index.VectorDimensions");
        Assert.IsNotNull(flag, "a config that would provision a different index has to be named somewhere");
        Assert.AreEqual(FlagSeverity.Warning, flag!.Severity);
        StringAssert.Contains(flag.Observed, "1536");
        StringAssert.Contains(flag.Expected, "3072");

        // The remediation must not claim uploads are failing, and must name the recreate as the
        // moment the disagreement becomes real.
        StringAssert.Contains(flag.Action, "recreate");
    }

    [TestMethod]
    public void VectorizerModelDiffersFromDocumentModel_IsCritical()
    {
        var flags = Evaluate(Report(Live(vectorizerModel: "text-embedding-ada-002")));

        var flag = flags.SingleOrDefault(f => f.Metric == "Index.VectorizerModel");
        Assert.IsNotNull(flag);
        Assert.AreEqual(FlagSeverity.Critical, flag!.Severity);
    }

    [TestMethod]
    public void MetricOtherThanCosine_IsAWarning()
    {
        var flags = Evaluate(Report(Live(metric: "euclidean")));

        var flag = flags.SingleOrDefault(f => f.Metric == "Index.VectorMetric");
        Assert.IsNotNull(flag);
        Assert.AreEqual(FlagSeverity.Warning, flag!.Severity);
        Assert.AreEqual("cosine", flag.Expected);
    }

    [TestMethod]
    public void MetricNotReported_ProducesNoMetricFlag()
    {
        // The service returned no hnswParameters block: not measured, not a disagreement.
        Assert.IsFalse(Evaluate(Report(Live(metric: null))).Any(f => f.Metric == "Index.VectorMetric"));
    }

    [TestMethod]
    public void FieldAbsent_IsCritical_AndSuppressesTheComparisons()
    {
        var flags = Evaluate(Report(Live(fieldPresent: false, dimensions: null, metric: null)));

        Assert.IsTrue(flags.Any(f => f.Metric == "Index.VectorField" && f.Severity == FlagSeverity.Critical));
        Assert.IsFalse(flags.Any(f => f.Metric == "Index.VectorDimensions"));
    }

    [TestMethod]
    public void NoReadback_ProducesNoIndexFlags()
    {
        Assert.IsFalse(Evaluate(Report(config: null)).Any(f => f.Metric.StartsWith("Index.")));
    }

    [TestMethod]
    public void IsNotSuppressedByCalibrationMode()
    {
        var flags = Evaluate(Report(Live(dimensions: 1536)), calibrationMode: true);

        var flag = flags.SingleOrDefault(f => f.Metric == "Index.VectorDimensions");
        Assert.IsNotNull(flag);
        Assert.IsFalse(flag!.AwaitingCalibration, "an equality has nothing to calibrate");
    }
}
