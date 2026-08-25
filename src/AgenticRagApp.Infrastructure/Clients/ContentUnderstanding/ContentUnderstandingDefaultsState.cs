namespace AgenticRagApp.Infrastructure.Clients.ContentUnderstanding;

// What happened when ContentUnderstandingDefaultsSetup ran (or that it never did). Written at
// host startup, read by the extraction stage, which carries it into the run report's red flags.
//
// Exists because App Insights was the only place the startup check's outcome was visible, and
// diagnosing the first live runs stalled on exactly that: five identical "completion model not
// resolved" runs with no way to tell from the reports whether the mapping check ever executed,
// succeeded, or failed. The default value below is itself the diagnostic for "the deployed
// build predates the check, or hosted services did not run".
public sealed class ContentUnderstandingDefaultsState
{
    public string Summary { get; set; } = "startup check never ran (build predates it, or the host did not start it)";
}
