using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Extensions.Schedules.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Extensions.Schedules.Tests;

public sealed class WorkflowSelfRescheduleModuleTests
{
    [Fact]
    public async Task HandleAsync_WithValidParameters_ShouldEnsureScheduleAndPublishAcceptedReceipt()
    {
        var port = new RecordingWorkflowScheduleCommandPort();
        var module = new WorkflowSelfRescheduleModule(port);
        var context = new RecordingWorkflowContext();
        var request = CreateRequest();

        await module.HandleAsync(CreateEnvelope(request), context, CancellationToken.None);

        port.Configurations.Should().ContainSingle();
        var configuration = port.Configurations.Single();
        configuration.ScheduleId.Should().Be("schedule-1");
        configuration.CronExpression.Should().Be("*/15 * * * *");
        configuration.Timezone.Should().Be("UTC");
        configuration.WorkflowName.Should().Be("daily");
        configuration.Prompt.Should().Be("scheduled prompt");
        configuration.ScopeId.Should().Be("scope-1");
        configuration.Headers.Should().Contain("trace", "enabled");
        configuration.Auth!.SenderNyxId.Should().BeEquivalentTo(
            new WorkflowScheduleNyxIdCredentialSource(
                new WorkflowScheduleNyxIdSubjectRef("lark", "tenant-a", "external-user-42"),
                "proxy"));
        configuration.Auth.ScopeOwnerNyxId.Should().BeNull();
        configuration.MutationContext.Should().BeNull();
        var completed = context.Published.Should().ContainSingle().Which.Event.Unpack<StepCompletedEvent>();
        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("schedule-1");
        completed.Annotations.Should().Contain("schedule_id", "schedule-1");
        completed.Annotations.Should().Contain("schedule_actor_id", "schedule-actor-1");
        completed.Annotations.Should().Contain("command_id", "command-1");
        completed.Annotations.Should().Contain("correlation_id", "correlation-1");
        completed.Annotations.Should().Contain("ack_stage", "accepted");
        completed.Annotations.Should().NotContainKey("projection_fresh");
    }

