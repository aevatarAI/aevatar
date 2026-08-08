using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Google.Protobuf;

namespace Aevatar.GAgents.NyxidChat;

public interface INyxIdChatToolVerificationPort
{
    Task<NyxIdChatToolVerificationResult> VerifyAsync(
        NyxIdChatOperationKey key,
        NyxIdChatToolVerificationInput input,
        CancellationToken ct);
}

public sealed class NyxIdChatToolVerificationPort : INyxIdChatToolVerificationPort
{
    internal const string UnavailableCode = "NYXID_CHAT_TOOL_VERIFICATION_UNAVAILABLE";
    internal const string FailedCode = "NYXID_CHAT_TOOL_VERIFICATION_FAILED";

    private readonly INyxIdAdmittedOperationToolFactory? _toolFactory;
    private readonly IAgentToolExecutionPort? _executionPort;

    public NyxIdChatToolVerificationPort(
        INyxIdAdmittedOperationToolFactory? toolFactory = null,
        IAgentToolExecutionPort? executionPort = null)
    {
        _toolFactory = toolFactory;
        _executionPort = executionPort;
    }

    public async Task<NyxIdChatToolVerificationResult> VerifyAsync(
        NyxIdChatOperationKey key,
        NyxIdChatToolVerificationInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(input);
        var contract = input.ReadBack;
        if (!NyxIdChatOperationAdmissionPolicy.IsValidReadBack(contract) ||
            contract.Assertion.ExpectedValueSource ==
                AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId &&
            string.IsNullOrWhiteSpace(input.ProviderResourceId) ||
            _toolFactory?.CreateRead(contract.ReadOperation) is not { } tool ||
            _executionPort is null)
        {
            return Unavailable(input, UnavailableCode,
                "The admitted verification read is unavailable.");
        }

        var callId = $"verify:{key.OperationId}:{key.OperationGeneration}";
        var context = AgentToolExecutionContextMapper.FromPayload(input.ToolContext) with
        {
            Request = new AgentToolRequestIdentity(
                key.OperationId,
                callId,
                key.OperationId),
            ExecutionOwner = AgentToolExecutionOwners.Actor(key.ConversationActorId),
            OperationAdmission = AgentToolOperationAdmissionPayloadMapper.FromPayload(
                contract.ReadOperation),
            InvocationSurface = AgentToolInvocationSurface.HumanSession,
            Chat = new AgentChatInvocationContext(
                AgentChatInvocationSurface.NyxIdAssistant,
                key.ConversationActorId,
                key.TurnId,
                key.TaskId,
                key.StepId,
                null),
        };
        var outcome = await _executionPort.ExecuteAsync(
            new AgentToolExecutionRequest(
                tool,
                JsonFormatter.Default.Format(contract.Arguments),
                context,
                AgentToolApprovalContinuationMode.None,
                ApprovalGrant: null),
            ct).ConfigureAwait(false);
        if (outcome.Kind is not (AgentToolExecutionOutcomeKind.Executed or
                                AgentToolExecutionOutcomeKind.ExecutedAuditIncomplete) ||
            outcome.Receipt.Status != AgentToolReceiptStatus.Success ||
            !TryEvaluate(
                outcome.ResultJson,
                contract.ReadOperation,
                contract.Assertion,
                input.ProviderResourceId,
                out var matched))
        {
            return Unavailable(input, FailedCode,
                "The admitted verification read did not produce usable typed evidence.");
        }

        if (!matched && contract.Assertion.Match == AgentToolReadBackMatchPayload.ArrayContainsEquals)
        {
            return Unavailable(input, UnavailableCode,
                "The bounded verification read did not contain the provider resource; absence is not proof of non-application.");
        }

        return new NyxIdChatToolVerificationResult
        {
            EffectStepId = input.EffectStepId,
            Disposition = matched
                ? NyxIdChatToolVerificationDisposition.Applied
                : NyxIdChatToolVerificationDisposition.NotApplied,
            ReadOperation = contract.ReadOperation.Clone(),
            CheckName = contract.CheckName,
        };
    }

