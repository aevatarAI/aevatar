using System.Diagnostics;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Opens one <see cref="Activity"/> around the pipeline invocation and tags it with the mandatory
/// observability dimensions from RFC §6.1. Downstream middlewares and grain spans run inside this
/// span so OTEL pipelines can aggregate on a single trace.
/// </summary>
public sealed class TracingMiddleware : IChannelMiddleware
{
    /// <summary>Default retry-count tag value when inbound hasn't been redelivered.</summary>
    public const int DefaultRetryCount = 0;

    /// <inheritdoc />
    public async Task InvokeAsync(ITurnContext context, Func<Task> next, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var activity = context.Activity;
        using var span = ChannelDiagnostics.ActivitySource.StartActivity(
            ChannelDiagnostics.Spans.PipelineInvoke,
            ActivityKind.Server);

        if (span is not null)
        {
            span.SetTag(ChannelDiagnostics.Tags.ActivityId, activity?.Id);
            span.SetTag(ChannelDiagnostics.Tags.ProviderEventId, activity?.RawPayloadBlobRef);
            span.SetTag(ChannelDiagnostics.Tags.CanonicalKey, activity?.Conversation?.CanonicalKey);
            span.SetTag(ChannelDiagnostics.Tags.BotInstanceId, activity?.Bot?.Value ?? context.Bot?.Bot?.Value);
            span.SetTag(ChannelDiagnostics.Tags.ChannelId, activity?.ChannelId?.Value ?? context.Bot?.Channel?.Value);
            span.SetTag(ChannelDiagnostics.Tags.RetryCount, DefaultRetryCount);
            span.SetTag(ChannelDiagnostics.Tags.RawPayloadBlobRef, activity?.RawPayloadBlobRef);
            span.SetTag(ChannelDiagnostics.Tags.AuthPrincipal, ResolveAuthPrincipal(context));
        }

        try
        {
            await next();
            span?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.Message);
            span?.SetTag("error.type", ex.GetType().FullName);
            throw;
        }
    }

    private static string ResolveAuthPrincipal(ITurnContext context)
    {
        var bot = context.Bot;
        if (bot is null)
            return "bot";
        return string.IsNullOrWhiteSpace(bot.RegistrationId) ? "bot" : $"bot:{bot.RegistrationId}";
    }
}
