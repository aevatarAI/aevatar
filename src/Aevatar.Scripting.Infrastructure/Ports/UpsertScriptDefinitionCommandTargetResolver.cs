using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class UpsertScriptDefinitionCommandTargetResolver
    : ICommandTargetResolver<UpsertScriptDefinitionCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>
{
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
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
        if ((command.ScriptPackage?.CsharpSources.Count ?? 0) == 0)
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.InvalidArgument("scriptPackage", "Script package must contain at least one C# source."));

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
