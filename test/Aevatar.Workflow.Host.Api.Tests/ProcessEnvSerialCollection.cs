namespace Aevatar.Workflow.Host.Api.Tests;

[CollectionDefinition(ProcessEnvSerialCollection.Name, DisableParallelization = true)]
public sealed class ProcessEnvSerialCollection
{
    public const string Name = "ProcessEnvSerial";
}
