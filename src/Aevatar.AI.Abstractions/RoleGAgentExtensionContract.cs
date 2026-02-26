using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Abstractions;

/// <summary>
/// Contract helpers for event modules that extend role-agent behavior
/// without creating a custom derived GAgent class.
/// </summary>
public static class RoleGAgentExtensionContract
{
    /// <summary>Codec for app state carried in <see cref="SetRoleAppStateEvent"/>.</summary>
    public const string AppStateCodecProtobufAny = "protobuf-any";

    /// <summary>Codec for app config JSON carried in <see cref="SetRoleAppConfigEvent"/>.</summary>
    public const string AppConfigCodecJsonPlain = "json/plain";

    /// <summary>
    /// Builds an app-config patch event payload that can be published to a RoleGAgent.
    /// </summary>
    public static SetRoleAppConfigEvent CreateAppConfigPatch(
        string appConfigJson,
        int appConfigSchemaVersion,
        string appConfigCodec = AppConfigCodecJsonPlain) =>
        new()
        {
            AppConfigJson = appConfigJson ?? string.Empty,
            AppConfigCodec = string.IsNullOrWhiteSpace(appConfigCodec) ? AppConfigCodecJsonPlain : appConfigCodec,
            AppConfigSchemaVersion = appConfigSchemaVersion,
        };

    /// <summary>
    /// Builds an app-state update event payload that can be published to a RoleGAgent.
    /// </summary>
    public static SetRoleAppStateEvent CreateAppStateUpdate(
        IMessage appState,
        int appStateSchemaVersion,
        string appStateCodec = AppStateCodecProtobufAny)
    {
        ArgumentNullException.ThrowIfNull(appState);
        return new SetRoleAppStateEvent
        {
            AppState = Any.Pack(appState),
            AppStateCodec = string.IsNullOrWhiteSpace(appStateCodec) ? AppStateCodecProtobufAny : appStateCodec,
            AppStateSchemaVersion = appStateSchemaVersion,
        };
    }
}
