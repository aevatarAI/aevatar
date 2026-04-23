using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdRelayReplies
{
    public static string ClassifyError(string error)
    {
        if (error.Contains("403", StringComparison.Ordinal) ||
            error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            return "Sorry, I can't reach the AI service right now (403 Forbidden).";

        if (error.Contains("401", StringComparison.Ordinal) ||
            error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            return "Sorry, authentication with the AI service failed (401).";

        if (error.Contains("429", StringComparison.Ordinal) ||
            error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("too many", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the AI service is busy right now (429). Please wait a moment and try again.";

        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the AI service took too long to respond. Please try again.";

        if (error.Contains("model", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "Sorry, the configured AI model is not available.";

        return "Sorry, something went wrong while generating a response.";
    }

    public static bool TryExtractLlmError(string? content, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        const string llmErrorPrefix = "[[AEVATAR_LLM_ERROR]]";
        const string llmFailedPrefix = "LLM request failed:";
        if (content.StartsWith(llmErrorPrefix, StringComparison.Ordinal))
        {
            error = content[llmErrorPrefix.Length..].Trim();
            return true;
        }

        if (content.StartsWith(llmFailedPrefix, StringComparison.Ordinal))
        {
            error = content.Trim();
            return true;
        }

        return false;
    }

    public static string BuildDiagnostic(
        Google.Protobuf.Collections.MapField<string, string> metadata,
        IConfiguration? configuration,
        string errorMessage)
    {
        var modelOverride = metadata.TryGetValue(LLMRequestMetadataKeys.ModelOverride, out var m) ? m : null;
        var serverDefault = configuration?["Aevatar:NyxId:DefaultModel"] ?? "(OpenAIModel option)";
        var route = metadata.TryGetValue(LLMRequestMetadataKeys.NyxIdRoutePreference, out var r)
            && !string.IsNullOrWhiteSpace(r) ? r : "gateway";
        var hasToken = metadata.ContainsKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        var scope = metadata.TryGetValue("scope_id", out var s) ? s : "<unknown>";

        var model = !string.IsNullOrWhiteSpace(modelOverride)
            ? $"{modelOverride} (from config.json)"
            : $"server-default={serverDefault}";

        var error = errorMessage.Length > 300 ? errorMessage[..300] + "..." : errorMessage;

        return $"Model: {model}\nRoute: {route}\nScope: {scope}\nToken: {(hasToken ? "present" : "MISSING")}\nError: {error}";
    }

    public static async Task FinalizeReplyAsync(
        IAsyncDisposable subscription,
        TaskCompletionSource<string> responseTcs,
        NyxIdRelayReplyAccumulator relayReply,
        string sessionId,
        string messageId,
        string relayToken,
        Google.Protobuf.Collections.MapField<string, string> metadata,
        NyxIdRelayOptions relayOptions,
        NyxIdApiClient nyxClient,
        IConfiguration? configuration,
        ILogger logger)
    {
        await using var ownedSubscription = subscription;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(relayOptions.ResponseTimeoutSeconds));

        var completed = await Task.WhenAny(
            responseTcs.Task,
            Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token));

        string replyText;
        if (completed == responseTcs.Task && responseTcs.Task.IsCompletedSuccessfully)
        {
            replyText = responseTcs.Task.Result;
            logger.LogInformation(
                "Relay response completed: session={SessionId}, length={Length}",
                sessionId,
                replyText.Length);
        }
        else
        {
            var partial = relayReply.Snapshot();
            logger.LogWarning(
                "Relay response timed out: session={SessionId}, partial_length={Length}",
                sessionId,
                partial.Length);
            replyText = partial.Length > 0
                ? partial
                : "Sorry, it's taking too long to respond. Please try again.";
        }

        if (relayReply.WasTruncated)
        {
            logger.LogWarning(
                "Relay response buffer truncated: session={SessionId}, max_chars={MaxChars}",
                sessionId,
                relayReply.MaxChars);
        }

        var relayError = relayReply.GetErrorMessage();
        if (!string.IsNullOrWhiteSpace(relayError))
        {
            logger.LogWarning(
                "Relay LLM error surfaced through async reply: session={SessionId}, error={Error}",
                sessionId,
                relayError);
            replyText = ClassifyError(relayError);
            if (relayOptions.EnableDebugDiagnostics)
            {
                var diagnostic = BuildDiagnostic(metadata, configuration, relayError);
                replyText += $"\n\n[Debug]\n{diagnostic}";
            }
        }
        else if (string.IsNullOrWhiteSpace(replyText))
        {
            replyText = "Sorry, I wasn't able to generate a response. Please try again.";
        }

        using var deliveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var delivery = await nyxClient.SendChannelRelayTextReplyAsync(relayToken, messageId, replyText, deliveryCts.Token);
        if (!delivery.Succeeded)
        {
            logger.LogError(
                "Relay async reply delivery failed: session={SessionId}, messageId={MessageId}, detail={Detail}",
                sessionId,
                messageId,
                delivery.Detail);
            return;
        }

        logger.LogInformation(
            "Relay async reply delivered: session={SessionId}, messageId={MessageId}, platformMessageId={PlatformMessageId}",
            sessionId,
            delivery.MessageId,
            delivery.PlatformMessageId);
    }
}

internal class NyxIdRelayReplyAccumulator
{
    private readonly object _gate = new();
    private readonly StringBuilder _responseBuilder = new();
    private string? _errorMessage;
    private bool _wasTruncated;

    public NyxIdRelayReplyAccumulator(int maxChars)
    {
        MaxChars = maxChars > 0 ? maxChars : 16 * 1024;
    }

    public int MaxChars { get; }

    public bool WasTruncated
    {
        get
        {
            lock (_gate)
                return _wasTruncated;
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _responseBuilder.Length == 0;
        }
    }

    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        lock (_gate)
        {
            if (_responseBuilder.Length >= MaxChars)
            {
                _wasTruncated = true;
                return;
            }

            var remaining = MaxChars - _responseBuilder.Length;
            if (text.Length <= remaining)
            {
                _responseBuilder.Append(text);
                return;
            }

            _responseBuilder.Append(text, 0, remaining);
            _wasTruncated = true;
        }
    }

    public void SetError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return;

        lock (_gate)
            _errorMessage = errorMessage.Trim();
    }

    public string? GetErrorMessage()
    {
        lock (_gate)
            return _errorMessage;
    }

    public string Snapshot()
    {
        lock (_gate)
            return _responseBuilder.ToString();
    }
}
