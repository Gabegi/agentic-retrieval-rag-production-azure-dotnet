using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The document's title: native PDF Title when the file sets one, else a filename-derived
// fallback - unlike the other Get*Helpers, this doesn't read off the DI AnalyzeResult at
// all, since native metadata already has the more trustworthy answer when it's present.
internal static class GetTitleHelper
{
    // nativeMetadata.Title when the PDF actually sets one, else derived from the blob name.
    // - GetFileNameWithoutExtension, not Split('/')[0]: for "protocols/policy-2024.pdf"
    //   the latter returns the folder, not the file.
    // - nativeMetadata is nullable so document assembly (PdfExtractionPipeline.BuildDocuments)
    //   can call this with whatever it has: "no native metadata at all" and "native metadata
    //   with no Title" both mean the same thing here, and both already have an answer. Callers
    //   shouldn't have to re-implement the filename fallback to handle the null case.
    public static string GetTitle(DocMetadata? nativeMetadata, string blobName)
    {
        var title = nativeMetadata?.Title;

        return !string.IsNullOrWhiteSpace(title)
            ? title
            : Path.GetFileNameWithoutExtension(blobName.AsSpan()).ToString();
    }
}
