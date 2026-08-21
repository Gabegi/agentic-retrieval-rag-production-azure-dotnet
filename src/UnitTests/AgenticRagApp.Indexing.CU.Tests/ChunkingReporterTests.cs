using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;
using AgenticRagApp.Indexing.CU.Utils;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Indexing;

// Step 5 on its own: ChunkingRunState accumulates, ChunkingReporter turns it into the report.
//
// Tested here rather than only through ChunkingService because the cases worth covering are the
// ones the service cannot stage - a document that throws mid-chunk, a run that dies before the
// loop reaches half its corpus - and because these two classes are where the report's contract
// now lives. ChunkingServiceTests still covers the end-to-end shape.
[TestClass]
public class ChunkingReporterTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────

    private static (ChunkingReporter Reporter, List<ChunkingRunReport> Reports) BuildReporter()
    {
        var reports = new List<ChunkingRunReport>();
        var writer  = new Mock<IPipelineArtifactWriter>();

        writer
            .Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<ChunkingRunReport>(), It.IsAny<CancellationToken>()))
            .Callback<string, ChunkingRunReport, CancellationToken>((_, r, _) => reports.Add(r))
            .Returns(Task.CompletedTask);

        return (new ChunkingReporter(writer.Object, NullLogger<ChunkingReporter>.Instance), reports);
    }

    private static PdfExtractionDocument Doc(
        string sourceId,
        string content = "body",
        string title   = "CAO GGZ",
        IReadOnlyList<Heading>?   headings = null,
        IReadOnlyList<TableInfo>? tables   = null,
        DocumentProfile?          profile  = null) =>
        new(SourceId:         sourceId,
            Content:          content,
            PageSpans:        [new PageSpan(1, 0, content.Length, null, IsPictureOnly: false)],
            Title:            title,
            Author:           null,
            CreatedAt:        null,
            ModDate:          null,
            PageCount:        null,
            LastModifiedDate: null,
            ZenyaDocumentId:  null,
            ZenyaVersion:     null,
            ZenyaStatus:      null,
            ZenyaUrl:         null,
            Bookmarks:        [],
            PageBreadcrumbs:  new Dictionary<int, string>(),
            Sections:         [],
            Headings:         headings ?? [],
            Boilerplate:      [],
            Tables:           tables ?? [],
            SelectionMarks:   [],
            Figures:          [],
            Lines:            [],
            Profile:          profile,
            Language:         null);

    private static ChunkObject Chunk(string documentId, int sectionIndex, int childIndex, int tokens) =>
        new()
        {
            Content      = new string('a', tokens),
            SectionIndex = sectionIndex,
            ChildIndex   = childIndex,
            Metadata     = new ChunkMetadata
            {
                Id         = $"{documentId}::s{sectionIndex}::{childIndex}",
                DocumentId = documentId,
                TokenCount = tokens,
            },
        };

    private static (ChunkingRunState State, List<ChunkObject> Chunks) StateFor(
        params PdfExtractionDocument[] docs)
    {
        var chunks = new List<ChunkObject>();
        return (new ChunkingRunState(docs, chunks, "instance-1", DateTimeOffset.UtcNow), chunks);
    }

    // ── rows ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EveryInputDocument_GetsExactlyOneRow_OrderedBySourceId()
    {
        var (reporter, reports) = BuildReporter();
        var (state, chunks)     = StateFor(Doc("docB"), Doc("docA"));

        var keptB = new[] { Chunk("docB", 0, 0, 120) };
        chunks.AddRange(keptB);
        state.Chunked(Doc("docB"), keptB, keptB.Length, "Recursive");

        var keptA = new[] { Chunk("docA", 0, 0, 90) };
        chunks.AddRange(keptA);
        state.Chunked(Doc("docA"), keptA, keptA.Length, "Recursive");

        state.Stage = null;
        await reporter.WriteAsync(state, CancellationToken.None);

        var report = reports.Single();
        CollectionAssert.AreEqual(new[] { "docA", "docB" }, report.Documents.Select(d => d.SourceId).ToList());
        Assert.IsTrue(report.Documents.All(d => d.Outcome == "chunked"));
        Assert.IsTrue(report.Success);
    }

    [TestMethod]
    public async Task ChunkedRow_CarriesTheCutStatsReadOffItsOwnChunks()
    {
        var (reporter, reports) = BuildReporter();
        var doc                 = Doc("doc1", headings: [new Heading("Inleiding", "sectionHeading", 0, 1)]);
        var (state, chunks)     = StateFor(doc);

        // Two sections, one of them over the ceiling - the row has to say both.
        var kept = new[]
        {
            Chunk("doc1", 0, 0, 40),
            Chunk("doc1", 1, 0, 600),
        };
        chunks.AddRange(kept);

        // Step 2b runs before the strategy on the real route-1 path, so a fixture that skips it
        // describes a document that located none of its headings - which is now its own outcome.
        state.HeadingsLocatedFor(doc, new HeadingLocationResult(
            Sections: [], HeadingsTotal: 1, HeadingsLocated: 1,
            PairedHeadingsMerged: 0, HeadingsWithoutOffset: 0));
        state.Chunked(doc, kept, kept.Length, "DeclaredBoundary");

        await reporter.WriteAsync(state, CancellationToken.None);

        var row = reports.Single().Documents.Single();
        Assert.AreEqual("chunked", row.Outcome);
        Assert.AreEqual(2, row.ChunkCount);
        Assert.AreEqual(2, row.SectionCount);
        Assert.AreEqual("DeclaredBoundary", row.Strategy);
        Assert.AreEqual(1, row.ChunksAboveCeiling, "600 tokens is over the 512 ceiling");
        Assert.AreEqual(1, row.ShortChunks, "40 tokens is under the 50-token short-chunk line");
        Assert.AreEqual(1, row.HeadingCount, "the headings the document declared, whatever the route did");
        Assert.IsFalse(row.EmptyTitle);
    }

    // The routing gate counts declared headings BEFORE location runs, so a document can take
    // route 1 on two headings, locate neither, and be chunked as one unnamed section. Its chunks
    // are real, so it is not zero_chunks; its boundaries are not declared ones, so it is not
    // "chunked" either.
    [TestMethod]
    public async Task Route1DocumentThatLocatedNoHeadings_IsReportedAsChunkedUnanchored()
    {
        var (reporter, reports) = BuildReporter();
        var doc                 = Doc("unanchored", headings:
        [
            new Heading("Artikel 1", "sectionHeading", 0, 1),
            new Heading("Artikel 2", "sectionHeading", 100, 1),
        ]);
        var (state, chunks)     = StateFor(doc);

        state.HeadingsLocatedFor(doc, new HeadingLocationResult(
            Sections: [], HeadingsTotal: 2, HeadingsLocated: 0,
            PairedHeadingsMerged: 0, HeadingsWithoutOffset: 0));

        var kept = new[] { Chunk("unanchored", 0, 0, 300) };
        chunks.AddRange(kept);
        state.Chunked(doc, kept, kept.Length, "DeclaredBoundary");

        await reporter.WriteAsync(state, CancellationToken.None);

        var row = reports.Single().Documents.Single();
        Assert.AreEqual("chunked_unanchored", row.Outcome);
        Assert.AreEqual(1, row.ChunkCount, "the chunks are kept - this is not a failure row");
        Assert.AreEqual(2, row.HeadingCount);
        Assert.AreEqual(0, row.HeadingsLocated);
        StringAssert.Contains(row.Reason!, "located none of them");
    }

    // The same shape on route 2 is not unanchored: the recursive route never attempts to
    // anchor, so zero located headings is what it is supposed to report.
    [TestMethod]
    public async Task Route2DocumentWithNoLocatedHeadings_IsStillReportedAsChunked()
    {
        var (reporter, reports) = BuildReporter();
        var doc                 = Doc("flat");
        var (state, chunks)     = StateFor(doc);

        var kept = new[] { Chunk("flat", 0, 0, 300) };
        chunks.AddRange(kept);
        state.Chunked(doc, kept, kept.Length, "Recursive");

        await reporter.WriteAsync(state, CancellationToken.None);

        var row = reports.Single().Documents.Single();
        Assert.AreEqual("chunked", row.Outcome);
        Assert.IsNull(row.Reason);
    }

    [TestMethod]
    public async Task EveryCutDroppedAsResidue_ReportsZeroChunks_WithTheCountAndTheReason()
    {
        var (reporter, reports) = BuildReporter();
        var doc                 = Doc("residue");
        var (state, _)          = StateFor(doc);

        // Three cuts in, none kept: the minimum-content rule took all of them.
        state.Chunked(doc, [], cutCount: 3, route: "Recursive");

        await reporter.WriteAsync(state, CancellationToken.None);

        var row = reports.Single().Documents.Single();
        Assert.AreEqual("zero_chunks", row.Outcome);
        Assert.AreEqual(0, row.ChunkCount);
        Assert.AreEqual(3, row.ResidueChunksDropped);
        StringAssert.Contains(row.Reason, "minimum-content rule");
    }

    [TestMethod]
    public async Task RouteProducedNoCuts_IsADifferentReasonFromResidue()
    {
        var (reporter, reports) = BuildReporter();
        var doc                 = Doc("empty", content: "");
        var (state, _)          = StateFor(doc);

        state.Chunked(doc, [], cutCount: 0, route: "Recursive");

        await reporter.WriteAsync(state, CancellationToken.None);

        var row = reports.Single().Documents.Single();
        Assert.AreEqual("zero_chunks", row.Outcome);
        Assert.AreEqual(0, row.ResidueChunksDropped);
        StringAssert.Contains(row.Reason, "no cuts");
    }

    [TestMethod]
    public async Task EmptyTitleAndTableShape_AreReportedSignals()
    {
        var (reporter, reports) = BuildReporter();

        // Table-dominant by the fallback branch: no profile, so the table COUNT decides.
        var tables = Enumerable.Range(0, 3)
            .Select(_ => new TableInfo(1, 1, [], 0, 1, null, [], []))
            .ToList();

        var doc        = Doc("untitled", title: "", tables: tables);
        var (state, _) = StateFor(doc);

        state.Chunked(doc, [Chunk("untitled", 0, 0, 100)], cutCount: 1, route: "Recursive");

        await reporter.WriteAsync(state, CancellationToken.None);

        var row = reports.Single().Documents.Single();
        Assert.IsTrue(row.EmptyTitle, "an empty title on route 2 means chunks embedded with no identity at all");
        Assert.IsTrue(row.IsTableShaped);
    }

    // ── failure ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AFailedDocument_GetsItsOwnRow_AndTheStageFailsOnceAtTheEnd()
    {
        var (reporter, reports) = BuildReporter();
        var good                = Doc("good");
        var bad                 = Doc("bad");
        var (state, chunks)     = StateFor(good, bad);

        var kept = new[] { Chunk("good", 0, 0, 100) };
        chunks.AddRange(kept);
        state.Chunked(good, kept, kept.Length, "Recursive");
        state.DocumentFailed(bad, new InvalidOperationException("the splitter fell over"));

        // The stage still fails - but only after every other document was chunked and reported.
        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => state.ThrowIfAnyDocumentFailed());
        StringAssert.Contains(thrown.Message, "bad");

        state.Threw(thrown);
        await reporter.WriteAsync(state, CancellationToken.None);

        var report = reports.Single();
        Assert.IsFalse(report.Success);
        Assert.AreEqual("chunking", report.FailedAtStage);

        var badRow = report.Documents.Single(d => d.SourceId == "bad");
        Assert.AreEqual("failed", badRow.Outcome);
        StringAssert.Contains(badRow.Reason, "the splitter fell over");

        var goodRow = report.Documents.Single(d => d.SourceId == "good");
        Assert.AreEqual("chunked", goodRow.Outcome);
        Assert.AreEqual(1, goodRow.ChunkCount);
    }

    [TestMethod]
    public async Task DocumentsTheStageNeverReached_AreReportedAsNotReached_NotAsMissing()
    {
        var (reporter, reports) = BuildReporter();
        var (state, _)          = StateFor(Doc("first"), Doc("second"));

        // Died in identity resolution, so neither document was ever processed.
        state.Threw(new InvalidOperationException("expected 3 dimensions"));

        await reporter.WriteAsync(state, CancellationToken.None);

        var report = reports.Single();
        Assert.IsFalse(report.Success);
        Assert.AreEqual("identity-resolution", report.FailedAtStage);
        Assert.IsTrue(report.Documents.All(d => d.Outcome == "not_reached"));
        Assert.IsTrue(report.Documents.All(d => d.Reason!.Contains("identity-resolution")));
    }

    [TestMethod]
    public async Task AWriteFailure_IsSwallowed_SoItCannotMaskTheStagesOwnOutcome()
    {
        var writer = new Mock<IPipelineArtifactWriter>();
        writer
            .Setup(w => w.WriteArtifactAsync(It.IsAny<string>(), It.IsAny<ChunkingRunReport>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("blob storage is down"));

        var reporter   = new ChunkingReporter(writer.Object, NullLogger<ChunkingReporter>.Instance);
        var (state, _) = StateFor(Doc("doc1"));

        await reporter.WriteAsync(state, CancellationToken.None);
    }

    // ── heading location ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task HeadingLocation_FlagsEscalation_OnlyAboveTheTwoPercentThreshold()
    {
        var (reporter, reports) = BuildReporter();
        var doc                 = Doc("doc1");
        var (state, _)          = StateFor(doc);

        // 97 of 100 located is a 3% failure rate, over the >2% line fixed in advance.
        state.HeadingsLocatedFor(doc, new HeadingLocationResult(
            Sections: [], HeadingsTotal: 100, HeadingsLocated: 97,
            PairedHeadingsMerged: 2, HeadingsWithoutOffset: 0));
        state.Chunked(doc, [Chunk("doc1", 0, 0, 100)], cutCount: 1, route: "DeclaredBoundary");

        await reporter.WriteAsync(state, CancellationToken.None);

        var summary = reports.Single().HeadingLocation!;
        Assert.AreEqual(100, summary.HeadingsTotal);
        Assert.AreEqual(97, summary.HeadingsLocated);
        Assert.IsTrue(summary.ExceedsEscalationThreshold);
        Assert.AreEqual(2, summary.PairedZeroBodyHeadingsMerged);

        var row = reports.Single().Documents.Single();
        Assert.AreEqual(100, row.HeadingsTotal);
        Assert.AreEqual(97, row.HeadingsLocated);
    }

    [TestMethod]
    public async Task NoDocumentTookTheDeclaredRoute_ReportsNoHeadingSummaryAtAll()
    {
        var (reporter, reports) = BuildReporter();
        var doc                 = Doc("doc1");
        var (state, _)          = StateFor(doc);

        state.Chunked(doc, [Chunk("doc1", 0, 0, 100)], cutCount: 1, route: "Recursive");

        await reporter.WriteAsync(state, CancellationToken.None);

        // Null, not a 0% failure rate: nothing was attempted, so there is nothing to report.
        Assert.IsNull(reports.Single().HeadingLocation);
        Assert.AreEqual(0, reports.Single().Documents.Single().HeadingsTotal);
    }
}
