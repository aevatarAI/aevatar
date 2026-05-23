using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChatConversationGAgentLifecycleBoundaryTests
{
    [Fact]
    public void ChatConversationGAgent_ShouldUseNarrowTopologyPort_NotRuntimeServiceLocator()
    {
        // Refactor (iter49/cluster-049-chat-history-index-side-lifecycle):
        //   Old pattern: ChatConversationGAgent resolved IActorRuntime via Services locator and created index actor inline during event handling.
        //   New principle: Index actor addressing/provisioning is a constructor-injected narrow domain port; ChatHistoryIndexGAgent created via topology setup, not inline event handling.
        var constructor = typeof(ChatConversationGAgent).GetConstructors()
            .Should().ContainSingle().Subject;

        constructor.GetParameters()
            .Should().ContainSingle(p => p.ParameterType == typeof(IChatHistoryIndexTopologyPort));

        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "agents",
            "Aevatar.GAgents.ChatHistory",
            "ChatConversationGAgent.cs"));

        source.Should().NotContain(nameof(ServiceProviderServiceExtensions.GetRequiredService));
        source.Should().NotContain(nameof(ServiceProviderServiceExtensions.GetService));
        source.Should().NotContain("CreateAsync<ChatHistoryIndexGAgent>");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
