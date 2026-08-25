using System.Runtime.CompilerServices;

// Mirrors AgenticRagApp.Indexing.CU/AssemblyInfo.cs. Added for Utf16StringEncodingPolicy, which is
// internal (nothing outside this assembly should construct it - it is attached by
// ContentUnderstandingServiceVersion.Options()) but carries the one behaviour in the CU client
// that fails silently rather than loudly, so it must be directly testable.
[assembly: InternalsVisibleTo("AgenticRagApp.Infrastructure.Tests")]
