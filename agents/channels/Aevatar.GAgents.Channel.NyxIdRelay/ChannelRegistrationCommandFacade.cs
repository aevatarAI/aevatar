using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record ChannelRegistrationCommandAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId);

public enum ChannelRegistrationCommandStartError
{
    None = 0,
    StoreActorUnavailable = 1,
}

internal sealed class ChannelRegistrationCommandDispatchException : InvalidOperationException
{
    public ChannelRegistrationCommandDispatchException(
        string message,
        bool acceptanceUnknown,
        Exception? innerException = null)
        : base(message, innerException)
    {
        AcceptanceUnknown = acceptanceUnknown;
    }

    public bool AcceptanceUnknown { get; }
}

// Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
//   Old pattern: Nyx relay registration endpoints + singleton provisioning services 在 Host 内做 platform selection / scope resolution / remote Nyx provisioning / actor creation / envelope construction / dispatch through raw runtime/dispatch helpers。
//   New principle: Channel registration 暴露 typed application command facade(reuse existing CQRS command dispatch skeleton);Host 仅 adapt HTTP;provisioning adapters 只调 existing NyxID REST surfaces(**不修改 NyxID 仓库**);local mirror writes 进 standard command skeleton via narrow dispatch port。**不引入新 actor type / 新 envelope / 新 projection phase**(reflector force-pick minimal,排除 structural 的 ChannelRelayRegistrationRunGAgent)。
public sealed class ChannelRegistrationCommandFacade
{
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=public rebuild surfaces, new=internal Runtime startup helper only
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=facade RebuildProjectionAsync, new=facade handles register/unregister only
    // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=relay-local rebuild command DI, new=runtime startup dispatch only
    private readonly ICommandDispatchPipeline<ChannelBotRegisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> _registerDispatchPipeline;
    private readonly ICommandDispatchService<ChannelBotUnregisterCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> _unregisterDispatchService;
    private readonly ICommandDispatchService<ChannelBotUpdateRuntimeConfigCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> _updateRuntimeConfigDispatchService;

    internal ChannelRegistrationCommandFacade(
        ICommandDispatchPipeline<ChannelBotRegisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> registerDispatchPipeline,
        ICommandDispatchService<ChannelBotUnregisterCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> unregisterDispatchService,
        ICommandDispatchService<ChannelBotUpdateRuntimeConfigCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> updateRuntimeConfigDispatchService)
    {
        _registerDispatchPipeline = registerDispatchPipeline ?? throw new ArgumentNullException(nameof(registerDispatchPipeline));
        _unregisterDispatchService = unregisterDispatchService ?? throw new ArgumentNullException(nameof(unregisterDispatchService));
        _updateRuntimeConfigDispatchService = updateRuntimeConfigDispatchService ?? throw new ArgumentNullException(nameof(updateRuntimeConfigDispatchService));
    }

    public async Task<ChannelRegistrationCommandAcceptedReceipt> RegisterLocalMirrorAsync(
        ChannelBotRegisterCommand command,
        CancellationToken ct = default)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: provisioning services 手写 local mirror envelope 并直调 runtime/dispatch。
        //   New principle: local mirror writes 只进入 typed command facade 和 standard command skeleton。
        ArgumentNullException.ThrowIfNull(command);
        if (!NyxChannelBotIdentity.IsValid(command.NyxChannelBotId))
            throw new ArgumentException("missing_nyx_channel_bot_id", nameof(command));
        _ = ChannelPlatformId.FromCanonical(command.Platform);
        var prepared = await _registerDispatchPipeline.PrepareAsync(command, ct);
        if (!prepared.Succeeded || prepared.Target is null)
        {
            throw new ChannelRegistrationCommandDispatchException(
                "local_mirror_dispatch_failed",
                acceptanceUnknown: false);
        }

        // Cancellation known before dispatch cannot have admitted this command.
        // Once dispatch starts, failures still carry unknown acceptance semantics.
        ct.ThrowIfCancellationRequested();
        DispatchAdmission admission;
        try
        {
            admission = await _registerDispatchPipeline.DispatchPreparedAsync(prepared.Target, ct);
        }
        catch (Exception ex)
        {
            throw new ChannelRegistrationCommandDispatchException(
                "Channel registration command dispatch acceptance is unknown.",
                acceptanceUnknown: true,
                ex);
        }

        if (!admission.Accepted)
        {
            throw new ChannelRegistrationCommandDispatchException(
                "local_mirror_dispatch_failed",
                acceptanceUnknown: false);
        }

