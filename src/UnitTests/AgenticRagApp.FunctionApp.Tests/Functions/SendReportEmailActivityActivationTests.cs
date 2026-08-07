using Azure.Storage.Blobs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Functions.ReportEmail;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Functions;

// Regression test for a production incident (2026-08-07): SendReportEmailActivity's
// constructor originally took a plain (unkeyed) BlobContainerClient parameter, which is never
// registered anywhere in this app - every consumer of "pipeline-reports" builds one from
// BlobServiceClient inside its own factory closure instead. That failed on every invocation
// with "Unable to resolve service for type 'Azure.Storage.Blobs.BlobContainerClient'".
//
// The first attempt at a fix registered the class itself in Program.cs
// (services.AddSingleton<SendReportEmailActivity>(sp => new SendReportEmailActivity(...))) and
// had NO effect in production - the isolated-worker Functions host activates [Function]
// classes via ActivatorUtilities.CreateInstance(scopedProvider, functionType), which resolves
// each CONSTRUCTOR PARAMETER directly from the container and never consults a registration
// made for the class type itself. A quick "does this build" or "can I `new` it up with mocks"
// unit test would NOT have caught either bug, since both let you construct the class by hand
// with whatever you feel like passing in.
//
// This test instead calls ActivatorUtilities.CreateInstance the same way the real host does,
// against a service collection shaped like Program.cs's actual registrations (BlobServiceClient
// registered, no unkeyed BlobContainerClient) - so a constructor parameter that regresses to an
// unresolvable type fails a build here, not only in production.
[TestClass]
public class SendReportEmailActivityActivationTests
{
    [TestMethod]
    public void ActivatorUtilities_CanActivate_TheSameWayTheFunctionsHostDoes()
    {
        var services = new ServiceCollection();

        // Mirrors Program.cs: BlobServiceClient registered, no unkeyed BlobContainerClient -
        // if SendReportEmailActivity's constructor ever regresses to asking for one directly,
        // this call throws exactly the exception seen in production.
        services.AddSingleton(new Mock<IBlobStore>().Object);
        services.AddSingleton(new BlobServiceClient("UseDevelopmentStorage=true"));
        services.AddSingleton(new Mock<IChatClient>().Object);
        services.AddSingleton(new Mock<IReportEmailSender>().Object);
        services.AddSingleton(new ReportEmailOptions());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();

        // Concrete (non-interface) dependencies - Moq can't mock sealed classes, so these are
        // real instances built the same way, one level down: BlobContainerClient itself is
        // never registered, only ever built from BlobServiceClient inline.
        services.AddSingleton(sp => new RunReportAssembler(
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-reports"),
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("eval-results"),
            sp.GetRequiredService<ReportEmailOptions>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<RunReportAssembler>()));
        services.AddSingleton(sp => new RunAnalysisAgent(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<RunAnalysisAgent>()));
        services.AddSingleton<RunEmailRenderer>();

        using var provider = services.BuildServiceProvider();

        // The actual call the Functions Worker host makes to construct a [Function] class -
        // NOT `new SendReportEmailActivity(...)`, which would trivially pass regardless of
        // whether the real DI graph can satisfy it.
        var instance = ActivatorUtilities.CreateInstance<SendReportEmailActivity>(provider);

        Assert.IsNotNull(instance);
    }
}
