using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Aevatar.Foundation.Runtime.Hosting.DependencyInjection;

public static class ServiceCollectionExtensions
{
    private const string EventSourcingSection = "EventSourcing";

    public static IServiceCollection AddAevatarActorRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AevatarActorRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = ReadRuntimeOptionsFromConfiguration(configuration);
        configure?.Invoke(options);

        EnforceActorRuntimeInMemoryPolicy(configuration, options);

        services.Replace(ServiceDescriptor.Singleton(options));

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderInMemory, StringComparison.OrdinalIgnoreCase))
        {
            return AddInMemoryRuntime(services, options);
        }

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
        {
            return AddOrleansRuntime(services, options);
        }

        throw new InvalidOperationException(
            $"Unsupported ActorRuntime provider '{options.Provider}'.");
    }

    private static AevatarActorRuntimeOptions ReadRuntimeOptionsFromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(AevatarActorRuntimeOptions.SectionName);
        var options = new AevatarActorRuntimeOptions();
        section.Bind(options);

        var eventSourcingSection = section.GetSection(EventSourcingSection);
        options.EventSourcingEnableSnapshots = ReadBool(
            eventSourcingSection,
            nameof(AevatarActorRuntimeOptions.EventSourcingEnableSnapshots),
            options.EventSourcingEnableSnapshots);
        ReadInt(
            eventSourcingSection,
            nameof(AevatarActorRuntimeOptions.EventSourcingSnapshotInterval),
            value => options.EventSourcingSnapshotInterval = value,
            "snapshot interval");
        options.EventSourcingEnableEventCompaction = ReadBool(
            eventSourcingSection,
            nameof(AevatarActorRuntimeOptions.EventSourcingEnableEventCompaction),
            options.EventSourcingEnableEventCompaction);
        ReadInt(
            eventSourcingSection,
            nameof(AevatarActorRuntimeOptions.EventSourcingRetainedEventsAfterSnapshot),
            value => options.EventSourcingRetainedEventsAfterSnapshot = value,
            "retained events after snapshot");

        return options;
    }

    private static bool ReadBool(
        IConfigurationSection section,
        string key,
        bool defaultValue)
    {
        var nestedKey = ResolveNestedEventSourcingKey(key);
        return section.GetValue(nestedKey, section.GetValue(key, defaultValue));
    }

    private static void ReadInt(
        IConfigurationSection section,
        string key,
        Action<int> setValue,
        string settingName)
    {
        var value = section[ResolveNestedEventSourcingKey(key)] ?? section[key];
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!int.TryParse(value, out var parsed))
        {
            throw new FormatException($"Invalid {settingName} value '{value}'.");
        }

        setValue(parsed);
    }

    private static string ResolveNestedEventSourcingKey(string key)
    {
        return key.StartsWith(EventSourcingSection, StringComparison.Ordinal)
            ? key[EventSourcingSection.Length..]
            : key;
    }

    private static IServiceCollection AddInMemoryRuntime(IServiceCollection services, AevatarActorRuntimeOptions options)
    {
        AddAevatarRuntimeWithEventSourcingOptions(services, options);
        return services;
    }

    private static IServiceCollection AddOrleansRuntime(IServiceCollection services, AevatarActorRuntimeOptions options)
    {
        AddAevatarRuntimeWithEventSourcingOptions(services, options);
        services.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
        {
            orleansOptions.StreamBackend = options.OrleansStreamBackend;
            orleansOptions.StreamProviderName = options.OrleansStreamProviderName;
            orleansOptions.ActorEventNamespace = options.OrleansActorEventNamespace;
            orleansOptions.PersistenceBackend = options.OrleansPersistenceBackend;
            orleansOptions.GarnetConnectionString = options.OrleansGarnetConnectionString;
            orleansOptions.QueueCount = options.OrleansQueueCount;
            orleansOptions.QueueCacheSize = options.OrleansQueueCacheSize;
        });

        if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendInMemory, StringComparison.OrdinalIgnoreCase))
            return services;

        if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendKafkaProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(transportOptions =>
            {
                transportOptions.BootstrapServers = options.KafkaBootstrapServers;
                transportOptions.TopicName = options.KafkaTopicName;
                transportOptions.ConsumerGroup = options.KafkaConsumerGroup;
                transportOptions.TopicPartitionCount = options.OrleansQueueCount;
            });
            return services;
        }

        throw new InvalidOperationException(
            $"Unsupported Orleans stream backend '{options.OrleansStreamBackend}'.");
    }

    private static void AddAevatarRuntimeWithEventSourcingOptions(
        IServiceCollection services,
        AevatarActorRuntimeOptions options)
    {
        services.AddAevatarRuntime(configureEventSourcing: eventSourcingOptions =>
        {
            eventSourcingOptions.EnableSnapshots = options.EventSourcingEnableSnapshots;
            eventSourcingOptions.SnapshotInterval = options.EventSourcingSnapshotInterval;
            eventSourcingOptions.EnableEventCompaction = options.EventSourcingEnableEventCompaction;
            eventSourcingOptions.RetainedEventsAfterSnapshot = options.EventSourcingRetainedEventsAfterSnapshot;
        });
    }

    private static void EnforceActorRuntimeInMemoryPolicy(
        IConfiguration configuration,
        AevatarActorRuntimeOptions options)
    {
        var sectionPrefix = $"{AevatarActorRuntimeOptions.SectionName}:Policies";
        var denyInMemory = ResolveOptionalBool(
            configuration[$"{sectionPrefix}:DenyInMemoryBackends"],
            fallbackValue: false);
        var environment = ResolveRuntimeEnvironment(configuration[$"{sectionPrefix}:Environment"]);
        var production = IsProductionEnvironment(environment);

        if (!denyInMemory && !production)
            return;

        var inMemoryBackends = new List<string>();

        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderInMemory, StringComparison.OrdinalIgnoreCase))
            inMemoryBackends.Add("Provider");

        if (string.Equals(options.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendInMemory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
            inMemoryBackends.Add("OrleansStreamBackend");

        if (string.Equals(options.OrleansPersistenceBackend, AevatarActorRuntimeOptions.OrleansPersistenceBackendInMemory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
            inMemoryBackends.Add("OrleansPersistenceBackend");

        if (inMemoryBackends.Count > 0)
        {
            throw new InvalidOperationException(
                $"InMemory actor runtime backends are not allowed in production. " +
                $"The following backends are set to InMemory: {string.Join(", ", inMemoryBackends)}. " +
                $"Configure durable backends or set ActorRuntime:Policies:DenyInMemoryBackends to false for non-production use.");
        }
    }

    private static string ResolveRuntimeEnvironment(string? configuredEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(configuredEnvironment))
            return configuredEnvironment.Trim();

        var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(dotnetEnvironment))
            return dotnetEnvironment.Trim();

        var aspnetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return aspnetEnvironment?.Trim() ?? "";
    }

    private static bool IsProductionEnvironment(string environment) =>
        string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);

    private static bool ResolveOptionalBool(string? rawValue, bool fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallbackValue;

        if (!bool.TryParse(rawValue, out var parsed))
            throw new InvalidOperationException($"Invalid boolean value '{rawValue}'.");

        return parsed;
    }
}