    private static NyxIdChatToolVerificationResult Unavailable(
        NyxIdChatToolVerificationInput input,
        string code,
        string message) => new()
    {
        EffectStepId = input.EffectStepId,
        Disposition = NyxIdChatToolVerificationDisposition.Unavailable,
        ReadOperation = input.ReadBack?.ReadOperation?.Clone(),
        CheckName = input.ReadBack?.CheckName ?? string.Empty,
        FailureCode = code,
        SafeMessage = message,
    };

    internal static bool TryEvaluate(
        string resultJson,
        AgentToolOperationAdmissionPayload readOperation,
        AgentToolReadBackAssertionPayload assertion,
        out bool matched) =>
        TryEvaluate(resultJson, readOperation, assertion, string.Empty, out matched);

    internal static bool TryEvaluate(
        string resultJson,
        AgentToolOperationAdmissionPayload readOperation,
        AgentToolReadBackAssertionPayload assertion,
        string providerResourceId,
        out bool matched)
    {
        matched = false;
        try
        {
            var root = JsonNode.Parse(resultJson);
            if (root?["kind"]?.GetValue<string>() != "connected_service_read_projection" ||
                root["status"]?.GetValue<string>() != "succeeded" ||
                !MatchesProvenance(root["provenance"], readOperation))
                return false;

            var current = root["data"];
            var found = TryResolvePointer(current, assertion.JsonPointer, out var value);
            var expectedValue = ResolveExpectedValue(assertion, providerResourceId);
            matched = assertion.Match switch
            {
                AgentToolReadBackMatchPayload.Exists => found,
                AgentToolReadBackMatchPayload.Absent => !found,
                AgentToolReadBackMatchPayload.Equals => found &&
                    EqualsExpected(value, expectedValue),
                AgentToolReadBackMatchPayload.ArrayContainsEquals => found &&
                    value is JsonArray array &&
                    expectedValue is not null &&
                    array.Any(element =>
                        TryResolvePointer(element, assertion.ElementJsonPointer, out var candidate) &&
                        EqualsExpected(candidate, expectedValue)),
                _ => false,
            };
            return assertion.Match != AgentToolReadBackMatchPayload.Unspecified;
        }
        catch
        {
            return false;
        }
    }

    private static Google.Protobuf.WellKnownTypes.Value? ResolveExpectedValue(
        AgentToolReadBackAssertionPayload assertion,
        string providerResourceId) =>
        assertion.ExpectedValueSource == AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId
            ? string.IsNullOrWhiteSpace(providerResourceId)
                ? null
                : Google.Protobuf.WellKnownTypes.Value.ForString(providerResourceId.Trim())
            : assertion.ExpectedValue;

    private static bool EqualsExpected(
        JsonNode? actual,
        Google.Protobuf.WellKnownTypes.Value? expected) =>
        expected is not null &&
        JsonNode.DeepEquals(actual, JsonNode.Parse(JsonFormatter.Default.Format(expected)));

    private static bool MatchesProvenance(
        JsonNode? provenance,
        AgentToolOperationAdmissionPayload readOperation)
    {
        var admission = AgentToolOperationAdmissionPayloadMapper.FromPayload(readOperation);
        return admission is not null &&
               provenance?["source_kind"]?.GetValue<string>() == "nyxid_connected_service" &&
               string.Equals(
                   provenance["operation_selector_digest"]?.GetValue<string>(),
                   AgentToolOperationSelector.ComputeDigest(admission),
                   StringComparison.Ordinal);
    }

    private static bool TryResolvePointer(JsonNode? root, string pointer, out JsonNode? value)
    {
        value = root;
        if (pointer == string.Empty)
            return root is not null;
        if (root is null || !pointer.StartsWith("/", StringComparison.Ordinal))
            return false;

        foreach (var encoded in pointer.Split('/').Skip(1))
        {
            var segment = encoded.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (value is JsonObject obj && obj.TryGetPropertyValue(segment, out value))
                continue;
            if (value is JsonArray array && int.TryParse(segment, out var index) &&
                index >= 0 && index < array.Count)
            {
                value = array[index];
                continue;
            }
            value = null;
            return false;
        }
        return true;
    }
}
