using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Workflow.Extensions.Bridge;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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
}
