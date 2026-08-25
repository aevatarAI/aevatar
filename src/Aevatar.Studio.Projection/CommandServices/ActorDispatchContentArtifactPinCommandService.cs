using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class ActorDispatchContentArtifactPinCommandService : IContentArtifactPinCommandPort
{
    private const string PublisherId = "aevatar.studio.projection.content-artifact-pin";
    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;

    public ActorDispatchContentArtifactPinCommandService(
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
    }

    public Task<ContentArtifactPinAcceptedReceipt> SetAsync(
        string scopeId,
        string pinKey,
        SetContentArtifactPinRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            pinKey,
            new SetContentArtifactPinCommand
            {
                ScopeId = scopeId,
                PinKey = pinKey,
                ArtifactId = request.ArtifactId,
                RequestedBy = ToPrincipal(requester),
                ExpectedPinVersion = request.ExpectedPinVersion,
                MutationId = request.MutationId,
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            "set",
            ct);

    public Task<ContentArtifactPinAcceptedReceipt> ClearAsync(
        string scopeId,
        string pinKey,
        ClearContentArtifactPinRequest request,
        ContentArtifactPrincipalContract requester,
        CancellationToken ct = default) =>
        DispatchAsync(
            scopeId,
            pinKey,
            new ClearContentArtifactPinCommand
            {
                ScopeId = scopeId,
                PinKey = pinKey,
                RequestedBy = ToPrincipal(requester),
                ExpectedPinVersion = request.ExpectedPinVersion,
                MutationId = request.MutationId,
                RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            "clear",
            ct);

    private async Task<ContentArtifactPinAcceptedReceipt> DispatchAsync(
        string scopeId,
        string pinKey,
        IMessage payload,
        string operation,
        CancellationToken ct)
    {
        var normalizedScopeId = ContentArtifactConventions.NormalizeScopeId(scopeId);
        var normalizedPinKey = ContentArtifactConventions.NormalizeLabelKey(pinKey, nameof(pinKey));
        var actorId = ContentArtifactConventions.BuildPinActorId(normalizedScopeId, normalizedPinKey);
        var commandId = BuildCommandId(operation, normalizedPinKey, payload);
        var actor = await _bootstrap.EnsureAsync<ContentArtifactPinGAgent>(actorId, ct);
        var receipt = await _commandDispatch.DispatchAsync(
            actor,
            payload,
            PublisherId,
            commandId,
            commandId,
            commandId,
            ct);
        return new ContentArtifactPinAcceptedReceipt(
            normalizedScopeId,
            normalizedPinKey,
            receipt.CommandId,
            receipt.CorrelationId,
            ContentArtifactCommandStageNames.DispatchAccepted,
            receipt.AckedAt);
    }

    private static string BuildCommandId(string operation, string pinKey, IMessage payload)
    {
        IMessage canonical = payload switch
        {
            SetContentArtifactPinCommand command => Canonical(command),
            ClearContentArtifactPinCommand command => Canonical(command),
            _ => throw new InvalidOperationException(
                $"Unsupported ContentArtifact pin command payload '{payload.Descriptor.FullName}'."),
        };
        var digest = Convert.ToHexStringLower(SHA256.HashData(canonical.ToByteArray()));
        return $"content-artifact-pin-{operation}-{pinKey}-{digest}";
    }

    private static SetContentArtifactPinCommand Canonical(SetContentArtifactPinCommand command)
    {
        var canonical = command.Clone();
        canonical.RequestedAtUtc = null;
        return canonical;
    }

    private static ClearContentArtifactPinCommand Canonical(ClearContentArtifactPinCommand command)
    {
        var canonical = command.Clone();
        canonical.RequestedAtUtc = null;
        return canonical;
    }

    private static ContentArtifactPrincipal ToPrincipal(ContentArtifactPrincipalContract principal) =>
        new()
        {
            PrincipalId = principal.PrincipalId,
            PrincipalKind = principal.PrincipalKind,
        };
}
