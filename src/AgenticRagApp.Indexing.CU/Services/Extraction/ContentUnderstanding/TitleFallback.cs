using System.Text.RegularExpressions;

namespace AgenticRagApp.Indexing.CU.Services;

// The document's title, derived from its blob name.

// CU's own Title-role paragraph is deliberately NOT used: it is cover-page text, which carries
// the same "first big string on the page is not the document's name" risk the native titles
// did, and unlike the filename it is not stable across re-extractions.
internal static partial class TitleFallback
{
    // A repeated trailing "(Versie N)": "Handleiding Medimo toedienregistratie (webversie)
    // (Versie 2) (Versie 2)" - 45 chunks in the 260818 index. The doubling arrives IN the blob
    // name, so the repair is a collapse rule here and a report to the Zenya export.
    // Backreference, not a second wildcard - "(Versie 1) (Versie 2)" is not the artifact and is
    // kept.
    [GeneratedRegex(@"(\(Versie\s+\d+\))(?:\s*\1)+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedVersionSuffix();

    public static string GetTitle(string blobName)
    {
        // GetFileNameWithoutExtension, not Split('/')[0]: for "protocols/policy-2024.pdf"
        // the latter returns the folder, not the file.
        var title = Path.GetFileNameWithoutExtension(blobName.AsSpan()).ToString();

        return RepeatedVersionSuffix().Replace(title, "$1");
    }
}
