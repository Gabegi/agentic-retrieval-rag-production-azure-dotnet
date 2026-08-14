using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The document's title: native PDF Title when the file sets a usable one, else a
// filename-derived fallback - unlike the other Get*Helpers, this doesn't read off the DI
// AnalyzeResult at all, since native metadata already has the more trustworthy answer when
// it's present.
//
// "Usable" is doing work here. Measured over the real 51-document corpus on 2026-08-14, 10
// documents carry a native Title that is an authoring-tool artifact rather than a title:
// "Drukwerk", "1157026 Contoso", "Microsoft Word - Factsheet ZZP_def",
// "Contoso-Diversiteitskompas-boekje-v03.indd", "200604-Contoso-buddy infographic-A4-300
// dpi-cmyk", "Hulpmiddel begroting 2026 tarievenlijst.xlsx". That string is not cosmetic: it
// is the first line of every chunk's embedded text, the `title` field returned to the model,
// what a citation shows the user, and the leading term of the identity text used for family
// clustering and the confusable-title check. A document was being embedded, cited and
// clustered under the word "Drukwerk" (Dutch for "printed matter").
//
// The rules below are deliberately conservative - each one targets a specific artifact shape
// seen in the corpus, and a title that merely differs from the filename is kept. Being wrong
// in this direction costs a slightly worse title; being too aggressive would discard genuine
// titles the PDF author actually set. See docs/2608/260814/documentidentityresolver-fixes.md N6.
internal static class GetTitleHelper
{
    // Authoring tools that stamp their own name into the Title field.
    private static readonly string[] ToolPrefixes =
        ["Microsoft Word - ", "Microsoft PowerPoint - ", "Microsoft Excel - ", "Microsoft Publisher - "];

    // A saved filename left in the Title field, extension and all.
    private static readonly string[] SourceFileExtensions =
        [".indd", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".pub", ".ai", ".psd", ".qxd", ".pdf"];

    // A leading print-job or date number: "1157026 Contoso", "200604-Contoso-buddy...",
    // "130613 Vereenvoudigd risicosignaleringsprotocol". Six digits is the shortest form that
    // occurs (yymmdd), and requiring the run to START the title keeps legitimate titles that
    // merely contain a year ("CAO 2024 GHZ") out of it.
    private static readonly Regex LeadingJobNumber = new(@"^\d{6,}", RegexOptions.Compiled);

    private static readonly Regex WordPattern = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    public static string GetTitle(DocMetadata? nativeMetadata, string blobName)
    {
        // GetFileNameWithoutExtension, not Split('/')[0]: for "protocols/policy-2024.pdf"
        // the latter returns the folder, not the file.
        var fromFileName = Path.GetFileNameWithoutExtension(blobName.AsSpan()).ToString();

        // nativeMetadata is nullable so document assembly (PdfExtractionPipeline.BuildDocuments)
        // can call this with whatever it has: "no native metadata at all" and "native metadata
        // with no Title" both mean the same thing here, and both already have an answer.
        var title = nativeMetadata?.Title;

        return string.IsNullOrWhiteSpace(title) || LooksLikeExportArtifact(title, fromFileName)
            ? fromFileName
            : title;
    }

    // True when the native Title looks like something an authoring tool left behind rather
    // than a title a person wrote.
    internal static bool LooksLikeExportArtifact(string title, string fileNameTitle)
    {
        var trimmed = title.Trim();

        // The two agree, so there is nothing an artifact rule could improve. Checked first
        // because some of the rules below would otherwise misfire on a legitimate title that
        // happens to share the corpus's own naming conventions.
        if (string.Equals(trimmed, fileNameTitle.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (ToolPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (SourceFileExtensions.Any(e => trimmed.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            return true;

        // A leading digit run is only a print-job number if the FILENAME doesn't know about
        // it. This corpus names documents "202601 Privacybeleid Contoso", a yyyymm prefix that
        // is part of the convention - caught by this rule until the filename was consulted.
        var jobNumber = LeadingJobNumber.Match(trimmed);
        if (jobNumber.Success && !fileNameTitle.Contains(jobNumber.Value, StringComparison.Ordinal))
            return true;

        // A single word that appears nowhere in the filename ("Drukwerk" on
        // "1. Infokaart LG - Hoe doe ik een RIE"). One word is too little to be a title when
        // the filename disagrees with it entirely; a single-word title that IS in the filename
        // ("Privacybeleid" on "202601 Privacybeleid Contoso") is kept, since the two agree.
        var titleWords = WordPattern.Matches(trimmed).Select(m => m.Value).ToList();
        if (titleWords.Count == 1)
        {
            var fileWords = WordPattern.Matches(fileNameTitle).Select(m => m.Value);
            if (!fileWords.Contains(titleWords[0], StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
