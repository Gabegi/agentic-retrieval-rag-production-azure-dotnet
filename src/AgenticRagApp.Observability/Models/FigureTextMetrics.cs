namespace AgenticRagApp.Observability.Reports;

// How much of the run's chunk text is figure description, and how much of that is page-header /
// page-footer logo description (2026-09-15). Every such chunk still gets a full vector and an
// index slot, and a chunk that is mostly a logo's description can fill a result slot on a short
// query - retrieval noise, not a duplication problem (cu-figures-demonstration.md §5).
//
// Shares are figure characters over Content length, the basis D183 used for its 341 / 117 / 23,
// and the two cuts are D183's own (>= 50%, >= 90%). The header/footer subset is the "near-empty
// logo chunk" D183 counted 97 of by hand without a fixed threshold; here it gets the same two
// cuts so the number is reproducible. Measurement only - nothing is dropped. Routing header /
// footer descriptions out on FigureInfo.Role is D183's open item, and this is what would show it
// working: the header/footer counts should go to 0 while the body-figure counts stay.
public sealed record FigureTextMetrics(
    // Chunks that carried a figure-text count (stamped in step 4). Equal to ChunksProduced on the
    // PDF pipeline.
    int  Measured,
    int  ChunksWithFigureText,
    long FigureTextChars,
    int  ChunksOver50PctFigureText,
    int  ChunksOver90PctFigureText,
    int  ChunksWithHeaderFooterFigureText,
    long HeaderFooterFigureTextChars,
    int  ChunksOver50PctHeaderFooterFigureText,
    int  ChunksOver90PctHeaderFooterFigureText)
{
    public static FigureTextMetrics? From(IReadOnlyList<FigureTextSample> samples)
    {
        if (samples.Count == 0) return null;

        static double Share(int chars, int contentLength) => contentLength > 0 ? chars / (double)contentLength : 0d;

        return new FigureTextMetrics(
            Measured:                              samples.Count,
            ChunksWithFigureText:                  samples.Count(s => s.FigureChars > 0),
            FigureTextChars:                       samples.Sum(s => (long)s.FigureChars),
            ChunksOver50PctFigureText:             samples.Count(s => Share(s.FigureChars, s.ContentLength) >= 0.5),
            ChunksOver90PctFigureText:             samples.Count(s => Share(s.FigureChars, s.ContentLength) >= 0.9),
            ChunksWithHeaderFooterFigureText:      samples.Count(s => s.HeaderFooterChars > 0),
            HeaderFooterFigureTextChars:           samples.Sum(s => (long)s.HeaderFooterChars),
            ChunksOver50PctHeaderFooterFigureText: samples.Count(s => Share(s.HeaderFooterChars, s.ContentLength) >= 0.5),
            ChunksOver90PctHeaderFooterFigureText: samples.Count(s => Share(s.HeaderFooterChars, s.ContentLength) >= 0.9));
    }
}

// One chunk's contribution to FigureTextMetrics. HeaderFooterChars is a subset of FigureChars in
// practice (a logo's description is alt text too), but the two are counted independently.
public readonly record struct FigureTextSample(int FigureChars, int HeaderFooterChars, int ContentLength);
