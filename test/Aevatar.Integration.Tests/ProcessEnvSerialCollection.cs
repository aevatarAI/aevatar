namespace Aevatar.Integration.Tests;

[CollectionDefinition(ProcessEnvSerialCollection.Name, DisableParallelization = true)]
public sealed class ProcessEnvSerialCollection
{
    public const string Name = "ProcessEnvSerial";
}
