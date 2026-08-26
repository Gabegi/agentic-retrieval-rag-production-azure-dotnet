using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure.Search;

// The field contract BETWEEN the two services, which neither one's own tests can see.
//
// IndexService declares the index fields; KnowledgeService names a subset of them by string
// in SearchFields/SourceDataFields. Nothing in the type system connects the two lists, so a
// field removed from one and left in the other compiles and ships. Azure only rejects it at
// run time, and it does so on the FIRST activity of a rebuild:
//
//   Target Index with name '<index>' does not have a retrievable field with name '<field>'
//
// -- 400, run 03437511aa71422eb4b177285fca172d, 2026-08-26
//
// That run died in RecreateIndexActivity with every later stage null: no extraction, no
// chunking, no embedding, no validation. The cost of the drift is a whole failed pipeline
// run, so it belongs in CI, not in a run report.
//
// These tests read the real definitions from both services - no hardcoded field list, which
// would be a third copy to drift.
[TestClass]
public class IndexKnowledgeSourceContractTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "my-index",
        KnowledgeSourceName       = "my-knowledge-source",
        KnowledgeBaseName         = "my-knowledge-base",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
    };

    private static IList<SearchField> IndexFields() =>
        new IndexService(Config(), new Mock<SearchIndexClient>().Object, NullLogger<IndexService>.Instance)
            .BuildDefinition()
            .Fields;

    private static SearchIndexKnowledgeSourceParameters KnowledgeSourceParameters()
    {
        var client = new Mock<SearchIndexClient>();
        SearchIndexKnowledgeSource? captured = null;
        client.Setup(c => c.CreateOrUpdateKnowledgeSourceAsync(
                It.IsAny<SearchIndexKnowledgeSource>(), false, It.IsAny<CancellationToken>()))
            .Callback<KnowledgeSource, bool, CancellationToken>((ks, _, _) => captured = (SearchIndexKnowledgeSource)ks);

        new KnowledgeService(Config(), client.Object, NullLogger<KnowledgeService>.Instance)
            .EnsureKnowledgeSourceAsync().GetAwaiter().GetResult();

        Assert.IsNotNull(captured, "KnowledgeService did not submit a knowledge source.");
        return (SearchIndexKnowledgeSourceParameters)captured!.SearchIndexParameters;
    }

    // The exact condition Azure enforces, and the exact wording of the 400 it returns:
    // every SourceDataFields entry must exist AND be retrievable. IsHidden is what makes a
    // field non-retrievable, so content_vector (IsHidden = true) can never be listed here -
    // which is also why the "excluded, not needed for LLM context" note in KnowledgeService
    // is a hard requirement rather than a preference.
    [TestMethod]
    public void SourceDataFields_AllExistOnTheIndexAndAreRetrievable()
    {
        var byName = IndexFields().ToDictionary(f => f.Name);

        foreach (var reference in KnowledgeSourceParameters().SourceDataFields)
        {
            Assert.IsTrue(byName.TryGetValue(reference.Name, out var field),
                $"Knowledge source lists source-data field '{reference.Name}', which IndexService " +
                 "does not declare. Azure fails the rebuild with \"does not have a retrievable field " +
                $"with name '{reference.Name}'\".");

            Assert.IsTrue(field!.IsHidden != true,
                $"Source-data field '{reference.Name}' is hidden on the index, so it is not " +
                 "retrievable and Azure rejects the knowledge source.");
        }
    }

    // SearchFields drive BM25, so existing is not enough - a SimpleField named here is not
    // searchable and cannot be scored against.
    [TestMethod]
    public void SearchFields_AllExistOnTheIndexAndAreSearchable()
    {
        var byName = IndexFields().ToDictionary(f => f.Name);

        foreach (var reference in KnowledgeSourceParameters().SearchFields)
        {
            Assert.IsTrue(byName.TryGetValue(reference.Name, out var field),
                $"Knowledge source lists search field '{reference.Name}', which IndexService does not declare.");

            Assert.IsTrue(field!.IsSearchable == true,
                $"Search field '{reference.Name}' is not searchable on the index, so BM25 cannot score it.");
        }
    }

    // Guards the drift direction the two tests above cannot see: a field retired from the
    // index schema while its name is left behind in the knowledge source. Both lists come
    // from the live definitions, so a rename in either file surfaces here.
    [TestMethod]
    public void KnowledgeSource_ReferencesNoFieldNameAbsentFromTheIndex()
    {
        var indexNames  = IndexFields().Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var parameters  = KnowledgeSourceParameters();
        var referenced  = parameters.SearchFields.Select(f => f.Name)
            .Concat(parameters.SourceDataFields.Select(f => f.Name))
            .ToHashSet(StringComparer.Ordinal);

        var unknown = referenced.Except(indexNames).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.AreEqual(0, unknown.Count,
            $"Knowledge source references field(s) the index does not declare: {string.Join(", ", unknown)}.");
    }
}
