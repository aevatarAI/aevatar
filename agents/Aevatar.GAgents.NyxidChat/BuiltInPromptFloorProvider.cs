using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>Loads the mandatory capability floor shared by direct and relay prompt composition.</summary>
public sealed class BuiltInPromptFloorProvider : IBuiltInPromptFloorProvider
{
    private const string FloorResourceSuffix = "system-skill-overlay-default.md";
    private static readonly Lazy<BuiltInPromptFloorLayer> Cached = new(Load);

    public BuiltInPromptFloorLayer GetFloor() => Cached.Value;

    private static BuiltInPromptFloorLayer Load()
    {
        var assembly = typeof(BuiltInPromptFloorProvider).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(FloorResourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            return EmptyFloor();

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return EmptyFloor();

        using var reader = new StreamReader(stream);
        return new BuiltInPromptFloorLayer(
            reader.ReadToEnd().Trim(),
            new BuiltInPromptFloorProvenance("embedded:system-skill-overlay-default.md"));
    }

    private static BuiltInPromptFloorLayer EmptyFloor() =>
        new(string.Empty, new BuiltInPromptFloorProvenance("embedded:missing"));
}