    [Fact]
    public async Task HandleAsync_WithAliasAndInputPrompt_ShouldBuildServiceTarget()
    {
        var port = new RecordingWorkflowScheduleCommandPort();
        var module = new WorkflowSelfRescheduleModule(port);
        var context = new RecordingWorkflowContext();
        var request = CreateRequest("schedule_workflow");
        request.Parameters.Remove("workflow_name");
        request.Parameters["service_id"] = "workflow-service";
        request.Parameters.Remove("prompt");
        request.Input = "input prompt";

        await module.HandleAsync(CreateEnvelope(request), context, CancellationToken.None);

        var configuration = port.Configurations.Should().ContainSingle().Which;
        configuration.WorkflowName.Should().BeEmpty();
        configuration.ServiceId.Should().Be("workflow-service");
        configuration.Prompt.Should().Be("input prompt");
        context.Published.Should().ContainSingle()
            .Which.Event.Unpack<StepCompletedEvent>().Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithEnabledFalse_ShouldDisableExistingDeterministicSchedule()
    {
        var port = new RecordingWorkflowScheduleCommandPort();
        var module = new WorkflowSelfRescheduleModule(port);
        var context = new RecordingWorkflowContext();
        var request = CreateRequest();
        request.Parameters["schedule_id"] = "firecrawl:job-1";
        request.Parameters["workflow_name"] = "firecrawl_agent_async_poll";
        request.Parameters["prompt"] = """{"job_id":"job-1","schedule_id":"firecrawl:job-1"}""";
        request.Parameters["enabled"] = "false";

        await module.HandleAsync(CreateEnvelope(request), context, CancellationToken.None);

        var configuration = port.Configurations.Should().ContainSingle().Which;
        configuration.ScheduleId.Should().Be("firecrawl:job-1");
        configuration.WorkflowName.Should().Be("firecrawl_agent_async_poll");
        configuration.Prompt.Should().Contain("\"job_id\":\"job-1\"");
        configuration.Enabled.Should().BeFalse();
        context.Published.Should().ContainSingle()
            .Which.Event.Unpack<StepCompletedEvent>().Success.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenUpdatingOwningSchedule_ShouldOmitAuthForActorOwnedPreservation()
    {
        var port = new RecordingWorkflowScheduleCommandPort();
        var module = new WorkflowSelfRescheduleModule(port);
        var context = new RecordingWorkflowContext
        {
            ScheduleId = "schedule-1",
            CallerNyxIdAuthority = null,
        };

        await module.HandleAsync(CreateEnvelope(CreateRequest()), context, CancellationToken.None);

        var configuration = port.Configurations.Should().ContainSingle().Which;
        configuration.Auth.Should().BeNull();
        configuration.MutationContext.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_WhenCreatingDifferentScheduleWithoutCallerAuthority_ShouldPublishFailure()
    {
        var port = new RecordingWorkflowScheduleCommandPort();
        var module = new WorkflowSelfRescheduleModule(port);
        var context = new RecordingWorkflowContext { CallerNyxIdAuthority = null };

        await module.HandleAsync(CreateEnvelope(CreateRequest()), context, CancellationToken.None);

        port.Configurations.Should().BeEmpty();
        var completed = context.Published.Should().ContainSingle().Which.Event.Unpack<StepCompletedEvent>();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be(
            "workflow caller NyxID authority is required to create a different schedule.");
    }

    [Fact]
    public async Task HandleAsync_WhenTargetScopeDiffersFromRunScope_ShouldPublishFailure()
    {
        var port = new RecordingWorkflowScheduleCommandPort();
        var module = new WorkflowSelfRescheduleModule(port);
        var context = new RecordingWorkflowContext { ScopeId = "scope-owner" };

        await module.HandleAsync(CreateEnvelope(CreateRequest()), context, CancellationToken.None);

        port.Configurations.Should().BeEmpty();
        var completed = context.Published.Should().ContainSingle().Which.Event.Unpack<StepCompletedEvent>();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("scope_id must match the workflow run scope.");
    }

    [Theory]
    [InlineData("schedule_id", "schedule_id is required.")]
    [InlineData("cron_expression", "cron_expression is required.")]
    [InlineData("scope_id", "scope_id is required.")]
    public async Task HandleAsync_WhenRequiredParameterMissing_ShouldPublishDeterministicFailure(
        string missingKey,
        string expectedError)
    {
        var port = new RecordingWorkflowScheduleCommandPort();
        var module = new WorkflowSelfRescheduleModule(port);
        var context = new RecordingWorkflowContext();
        var request = CreateRequest();
        request.Parameters.Remove(missingKey);

        await module.HandleAsync(CreateEnvelope(request), context, CancellationToken.None);

        port.Configurations.Should().BeEmpty();
        var completed = context.Published.Should().ContainSingle().Which.Event.Unpack<StepCompletedEvent>();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be(expectedError);
    }

    [Fact]
    public async Task HandleAsync_WhenWorkflowAndServiceTargetMissing_ShouldPublishFailure()
    {
        var port = new RecordingWorkflowScheduleCommandPort();
        var module = new WorkflowSelfRescheduleModule(port);
        var context = new RecordingWorkflowContext();
        var request = CreateRequest();
        request.Parameters.Remove("workflow_name");

        await module.HandleAsync(CreateEnvelope(request), context, CancellationToken.None);

        port.Configurations.Should().BeEmpty();
        var completed = context.Published.Should().ContainSingle().Which.Event.Unpack<StepCompletedEvent>();
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("workflow_name or service_id is required.");
    }

    private static StepRequestEvent CreateRequest(string stepType = "self_reschedule") =>
        new()
        {
            StepId = "step-1",
            RunId = "run-1",
            StepType = stepType,
            Input = "fallback prompt",
            Parameters =
            {
                ["schedule_id"] = " schedule-1 ",
                ["cron_expression"] = " */15 * * * * ",
                ["timezone"] = " UTC ",
                ["workflow_name"] = " daily ",
                ["prompt"] = " scheduled prompt ",
                ["scope_id"] = " scope-1 ",
                ["header.trace"] = " enabled ",
            },
        };

    private static EventEnvelope CreateEnvelope(IMessage payload) =>
        new()
        {
            Payload = Any.Pack(payload),
        };

    private sealed class RecordingWorkflowScheduleCommandPort : IWorkflowScheduleCommandPort
    {
        public List<WorkflowScheduleConfiguration> Configurations { get; } = [];

        public Task<WorkflowScheduleMutationReceipt> EnsureAsync(
            WorkflowScheduleConfiguration configuration,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Configurations.Add(configuration);
            return Task.FromResult(new WorkflowScheduleMutationReceipt(
                configuration.ScheduleId,
                "schedule-actor-1",
                true,
                "command-1",
                "correlation-1",
                DateTimeOffset.UtcNow,
                "accepted"));
        }
    }

    private sealed class RecordingWorkflowContext : IWorkflowExecutionContext
    {
        public List<(Any Event, TopologyAudience Direction)> Published { get; } = [];

        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "run-actor-1";

        public string ScopeId { get; init; } = "scope-1";

        public string ScheduleId { get; init; } = string.Empty;

        public WorkflowCallerNyxIdAuthority? CallerNyxIdAuthority { get; init; } = new()
        {
            Platform = "lark",
            Tenant = "tenant-a",
            ExternalUserId = "external-user-42",
            Scope = "proxy",
        };

        public IServiceProvider Services => EmptyServiceProvider.Instance;

        public ILogger Logger => NullLogger.Instance;

        public string RunId => "run-1";

        public TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() =>
            new();

        public IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() =>
            [];

        public Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> =>
            Task.CompletedTask;

        public Task ClearStateAsync(string scopeKey, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience direction = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((Any.Pack(evt), direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, 1, RuntimeCallbackBackend.InMemory));

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(System.Type serviceType) => null;
    }
}
