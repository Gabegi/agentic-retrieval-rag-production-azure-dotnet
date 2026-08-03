using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Outline;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The PDF's own Info-dictionary + bookmark tree + AcroForm fields + XMP packet,
// read via PdfPig.
internal static class PdfNativeMetadataExtractor
{
    private static readonly XNamespace RdfNs = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace DcNs  = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace PdfNs = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace XmpNs = "http://ns.adobe.com/xap/1.0/";

    // Separator for XMP container properties that legitimately hold several values
    // (dc:creator, dc:subject). Matches the separator already used for multi-select
    // AcroForm list/combo values in GetFieldValue.
    private const string MultiValueSeparator = "; ";

    // Two diagnostic buckets, gathered behind one parameter so each step method takes
    // a single collector rather than a (warnings, infos) pair. Warn is for something
    // missing or broken; Info is for something present and fine. Both share the
    // PipelineIssue shape, which is what PdfStepDiagnostics stores.
    // Nested and private because every consumer is a private method in this class; lift
    // it to its own file if another step needs the same pattern.
    private sealed class PdfDiagnostics
    {
        private readonly string _blobName;

        public PdfDiagnostics(string blobName) => _blobName = blobName;

        public List<PipelineIssue> Warnings { get; } = [];
        public List<PipelineIssue> Infos    { get; } = [];

        // Every entry in this file is RowNumber=null (RowNumber is a CSV concept, never
        // set for PDFs) + the same DocumentId; this is the one shape they all share.
        public void Warn(string message) =>
            Warnings.Add(PipelineIssue.Warning(PipelineStage.Metadata, _blobName, message));

        public void Info(string message) =>
            Infos.Add(PipelineIssue.Warning(PipelineStage.Metadata, _blobName, message));
    }

    // Reads pdf native Title/Author/CreationDate/ModDate/Producer/Creator/Subject/
    // Keywords, the bookmark tree, AcroForm fields, IsEncrypted, and the XMP metadata
    // packet off an open pdf.
    // - Takes ownership of pdf's lifetime (disposes it here, not in the caller).
    // - Called once, by DocumentIntelligenceExtractor, after preflight opens pdf.
    // diagnostics is report/diagnostic material only (see PdfStepDiagnostics); never
    // fails the file, since native metadata is a nice-to-have, not required for DI to
    // process the document.
    public static DocMetadata ExtractPdfNativeMetadataAndDispose(
        PdfDocument pdf, string blobName, ILogger logger, out PdfStepDiagnostics diagnostics)
    {
        using (pdf)
        {
            var diag = new PdfDiagnostics(blobName);

            var info          = pdf.Information;
            var bookmarks     = TryGetBookmarks(pdf, blobName, logger, diag);
            var formFields    = TryGetAcroFormFields(pdf, blobName, logger, diag);
            var embeddedFiles = TryGetEmbeddedFiles(pdf, blobName, logger, diag);
            var xmp           = GetXmpMetadata(pdf, blobName, logger, diag);
            var pageDimensions = TryGetPageDimensions(pdf, blobName, logger, diag);

            var title     = NullIfEmpty(info.Title);
            var author    = NullIfEmpty(info.Author);
            var producer  = NullIfEmpty(info.Producer);
            var creator   = NullIfEmpty(info.Creator);
            var subject   = NullIfEmpty(info.Subject);
            var keywords  = NullIfEmpty(info.Keywords);

            // Parsing itself is PdfPig's own (GetCreatedDateTimeOffset/GetModifiedDateTimeOffset)
            // rather than hand-rolled. ResolveDate still needs the raw string alongside the
            // parsed value, since the library's Nullable<DateTimeOffset> return can't tell
            // "field absent" apart from "field present but unparseable" on its own, and the
            // warning messages below (and the tests that check them) depend on that distinction.
            var createdAt = ResolveDate(info.CreationDate, info.GetCreatedDateTimeOffset(), "CreationDate", diag);
            var modDate   = ResolveDate(info.ModifiedDate, info.GetModifiedDateTimeOffset(), "ModDate", diag);

            // Title and Producer get their own message and stay Warnings: each explains
            // a real downstream consequence (Title falls back to a filename-derived
            // value; a missing Producer suggests a non-standard export pipeline).
            // Author/Creator/Subject/Keywords have no such consequence anywhere
            // downstream - on this corpus (Word-exported PDFs) they're absent on
            // nearly every document, so as Warnings they dominated the Issues list and
            // (per finding #9/#15) could crowd out actual TextQuality errors from the
            // truncated report/log. Reported as Info instead: still visible, no longer
            // competing with real defects for the same budget.
            if (title is null)
                diag.Warn("No native Title in the PDF's Info dictionary; falls back to a filename-derived title downstream.");

            if (producer is null)
                diag.Warn("No native Producer in the PDF's Info dictionary; possible non-standard export pipeline.");

            foreach (var (fieldName, value) in new (string Name, string? Value)[]
            {
                ("Author", author), ("Creator", creator), ("Subject", subject), ("Keywords", keywords),
            })
                if (value is null)
                    diag.Info($"No native {fieldName} in the PDF's Info dictionary.");

            if (bookmarks is { Count: > 0 })
                diag.Info($"{bookmarks.Count} bookmark(s) found, max outline depth {bookmarks.Max(b => b.Level) + 1}.");

            if (pdf.IsEncrypted)
                diag.Warn("PDF carries encryption/permission restrictions (opened successfully, not password-protected).");

            var metadata = new DocMetadata(
                Title:      title,
                Author:     author,
                CreatedAt:  createdAt,
                ModDate:    modDate,
                Producer:   producer,
                Creator:    creator,
                Subject:    subject,
                Keywords:   keywords,
                PageCount:  pdf.NumberOfPages,
                Bookmarks:  bookmarks,
                IsEncrypted:    pdf.IsEncrypted,
                FormFields:     formFields,
                EmbeddedFiles:  embeddedFiles,
                Xmp:            xmp,
                NativePageDimensions: pageDimensions);

            // Title and Author are document content, not just processing state, so this
            // line puts a small amount of potentially personal data in the logs. Kept
            // deliberately: it's Debug-level (off in production by default) and these two
            // fields are what makes a metadata bug diagnosable from logs alone. Drop them
            // from the template if the deployment's log sink isn't cleared for content.
            logger.LogDebug(
                "PdfNativeMetadataExtractor: '{Blob}', {Pages} page(s), title={Title}, author={Author}, created={Created}, modified={Modified}, producer={Producer}",
                blobName, metadata.PageCount, metadata.Title, metadata.Author, metadata.CreatedAt, metadata.ModDate, metadata.Producer);

            diagnostics = new PdfStepDiagnostics(
                Warnings: diag.Warnings,
                Errors:   [],
                Info:     diag.Infos);

            return metadata;
        }
    }

