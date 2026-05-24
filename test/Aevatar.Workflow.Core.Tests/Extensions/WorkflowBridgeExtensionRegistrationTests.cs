using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Extensions.Bridge;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Extensions;

public sealed class WorkflowBridgeExtensionRegistrationTests
{
    [Theory]
    [InlineData("workflow.telegram-bridge", typeof(TelegramBridgeGAgent))]
    [InlineData("workflow.telegram-user-bridge", typeof(TelegramUserBridgeGAgent))]
    [InlineData("workflow.telegram-wait-reply", typeof(TelegramWaitReplyGAgent))]
    public void AddWorkflowBridgeExtensions_ShouldRegisterStableAgentKinds(
        string agentKind,
        Type expectedImplementationType)
    {
        var services = new ServiceCollection();

        services.AddWorkflowBridgeExtensions();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentKindRegistry>();

        var implementation = registry.Resolve(agentKind);

        implementation.Metadata.Kind.Should().Be(agentKind);
        implementation.Metadata.ImplementationClrTypeName.Should().Be(expectedImplementationType.FullName);
    }

    [Fact]
    public void AddWorkflowBridgeExtensions_ShouldRegisterTelegramGetUpdatesExternalLinkTransportFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectorRegistry, EmptyConnectorRegistry>();
        services.AddSingleton<ILogger<TelegramGetUpdatesExternalLinkTransport>>(
            NullLogger<TelegramGetUpdatesExternalLinkTransport>.Instance);

        services.AddWorkflowBridgeExtensions();

        using var provider = services.BuildServiceProvider();

        var factories = provider.GetServices<IExternalLinkTransportFactory>();
        var factory = factories.Should().ContainSingle(x =>
            x.CanCreate(TelegramGetUpdatesExternalLinkTransport.TransportTypeName)).Subject;
        factory.CanCreate("TELEGRAM-GET-UPDATES").Should().BeTrue();
        factory.Create().Should().BeOfType<TelegramGetUpdatesExternalLinkTransport>();
    }

    private sealed class EmptyConnectorRegistry : IConnectorRegistry
    {
        public void Register(IConnector connector)
        {
            _ = connector;
        }

        public bool TryGet(string name, out IConnector? connector)
        {
            _ = name;
            connector = null;
            return false;
        }

        public IReadOnlyList<string> ListNames() => [];
    }
}
