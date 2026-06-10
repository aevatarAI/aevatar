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
            WorkflowRuntimeContext = new WorkflowToolRuntimeContextPayload
            {
                ParentActorId = "parent-actor",
                ParentRunId = "parent-run",
                ParentStepId = "reply",
                RootRunId = "root-run",
                Depth = 2,
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
        provider.LastRequest.ToolContext.WorkflowRuntime.ParentActorId.Should().Be("parent-actor");
        provider.LastRequest.ToolContext.WorkflowRuntime.ParentRunId.Should().Be("parent-run");
        provider.LastRequest.ToolContext.WorkflowRuntime.ParentStepId.Should().Be("reply");
        provider.LastRequest.ToolContext.WorkflowRuntime.RootRunId.Should().Be("root-run");
        provider.LastRequest.ToolContext.WorkflowRuntime.Depth.Should().Be(2);
        provider.LastRequest.ToolContext.WorkflowRuntime.HasManagedParent.Should().BeTrue();
        (provider.LastRequest.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal))
            .Should()
            .BeEmpty();
        publisher.Published.OfType<WorkflowLlmInvocationCompletedEvent>()
            .Should()
            .ContainSingle(x => x.Success);
    }

    [Fact]
    public async Task WorkflowRoleGAgent_WhenToolReceiptCarriesManagedHandoff_ShouldPublishHandoffCompletion()
    {
        var provider = new RecordingLlmProvider
        {
            ToolReceipt = new AgentToolReceipt
            {
                CallId = "tool-call-1",
                ToolName = "aevatar_start_workflow",
                Status = AgentToolReceiptStatus.Success,
                ManagedWorkflowHandoff = new ManagedWorkflowHandoffReceipt
                {
                    ParentActorId = "parent-actor",
                    ParentRunId = "parent-run",
                    ParentStepId = "reply",
                    InvocationId = "parent-run:workflow_tool:reply:tool-call-1",
                    ChildRunId = "parent-run:workflow_tool:reply:tool-call-1",
                },
            },
        };
        var publisher = new RecordingEventPublisher();
        var agent = new WorkflowRoleGAgent(provider)
        {
            EventPublisher = publisher,
        };

        await agent.HandleWorkflowLlmExecutionIntent(new WorkflowLlmExecutionIntent
        {
            RunId = "parent-run",
            StepId = "reply",
            SessionId = "session-1",
            Prompt = "start child",
        });

        var completed = publisher.Published
            .OfType<WorkflowLlmInvocationCompletedEvent>()
            .Single(x => x.ManagedHandoff != null && !string.IsNullOrWhiteSpace(x.ManagedHandoff.InvocationId));
        completed.Success.Should().BeTrue();
        completed.ManagedHandoff.Should().NotBeNull();
        completed.ManagedHandoff.InvocationId.Should().Be("parent-run:workflow_tool:reply:tool-call-1");
        completed.ManagedHandoff.ParentStepId.Should().Be("reply");
    }

    [Fact]
    public async Task WorkflowRoleGAgent_ShouldMapWorkflowFileRefsToChatUriPartsWithoutBase64()
    {
        var provider = new RecordingLlmProvider();
        var publisher = new RecordingEventPublisher();
        var agent = new WorkflowRoleGAgent(provider)
        {
            EventPublisher = publisher,
        };
        var intent = new WorkflowLlmExecutionIntent
        {
            RunId = "run-files",
            StepId = "reply",
            SessionId = "session-files",
            Prompt = "describe this",
        };
        intent.InputFileRefs.Add(BuildWorkflowFileRef("file-role"));

        await agent.HandleWorkflowLlmExecutionIntent(intent);

        provider.LastRequest.Should().NotBeNull();
        var user = provider.LastRequest!.Messages.Should().ContainSingle().Subject;
        user.ContentParts.Should().NotBeNull();
        user.ContentParts!.Should().HaveCount(2);
        user.ContentParts[0].Kind.Should().Be(ContentPartKind.Text);
        user.ContentParts[0].Text.Should().Be("describe this");
        user.ContentParts[1].Kind.Should().Be(ContentPartKind.Image);
        user.ContentParts[1].Uri.Should().Be("workflow-file://file-role");
        user.ContentParts[1].MediaType.Should().Be("image/png");
        user.ContentParts[1].Name.Should().Be("file-role.png");
        user.ContentParts[1].DataBase64.Should().BeNull();
    }

    private sealed class RecordingLlmProvider : ILLMProviderFactory, ILLMProvider
    {
        public LLMRequest? LastRequest { get; private set; }

        public AgentToolReceipt? ToolReceipt { get; init; }

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
            if (ToolReceipt != null)
            {
                yield return new LLMStreamChunk
                {
                    ToolReceipt = ToolReceipt,
                };
            }

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

    private static WorkflowFileRef BuildWorkflowFileRef(string fileId) =>
        new()
        {
            FileId = fileId,
            ArtifactId = $"workflow-file://{fileId}",
            SourceKind = WorkflowFileSourceKind.ConnectedServiceResource,
            SourceMessageId = "om_1",
            SourceResourceKey = "image_key_1",
            FileName = $"{fileId}.png",
            MediaType = "image/png",
            SizeBytes = 3,
            Sha256 = $"sha-{fileId}",
            CreatedAtUnixMs = 1710000000000,
            ExpiresAtUnixMs = 1710003600000,
        };
}
