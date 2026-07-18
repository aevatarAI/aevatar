using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioInfrastructureServiceCollectionExtensionsTests
{
    [Fact]
    public void AddStudioInfrastructure_ShouldUseConversationActorAsContinuationAdmissionGate()
    {
        var services = new ServiceCollection();

        services.AddStudioInfrastructure(new ConfigurationBuilder().Build());

        var descriptor = services.Where(x => x.ServiceType == typeof(IChatConversationContinuationAdmissionReader))
            .Should()
            .ContainSingle()
            .Which;

        descriptor.ImplementationType.Should().Be(typeof(ChatConversationActorContinuationAdmissionReader));
    }
}
