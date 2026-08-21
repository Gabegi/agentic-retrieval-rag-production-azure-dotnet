using Azure.AI.DocumentIntelligence;

namespace AgenticRagApp.Indexing.CU.Services;

// Cheapest possible language filter (chunking-signals-map.md §3c #3): the corpus is Dutch
// plus at least one English document, and the measured chars/token ratio differs enough
// between them that every size ceiling is silently wrong for whichever documents this
// misses. DI already detects this on every "prebuilt-layout" call and returns it as
// AnalyzeResult.Languages - a free signal already paid for and never read, same shape as
// the rest of Group A in pre-chunking-action-items.md. No heuristic needed on top of it.
internal static class LanguageDetectionHelper
{
    // A document can carry more than one DocumentLanguage entry (DI detects per-span, so a
    // mostly-Dutch document with one quoted English paragraph gets two entries) - the
    // dominant one is whichever locale's spans cover the most characters, not just the
    // first entry or the highest-confidence one (confidence is about certainty of a given
    // span's detection, not about how much of the document that language actually covers).
    public static string Detect(AnalyzeResult result)
    {
        var languages = result.Languages;
        if (languages is not { Count: > 0 }) return "nl";

        return languages
            .GroupBy(l => l.Locale, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Locale: g.Key, CoveredChars: g.SelectMany(l => l.Spans ?? []).Sum(s => s.Length)))
            .OrderByDescending(x => x.CoveredChars)
            .First().Locale;
    }
}
