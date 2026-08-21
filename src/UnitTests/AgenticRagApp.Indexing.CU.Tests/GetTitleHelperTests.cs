using AgenticRagApp.Indexing.CU.Models;
using AgenticRagApp.Indexing.CU.Services;

namespace RagApp.UnitTests.Indexing;

// The rejected cases below are the real ones, measured over the 51-document corpus on
// 2026-08-14 (docs/2608/260814/b1b2-measurement-raw.md). The kept cases are the other half of
// that measurement - native titles that differ from the filename and are perfectly good.
[TestClass]
public class GetTitleHelperTests
{
    private static DocMetadata Meta(string? title) => new(
        Title: title, Author: null, CreatedAt: null, ModDate: null,
        Producer: null, Creator: null, Subject: null, Keywords: null,
        PageCount: 1, Bookmarks: null,
        IsEncrypted: false, FormFields: null, EmbeddedFiles: null, Xmp: null,
        NativePageDimensions: null);

    private static string Title(string? nativeTitle, string blobName) =>
        GetTitleHelper.GetTitle(Meta(nativeTitle), blobName);

    [TestMethod]
    public void NoNativeTitle_FallsBackToTheFileName()
    {
        Assert.AreEqual("CAO GGZ (Versie 4)", Title(null, "CAO GGZ (Versie 4).pdf"));
        Assert.AreEqual("CAO GGZ (Versie 4)", Title("   ", "CAO GGZ (Versie 4).pdf"));
    }

    [TestMethod]
    public void NativeTitle_IsKeptWhenItLooksLikeATitle()
    {
        Assert.AreEqual("CAO GGZ 2025", Title("CAO GGZ 2025", "cao-ggz.pdf"));

        // Both real corpus cases where the native title differs from the filename and is
        // genuinely the better string - these must survive the heuristic.
        Assert.AreEqual(
            "Infographic Contoso AI-lab",
            Title("Infographic Contoso AI-lab", "AI-Lab - praatplaat (Versie 1).pdf"));
        Assert.AreEqual(
            "Generiek kompas Samen werken aan kwaliteit van bestaan",
            Title("Generiek kompas Samen werken aan kwaliteit van bestaan",
                  "Generiek kompas samen werken aan kwaliteit van bestaan (Versie 1).pdf"));
    }

    [TestMethod]
    public void AuthoringToolPrefix_IsRejected()
    {
        Assert.AreEqual(
            "Factsheet ZZP (Versie 1)",
            Title("Microsoft Word - Factsheet ZZP_def", "Factsheet ZZP (Versie 1).pdf"));
    }

    [TestMethod]
    public void SourceFileExtension_IsRejected()
    {
        Assert.AreEqual(
            "Diversiteitskompas - boekje (Versie 1)",
            Title("Contoso-Diversiteitskompas-boekje-v03.indd", "Diversiteitskompas - boekje (Versie 1).pdf"));
        Assert.AreEqual(
            "Hulpmiddel begroting 2026 tarievenlijst (Versie 1)",
            Title("Hulpmiddel begroting 2026 tarievenlijst.xlsx", "Hulpmiddel begroting 2026 tarievenlijst (Versie 1).pdf"));
    }

    [TestMethod]
    public void LeadingJobOrDateNumber_IsRejected()
    {
        Assert.AreEqual(
            "Folder Beeldzorg - informatie (Versie 3)",
            Title("1157026 Contoso", "Folder Beeldzorg - informatie (Versie 3).pdf"));
        Assert.AreEqual(
            "Buddy - infographic (Versie 1)",
            Title("200604-Contoso-buddy infographic-A4-300 dpi-cmyk", "Buddy - infographic (Versie 1).pdf"));
    }

    [TestMethod]
    public void YearInsideATitle_IsNotAJobNumber()
    {
        // The job-number rule requires the digits to START the title, so a legitimate title
        // carrying a year is untouched.
        Assert.AreEqual("CAO 2024 GHZ", Title("CAO 2024 GHZ", "cao-ghz.pdf"));
    }

    [TestMethod]
    public void LeadingNumberTheFileNameAlsoHas_IsPartOfTheNamingConvention()
    {
        // Caught by validating the heuristic against the real corpus: this corpus names
        // documents "202601 Privacybeleid Contoso", a yyyymm prefix the filename carries too.
        // A job number is one the FILENAME does not know about.
        Assert.AreEqual(
            "202601 Privacybeleid Contoso (Versie 3)",
            Title("202601 Privacybeleid Contoso (Versie 3)", "202601 Privacybeleid Contoso (Versie 3).pdf"));

        Assert.AreEqual(
            "202601 Privacybeleid Contoso",
            Title("202601 Privacybeleid Contoso", "202601 Privacybeleid Contoso (Versie 3).pdf"));
    }

    [TestMethod]
    public void TitleIdenticalToTheFileName_IsNeverAnArtifact()
    {
        // The two agree, so no rule can improve on it - checked before the rules so that a
        // legitimate title sharing a corpus naming convention cannot be rejected.
        Assert.AreEqual("Drukwerk", Title("Drukwerk", "Drukwerk.pdf"));
    }

    [TestMethod]
    public void SingleWordTitle_IsRejectedOnlyWhenTheFileNameDisagrees()
    {
        // "Drukwerk" (printed matter) on a document about risk inventories.
        Assert.AreEqual(
            "1. Infokaart LG - Hoe doe ik een RIE (Versie 1)",
            Title("Drukwerk", "1. Infokaart LG - Hoe doe ik een RIE (Versie 1).pdf"));

        // A one-word title the filename agrees with is a real title, not an artifact.
        Assert.AreEqual(
            "Privacybeleid",
            Title("Privacybeleid", "202601 Privacybeleid Contoso (Versie 3).pdf"));
    }
}