        return prepared.Target.Receipt;
    }

    public async Task<ChannelRegistrationCommandAcceptedReceipt> UnregisterAsync(
        string registrationId,
        CancellationToken ct = default)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: delete endpoint/tool 查询后手写 dispatch unregister。
        //   New principle: unregister 只通过 typed command facade 投递到标准 command skeleton。
        var result = await _unregisterDispatchService.DispatchAsync(
            new ChannelBotUnregisterCommand
            {
                RegistrationId = registrationId ?? string.Empty,
            },
            ct);
        return ResolveReceipt(result);
    }

    public async Task<ChannelRegistrationCommandAcceptedReceipt> UpdateRuntimeConfigAsync(
        string registrationId,
        ChannelBotRuntimeConfig? runtimeConfig,
        string defaultSkillName,
        ChannelRegistrationServiceSelection serviceSelection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(serviceSelection);
        var result = await _updateRuntimeConfigDispatchService.DispatchAsync(
            new ChannelBotUpdateRuntimeConfigCommand
            {
                RegistrationId = registrationId ?? string.Empty,
                RuntimeConfig = runtimeConfig?.Clone(),
                DefaultSkillName = defaultSkillName ?? string.Empty,
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                AuthorizationMode = serviceSelection.AuthorizationMode,
                RegistrationServiceAllowlist = serviceSelection.AuthorizationMode ==
                    ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist
                        ? new ChannelRegistrationServiceAllowlist
                        {
                            ServiceIds = { serviceSelection.ServiceIds },
                        }
                        : null,
            },
            ct);
        return ResolveReceipt(result);
    }

    private static ChannelRegistrationCommandAcceptedReceipt ResolveReceipt(
        CommandDispatchResult<ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> result)
    {
        if (result.Succeeded && result.Receipt is not null)
            return result.Receipt;

        throw new InvalidOperationException($"Channel registration command dispatch failed: {result.Error}");
    }
}

/// <summary>Validates caller-authorized existing Bot identity before adoption.</summary>
public sealed class ChannelRelayRegistrationFacade(
    INyxChannelBotAdoptionService adoptionService,
    VerifiedNyxChannelBotDetail.Reader botReader,
    IChannelRegistrationOwnerResolver ownerResolver,
    ChannelAgentKeyWriteMode writeMode = ChannelAgentKeyWriteMode.Disabled)
{
    public async Task<NyxChannelBotAdoptionResult> RegisterAsync(
        ChannelRelayRegistrationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NyxChannelBotIdentity.IsValid(request.NyxChannelBotId))
            return Failure("missing_nyx_channel_bot_id");
        if (string.IsNullOrWhiteSpace(request.Platform))
            return Failure("invalid_channel_bot_detail");
        ChannelPlatformId canonicalPlatform;
        try
        {
            // Application callers already cross the external Host/tool boundary. Keep this
            // assertion canonical and reject missed normalization before owner/detail reads.
            canonicalPlatform = ChannelPlatformId.FromCanonical(request.Platform);
        }
        catch (ArgumentException)
        {
            return Failure("invalid_channel_bot_detail");
        }
        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return Failure("missing_access_token");
        if (string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
            return Failure("missing_webhook_base_url");
        if (!NyxRelayCallbackUrl.IsSecureBaseUrl(request.WebhookBaseUrl))
            return Failure("insecure_webhook_base_url");
        if (string.IsNullOrWhiteSpace(request.ScopeId))
            return Failure("missing_scope_id");
        if (writeMode != ChannelAgentKeyWriteMode.NyxIdDefault)
            return Failure("channel_agent_key_write_gate_closed");

        var owner = await ownerResolver.ResolveAsync(request.AccessToken, request.ScopeId, ct);
        if (!owner.Succeeded)
            return Failure(owner.ErrorCode);
        var detail = await botReader.ReadAsync(request.AccessToken, request.NyxChannelBotId,
            canonicalPlatform.Value, owner.Owner!, ct);
        if (!detail.Succeeded)
            return Failure(detail.ErrorCode);
        var canonicalRequest = request with { Platform = detail.Bot!.Platform.Value };
        var result = await adoptionService.AdoptAsync(new(detail.Bot, canonicalRequest), ct);
        return result.Succeeded ? result : result with
        {
            Error = NyxApiResponseHelper.NormalizePublicFailureReason(result.Error),
            ErrorDetail = null,
        };

        NyxChannelBotAdoptionResult Failure(string error) => new(false, "error", request.Platform, Error: error);
    }
}

