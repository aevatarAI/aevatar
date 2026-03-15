using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ProvisionScriptRuntimeCommandTargetResolver
    : ICommandTargetResolver<ProvisionScriptRuntimeCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;

    public ProvisionScriptRuntimeCommandTargetResolver(RuntimeScriptActorAccessor actorAccessor)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
    }

    public async Task<CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>> ResolveAsync(
        ProvisionScriptRuntimeCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.DefinitionActorId))
        {
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.InvalidArgument(
                    "definitionActorId",
                    "Definition actor id is required."));
        }

        var runtimeActorId = ResolveRuntimeActorId(command);
        var actor = await _actorAccessor.GetOrCreateAsync<ScriptBehaviorGAgent>(
            runtimeActorId,
            "Script runtime actor not found after create",
            ct);

        return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Success(
            new ScriptingActorCommandTarget(actor));
    }

    private static string ResolveRuntimeActorId(ProvisionScriptRuntimeCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.RuntimeActorId))
            return command.RuntimeActorId;

        var revisionScope = string.IsNullOrWhiteSpace(command.ScriptRevision)
            ? command.DefinitionSnapshot.Revision
            : command.ScriptRevision;
        return $"script-runtime:{command.DefinitionActorId}:{revisionScope}:{Guid.NewGuid():N}";
    }
}
