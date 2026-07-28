using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfSectionBreadCrumbBuilderTests
{
    private static Bookmark Bm(string title, int level, int? page, bool external = false) => new(title, level, page, external);

    [TestMethod]
    public void NoBookmarks_ReturnsEmpty_NoWarnings()
    {
        var (breadcrumbs, diagnostics) = PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(null, pageCount: 5, "doc.pdf");

        Assert.AreEqual(0, breadcrumbs.Count);
        Assert.AreEqual(0, diagnostics.Warnings.Count);
    }

    [TestMethod]
    public void UnresolvableInternalAndExternal_ProduceSeparateCounts()
    {
        var bookmarks = new[]
        {
            Bm("Real", 0, 1),
            Bm("Broken internal link", 0, null, external: false),
            Bm("External file link", 0, null, external: true),
        };

        var (_, diagnostics) = PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(bookmarks, pageCount: 5, "doc.pdf");

        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("1 bookmark(s) excluded - no resolvable page")));
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("1 bookmark(s) excluded - point to an external file")));
    }

    [TestMethod]
    public void AllBookmarksUnresolvable_ReturnsEmptyWithoutOutOfRangeOrEmptyWarnings()
    {
        // Every bookmark fails the PageNumber > 0 filter - ordered.Count hits 0 and the
        // method returns early, before the out-of-range/skipped-level/empty-result warnings
        // (which only make sense once at least one bookmark resolved to a page) ever run.
        var bookmarks = new[] { Bm("Broken", 0, null) };

        var (breadcrumbs, diagnostics) = PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(bookmarks, pageCount: 5, "doc.pdf");

        Assert.AreEqual(0, breadcrumbs.Count);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("1 bookmark(s) excluded - no resolvable page")));
        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("resolved to a page but none produced")));
    }

    [TestMethod]
    public void BookmarkBeyondPageCount_ProducesOutOfRangeWarning_AndNoBreadcrumbAssigned()
    {
        var bookmarks = new[] { Bm("Chapter 1", 0, 10) };

        var (breadcrumbs, diagnostics) = PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(bookmarks, pageCount: 3, "doc.pdf");

        Assert.AreEqual(0, breadcrumbs.Count);
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("point beyond this document's 3 page(s)")));
        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("resolved to a page but none produced a breadcrumb")));
    }

    [TestMethod]
    public void SkippedOutlineLevel_ProducesWarning()
    {
        // Level 2 with nothing at level 0/1 before it - a sloppy/skipped outline level.
        var bookmarks = new[] { Bm("Deep section", 2, 1) };

        var (_, diagnostics) = PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(bookmarks, pageCount: 3, "doc.pdf");

        Assert.IsTrue(diagnostics.Warnings.Any(w => w.Message.Contains("skip an outline level")));
    }

    [TestMethod]
    public void SequentialLevels_NoSkippedLevelWarning()
    {
        var bookmarks = new[] { Bm("Chapter 1", 0, 1), Bm("1.1 Intro", 1, 2) };

        var (_, diagnostics) = PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(bookmarks, pageCount: 3, "doc.pdf");

        Assert.IsFalse(diagnostics.Warnings.Any(w => w.Message.Contains("skip an outline level")));
    }

    [TestMethod]
    public void NestedBookmarks_BuildCorrectBreadcrumbPerPage()
    {
        var bookmarks = new[] { Bm("Chapter 1", 0, 1), Bm("1.1 Intro", 1, 2) };

        var (breadcrumbs, _) = PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(bookmarks, pageCount: 3, "doc.pdf");

        Assert.AreEqual("_Section: Chapter 1_", breadcrumbs[1]);
        Assert.AreEqual("_Section: Chapter 1 > 1.1 Intro_", breadcrumbs[2]);
        Assert.AreEqual("_Section: Chapter 1 > 1.1 Intro_", breadcrumbs[3], "Page 3 carries forward the last-active breadcrumb.");
    }

    [TestMethod]
    public void NewTopLevelBookmark_ResetsDeeperStackEntries()
    {
        // Chapter 1 > 1.1 Intro (page 1-2), then a new top-level Chapter 2 (page 3) should
        // NOT carry "1.1 Intro" forward as a leftover sub-section.
        var bookmarks = new[]
        {
            Bm("Chapter 1", 0, 1),
            Bm("1.1 Intro", 1, 2),
            Bm("Chapter 2", 0, 3),
        };

        var (breadcrumbs, _) = PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(bookmarks, pageCount: 3, "doc.pdf");

        Assert.AreEqual("_Section: Chapter 2_", breadcrumbs[3]);
    }

    [TestMethod]
    public void SameStablePageOrder_PreservesOutlineOrderForTiedPages()
    {
        // Two bookmarks on the same page, in outline order - OrderBy's stable sort must
        // keep "Part A" before "Part B" so "Part B" (processed second) wins as most-recently-active.
        var bookmarks = new[] { Bm("Part A", 0, 1), Bm("Part B", 0, 1) };

        var (breadcrumbs, _) = PdfSectionBreadCrumbBuilder.BuildSectionBreadcrumbs(bookmarks, pageCount: 1, "doc.pdf");

        Assert.AreEqual("_Section: Part B_", breadcrumbs[1]);
    }
}