public sealed record ChannelRelayRegistrationRequest(
    string Platform,
    string AccessToken,
    string WebhookBaseUrl,
    string ScopeId,
    string Label,
    string NyxProviderSlug,
    string NyxChannelBotId,
    string DefaultSkillName = "",
    ChannelBotRuntimeConfig? RuntimeConfig = null,
    ChannelRegistrationServiceSelection? RequestedServiceSelection = null)
{
    public ChannelRegistrationServiceSelection ServiceSelection =>
        RequestedServiceSelection ?? ChannelRegistrationServiceSelection.NyxIdDefault;

    public ChannelBotRuntimeConfig? RuntimeConfigCopy => RuntimeConfig?.Clone();
}

internal sealed record ChannelBotRegistrationCommandTarget(IActor Actor) : IActorCommandDispatchTarget
{
    public string TargetId => ChannelBotRegistrationGAgent.WellKnownId;
}

internal sealed class ChannelBotRegistrationCommandTargetResolver<TCommand>
    : ICommandTargetResolver<TCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandStartError>
{
    private readonly IActorRuntime _actorRuntime;

    public ChannelBotRegistrationCommandTargetResolver(IActorRuntime actorRuntime)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
    }

    public async Task<CommandTargetResolution<ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandStartError>> ResolveAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: static command helper 同时负责 actor lifecycle 和 envelope dispatch。
        //   New principle: target resolver 只解析/创建权威 registration actor，dispatch 留给 skeleton。
        var actor = await _actorRuntime.GetAsync(ChannelBotRegistrationGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<ChannelBotRegistrationGAgent>(
                ChannelBotRegistrationGAgent.WellKnownId,
                ct);

        return actor is null
            ? CommandTargetResolution<ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandStartError>.Failure(ChannelRegistrationCommandStartError.StoreActorUnavailable)
            : CommandTargetResolution<ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandStartError>.Success(new ChannelBotRegistrationCommandTarget(actor));
    }
}

internal sealed class ChannelBotRegistrationCommandEnvelopeFactory :
    ICommandEnvelopeFactory<ChannelBotRegisterCommand>,
    ICommandEnvelopeFactory<ChannelBotUnregisterCommand>,
    ICommandEnvelopeFactory<ChannelBotUpdateRuntimeConfigCommand>,
    ICommandEnvelopeFactory<ChannelBotWorkflowResultDeliveryRepairRequestCommand>,
    ICommandEnvelopeFactory<ChannelBotWorkflowResultDeliveryRepairPrepareCommand>,
    ICommandEnvelopeFactory<ChannelBotWorkflowResultDeliveryRepairCompleteCommand>,
    ICommandEnvelopeFactory<ChannelBotWorkflowResultDeliveryRepairFailCommand>
{
    private const string PublisherActorId = "channel-runtime.registration-store";

    public EventEnvelope CreateEnvelope(ChannelBotRegisterCommand command, CommandContext context) =>
        CreateEnvelopeCore(command, context);

    public EventEnvelope CreateEnvelope(ChannelBotUnregisterCommand command, CommandContext context) =>
        CreateEnvelopeCore(command, context);

    public EventEnvelope CreateEnvelope(ChannelBotUpdateRuntimeConfigCommand command, CommandContext context) =>
        CreateEnvelopeCore(command, context);

    public EventEnvelope CreateEnvelope(
        ChannelBotWorkflowResultDeliveryRepairRequestCommand command,
        CommandContext context) =>
        CreateEnvelopeCore(command, context);

    public EventEnvelope CreateEnvelope(
        ChannelBotWorkflowResultDeliveryRepairPrepareCommand command,
        CommandContext context) =>
        CreateEnvelopeCore(command, context);

    public EventEnvelope CreateEnvelope(
        ChannelBotWorkflowResultDeliveryRepairCompleteCommand command,
        CommandContext context) =>
        CreateEnvelopeCore(command, context);

    public EventEnvelope CreateEnvelope(
        ChannelBotWorkflowResultDeliveryRepairFailCommand command,
        CommandContext context) =>
        CreateEnvelopeCore(command, context);

    private static EventEnvelope CreateEnvelopeCore(IMessage command, CommandContext context)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: each caller built EventEnvelope directly around registration commands.
        //   New principle: one envelope factory reuses the existing EventEnvelope contract; no new envelope/projection phase.
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, ChannelBotRegistrationGAgent.WellKnownId),
        };
    }
}

internal sealed class ChannelRegistrationCommandReceiptFactory
    : ICommandReceiptFactory<ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt>
{
    public ChannelRegistrationCommandAcceptedReceipt Create(
        ChannelBotRegistrationCommandTarget target,
        CommandContext context)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: callers treated dispatch success as an untyped helper completion.
        //   New principle: the command skeleton returns an honest accepted receipt with stable command/correlation ids.
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new ChannelRegistrationCommandAcceptedReceipt(
            target.TargetId,
            context.CommandId,
            context.CorrelationId);
    }
}
