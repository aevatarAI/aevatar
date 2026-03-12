using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionCommandTargetResolver
    : ICommandTargetResolver<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionStartError>
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly IScriptEvolutionProjectionPort _projectionPort;

    public ScriptEvolutionCommandTargetResolver(
        RuntimeScriptActorAccessor actorAccessor,
        IScriptingActorAddressResolver addressResolver,
        IScriptEvolutionProjectionPort projectionPort)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<CommandTargetResolution<ScriptEvolutionCommandTarget, ScriptEvolutionStartError>> ResolveAsync(
        ScriptEvolutionProposal command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var proposalId = string.IsNullOrWhiteSpace(command.ProposalId)
            ? Guid.NewGuid().ToString("N")
            : command.ProposalId;
        var managerActorId = _addressResolver.GetEvolutionManagerActorId();

        _ = await _actorAccessor.GetOrCreateAsync<ScriptEvolutionManagerGAgent>(
            managerActorId,
            "Script evolution manager actor not found",
            ct);

        var sessionActorId = _addressResolver.GetEvolutionSessionActorId(proposalId);
        var sessionActor = await _actorAccessor.GetOrCreateAsync<ScriptEvolutionSessionGAgent>(
            sessionActorId,
            "Script evolution session actor not found",
            ct);

        return CommandTargetResolution<ScriptEvolutionCommandTarget, ScriptEvolutionStartError>.Success(
            new ScriptEvolutionCommandTarget(
                sessionActor,
                proposalId,
                _projectionPort));
    }
}
