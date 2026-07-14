using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Extensions.Schedules.Modules;

public sealed class WorkflowSelfRescheduleModule : IEventModule<IWorkflowExecutionContext>
{
    private const string CanonicalStepType = "self_reschedule";
    private const string AliasStepType = "schedule_workflow";
    private const string HeaderPrefix = "header.";
    private readonly IWorkflowScheduleCommandPort _scheduleCommandPort;

    public WorkflowSelfRescheduleModule(IWorkflowScheduleCommandPort scheduleCommandPort)
    {
        _scheduleCommandPort = scheduleCommandPort ?? throw new ArgumentNullException(nameof(scheduleCommandPort));
    }

    public string Name => CanonicalStepType;

    public int Priority => 6;

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (!IsScheduleStep(request.StepType))
            return;

        var parseResult = TryBuildConfiguration(request, ctx, out var configuration, out var error);
        if (!parseResult)
        {
            await PublishFailureAsync(ctx, request, error, ct);
            return;
        }

        try
        {
            var receipt = await _scheduleCommandPort.EnsureAsync(configuration!, ct);
            var completed = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = receipt.Accepted,
                Output = receipt.ScheduleId,
            };
            completed.Annotations["schedule_id"] = receipt.ScheduleId;
            completed.Annotations["schedule_actor_id"] = receipt.ScheduleActorId;
            completed.Annotations["command_id"] = receipt.CommandId;
            completed.Annotations["correlation_id"] = receipt.CorrelationId;
            completed.Annotations["ack_stage"] = receipt.AckStage;

            await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(
                ex,
                "Workflow schedule module failed to ensure schedule for run {RunId} step {StepId}.",
                request.RunId,
                request.StepId);
            await PublishFailureAsync(ctx, request, ex.Message, CancellationToken.None);
        }
    }

    private static bool IsScheduleStep(string stepType) =>
        string.Equals(stepType, CanonicalStepType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stepType, AliasStepType, StringComparison.OrdinalIgnoreCase);

    private static bool TryBuildConfiguration(
        StepRequestEvent request,
        IWorkflowExecutionContext ctx,
        out WorkflowScheduleConfiguration? configuration,
        out string error)
    {
        configuration = null;
        var scheduleId = GetRequired(request.Parameters, "schedule_id", out error);
        if (scheduleId == null)
            return false;

        var cronExpression = GetRequired(request.Parameters, "cron_expression", out error, "cron");
        if (cronExpression == null)
            return false;

        var scopeId = GetRequired(request.Parameters, "scope_id", out error, "tenant_id");
        if (scopeId == null)
            return false;

        var runScopeId = ctx.ScopeId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runScopeId))
        {
            error = "workflow run scope is required for self_reschedule.";
            return false;
        }
        if (!string.Equals(scopeId, runScopeId, StringComparison.Ordinal))
        {
            error = "scope_id must match the workflow run scope.";
            return false;
        }

        var workflowName = GetOptional(request.Parameters, "workflow_name");
        var serviceId = GetOptional(request.Parameters, "service_id");
        if (string.IsNullOrWhiteSpace(workflowName) && string.IsNullOrWhiteSpace(serviceId))
        {
            error = "workflow_name or service_id is required.";
            return false;
        }

        var prompt = GetOptional(request.Parameters, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = request.Input ?? string.Empty;

        WorkflowScheduleAuth? auth = null;
        if (!string.Equals(scheduleId, ctx.ScheduleId?.Trim(), StringComparison.Ordinal))
        {
            var authority = ctx.CallerNyxIdAuthority;
            var platform = authority?.Platform?.Trim() ?? string.Empty;
            var externalUserId = authority?.ExternalUserId?.Trim() ?? string.Empty;
            var capabilityScope = authority?.Scope?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(platform) ||
                string.IsNullOrWhiteSpace(externalUserId) ||
                string.IsNullOrWhiteSpace(capabilityScope))
            {
                error = "workflow caller NyxID authority is required to create a different schedule.";
                return false;
            }

            auth = new WorkflowScheduleAuth(
                SenderNyxId: new WorkflowScheduleNyxIdCredentialSource(
                    new WorkflowScheduleNyxIdSubjectRef(
                        platform,
                        authority?.Tenant?.Trim() ?? string.Empty,
                        externalUserId),
                    capabilityScope));
        }

        configuration = new WorkflowScheduleConfiguration(
            scheduleId,
            GetOptional(request.Parameters, "display_name") ?? string.Empty,
            workflowName ?? string.Empty,
            prompt,
            cronExpression,
            GetOptional(request.Parameters, "timezone") ?? "UTC",
            ParseEnabled(request.Parameters),
            CollectHeaders(request.Parameters),
            ScopeId: scopeId,
            AppId: GetOptional(request.Parameters, "app_id"),
            Namespace: GetOptional(request.Parameters, "namespace"),
            ServiceId: serviceId,
            RevisionId: GetOptional(request.Parameters, "revision_id"),
            Auth: auth,
            MutationContext: null);
        error = string.Empty;
        return true;
    }

    private static string? GetRequired(
        IDictionary<string, string> parameters,
        string key,
        out string error,
        params string[] aliases)
    {
        var value = GetOptional(parameters, key, aliases);
        if (!string.IsNullOrWhiteSpace(value))
        {
            error = string.Empty;
            return value;
        }

        error = $"{key} is required.";
        return null;
    }

    private static string? GetOptional(
        IDictionary<string, string> parameters,
        string key,
        params string[] aliases)
    {
        if (TryGetTrimmed(parameters, key, out var value))
            return value;

        foreach (var alias in aliases)
        {
            if (TryGetTrimmed(parameters, alias, out value))
                return value;
        }

        return null;
    }

    private static bool TryGetTrimmed(
        IDictionary<string, string> parameters,
        string key,
        out string? value)
    {
        foreach (var (parameterKey, parameterValue) in parameters)
        {
            if (!string.Equals(parameterKey, key, StringComparison.OrdinalIgnoreCase))
                continue;

            value = string.IsNullOrWhiteSpace(parameterValue) ? null : parameterValue.Trim();
            return value != null;
        }

        value = null;
        return false;
    }

    private static bool ParseEnabled(IDictionary<string, string> parameters)
    {
        var enabled = GetOptional(parameters, "enabled");
        return enabled == null || bool.TryParse(enabled, out var parsed) && parsed;
    }

    private static IReadOnlyDictionary<string, string> CollectHeaders(IDictionary<string, string> parameters)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (!key.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var headerName = key[HeaderPrefix.Length..].Trim();
            var headerValue = value?.Trim();
            if (headerName.Length == 0 || string.IsNullOrWhiteSpace(headerValue))
                continue;

            headers[headerName] = headerValue;
        }

        return headers;
    }

    private static Task PublishFailureAsync(
        IWorkflowExecutionContext ctx,
        StepRequestEvent request,
        string error,
        CancellationToken ct) =>
        ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = string.IsNullOrWhiteSpace(error) ? "Workflow schedule ensure failed." : error,
        }, TopologyAudience.Self, ct);
}
