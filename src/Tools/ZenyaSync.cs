#:project ../AgenticRagApp.Infrastructure/AgenticRagApp.Infrastructure.csproj
#:package Microsoft.Extensions.Hosting
#:property PublishAot=false

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
//   2  not authenticated: Zenya answered /users/me as Anonymous (D173 2a) - nothing was synced
//
// 2026-09-15: this was the project AgenticRagApp.Tools.ZenyaSync until it became a .NET 10
// file-based app - the same 50 lines with no .csproj and no solution entry, so the launcher
// stops showing up as a project next to the real ones. `dotnet run src/Tools/ZenyaSync.cs`
// restores and builds it; the #:project directive supplies Infrastructure and #:package takes
// its version from Directory.Packages.props (central package management rejects a version on
// the directive). Its restore graph is locked by src/Tools/packages.lock.json, the same
// guarantee the project had.
//
// 2026-09-21: `#:property PublishAot=false` above is load-bearing - do not drop it. A file-based
// app's implicit project defaults to PublishAot=true, which pulls in Microsoft.DotNet.ILCompiler
// AND makes the restore graph runtime-specific. Both are fatal to a COMMITTED lock file, because
// the lock then records the machine that generated it:
//   - a lock written on Windows carries net10.0/win-x64 and nothing else, so the Linux hosted
//     agent fails with "project's runtime identifiers: linux-x64, lock file's: win-x64";
//   - ILCompiler's version tracks the SDK, so a lock pinning 10.0.9 breaks the moment the agent's
//     floating 10.0.x rolls to 10.0.12 - which is exactly how this surfaced, on the 2026-09-21
//     run, as NU1004 on both counts at once.
// Nothing here is ever AOT-published (the pipeline does `dotnet run`), so turning it off costs
// nothing and makes the lock platform- and SDK-patch-neutral: one net10.0 section, no ILCompiler.
// If the lock ever needs regenerating: `dotnet restore src/Tools/ZenyaSync.cs --force-evaluate`,
// then confirm it still has no "/win-x64" (or any other RID) section before committing.
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
Console.WriteLine($"  metadata dropped  {result.MetadataDropped}");
Console.WriteLine($"  harvested         {result.Harvested}");
Console.WriteLine($"  harvest failed    {result.HarvestFailed}");
Console.WriteLine($"  bytes downloaded  {result.BytesDownloaded}");
foreach (var (ext, count) in result.WrittenByExtension.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  written .{ext,-6} {count}");
foreach (var failure in result.Failures)
    Console.WriteLine($"  FAILED {failure.DocumentId} [{failure.Stage}] '{failure.Title}': {failure.Error}");

return result.Failed > 0 ? 1 : 0;
