using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
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
    public async Task AddNyxIdChat_ShouldNotWireGlobalYieldApprovalIntoChannelReplyGenerator()
    {
        var tool = new ApprovalRequiredTool();
        var services = new ServiceCollection();
        services.AddSingleton<ILLMProviderFactory>(new ToolResultEchoingProviderFactory());
        services.AddSingleton<IAgentToolSource>(new SingleToolSource(tool));
        services.AddSingleton<IToolApprovalHandler, YieldApprovalHandler>();

        services.AddNyxIdChat(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var generator = provider.GetRequiredService<IConversationReplyGenerator>();

        var reply = await generator.GenerateReplyAsync(
            new ChatActivity
            {
                Id = "msg-channel-global-yield",
                Conversation = new ConversationReference { CanonicalKey = "lark:dm:user-global-yield" },
                Content = new MessageContent { Text = "run tool" },
            },
            new Dictionary<string, string>(),
            streamingSink: null,
            CancellationToken.None);

        reply.Text.Should().Contain("approval-gated tools cannot run here");
        reply.Text.Should().NotContain("An approval request has been sent.");
        reply.Text.Should().NotContain("\"approval_required\":true");
        tool.ExecuteCount.Should().Be(0);
    }

    private sealed class SingleToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
    }

    private sealed class ApprovalRequiredTool : IAgentTool
    {
        public const string ToolName = "approval_required_tool";

        public int ExecuteCount { get; private set; }

        public string Name => ToolName;

        public string Description => "Requires approval.";

        public string ParametersSchema => "{}";

        public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;

        public bool IsDestructive => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("""{"executed":true}""");
        }
    }

    private sealed class ToolResultEchoingProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "tool-result-echoing";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var toolResult = request.Messages.LastOrDefault(static message => message.Role == "tool")?.Content;
            if (toolResult is not null)
            {
                yield return new LLMStreamChunk { DeltaContent = toolResult };
                yield return new LLMStreamChunk { IsLast = true };
                await Task.CompletedTask;
                yield break;
            }

            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-approval",
                    Name = ApprovalRequiredTool.ToolName,
                    ArgumentsJson = "{}",
                },
            };
            yield return new LLMStreamChunk { IsLast = true };
            await Task.CompletedTask;
        }
    }
}
