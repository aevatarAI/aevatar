namespace Aevatar.Hosting.Tests;

[CollectionDefinition(ProcessEnvSerialCollection.Name, DisableParallelization = true)]
public sealed class ProcessEnvSerialCollection
{
    public const string Name = "ProcessEnvSerial";
}
