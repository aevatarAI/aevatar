using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;

namespace Aevatar.AI.ToolProviders.NyxId.ExactServiceApprovals;

internal sealed record NyxIdExactServiceArgumentsMapResult(
    JsonObject? Arguments,
    string? FailureCode)
{
    public bool Succeeded => Arguments is not null;
}

internal static class NyxIdExactServiceArgumentsMapper
{
    private static readonly string[] RequestBodyFieldCandidates =
        ["body", "request_body", "requestBody", "payload"];

    public static NyxIdExactServiceArgumentsMapResult Map(
        AgentToolOperationAdmission admission,
        JsonNode arguments)
    {
        if (arguments is not JsonObject root)
            return Failed("exact_service_arguments_invalid");
        if (HasAmbiguousParameterNames(admission.Parameters))
            return Failed("exact_service_argument_name_collision");
        if (HasAmbiguousJsonObjectSchema(admission))
            return Failed("exact_service_body_contract_ambiguous");

        var validated = NyxIdAdmittedRequestBuilder.Build(admission, root.ToJsonString());
        if (!validated.Succeeded)
            return Failed(validated.Failure!.Code);

        var exactArguments = new JsonObject();
        foreach (var slot in (ReadOnlySpan<string>)["path_params", "query", "headers"])
        {
            if (!TryCopyObjectSlot(root, slot, exactArguments))
                return Failed("exact_service_argument_name_collision");
        }

        if (!root.TryGetPropertyValue("body", out var body) || body is null)
            return Success(exactArguments);

        if (UsesFlattenedJsonBody(admission))
        {
            if (body is not JsonObject bodyObject)
                return Failed("exact_service_arguments_invalid");

            var declaredBodyCollision = admission.RequestBody!.Schema.Properties.Any(property =>
                admission.Parameters.Any(parameter => NamesConflict(parameter, property.Name)));
            if (!declaredBodyCollision)
            {
                foreach (var property in bodyObject)
                {
                    if (admission.Parameters.Any(parameter =>
                            NamesConflict(parameter, property.Key)))
                    {
                        return Failed("exact_service_argument_name_collision");
                    }
                    exactArguments[property.Key] = property.Value?.DeepClone();
                }
                return Success(exactArguments);
            }
        }

        exactArguments[ResolveRequestBodyFieldName(admission.Parameters)] = body.DeepClone();
        return Success(exactArguments);
    }

    private static bool TryCopyObjectSlot(
        JsonObject root,
        string slot,
        JsonObject destination)
    {
        if (!root.TryGetPropertyValue(slot, out var value) || value is null)
            return true;
        if (value is not JsonObject values)
            return false;
        foreach (var property in values)
        {
            if (destination.ContainsKey(property.Key))
                return false;
            destination[property.Key] = property.Value?.DeepClone();
        }
        return true;
    }

    private static bool HasAmbiguousParameterNames(
        IReadOnlyList<AgentToolOperationParameter> parameters)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            for (var otherIndex = index + 1; otherIndex < parameters.Count; otherIndex++)
            {
                var left = parameters[index];
                var right = parameters[otherIndex];
                var comparison = left.Location == AgentToolOperationParameterLocation.Header &&
                                 right.Location == AgentToolOperationParameterLocation.Header
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (string.Equals(left.Name, right.Name, comparison))
                    return true;
            }
        }
        return false;
    }

    private static bool UsesFlattenedJsonBody(AgentToolOperationAdmission admission)
    {
        var requestBody = admission.RequestBody;
        return requestBody is
               {
                   Required: true,
                   Schema.Kind: AgentToolOperationValueKind.Object,
               } && IsJsonMediaType(requestBody.MediaType);
    }

    private static bool HasAmbiguousJsonObjectSchema(AgentToolOperationAdmission admission) =>
        admission.RequestBody is
        {
            Required: true,
            Schema.Kind: AgentToolOperationValueKind.Object,
            Schema.Properties.Count: 0,
        } requestBody && IsJsonMediaType(requestBody.MediaType);

    private static bool IsJsonMediaType(string value)
    {
        var mediaType = value.Split(';', 2)[0].Trim();
        return mediaType.Length == 0 ||
               mediaType == "*/*" ||
               mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRequestBodyFieldName(
        IReadOnlyList<AgentToolOperationParameter> parameters)
    {
        foreach (var candidate in RequestBodyFieldCandidates)
        {
            if (!parameters.Any(parameter => NamesConflict(parameter, candidate)))
                return candidate;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"body_{suffix}";
            if (!parameters.Any(parameter => NamesConflict(parameter, candidate)))
                return candidate;
        }
    }

    private static bool NamesConflict(
        AgentToolOperationParameter parameter,
        string candidate) => string.Equals(
        parameter.Name,
        candidate,
        parameter.Location == AgentToolOperationParameterLocation.Header
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static NyxIdExactServiceArgumentsMapResult Success(JsonObject arguments) =>
        new(arguments, null);

    private static NyxIdExactServiceArgumentsMapResult Failed(string code) =>
        new(null, code);
}
