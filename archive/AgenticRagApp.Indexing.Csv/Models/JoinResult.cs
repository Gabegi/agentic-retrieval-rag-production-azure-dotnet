namespace AgenticRagApp.Indexing.Csv.Models;

public class JoinResult
{
    private readonly List<JoinedPageRecord> _joined              = [];
    private readonly List<JoinError>        _errors              = [];
    private readonly List<JoinError>        _dataQualityWarnings = [];
    private readonly List<IndexRecord>      _skippedIndexRecords = [];

    public IReadOnlyList<JoinedPageRecord> Joined              => _joined;
    public IReadOnlyList<JoinError>        Errors              => _errors;              // page → no matching index record
    public IReadOnlyList<JoinError>        DataQualityWarnings => _dataQualityWarnings; // e.g. duplicate index DOCUMENT_ID
    public IReadOnlyList<IndexRecord>      SkippedIndexRecords => _skippedIndexRecords; // index docs with no pages — expected

    public int InactivePagesSkipped { get; private set; }

    internal void AddJoined(JoinedPageRecord r)       => _joined.Add(r);
    internal void AddToNotFound(JoinError e)               => _errors.Add(e);
    internal void AddToInactive(JoinError w)  => _dataQualityWarnings.Add(w);
    internal void AddToDuplicates(JoinError w) => _dataQualityWarnings.Add(w);
    internal void AddToIndexWithoutPages(IndexRecord r) => _skippedIndexRecords.Add(r);
    internal void CountInactivePageSkipped()           => InactivePagesSkipped++;
}
