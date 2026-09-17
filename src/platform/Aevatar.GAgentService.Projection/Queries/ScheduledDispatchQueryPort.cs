using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ScheduledDispatchQueryPort : IScheduledDispatchQueryPort
{
    private readonly IProjectionDocumentReader<ScheduledDispatchDocument, string> _documentReader;

    public ScheduledDispatchQueryPort(IProjectionDocumentReader<ScheduledDispatchDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scheduleId))
            return null;

        var document = await _documentReader.GetAsync(scheduleId.Trim(), ct);
        return document == null ? null : MapDetail(document);
    }

    public Task<ScheduledDispatchListResult> ListAsync(
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default) =>
        ListAsync(new ScheduledDispatchListQuery(take, cursor, includeTotalCount), ct);

    public async Task<ScheduledDispatchListResult> ListAsync(
        ScheduledDispatchListQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = await _documentReader.QueryAsync(new ProjectionDocumentQuery
        {
            Take = Math.Clamp(query.Take, 1, 200),
            Cursor = query.Cursor,
            IncludeTotalCount = query.IncludeTotalCount,
            Filters = BuildFilters(query),
            AnyOfFilters = BuildAnyOfFilters(query),
        }, ct);

        return new ScheduledDispatchListResult(
            result.Items.Select(MapSummary).ToArray(),
            result.NextCursor,
            result.TotalCount);
    }

    private static ProjectionDocumentFilter[] BuildFilters(ScheduledDispatchListQuery query)
    {
        var filters = new List<ProjectionDocumentFilter>();
        if (!query.IncludeDeleted && !query.ExcludeCompletedTeamAutomationDeletions)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.Deleted),
                Operator = ProjectionDocumentFilterOperator.EqOrMissing,
                Value = ProjectionDocumentValue.FromBool(false),
            });
        }
        if (query.ExcludeTeamOwned)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.TeamOwned),
                Operator = ProjectionDocumentFilterOperator.EqOrMissing,
                Value = ProjectionDocumentValue.FromBool(false),
            });
        }
        if (query.TeamAutomationOwner != null || !string.IsNullOrWhiteSpace(query.TeamAutomationScopeId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = $"{nameof(ScheduledDispatchDocument.TeamAutomationOwner)}.{nameof(TeamMemberAutomationOwnerDocument.ScopeId)}",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(
                    query.TeamAutomationOwner?.ScopeId ?? query.TeamAutomationScopeId!.Trim()),
            });
            var teamId = query.TeamAutomationOwner?.TeamId ?? query.TeamAutomationTeamId?.Trim();
            if (!string.IsNullOrWhiteSpace(teamId))
            {
                filters.Add(new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.TeamId),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(teamId),
                });
            }
            var memberId = query.TeamAutomationOwner?.MemberId ?? query.TeamAutomationMemberId?.Trim();
            if (!string.IsNullOrWhiteSpace(memberId))
            {
                filters.Add(new ProjectionDocumentFilter
                {
                    FieldPath = $"{nameof(ScheduledDispatchDocument.TeamAutomationOwner)}.{nameof(TeamMemberAutomationOwnerDocument.MemberId)}",
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(memberId),
                });
            }
        }
        if (query.TargetKind != null)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.TargetKind),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.TargetKind.Value.ToString()),
            });
        }

        if (!string.IsNullOrWhiteSpace(query.ServiceEndpointId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.ServiceEndpointId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.ServiceEndpointId.Trim()),
            });
        }

        if (!string.IsNullOrWhiteSpace(query.ServiceKey))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.ServiceKey),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.ServiceKey.Trim()),
            });
        }

        if (!string.IsNullOrWhiteSpace(query.ServiceId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.ServiceId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.ServiceId.Trim()),
            });
        }

        if (!string.IsNullOrWhiteSpace(query.ServiceRevisionId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.ServiceRevisionId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.ServiceRevisionId.Trim()),
            });
        }

        if (query.ScheduleKind != null)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.ScheduleKind),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.ScheduleKind.Value.ToString()),
            });
        }

        return filters.ToArray();
    }

    private static ProjectionDocumentFilter[] BuildAnyOfFilters(ScheduledDispatchListQuery query)
    {
        if (!query.ExcludeCompletedTeamAutomationDeletions)
            return [];

        return
        [
            new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.Deleted),
                Operator = ProjectionDocumentFilterOperator.EqOrMissing,
                Value = ProjectionDocumentValue.FromBool(false),
            },
            new ProjectionDocumentFilter
            {
                FieldPath = nameof(ScheduledDispatchDocument.RevocationPending),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromBool(true),
            },
        ];
    }

    private static ScheduledDispatchDetail MapDetail(ScheduledDispatchDocument document) =>
        new(
            MapSummary(document),
            document.FireRecords
                .Select(MapFireRecord)
                .OrderByDescending(static x => x.CompletedAt)
                .ToArray());

    private static ScheduledDispatchSummary MapSummary(ScheduledDispatchDocument document)
    {
        var targetKind = ParseTargetKind(document.TargetKind);
        var scheduleKind = ParseScheduleKind(document.ScheduleKind);
        var credentialRequirementTargetKind =
            ParseCredentialRequirementTargetKind(document.CredentialRequirementTargetKind);
        if (credentialRequirementTargetKind == ScheduledDispatchCredentialRequirementTargetKind.Unspecified)
        {
            credentialRequirementTargetKind = ResolveCredentialRequirementTargetKind(targetKind, scheduleKind);
        }

        return new ScheduledDispatchSummary(
            document.ScheduleId,
            document.DisplayName ?? string.Empty,
            targetKind,
            document.TargetActorId ?? string.Empty,
            document.PayloadTypeUrl ?? string.Empty,
            document.ServiceKey ?? string.Empty,
            document.ServiceId ?? string.Empty,
            document.ServiceEndpointId ?? string.Empty,
            document.CronExpression ?? string.Empty,
            document.Timezone ?? string.Empty,
            document.Enabled,
            document.CreatedAt,
            document.UpdatedAt,
            document.NextFireAt,
            document.LastFireAt,
            document.LastTargetActorId ?? string.Empty,
            document.LastCommandId ?? string.Empty,
            document.LastCorrelationId ?? string.Empty,
            document.LastError ?? string.Empty,
            document.LastErrorCode ?? string.Empty,
            document.FireCount,
            document.FailureCount,
            document.Headers.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            document.ScheduleActorId ?? string.Empty,
            document.Prompt ?? string.Empty,
            scheduleKind,
            document.Deleted,
            document.OverdueFireDetectedCount,
            document.LastOverdueFireAt,
            credentialRequirementTargetKind,
            ParseCredentialSourceKind(document.CredentialSourceKind),
            ParseScheduleMode(document.ScheduleMode),
            document.OneShotFireAt,
            document.Completed,
            document.TeamOwned,
            document.TeamAutomationOwner?.ScopeId ?? string.Empty,
            document.TeamAutomationOwner?.MemberId ?? string.Empty,
            document.TeamId ?? string.Empty,
            ParseTeamAutomationLifecycleStatus(document.TeamAutomationLifecycleStatus),
            document.CredentialExpiresAt,
            document.TeamAutomationOperationId ?? string.Empty,
            document.CredentialGeneration,
            document.RevocationPending,
            document.LastAuthorizationErrorCode ?? string.Empty,
            document.StateVersion,
            document.PermissionDigest ?? string.Empty,
            document.PolicyVersion ?? string.Empty,
            document.TeamAutomationIdempotencyKey ?? string.Empty,
            document.ActiveCredentialOwner?.Authority ?? string.Empty,
            document.ActiveCredentialOwner?.OwnerKind ?? string.Empty,
            document.ActiveCredentialOwner?.OwnerSubject ?? string.Empty)
        {
            OwnerLLMRouteKind = string.IsNullOrEmpty(document.OwnerLlmRouteKind)
                ? "unspecified"
                : document.OwnerLlmRouteKind,
            OwnerLLMRoute = document.OwnerLlmRoute ?? string.Empty,
            OwnerLLMUserServiceId = document.OwnerLlmUserServiceId ?? string.Empty,
            OwnerLLMServiceSlug = document.OwnerLlmServiceSlug ?? string.Empty,
            OwnerLLMModel = document.OwnerLlmModel ?? string.Empty,
            ServiceRevisionId = document.ServiceRevisionId ?? string.Empty,
            ServiceIdentity = document.ServiceIdentity?.Clone() ?? new ServiceIdentity(),
            NyxIdRevocationStatus = document.NyxidRevocationStatus ?? string.Empty,
            VaultRevocationStatus = document.VaultRevocationStatus ?? string.Empty,
        };
    }

    private static ScheduledDispatchFireRecord MapFireRecord(ScheduledDispatchFireRecordDocument document) =>
        new(
            document.ScheduledFireAt,
            document.CompletedAt,
            document.IdempotencyKey ?? string.Empty,
            document.TargetActorId ?? string.Empty,
            document.CommandId ?? string.Empty,
            document.CorrelationId ?? string.Empty,
            document.Error ?? string.Empty,
            document.ErrorCode ?? string.Empty,
            document.Manual);

    private static ScheduledDispatchTargetKind ParseTargetKind(string? value) =>
        Enum.TryParse<ScheduledDispatchTargetKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ScheduledDispatchTargetKind.Envelope;

    private static ScheduledDispatchScheduleKind ParseScheduleKind(string? value) =>
        Enum.TryParse<ScheduledDispatchScheduleKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ScheduledDispatchScheduleKind.Generic;

    private static ScheduledDispatchCredentialRequirementTargetKind ParseCredentialRequirementTargetKind(
        string? value) =>
        Enum.TryParse<ScheduledDispatchCredentialRequirementTargetKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ScheduledDispatchCredentialRequirementTargetKind.Unspecified;

    private static ScheduledDispatchCredentialSourceKind ParseCredentialSourceKind(string? value) =>
        Enum.TryParse<ScheduledDispatchCredentialSourceKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ScheduledDispatchCredentialSourceKind.None;

    private static ScheduledDispatchCredentialRequirementTargetKind ResolveCredentialRequirementTargetKind(
        ScheduledDispatchTargetKind targetKind,
        ScheduledDispatchScheduleKind scheduleKind)
    {
        if (targetKind == ScheduledDispatchTargetKind.Envelope)
            return ScheduledDispatchCredentialRequirementTargetKind.Envelope;

        return targetKind == ScheduledDispatchTargetKind.ServiceInvocation &&
               scheduleKind == ScheduledDispatchScheduleKind.Workflow
            ? ScheduledDispatchCredentialRequirementTargetKind.WorkflowService
            : ScheduledDispatchCredentialRequirementTargetKind.Unspecified;
    }

    private static ScheduledDispatchScheduleMode ParseScheduleMode(string? value) =>
        Enum.TryParse<ScheduledDispatchScheduleMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ScheduledDispatchScheduleMode.RecurringCron;

    private static TeamAutomationLifecycleStatus ParseTeamAutomationLifecycleStatus(
        TeamAutomationLifecycleStatusDocument value) => value switch
    {
        TeamAutomationLifecycleStatusDocument.Unspecified => TeamAutomationLifecycleStatus.Unspecified,
        TeamAutomationLifecycleStatusDocument.ProvisioningPending =>
            TeamAutomationLifecycleStatus.ProvisioningPending,
        TeamAutomationLifecycleStatusDocument.Active => TeamAutomationLifecycleStatus.Active,
        TeamAutomationLifecycleStatusDocument.NeedsAuthorization =>
            TeamAutomationLifecycleStatus.NeedsAuthorization,
        TeamAutomationLifecycleStatusDocument.ReplacementPending =>
            TeamAutomationLifecycleStatus.ReplacementPending,
        TeamAutomationLifecycleStatusDocument.Deleting => TeamAutomationLifecycleStatus.Deleting,
        TeamAutomationLifecycleStatusDocument.RevocationPending =>
            TeamAutomationLifecycleStatus.RevocationPending,
        TeamAutomationLifecycleStatusDocument.Failed => TeamAutomationLifecycleStatus.Failed,
        _ => throw new InvalidOperationException(
            $"Unknown Team automation lifecycle status value '{(int)value}'."),
    };
}