    // Bookmarks/outline tree; PdfPig only, best-effort.
    // - TryGetBookmarks can still throw on a malformed node despite the name; caught here.
    // - null = read failed (skip bookmarks). Empty list = read fine, PDF has none.
    // Absence is reported as Info, not Warn: a PDF without an outline is normal, and
    // TryGetBookmarks/TryGetFormFields/TryGetEmbeddedFiles/GetXmpMetadata all report
    // absence the same way so a report reader can tell "not present" from "read failed"
    // (which warns) and from "step never ran" (which says nothing).
    private static IReadOnlyList<Bookmark>? TryGetBookmarks(
        PdfDocument pdf, string blobName, ILogger logger, PdfDiagnostics diag)
    {
        try
        {
            if (!pdf.TryGetBookmarks(out var bookmarks))
            {
                logger.LogInformation("No bookmarks/outline found in '{Blob}'.", blobName);
                diag.Info("No bookmarks/outline present.");
                return Array.Empty<Bookmark>();
            }

            return bookmarks.GetNodes()
                .Select(node => new Bookmark(
                    node.Title, node.Level, TryGetPageNumber(node),
                    IsExternal: node is ExternalBookmarkNode,
                    IsEmbedded: node is EmbeddedBookmarkNode))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bookmark extraction failed for '{Blob}'; continuing without bookmarks.", blobName);
            diag.Warn($"Bookmark extraction failed: {ex.Message}");
            return null;
        }
    }

