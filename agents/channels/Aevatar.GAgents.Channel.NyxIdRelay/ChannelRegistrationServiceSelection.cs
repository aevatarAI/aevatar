using System.Text.Json;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class ChannelRegistrationServiceSelection
{
    private static readonly ChannelRegistrationServiceSelection DefaultSelection =
        new(ChannelRegistrationAuthorizationMode.NyxidDefault, []);

    private ChannelRegistrationServiceSelection(
        ChannelRegistrationAuthorizationMode authorizationMode,
        IReadOnlyList<string> serviceIds)
    {
        AuthorizationMode = authorizationMode;
        ServiceIds = serviceIds;
    }

    public ChannelRegistrationAuthorizationMode AuthorizationMode { get; }

    public IReadOnlyList<string> ServiceIds { get; }

    public static ChannelRegistrationServiceSelection NyxIdDefault => DefaultSelection;

    internal static ChannelRegistrationServiceSelection Explicit(IReadOnlyList<string> serviceIds) =>
        new(ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist, serviceIds.ToArray());
}

public static class ChannelRegistrationServiceIdsJsonParser
{
    public static bool TryParse(
        JsonElement root,
        out ChannelRegistrationServiceSelection selection)
    {
        selection = ChannelRegistrationServiceSelection.NyxIdDefault;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("service_ids", out var serviceIdsElement))
            return true;

        if (serviceIdsElement.ValueKind != JsonValueKind.Array)
            return false;

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var element in serviceIdsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
                return false;

            var serviceId = element.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(serviceId))
                return false;

            normalized.Add(serviceId);
        }

        selection = ChannelRegistrationServiceSelection.Explicit(normalized.ToArray());
        return true;
    }
}
