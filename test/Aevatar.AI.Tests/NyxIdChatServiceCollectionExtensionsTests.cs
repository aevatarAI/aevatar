using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.Core.AgentProfiles;
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
    public void AddNyxIdChat_WhenAssistantActionsEnabled_ShouldRegisterStrictStartupFetcher()
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
