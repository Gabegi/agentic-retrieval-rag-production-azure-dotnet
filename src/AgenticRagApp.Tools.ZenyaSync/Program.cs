using AgenticRagApp.Infrastructure.Clients.Zenya;
using AgenticRagApp.Infrastructure.Clients.Zenya.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Thin launcher for ZenyaSyncService (D175 A6, D185): the ADO Sync stage runs this on the hosted
// agent because a library cannot be `dotnet run` and the Function App cannot reach Zenya until
// the hub firewall opens (D174). Everything real lives in Infrastructure/Clients/Zenya; this
// file only wires configuration (environment variables - the pipeline's env: block), asserts
// the Zenya identity, runs one sync and turns the result into an exit code:
//   0  every listed document handled (dry run or real)
//   1  the run completed but one or more documents failed - see the failure lines
//   2  not authenticated: Zenya answered /users/me as Anonymous (D173 §2a) - nothing was synced
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZenyaClient(builder.Configuration);
builder.Services.AddZenyaSync(builder.Configuration);
using var host = builder.Build();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var logger  = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ZenyaSync");
var options = host.Services.GetRequiredService<ZenyaSyncOptions>();
var zenya   = host.Services.GetRequiredService<IZenyaClient>();

try
{
    var user = await zenya.EnsureAuthenticatedAsync(cts.Token);
    logger.LogInformation("Authenticated to Zenya as '{Login}' ({UserType}). Target: {Account}/{Container}. Dry run: {DryRun}.",
        user.LoginCode, user.UserType, options.StorageAccountUrl, options.StorageContainer, options.DryRun);
}
catch (ZenyaAnonymousException ex)
{
    logger.LogError(ex, "Zenya answered /users/me as Anonymous - the token was not accepted. Nothing synced.");
    return 2;
}

var result = await host.Services.GetRequiredService<ZenyaSyncService>().RunAsync(cts.Token);

Console.WriteLine();
Console.WriteLine($"Zenya sync {(result.DryRun ? "DRY RUN" : "run")} - {result.Elapsed:g}");
Console.WriteLine($"  listed            {result.Listed}");
Console.WriteLine($"  new               {result.New}");
Console.WriteLine($"  changed           {result.Changed}");
Console.WriteLine($"  unchanged         {result.Unchanged}");
Console.WriteLine($"  removed           {result.Removed}");
Console.WriteLine($"  authored-skipped  {result.AuthoredSkipped}");
Console.WriteLine($"  not-downloadable  {result.NotDownloadable}");
Console.WriteLine($"  failed            {result.Failed}");
Console.WriteLine($"  foreign blobs     {result.ForeignBlobs}");
Console.WriteLine($"  pdf without %PDF  {result.PdfWithoutMagic}");
Console.WriteLine($"  bytes downloaded  {result.BytesDownloaded}");
foreach (var (ext, count) in result.WrittenByExtension.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  written .{ext,-6} {count}");
foreach (var failure in result.Failures)
    Console.WriteLine($"  FAILED {failure.DocumentId} [{failure.Stage}] '{failure.Title}': {failure.Error}");

return result.Failed > 0 ? 1 : 0;
