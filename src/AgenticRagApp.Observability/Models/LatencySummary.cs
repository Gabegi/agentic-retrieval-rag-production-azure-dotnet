namespace AgenticRagApp.Observability.Reports;

// A distribution of per-operation wall-clocks reduced to what a reader compares: how many, the
// median, the tail, the worst (2026-09-18, D203 §6c).
//
// Exists because the per-op histogram (Instrumentation.VectorCacheOpMs) exports to Azure
// Monitor only, and this project's workflow reads the run report from blob, not App Insights -
// nobody working on the pipeline has portal access to the metric, and Kudu LogFiles carry the
// host process only (D200 §6f). A number that exists solely where nobody can read it is not a
// measurement, so the same samples are summarised here and ride the report.
//
// Percentiles are nearest-rank over the sorted samples: P50 of 3,403 hits is the 1,702nd fastest.
// No interpolation, so every figure is a value that actually occurred. Null summary = no samples,
// never a zero - a run with no misses has no miss latency.
public sealed record LatencySummary(int Count, double P50Ms, double P95Ms, double MaxMs)
{
    public static LatencySummary? From(IReadOnlyCollection<double> samplesMs)
    {
        if (samplesMs.Count == 0) return null;

        var sorted = samplesMs.ToArray();
        Array.Sort(sorted);
        return new LatencySummary(
            Count: sorted.Length,
            P50Ms: Math.Round(NearestRank(sorted, 0.50), 1),
            P95Ms: Math.Round(NearestRank(sorted, 0.95), 1),
            MaxMs: Math.Round(sorted[^1], 1));
    }

    private static double NearestRank(double[] sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

// The four blob operations the vector cache makes against the store, each as a distribution
// (D203 M2a, on the report). The bracket is the SDK call alone: a GET's JSON parse is subtracted
// out (VectorCacheGateway), a DELETE is timed inside VectorCache. GetHit carries a ~39 KB body,
// GetMiss carries none - so GetHit.P50 against GetMiss.P50 is the payload's share of a round
// trip, which is the number D203 O2 (raw float32 instead of JSON) is decided on.
public sealed record VectorCacheOpLatency(
    LatencySummary? GetHit,
    LatencySummary? GetMiss,
    LatencySummary? Put,
    LatencySummary? Delete);
