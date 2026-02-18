namespace Aevatar.CQRS.Core.Profiles;

public sealed class DefaultSubsystemProfileRegistry : ISubsystemProfileRegistry
{
    private readonly IReadOnlyDictionary<string, ISubsystemProfile> _profiles;
    private readonly SubsystemSelectionOptions _options;

    public DefaultSubsystemProfileRegistry(
        IEnumerable<ISubsystemProfile> profiles,
        SubsystemSelectionOptions options)
    {
        _profiles = profiles
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        _options = options;
    }

    public IReadOnlyList<string> ListNames() => _profiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    public ISubsystemProfile Resolve(string? preferredName = null)
    {
        var requested = string.IsNullOrWhiteSpace(preferredName)
            ? _options.DefaultSubsystem
            : preferredName;

        if (!string.IsNullOrWhiteSpace(requested) && _profiles.TryGetValue(requested, out var matched))
            return matched;

        if (_profiles.Count == 1)
            return _profiles.Values.Single();

        if (_profiles.Count == 0)
            throw new InvalidOperationException("No subsystem profile registered.");

        throw new InvalidOperationException(
            $"Cannot resolve subsystem profile. configured='{requested ?? "(null)"}', available='{string.Join(",", _profiles.Keys)}'.");
    }
}
