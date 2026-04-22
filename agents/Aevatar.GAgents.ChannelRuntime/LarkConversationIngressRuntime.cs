using System.Text;
using Aevatar.GAgents.Channel.Lark;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

internal interface ILarkConversationIngressRuntime
{
    Task<IResult> HandleAsync(HttpContext http, ChannelBotRegistrationEntry registration, CancellationToken ct);
}

internal sealed class LarkConversationIngressRuntime : ILarkConversationIngressRuntime
{
    private readonly LarkConversationAdapterFactory _adapterFactory;
    private readonly ILarkConversationInbox _inbox;
    private readonly ILogger<LarkConversationIngressRuntime> _logger;
    private readonly IChannelRuntimeDiagnostics? _diagnostics;

    public LarkConversationIngressRuntime(
        LarkConversationAdapterFactory adapterFactory,
        ILarkConversationInbox inbox,
        ILogger<LarkConversationIngressRuntime> logger,
        IChannelRuntimeDiagnostics? diagnostics = null)
    {
        _adapterFactory = adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diagnostics = diagnostics;
    }

    public async Task<IResult> HandleAsync(HttpContext http, ChannelBotRegistrationEntry registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(registration);

        try
        {
            var adapter = await _adapterFactory.CreateAsync(registration, ct);
            try
            {
                var request = await BuildWebhookRequestAsync(http, ct);
                var response = await adapter.HandleWebhookAsync(request, ct);

                if (!string.IsNullOrWhiteSpace(response.ResponseBody))
                {
                    RecordDiagnostic("Callback:verified", registration.Platform, registration.Id, "lark_webhook_verification");
                    return Results.Content(
                        response.ResponseBody,
                        "application/json",
                        Encoding.UTF8,
                        response.StatusCode);
                }

                if (response.StatusCode != StatusCodes.Status200OK)
                {
                    RecordDiagnostic("Callback:error", registration.Platform, registration.Id, $"webhook_rejected:{response.StatusCode}");
                    return Results.StatusCode(response.StatusCode);
                }

                if (response.Activity is null)
                {
                    RecordDiagnostic("Callback:ignored", registration.Platform, registration.Id, "adapter_returned_null");
                    return Results.Ok(new { status = "ignored" });
                }

                await _inbox.EnqueueAsync(response.Activity, ct);
                RecordDiagnostic("Callback:accepted", registration.Platform, registration.Id, "durable_inbox_committed");
                return Results.Ok(new { status = "accepted" });
            }
            finally
            {
                await adapter.StopReceivingAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Lark ingress failed: registrationId={RegistrationId}",
                registration.Id);
            RecordDiagnostic("Callback:error", registration.Platform, registration.Id, ex.GetType().Name);
            return Results.Json(
                new { status = "dispatch_error", error = "dispatch_failed" },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<LarkWebhookRequest> BuildWebhookRequestAsync(HttpContext http, CancellationToken ct)
    {
        http.Request.EnableBuffering();
        if (http.Request.Body.CanSeek)
            http.Request.Body.Position = 0;

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        if (http.Request.Body.CanSeek)
            http.Request.Body.Position = 0;

        var headers = http.Request.Headers.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        return new LarkWebhookRequest(buffer.ToArray(), headers);
    }

    private void RecordDiagnostic(string stage, string platform, string registrationId, string? detail = null)
    {
        _diagnostics?.Record(stage, platform, registrationId, detail);
    }
}