    // Native per-page size (MediaBox), read directly off PdfPig's Page.Width/Height -
    // best-effort, same pattern as TryGetBookmarks. Unit is always "point" (PdfPig's
    // own unit); PageDimensionWarningsHelper does the inch conversion later, using
    // whichever unit Document Intelligence reports for that page, not an assumption
    // baked in here.
    private static IReadOnlyList<PageDimensions>? TryGetPageDimensions(
        PdfDocument pdf, string blobName, ILogger logger, PdfDiagnostics diag)
    {
        try
        {
            var dimensions = new List<PageDimensions>(pdf.NumberOfPages);
            for (var pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
            {
                var page = pdf.GetPage(pageNumber);
                dimensions.Add(new PageDimensions(pageNumber, page.Width, page.Height, "point"));
            }
            return dimensions;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Native page-dimension read failed for '{Blob}'; continuing without it.", blobName);
            diag.Warn($"Native page-dimension read failed: {ex.Message}");
            return null;
        }
    }

    // AcroForm fields; PdfPig only, best-effort, same pattern as TryGetBookmarks.
    // PartialName is the field's own name segment (AcroFieldCommonInformation doesn't
    // expose the fully-qualified dotted name; that requires walking the Parent chain,
    // which PdfPig only exposes as an unresolved indirect reference).
    private static IReadOnlyList<AcroFormField>? TryGetAcroFormFields(
        PdfDocument pdf, string blobName, ILogger logger, PdfDiagnostics diag)
    {
        try
        {
            if (!pdf.TryGetForm(out var form))
            {
                diag.Info("No AcroForm present.");
                return Array.Empty<AcroFormField>();
            }

            var fields = form.Fields
                .Select(f => new AcroFormField(
                    f.Information.PartialName, f.Information.AlternateName, f.Information.MappingName,
                    f.FieldType.ToString(), f.FieldFlags, f.PageNumber, GetFieldValue(f)))
                .ToList();

            diag.Info(fields.Count > 0
                ? $"{fields.Count} AcroForm field(s) found."
                : "AcroForm present but contains no fields.");

            return fields;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AcroForm extraction failed for '{Blob}'; continuing without form fields.", blobName);
            diag.Warn($"AcroForm extraction failed: {ex.Message}");
            return null;
        }
    }

    // The entered/selected value, if any. Lives on the field-type-specific
    // subclass, not AcroFieldBase itself, so this has to type-switch. Checkbox/
    // radio only report their CurrentValue when actually checked/selected (an
    // unchecked box has a CurrentValue too, the "off" state name, which isn't
    // meaningful as "the value"). Push buttons and signature fields carry no
    // value-bearing data at all.
    private static string? GetFieldValue(AcroFieldBase field) => field switch
    {
        AcroTextField        text                   => NullIfEmpty(text.Value),
        AcroCheckboxField    { IsChecked: true }  cb => cb.CurrentValue?.Data,
        AcroRadioButtonField { IsSelected: true } rb => rb.CurrentValue?.Data,
        AcroListBoxField     list                   => JoinOrNull(list.SelectedOptions),
        AcroComboBoxField    combo                  => JoinOrNull(combo.SelectedOptions),
        _                                           => null,
    };

    private static string? JoinOrNull(IReadOnlyList<string> values) =>
        values.Count > 0 ? string.Join(MultiValueSeparator, values) : null;

    // Embedded file attachments; PdfPig only, best-effort, same pattern as
    // TryGetBookmarks/TryGetFormFields. Name/FileSpecification only, never the
    // attachment's own bytes, which don't belong in a metadata report.
    private static IReadOnlyList<EmbeddedFileInfo>? TryGetEmbeddedFiles(
        PdfDocument pdf, string blobName, ILogger logger, PdfDiagnostics diag)
    {
        try
        {
            if (!pdf.Advanced.TryGetEmbeddedFiles(out var files))
            {
                diag.Info("No embedded file attachments present.");
                return Array.Empty<EmbeddedFileInfo>();
            }

            var result = files
                .Select(f => new EmbeddedFileInfo(f.Name, f.FileSpecification))
                .ToList();

            diag.Info(result.Count > 0
                ? $"{result.Count} embedded file attachment(s) found."
                : "No embedded file attachments present.");

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Embedded-file extraction failed for '{Blob}'; continuing without attachments.", blobName);
            diag.Warn($"Embedded-file extraction failed: {ex.Message}");
            return null;
        }
    }

    // XMP metadata packet (Catalog -> /Metadata stream), a separate and newer metadata
    // mechanism from the legacy Info dictionary above; best-effort, same pattern as
    // TryGetBookmarks/TryGetFormFields. PdfPig only hands back the raw decoded stream
    // bytes (XmpMetadata.MetadataStreamToken.Data); it does not parse the RDF/XML
    // itself, so only the handful of common Dublin Core / XMP Basic / PDF-schema
    // fields most export tools actually write are read here; see XmpFacts's own comment.
    private static XmpFacts? GetXmpMetadata(
        PdfDocument pdf, string blobName, ILogger logger, PdfDiagnostics diag)
    {
        try
        {
            if (!pdf.TryGetXmpMetadata(out var xmp))
            {
                // No packet at all reads the same as "packet with no rdf:Description"
                // below - both are zero XMP facts, not a failure - so both return the
                // same all-null XmpFacts shape rather than null. That keeps null
                // reserved for "extraction threw" everywhere in this file, matching
                // TryGetBookmarks/TryGetFormFields/TryGetEmbeddedFiles (Array.Empty for
                // absent, null for failed). The diagnostics still tell the two zero-facts
                // cases apart by message.
                diag.Info("No XMP metadata packet present.");
                return new XmpFacts(null, null, null, null, null, null);
            }

            // Loaded from the raw byte stream, not decoded as UTF-8 up front: XMP
            // packets may be UTF-16 (with BOM) or declare their own encoding, and
            // XDocument.Load lets the XML reader resolve that instead of assuming UTF-8.
            //
            // SECURITY: this overload defaults to DtdProcessing.Prohibit, which is what
            // keeps an untrusted blob from reaching entity expansion / external entity
            // resolution. Do not swap it for an XmlReader overload without setting
            // DtdProcessing = Prohibit and XmlResolver = null explicitly.
            using var xmlStream = new MemoryStream(xmp.MetadataStreamToken.Data.ToArray());
            var doc = XDocument.Load(xmlStream);

            var descriptions = doc.Descendants(RdfNs + "Description").ToList();
            if (descriptions.Count == 0)
            {
                diag.Warn("XMP packet found but had no rdf:Description element.");
                return new XmpFacts(null, null, null, null, null, null);
            }

            // Writers routinely split properties across several rdf:Description
            // siblings, one per schema (dc:, pdf:, xmp:), so every lookup runs across
            // all of them and takes the first non-empty match rather than only reading
            // the first description encountered.
            //
            // dc:title is an rdf:Alt (one value, several languages) so it picks
            // x-default. dc:creator (rdf:Seq, ordered authors) and dc:subject
            // (rdf:Bag, keyword set) are genuinely multi-value, so every rdf:li is
            // kept and joined; taking only the first would silently drop co-authors
            // and all but one keyword.
            var facts = new XmpFacts(
                Title:      FirstAcross(descriptions, d => AltContainerValue(d.Element(DcNs + "title"))),
                Creator:    FirstAcross(descriptions, d => AllContainerValues(d.Element(DcNs + "creator"))),
                Subject:    FirstAcross(descriptions, d => AllContainerValues(d.Element(DcNs + "subject"))),
                Producer:   FirstAcross(descriptions, d => SimpleValue(d, PdfNs, "Producer")),
                CreateDate: ParseXmpDate(FirstAcross(descriptions, d => SimpleValue(d, XmpNs, "CreateDate"))),
                ModifyDate: ParseXmpDate(FirstAcross(descriptions, d => SimpleValue(d, XmpNs, "ModifyDate"))));

            diag.Info("XMP metadata packet found and parsed.");
            return facts;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "XMP metadata extraction failed for '{Blob}'; continuing without XMP.", blobName);
            diag.Warn($"XMP metadata extraction failed: {ex.Message}");
            return null;
        }
    }

