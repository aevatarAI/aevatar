using System.Reflection;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatSystemPrompt
{
    private static readonly Lazy<string> Cached = new(Load);

    public static string Value => Cached.Value;

    private static string Load()
    {
        var assembly = typeof(NyxIdChatSystemPrompt).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("system-prompt.md", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            return "You are a helpful NyxID assistant.";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return "You are a helpful NyxID assistant.";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
