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

    internal ChannelRegistrationCommandFacade(
        ICommandDispatchPipeline<ChannelBotRegisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> registerDispatchPipeline,
        ICommandDispatchService<ChannelBotUnregisterCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> unregisterDispatchService)
    {
        _registerDispatchPipeline = registerDispatchPipeline ?? throw new ArgumentNullException(nameof(registerDispatchPipeline));
        _unregisterDispatchService = unregisterDispatchService ?? throw new ArgumentNullException(nameof(unregisterDispatchService));
    }

    public async Task<ChannelRegistrationCommandAcceptedReceipt> RegisterLocalMirrorAsync(
        ChannelBotRegisterCommand command,
        CancellationToken ct = default)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: provisioning services 手写 local mirror envelope 并直调 runtime/dispatch。
        //   New principle: local mirror writes 只进入 typed command facade 和 standard command skeleton。
        ArgumentNullException.ThrowIfNull(command);
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

    private static ChannelRegistrationCommandAcceptedReceipt ResolveReceipt(
        CommandDispatchResult<ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError> result)
    {
        if (result.Succeeded && result.Receipt is not null)
            return result.Receipt;

        throw new InvalidOperationException($"Channel registration command dispatch failed: {result.Error}");
    }
}

// Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
//   Old pattern: HTTP endpoint selected platform provisioning services and owned registration saga branching.
//   New principle: typed application facade owns platform selection; Host only adapts HTTP request/response shapes.
public sealed class ChannelRelayRegistrationFacade
{
    private readonly IReadOnlyDictionary<string, INyxChannelBotProvisioningService> _provisioningServices;
    private readonly ChannelAgentKeyWriteMode _writeMode;

    public ChannelRelayRegistrationFacade(
        IEnumerable<INyxChannelBotProvisioningService> provisioningServices,
        ChannelAgentKeyWriteMode writeMode = ChannelAgentKeyWriteMode.Disabled)
    {
        _provisioningServices = BuildProvisioningServiceMap(provisioningServices);
        _writeMode = writeMode;
    }

    public async Task<NyxChannelBotProvisioningResult> RegisterAsync(
        ChannelRelayRegistrationRequest request,
        CancellationToken ct = default)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: endpoint performed platform selection before invoking provisioning.
        //   New principle: facade selects typed provisioning adapter; adapter only calls NyxID and local mirror facade.
        ArgumentNullException.ThrowIfNull(request);

        var platform = request.Platform.Trim().ToLowerInvariant();
        if (!_provisioningServices.TryGetValue(platform, out var provisioningService))
        {
            return new NyxChannelBotProvisioningResult(
                Succeeded: false,
                Status: "error",
                Platform: platform,
                Error: "unsupported_platform",
                Note: $"Platform '{platform}' is not in the supported production contract. ChannelRuntime currently provisions relay registrations for: {string.Join(", ", _provisioningServices.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))}.");
        }

        if (_writeMode != ChannelAgentKeyWriteMode.NyxIdDefault)
        {
            return new NyxChannelBotProvisioningResult(
                Succeeded: false,
                Status: "error",
                Platform: platform,
                Error: "channel_agent_key_write_gate_closed");
        }

        var result = await provisioningService.ProvisionAsync(request.ToProvisioningRequest(platform), ct);
        if (result.Succeeded)
            return result;

        var failureReason = NyxApiResponseHelper.NormalizePublicFailureReason(result.Error);
        return result with { Error = failureReason };
    }

    private static IReadOnlyDictionary<string, INyxChannelBotProvisioningService> BuildProvisioningServiceMap(
        IEnumerable<INyxChannelBotProvisioningService> provisioningServices)
    {
        ArgumentNullException.ThrowIfNull(provisioningServices);

        var serviceMap = new Dictionary<string, INyxChannelBotProvisioningService>(StringComparer.OrdinalIgnoreCase);
        foreach (var provisioningService in provisioningServices)
        {
            if (provisioningService is null)
                continue;

            var platformKey = provisioningService.Platform?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(platformKey))
                continue;

            if (!serviceMap.TryAdd(platformKey, provisioningService))
            {
                throw new InvalidOperationException(
                    $"Multiple Nyx channel provisioning services are registered for platform '{platformKey}'.");
            }
        }

        return serviceMap;
    }
}

public sealed record ChannelRelayRegistrationRequest(
    string Platform,
    string AccessToken,
    string WebhookBaseUrl,
    string ScopeId,
    string Label,
    string NyxProviderSlug,
    NyxChannelLarkCredentials? Lark = null,
    IReadOnlyDictionary<string, string>? Credentials = null,
    string DefaultSkillName = "",
    ChannelRegistrationServiceSelection? RequestedServiceSelection = null)
{
    public ChannelRegistrationServiceSelection ServiceSelection =>
        RequestedServiceSelection ?? ChannelRegistrationServiceSelection.NyxIdDefault;

    public NyxChannelBotProvisioningRequest ToProvisioningRequest(string platform)
    {
        // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
        //   Old pattern: HTTP endpoints rebuilt provisioning DTOs inline while owning platform branching.
        //   New principle: relay registration request mapping stays typed and local to the application facade boundary.
        return new NyxChannelBotProvisioningRequest(
            Platform: platform,
            AccessToken: AccessToken,
            WebhookBaseUrl: WebhookBaseUrl,
            ScopeId: ScopeId,
            Label: Label,
            NyxProviderSlug: NyxProviderSlug,
            Lark: Lark,
            Credentials: Credentials,
            DefaultSkillName: DefaultSkillName,
            RequestedServiceSelection: ServiceSelection);
    }
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
