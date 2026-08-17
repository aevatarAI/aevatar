using System.Text;

namespace Aevatar.Workflow.Abstractions;

/// <summary>
/// Canonical contract for deterministic response projections sealed into workflow capability
/// admission. Validation is shared by authoring, admission integrity, and runtime matching.
/// </summary>
public static class WorkflowToolResponseProjectionContract
{
    public const int MaxFields = 64;
    public const int MaxOperationsPerField = 16;
    public const int MaxArrayMapItems = 1024;
    public const int MaxArrayMapDepth = 1;
    public const int MaxOutputNameBytes = 64;
    public const int MaxJsonPointerBytes = 512;
    public const int MaxExpectedStringBytes = 1024;
    public const int MaxProjectedResponseBytes = 64 * 1024;

    public static bool AreEquivalent(
        WorkflowToolResponseProjection? left,
        WorkflowToolResponseProjection? right) =>
        left is null ? right is null : right is not null && left.Equals(right);

    public static void ValidateOrThrow(WorkflowToolResponseProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Fields.Count is 0 or > MaxFields)
        {
            throw new InvalidOperationException(
                $"Workflow tool response projection must contain between 1 and {MaxFields} fields.");
        }

        string? previousOutputName = null;
        foreach (var field in projection.Fields)
        {
            ValidateOutputName(field.OutputName);
            if (previousOutputName is not null &&
                string.CompareOrdinal(previousOutputName, field.OutputName) >= 0)
            {
                throw new InvalidOperationException(
                    "Workflow tool response projection fields must be uniquely ordered by output name.");
            }
            previousOutputName = field.OutputName;

            if (field.Operations.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Workflow tool response projection field '{field.OutputName}' must contain between 1 and " +
                    $"{MaxOperationsPerField} operations.");
            }

            var operationCount = 0;
            foreach (var operation in field.Operations)
                operationCount += ValidateOperation(operation, mapDepth: 0);
            if (operationCount > MaxOperationsPerField)
            {
                throw new InvalidOperationException(
                    $"Workflow tool response projection field '{field.OutputName}' must contain between 1 and " +
                    $"{MaxOperationsPerField} operations, including mapped operations.");
            }
        }
    }

    public static bool IsValidJsonPointer(string? pointer)
    {
        if (pointer is null || Encoding.UTF8.GetByteCount(pointer) > MaxJsonPointerBytes)
            return false;
        if (pointer.Length == 0)
            return true;
        if (pointer[0] != '/')
            return false;

        for (var index = 0; index < pointer.Length; index++)
        {
            if (pointer[index] != '~')
                continue;
            if (++index >= pointer.Length || pointer[index] is not ('0' or '1'))
                return false;
        }

        return true;
    }

    private static void ValidateOutputName(string? outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName) ||
            !string.Equals(outputName, outputName.Trim(), StringComparison.Ordinal) ||
            Encoding.UTF8.GetByteCount(outputName) > MaxOutputNameBytes ||
            !(char.IsAsciiLetter(outputName[0]) || outputName[0] == '_') ||
            outputName.Skip(1).Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new InvalidOperationException(
                "Workflow tool response projection output names must be canonical ASCII identifiers.");
        }
    }

    private static int ValidateOperation(
        WorkflowToolResponseProjectionOperation operation,
        int mapDepth)
    {
        ArgumentNullException.ThrowIfNull(operation);
        switch (operation.OperationCase)
        {
            case WorkflowToolResponseProjectionOperation.OperationOneofCase.JsonPointer:
                if (!IsValidJsonPointer(operation.JsonPointer))
                {
                    throw new InvalidOperationException(
                        "Workflow tool response projection contains an invalid RFC 6901 JSON pointer.");
                }
                break;
            case WorkflowToolResponseProjectionOperation.OperationOneofCase.ParseJson:
                if (!operation.ParseJson)
                {
                    throw new InvalidOperationException(
                        "Workflow tool response projection parse_json operations must be true.");
                }
                break;
            case WorkflowToolResponseProjectionOperation.OperationOneofCase.ArrayMatch:
                if (operation.ArrayMatch is null ||
                    !IsValidJsonPointer(operation.ArrayMatch.ElementJsonPointer) ||
                    Encoding.UTF8.GetByteCount(operation.ArrayMatch.ExpectedString ?? string.Empty) >
                    MaxExpectedStringBytes)
                {
                    throw new InvalidOperationException(
                        "Workflow tool response projection contains an invalid array match operation.");
                }
                break;
            case WorkflowToolResponseProjectionOperation.OperationOneofCase.ArrayMap:
                if (operation.ArrayMap is null || operation.ArrayMap.Operations.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Workflow tool response projection contains an empty array map operation.");
                }
                if (mapDepth >= MaxArrayMapDepth)
                {
                    throw new InvalidOperationException(
                        "Workflow tool response projection array map operations cannot be nested.");
                }

                var mappedOperationCount = 1;
                foreach (var mappedOperation in operation.ArrayMap.Operations)
                    mappedOperationCount += ValidateOperation(mappedOperation, mapDepth + 1);
                return mappedOperationCount;
            default:
                throw new InvalidOperationException(
                    "Workflow tool response projection operations must select exactly one operation kind.");
        }

        return 1;
    }
}
