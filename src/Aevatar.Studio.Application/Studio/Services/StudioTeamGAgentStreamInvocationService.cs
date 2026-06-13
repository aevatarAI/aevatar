using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.AGUI.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioTeamGAgentStreamInvocationService : IStudioTeamGAgentStreamInvocationService
{
    private readonly ITeamEntryMemberResolver _teamEntryMemberResolver;
    private readonly IStaticGAgentStreamInvocationPort<AGUIEvent> _staticInvocationPort;

    public StudioTeamGAgentStreamInvocationService(
        ITeamEntryMemberResolver teamEntryMemberResolver,
        IStaticGAgentStreamInvocationPort<AGUIEvent> staticInvocationPort)
    {
        _teamEntryMemberResolver = teamEntryMemberResolver
            ?? throw new ArgumentNullException(nameof(teamEntryMemberResolver));
        _staticInvocationPort = staticInvocationPort
            ?? throw new ArgumentNullException(nameof(staticInvocationPort));
    }

    public async Task<StudioTeamStreamInvocationResult> InvokeAsync(
        StudioTeamStreamInvocationRequest request,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<StudioTeamStreamInvocationAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var teamId = NormalizeRequired(request.TeamId, nameof(request.TeamId));
        var endpointId = NormalizeRequired(request.EndpointId, nameof(request.EndpointId));
        var input = request.Input ?? throw new InvalidOperationException("input is required.");

        var resolution = await _teamEntryMemberResolver.ResolveAsync(scopeId, teamId, endpointId, ct);
        var identity = new ServiceIdentity
        {
            TenantId = NormalizeRequired(resolution.ScopeId, nameof(resolution.ScopeId)),
            AppId = ScopeServiceIdentityDefaults.ServiceAppId,
            Namespace = ScopeServiceIdentityDefaults.ServiceNamespace,
            ServiceId = NormalizeRequired(
                resolution.PublishedServiceId,
                nameof(resolution.PublishedServiceId)),
        };

        var result = await _staticInvocationPort.InvokeAsync(
            new StaticGAgentStreamInvocationRequest(identity, endpointId, MapInput(input)),
            emitAsync,
            onAcceptedAsync == null
                ? null
                : (receipt, token) => onAcceptedAsync(MapAcceptedReceipt(receipt), token),
            ct);

        return new StudioTeamStreamInvocationResult(
            result.Accepted == null ? null : MapAcceptedReceipt(result.Accepted),
            result.StartError.ToString(),
            result.CompletionStatus.ToString(),
            result.CompletionObserved);
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }

    private static StaticGAgentStreamInvocationInput MapInput(StudioTeamStreamInvocationInput input) =>
        new(
            Prompt: input.Prompt,
            PreferredActorId: input.PreferredActorId,
            SessionId: input.SessionId,
            RevisionId: input.RevisionId,
            Headers: input.Headers,
            InputParts: MapInputParts(input.InputParts),
            Timeout: input.Timeout);

    private static IReadOnlyList<GAgentDraftRunInputPart>? MapInputParts(
        IReadOnlyList<StudioTeamStreamInvocationInputPart>? inputParts)
    {
        if (inputParts == null)
            return null;

        var mapped = new List<GAgentDraftRunInputPart>(inputParts.Count);
        foreach (var part in inputParts)
        {
            mapped.Add(new GAgentDraftRunInputPart
            {
                Kind = MapInputPartKind(part.Type),
                Text = part.Text,
                DataBase64 = part.DataBase64,
                MediaType = part.MediaType,
                Uri = part.Uri,
                Name = part.Name,
            });
        }

        return mapped;
    }

    private static GAgentDraftRunInputPartKind MapInputPartKind(string type)
    {
        var normalized = NormalizeRequired(type, nameof(StudioTeamStreamInvocationInputPart.Type));
        return normalized.ToLowerInvariant() switch
        {
            "text" => GAgentDraftRunInputPartKind.Text,
            "image" => GAgentDraftRunInputPartKind.Image,
            "audio" => GAgentDraftRunInputPartKind.Audio,
            "video" => GAgentDraftRunInputPartKind.Video,
            _ => GAgentDraftRunInputPartKind.Unspecified,
        };
    }

    private static StudioTeamStreamInvocationAcceptedReceipt MapAcceptedReceipt(
        StaticGAgentStreamAcceptedReceipt receipt) =>
        new(
            receipt.GAgentReceipt.CommandId,
            receipt.GAgentReceipt.ActorId,
            receipt.GAgentReceipt.CorrelationId);
}
