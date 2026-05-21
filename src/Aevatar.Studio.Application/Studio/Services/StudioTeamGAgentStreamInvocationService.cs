using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioTeamGAgentStreamInvocationService : IStudioTeamGAgentStreamInvocationService
{
    private const string ServiceAppId = "default";
    private const string ServiceNamespace = "default";

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

    public async Task<StaticGAgentStreamInvocationResult> InvokeAsync(
        StudioTeamGAgentStreamInvocationRequest request,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var teamId = NormalizeRequired(request.TeamId, nameof(request.TeamId));
        var endpointId = NormalizeRequired(request.EndpointId, nameof(request.EndpointId));
        var input = request.Input ?? throw new InvalidOperationException("input is required.");

        var resolution = await _teamEntryMemberResolver.ResolveAsync(scopeId, teamId, ct);
        var identity = new ServiceIdentity
        {
            TenantId = resolution.ScopeId,
            AppId = ServiceAppId,
            Namespace = ServiceNamespace,
            ServiceId = resolution.PublishedServiceId,
        };

        return await _staticInvocationPort.InvokeAsync(
            new StaticGAgentStreamInvocationRequest(identity, endpointId, input),
            emitAsync,
            onAcceptedAsync,
            ct);
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        return normalized;
    }
}
