using System.Reflection;

namespace Aevatar.GAgents.ChatbotClassifier;

internal static class ChatbotClassifierSystemPrompt
{
    private static readonly Lazy<string> Cached = new(Load);

    public static string Value => Cached.Value;

    private static string Load()
    {
        var assembly = typeof(ChatbotClassifierSystemPrompt).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("system-prompt.md", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            return "You are the NyxID chatbot classification service. Respond with JSON only.";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return "You are the NyxID chatbot classification service. Respond with JSON only.";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
