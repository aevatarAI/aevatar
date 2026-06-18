using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

internal sealed class SkillRunnerCronSchedulePort : ISkillRunnerCronSchedulePort
{
    private const string PublisherActorId = "scheduled.skill-runner.schedule";

    private readonly IScheduledDispatchApplicationService _scheduledDispatches;

    public SkillRunnerCronSchedulePort(IScheduledDispatchApplicationService scheduledDispatches)
    {
        _scheduledDispatches = scheduledDispatches ?? throw new ArgumentNullException(nameof(scheduledDispatches));
    }

    public Task EnsureAsync(
        string agentId,
        InitializeSkillRunnerCommand command,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(command);
        if (NormalizeScheduleMode(command.ScheduleMode) != SkillRunnerScheduleMode.Cron)
            return Task.CompletedTask;

        return _scheduledDispatches.EnsureAsync(CreateConfiguration(agentId, command), ct);
    }

    public Task EnableAsync(
        string agentId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _scheduledDispatches.EnableAsync(BuildScheduleId(agentId), reason ?? string.Empty, ct);
    }

    public Task DisableAsync(
        string agentId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return _scheduledDispatches.DisableAsync(BuildScheduleId(agentId), reason ?? string.Empty, ct);
    }

    internal static string BuildScheduleId(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return $"skill-runner:{agentId.Trim()}";
    }

    private static ScheduledDispatchConfiguration CreateConfiguration(
        string agentId,
        InitializeSkillRunnerCommand command)
    {
        var normalizedAgentId = agentId.Trim();
        var scheduleId = BuildScheduleId(normalizedAgentId);
        var triggerEnvelope = new EventEnvelope
        {
            Id = $"{scheduleId}:trigger",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new TriggerSkillRunnerExecutionCommand { Reason = "schedule" }),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, normalizedAgentId),
            Propagation = new EnvelopePropagation { CorrelationId = scheduleId },
        };

        return new ScheduledDispatchConfiguration(
            scheduleId,
            ResolveDisplayName(command),
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                ActorId: normalizedAgentId,
                Envelope: triggerEnvelope),
            command.ScheduleCron?.Trim() ?? string.Empty,
            NormalizeTimezone(command.ScheduleTimezone),
            command.Enabled,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduledDispatchScheduleKind.SkillRunner);
    }

    private static SkillRunnerScheduleMode NormalizeScheduleMode(SkillRunnerScheduleMode mode) =>
        mode == SkillRunnerScheduleMode.OneShot
            ? SkillRunnerScheduleMode.OneShot
            : SkillRunnerScheduleMode.Cron;

    private static string ResolveDisplayName(InitializeSkillRunnerCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.TemplateName))
            return command.TemplateName.Trim();
        if (!string.IsNullOrWhiteSpace(command.SkillName))
            return command.SkillName.Trim();
        if (!string.IsNullOrWhiteSpace(command.SkillRef?.Name))
            return command.SkillRef.Name.Trim();

        return "SkillRunner";
    }

    private static string NormalizeTimezone(string? timezone) =>
        string.IsNullOrWhiteSpace(timezone)
            ? SkillRunnerDefaults.DefaultTimezone
            : timezone.Trim();
}
