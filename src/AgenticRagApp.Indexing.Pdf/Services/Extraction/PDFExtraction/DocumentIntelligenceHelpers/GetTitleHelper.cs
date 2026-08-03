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
    public static string GetTitle(DocMetadata nativeMetadata, string blobName) =>
        !string.IsNullOrWhiteSpace(nativeMetadata.Title)
            ? nativeMetadata.Title
            : Path.GetFileNameWithoutExtension(blobName.AsSpan()).ToString();
}
