namespace AgenticRagApp.Indexing.Pdf.Models;

// RowSpan/ColumnSpan are null for a regular single-cell entry and only set on a cell
// that merges multiple rows/columns - without them, a merged header cell looks like a
// missing cell to anything reconstructing the table layout downstream.
public sealed record TableCellInfo(int RowIndex, int ColumnIndex, string Kind, string Content, int? RowSpan, int? ColumnSpan);
