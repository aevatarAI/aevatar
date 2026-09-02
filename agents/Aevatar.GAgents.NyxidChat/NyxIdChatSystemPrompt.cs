using System.Reflection;
using Aevatar.AI.Abstractions.Prompting;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatSystemPrompt
{
    private static readonly Lazy<KernelPromptLayer> Cached = new(Load);

    public static KernelPromptLayer Value => Cached.Value;

    private static KernelPromptLayer Load()
    {
        // Direct and relay composition share this typed kernel source.
        var assembly = typeof(NyxIdChatSystemPrompt).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("system-prompt.md", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            return Fallback();

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return Fallback();

        using var reader = new StreamReader(stream);
        return new KernelPromptLayer(
            reader.ReadToEnd(),
            new KernelPromptProvenance("embedded:system-prompt.md"));
    }

    private static KernelPromptLayer Fallback() =>
        new("You are a helpful NyxID assistant.", new KernelPromptProvenance("embedded:fallback"));
}
