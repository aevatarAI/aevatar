using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Schedules;
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
        ScheduledDispatchConfigurationHttpRequest input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct = default)
    {
        ScheduledDispatchConfiguration configuration;
        try
        {
            configuration = await input.ToConfigurationAsync(input.ScheduleId, catalogReader, revisionCatalogReader, ct);
        }
        catch (Exception ex) when (TryMapScheduleConfigurationError(ex, out var result))
        {
            return result;
        }

        try
        {
            var receipt = await schedules.CreateAsync(configuration, ct);
            return Results.Accepted($"/api/schedules/{receipt.ScheduleId}", receipt);
        }
        catch (Exception ex) when (TryMapScheduleMutationError(ex, out var result))
        {
            return result;
        }
    }

    internal static async Task<IResult> Update(
        string scheduleId,
        ScheduledDispatchConfigurationHttpRequest input,
        [FromServices] IScheduledDispatchApplicationService schedules,
        [FromServices] IServiceCatalogQueryReader catalogReader,
        [FromServices] IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct = default)
    {
        ScheduledDispatchConfiguration configuration;
        try
        {
            configuration = await input.ToConfigurationAsync(scheduleId, catalogReader, revisionCatalogReader, ct);
        }
        catch (Exception ex) when (TryMapScheduleConfigurationError(ex, out var result))
        {
            return result;
        }

        try
        {
            var receipt = await schedules.UpdateAsync(scheduleId, configuration, ct);
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
}

public sealed record ScheduledDispatchConfigurationHttpRequest
{
    public string? ScheduleId { get; init; }
    public string? DisplayName { get; init; }
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
        CancellationToken ct = default) =>
        new(
            ScheduleId: string.IsNullOrWhiteSpace(ScheduleId) ? fallbackScheduleId ?? string.Empty : ScheduleId,
            DisplayName: DisplayName ?? string.Empty,
            Target: await ResolveTargetAsync(catalogReader, revisionCatalogReader, ct),
            CronExpression: CronExpression,
            Timezone: Timezone ?? string.Empty,
            Enabled: Enabled,
            Headers: Headers ?? new Dictionary<string, string>(StringComparer.Ordinal));

    private async Task<ScheduledDispatchTargetDescriptor> ResolveTargetAsync(
        IServiceCatalogQueryReader catalogReader,
        IServiceRevisionCatalogQueryReader revisionCatalogReader,
        CancellationToken ct)
    {
        var targetCount = (Envelope == null ? 0 : 1) +
                          (ServiceInvocation == null ? 0 : 1);
        if (targetCount != 1)
            throw new ArgumentException("Exactly one scheduled dispatch target is required.");

        if (Envelope != null)
            return Envelope.ToTarget();
        return await ServiceInvocation!.ToTargetAsync(catalogReader, revisionCatalogReader, ct);
    }
}

public sealed record ScheduledDispatchEnvelopeTargetHttpRequest
{
    public string? ActorId { get; init; }
    public required EventEnvelope Envelope { get; init; }

    public ScheduledDispatchTargetDescriptor ToTarget() =>
        new(
            ScheduledDispatchTargetKind.Envelope,
            ActorId: ActorId,
            Envelope: Envelope);
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
        CancellationToken ct = default)
    {
        var (payload, revisionId) = await ResolvePayloadAsync(catalogReader, revisionCatalogReader, ct);
        return ToTarget(payload, revisionId);
    }

    public ScheduledDispatchTargetDescriptor ToTarget(Any payload, string revisionId) =>
        new(
            ScheduledDispatchTargetKind.ServiceInvocation,
            ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                Identity,
                EndpointId,
                payload,
                revisionId,
                Caller,
                Auth?.ToAuth()));

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
}

public sealed record ScheduledServiceInvocationAuthHttpRequest
{
    public ScheduledServiceInvocationNyxIdCredentialSourceHttpRequest? SenderNyxId { get; init; }
    public ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest? ScopeOwnerNyxId { get; init; }

    public ScheduledServiceInvocationAuth ToAuth()
    {
        var hasSenderNyxId = SenderNyxId != null;
        var hasScopeOwnerNyxId = ScopeOwnerNyxId != null;
        if (hasSenderNyxId == hasScopeOwnerNyxId)
            throw new ArgumentException("Exactly one NyxID credential source is required.", nameof(SenderNyxId));

        return hasScopeOwnerNyxId
            ? new ScheduledServiceInvocationAuth(ScopeOwnerNyxId: ScopeOwnerNyxId!.ToSource())
            : new ScheduledServiceInvocationAuth(SenderNyxId: SenderNyxId!.ToSource());
    }
}

public sealed record ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceHttpRequest
{
    public required string Scope { get; init; }

    public ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource ToSource() =>
        new(NormalizeRequired(Scope, nameof(Scope)));

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
