using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WorkflowRoleGAgentMappingTests
{
    [Fact]
    public async Task WorkflowRoleGAgent_ShouldMapWorkflowCredentialAndRouteAtAiBoundary()
    {
        var provider = new RecordingLlmProvider();
        var publisher = new RecordingEventPublisher();
        var agent = new WorkflowRoleGAgent(provider)
        {
            EventPublisher = publisher,
        };

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "reply",
            SessionId = "session-1",
            Prompt = "hello",
            Model = "model-a",
            UserMemoryPrompt = "memory",
            RoutePreference = " route-a ",
            CallerCredential = new WorkflowCallerCredential
            {
                BearerToken = " raw-token ",
            },
        });

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.LlmControl.Should().NotBeNull();
        provider.LastRequest.LlmControl!.NyxIdAccessToken.Should().Be("raw-token");
        provider.LastRequest.LlmControl.NyxIdRoutePreference.Should().Be("route-a");
        provider.LastRequest.ToolContext.Should().NotBeNull();
        provider.LastRequest.ToolContext!.Credentials.NyxIdAccessToken.Should().Be("raw-token");
        provider.LastRequest.ToolContext.Credentials.NyxIdOrgToken.Should().Be("raw-token");
        provider.LastRequest.ToolContext.Routing.NyxIdRoutePreference.Should().Be("route-a");
        (provider.LastRequest.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal))
            .Should()
            .BeEmpty();
        publisher.Published.OfType<WorkflowLlmInvocationCompletedEvent>()
            .Should()
            .ContainSingle(x => x.Success);
    }

    [Fact]
    public async Task WorkflowRoleGAgent_ShouldMapWorkflowToolScopeToAiVisibility()
    {
        var provider = new RecordingLlmProvider();
        var publisher = new RecordingEventPublisher();
        var agent = new WorkflowRoleGAgent(provider)
        {
            EventPublisher = publisher,
        };

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "run-1",
            StepId = "reply",
            SessionId = "session-1",
            Prompt = "hello",
            AgentToolScope = new WorkflowAgentToolScope
            {
                AllowedToolNames = { "search" },
            },
        });

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.ToolContext.Should().NotBeNull();
        provider.LastRequest.ToolContext!.ToolVisibility.IsRestricted.Should().BeTrue();
        provider.LastRequest.ToolContext.ToolVisibility.Allows("search").Should().BeTrue();
        provider.LastRequest.ToolContext.ToolVisibility.Allows("calendar").Should().BeFalse();
    }

    private sealed class RecordingLlmProvider : ILLMProviderFactory, ILLMProvider
    {
        public LLMRequest? LastRequest { get; private set; }

        public string Name => "recording";

        public ILLMProvider GetProvider(string name)
        {
            _ = name;
            return this;
        }

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            await Task.Yield();
            yield return new LLMStreamChunk
            {
                DeltaContent = "ok",
            };
            yield return new LLMStreamChunk
            {
                IsLast = true,
                FinishReason = "stop",
            };
        }
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<IMessage> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = audience;
            _ = ct;
            _ = sourceEnvelope;
            _ = options;
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            return PublishAsync(evt, TopologyAudience.Children, ct, sourceEnvelope, options);
        }
    }
}
