using Aevatar.AI.Abstractions;
using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Application.Services;
using Aevatar.AppPlatform.Hosting.Invocation;
using Aevatar.AppPlatform.Infrastructure.Stores;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.AppPlatform.Tests;

public class WorkflowAppFunctionRuntimeInvocationPortTests
{
    [Fact]
    public async Task TryInvokeAsync_WhenWorkflowCompletes_ShouldAdvanceOperationToCompleted()
    {
        var interactionService = new StubWorkflowInteractionService();
        var operationStore = new InMemoryOperationStore();
        var commandPort = new OperationCommandApplicationService(operationStore);
        var queryPort = new OperationQueryApplicationService(operationStore);
        var port = new WorkflowAppFunctionRuntimeInvocationPort(
            interactionService,
            commandPort,
            NullLogger<WorkflowAppFunctionRuntimeInvocationPort>.Instance);

        string? operationId = null;
        var accepted = await port.TryInvokeAsync(
            CreateTarget(),
            new AppFunctionInvokeRequest
            {
                Payload = Any.Pack(new ChatRequestEvent
                {
                    Prompt = "hello",
                    ScopeId = "scope-dev",
                }),
                CommandId = "cmd-workflow",
                CorrelationId = "corr-workflow",
            },
            async (runtimeAccepted, ct) =>
            {
                var operation = await commandPort.AcceptAsync(
                    new AppOperationSnapshot
                    {
                        Kind = AppOperationKind.FunctionInvoke,
                        Status = AppOperationStatus.Accepted,
                        AppId = "copilot",
                        ReleaseId = "prod",
                        FunctionId = "default-chat",
                        ServiceId = "chat-gateway",
                        EndpointId = "chat",
                        RequestId = runtimeAccepted.RequestId,
                        TargetActorId = runtimeAccepted.TargetActorId,
                        CommandId = runtimeAccepted.CommandId,
                        CorrelationId = runtimeAccepted.CorrelationId,
                        CreatedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc)),
                    },
                    ct);
                operationId = operation.OperationId;
                return operation.OperationId;
            });

        accepted.Should().NotBeNull();
        await interactionService.Completed;
        operationId.Should().NotBeNullOrWhiteSpace();

        var snapshot = await queryPort.GetAsync(operationId!);
        var result = await queryPort.GetResultAsync(operationId!);
        var events = await queryPort.ListEventsAsync(operationId!);

        snapshot.Should().NotBeNull();
        snapshot!.Status.Should().Be(AppOperationStatus.Completed);
        result.Should().NotBeNull();
        result!.Status.Should().Be(AppOperationStatus.Completed);
        result.ResultCode.Should().Be("workflow.completed");
        result.Payload.Unpack<StringValue>().Value.Should().Be("done");
        events.Should().Contain(x => x.EventCode == "workflow.run_started");
        events.Should().Contain(x => x.EventCode == "workflow.completed");
    }

    private static AppFunctionExecutionTarget CreateTarget() =>
        new(
            new AppDefinitionSnapshot
            {
                AppId = "copilot",
                OwnerScopeId = "scope-dev",
                DefaultReleaseId = "prod",
            },
            new AppReleaseSnapshot
            {
                AppId = "copilot",
                ReleaseId = "prod",
                Status = AppReleaseStatus.Published,
            },
            new AppEntryRef
            {
                EntryId = "default-chat",
                ServiceId = "chat-gateway",
                EndpointId = "chat",
            },
            new AppServiceRef
            {
                TenantId = "scope-dev",
                AppId = "copilot",
                Namespace = "prod",
                ServiceId = "chat-gateway",
                RevisionId = "r1",
                ImplementationKind = AppImplementationKind.Workflow,
                Role = AppServiceRole.Entry,
            },
            PrimaryActorId: "service-actor-1",
            DeploymentId: "deploy-1",
            ActiveRevisionId: "r1");

    private sealed class StubWorkflowInteractionService
        : ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
            WorkflowChatRunRequest command,
            Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            var receipt = new WorkflowChatRunAcceptedReceipt(
                ActorId: "workflow-run-1",
                WorkflowName: "chat",
                CommandId: "cmd-workflow",
                CorrelationId: "corr-workflow");

            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            await emitAsync(
                new WorkflowRunEventEnvelope
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    RunStarted = new WorkflowRunStartedEventPayload
                    {
                        RunId = "run-1",
                        ThreadId = "thread-1",
                    },
                },
                ct);

            await emitAsync(
                new WorkflowRunEventEnvelope
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    RunFinished = new WorkflowRunFinishedEventPayload
                    {
                        ThreadId = "thread-1",
                        Result = Any.Pack(new StringValue { Value = "done" }),
                    },
                },
                ct);

            _completed.TrySetResult();
            return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>.Success(
                receipt,
                new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Completed,
                    Completed: true));
        }
    }
}