    // Simple (non-container) XMP properties may legally be written as an
    // rdf:Description attribute instead of a child element (pdf:Producer="X"
    // rather than <pdf:Producer>X</pdf:Producer>); attribute wins if both
    // somehow appear. Not used for dc:title/creator/subject: those are always
    // rdf:Alt/Bag/Seq containers per spec, which can't be expressed as an
    // attribute at all.
    private static string? SimpleValue(XElement description, XNamespace ns, string name) =>
        NullIfEmpty((string?)description.Attribute(ns + name) ?? description.Element(ns + name)?.Value);

    // First non-empty result for one property across every rdf:Description in the
    // packet; see the schema-splitting note in GetXmpMetadata.
    private static string? FirstAcross(List<XElement> descriptions, Func<XElement, string?> select)
    {
        foreach (var description in descriptions)
        {
            var value = select(description);
            if (value is not null) return value;
        }
        return null;
    }

    // rdf:Alt container (dc:title): language alternatives of a single value, so exactly
    // one is chosen. Prefers the x-default item, else the first entry, else the
    // element's own text for the rare tool that writes a bare string instead of a
    // container.
    // internal (not private): unit tested directly against hand-built XElement
    // fragments - it takes a plain XElement, so no PDF/PdfPig fixture is needed at all.
    internal static string? AltContainerValue(XElement? element)
    {
        var items = ContainerItems(element);
        if (items is null) return NullIfEmpty(element?.Value);
        if (items.Count == 0) return null;

        var defaultItem = items.FirstOrDefault(li => (string?)li.Attribute(XNamespace.Xml + "lang") == "x-default");
        return NullIfEmpty((defaultItem ?? items[0]).Value);
    }

