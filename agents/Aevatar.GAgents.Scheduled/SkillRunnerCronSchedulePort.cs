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
        // Scheduled dispatch reserves ':' as the actor-id namespace delimiter
        // (ScheduledDispatchActorId formats "scheduled-dispatch:<scheduleId>") so its
        // NormalizeScheduleId rejects schedule ids containing ':'. Use '.' to keep the
        // schedule id within the allowed [A-Za-z0-9._-] set; a ':' here made every
        // SkillRunner cron creation fail with ArgumentException -> initialize_failed.
        return $"skill-runner.{agentId.Trim()}";
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
            BuildHeaders(command),
            ScheduledDispatchScheduleKind.SkillRunner);
    }

    private static IReadOnlyDictionary<string, string> BuildHeaders(InitializeSkillRunnerCommand command)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ScheduledDispatchMetadataKeys.Origin] = "skill_runner",
            [ScheduledDispatchMetadataKeys.TargetKind] = "skill_runner",
        };

        AddIfPresent(headers, ScheduledDispatchMetadataKeys.ScopeId, command.ScopeId);
        AddIfPresent(headers, ScheduledDispatchMetadataKeys.TeamId, command.TeamId);
        AddIfPresent(headers, ScheduledDispatchMetadataKeys.MemberId, command.MemberId);
        AddIfPresent(headers, ScheduledDispatchMetadataKeys.TargetName, ResolveTargetName(command));

        var outbound = command.OutboundConfig;
        if (outbound != null)
        {
            AddIfPresent(headers, ScheduledDispatchMetadataKeys.LarkConversationId, outbound.ConversationId);
            AddIfPresent(headers, ScheduledDispatchMetadataKeys.LarkReceiveId, outbound.LarkReceiveId);
            AddIfPresent(headers, ScheduledDispatchMetadataKeys.LarkReceiveIdType, outbound.LarkReceiveIdType);
            AddIfPresent(headers, ScheduledDispatchMetadataKeys.LarkOutboundProviderSlug, outbound.NyxProviderSlug);
        }

        return headers;
    }

    private static string ResolveTargetName(InitializeSkillRunnerCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.SkillRef?.Name))
            return command.SkillRef.Name.Trim();
        if (!string.IsNullOrWhiteSpace(command.SkillName))
            return command.SkillName.Trim();

        return ResolveDisplayName(command);
    }

    private static void AddIfPresent(IDictionary<string, string> headers, string key, string? value)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
            headers[key] = normalized;
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
