using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class UpsertScriptDefinitionCommandTargetResolver
    : ICommandTargetResolver<UpsertScriptDefinitionCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public UpsertScriptDefinitionCommandTargetResolver(
        RuntimeScriptActorAccessor actorAccessor,
        IScriptingActorAddressResolver addressResolver)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task<CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>> ResolveAsync(
        UpsertScriptDefinitionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.ScriptId))
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.InvalidArgument("scriptId", "Script id is required."));
        if (string.IsNullOrWhiteSpace(command.ScriptRevision))
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.InvalidArgument("scriptRevision", "Script revision is required."));
        if (string.IsNullOrWhiteSpace(command.SourceText))
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.InvalidArgument("sourceText", "Source text is required."));

        var actorId = string.IsNullOrWhiteSpace(command.DefinitionActorId)
            ? _addressResolver.GetDefinitionActorId(command.ScriptId, command.ScopeId)
            : command.DefinitionActorId;

        var actor = await _actorAccessor.GetOrCreateAsync<ScriptDefinitionGAgent>(
            actorId,
            "Script definition actor not found",
            ct);

        return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Success(
            new ScriptingActorCommandTarget(actor));
    }
}
