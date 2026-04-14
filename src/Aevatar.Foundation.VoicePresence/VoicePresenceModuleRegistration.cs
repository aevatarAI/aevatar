using Aevatar.Foundation.VoicePresence.Modules;

namespace Aevatar.Foundation.VoicePresence;

/// <summary>
/// Registration entry used by <see cref="VoicePresenceModuleFactory"/> to create
/// voice-presence modules by configured names.
/// </summary>
public sealed class VoicePresenceModuleRegistration
{
    public VoicePresenceModuleRegistration(
        IEnumerable<string> names,
        Func<IServiceProvider, VoicePresenceModule> create)
    {
        ArgumentNullException.ThrowIfNull(names);
        Create = create ?? throw new ArgumentNullException(nameof(create));

        Names = names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (Names.Count == 0)
            throw new ArgumentException("At least one module name is required.", nameof(names));
    }

    public IReadOnlyList<string> Names { get; }

    public Func<IServiceProvider, VoicePresenceModule> Create { get; }
}
