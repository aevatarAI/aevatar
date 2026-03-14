using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class PromoteScriptCatalogRevisionCommandTargetResolver
    : ICommandTargetResolver<PromoteScriptCatalogRevisionCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public PromoteScriptCatalogRevisionCommandTargetResolver(
        RuntimeScriptActorAccessor actorAccessor,
        IScriptingActorAddressResolver addressResolver)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task<CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>> ResolveAsync(
        PromoteScriptCatalogRevisionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.ScriptId))
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.InvalidArgument("scriptId", "Script id is required."));
        if (string.IsNullOrWhiteSpace(command.Revision))
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.InvalidArgument("revision", "Revision is required."));

        var actorId = string.IsNullOrWhiteSpace(command.CatalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : command.CatalogActorId;
        var actor = await _actorAccessor.GetOrCreateAsync<ScriptCatalogGAgent>(
            actorId,
            "Script catalog actor not found",
            ct);

        return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Success(
            new ScriptingActorCommandTarget(actor));
    }
}
