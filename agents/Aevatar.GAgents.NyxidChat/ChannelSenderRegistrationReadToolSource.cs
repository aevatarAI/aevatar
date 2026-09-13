using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.Foundation.Abstractions.Tools;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Exposes only the bound sender's own Aevatar registration list. The native outer
/// capability admits discovery independently of the platform Bot; the exact inner
/// connected-service operation still crosses the shared execution and audit boundary.
/// </summary>
public sealed class ChannelSenderRegistrationReadToolSource(
    IAgentToolExecutionPort executionPort,
    NyxIdToolOptions? options = null,
    INyxIdApiClientFactory? apiClientFactory = null,
    INyxIdChannelRegistrationReadCapabilityIssuer? capabilityIssuer = null,
    ILogger<ChannelSenderRegistrationReadToolSource>? logger = null,
    NyxIdDelegationTokenLease? delegationTokenLease = null) : IAgentToolSource
{
    private const string RegistrationListPath = "/api/channels/registrations";
    private readonly ILogger _logger = logger ?? NullLogger<ChannelSenderRegistrationReadToolSource>.Instance;

    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var context = AgentToolRequestContext.Current;
        if (context is null || SenderReadIdentity.From(context) is not { } identity ||
            options is null || apiClientFactory is null || capabilityIssuer is null)
        {
            return [];
        }

        try
        {
            var senderContext = await CreateSenderContextAsync(context, identity, ct).ConfigureAwait(false);
            using var client = apiClientFactory.CreateClient();
            using var transientLease = delegationTokenLease is null
                ? new NyxIdDelegationTokenLease(apiClientFactory, TimeProvider.System)
                : null;
            var tools = await DiscoverReadsAsync(senderContext, client,
                delegationTokenLease ?? transientLease!, ct).ConfigureAwait(false);
            return tools.Select(tool => (IAgentTool)new SenderRegistrationReadTool(this, identity, tool)).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sender Channel registration read discovery failed. errorType={ErrorType}", ex.GetType().Name);
            return [];
        }
    }

    private async Task<AgentToolExecutionContext> CreateSenderContextAsync(
        AgentToolExecutionContext context,
        SenderReadIdentity identity,
        CancellationToken ct)
    {
        var capability = await capabilityIssuer!.IssueByBindingIdAsync(new ExternalSubjectRef
        {
            Platform = identity.Platform,
            Tenant = identity.Tenant,
            ExternalUserId = identity.ExternalUserId,
        }, identity.BindingId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(capability.AccessToken))
            throw new InvalidOperationException("sender_registration_read_credential_unavailable");

        return context with
        {
            CredentialSource = AgentToolCredentialSource.BearerToken,
            DurableNyxIdCredential = null,
            OperationAdmission = null,
            Credentials = new AgentToolCredentials(
                capability.AccessToken,
                capability.AccessToken,
                capability.AccessToken,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
                NyxIdCredentialAuthority: AgentToolNyxIdCredentialAuthority.ToolExecutionContext),
        };
    }

    private async Task<IReadOnlyList<IAgentTool>> DiscoverReadsAsync(
        AgentToolExecutionContext senderContext,
        NyxIdApiClient client,
        NyxIdDelegationTokenLease lease,
        CancellationToken ct)
    {
        var source = new NyxIdConnectedServiceToolSource(
            options!, client, new NyxIdServiceInstanceClient(client), delegationTokenLease: lease);
        using var scope = AgentToolContextScope.Push(senderContext);
        var tools = await source.DiscoverToolsForCatalogServiceAsync("aevatar", ct).ConfigureAwait(false);
        return tools.Where(IsOwnRegistrationRead).ToArray();
    }

    private static bool IsOwnRegistrationRead(IAgentTool tool) =>
        tool.IsReadOnly && !tool.IsDestructive &&
        tool is IAgentToolOperationAdmissionOwner
        {
            OperationAdmission:
            {
                CatalogServiceSlug: "aevatar",
                HttpMethod: "GET",
                PathTemplate: RegistrationListPath,
                Identity: AgentToolOperationIdentity.PublishedEndpoint,
                AuthorizationBasis: AgentToolOperationAuthorizationBasis.PublishedContract,
                RequestBody: null,
                ExecutionPolicy:
                {
                    Risk: AgentToolOperationRisk.ReadOnly,
                    Approval: AgentToolOperationApproval.None,
                    EnforcementOwner: AgentToolOperationEnforcementOwner.Aevatar,
                },
            } admission,
        } &&
        admission.Parameters.All(static parameter => !parameter.Required) &&
        !admission.PathParameters.Any();

    private async Task<AgentToolTerminalOutcome> ExecuteAsync(
        SenderRegistrationReadTool tool,
        string callId,
        string toolName,
        string argumentsJson,
        CancellationToken ct)
    {
        if (!HasEmptyArguments(argumentsJson))
            return Failure(callId, toolName, "INVALID_ARGUMENTS", "This sender-bound read accepts only an empty object.");
        var context = AgentToolRequestContext.Current;
        if (context is null || SenderReadIdentity.From(context) != tool.Identity)
        {
            return Failure(callId, toolName, "SENDER_CHANGED",
                "The verified sender binding no longer matches this read.", denied: true);
        }

        try
        {
            // Re-issue at execution: discovery never leaves a token on the tool.
            var senderContext = await CreateSenderContextAsync(context, tool.Identity, ct).ConfigureAwait(false);
            using var client = apiClientFactory!.CreateClient();
            using var transientLease = delegationTokenLease is null
                ? new NyxIdDelegationTokenLease(apiClientFactory, TimeProvider.System)
                : null;
            var currentReads = await DiscoverReadsAsync(senderContext, client,
                delegationTokenLease ?? transientLease!, ct).ConfigureAwait(false);
            var current = currentReads.SingleOrDefault(candidate =>
                candidate is IAgentToolOperationAdmissionOwner owner &&
                string.Equals(AgentToolOperationSelector.ComputeDigest(owner.OperationAdmission),
                    tool.SelectorDigest, StringComparison.Ordinal));
            if (current is not IAgentToolOperationAdmissionOwner currentOwner)
            {
                return Failure(callId, toolName, "OPERATION_CHANGED",
                    "The sender-authorized registration read is no longer available with the same contract.", denied: true);
            }

            senderContext = senderContext with
            {
                OperationAdmission = currentOwner.ResolveOperationAdmission("{}"),
                Request = context.Request with { CallId = $"{callId}:sender-registration-read" },
            };
            var outcome = await executionPort.ExecuteAsync(new AgentToolExecutionRequest(
                current, "{}", senderContext, AgentToolApprovalContinuationMode.None, ApprovalGrant: null), ct)
                .ConfigureAwait(false);
            if (!outcome.AuditCompleted)
            {
                return Failure(callId, toolName, "AUDIT_INCOMPLETE",
                    "The sender registration read did not complete its audit confirmation.");
            }
            var receipt = outcome.Receipt.Clone();
            receipt.CallId = callId;
            receipt.ToolName = toolName;
            return new AgentToolTerminalOutcome(outcome.ResultJson, receipt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sender Channel registration read failed. errorType={ErrorType}", ex.GetType().Name);
            return Failure(callId, toolName, "UNAVAILABLE", "The bound sender's Channel registration read is unavailable.");
        }
    }

    private static bool HasEmptyArguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   !document.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AgentToolTerminalOutcome Failure(
        string callId, string toolName, string reason, string message, bool denied = false)
    {
        var code = "CHANNEL_SENDER_REGISTRATIONS_" + reason;
        return new AgentToolTerminalOutcome(JsonSerializer.Serialize(new { error = code, message }), new AgentToolReceipt
        {
            CallId = callId,
            ToolName = toolName,
            Status = denied ? AgentToolReceiptStatus.Denied : AgentToolReceiptStatus.Error,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ErrorCode = code,
            ErrorMessage = message,
        });
    }

    private sealed record SenderReadIdentity(
        string BindingId, string? NyxUserId, string Platform, string Tenant, string ExternalUserId,
        string? RegistrationId, string? ChannelSenderId)
    {
        public static SenderReadIdentity? From(AgentToolExecutionContext context) =>
            !string.IsNullOrWhiteSpace(context.SenderBinding.BindingId) && context.NyxIdAuthority.IsComplete
                ? new SenderReadIdentity(context.SenderBinding.BindingId, context.SenderBinding.NyxUserId,
                    context.NyxIdAuthority.Platform!, context.NyxIdAuthority.Tenant ?? string.Empty,
                    context.NyxIdAuthority.ExternalUserId!, context.Channel.BotRegistrationId, context.Channel.SenderId)
                : null;
    }

    // Native boundary intentionally has no outer operation admission: the platform
    // Agent Key owns this capability call, while the inner read belongs to the sender.
    private sealed class SenderRegistrationReadTool : IAgentTool
    {
        private readonly ChannelSenderRegistrationReadToolSource _source;

        public SenderRegistrationReadTool(ChannelSenderRegistrationReadToolSource source, SenderReadIdentity identity, IAgentTool operation)
        {
            _source = source;
            Identity = identity;
            SelectorDigest = AgentToolOperationSelector.ComputeDigest(((IAgentToolOperationAdmissionOwner)operation).OperationAdmission);
            Name = "sender_" + operation.Name;
            Description = $"Read the verified sender's own Aevatar Channel registrations using GET {RegistrationListPath}. " +
                          operation.Description + " Accepts only {} and cannot select another account or an admin scope.";
            Presentation = operation.Presentation.Clone();
            Presentation.InvocationName = Name;
            Presentation.Description = Description;
        }

        public SenderReadIdentity Identity { get; }
        public string SelectorDigest { get; }
        public string Name { get; }
        public string Description { get; }
        public string ParametersSchema => """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";
        public bool IsReadOnly => true;
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
        public ToolPresentationDescriptor Presentation { get; }

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            (await ExecuteWithOutcomeAsync(AgentToolRequestContext.Current?.Request.CallId ?? string.Empty,
                Name, argumentsJson, ct).ConfigureAwait(false)).ResultJson;

        public Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
            string callId, string toolName, string argumentsJson, CancellationToken ct = default) =>
            _source.ExecuteAsync(this, callId, toolName, argumentsJson, ct);
    }
}
