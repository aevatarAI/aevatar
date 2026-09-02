using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioInfrastructureServiceCollectionExtensionsTests
{
    [Fact]
    public void AddStudioInfrastructure_ShouldUseConversationProjectionAsContinuationAdmissionGate()
    {
        var services = new ServiceCollection();

        services.AddStudioInfrastructure(new ConfigurationBuilder().Build());

        services.Where(x => x.ServiceType == typeof(IChatConversationContinuationAdmissionReader))
            .Should()
            .ContainSingle()
            .Which.ImplementationType.Should()
            .Be(typeof(ProjectionChatConversationContinuationAdmissionReader));
    }

    [Fact]
    public void AddStudioInfrastructure_ShouldRegisterNyxIdChatReadModelQueryPort()
    {
        var services = new ServiceCollection();

        services.AddStudioInfrastructure(new ConfigurationBuilder().Build());

        services.Where(x =>
                x.ServiceType == typeof(INyxIdChatConversationStateQueryPort))
            .Should()
            .ContainSingle()
            .Which.ImplementationType.Should()
            .Be(typeof(ProjectionNyxIdChatConversationStateQueryPort));
    }
}
