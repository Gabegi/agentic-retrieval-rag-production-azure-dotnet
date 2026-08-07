namespace AgenticRagApp.Infrastructure.Clients.Blob;

// Thrown by AssertContainerExistsAsync. A distinct type, not a bare InvalidOperationException,
// so a caller who genuinely wants to distinguish "the container isn't there yet" from every
// other blob failure can catch it specifically - and so this message (which always names the
// container and infra/storage.tf) can't be confused with any other exception's text.
public sealed class ContainerNotDeclaredException : Exception
{
    public string ContainerName { get; }

    public ContainerNotDeclaredException(string containerName)
        : base($"Blob container '{containerName}' does not exist. Every container this app writes "
             + $"to must be declared as an azurerm_storage_container resource in infra/storage.tf "
             + $"(or infra/function_app.tf for the func account) and applied before the app runs "
             + $"against it. If '{containerName}' should exist, check the resource's `name` argument "
             + $"matches exactly - a mismatch here previously caused the app to silently auto-create "
             + $"an unmanaged second container instead of failing.")
    {
        ContainerName = containerName;
    }
}
