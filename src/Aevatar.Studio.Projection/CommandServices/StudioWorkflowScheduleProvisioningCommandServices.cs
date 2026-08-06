using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class ActorDispatchStudioWorkflowScheduleProvisioningCommandService
    : IStudioWorkflowScheduleProvisioningCommandPort
{
    private const string PublisherId = "aevatar.studio.projection.workflow-schedule-provisioning";
    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;

    public ActorDispatchStudioWorkflowScheduleProvisioningCommandService(
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
    }

    public async Task<StudioWorkflowScheduleProvisioningAcceptance> AcceptAsync(
        StudioWorkflowScheduleProvisioningIntent intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var actorId = StudioMemberConventions.BuildActorId(intent.ScopeId, intent.MemberId);
        var actor = await _bootstrap.EnsureAsync<StudioMemberGAgent>(actorId, ct);
        await _commandDispatch.DispatchAsync(
            actor,
            new StudioMemberWorkflowScheduleProvisioningRequested
            {
                Intent = ToActorIntent(intent),
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            PublisherId,
            ct: ct);
        return new StudioWorkflowScheduleProvisioningAcceptance(
            true,
            intent.ProvisioningId,
            StudioWorkflowScheduleProvisioningStatusNames.PendingBinding);
    }

    private static StudioMemberWorkflowScheduleProvisioningIntent ToActorIntent(
        StudioWorkflowScheduleProvisioningIntent intent)
    {
        var owner = intent.AuthenticatedOwner;
        return new StudioMemberWorkflowScheduleProvisioningIntent
        {
            ProvisioningId = intent.ProvisioningId,
            ScopeId = intent.ScopeId,
            TeamId = intent.TeamId,
            MemberId = intent.MemberId,
            PublishedServiceId = intent.PublishedServiceId,
            WorkflowId = intent.WorkflowId,
            RevisionId = intent.RevisionId,
            DisplayName = intent.DisplayName,
            Prompt = intent.Prompt,
            Owner = owner.Owner.Clone(),
            SubjectPlatform = owner.SubjectPlatform,
            SubjectTenant = owner.SubjectTenant,
            SubjectExternalUserId = owner.SubjectExternalUserId,
            VerifiedBindingId = owner.VerifiedBindingId,
            AuthenticatedActor = owner.AuthenticatedActor?.Clone(),
            ScheduleMode = intent.ScheduleMode switch
            {
                ScheduledDispatchScheduleMode.RecurringCron =>
                    StudioMemberWorkflowScheduleMode.RecurringCron,
                ScheduledDispatchScheduleMode.OneShotAtUtc =>
                    StudioMemberWorkflowScheduleMode.OneShotAtUtc,
                _ => StudioMemberWorkflowScheduleMode.Unspecified,
            },
            CronExpression = intent.CronExpression,
            Timezone = intent.Timezone,
            OneShotDelaySeconds = intent.OneShotDelaySeconds,
            BindingRunId = intent.BindingRunId ?? string.Empty,
            ScheduleOperationId = intent.ScheduleOperationId ?? string.Empty,
            ScheduleIdempotencyKey = intent.ScheduleIdempotencyKey ?? string.Empty,
        };
    }
}

internal sealed class StudioMemberWorkflowScheduleProvisioningExecutionPort
    : IStudioMemberWorkflowScheduleProvisioningPort
{
    private const string MemberDirectRoute = "aevatar.studio.projection.studio-member";
    private readonly IStudioWorkflowScheduleProvisioningExecutor _executor;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly ILogger<StudioMemberWorkflowScheduleProvisioningExecutionPort> _logger;

    public StudioMemberWorkflowScheduleProvisioningExecutionPort(
        IStudioWorkflowScheduleProvisioningExecutor executor,
        IActorDispatchPort dispatchPort,
        ILogger<StudioMemberWorkflowScheduleProvisioningExecutionPort> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<StudioMemberWorkflowScheduleProvisioningExecutionAccepted> ExecuteAsync(
        string replyActorId,
        StudioMemberWorkflowScheduleProvisioningIntent intent,
        DateTimeOffset? oneShotFireAt,
        int attempt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replyActorId);
        ArgumentNullException.ThrowIfNull(intent);
        _ = RunAndDispatchAsync(
            replyActorId,
            intent.Clone(),
            oneShotFireAt,
            attempt,
            CancellationToken.None);
        return Task.FromResult(new StudioMemberWorkflowScheduleProvisioningExecutionAccepted(
            intent.ProvisioningId,
            attempt));
    }

    private async Task RunAndDispatchAsync(
        string replyActorId,
        StudioMemberWorkflowScheduleProvisioningIntent intent,
        DateTimeOffset? oneShotFireAt,
        int attempt,
        CancellationToken ct)
    {
        IMessage continuation;
        try
        {
            var result = await _executor.ExecuteAsync(
                new StudioWorkflowScheduleProvisioningExecution(
                    ToApplicationIntent(intent),
                    oneShotFireAt),
                ct).ConfigureAwait(false);
            continuation = result.Success
                ? new StudioMemberWorkflowScheduleProvisioningSucceeded
                {
                    ProvisioningId = intent.ProvisioningId,
                    ScheduleId = result.ScheduleId ?? string.Empty,
                    OperationId = result.OperationId ?? string.Empty,
                    Attempt = attempt,
                    CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                }
                : result.Retryable
                    ? new StudioMemberWorkflowScheduleProvisioningRetryDeferred
                    {
                        ProvisioningId = intent.ProvisioningId,
                        Attempt = attempt,
                        FailureCode = result.FailureCode,
                        Detail = result.Detail,
                        DeferredAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    }
                    : BuildFailed(intent.ProvisioningId, attempt, result.FailureCode, result.Detail);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or StudioMemberAutomationPlanConflictException)
        {
            _logger.LogError(
                ex,
                "Workflow schedule provisioning attempt failed. provisioningId={ProvisioningId} memberId={MemberId} revisionId={RevisionId}",
                intent.ProvisioningId,
                intent.MemberId,
                intent.RevisionId);
            continuation = BuildFailed(
                intent.ProvisioningId,
                attempt,
                "workflow_schedule_provisioning_failed",
                ex.GetType().Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Workflow schedule provisioning attempt will retry. provisioningId={ProvisioningId} memberId={MemberId} revisionId={RevisionId} attempt={Attempt}",
                intent.ProvisioningId,
                intent.MemberId,
                intent.RevisionId,
                attempt);
            continuation = new StudioMemberWorkflowScheduleProvisioningRetryDeferred
            {
                ProvisioningId = intent.ProvisioningId,
                Attempt = attempt,
                FailureCode = "workflow_schedule_provisioning_attempt_failed",
                Detail = ex.GetType().Name,
                DeferredAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };
        }

        try
        {
            await DispatchContinuationAsync(replyActorId, continuation, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Workflow schedule provisioning continuation dispatch failed. provisioningId={ProvisioningId} replyActorId={ReplyActorId}",
                intent.ProvisioningId,
                replyActorId);
        }
    }

    private Task DispatchContinuationAsync(
        string replyActorId,
        IMessage continuation,
        CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(continuation),
            Route = EnvelopeRouteSemantics.CreateDirect(MemberDirectRoute, replyActorId),
        };
        return _dispatchPort.DispatchAsync(replyActorId, envelope, ct);
    }

    private static StudioWorkflowScheduleProvisioningIntent ToApplicationIntent(
        StudioMemberWorkflowScheduleProvisioningIntent intent) =>
        new(
            intent.ProvisioningId,
            intent.ScopeId,
            intent.TeamId,
            intent.MemberId,
            intent.PublishedServiceId,
            intent.WorkflowId,
            intent.RevisionId,
            intent.DisplayName,
            intent.Prompt,
            new AuthenticatedAuthorizationOwnerContext(
                intent.Owner.Clone(),
                intent.SubjectPlatform,
                intent.SubjectTenant,
                intent.SubjectExternalUserId,
                intent.VerifiedBindingId,
                intent.AuthenticatedActor?.Clone()),
            intent.ScheduleMode switch
            {
                StudioMemberWorkflowScheduleMode.RecurringCron =>
                    ScheduledDispatchScheduleMode.RecurringCron,
                StudioMemberWorkflowScheduleMode.OneShotAtUtc =>
                    ScheduledDispatchScheduleMode.OneShotAtUtc,
                _ => throw new InvalidOperationException("schedule provisioning mode is invalid."),
            },
            intent.CronExpression,
            intent.Timezone,
            intent.OneShotDelaySeconds,
            NormalizeOptional(intent.BindingRunId),
            NormalizeOptional(intent.ScheduleOperationId),
            NormalizeOptional(intent.ScheduleIdempotencyKey));

    private static StudioMemberWorkflowScheduleProvisioningFailed BuildFailed(
        string provisioningId,
        int attempt,
        string failureCode,
        string detail) =>
        new()
        {
            ProvisioningId = provisioningId,
            Attempt = attempt,
            Failure = new StudioMemberWorkflowScheduleProvisioningFailure
            {
                Code = string.IsNullOrWhiteSpace(failureCode)
                    ? "workflow_schedule_provisioning_failed"
                    : failureCode,
                Message = detail ?? string.Empty,
                FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }
}
