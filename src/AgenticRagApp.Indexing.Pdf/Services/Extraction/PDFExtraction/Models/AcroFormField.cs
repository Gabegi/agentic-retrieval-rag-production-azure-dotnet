namespace AgenticRagApp.Indexing.Pdf.Models;

// One interactive form field from a PDF's AcroForm, as read by
// PdfNativeMetadataExtractor.TryGetForm.
// - PartialName is the field's own name segment (PdfPig's
//   AcroFieldCommonInformation.PartialName - not the fully-qualified dotted name,
//   since that requires walking the Parent chain PdfPig only exposes as an indirect
//   reference, not resolved).
// - AlternateName is the human-readable label/tooltip a form-fill UI would show
//   ("Patient Name") - usually far more useful for QA than PartialName, which is
//   often an internal id ("Text1", "Field_a3f2").
// - MappingName is the export-value name used when the form's data is submitted/exported.
// - FieldFlags is PdfPig's own AcroFieldBase.FieldFlags bitmask (read-only, required,
//   etc. per the PDF spec's field-flag bit table) - kept as the raw uint since decoding
//   individual bits isn't needed for anything today.
// - FieldType is PdfPig's own AcroFieldType.ToString() (Text/CheckBox/RadioButton/
//   ListBox/ComboBox/PushButton/Signature) - kept as a string here so this model
//   doesn't need to reference PdfPig's own enum type.
// - Value is a normalized text representation of whatever the field-type-specific
//   subclass actually carries - AcroTextField.Value as-is; a checkbox/radio button as
//   its CurrentValue when checked/selected, else null; a list/combo box as its
//   SelectedOptions joined with "; ". Push buttons and signature fields have no
//   value-bearing subclass data, so this is always null for those two.
public sealed record AcroFormField(
    string? PartialName, string? AlternateName, string? MappingName,
    string FieldType, uint FieldFlags, int? PageNumber, string? Value);
