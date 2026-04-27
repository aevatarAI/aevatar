namespace Aevatar.GAgents.Channel.NyxIdRelay;

public static class NyxIdRelayErrorClassifier
{
    public static string Classify(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "Sorry, something went wrong while generating a response.";

        if (error.Contains("403", StringComparison.Ordinal) ||
            error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return "Sorry, I can't reach the AI service right now (403 Forbidden).";
        }

        if (error.Contains("401", StringComparison.Ordinal) ||
            error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return "Sorry, authentication with the AI service failed (401).";
        }

        if (error.Contains("429", StringComparison.Ordinal) ||
            error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("too many", StringComparison.OrdinalIgnoreCase))
        {
            return "Sorry, the AI service is busy right now (429). Please wait a moment and try again.";
        }

        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the AI service took too long to respond. Please try again.";

        if (error.Contains("model", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "Sorry, the configured AI model is not available.";
        }

        return "Sorry, something went wrong while generating a response.";
    }
}
