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
            !AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryGetCanonicalSource(
                contract.Assertion,
                out var expectedValueSource) ||
            expectedValueSource == AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId &&
            string.IsNullOrWhiteSpace(input.ProviderResourceId) ||
            _toolFactory?.CreateRead(contract.ReadOperation) is not { } tool ||
            _executionPort is null)
        {
            return Unavailable(input, UnavailableCode,
                "The admitted verification read is unavailable.");
        }

        var baseCallId = $"verify:{key.OperationId}:{key.OperationGeneration}";
        var baseContext = AgentToolExecutionContextMapper.FromPayload(input.ToolContext) with
        {
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
        var arguments = contract.Arguments.Clone();
        var seenPageTokens = new HashSet<string>(StringComparer.Ordinal);
        var maxPages = contract.Pagination is null ? 1 : checked((int)contract.Pagination.MaxPages);
        for (var page = 1; page <= maxPages; page++)
        {
            var callId = page == 1 ? baseCallId : $"{baseCallId}:page:{page}";
            var requestId = contract.Pagination is null
                ? key.OperationId
                : $"{key.OperationId}:read-page:{page}";
            var context = baseContext with
            {
                Request = new AgentToolRequestIdentity(requestId, callId, key.OperationId),
            };
            var outcome = await _executionPort.ExecuteAsync(
                new AgentToolExecutionRequest(
                    tool,
                    JsonFormatter.Default.Format(arguments),
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

            if (matched)
                return Result(input, NyxIdChatToolVerificationDisposition.Applied);

            if (contract.NotAppliedAssertion is not null)
            {
                if (!TryEvaluate(
                        outcome.ResultJson,
                        contract.ReadOperation,
                        contract.NotAppliedAssertion,
                        string.Empty,
                        out var notAppliedMatched))
                {
                    return Unavailable(input, FailedCode,
                        "The admitted verification read did not produce usable typed evidence.");
                }
                if (notAppliedMatched)
                    return Result(input, NyxIdChatToolVerificationDisposition.NotApplied);
            }

            if (contract.Pagination is null)
            {
                return contract.NotAppliedAssertion is not null ||
                       contract.Assertion.Match == AgentToolReadBackMatchPayload.ArrayContainsEquals
                    ? Unavailable(input, UnavailableCode,
                        "The verification read did not prove application or non-application.")
                    : Result(input, NyxIdChatToolVerificationDisposition.NotApplied);
            }

            if (!TryReadPagination(
                    outcome.ResultJson,
                    contract.ReadOperation,
                    contract.Pagination,
                    out var hasMore,
                    out var pageToken))
            {
                return Unavailable(input, UnavailableCode,
                    "The verification pagination contract returned unusable typed evidence.");
            }
            if (!hasMore)
                return Result(input, NyxIdChatToolVerificationDisposition.NotApplied);
            if (page == maxPages ||
                string.IsNullOrWhiteSpace(pageToken) ||
                !seenPageTokens.Add(pageToken))
            {
                return Unavailable(input, UnavailableCode,
                    "The exhaustive verification read did not reach a terminal page.");
            }
            if (!TryWritePaginationArgument(arguments, contract.Pagination, pageToken))
            {
                return Unavailable(input, UnavailableCode,
                    "The verification continuation token could not be applied safely.");
            }
        }

        return Unavailable(input, UnavailableCode,
            "The exhaustive verification read did not reach a terminal page.");
    }

    private static NyxIdChatToolVerificationResult Result(
        NyxIdChatToolVerificationInput input,
        NyxIdChatToolVerificationDisposition disposition) => new()
    {
        EffectStepId = input.EffectStepId,
        Disposition = disposition,
        ReadOperation = input.ReadBack.ReadOperation.Clone(),
        CheckName = input.ReadBack.CheckName,
    };

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
            if (!AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryGetCanonicalSource(
                    assertion,
                    out var expectedValueSource))
            {
                return false;
            }
            if (!TryReadProjectionData(resultJson, readOperation, out var current))
                return false;

            var found = TryResolvePointer(current, assertion.JsonPointer, out var value);
            var expectedValue = ResolveExpectedValue(
                assertion,
                expectedValueSource,
                providerResourceId);
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

    private static bool TryReadPagination(
        string resultJson,
        AgentToolOperationAdmissionPayload readOperation,
        AgentToolReadBackPaginationPayload pagination,
        out bool hasMore,
        out string pageToken)
    {
        hasMore = false;
        pageToken = string.Empty;
        if (!TryReadProjectionData(resultJson, readOperation, out var data) ||
            !TryResolvePointer(data, pagination.HasMoreJsonPointer, out var hasMoreNode) ||
            hasMoreNode is not JsonValue hasMoreValue ||
            !hasMoreValue.TryGetValue<bool>(out hasMore))
        {
            return false;
        }

        if (!hasMore)
            return true;

        if (!TryResolvePointer(data, pagination.PageTokenJsonPointer, out var tokenNode) ||
            tokenNode is not JsonValue tokenValue ||
            !tokenValue.TryGetValue<string>(out var resolvedPageToken) ||
            string.IsNullOrWhiteSpace(resolvedPageToken))
        {
            return false;
        }

        pageToken = resolvedPageToken;
        return true;
    }

    private static bool TryWritePaginationArgument(
        Google.Protobuf.WellKnownTypes.Struct arguments,
        AgentToolReadBackPaginationPayload pagination,
        string pageToken)
    {
        if (pagination.PageTokenLocation != AgentToolOperationParameterLocationPayload.Query ||
            string.IsNullOrWhiteSpace(pagination.PageTokenArgumentName) ||
            string.IsNullOrWhiteSpace(pageToken))
        {
            return false;
        }

        Google.Protobuf.WellKnownTypes.Struct query;
        if (!arguments.Fields.TryGetValue("query", out var queryValue) ||
            queryValue.KindCase != Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StructValue)
        {
            query = new Google.Protobuf.WellKnownTypes.Struct();
            arguments.Fields["query"] = Google.Protobuf.WellKnownTypes.Value.ForStruct(query);
        }
        else
        {
            query = queryValue.StructValue;
        }
        query.Fields[pagination.PageTokenArgumentName] =
            Google.Protobuf.WellKnownTypes.Value.ForString(pageToken);
        return true;
    }

    private static bool TryReadProjectionData(
        string resultJson,
        AgentToolOperationAdmissionPayload readOperation,
        out JsonNode? data)
    {
        data = null;
        try
        {
            var root = JsonNode.Parse(resultJson);
            if (root?["kind"]?.GetValue<string>() != "connected_service_read_projection" ||
                root["status"]?.GetValue<string>() != "succeeded" ||
                !MatchesProvenance(root["provenance"], readOperation))
            {
                return false;
            }

            data = root["data"];
            return data is not null;
        }
        catch
        {
            return false;
        }
    }

    private static Google.Protobuf.WellKnownTypes.Value? ResolveExpectedValue(
        AgentToolReadBackAssertionPayload assertion,
        AgentToolReadBackExpectedValueSourcePayload expectedValueSource,
        string providerResourceId) =>
        expectedValueSource == AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId
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
