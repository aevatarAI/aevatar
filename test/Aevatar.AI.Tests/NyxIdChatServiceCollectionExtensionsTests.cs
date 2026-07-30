using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Tools;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatServiceCollectionExtensionsTests
{
    [Fact]
    public void AddNyxIdChat_ShouldRegisterDefaultDisabledAgentProfileSource()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(INyxIdChatAgentProfileSnapshotSource) &&
            descriptor.ImplementationType == typeof(DisabledNyxIdChatAgentProfileSnapshotSource));
    }

    [Fact]
    public void AddNyxIdChat_ShouldRegisterProfileConsumersWithoutServiceLevelCatalog()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IAgentProfileTurnClassifier) &&
            descriptor.ImplementationFactory != null);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(AgentProfileTurnCatalogMaterializer));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(AgentProfileTurnCatalog));
    }

    [Fact]
    public void AddNyxIdChat_ShouldNotRegisterRelayReplayGuard()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:Relay:CallbackReplayWindowSeconds"] = "420",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdChat(configuration);
        using var provider = services.BuildServiceProvider();

        services.Any(descriptor =>
                descriptor.ServiceType.FullName is { } name &&
                name.Contains("NyxIdRelayReplayGuard", StringComparison.Ordinal))
            .Should().BeFalse();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(NyxIdRelayAuthValidator));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolReceiptRenderer) &&
            descriptor.ImplementationType == typeof(AgentToolReceiptRenderer));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommandObservationScopeLeasePreparation<
                NyxIdChatCommand,
                NyxIdChatCommandTarget,
                NyxIdChatAcceptedReceipt,
                NyxIdChatStartError>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommandObservationScopeLeasePreparation<
                NyxIdApprovalCommand,
                NyxIdChatCommandTarget,
                NyxIdChatAcceptedReceipt,
                NyxIdChatStartError>));
    }

    [Fact]
    public void AddNyxIdChat_ShouldRegisterAdmittedToolExecutionPort()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IAgentToolExecutionPort) &&
            descriptor.ImplementationType == typeof(AdmittedAgentToolExecutor));
    }
}
