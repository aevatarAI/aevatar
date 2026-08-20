using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.ToolProviders;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

internal sealed class NyxIdConnectedServiceReadBackPlan
{
    private readonly AgentToolOperationAdmission _effectOperation;
    private readonly AgentToolOperationAdmission _readOperation;
    private readonly NyxIdAssistantOperationReadBackBinding _binding;

    private NyxIdConnectedServiceReadBackPlan(
        AgentToolOperationAdmission effectOperation,
        AgentToolOperationAdmission readOperation,
        NyxIdAssistantOperationReadBackBinding binding)
    {
        _effectOperation = effectOperation;
        _readOperation = readOperation;
        _binding = binding;
    }

    public static NyxIdConnectedServiceReadBackPlan? Create(
        AgentToolOperationAdmission effectOperation,
        AgentToolOperationAdmission readOperation,
        NyxIdAssistantOperationReadBackBinding binding)
    {
        ArgumentNullException.ThrowIfNull(effectOperation);
        ArgumentNullException.ThrowIfNull(readOperation);
        ArgumentNullException.ThrowIfNull(binding);

        if (effectOperation.ExecutionPolicy.Risk != AgentToolOperationRisk.Write ||
            readOperation.ExecutionPolicy.Risk != AgentToolOperationRisk.ReadOnly ||
            readOperation.ExecutionPolicy.Approval != AgentToolOperationApproval.None ||
            readOperation.ReadBack is not null ||
            !string.Equals(effectOperation.ServiceInstanceId, readOperation.ServiceInstanceId, StringComparison.Ordinal) ||
            !string.Equals(effectOperation.ServiceSlug, readOperation.ServiceSlug, StringComparison.Ordinal) ||
            !string.Equals(
                effectOperation.CatalogServiceSlug,
                readOperation.CatalogServiceSlug,
                StringComparison.Ordinal) ||
            !string.Equals(effectOperation.CatalogDigest, readOperation.CatalogDigest, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(binding.CheckName) ||
            binding.Match == AgentToolReadBackMatch.Unspecified ||
            binding.ArgumentBindings.Count == 0 && binding.ProviderResourceArgument is null ||
            binding.ArgumentBindings.Select(TargetKey)
                .Concat(binding.LiteralReadArguments.Select(TargetKey))
                .Concat(binding.ProviderResourceArgument is null
                    ? []
                    : [TargetKey(binding.ProviderResourceArgument)])
                .GroupBy(static key => key)
                .Any(static group => group.Count() != 1) ||
            binding.ArgumentBindings.Any(argument =>
                !HasArgument(effectOperation, argument.EffectLocation, argument.EffectArgumentName) ||
                !HasArgument(readOperation, argument.ReadLocation, argument.ReadArgumentName)) ||
            binding.LiteralReadArguments.Any(argument =>
                argument.Value is null ||
                !HasArgument(readOperation, argument.ReadLocation, argument.ReadArgumentName)) ||
            !IsValidProviderResourceArgument(
                binding.ProviderResourceArgument,
                readOperation,
                UsesProviderResourceIdentity(binding)) ||
            binding.EffectArgumentConstraints.Any(constraint =>
                constraint.ExpectedValue is null ||
                !HasArgument(effectOperation, constraint.EffectLocation, constraint.EffectArgumentName)) ||
            !IsValidNotAppliedEvidence(binding.NotAppliedEvidence) ||
            !IsValidPagination(binding.Pagination, readOperation, binding.Match) ||
            !RequiredReadArguments(readOperation).All(required =>
                binding.ArgumentBindings.Any(argument => TargetKey(argument) == required) ||
                binding.LiteralReadArguments.Any(argument => TargetKey(argument) == required) ||
                binding.ProviderResourceArgument is not null &&
                TargetKey(binding.ProviderResourceArgument) == required) ||
            binding.Match is AgentToolReadBackMatch.Equals or AgentToolReadBackMatch.ArrayContainsEquals &&
            !UsesProviderResourceIdentity(binding) &&
            !HasArgument(effectOperation, binding.ExpectedValueLocation, binding.ExpectedValueArgumentName) ||
            binding.Match == AgentToolReadBackMatch.ArrayContainsEquals &&
            (string.IsNullOrWhiteSpace(binding.JsonPointer) ||
             string.IsNullOrWhiteSpace(binding.ElementJsonPointer)))
        {
            return null;
        }

        return new NyxIdConnectedServiceReadBackPlan(effectOperation, readOperation, binding);
    }

    public bool TryFreeze(string argumentsJson, out AgentToolOperationReadBack readBack)
    {
        readBack = null!;
        JsonObject? effectArguments;
        try
        {
            effectArguments = JsonNode.Parse(argumentsJson) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
        if (effectArguments is null)
            return false;

        foreach (var constraint in _binding.EffectArgumentConstraints)
        {
            if (!TryReadArgument(
                    effectArguments,
                    constraint.EffectLocation,
                    constraint.EffectArgumentName,
                    out var constrainedNode) ||
                !JsonEquals(constrainedNode, constraint.ExpectedValue))
            {
                return false;
            }
        }

        var readArguments = new JsonObject();
        foreach (var argument in _binding.ArgumentBindings)
        {
            if (!TryReadArgument(
                    effectArguments,
                    argument.EffectLocation,
                    argument.EffectArgumentName,
                    out var value))
            {
                return false;
            }
            WriteArgument(
                readArguments,
                argument.ReadLocation,
                argument.ReadArgumentName,
                value!.DeepClone());
        }

        foreach (var argument in _binding.LiteralReadArguments)
        {
            WriteArgument(
                readArguments,
                argument.ReadLocation,
                argument.ReadArgumentName,
                JsonNode.Parse(JsonFormatter.Default.Format(argument.Value))!);
        }

        Value? expectedValue = null;
        var expectedValueSource = AgentToolReadBackExpectedValueSource.FrozenValue;
        if (_binding.Match is AgentToolReadBackMatch.Equals or AgentToolReadBackMatch.ArrayContainsEquals)
        {
            if (UsesProviderResourceIdentity(_binding))
            {
                expectedValueSource = AgentToolReadBackExpectedValueSource.ProviderResourceId;
            }
            else
            {
                if (!TryReadArgument(
                        effectArguments,
                        _binding.ExpectedValueLocation,
                        _binding.ExpectedValueArgumentName,
                        out var expectedNode))
                {
                    return false;
                }
                expectedValue = JsonParser.Default.Parse<Value>(expectedNode!.ToJsonString());
            }
        }

        var typedArguments = JsonParser.Default.Parse<Struct>(readArguments.ToJsonString());
        var notAppliedAssertion = _binding.NotAppliedEvidence is null
            ? null
            : new AgentToolReadBackAssertion(
                AgentToolReadBackMatch.Equals,
                _binding.NotAppliedEvidence.JsonPointer,
                _binding.NotAppliedEvidence.ExpectedValue.Clone());
        var pagination = _binding.Pagination is null
            ? null
            : new AgentToolReadBackPagination(
                _binding.Pagination.HasMoreJsonPointer,
                _binding.Pagination.PageTokenJsonPointer,
                ToAdmissionLocation(_binding.Pagination.PageTokenLocation),
                _binding.Pagination.PageTokenArgumentName,
                _binding.Pagination.MaxPages);
        var providerResourceArgument = _binding.ProviderResourceArgument is null
            ? null
            : new AgentToolReadBackProviderResourceArgument(
                ToAdmissionLocation(_binding.ProviderResourceArgument.ReadLocation),
                _binding.ProviderResourceArgument.ReadArgumentName);
        readBack = new AgentToolOperationReadBack(
            _readOperation,
            typedArguments,
            new AgentToolReadBackAssertion(
                _binding.Match,
                _binding.JsonPointer ?? string.Empty,
                expectedValue,
                _binding.ElementJsonPointer ?? string.Empty,
                expectedValueSource),
            _binding.CheckName,
            notAppliedAssertion,
            pagination,
            providerResourceArgument,
            _binding.EffectResultIdentityJsonPointer ?? string.Empty);
        return true;
    }

    public string? ExtractProviderResourceId(string? effectResultJson)
        => NyxIdEffectResultIdentityExtractor.ExtractAtPointer(
            effectResultJson,
            _binding.EffectResultIdentityJsonPointer);

    private static bool UsesProviderResourceIdentity(NyxIdAssistantOperationReadBackBinding binding) =>
        !string.IsNullOrWhiteSpace(binding.EffectResultIdentityJsonPointer) &&
        AgentToolEffectResultIdentityJsonPointer.IsValid(binding.EffectResultIdentityJsonPointer);

    private static bool IsValidNotAppliedEvidence(
        NyxIdAssistantReadBackNotAppliedEvidence? evidence) =>
        evidence is null ||
        evidence.ExpectedValue is not null &&
        evidence.ExpectedValue.KindCase != Value.KindOneofCase.None &&
        !string.IsNullOrWhiteSpace(evidence.JsonPointer) &&
        evidence.JsonPointer.StartsWith("/", StringComparison.Ordinal);

    private static bool IsValidProviderResourceArgument(
        NyxIdAssistantReadBackProviderResourceArgument? argument,
        AgentToolOperationAdmission readOperation,
        bool usesProviderResourceIdentity) =>
        argument is null ||
        usesProviderResourceIdentity &&
        (argument.ReadLocation is NyxIdAssistantOperationArgumentLocation.Path or
            NyxIdAssistantOperationArgumentLocation.Query or
            NyxIdAssistantOperationArgumentLocation.Header) &&
        HasArgument(readOperation, argument.ReadLocation, argument.ReadArgumentName);

    private static bool IsValidPagination(
        NyxIdAssistantReadBackPagination? pagination,
        AgentToolOperationAdmission readOperation,
        AgentToolReadBackMatch match) =>
        pagination is null ||
        match == AgentToolReadBackMatch.ArrayContainsEquals &&
        pagination.MaxPages is > 0 and <= 1000 &&
        !string.IsNullOrWhiteSpace(pagination.HasMoreJsonPointer) &&
        pagination.HasMoreJsonPointer.StartsWith("/", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(pagination.PageTokenJsonPointer) &&
        pagination.PageTokenJsonPointer.StartsWith("/", StringComparison.Ordinal) &&
        pagination.PageTokenLocation == NyxIdAssistantOperationArgumentLocation.Query &&
        HasArgument(
            readOperation,
            pagination.PageTokenLocation,
            pagination.PageTokenArgumentName);

    private static bool HasArgument(
        AgentToolOperationAdmission operation,
        NyxIdAssistantOperationArgumentLocation location,
        string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        if (location == NyxIdAssistantOperationArgumentLocation.Body)
            return operation.RequestBody?.Schema.FindProperty(name) is not null;
        return operation.Parameters.Any(parameter =>
            parameter.Location == ToAdmissionLocation(location) &&
            string.Equals(parameter.Name, name, StringComparison.Ordinal));
    }

    private static IEnumerable<(NyxIdAssistantOperationArgumentLocation Location, string Name)>
        RequiredReadArguments(AgentToolOperationAdmission operation) =>
        operation.Parameters
            .Where(static parameter => parameter.Required)
            .Select(parameter => (FromAdmissionLocation(parameter.Location), parameter.Name));

    private static (NyxIdAssistantOperationArgumentLocation Location, string Name) TargetKey(
        NyxIdAssistantReadBackArgumentBinding binding) =>
        (binding.ReadLocation, binding.ReadArgumentName);

    private static (NyxIdAssistantOperationArgumentLocation Location, string Name) TargetKey(
        NyxIdAssistantReadBackLiteralArgument argument) =>
        (argument.ReadLocation, argument.ReadArgumentName);

    private static (NyxIdAssistantOperationArgumentLocation Location, string Name) TargetKey(
        NyxIdAssistantReadBackProviderResourceArgument argument) =>
        (argument.ReadLocation, argument.ReadArgumentName);

    private static bool JsonEquals(JsonNode? node, Value expected) =>
        node is not null && string.Equals(
            node.ToJsonString(),
            JsonFormatter.Default.Format(expected),
            StringComparison.Ordinal);

    private static bool TryReadArgument(
        JsonObject root,
        NyxIdAssistantOperationArgumentLocation location,
        string name,
        out JsonNode? value)
    {
        value = null;
        var slot = Slot(location);
        return slot is not null &&
               root[slot] is JsonObject values &&
               values.TryGetPropertyValue(name, out value) &&
               value is not null;
    }

    private static void WriteArgument(
        JsonObject root,
        NyxIdAssistantOperationArgumentLocation location,
        string name,
        JsonNode value)
    {
        var slot = Slot(location) ?? throw new InvalidOperationException("Invalid read argument location.");
        if (root[slot] is not JsonObject values)
        {
            values = new JsonObject();
            root[slot] = values;
        }
        values[name] = value;
    }

    private static string? Slot(NyxIdAssistantOperationArgumentLocation location) => location switch
    {
        NyxIdAssistantOperationArgumentLocation.Path => "path_params",
        NyxIdAssistantOperationArgumentLocation.Query => "query",
        NyxIdAssistantOperationArgumentLocation.Header => "headers",
        NyxIdAssistantOperationArgumentLocation.Body => "body",
        _ => null,
    };

    private static AgentToolOperationParameterLocation ToAdmissionLocation(
        NyxIdAssistantOperationArgumentLocation location) => location switch
    {
        NyxIdAssistantOperationArgumentLocation.Path => AgentToolOperationParameterLocation.Path,
        NyxIdAssistantOperationArgumentLocation.Query => AgentToolOperationParameterLocation.Query,
        NyxIdAssistantOperationArgumentLocation.Header => AgentToolOperationParameterLocation.Header,
        _ => AgentToolOperationParameterLocation.Unspecified,
    };

    private static NyxIdAssistantOperationArgumentLocation FromAdmissionLocation(
        AgentToolOperationParameterLocation location) => location switch
    {
        AgentToolOperationParameterLocation.Path => NyxIdAssistantOperationArgumentLocation.Path,
        AgentToolOperationParameterLocation.Query => NyxIdAssistantOperationArgumentLocation.Query,
        AgentToolOperationParameterLocation.Header => NyxIdAssistantOperationArgumentLocation.Header,
        _ => NyxIdAssistantOperationArgumentLocation.Unspecified,
    };
}

internal static class NyxIdAssistantOperationReadBackRegistry
{
    public static NyxIdConnectedServiceReadBackPlan? Resolve(
        NyxIdToolOptions options,
        NyxIdMcpService service,
        NyxIdMcpEndpoint effectEndpoint,
        string catalogDigest,
        NyxIdServiceInstance serviceInstance)
    {
        var bindings = options.AssistantOperationReadBackBindings
            .Where(binding =>
                string.Equals(
                    binding.CatalogServiceSlug,
                    serviceInstance.CatalogServiceSlug,
                    StringComparison.Ordinal) &&
                MatchesEndpoint(
                    effectEndpoint,
                    binding.EffectEndpointId,
                    binding.EffectHttpMethod,
                    binding.EffectPathTemplate))
            .Take(2)
            .ToArray();
        if (bindings.Length != 1)
            return null;

        var readEndpoints = service.Endpoints
            .Where(endpoint => MatchesEndpoint(
                endpoint,
                bindings[0].ReadEndpointId,
                bindings[0].ReadHttpMethod,
                bindings[0].ReadPathTemplate))
            .Take(2)
            .ToArray();
        if (readEndpoints.Length != 1 || !readEndpoints[0].IsReadOnly)
            return null;

        var effectAdmission = Map(
            service,
            effectEndpoint,
            catalogDigest,
            serviceInstance.CatalogServiceSlug);
        var readAdmission = Map(
            service,
            readEndpoints[0],
            catalogDigest,
            serviceInstance.CatalogServiceSlug);
        return NyxIdConnectedServiceReadBackPlan.Create(effectAdmission, readAdmission, bindings[0]);
    }

    private static bool MatchesEndpoint(
        NyxIdMcpEndpoint endpoint,
        string endpointId,
        string httpMethod,
        string pathTemplate)
    {
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            return string.Equals(endpoint.EndpointId, endpointId, StringComparison.Ordinal) &&
                   (string.IsNullOrWhiteSpace(httpMethod) ||
                    string.Equals(endpoint.Method, httpMethod, StringComparison.OrdinalIgnoreCase)) &&
                   (string.IsNullOrWhiteSpace(pathTemplate) ||
                    string.Equals(endpoint.PathTemplate, pathTemplate, StringComparison.Ordinal));
        }

        return !string.IsNullOrWhiteSpace(httpMethod) &&
               !string.IsNullOrWhiteSpace(pathTemplate) &&
               string.Equals(endpoint.Method, httpMethod, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(endpoint.PathTemplate, pathTemplate, StringComparison.Ordinal);
    }

    private static AgentToolOperationAdmission Map(
        NyxIdMcpService service,
        NyxIdMcpEndpoint endpoint,
        string catalogDigest,
        string catalogServiceSlug)
    {
        var proof = NyxIdOperationAdmissionProofBuilder.Build(
            service.UserServiceId,
            service.ServiceSlug,
            endpoint,
            endpoint.ContractDigest).NyxIdUserService;
        return NyxIdConnectedServiceOperationAdmissionMapper.Map(
            proof,
            catalogDigest,
            catalogServiceSlug);
    }
}
