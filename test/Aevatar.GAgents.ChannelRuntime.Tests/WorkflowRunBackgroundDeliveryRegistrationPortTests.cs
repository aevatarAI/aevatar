using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.WorkflowRunDelivery;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class WorkflowRunBackgroundDeliveryRegistrationPortTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 16, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task RegisterAsync_WhenDeliveryActorIsMissing_ShouldCreateDerivedActorAndDispatchStartRequest()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatchPort);
        var registration = Registration(
            deliveryId: string.Empty,
            workflowActorId: "workflow-actor",
            workflowCommandId: "workflow-command",
            workflowCorrelationId: string.Empty);
        const string expectedActorId = "workflow-run-delivery:workflow-actor:workflow-command";

        var receipt = await port.RegisterAsync(registration);

        runtime.ExistsCalls.Should().ContainSingle().Which.Should().Be(expectedActorId);
        runtime.CreatedActors.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowRunDeliveryGAgent), expectedActorId));
        dispatchPort.Dispatches.Should().ContainSingle();
        var dispatch = dispatchPort.Dispatches.Single();
        dispatch.ActorId.Should().Be(expectedActorId);
        AssertEnvelope(dispatch.Envelope, expectedActorId, "workflow-command");
        AssertStartRequest(dispatch.Envelope.Payload.Unpack<WorkflowRunDeliveryStartRequested>(), expectedActorId, registration);
        AssertReceipt(receipt, expectedActorId, registration);
    }

    [Fact]
    public async Task RegisterAsync_WhenDeliveryActorExists_ShouldReuseExplicitActorIdWithoutCreating()
    {
        var runtime = new RecordingActorRuntime();
        runtime.MarkExists("workflow-run-delivery:explicit");
        var dispatchPort = new RecordingActorDispatchPort();
        var port = CreatePort(runtime, dispatchPort);
        var registration = Registration(
            deliveryId: " workflow-run-delivery:explicit ",
            workflowCorrelationId: "workflow-correlation-1");

        var receipt = await port.RegisterAsync(registration);

        runtime.ExistsCalls.Should().ContainSingle().Which.Should().Be("workflow-run-delivery:explicit");
        runtime.CreatedActors.Should().BeEmpty();
        dispatchPort.Dispatches.Should().ContainSingle();
        var dispatch = dispatchPort.Dispatches.Single();
        dispatch.ActorId.Should().Be("workflow-run-delivery:explicit");
        AssertEnvelope(dispatch.Envelope, "workflow-run-delivery:explicit", "workflow-correlation-1");
        AssertStartRequest(
            dispatch.Envelope.Payload.Unpack<WorkflowRunDeliveryStartRequested>(),
            "workflow-run-delivery:explicit",
            registration);
        AssertReceipt(receipt, "workflow-run-delivery:explicit", registration);
    }

    private static WorkflowRunBackgroundDeliveryRegistrationPort CreatePort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort) =>
        new(
            runtime,
            dispatchPort,
            NullLogger<WorkflowRunBackgroundDeliveryRegistrationPort>.Instance,
            new FakeTimeProvider(Now));

    private static readonly ChannelWorkflowResultDeliveryCredential DeliveryCredential = new()
    {
        SecretReference = new Aevatar.Foundation.Abstractions.Credentials.SecretReference
        {
            Ref = "sec_workflow_delivery_1",
            Purpose = "channel.workflow-result-delivery-agent-key",
            OwnerScopeKey = "registration-scope-1",
        },
        SubjectId = "nyx-api-key-1",
    };

    private static WorkflowRunBackgroundDeliveryRegistration Registration(
        string deliveryId = "workflow-run-delivery:explicit",
        string workflowActorId = "workflow-actor",
        string workflowRunId = "workflow-run-1",
        string workflowCommandId = "workflow-command-1",
        string workflowCorrelationId = "workflow-correlation-1",
        string streamTopic = "aevatar://actors/workflow-actor/runs/workflow-command-1",
        string channelPlatform = "lark",
        string replyMessageId = "reply-message-1",
        string platformMessageId = "platform-message-1",
        string registrationScopeId = "registration-scope-1",
        string botRegistrationId = "bot-reg-1") =>
        new(
            DeliveryId: deliveryId,
            WorkflowActorId: workflowActorId,
            WorkflowRunId: workflowRunId,
            WorkflowCommandId: workflowCommandId,
            WorkflowCorrelationId: workflowCorrelationId,
            StreamTopic: streamTopic,
            ChannelPlatform: channelPlatform,
            ReplyMessageId: replyMessageId,
            PlatformMessageId: platformMessageId,
            WorkflowResultDeliveryCredential: DeliveryCredential.Clone(),
            RegistrationScopeId: registrationScopeId,
            BotRegistrationId: botRegistrationId);

    private static void AssertEnvelope(EventEnvelope envelope, string expectedActorId, string expectedCorrelationId)
    {
        envelope.Id.Should().NotBeNullOrWhiteSpace();
        envelope.Timestamp.ToDateTimeOffset().Should().Be(Now);
        envelope.Route.PublisherActorId.Should().Be("workflow-run-background-delivery-registration");
        envelope.Route.GetTargetActorId().Should().Be(expectedActorId);
        envelope.Propagation.CorrelationId.Should().Be(expectedCorrelationId);
        envelope.Runtime.Deduplication.OperationId.Should().Be($"workflow-run-delivery-start:{expectedActorId}");
    }

    private static void AssertStartRequest(
        WorkflowRunDeliveryStartRequested command,
        string expectedActorId,
        WorkflowRunBackgroundDeliveryRegistration registration)
    {
        command.DeliveryId.Should().Be(expectedActorId);
        command.WorkflowActorId.Should().Be(registration.WorkflowActorId);
        command.WorkflowRunId.Should().Be(registration.WorkflowRunId);
        command.WorkflowCommandId.Should().Be(registration.WorkflowCommandId);
        command.WorkflowCorrelationId.Should().Be(registration.WorkflowCorrelationId);
        command.StreamTopic.Should().Be(registration.StreamTopic);
        command.ChannelPlatform.Should().Be(registration.ChannelPlatform);
        command.ReplyMessageId.Should().Be(registration.ReplyMessageId);
        command.PlatformMessageId.Should().Be(registration.PlatformMessageId);
        command.RegistrationScopeId.Should().Be(registration.RegistrationScopeId);
        command.WorkflowResultDeliveryCredential.Should().Be(registration.WorkflowResultDeliveryCredential);
        command.BotRegistrationId.Should().Be(registration.BotRegistrationId);
    }

    private static void AssertReceipt(
        WorkflowRunBackgroundDeliveryReceipt receipt,
        string expectedActorId,
        WorkflowRunBackgroundDeliveryRegistration registration)
    {
        receipt.DeliveryActorId.Should().Be(expectedActorId);
        receipt.WorkflowActorId.Should().Be(registration.WorkflowActorId);
        receipt.WorkflowRunId.Should().Be(registration.WorkflowRunId);
        receipt.WorkflowCommandId.Should().Be(registration.WorkflowCommandId);
        receipt.WorkflowCorrelationId.Should().Be(registration.WorkflowCorrelationId);
        receipt.StreamTopic.Should().Be(registration.StreamTopic);
        receipt.ChannelPlatform.Should().Be(registration.ChannelPlatform);
        receipt.ReplyMessageId.Should().Be(registration.ReplyMessageId);
        receipt.PlatformMessageId.Should().Be(registration.PlatformMessageId);
        receipt.RegistrationScopeId.Should().Be(registration.RegistrationScopeId);
        // The receipt intentionally carries no credential material or handle.
        receipt.ToString().Should().NotContain("sec_workflow_delivery_1");
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly HashSet<string> _existingActorIds = new(StringComparer.Ordinal);
        public List<string> ExistsCalls { get; } = [];
        public List<(Type AgentType, string? Id)> CreatedActors { get; } = [];

        public void MarkExists(string actorId) => _existingActorIds.Add(actorId);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            CreatedActors.Add((typeof(TAgent), id));
            if (!string.IsNullOrWhiteSpace(id))
                _existingActorIds.Add(id);

            return Task.FromResult<IActor>(new NoopActor(id ?? string.Empty));
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreatedActors.Add((agentType, id));
            if (!string.IsNullOrWhiteSpace(id))
                _existingActorIds.Add(id);

            return Task.FromResult<IActor>(new NoopActor(id ?? string.Empty));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id)
        {
            ExistsCalls.Add(id);
            return Task.FromResult(_existingActorIds.Contains(id));
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Dispatches.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class NoopActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new NoopAgent(id);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
