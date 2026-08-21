using System.Reflection;

namespace AgenticRagApp.Indexing.DI.Services;

// Identifies the build that produced an extraction run, so a run's own log line answers
// "which code produced this output?" without cross-referencing a deployment.
//
// That question matters more than it looks: extraction output changes with this assembly, not
// just with the PDFs. Within one working session it gained LineInfo collection, paired
// zero-body heading merging, heading depth, language detection and resolved section elements -
// every one of which changes the correct output for unchanged bytes. Comparing two runs'
// numbers without knowing whether the code moved underneath them is how you conclude the
// corpus changed when only the extractor did.
//
// Derived, never hand-maintained. The module version id is stamped in by the compiler and
// changes whenever this assembly's IL changes; .NET builds are deterministic by default, so
// identical sources give an identical id and a no-op rebuild doesn't look like a new build.
//
// This was originally the version half of a content-hash extraction cache key. That cache was
// removed (see PdfExtractionPipeline.LogContentHashOutcome for why) - the build identity is
// still worth stamping into the run log on its own.
public static class ExtractionVersion
{
    // Computed once per process; the assembly cannot change underneath a running host.
    public static string Current { get; } =
        typeof(ExtractionVersion).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    // Preferred for logging: an informational version is legible to a human, where the module
    // id is a bare GUID. Falls back to the module id so the log line always identifies the
    // build, even in a project that sets no version attributes.
    public static string AssemblyVersion { get; } =
        typeof(ExtractionVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ExtractionVersion).Assembly.GetName().Version?.ToString()
        ?? Current;
}
