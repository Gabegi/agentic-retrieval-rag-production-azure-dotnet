using Azure.Search.Documents.Indexes.Models;
using AgenticRagApp.Infrastructure.Clients.Search;

namespace RagApp.UnitTests.Infrastructure.Search;

// The mapping from a live SearchIndex definition to the run report's VectorConfig (2026-09-15).
// Pins that what lands on the report is what the SERVICE says - parameters present or absent -
// next to what the configuration says, so the run analysis can compare the two.
[TestClass]
public class IndexVectorConfigTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private static SearchIndex LiveIndex(HnswParameters? hnsw, int dimensions = 3072, string field = "content_vector")
    {
        var index = new SearchIndex("live-index");
        index.Fields.Add(new SimpleField("id", SearchFieldDataType.String) { IsKey = true });
        index.Fields.Add(new VectorSearchField(field, dimensions, "vector-profile"));

        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("hnsw-config") { Parameters = hnsw });
        vectorSearch.Profiles.Add(new VectorSearchProfile("vector-profile", "hnsw-config") { VectorizerName = "openai-vectorizer" });
        vectorSearch.Vectorizers.Add(new AzureOpenAIVectorizer("openai-vectorizer")
        {
            Parameters = new AzureOpenAIVectorizerParameters
            {
                ResourceUri    = new Uri("https://openai.example.com"),
                DeploymentName = "embed",
                ModelName      = AzureOpenAIModelName.TextEmbedding3Large,
            },
        });
        index.VectorSearch = vectorSearch;
        return index;
    }

    [TestMethod]
    public void From_ReadsMetricGraphParametersAndVectorizer_AsTheServiceReportsThem()
    {
        var live = LiveIndex(new HnswParameters
        {
            Metric = VectorSearchAlgorithmMetric.Cosine, M = 4, EfConstruction = 400, EfSearch = 500,
        });

        var v = IndexVectorConfig.From(live, "content_vector", 3072, "text-embedding-3-large", ReadAt);

        Assert.IsTrue(v.FieldPresent);
        Assert.AreEqual(3072,        v.Dimensions);
        Assert.AreEqual(3072,        v.ConfiguredDimensions);
        Assert.AreEqual("Hnsw",      v.Algorithm);
        Assert.AreEqual("cosine",    v.Metric);
        Assert.AreEqual(4,           v.M);
        Assert.AreEqual(400,         v.EfConstruction);
        Assert.AreEqual(500,         v.EfSearch);
        Assert.IsNull(v.Compression, "no compression configured reads as null, not as a name");
        Assert.AreEqual("AzureOpenAI",            v.Vectorizer);
        Assert.AreEqual("text-embedding-3-large", v.VectorizerModel);
        Assert.AreEqual("embed",                  v.VectorizerDeployment);
        Assert.AreEqual(ReadAt,                   v.ReadAtUtc);
    }

    [TestMethod]
    public void From_NoHnswParametersOnTheWire_ReportsNulls_NotDefaults()
    {
        // The code creates the index without parameters. Whether the service materializes its
        // defaults on read-back is exactly what the report is meant to tell us, so an absent
        // block must not be filled in here.
        var v = IndexVectorConfig.From(LiveIndex(hnsw: null), "content_vector", 3072, "text-embedding-3-large", ReadAt);

        Assert.IsNull(v.Metric);
        Assert.IsNull(v.M);
        Assert.IsNull(v.EfConstruction);
        Assert.IsNull(v.EfSearch);
    }

    [TestMethod]
    public void From_LiveWidthDiffersFromConfiguration_BothAreCarried()
    {
        var v = IndexVectorConfig.From(LiveIndex(hnsw: null, dimensions: 1536), "content_vector", 3072, "text-embedding-3-large", ReadAt);

        Assert.AreEqual(1536, v.Dimensions);
        Assert.AreEqual(3072, v.ConfiguredDimensions);
    }

    [TestMethod]
    public void From_FieldMissing_IsReportedAsAbsent_WithNoBorrowedWidth()
    {
        var v = IndexVectorConfig.From(LiveIndex(hnsw: null, field: "renamed_vector"), "content_vector", 3072, "text-embedding-3-large", ReadAt);

        Assert.IsFalse(v.FieldPresent);
        Assert.IsNull(v.Dimensions);
        Assert.IsNull(v.Metric, "no field means no profile, so no algorithm to read a metric from");
    }
}
