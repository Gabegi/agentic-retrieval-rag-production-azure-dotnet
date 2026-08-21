using Microsoft.Extensions.Logging.Abstractions;
using AgenticRagApp.Infrastructure.Clients.DocumentIdentity;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Indexing;

// Direct tests for the write gate. IsUnchanged is private, and stays private - it is exercised
// here through PersistAsync, its only caller, which is also where its effect is observable: a
// record that compares equal must produce no store write at all.
//
// The regression behind it: this step used to write every document unconditionally, so a run in
// which nothing changed still paid one blob write per document (chunking-done.md §17 item 14).
[TestClass]
public class IdentityStoreWriterTests
{
    private const string ModelId = "text-embedding-3-large@3";

    // Records what was written rather than mocking expectations, so a test can assert on the
    // record's contents and not only on the call count.
    private sealed class RecordingStore : IDocumentIdentityStore
    {
        public List<DocumentIdentityRecord> Written { get; } = [];

        public Task SetAsync(DocumentIdentityRecord record, CancellationToken ct = default)
        {
            Written.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DocumentIdentityRecord>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DocumentIdentityRecord>>(Written);

        public Task<int> EvictOrphanedAsync(IReadOnlySet<string> liveDocumentIds, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private static DocumentIdentity Identity(
        string sourceId, string title = "CAO GGZ", string? tag = "ggz", string? hash = null) =>
        new(SourceId: sourceId, Title: title, DomainTag: tag,
            IdentityText: title, Hash: hash ?? $"hash-{sourceId}", IdentityTokens: 4);

    private static DocumentIdentityRecord Stored(
        string  sourceId,
        string  title    = "CAO GGZ",
        string? tag      = "ggz",
        string  familyId = "fam-1",
        string? hash     = null,
        string? modelId  = ModelId,
        float[]? vector  = null) =>
        new(SourceId:         sourceId,
            Title:            title,
            DomainTag:        tag,
            Vector:           vector ?? [0.1f, 0.2f, 0.3f],
            FamilyId:         familyId,
            IdentityTextHash: hash ?? $"hash-{sourceId}",
            EmbeddingModelId: modelId);

    private static Task<PersistOutcome> Persist(
        RecordingStore store,
        IReadOnlyList<DocumentIdentity> thisRun,
        IReadOnlyDictionary<string, DocumentIdentityRecord>? persisted = null,
        IReadOnlyDictionary<string, string>? familyIdOf = null,
        IReadOnlyDictionary<string, float[]>? freshVectors = null,
        IReadOnlyDictionary<string, WorkingDoc>? working = null) =>
        IdentityStoreWriter.PersistAsync(
            store,
            NullLogger.Instance,
            thisRun,
            working ?? thisRun.ToDictionary(d => d.SourceId, d => new WorkingDoc(d.Title, [0.1f, 0.2f, 0.3f])),
            persisted    ?? new Dictionary<string, DocumentIdentityRecord>(),
            familyIdOf   ?? thisRun.ToDictionary(d => d.SourceId, _ => "fam-1"),
            freshVectors ?? new Dictionary<string, float[]>(),
            ModelId,
            CancellationToken.None);

    // ── The six-field comparison ─────────────────────────────────────────────

    [TestMethod]
    public async Task ARecordIdenticalInAllSixFields_IsNotWritten()
    {
        var store = new RecordingStore();
        var run   = new[] { Identity("a.pdf") };

        var outcome = await Persist(store, run,
            persisted: new Dictionary<string, DocumentIdentityRecord> { ["a.pdf"] = Stored("a.pdf") });

        Assert.AreEqual(0, store.Written.Count, "nothing moved, so nothing is written");
        Assert.AreEqual(0, outcome.RecordsWritten);
        Assert.AreEqual(1, outcome.RecordsUnchanged);
    }

    [TestMethod]
    public async Task AChangedFamilyId_IsWrittenEvenThoughTheIdentityHashDidNot_Change()
    {
        // The case the hash cannot see: a document's own identity text is untouched, and its
        // family changed because ANOTHER document joined its cluster.
        var store = new RecordingStore();
        var run   = new[] { Identity("a.pdf") };

        var outcome = await Persist(store, run,
            persisted:  new Dictionary<string, DocumentIdentityRecord> { ["a.pdf"] = Stored("a.pdf", familyId: "fam-OLD") },
            familyIdOf: new Dictionary<string, string> { ["a.pdf"] = "fam-NEW" });

        Assert.AreEqual(1, outcome.RecordsWritten);
        Assert.AreEqual("fam-NEW", store.Written.Single().FamilyId);
    }

    [TestMethod]
    public async Task AChangedTitle_IsWritten()
    {
        var store = new RecordingStore();

        var outcome = await Persist(store, [Identity("a.pdf", title: "CAO GGZ 2026")],
            persisted: new Dictionary<string, DocumentIdentityRecord> { ["a.pdf"] = Stored("a.pdf", title: "CAO GGZ 2025") });

        Assert.AreEqual(1, outcome.RecordsWritten);
        Assert.AreEqual("CAO GGZ 2026", store.Written.Single().Title);
    }

    [TestMethod]
    public async Task AChangedDomainTag_IsWritten()
    {
        var store = new RecordingStore();

        var outcome = await Persist(store, [Identity("a.pdf", tag: "vvt")],
            persisted: new Dictionary<string, DocumentIdentityRecord> { ["a.pdf"] = Stored("a.pdf", tag: "ggz") });

        Assert.AreEqual(1, outcome.RecordsWritten);
        Assert.AreEqual("vvt", store.Written.Single().DomainTag);
    }

    [TestMethod]
    public async Task AChangedIdentityHash_IsWritten()
    {
        var store = new RecordingStore();

        var outcome = await Persist(store, [Identity("a.pdf", hash: "hash-NEW")],
            persisted: new Dictionary<string, DocumentIdentityRecord> { ["a.pdf"] = Stored("a.pdf", hash: "hash-OLD") });

        Assert.AreEqual(1, outcome.RecordsWritten);
    }

    [TestMethod]
    public async Task AStoredRecordFromAnotherModel_IsWritten()
    {
        var store = new RecordingStore();

        var outcome = await Persist(store, [Identity("a.pdf")],
            persisted: new Dictionary<string, DocumentIdentityRecord> { ["a.pdf"] = Stored("a.pdf", modelId: "other@1") });

        Assert.AreEqual(1, outcome.RecordsWritten);
        Assert.AreEqual(ModelId, store.Written.Single().EmbeddingModelId);
    }

    [TestMethod]
    public async Task AFreshlyEmbeddedDocument_IsAlwaysWritten()
    {
        // Embedding it was the point; the vector is a different array and has to be stored, even
        // when every other field compares equal.
        var store = new RecordingStore();

        var outcome = await Persist(store, [Identity("a.pdf")],
            persisted:    new Dictionary<string, DocumentIdentityRecord> { ["a.pdf"] = Stored("a.pdf") },
            freshVectors: new Dictionary<string, float[]> { ["a.pdf"] = [0.1f, 0.2f, 0.3f] });

        Assert.AreEqual(1, outcome.RecordsWritten);
        Assert.AreEqual(0, outcome.RecordsUnchanged);
    }

    [TestMethod]
    public async Task ADocumentWithNoStoredRecord_IsWritten()
    {
        var store = new RecordingStore();

        var outcome = await Persist(store, [Identity("new.pdf")]);

        Assert.AreEqual(1, outcome.RecordsWritten);
        Assert.AreEqual("new.pdf", store.Written.Single().SourceId);
    }

    // ── Skips and older documents ────────────────────────────────────────────

    [TestMethod]
    public async Task ADocumentMissingFromTheWorkingSetOrTheFamilyMap_IsSkippedEntirely()
    {
        // Both are the resolver's own upstream exclusions; reaching the store without them would
        // persist a record with no family id at all.
        var store = new RecordingStore();

        var noWorking = await Persist(store, [Identity("a.pdf")],
            working: new Dictionary<string, WorkingDoc>());
        Assert.AreEqual(0, noWorking.RecordsWritten);
        Assert.AreEqual(0, noWorking.RecordsUnchanged, "skipped is not the same as unchanged");

        var noFamily = await Persist(store, [Identity("a.pdf")],
            familyIdOf: new Dictionary<string, string>());
        Assert.AreEqual(0, noFamily.RecordsWritten);
        Assert.AreEqual(0, store.Written.Count);
    }

    [TestMethod]
    public async Task AnOlderDocumentReHomedByThisRun_IsWrittenAndReportedAsAMove()
    {
        // The document itself was not processed this run; another document's arrival merged its
        // cluster. Nothing else in the pipeline can notice this, which is why it is reported.
        var store = new RecordingStore();

        var outcome = await Persist(store, [Identity("new.pdf")],
            persisted:  new Dictionary<string, DocumentIdentityRecord> { ["old.pdf"] = Stored("old.pdf", familyId: "fam-OLD") },
            familyIdOf: new Dictionary<string, string> { ["new.pdf"] = "fam-NEW", ["old.pdf"] = "fam-NEW" });

        var move = outcome.Moves.Single();
        Assert.AreEqual("old.pdf",  move.SourceId);
        Assert.AreEqual("fam-OLD",  move.FromFamilyId);
        Assert.AreEqual("fam-NEW",  move.ToFamilyId);
        Assert.AreEqual(2, outcome.RecordsWritten, "the new document and the re-homed one");
    }

    [TestMethod]
    public async Task AnOlderDocumentWhoseFamilyIsUnchanged_IsNotTouched()
    {
        var store = new RecordingStore();

        var outcome = await Persist(store, [Identity("new.pdf")],
            persisted:  new Dictionary<string, DocumentIdentityRecord> { ["old.pdf"] = Stored("old.pdf", familyId: "fam-1") },
            familyIdOf: new Dictionary<string, string> { ["new.pdf"] = "fam-2", ["old.pdf"] = "fam-1" });

        Assert.AreEqual(0, outcome.Moves.Count);
        Assert.AreEqual(1, outcome.RecordsWritten, "only the document this run actually processed");
    }

    [TestMethod]
    public async Task ADocumentInThisRun_IsNeverAlsoCountedAsAMove()
    {
        // The two loops must not double-write: the second one skips anything already in the run.
        var store = new RecordingStore();

        var outcome = await Persist(store, [Identity("a.pdf")],
            persisted:  new Dictionary<string, DocumentIdentityRecord> { ["a.pdf"] = Stored("a.pdf", familyId: "fam-OLD") },
            familyIdOf: new Dictionary<string, string> { ["a.pdf"] = "fam-NEW" });

        Assert.AreEqual(0, outcome.Moves.Count, "a run document's change is a write, not a move");
        Assert.AreEqual(1, outcome.RecordsWritten);
        Assert.AreEqual(1, store.Written.Count);
    }
}
