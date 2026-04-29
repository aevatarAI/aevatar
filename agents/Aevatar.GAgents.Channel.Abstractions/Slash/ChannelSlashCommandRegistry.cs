namespace Aevatar.GAgents.Channel.Abstractions.Slash;

/// <summary>
/// Validates and indexes the DI-registered <see cref="IChannelSlashCommandHandler"/>
/// set at startup so name / alias collisions surface as a fail-fast
/// <see cref="InvalidOperationException"/> instead of a silent first-wins
/// dispatch (PR #521 review). Every command name and alias is folded to lower-
/// case for matching; uniqueness is enforced across the union of all registered
/// names and aliases — so a future handler claiming <c>"model"</c> as an alias
/// fails as loudly as one claiming the canonical name.
/// </summary>
public sealed class ChannelSlashCommandRegistry
{
    private readonly Dictionary<string, IChannelSlashCommandHandler> _byName;

    /// <summary>
    /// Build the registry from the DI-resolved handler set. Throws
    /// <see cref="InvalidOperationException"/> when two handlers claim the
    /// same name or alias (PR #521 review).
    /// </summary>
    public ChannelSlashCommandRegistry(IEnumerable<IChannelSlashCommandHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _byName = new Dictionary<string, IChannelSlashCommandHandler>(StringComparer.Ordinal);
        var collisions = new List<string>();

        foreach (var handler in handlers)
        {
            foreach (var token in EnumerateTokens(handler))
            {
                var key = token.ToLowerInvariant();
                if (_byName.TryGetValue(key, out var existing) && !ReferenceEquals(existing, handler))
                {
                    collisions.Add($"'/{key}' claimed by both {existing.GetType().FullName} and {handler.GetType().FullName}");
                    continue;
                }
                _byName[key] = handler;
            }
        }

        if (collisions.Count > 0)
        {
            throw new InvalidOperationException(
                "Duplicate slash command registration detected. Each Name and Alias must be unique across all IChannelSlashCommandHandler implementations: "
                + string.Join("; ", collisions));
        }
    }

    /// <summary>
    /// Returns the handler that owns <paramref name="commandName"/> (canonical
    /// name or any alias), or <c>null</c> if no handler is registered for it.
    /// Match is case-insensitive.
    /// </summary>
    public IChannelSlashCommandHandler? Find(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
            return null;
        return _byName.TryGetValue(commandName.ToLowerInvariant(), out var handler) ? handler : null;
    }

    /// <summary>
    /// All registered handlers (deduplicated) in registration order. Mostly
    /// useful for diagnostics / a future <c>/help</c> command.
    /// </summary>
    public IReadOnlyCollection<IChannelSlashCommandHandler> Handlers =>
        _byName.Values.Distinct().ToArray();

    private static IEnumerable<string> EnumerateTokens(IChannelSlashCommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(handler.Name))
            throw new InvalidOperationException(
                $"{handler.GetType().FullName}.Name must not be blank.");
        yield return handler.Name;
        foreach (var alias in handler.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
        }
    }
}
