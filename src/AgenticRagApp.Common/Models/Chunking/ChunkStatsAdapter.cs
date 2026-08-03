using System.Text.Json.Serialization;

namespace AgenticRagApp.Common.Models;

// Implements IChunkStatsSource so Observability's ChunkingStageMetrics.Compute can work
// generically without referencing this (or any other doc-type's) chunk type directly -
// see docs/260721 for why. Not ISnapshotSource - CSV doesn't use the rolling snapshot today.
public class ChunkStatsAdapter : IChunkStatsSource
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("document_id")]
    public string DocumentId { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("department")]
    public string? Department { get; set; }

    [JsonPropertyName("quick_code")]
    public string? QuickCode { get; set; }

    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; set; }

    [JsonPropertyName("last_modified_date")]
    public DateTimeOffset? LastModifiedDate { get; set; }

    [JsonPropertyName("check_date")]
    public DateTimeOffset? CheckDate { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("heading")]
    public string? Heading { get; set; }

    [JsonPropertyName("page_number")]
    public int PageNumber { get; set; }

    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("content_vector")]
    public float[]? ContentVector { get; set; }

    [JsonIgnore] public int  TokenEstimate => Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    [JsonIgnore] public bool IsEmpty       => string.IsNullOrWhiteSpace(Content);
    [JsonIgnore] public bool IsOversized   => TokenEstimate > 1024;
    [JsonIgnore] public bool IsUndersized  => TokenEstimate < 20;

    // Sentence boundary proxies — a coherent chunk starts and ends at natural boundaries
    [JsonIgnore] public bool StartsClean => Content.Length > 0 && (char.IsUpper(Content[0]) || char.IsDigit(Content[0]));
    [JsonIgnore] public bool EndsClean   => Content.Length > 0 && ".!?:)\"'".Contains(Content[^1]);
    [JsonIgnore] public bool IsCoherent  => StartsClean && EndsClean;

    // Content already includes the section heading (prepended by extraction services),
    // so keyword and vector signals are aligned. Summary lives in its own searchable/
    // semantic field (not in Content) so it doesn't repeat inside the stored text, but it's
    // folded in here so the vector embedding still carries that curated signal too — the
    // same benefit the summary field gives BM25/semantic ranking.
    [JsonIgnore] public string EmbeddingText =>
        string.IsNullOrWhiteSpace(Summary) ? Content : $"{Summary}\n\n{Content}";
}
