namespace AgenticRagApp.Indexing.CU.Models;

// A barcode or QR code the service decoded (DocumentPage.Barcodes).
//
// The decoded VALUE was never lost - CU writes it into the markdown, so it is chunked and
// searchable like any other text. What was lost is everything around it: which page it was on,
// what kind of code it was, and how sure the service was. D161 had to count the corpus's QR
// codes by hand out of the markdown for exactly that reason.
//
// SIX occurrences corpus-wide (D161), and the production corpus is expected to carry roughly as
// few. That is the honest value of this record: it is a monitored number rather than a
// retrieval improvement, and it costs nothing precisely BECAUSE they are rare. If a corpus ever
// arrives where codes carry meaning, the data is already typed and page-attributed.
//
// Kind is the service's own DocumentBarcodeKind as a string ("qrCode", "code128", ...) - not an
// enum of ours, so a kind this build has never seen still reports itself. Offset is nullable
// for the same reason as everywhere else in this folder: 0 is a valid offset.
public sealed record BarcodeInfo(
    string? Kind,
    string? Value,
    int? Offset,
    int PageNumber,
    double? Confidence);
