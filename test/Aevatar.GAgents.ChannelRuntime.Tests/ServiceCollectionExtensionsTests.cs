using Aevatar.GAgents.Scheduled;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Channel;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.DependencyInjection;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Device;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddChannelRuntime_RegistersProviderNeutralRegistrationProjectionServices()
    {
        var services = new ServiceCollection();

        var result = services.AddChannelRuntime();
        services.AddNyxIdRelayChannel();
        services.AddLarkPlatform();
        services.AddChannelInteractiveReplyTools();
        services.AddTelegramPlatform();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelMessageComposerRegistry>();

        result.Should().BeSameAs(services);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ConversationDeliveryCurrentStateDocument>) &&
            descriptor.ImplementationType == typeof(ConversationDeliveryDocumentMetadataProvider));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionDocumentMetadataProvider<ProjectionScopeStatusDocument>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionScopeWatermarkQueryPort) &&
            descriptor.ImplementationType == typeof(ProjectionScopeStatusQueryPort));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.Name.Contains("AevatarSecretsStore", StringComparison.Ordinal));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationRuntimeQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IChannelBotRegistrationQueryByNyxIdentityPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IConversationDeliveryQueryPort) &&
            descriptor.ImplementationType == typeof(ConversationDeliveryQueryPort));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(INyxIdRelayScopeResolver));
        services.Any(descriptor =>
                descriptor.ServiceType.FullName is { } name &&
                name.Contains("NyxIdRelayReplayGuard", StringComparison.Ordinal))
            .Should().BeFalse();
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(ChannelBotRegistrationStartupService));
        AssertProjectionActivationProviderRegistered<ChannelBotRegistrationCommittedStateProjectionActivationPlanProvider>(
            services);
        AssertProjectionActivationProviderRegistered<ConversationDeliveryCommittedStateProjectionActivationPlanProvider>(
            services);
        services.Any(descriptor =>
                ContainsConversationDeliveryBootstrapActivatorName(descriptor.ServiceType.FullName) ||
                ContainsConversationDeliveryBootstrapActivatorName(descriptor.ImplementationType?.FullName) ||
                ContainsConversationDeliveryBootstrapActivatorName(descriptor.ImplementationInstance?.GetType().FullName) ||
                ContainsConversationDeliveryBootstrapActivatorName(descriptor.ImplementationFactory?.Method.ReturnType.FullName))
            .Should().BeFalse();
        // Refactor (iter20/cluster-003):
        //   Old pattern: Lark-local durable inbox subscriber worker stream path(orphan)
        //   New principle: delete orphan path,NyxID relay 唯一 ingress
        AssertNoRetiredLarkConversationInboxRegistration(services);
        // Refactor (iter36/cluster-042-channel-diagnostics-readmodel):
        //   Old pattern: AddChannelRuntime registered singleton process-local ChannelRuntimeDiagnostics as a queryable fact source.
        //   New principle: channel diagnostics are logs/metrics only unless backed by actor/projection readmodels; DI must not restore the retired singleton.
        AssertNoRetiredChannelRuntimeDiagnosticsRegistration(services);
        registry.Get(ChannelId.From("lark")).Should().BeOfType<LarkMessageComposer>();
        services.Count(descriptor => descriptor.ServiceType == typeof(IPlatformAdapter))
            .Should().Be(0);
        services.Count(descriptor => descriptor.ServiceType == typeof(INyxChannelBotProvisioningService))
            .Should().Be(2);
        provider.GetServices<IChannelNativeMessageSender>()
            .Select(sender => sender.GetType())
            .Should()
            .Contain(typeof(LarkChannelNativeMessageSender))
            .And
            .Contain(typeof(TelegramChannelNativeMessageSender));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ChannelRelayRegistrationFacade));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ChannelRegistrationCommandFacade));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommandDispatchService<ChannelBotRegisterCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>));
        registry.Get(ChannelId.From("telegram")).Should().BeOfType<Aevatar.GAgents.Platform.Telegram.TelegramMessageComposer>();
    }

    [Fact]
    public void AddNyxIdRelayChannel_ShouldNotOwnPlatformNativeMessageSenders()
    {
        var services = new ServiceCollection();

        services.AddNyxIdRelayChannel();

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IChannelNativeMessageSender));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(LarkChannelNativeMessageSender));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(TelegramChannelNativeMessageSender));
        services.Any(IsLarkOutboundRelayDispatcherDescriptor).Should().BeFalse();
    }

    [Fact]
    public void AddLarkAndTelegramPlatform_ShouldRegisterPlatformNativeMessageSenders()
    {
        var services = new ServiceCollection();

        services.AddLarkPlatform();
        services.AddTelegramPlatform();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(LarkChannelNativeMessageSender));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(TelegramChannelNativeMessageSender));
        services.Count(descriptor => descriptor.ServiceType == typeof(IChannelNativeMessageSender))
            .Should()
            .Be(2);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ILarkOutboundDispatcher) &&
            descriptor.ImplementationType == typeof(LarkOutboundDispatcher));
    }

    [Fact]
    public void AddNyxIdChat_ShouldNotRegisterVoiceDemoBootstrapCommandSurface()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());

        services.Any(ContainsVoiceDemoRegistration).Should().BeFalse();
        GetOptionalSourcePath("agents", "Aevatar.GAgents.NyxidChat", "IVoiceDemoAgentCommandPort.cs")
            .Should()
            .BeNull();
        GetOptionalSourcePath("agents", "Aevatar.GAgents.NyxidChat", "VoiceDemoAgentCommandPort.cs")
            .Should()
            .BeNull();
    }

    private static bool ContainsVoiceDemoRegistration(ServiceDescriptor descriptor)
    {
        var serviceTypeName = descriptor.ServiceType.FullName;
        var implementationTypeName = descriptor.ImplementationType?.FullName;
        return serviceTypeName?.Contains("VoiceDemo", StringComparison.Ordinal) == true ||
               implementationTypeName?.Contains("VoiceDemo", StringComparison.Ordinal) == true;
    }

    private static bool IsLarkOutboundRelayDispatcherDescriptor(ServiceDescriptor descriptor)
    {
        var serviceTypeName = descriptor.ServiceType.FullName;
        var implementationTypeName = descriptor.ImplementationType?.FullName;
        return serviceTypeName?.Contains("LarkOutboundRelayDispatcher", StringComparison.Ordinal) == true ||
               implementationTypeName?.Contains("LarkOutboundRelayDispatcher", StringComparison.Ordinal) == true;
    }

    [Fact]
    public void AddDeviceRegistration_RegistersDeviceCommandFacades()
    {
        var services = new ServiceCollection();

        var result = services.AddDeviceRegistration();

        result.Should().BeSameAs(services);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(DeviceRegistrationCommandFacade));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IDeviceCallbackCommandService) &&
            descriptor.ImplementationType == typeof(DeviceCallbackCommandFacade));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommandDispatchService<DeviceRegisterCommand, DeviceCommandAcceptedReceipt, DeviceRegistrationCommandStartError>));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommandDispatchService<DeviceCallbackDispatchCommand, DeviceCommandAcceptedReceipt, DeviceCallbackCommandStartError>));
        AssertProjectionActivationProviderRegistered<DeviceRegistrationCommittedStateProjectionActivationPlanProvider>(
            services);
    }

    [Fact]
    public void AddScheduledAgents_RegistersCommittedStateProjectionActivationProvider()
    {
        var services = new ServiceCollection();

        services.AddScheduledAgents();

        AssertProjectionActivationProviderRegistered<UserAgentCatalogCommittedStateProjectionActivationPlanProvider>(
            services);
    }

    [Fact]
    public void AddChannelIdentity_RegistersCommittedStateProjectionActivationProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NyxIdBrokerOptions.ResourceServerBaseUrlConfigurationKey] =
                    " https://api.example.test/// ",
            })
            .Build();

        services.AddChannelIdentity(configuration);
        using var provider = services.BuildServiceProvider();

        AssertProjectionActivationProviderRegistered<ChannelIdentityCommittedStateProjectionActivationPlanProvider>(
            services);
        provider.GetRequiredService<IOptions<NyxIdBrokerOptions>>()
            .Value.ResourceServerBaseUrl.Should().Be("https://api.example.test");
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(AevatarOAuthClientEsAclStartupGuard));
    }

    [Fact]
    public void AddChannelRuntime_RegistersLarkInteractiveReplyProducer_SoDispatcherCanFindIt()
    {
        var services = new ServiceCollection();

        services.AddChannelRuntime();
        services.AddNyxIdRelayChannel();
        services.AddLarkPlatform();
        services.AddChannelInteractiveReplyTools();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelMessageComposerRegistry>();

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IInteractiveReplyDispatcher));
        provider.GetRequiredService<IInteractiveReplyCollector>().Should().NotBeNull();
        registry.GetNativeProducer(ChannelId.From("lark")).Should().BeOfType<LarkChannelNativeMessageProducer>();
        registry.Get(ChannelId.From("lark")).Should().BeOfType<LarkMessageComposer>();
    }

    [Fact]
    public void AddNyxIdRelayChannel_ShouldReplaceInteractionNotificationPortWithChannelNeutralRelay()
    {
        var services = new ServiceCollection();
        var existingNotificationPort = Substitute.For<IChannelInteractionNotificationPort>();
        services.AddSingleton(existingNotificationPort);

        services.AddNyxIdRelayChannel();

        services.Where(descriptor => descriptor.ServiceType == typeof(IChannelInteractionNotificationPort))
            .Should()
            .ContainSingle()
            .Which.ImplementationType
            .Should()
            .Be(typeof(NyxIdRelayChannelInteractionNotificationPort));
    }

    [Fact]
    public void AddNyxIdRelayChannel_ShouldReplaceRemoteApprovalNotificationPortWithChannelRelay()
    {
        var services = new ServiceCollection();
        var existingNotificationPort = Substitute.For<IRemoteToolApprovalNotificationPort>();
        services.AddSingleton(existingNotificationPort);

        services.AddNyxIdRelayChannel();

        services.Where(descriptor => descriptor.ServiceType == typeof(IRemoteToolApprovalNotificationPort))
            .Should()
            .ContainSingle()
            .Which.ImplementationType
            .Should()
            .Be(typeof(NyxIdRelayRemoteToolApprovalNotificationPort));
    }

    [Fact]
    public void AddNyxIdRelayChannel_ShouldReplaceNyxIdChatTailTextFallback()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());
        services.AddNyxIdRelayChannel();
        services.AddLarkPlatform();

        services.Where(descriptor => descriptor.ServiceType == typeof(IChannelRelayTailTextSender))
            .Should()
            .ContainSingle()
            .Which.ImplementationFactory
            .Should()
            .NotBeNull();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IChannelRelayTailTextSender>()
            .Should()
            .BeOfType<LarkChannelRelayTailTextSender>();
    }

    private static void AssertNoRetiredLarkConversationInboxRegistration(IServiceCollection services)
    {
        services.Any(descriptor =>
            ContainsLarkConversationInboxName(descriptor.ServiceType.FullName) ||
            ContainsLarkConversationInboxName(descriptor.ImplementationType?.FullName))
            .Should().BeFalse();
    }

    private static bool ContainsLarkConversationInboxName(string? name) =>
        name is not null && name.Contains("LarkConversationInbox", StringComparison.Ordinal);

    private static void AssertNoRetiredChannelRuntimeDiagnosticsRegistration(IServiceCollection services)
    {
        services.Any(descriptor =>
            ContainsChannelRuntimeDiagnosticsName(descriptor.ServiceType.FullName) ||
            ContainsChannelRuntimeDiagnosticsName(descriptor.ImplementationType?.FullName) ||
            ContainsChannelRuntimeDiagnosticsName(descriptor.ImplementationInstance?.GetType().FullName) ||
            ContainsChannelRuntimeDiagnosticsName(descriptor.ImplementationFactory?.Method.ReturnType.FullName))
            .Should().BeFalse();
    }

    private static bool ContainsChannelRuntimeDiagnosticsName(string? name) =>
        name is not null && name.Contains("ChannelRuntimeDiagnostics", StringComparison.Ordinal);

    private static bool ContainsConversationDeliveryBootstrapActivatorName(string? name) =>
        name is not null && name.Contains("ConversationDeliveryProjectionBootstrapActivator", StringComparison.Ordinal);

    private static string? GetOptionalSourcePath(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            {
                directory = directory.Parent;
                continue;
            }

            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            return File.Exists(candidate) ? candidate : null;
        }

        return null;
    }

    private static void AssertProjectionActivationProviderRegistered<TProvider>(IServiceCollection services)
        where TProvider : IProjectionActivationPlanProvider
    {
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ProjectionActivationPlanDispatcher));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ICommittedStatePublicationHook) &&
            descriptor.ImplementationType == typeof(CommittedStateProjectionActivationHook));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IProjectionActivationPlanProvider) &&
            descriptor.ImplementationType == typeof(TProvider));
    }
}
