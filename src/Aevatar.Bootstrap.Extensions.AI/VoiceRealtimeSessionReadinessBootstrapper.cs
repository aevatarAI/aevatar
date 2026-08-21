using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Bootstrap.Extensions.AI;

internal static class VoiceRealtimeSessionReadinessBootstrapper
{
    public static async Task<RealtimeVoiceProviderSession> ConnectAsync(
        VoicePresenceSessionLeaseHandle handle,
        IRealtimeVoiceProvider provider,
        VoiceProviderConfig providerConfig,
        VoiceSessionConfig sessionConfig,
        IVoiceToolCatalog? toolCatalog,
        Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
        Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
        ILogger<ReadinessGatedRealtimeVoiceProviderSession>? logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(providerConfig);
        ArgumentNullException.ThrowIfNull(sessionConfig);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(audioSink);

        var providerSession = await provider.ConnectAsync(
            BuildSessionKey(handle),
            providerConfig,
            eventSink,
            audioSink,
            ct);

        // Apply the resolved per-session prompt (chat-route policy sessionOverrides.instructions, carried
        // on the lease handle) to the relay/model session. The static BuildOpenAIVoiceSessionConfig never
        // sees it, so without this the model runs with an EMPTY system prompt and has no guidance for
        // which tools to call (it could only call obvious zero-arg tools, never nyxid_proxy for HA).
        var readinessConfig = sessionConfig.Clone();
        if (!string.IsNullOrWhiteSpace(handle.Instructions))
            readinessConfig.Instructions = handle.Instructions;

        var readinessCancellation = new CancellationTokenSource();
        var readiness = CompleteReadinessAsync(
            providerSession,
            readinessConfig,
            toolCatalog,
            handle.ToolContext?.Clone(),
            readinessCancellation.Token);
        return new ReadinessGatedRealtimeVoiceProviderSession(providerSession, readiness, readinessCancellation, logger);
    }

    public static Task<RealtimeVoiceProviderSession> ConnectAsync(
        VoicePresenceSessionLeaseHandle handle,
        IRealtimeVoiceProvider provider,
        VoiceProviderConfig providerConfig,
        VoiceSessionConfig sessionConfig,
        IVoiceToolCatalog? toolCatalog,
        Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
        Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
        CancellationToken ct) =>
        ConnectAsync(handle, provider, providerConfig, sessionConfig, toolCatalog, eventSink, audioSink, logger: null, ct);

    private static VoiceProviderSessionKey BuildSessionKey(VoicePresenceSessionLeaseHandle handle) =>
        new(
            handle.SessionId,
            handle.OwnerId,
            handle.ActiveTransportLeaseId ?? string.Empty,
            handle.LeaseEpoch,
            Timestamp.FromDateTimeOffset(handle.ExpiresAtUtc.ToUniversalTime()),
            handle.ActorId,
            handle.ModuleName,
            handle.ToolContext?.Clone());

    private static async Task CompleteReadinessAsync(
        RealtimeVoiceProviderSession providerSession,
        VoiceSessionConfig sessionConfig,
        IVoiceToolCatalog? toolCatalog,
        VoiceToolExecutionContext? toolContext,
        CancellationToken ct)
    {
        var effectiveSession = await BuildEffectiveVoiceSessionConfigAsync(sessionConfig, toolCatalog, toolContext, ct);
        await providerSession.UpdateSessionAsync(effectiveSession, ct);
    }

    internal static async Task<VoiceSessionConfig> BuildEffectiveVoiceSessionConfigAsync(
        VoiceSessionConfig sessionConfig,
        IVoiceToolCatalog? toolCatalog,
        VoiceToolExecutionContext? toolContext,
        CancellationToken ct)
    {
        var effectiveSession = sessionConfig.Clone();
        if (toolCatalog == null)
            return effectiveSession;

        var snapshot = await toolCatalog.DiscoverAsync(toolContext, ct);
        VoiceToolCatalogSnapshotValidator.Validate(snapshot);
        effectiveSession.ToolNames.Clear();
        effectiveSession.ToolDefinitions.Clear();
        foreach (var discoveredTool in snapshot.Tools)
        {
            var toolName = discoveredTool.Name?.Trim();
            effectiveSession.ToolDefinitions.Add(new VoiceToolDefinition
            {
                Name = toolName!,
                Description = discoveredTool.Description ?? string.Empty,
                ParametersSchema = discoveredTool.ParametersSchema,
                Owner = discoveredTool.Owner,
            });
        }

        return effectiveSession;
    }
}
