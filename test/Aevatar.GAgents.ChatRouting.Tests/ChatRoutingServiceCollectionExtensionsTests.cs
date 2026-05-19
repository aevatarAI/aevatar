using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatRouting.Tests;

/// <summary>
/// Guards the ChatRoutePolicy projection store registration. A missing
/// document store registration makes the readmodel silently never materialize,
/// so this is asserted explicitly rather than left to integration coverage.
/// </summary>
public sealed class ChatRoutingServiceCollectionExtensionsTests
{
    [Fact]
    public void AddChatRoutingAgents_RegistersChatRoutePolicyDocumentStore()
    {
        using var provider = new ServiceCollection()
            .AddChatRoutingAgents()
            .BuildServiceProvider();

        provider.GetService<IProjectionDocumentReader<ChatRoutePolicyCurrentStateDocument, string>>()
            .Should().NotBeNull("the readmodel cannot be queried without its document reader");
        provider.GetService<IProjectionDocumentWriter<ChatRoutePolicyCurrentStateDocument>>()
            .Should().NotBeNull("the projector cannot upsert without its document writer");
    }
}
