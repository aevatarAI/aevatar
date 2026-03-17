using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RunScriptRuntimeCommandTargetResolver
    : ICommandTargetResolver<RunScriptRuntimeCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;

    public RunScriptRuntimeCommandTargetResolver(RuntimeScriptActorAccessor actorAccessor)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
    }

    public async Task<CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>> ResolveAsync(
        RunScriptRuntimeCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RuntimeActorId))
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.InvalidArgument("runtimeActorId", "Runtime actor id is required."));
        if (string.IsNullOrWhiteSpace(command.RunId))
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.InvalidArgument("runId", "Run id is required."));

        var actor = await _actorAccessor.GetAsync(command.RuntimeActorId);
        if (actor == null)
        {
            return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Failure(
                ScriptingCommandStartError.ActorNotFound(
                    command.RuntimeActorId,
                    $"Script runtime actor not found: {command.RuntimeActorId}"));
        }

        return CommandTargetResolution<ScriptingActorCommandTarget, ScriptingCommandStartError>.Success(
            new ScriptingActorCommandTarget(actor));
    }
}
