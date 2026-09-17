using System.Reflection;

namespace AgenticRagApp.Observability;

// Which binary produced a run. Added 2026-09-17 (D197 action 4) after a review of runs
// 9/260915/1 and 9/260917 could not tell whether a committed change had been deployed: the two
// reports were identical in shape, and the only evidence either way was arithmetic on the number
// under test. Two runs are comparable when this string matches and are a different experiment
// when it does not - that is the whole job.
//
// It reports BOTH halves on purpose:
//
//   - InformationalVersion is what a human wants (a version, and a commit sha once the build
//     stamps one). Nothing in this repo sets it today - there is no Version property in
//     src/Directory.Build.props, no SourceRevisionId and no SourceLink package - so it reads
//     "1.0.0" on every build until that changes. Reporting it alone would be a field that looks
//     like build identity and never varies.
//   - The module version id DOES vary: Roslyn writes a fresh MVID into every assembly it emits,
//     and deterministic builds (the SDK default) make it a function of the inputs - same source
//     and references give the same GUID, any code change gives a different one. Opaque to read,
//     but it is the half that actually answers "is this the same binary as yesterday's run".
//
// So the version half becomes useful the day the build stamps it, and the MVID half is useful
// now. Do not "simplify" this to the version alone without checking that the build sets one.
public static class BuildIdentity
{
    // Format: "1.0.0 (a1b2c3...)" - InformationalVersion, then the MVID as 32 hex digits.
    // Falls back to the MVID alone if an assembly carries no informational version at all.
    public static string For(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var mvid    = assembly.ManifestModule.ModuleVersionId.ToString("N");
        return string.IsNullOrWhiteSpace(version) ? mvid : $"{version} ({mvid})";
    }
}
