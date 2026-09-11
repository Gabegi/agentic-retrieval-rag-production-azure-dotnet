using System.Text;
using AgenticRagApp.Infrastructure.Clients.Zenya.Models;

namespace AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

// The on-disk contract of the zenya-documents container: where a document lands and what its
// blob metadata says. Everything the indexing side will later read off this container is
// defined here and nowhere else (D185).
//
// Layout - two virtual folders (blob-name prefixes), chosen 2026-09-11:
//   pdf/{document_id}.pdf     what /download returned as a PDF
//   docs/{document_id}.{ext}  every other binary (Word mostly), real extension, ".bin" if unknown
// The blob name doubles as the indexer's SourceId (PdfExtractionDocument: SourceId == blobName),
// so it is the Zenya document_id, never the title: a rename in Zenya must not look like a new
// document (D175 A5). When the PDF indexer is pointed at this container it lists the pdf/
// prefix only, so Word files never reach Content Understanding by accident.
public static class ZenyaBlobLayout
{
    public const string PdfPrefix  = "pdf/";
    public const string DocsPrefix = "docs/";

    // Blob metadata keys. Free-text values (title, quick code, type names) are ALWAYS
    // RFC 3986 percent-encoded (Uri.EscapeDataString): blob metadata values must be ASCII and
    // Dutch titles are not. Readers decode with Uri.UnescapeDataString. Identifiers, numbers,
    // Zenya's yyyyMMddHHmmss timestamps and mime types are ASCII by construction and stored raw.
    public const string DocumentIdKey   = "zenya_document_id";   // raw GUID - the removal pass keys on this
    public const string VersionKey      = "zenya_version";       // raw int - the change signal
    public const string StatusKey       = "zenya_status";        // raw; the DTO `state`, else the listing state
    public const string QuickCodeKey    = "zenya_quick_code";    // encoded (Snelcode)
    public const string TitleKey        = "zenya_title";         // encoded
    public const string TypeKey         = "zenya_type";          // encoded
    public const string DocumentTypeKey = "zenya_document_type"; // encoded
    public const string MimeTypeKey     = "zenya_mime_type";     // raw
    public const string LastModifiedKey = "zenya_last_modified"; // raw, Zenya's yyyyMMddHHmmss UTC string
    public const string SyncedAtKey     = "zenya_synced_at";     // raw, ISO-8601 UTC of this write

    // Fallback for zenya_status when the document DTO carries no `state`: the listing state the
    // sync asks for (Zenya default; which states the corpus needs is an open product question,
    // D175). With `state` present, that documented value is recorded instead.
    public const string PublishedStatus = "published";

    public static IReadOnlyDictionary<string, string> BuildMetadata(
        ZenyaDocumentMetadata document, string? mimeType, DateTimeOffset syncedAt)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DocumentIdKey] = document.DocumentId,
            [VersionKey]    = document.Version.ToString(),
            [StatusKey]     = string.IsNullOrWhiteSpace(document.State) ? PublishedStatus : document.State.Trim(),
            [SyncedAtKey]   = syncedAt.ToUniversalTime().ToString("O"),
        };
        AddEncoded(metadata, QuickCodeKey,    document.QuickCode);
        AddEncoded(metadata, TitleKey,        document.Title);
        AddEncoded(metadata, TypeKey,         document.Type);
        AddEncoded(metadata, DocumentTypeKey, document.DocumentType?.Name);
        AddRaw(metadata, MimeTypeKey,     mimeType);
        AddRaw(metadata, LastModifiedKey, document.LastModifiedDateTime);
        return metadata;
    }

    // Name from what the download actually was, in this order: the response's Content-Type, the
    // metadata's download_binary_extension, the metadata's mime_type. The response wins because
    // Zenya can render some documents as PDF on download (download_as_pdf) regardless of the
    // stored type - D173 §"first full run" asks exactly this question, and naming by the answer
    // keeps the container truthful.
    public static string BlobNameFor(ZenyaDocumentMetadata document, string? responseContentType)
    {
        var extension = ExtensionFor(responseContentType, document.DownloadBinaryExtension, document.MimeType);
        var prefix = extension == "pdf" ? PdfPrefix : DocsPrefix;
        return $"{prefix}{document.DocumentId}.{extension}";
    }

    public static string ExtensionFor(string? responseContentType, string? declaredExtension, string? declaredMimeType)
    {
        if (ExtensionForMime(responseContentType) is { } fromResponse) return fromResponse;
        if (!string.IsNullOrWhiteSpace(declaredExtension))
        {
            var ext = declaredExtension.Trim().TrimStart('.').ToLowerInvariant();
            if (ext.Length > 0 && ext.All(c => char.IsAsciiLetterOrDigit(c))) return ext;
        }
        return ExtensionForMime(declaredMimeType) ?? "bin";
    }

    // Only the types a document-management system plausibly serves; anything else is "bin" with
    // the mime type preserved in metadata, so nothing is guessed.
    private static string? ExtensionForMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return null;
        var media = mime.Split(';')[0].Trim().ToLowerInvariant();
        return media switch
        {
            "application/pdf" => "pdf",
            "application/msword" => "doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
            "application/vnd.ms-excel" => "xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
            "application/vnd.ms-powerpoint" => "ppt",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "pptx",
            "text/plain" => "txt",
            "text/html" => "html",
            _ => null,
        };
    }

    // "%PDF" at offset 0 (PDF 1.7 §7.5.2). Reported per run so the first full sync answers D173's
    // question 3 ("does /download return real PDF bytes") with a count, not an assumption.
    public static bool HasPdfMagic(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F';

    private static void AddEncoded(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) metadata[key] = Uri.EscapeDataString(value.Trim());
    }

    private static void AddRaw(Dictionary<string, string> metadata, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        // Defensive: a raw field that turns out non-ASCII would fail the whole upload with a 400;
        // encode it rather than lose the document over a metadata byte.
        metadata[key] = Encoding.ASCII.GetByteCount(trimmed) == trimmed.Length && !trimmed.Any(char.IsControl)
            ? trimmed
            : Uri.EscapeDataString(trimmed);
    }
}
