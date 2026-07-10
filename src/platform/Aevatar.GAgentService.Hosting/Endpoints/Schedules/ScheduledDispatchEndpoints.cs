using System.Security.Claims;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Hosting.Serialization;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgentService.Hosting.Endpoints.Schedules;

public static class ScheduledDispatchEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/schedules", Create)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPut("/schedules/{scheduleId}", Update)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/schedules/{scheduleId}:enable", Enable)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/schedules/{scheduleId}:disable", Disable)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapDelete("/schedules/{scheduleId}", Delete)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchMutationReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/schedules", List)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchListResult>(StatusCodes.Status200OK);
        group.MapGet("/schedules/{scheduleId}", Get)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/schedules/preview", Preview)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchPreview>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/schedules/{scheduleId}:run-now", RunNow)
            .WithTags("Schedules")
            .Produces<ScheduledDispatchRunNowReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> Create(
        HttpContext http,
        ScheduledDispatchConfigurationHttpRequest input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct = default)
    {
        ScheduledDispatchConfiguration configuration;
        ScheduledDispatchMutationContext context;
        try
        {
            context = ResolveMutationContext(http);
            configuration = await input.ToConfigurationAsync(
                input.ScheduleId,
                catalogReader,
                revisionCatalogReader,
                context.AuthenticatedNyxIdOwnerSubject,
                defaultMissingWorkflowScheduleAuth: true,
                ct);
        }
        catch (Exception ex) when (TryMapScheduleConfigurationError(ex, out var result))
        {
            return result;
        }

        try
        {
            var receipt = await schedules.CreateAsync(configuration, context, ct);
            return Results.Accepted($"/api/schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Update(
        HttpContext http,
        string scheduleId,
        ScheduledDispatchConfigurationHttpRequest input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct = default)
    {
        ScheduledDispatchConfiguration configuration;
        ScheduledDispatchMutationContext context;
        try
        {
            context = ResolveMutationContext(http);
            configuration = await input.ToConfigurationAsync(
                scheduleId,
                catalogReader,
                revisionCatalogReader,
                context.AuthenticatedNyxIdOwnerSubject,
                defaultMissingWorkflowScheduleAuth: false,
                ct);
        }
        catch (Exception ex) when (TryMapScheduleConfigurationError(ex, out var result))
        {
            return result;
        }

        try
        {
            var receipt = await schedules.UpdateAsync(scheduleId, configuration, context, ct);
            return Results.Accepted($"/api/schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Enable(
        string scheduleId,
        ScheduledDispatchStateChangeHttpRequest? input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.EnableAsync(scheduleId, input?.Reason ?? string.Empty, ct);
            return Results.Accepted($"/api/schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Disable(
        string scheduleId,
        ScheduledDispatchStateChangeHttpRequest? input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.DisableAsync(scheduleId, input?.Reason ?? string.Empty, ct);
            return Results.Accepted($"/api/schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Delete(
        string scheduleId,
        [FromQuery] string? reason,
        [FromBody] ScheduledDispatchStateChangeHttpRequest? input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.DeleteAsync(scheduleId, reason ?? input?.Reason ?? string.Empty, ct);
            return Results.Accepted($"/api/schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> List(
        [FromServices] IScheduledDispatchApplicationService schedules,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default)
    {
        return Results.Ok(await schedules.ListAsync(take, cursor, includeTotalCount, ct));
    }

    internal static async Task<IResult> Get(
        string scheduleId,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var schedule = await schedules.GetAsync(scheduleId, ct);
            return schedule == null ? Results.NotFound() : Results.Ok(schedule);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> Preview(
        ScheduledDispatchPreviewHttpRequest input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            return Results.Ok(await schedules.PreviewAsync(
                input.CronExpression,
                input.Timezone,
                input.Count <= 0 ? 5 : input.Count,
                input.FromUtc,
                ct));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    internal static async Task<IResult> RunNow(
        string scheduleId,
        [FromServices] IScheduledDispatchApplicationService schedules,
        CancellationToken ct = default)
    {
        try
        {
            var receipt = await schedules.RunNowAsync(scheduleId, ct);
            return Results.Accepted($"/api/schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    private static ScheduledDispatchMutationContext ResolveMutationContext(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new ScheduledDispatchMutationContext(
            ReadFirstClaim(http.User, "scope_id", "workflow.scope_id"),
            ResolveAuthenticatedNyxIdOwnerSubject(http));
    }

    private static ScheduledServiceInvocationNyxIdSubjectRef? ResolveAuthenticatedNyxIdOwnerSubject(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var ownerUserId = ReadFirstClaim(
            http.User,
            "uid",
            "sub",
            ClaimTypes.NameIdentifier,
            "user_id");
        if (string.IsNullOrWhiteSpace(ownerUserId))
            return null;

        return new ScheduledServiceInvocationNyxIdSubjectRef(
            OwnerScope.NyxIdPlatform,
            string.Empty,
            ownerUserId.Trim());
    }

    private static string? ReadFirstClaim(ClaimsPrincipal? user, params string[] claimTypes)
    {
        if (user == null)
            return null;

        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    internal static bool TryMapScheduleConfigurationError(Exception ex, out IResult result)
    {
        switch (ex)
        {
            case FormatException:
                result = Results.BadRequest(new
                {
                    code = "INVALID_SCHEDULED_DISPATCH_REQUEST",
                    message = "payloadBase64 must be valid base64.",
                    validation = new
                    {
                        field = "serviceInvocation.payloadBase64",
                        error = "INVALID_BASE64",
                    },
                });
                return true;
            case InvalidOperationException invalid:
                result = Results.BadRequest(new
                {
                    code = "INVALID_SCHEDULED_DISPATCH_REQUEST",
                    message = invalid.Message,
                });
                return true;
            case ArgumentException argument:
                result = Results.BadRequest(new { error = argument.Message });
                return true;
            default:
                result = Results.Empty;
                return false;
        }
    }

    internal static bool TryMapScheduleMutationError(Exception ex, out IResult result)
    {
        switch (ex)
        {
            case ArgumentException argument:
                result = Results.BadRequest(new { error = argument.Message });
                return true;
            case ScheduledDispatchNotFoundException notFound:
                result = Results.NotFound(new { error = notFound.Message });
                return true;
            case ScheduledDispatchConflictException conflict:
                result = Results.Conflict(new { error = conflict.Message });
                return true;
            default:
                result = Results.Empty;
                return false;
        }
    }

    internal static void RejectExternalCallerDurableCredential(Any? payload)
    {
        if (payload?.TryUnpack<ChatRequestEvent>(out var chatRequest) != true ||
            chatRequest.CallerDurableCredential == null)
        {
            return;
        }

        throw new ArgumentException(
            "caller_durable_credential is trusted-only and cannot be supplied by schedule API payloads.",
            "caller_durable_credential");
    }

}

public sealed record ScheduledDispatchConfigurationHttpRequest
{
    private const string DefaultWorkflowScheduleNyxIdScope = "proxy";

    public string? ScheduleId { get; init; }
    public string? DisplayName { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScheduledDispatchScheduleKind ScheduleKind { get; init; } = ScheduledDispatchScheduleKind.Generic;
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public ScheduledDispatchEnvelopeTargetHttpRequest? Envelope { get; init; }
    public ScheduledDispatchServiceInvocationTargetHttpRequest? ServiceInvocation { get; init; }

    public async Task<ScheduledDispatchConfiguration> ToConfigurationAsync(
        string? fallbackScheduleId,
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null,
        bool defaultMissingWorkflowScheduleAuth = true,
        CancellationToken ct = default)
    {
        var resolvedTarget = await ResolveTargetAsync(catalogReader, revisionCatalogReader, authenticatedOwnerSubject, ct);
        var scheduleKind = ResolveScheduleKind(resolvedTarget);
        var target = defaultMissingWorkflowScheduleAuth
            ? ApplyDefaultWorkflowScheduleAuth(resolvedTarget.Target, scheduleKind, authenticatedOwnerSubject)
            : resolvedTarget.Target;
        return new ScheduledDispatchConfiguration(
            ScheduleId: string.IsNullOrWhiteSpace(ScheduleId) ? fallbackScheduleId ?? string.Empty : ScheduleId,
            DisplayName: DisplayName ?? string.Empty,
            Target: target,
            CronExpression: CronExpression,
            Timezone: Timezone ?? string.Empty,
            Enabled: Enabled,
            Headers: Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ScheduleKind: scheduleKind);
    }

    private async Task<ResolvedScheduledDispatchTarget> ResolveTargetAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject,
        CancellationToken ct)
    {
        var targetCount = (Envelope == null ? 0 : 1) +
                          (ServiceInvocation == null ? 0 : 1);
        if (targetCount != 1)
            throw new ArgumentException("Exactly one scheduled dispatch target is required.");

        if (Envelope != null)
            return new ResolvedScheduledDispatchTarget(Envelope.ToTarget(), IsWorkflowServiceTarget: false);
        return await ServiceInvocation!.ToResolvedTargetAsync(catalogReader, revisionCatalogReader, authenticatedOwnerSubject, ct);
    }

    private ScheduledDispatchScheduleKind ResolveScheduleKind(ResolvedScheduledDispatchTarget resolvedTarget)
    {
        if (ScheduleKind == ScheduledDispatchScheduleKind.Workflow)
        {
            if (!resolvedTarget.IsWorkflowServiceTarget)
            {
                throw new ArgumentException(
                    "scheduleKind Workflow requires a workflow service invocation target.",
                    nameof(ScheduleKind));
            }

            return ScheduledDispatchScheduleKind.Workflow;
        }

        if (resolvedTarget.IsWorkflowServiceTarget && ScheduleKind == ScheduledDispatchScheduleKind.Generic)
        {
            return ScheduledDispatchScheduleKind.Workflow;
        }

        return ScheduleKind;
    }

    private static ScheduledDispatchTargetDescriptor ApplyDefaultWorkflowScheduleAuth(
        ScheduledDispatchTargetDescriptor target,
        ScheduledDispatchScheduleKind scheduleKind,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject)
    {
        var serviceInvocation = target.ServiceInvocation;
        if (scheduleKind != ScheduledDispatchScheduleKind.Workflow ||
            target.Kind != ScheduledDispatchTargetKind.ServiceInvocation ||
            serviceInvocation == null ||
            serviceInvocation.Auth != null)
        {
            return target;
        }

        if (authenticatedOwnerSubject == null)
        {
            throw new ArgumentException(
                "Authenticated NyxID owner subject is required for workflow schedule auth.",
                nameof(authenticatedOwnerSubject));
        }

        return target with
        {
            ServiceInvocation = serviceInvocation with
            {
                Auth = new ScheduledServiceInvocationAuth(
                    new ScheduledServiceInvocationNyxIdCredentialSource(
                        authenticatedOwnerSubject,
                        DefaultWorkflowScheduleNyxIdScope,
                        ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner)),
            },
        };
    }

    internal sealed record ResolvedScheduledDispatchTarget(
        ScheduledDispatchTargetDescriptor Target,
        bool IsWorkflowServiceTarget);
}

public sealed record ScheduledDispatchEnvelopeTargetHttpRequest
{
    public string? ActorId { get; init; }
    public required EventEnvelope Envelope { get; init; }

    public ScheduledDispatchTargetDescriptor ToTarget() =>
        CreateTarget(Envelope);

    private ScheduledDispatchTargetDescriptor CreateTarget(EventEnvelope envelope)
    {
        ScheduledDispatchEndpoints.RejectExternalCallerDurableCredential(envelope.Payload);
        return new(
            ScheduledDispatchTargetKind.Envelope,
            ActorId: ActorId,
            Envelope: envelope);
    }
}

public sealed record ScheduledDispatchServiceInvocationTargetHttpRequest
{
    public required ServiceIdentity Identity { get; init; }
    public required string EndpointId { get; init; }
    public required string PayloadTypeUrl { get; init; }
    public string? PayloadBase64 { get; init; }
    public string? PayloadJson { get; init; }
    public string? RevisionId { get; init; }
    public ServiceInvocationCaller? Caller { get; init; }
    public ScheduledServiceInvocationAuthHttpRequest? Auth { get; init; }

    public async Task<ScheduledDispatchTargetDescriptor> ToTargetAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null,
        CancellationToken ct = default) =>
        (await ToResolvedTargetAsync(catalogReader, revisionCatalogReader, authenticatedOwnerSubject, ct))
        .Target;

    internal async Task<ScheduledDispatchConfigurationHttpRequest.ResolvedScheduledDispatchTarget> ToResolvedTargetAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null,
        CancellationToken ct = default)
    {
        var (payload, revisionId) = await ResolvePayloadAsync(catalogReader, revisionCatalogReader, ct);
        var target = ToTarget(payload, revisionId, authenticatedOwnerSubject);
        var implementationRevision = await ResolveImplementationRevisionAsync(catalogReader, revisionCatalogReader, revisionId, ct);
        return new ScheduledDispatchConfigurationHttpRequest.ResolvedScheduledDispatchTarget(
            target,
            IsWorkflowRevision(implementationRevision));
    }

    public ScheduledDispatchTargetDescriptor ToTarget(
        Any payload,
        string revisionId,
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null)
    {
        ScheduledDispatchEndpoints.RejectExternalCallerDurableCredential(payload);
        return new(
            ScheduledDispatchTargetKind.ServiceInvocation,
            ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                Identity,
                EndpointId,
                payload,
                revisionId,
                Caller,
                Auth?.ToAuth(authenticatedOwnerSubject)));
    }

    private async Task<(Any Payload, string RevisionId)> ResolvePayloadAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct)
    {
        var typeUrl = PayloadTypeUrl ?? string.Empty;
        var requestedRevisionId = RevisionId?.Trim() ?? string.Empty;
        var hasJson = !string.IsNullOrWhiteSpace(PayloadJson);
        var hasBase64 = !string.IsNullOrWhiteSpace(PayloadBase64);
        if (hasJson && hasBase64)
            throw new InvalidOperationException(
                "payloadJson and payloadBase64 are mutually exclusive; specify only one.");

        if (hasJson)
        {
            if (string.IsNullOrWhiteSpace(typeUrl))
                throw new InvalidOperationException("payloadTypeUrl is required when payloadJson is provided.");

            var revisionId = requestedRevisionId;
            if (string.IsNullOrWhiteSpace(revisionId))
            {
                var catalog = await catalogReader.GetAsync(Identity, ct);
                revisionId = catalog?.ActiveServingRevisionId ?? string.Empty;
            }

            var packed = await ServiceJsonPayloads.PackJsonAsync(
                revisionCatalogReader,
                Identity,
                revisionId,
                typeUrl,
                PayloadJson!,
                ct);
            return (packed, revisionId);
        }

        return (ServiceJsonPayloads.PackBase64(typeUrl, PayloadBase64), requestedRevisionId);
    }

    private async Task<ServiceRevisionSnapshot?> ResolveImplementationRevisionAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        string resolvedRevisionId,
        CancellationToken ct)
    {
        var revisions = await revisionCatalogReader.GetAsync(Identity, ct).ConfigureAwait(false);
        if (revisions == null || revisions.Revisions.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(resolvedRevisionId))
        {
            return revisions.Revisions.FirstOrDefault(revision =>
                string.Equals(revision.RevisionId, resolvedRevisionId, StringComparison.Ordinal) &&
                ServiceEndpointContractMath.RevisionContainsEndpoint(revision, EndpointId));
        }

        var service = await catalogReader.GetAsync(Identity, ct).ConfigureAwait(false);
        return service == null
            ? null
            : ServiceEndpointContractMath.ResolveCurrentContractRevision(service, revisions, EndpointId);
    }

    private static bool IsWorkflowRevision(ServiceRevisionSnapshot? revision) =>
        revision != null &&
        (string.Equals(
             revision.ImplementationKind,
             ServiceEndpointContractMath.ImplementationKindWorkflow,
             StringComparison.OrdinalIgnoreCase) ||
         revision.Implementation?.Workflow != null);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScheduledServiceInvocationAuthHttpRequest
{
    public ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest? SenderNyxId { get; init; }
    public string? DurableSenderBearerToken { get; init; }
    public ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest? ScopeOwnerNyxId { get; init; }

    public ScheduledServiceInvocationAuth ToAuth(
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null)
    {
        var hasSenderNyxId = SenderNyxId != null;
        var durableToken = DurableSenderBearerToken?.Trim() ?? string.Empty;
        var hasDurableSenderBearerToken = durableToken.Length > 0;
        var hasScopeOwnerNyxId = ScopeOwnerNyxId != null;
        if (hasDurableSenderBearerToken)
        {
            throw new ArgumentException(
                "durableSenderBearerToken is no longer accepted for schedule auth; use senderNyxId or scopeOwnerNyxId.",
                nameof(DurableSenderBearerToken));
        }

        if (Convert.ToInt32(hasSenderNyxId) +
            Convert.ToInt32(hasScopeOwnerNyxId) != 1)
        {
            throw new ArgumentException("Exactly one service invocation credential source is required.", nameof(SenderNyxId));
        }

        if (hasScopeOwnerNyxId)
            return new ScheduledServiceInvocationAuth(ScopeOwnerNyxId!.ToSource(authenticatedOwnerSubject));

        return new ScheduledServiceInvocationAuth(SenderNyxId!.ToSource());
    }
}

public sealed record ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest
{
    public required string Scope { get; init; }

    public ScheduledServiceInvocationNyxIdCredentialSource ToSource(
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject = null) =>
        new(
            RequireAuthenticatedOwnerSubject(authenticatedOwnerSubject),
            NormalizeRequired(Scope, nameof(Scope)),
            ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner);

    private static ScheduledServiceInvocationNyxIdSubjectRef RequireAuthenticatedOwnerSubject(
        ScheduledServiceInvocationNyxIdSubjectRef? authenticatedOwnerSubject) =>
        authenticatedOwnerSubject ?? throw new ArgumentException(
            "Authenticated NyxID owner subject is required for scope owner schedule auth.",
            nameof(authenticatedOwnerSubject));

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        return value.Trim();
    }
}

public sealed record ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest
{
    public required ScheduledServiceInvocationNyxIdSubjectRefHttpRequest Subject { get; init; }
    public required string Scope { get; init; }

    public ScheduledServiceInvocationNyxIdCredentialSource ToSource() =>
        new(NormalizeSubject(Subject), NormalizeRequired(Scope, nameof(Scope)));

    private static ScheduledServiceInvocationNyxIdSubjectRef NormalizeSubject(
        ScheduledServiceInvocationNyxIdSubjectRefHttpRequest? subject)
    {
        if (subject == null)
            throw new ArgumentException("Subject is required.", nameof(Subject));

        return subject.ToSubject();
    }

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        return value.Trim();
    }
}

public sealed record ScheduledServiceInvocationNyxIdSubjectRefHttpRequest
{
    public required string Platform { get; init; }
    public string? Tenant { get; init; }
    public required string ExternalUserId { get; init; }

    public ScheduledServiceInvocationNyxIdSubjectRef ToSubject() =>
        new(
            NormalizeRequired(Platform, nameof(Platform)).ToLowerInvariant(),
            NormalizeOptional(Tenant),
            NormalizeRequired(ExternalUserId, nameof(ExternalUserId)));

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);

        return value.Trim();
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

public sealed record ScheduledDispatchPreviewHttpRequest
{
    public required string CronExpression { get; init; }
    public string? Timezone { get; init; }
    public int Count { get; init; } = 5;
    public DateTimeOffset? FromUtc { get; init; }
}

public sealed record ScheduledDispatchStateChangeHttpRequest
{
    public string? Reason { get; init; }
}
