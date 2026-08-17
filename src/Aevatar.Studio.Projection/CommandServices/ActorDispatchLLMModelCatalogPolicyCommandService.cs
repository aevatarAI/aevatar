using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using ApplicationPolicyMode = Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicyMode;
using ProtoPolicyMode = Aevatar.GAgents.UserConfig.LLMModelCatalogPolicyMode;

namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class ActorDispatchLLMModelCatalogPolicyCommandService
    : ILLMModelCatalogPolicyCommandPort
{
    private const string DirectRoute = "aevatar.studio.projection.llm-model-catalog-policy";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;

    public ActorDispatchLLMModelCatalogPolicyCommandService(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<UserConfigSaveReceipt> ReplaceAsync(
        ReplaceLLMModelCatalogPolicy command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);

        var actorId = LLMModelCatalogPolicyActorIdMapper.Build(command.Owner);
        var payload = MapCommand(command);
        _ = LLMModelCatalogPolicyGAgent.BuildReplacedEvent(
            new LLMModelCatalogPolicyGAgentState(),
            payload);
        var actor = await _bootstrap.EnsureAsync<LLMModelCatalogPolicyGAgent>(actorId, ct);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
        };

        var admission = await _dispatchPort.DispatchAsync(actor.Id, envelope, ct).ConfigureAwait(false);
        return new UserConfigSaveReceipt(
            Accepted: admission.Accepted,
            CommandId: admission.CommandId,
            AckStage: admission.Accepted
                ? UserConfigCommandAckStage.Accepted
                : UserConfigCommandAckStage.AdmissionRejected,
            ActorId: admission.ActorId,
            CorrelationId: admission.CorrelationId,
            AckedAtUtc: admission.AckedAt);
    }

    private static void ValidateCommand(ReplaceLLMModelCatalogPolicy command)
    {
        if (command.ExpectedStateVersion < 0)
            throw new ArgumentOutOfRangeException(
                nameof(command.ExpectedStateVersion),
                "Expected state version must be non-negative.");
        if (string.IsNullOrWhiteSpace(command.MutationId))
            throw new ArgumentException("Mutation ID is required.", nameof(command.MutationId));
        if (command.Sources is null)
            throw new ArgumentNullException(nameof(command.Sources));

        switch (command.Owner.Kind)
        {
            case LLMModelCatalogPolicyOwnerKind.Platform:
                if (command.Mode != ApplicationPolicyMode.Custom)
                    throw new ArgumentException("Platform policy mode must be custom.", nameof(command.Mode));
                if (command.Sources.Any(static source =>
                        source.SourceIdentity is not NyxIDCatalogServiceModelSourceIdentity catalog ||
                        string.IsNullOrWhiteSpace(catalog.CatalogServiceId)))
                {
                    throw new ArgumentException(
                        "Platform policy sources must reference catalog services.",
                        nameof(command.Sources));
                }
                break;
            case LLMModelCatalogPolicyOwnerKind.Scope:
                if (command.Mode == ApplicationPolicyMode.InheritPlatform)
                {
                    if (command.Sources.Count != 0)
                    {
                        throw new ArgumentException(
                            "Inherited scope policy must not contain sources.",
                            nameof(command.Sources));
                    }
                }
                else if (command.Mode != ApplicationPolicyMode.Custom)
                {
                    throw new ArgumentException(
                        "Scope policy mode must be inherit platform or custom.",
                        nameof(command.Mode));
                }
                if (command.Sources.Any(static source =>
                        source.SourceIdentity is not NyxIDUserServiceModelSourceIdentity user ||
                        string.IsNullOrWhiteSpace(user.UserServiceId)))
                {
                    throw new ArgumentException(
                        "Scope policy sources must reference exact user services.",
                        nameof(command.Sources));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command.Owner));
        }
    }

    private static ReplaceLLMModelCatalogPolicyCommand MapCommand(ReplaceLLMModelCatalogPolicy command)
    {
        var payload = new ReplaceLLMModelCatalogPolicyCommand
        {
            OwnerType = command.Owner.Kind switch
            {
                LLMModelCatalogPolicyOwnerKind.Platform => LLMModelCatalogPolicyOwnerType.Platform,
                LLMModelCatalogPolicyOwnerKind.Scope => LLMModelCatalogPolicyOwnerType.Scope,
                _ => throw new ArgumentOutOfRangeException(nameof(command.Owner)),
            },
            ScopeId = command.Owner.ScopeId ?? string.Empty,
            Mode = command.Mode switch
            {
                ApplicationPolicyMode.InheritPlatform => ProtoPolicyMode.InheritPlatform,
                ApplicationPolicyMode.Custom => ProtoPolicyMode.Custom,
                _ => throw new ArgumentOutOfRangeException(nameof(command.Mode)),
            },
            ExpectedStateVersion = command.ExpectedStateVersion,
            MutationId = command.MutationId ?? string.Empty,
        };
        payload.Sources.AddRange((command.Sources ?? throw new ArgumentNullException(nameof(command.Sources)))
            .Select(MapSource));
        return payload;
    }

    private static Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource MapSource(
        Aevatar.Studio.Application.Studio.Abstractions.LLMModelCatalogPolicySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var reference = new NyxIDModelSourceReference
        {
            ServiceSlugSnapshot = source.ServiceSlugSnapshot ?? string.Empty,
        };
        switch (source.SourceIdentity)
        {
            case NyxIDCatalogServiceModelSourceIdentity catalog:
                reference.CatalogServiceId = catalog.CatalogServiceId;
                break;
            case NyxIDUserServiceModelSourceIdentity user:
                reference.UserServiceId = user.UserServiceId;
                break;
            case null:
                throw new ArgumentNullException(nameof(source.SourceIdentity));
            default:
                throw new ArgumentOutOfRangeException(nameof(source.SourceIdentity));
        }

        return new Aevatar.GAgents.UserConfig.LLMModelCatalogPolicySource
        {
            Source = reference,
            ExplicitModels = MapExplicitModels(
                source.ModelSelection ?? throw new ArgumentNullException(nameof(source.ModelSelection))),
        };
    }

    private static ExplicitLLMModelIDs MapExplicitModels(ExplicitLLMModels selection)
    {
        var explicitModels = new ExplicitLLMModelIDs();
        explicitModels.UpstreamModelIds.AddRange(
            selection.UpstreamModelIds ?? throw new ArgumentNullException(nameof(selection.UpstreamModelIds)));
        return explicitModels;
    }
}
