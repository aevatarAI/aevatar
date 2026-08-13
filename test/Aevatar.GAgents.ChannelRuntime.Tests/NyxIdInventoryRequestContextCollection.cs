using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

internal static class ChannelRuntimeTestCollections
{
    public const string NyxIdInventoryRequestContext = "NyxID inventory request context";
}

[CollectionDefinition(ChannelRuntimeTestCollections.NyxIdInventoryRequestContext, DisableParallelization = true)]
public sealed class NyxIdInventoryRequestContextCollection
{
}
