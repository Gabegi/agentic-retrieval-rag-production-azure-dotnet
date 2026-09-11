using Microsoft.Extensions.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.Zenya.Sync;

// Where the sync writes and whether it writes at all. Keys match the env contract of
// .pipelines/base/zenya-document-sync.yml (Sync stage) so the pipeline and the host never
// disagree on names:
//   STORAGE_ACCOUNT_URL   https://<account>.blob.core.windows.net
//   STORAGE_CONTAINER     the dedicated target container (terraform: "zenya-documents",
//                         infra/storage.tf). Never the hand-uploaded "documents" corpus: the
//                         removal pass deletes every blob it does not recognise as current.
//   ZENYA_SYNC_DRY_RUN    true (default) = walk Zenya and the container, report what WOULD be
//                         written/removed, touch nothing. Absent = dry run; only an explicit
//                         false writes.
public sealed class ZenyaSyncOptions
{
    public const string StorageAccountUrlKey = "STORAGE_ACCOUNT_URL";
    public const string StorageContainerKey  = "STORAGE_CONTAINER";
    public const string DryRunKey            = "ZENYA_SYNC_DRY_RUN";

    public required Uri StorageAccountUrl { get; init; }
    public required string StorageContainer { get; init; }
    public bool DryRun { get; init; } = true;

    public static ZenyaSyncOptions FromConfiguration(IConfiguration configuration)
    {
        static string? Get(IConfiguration c, string key) =>
            string.IsNullOrWhiteSpace(c[key]) ? null : c[key]!.Trim();

        var missing = new[] { StorageAccountUrlKey, StorageContainerKey }.Where(k => Get(configuration, k) is null).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing required Zenya sync setting(s): {string.Join(", ", missing)}. " +
                "The pipeline sets these on the Sync step; locally use environment variables.");

        if (!Uri.TryCreate(Get(configuration, StorageAccountUrlKey), UriKind.Absolute, out var accountUrl))
            throw new InvalidOperationException($"{StorageAccountUrlKey} is not an absolute URL.");

        // ADO renders a boolean template parameter as "True"/"False"; bool.TryParse is
        // case-insensitive, so both spellings land. Anything unparsable is a config error, not
        // a silent "false" - that would turn a typo into a live write.
        var dryRun = true;
        if (Get(configuration, DryRunKey) is { } raw && !bool.TryParse(raw, out dryRun))
            throw new InvalidOperationException($"{DryRunKey} must be true or false, got '{raw}'.");

        return new ZenyaSyncOptions
        {
            StorageAccountUrl = accountUrl,
            StorageContainer  = Get(configuration, StorageContainerKey)!,
            DryRun            = dryRun,
        };
    }
}
