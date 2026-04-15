using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;

namespace Aevatar.Foundation.VoicePresence;

/// <summary>
/// Standard <see cref="IEventModuleFactory{TContext}"/> implementation for voice-presence modules.
/// </summary>
public sealed class VoicePresenceModuleFactory : IEventModuleFactory<IEventHandlerContext>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyDictionary<string, VoicePresenceModuleRegistration> _registrationsByName;

    public VoicePresenceModuleFactory(
        IServiceProvider serviceProvider,
        IEnumerable<VoicePresenceModuleRegistration> registrations)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _registrationsByName = BuildRegistrationMap(
            registrations ?? throw new ArgumentNullException(nameof(registrations)));
    }

    public bool TryCreate(string name, out IEventModule<IEventHandlerContext>? module)
    {
        module = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (!_registrationsByName.TryGetValue(name, out var registration))
            return false;

        module = registration.Create(_serviceProvider, name);
        return module != null;
    }

    private static IReadOnlyDictionary<string, VoicePresenceModuleRegistration> BuildRegistrationMap(
        IEnumerable<VoicePresenceModuleRegistration> registrations)
    {
        var map = new Dictionary<string, VoicePresenceModuleRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registrations)
        {
            foreach (var name in registration.Names)
            {
                if (!map.TryAdd(name, registration))
                    throw new InvalidOperationException($"Duplicate voice presence module name '{name}' found.");
            }
        }

        return map;
    }
}
