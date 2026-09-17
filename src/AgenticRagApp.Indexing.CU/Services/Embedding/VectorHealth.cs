namespace AgenticRagApp.Indexing.CU.Services;

// The "is this vector trustworthy" verdict, shared by the three places a vector is judged: read
// back from the cache (VectorCacheGateway), returned by the embedding API (BatchEmbedder), and
// about to be published to the index (UploadService). Lives on its own rather than on any one
// caller so none owns a check the others depend on.
//
// Classify is what makes that sharing real. Until 2026-09-17 the three sites each ran the same
// two checks inline against their own injected dimension count, in an order documented only by a
// comment in one of them - the same predicate by convention. The order is now a property of this
// function, and there is only one.
public static class VectorHealth
{
    // WrongWidth beats everything, and the precedence is load-bearing rather than cosmetic: a
    // zero-length vector is BOTH wrong-width and vacuously all-zero, and reporting it as Empty
    // would say "the model returned a useless vector" about what is actually a shape mismatch -
    // pointing whoever reads the flag at the deployment instead of at the dimension config. It
    // also keeps one bad vector from reading as two defects, which is what the meters count on.
    //
    // NonFinite beats Empty for the same reason in the other direction: a vector carrying a NaN
    // is not merely unmatchable, it cannot be SENT (see VectorVerdict), and that is the more
    // urgent fact about it. One pass rather than two predicates, because the two questions read
    // the same components.
    public static VectorVerdict Classify(float[] vector, int expectedDimensions)
    {
        ArgumentNullException.ThrowIfNull(vector);

        if (vector.Length != expectedDimensions) return VectorVerdict.WrongWidth;

        var allZero = true;
        foreach (var component in vector)
        {
            if (!float.IsFinite(component)) return VectorVerdict.NonFinite;
            if (component != 0f) allZero = false;
        }

        return allZero ? VectorVerdict.Empty : VectorVerdict.Healthy;
    }

    // The absent-vector case, which Classify cannot produce and must not: it throws on null, and
    // a null vector has no verdict. UploadService still has to meter it, because "no vector at
    // all" is a distinct thing to see on a dashboard from "a vector we judged bad". Expressed as
    // a nullable overload rather than a NoVector enum member, so the enum keeps meaning exactly
    // "what Classify concluded" and no switch over it has to handle a case Classify never returns.
    public static KeyValuePair<string, object?> VerdictTag(VectorVerdict? verdict) =>
        verdict is { } v ? VerdictTag(v) : new("verdict", "no_vector");

    // The telemetry tag value for a verdict, defined once so the sites that meter these cannot
    // drift apart on spelling - the tag is what a dashboard filters on, and two spellings of
    // "non_finite" would read as two different things. Switch expression with CS8524 suppressed
    // and CS8509 left live, for the reason spelled out at BatchEmbedder's switch.
    public static KeyValuePair<string, object?> VerdictTag(VectorVerdict verdict) =>
#pragma warning disable CS8524
        new("verdict", verdict switch
        {
            VectorVerdict.Healthy    => "healthy",
            VectorVerdict.WrongWidth => "wrong_width",
            VectorVerdict.NonFinite  => "non_finite",
            VectorVerdict.Empty      => "empty",
        });
#pragma warning restore CS8524
}

// What Classify can conclude about a vector.
//
// Empty and NonFinite were ONE verdict until 2026-09-17, inherited from IsEmptyVector, which
// answered "can this vector ever match a query" and returned true for both. D199 C1 measured that
// they behave oppositely everywhere else and the grouping was split (D199 A0.5) before anything
// was built on top of it:
//
//   Empty     - uploads perfectly cleanly. Azure.Search.Documents serialises all-zero without
//               complaint; whether Search then STORES it is D199 C2, unmeasured and not
//               measurable through this project's pipeline-only workflow. Withholding it is a
//               policy decision taken on an open question.
//   NonFinite - never reaches the service at all. The SDK's JSON writer throws ArgumentException
//               on NaN and ±Infinity before the request is sent, killing the whole upload batch
//               (up to 1,000 chunks) and then the run. Withholding it prevents a hard failure
//               that would take every healthy document in that batch with it.
//
// One count and one log line cannot say which of those happened, which is the entire reason they
// are separate values here. Not persisted anywhere, so the numeric order is free to read well.
public enum VectorVerdict
{
    // Right width, every component finite, at least one non-zero.
    Healthy,

    // Not the configured embedding width at all. Checked first - see Classify.
    WrongWidth,

    // Right width, but carrying a NaN or an infinity. Cannot be serialised, let alone indexed.
    NonFinite,

    // Right width, finite, and every component zero. Indexes clean and matches nothing.
    Empty,
}