    // rdf:Seq / rdf:Bag containers (dc:creator, dc:subject): every item is part of the
    // value, so all non-empty entries are kept in document order and joined. Same bare
    // string fallback as AltContainerValue.
    private static string? AllContainerValues(XElement? element)
    {
        var items = ContainerItems(element);
        if (items is null) return NullIfEmpty(element?.Value);

        var values = items
            .Select(li => NullIfEmpty(li.Value))
            .Where(v => v is not null)
            .ToList();

        return values.Count > 0 ? string.Join(MultiValueSeparator, values) : null;
    }

    // The rdf:li entries of a container property, or null when the element is absent or
    // holds no container at all (which the two callers translate into their bare-string
    // fallback). Descendants rather than Elements because the rdf:Alt/Seq/Bag wrapper
    // sits between the property element and its items.
    private static List<XElement>? ContainerItems(XElement? element)
    {
        if (element is null) return null;

        var items = element.Descendants(RdfNs + "li").ToList();
        return items.Count == 0 ? null : items;
    }

    // XMP allows truncated ISO-8601 dates (bare "2024", "2024-05") alongside full
    // timestamps. DateTimeOffset.TryParse handles "2024-05" and full timestamps
    // fine but rejects a bare 4-digit year outright (confirmed empirically, not just
    // in the docs), so that's the one case that needs an explicit format fallback
    // rather than a full rewrite.
    private static readonly string[] XmpDateExactFormats = ["yyyy"];

    // AssumeUniversal | AdjustToUniversal on both attempts: an XMP date with no offset
    // ("2024", "2024-05-17") would otherwise pick up the *host machine's* offset, so the
    // same PDF indexed on two servers in different regions would store two different
    // instants. Values that do carry an offset are unaffected in meaning; they're just
    // normalised to UTC.
    private static DateTimeOffset? ParseXmpDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, styles, out var dt))
            return dt;

        return DateTimeOffset.TryParseExact(raw, XmpDateExactFormats, CultureInfo.InvariantCulture, styles, out dt)
            ? dt
            : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Resolves one Info-dictionary date field, using PdfPig's own parser (parsed) for
    // the value but the raw string (raw) to tell "field absent" apart from "field
    // present but unparseable"; the library's Nullable<DateTimeOffset> return can't
    // distinguish those on its own, and the two produce different warnings below.
    private static DateTimeOffset? ResolveDate(
        string? raw, DateTimeOffset? parsed, string fieldName, PdfDiagnostics diag)
    {
        if (string.IsNullOrEmpty(raw))
        {
            diag.Warn($"No native {fieldName} in the PDF's Info dictionary.");
            return null;
        }

        if (parsed is null)
        {
            diag.Warn($"{fieldName} '{raw}' could not be parsed.");
            return null;
        }

        if (parsed > DateTimeOffset.UtcNow)
            diag.Warn($"{fieldName} '{parsed:O}' is in the future.");

        return parsed;
    }

    // Page number for a bookmark node.
    // - ExternalBookmarkNode inherits DocumentBookmarkNode but points at another file,
    //   so it's excluded explicitly; its PageNumber isn't a page in this document.
    // - PdfPig's page-number-defaults-to-0 fix (#736/#930) isn't in the pinned 0.1.9,
    //   so an unresolvable destination may be missing from the tree rather than 0.
    private static int? TryGetPageNumber(BookmarkNode node) => node switch
    {
        ExternalBookmarkNode                       => null,
        DocumentBookmarkNode { PageNumber: > 0 } d => d.PageNumber,
        _                                          => null,
    };
}
