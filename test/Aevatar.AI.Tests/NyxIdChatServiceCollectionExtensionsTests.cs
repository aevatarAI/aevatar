using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Tools;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatServiceCollectionExtensionsTests
{
    [Fact]
    public void AddNyxIdChat_Default_ShouldDisableAssistantActionsWithoutStartupFetch()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>()
            .Should().NotContain(service =>
                service is NyxIdAssistantActionRegistryStartupService);
        var registry = provider.GetRequiredService<NyxIdAssistantActionRegistry>();
        registry.TryGetDefinition("service.connect", out _).Should().BeFalse();
        Action resolve = () => registry.ResolveCatalogServiceConnect("api-github");
        resolve.Should().Throw<NyxIdAssistantActionRegistryException>()
            .Which.Code.Should().Be("NYXID_ACTION_UNSUPPORTED");
    }

    [Fact]
    public void AddNyxIdChat_Default_ShouldDenyCanaryEffectFaultAuthorization()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<NyxIdChatCanaryEffectFaultOptions>();
        options.Enabled.Should().BeFalse();
        options.AllowedOwnerSubjects.Should().BeEmpty();
        provider.GetRequiredService<INyxIdChatCanaryEffectFaultAuthorizationPolicy>()
            .CanArm("ce646b72-dd49-4ea8-bc1e-8273672c102c")
            .Should().BeFalse();
    }

    [Fact]
    public void AddNyxIdChat_WhenAssistantActionsEnabled_ShouldRegisterStartupFetcher()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:AssistantActions:Enabled"] = "true",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdChat(configuration);

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType ==
            typeof(NyxIdAssistantActionRegistryStartupService));
    }

    [Fact]
    public void AddNyxIdChat_ShouldRegisterDefaultDisabledAgentProfileResolver()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(INyxIdChatAgentProfileResolver) &&
            descriptor.ImplementationType == typeof(DisabledNyxIdChatAgentProfileResolver));
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
            descriptor.ServiceType == typeof(IAgentProfileConnectedOperationSelector) &&
            descriptor.ImplementationFactory != null);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(AgentTurnToolCatalogMaterializer));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(AgentTurnToolCatalog));
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

    [Fact]
    public void AddNyxIdChat_ShouldRegisterAsynchronousTurnOperationPorts()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(INyxIdChatTurnOperationDispatchPort) &&
            descriptor.ImplementationType == typeof(NyxIdChatTurnOperationDispatchPort));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(INyxIdChatTurnOperationReconciliationPort) &&
            descriptor.ImplementationType ==
            typeof(AdmittedNyxIdChatTurnOperationReconciliationPort));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(INyxIdChatToolVerificationPort) &&
            descriptor.ImplementationType == typeof(NyxIdChatToolVerificationPort));
    }

    [Fact]
    public async Task AddNyxIdChat_WithoutCatalogQuery_ShouldFailPostconditionClosed()
    {
        var services = new ServiceCollection();
        services.AddNyxIdChat(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var result = await provider
            .GetRequiredService<INyxIdActionPostconditionPort>()
            .VerifyAsync(PostconditionInput());

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(NyxIdActionPostconditionPort.UnavailableCode);
        provider.GetServices<IHostedService>()
            .Should().NotContain(service =>
                service is NyxIdAssistantActionRegistryStartupService);
    }

    [Fact]
    public void AddNyxIdChat_WithCatalogQuery_ShouldComposeTypedPostconditionReader()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INyxIdAuthorizationCatalogQueryPort>(
            new MissingCatalogQueryPort());
        services.AddNyxIdChat(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<INyxIdActionPostconditionPort>()
            .Should().BeOfType<NyxIdActionPostconditionPort>();
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(INyxIdChatTurnOperationExecutor) &&
            descriptor.ImplementationType == typeof(NyxIdChatTurnOperationExecutor));
    }

    private static NyxIdChatActionPostconditionInput PostconditionInput() => new()
    {
        ScopeId = "scope-alpha",
        OwnerSubject = "owner-alpha",
        OriginTurnId = "turn-origin-alpha",
        ActionRequestId = "action-alpha",
        Action = NyxIdAssistantActionKind.ServiceConnect,
        ReportedDisposition = NyxIdChatActionDisposition.Completed,
        Params = new NyxIdAssistantActionParams
        {
            CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
            {
                ServiceSlug = "api-github",
            },
        },
    };

    private sealed class MissingCatalogQueryPort : INyxIdAuthorizationCatalogQueryPort
    {
        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default) => Task.FromResult<NyxIdAuthorizationCatalogSnapshot?>(null);
    }

}
