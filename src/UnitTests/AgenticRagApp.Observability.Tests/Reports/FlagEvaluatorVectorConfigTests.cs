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

    [TestMethod]
    public void LiveWidthDiffersFromConfiguration_IsCritical()
    {
        var flags = Evaluate(Report(Live(dimensions: 1536, configured: 3072)));

        var flag = flags.SingleOrDefault(f => f.Metric == "Index.VectorDimensions");
        Assert.IsNotNull(flag, "a config change without a re-index has to be named here - VectorDimErrors cannot see it");
        Assert.AreEqual(FlagSeverity.Critical, flag!.Severity);
        StringAssert.Contains(flag.Observed, "1536");
        StringAssert.Contains(flag.Expected, "3072");
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
