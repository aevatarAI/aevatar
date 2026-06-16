using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

// Refactor (iter23/cluster-002):
//   Old pattern: Command ports synchronously activate projection scopes before dispatch and sometimes turn projection lease failure into command admission failure.
//   New principle: Command ports dispatch accepted commands; projection activation is owned by committed-state hooks, explicit observation binders, startup activators, or background materializers.
// Refactor (iter149/issue1132): Old pattern: scheduler command port used handled-dispatch to imply actor command handling.  New principle: scheduler command port returns after accepted-only actor dispatch.
internal sealed class SkillRunnerCommandPort : ISkillRunnerCommandPort
{
    private const string PublisherActorId = "scheduled.skill-runner";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly ISkillRunnerCronSchedulePort _cronSchedulePort;
    private readonly ISkillRunnerExecutionQueryPort _executionQueryPort;

    public SkillRunnerCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        ISkillRunnerCronSchedulePort cronSchedulePort,
        ISkillRunnerExecutionQueryPort executionQueryPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _cronSchedulePort = cronSchedulePort ?? throw new ArgumentNullException(nameof(cronSchedulePort));
        _executionQueryPort = executionQueryPort ?? throw new ArgumentNullException(nameof(executionQueryPort));
    }

    public async Task InitializeAsync(
        string agentId,
        InitializeSkillRunnerCommand command,
        bool runImmediately,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(command);

        await EnsureSkillRunnerActorAsync(agentId, ct);
        await DispatchAsync(agentId, command, ct);
        await _cronSchedulePort.EnsureAsync(agentId, command, ct);

        if (runImmediately)
        {
            await DispatchAsync(agentId, new TriggerSkillRunnerExecutionCommand { Reason = "create_agent" }, ct);
        }
    }

    public async Task TriggerAsync(string agentId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureSkillRunnerActorAsync(agentId, ct);
        await DispatchAsync(agentId, new TriggerSkillRunnerExecutionCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task DisableAsync(string agentId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureSkillRunnerActorAsync(agentId, ct);
        await DisableCronScheduleIfPresentAsync(agentId, reason ?? string.Empty, ct);
        await DispatchAsync(agentId, new DisableSkillRunnerCommand { Reason = reason ?? string.Empty }, ct);
    }

    public async Task EnableAsync(string agentId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        await EnsureSkillRunnerActorAsync(agentId, ct);
        await DispatchAsync(agentId, new EnableSkillRunnerCommand { Reason = reason ?? string.Empty }, ct);
        if (await IsCronInitializedAsync(agentId, ct))
            await _cronSchedulePort.EnableAsync(agentId, reason ?? string.Empty, ct);
    }

    public async Task<SkillRunnerExternalTriggerAdmissionReceipt> AdmitExternalTriggerAsync(
        string agentId,
        AdmitSkillRunnerExternalTriggerCommand command,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(command);

        var actor = await _actorRuntime.GetAsync(agentId);
        if (actor is null)
        {
            throw new SkillRunnerExternalTriggerAdmissionException(
                SkillRunnerExternalTriggerAdmissionError.RunnerNotFound,
                agentId);
        }

        NormalizeExternalTriggerAdmission(command);
        var admission = await DispatchAsync(agentId, command, ct);
        return new SkillRunnerExternalTriggerAdmissionReceipt(
            admission.ActorId,
            admission.CommandId,
            admission.CorrelationId,
            command.Identity.AdmissionId,
            command.Identity.SourceId,
            command.Identity.DeliveryId);
    }

    private async Task EnsureSkillRunnerActorAsync(string agentId, CancellationToken ct)
    {
        _ = await _actorRuntime.GetAsync(agentId)
            ?? await _actorRuntime.CreateAsync<SkillRunnerGAgent>(agentId, ct);
    }

    private async Task<bool> IsCronInitializedAsync(string agentId, CancellationToken ct)
    {
        var document = await _executionQueryPort.GetAsync(agentId, ct);
        return document?.ScheduleMode == SkillRunnerScheduleMode.Cron;
    }

    private async Task DisableCronScheduleIfPresentAsync(string agentId, string reason, CancellationToken ct)
    {
        try
        {
            await _cronSchedulePort.DisableAsync(agentId, reason, ct);
        }
        catch (ScheduledDispatchNotFoundException)
        {
        }
    }

    private async Task<DispatchAdmission> DispatchAsync<TCommand>(string agentId, TCommand command, CancellationToken ct)
        where TCommand : class, IMessage
    {
        var commandId = ResolveCommandId(command);
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, agentId),
            Propagation = new EnvelopePropagation { CorrelationId = commandId },
        };
        return await _actorDispatchPort.DispatchAsync(agentId, envelope, ct);
    }

    private static string ResolveCommandId<TCommand>(TCommand command)
        where TCommand : class, IMessage
    {
        if (command is AdmitSkillRunnerExternalTriggerCommand externalCommand &&
            !string.IsNullOrWhiteSpace(externalCommand.Identity?.AdmissionId))
        {
            return externalCommand.Identity.AdmissionId.Trim();
        }

        return Guid.NewGuid().ToString("N");
    }

    private static void NormalizeExternalTriggerAdmission(AdmitSkillRunnerExternalTriggerCommand command)
    {
        command.Identity ??= new SkillRunnerExternalTriggerIdentity();
        command.Identity.SourceId = command.Identity.SourceId?.Trim() ?? string.Empty;
        command.Identity.DeliveryId = command.Identity.DeliveryId?.Trim() ?? string.Empty;
        command.Identity.AdmissionId = string.IsNullOrWhiteSpace(command.Identity.AdmissionId)
            ? Guid.NewGuid().ToString("N")
            : command.Identity.AdmissionId.Trim();
        command.Identity.PayloadSummary = command.Identity.PayloadSummary?.Trim() ?? string.Empty;
        command.Identity.PayloadRef = command.Identity.PayloadRef?.Trim() ?? string.Empty;
        if (command.Identity.ReceivedAt is null)
            command.Identity.ReceivedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
    }
}
