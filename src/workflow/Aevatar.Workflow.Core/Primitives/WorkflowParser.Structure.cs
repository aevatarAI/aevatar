using System.Reflection;
using Aevatar.Workflow.Abstractions.Workflows;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aevatar.Workflow.Core.Primitives;

public sealed partial class WorkflowParser
{
    private static readonly string[] BranchConditionFields = ["condition", "when", "case", "label", "if"];
    private static readonly string[] BranchTargetFields = ["next", "to", "target", "step"];

    /// <summary>
    /// Checks the original YAML nodes before defaults or normalization can discard invalid input.
    /// Callers loading their own stream must apply <see cref="WorkflowYamlResourceGuard"/> first.
    /// </summary>
    public static void ValidateStructure(YamlStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Documents.Count == 0)
            throw new WorkflowYamlStructureException("/", "YAML 为空");
        if (stream.Documents.Count != 1)
            throw new WorkflowYamlStructureException("/", "Workflow YAML must contain exactly one document.");

        var root = stream.Documents[0].RootNode;
        if (IsNullNode(root))
            throw new WorkflowYamlStructureException("/", "YAML 为空");
        ValidateNode(root, typeof(Raw), string.Empty, allowNull: false);
    }

    private static void ValidateNode(YamlNode node, Type contract, string path, bool allowNull)
    {
        if (IsNullNode(node))
        {
            if (!allowNull)
                throw StructureError(path, "A null value is not allowed here.");
            return;
        }

        contract = Nullable.GetUnderlyingType(contract) ?? contract;
        if (contract == typeof(object))
        {
            ValidateOpenValue(node, path);
            return;
        }

        if (node is YamlMappingNode keyedMapping)
            ValidateMappingKeys(keyedMapping, path);

        if (contract == typeof(string) || contract.IsValueType)
        {
            if (node is not YamlScalarNode)
                throw StructureError(path, "Expected a scalar value.");
            return;
        }

        if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(List<>))
        {
            if (node is not YamlSequenceNode sequence)
                throw StructureError(path, "Expected a list.");
            var elementContract = contract.GetGenericArguments()[0];
            for (var index = 0; index < sequence.Children.Count; index++)
                ValidateNode(sequence.Children[index], elementContract, AppendPath(path, index.ToString()), allowNull: false);
            return;
        }

        if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            if (node is not YamlMappingNode dictionary)
                throw StructureError(path, "Expected a mapping.");
            foreach (var entry in dictionary.Children)
            {
                var key = ReadKey(entry.Key, path);
                ValidateNode(entry.Value, contract.GetGenericArguments()[1], AppendPath(path, key), allowNull: true);
            }
            return;
        }

        if (node is not YamlMappingNode mapping)
            throw StructureError(path, "Expected a mapping.");

        var properties = contract.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.CanWrite && property.GetCustomAttribute<YamlIgnoreAttribute>() is null)
            .ToDictionary(static property =>
                property.GetCustomAttribute<YamlMemberAttribute>()?.Alias ??
                UnderscoredNamingConvention.Instance.Apply(property.Name), StringComparer.Ordinal);

        foreach (var entry in mapping.Children)
        {
            var key = ReadKey(entry.Key, path);
            var fieldPath = AppendPath(path, key);
            if (contract == typeof(Raw) && !WorkflowYamlRootSchema.IsAcceptedRootField(key))
            {
                throw new WorkflowYamlStructureException(fieldPath,
                    $"Unsupported workflow YAML root field '{key}'. Allowed root fields: {WorkflowYamlRootSchema.FormatAcceptedRootFields()}.",
                    code: "unknown_field");
            }
            if (!properties.TryGetValue(key, out var property))
                throw new WorkflowYamlStructureException(fieldPath, $"Unknown field '{key}'.", code: "unknown_field");

            if (contract == typeof(RawStep) && key == "branches")
                ValidateBranches(entry.Value, fieldPath);
            else if (ValidateSpecialField(contract, key, entry.Value, fieldPath))
                continue;
            else
                ValidateNode(entry.Value, property.PropertyType, fieldPath, allowNull: !property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null);

            if (contract == typeof(RawStep) && key == "parameters" && entry.Value is YamlMappingNode parameters)
                ValidateReservedParameters(parameters, fieldPath);
        }

        if (contract == typeof(Raw))
            RequireIdentity(mapping, "name", path, "缺少 name");
        else if (contract == typeof(RawRole))
            RequireIdentity(mapping, "id", path, "role 缺 id");
        else if (contract == typeof(RawStep))
            RequireIdentity(mapping, "id", path, "step 缺 id");
    }

    private static void RequireIdentity(YamlMappingNode mapping, string key, string path, string message)
    {
        var node = mapping.Children.FirstOrDefault(entry =>
            entry.Key is YamlScalarNode scalar && scalar.Value == key).Value;
        if (node is null || IsNullNode(node))
            throw StructureError(AppendPath(path, key), message);
    }

    private static void ValidateOpenValue(YamlNode node, string path)
    {
        if (node is YamlMappingNode mapping)
        {
            ValidateMappingKeys(mapping, path);
            foreach (var entry in mapping.Children)
            {
                var key = ReadKey(entry.Key, path);
                ValidateOpenValue(entry.Value, AppendPath(path, key));
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Children.Count; index++)
                ValidateOpenValue(sequence.Children[index], AppendPath(path, index.ToString()));
        }
    }

    private static void ValidateBranches(YamlNode node, string path)
    {
        if (IsNullNode(node))
            return;

        if (node is YamlMappingNode mapping)
        {
            ValidateMappingKeys(mapping, path);
            foreach (var entry in mapping.Children)
            {
                var key = ReadKey(entry.Key, path);
                ValidateBranchTarget(entry.Value, AppendPath(path, key));
            }
            return;
        }

        if (node is not YamlSequenceNode sequence)
            throw StructureError(path, "`branches` must be a mapping or a list.");

        var conditions = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var branchPath = AppendPath(path, index.ToString());
            if (sequence.Children[index] is not YamlMappingNode branch || IsNullNode(sequence.Children[index]))
                throw StructureError(branchPath, "Each branch entry must be a mapping.");

            ValidateMappingKeys(branch, branchPath);
            foreach (var entry in branch.Children)
            {
                var key = ReadKey(entry.Key, branchPath);
                if (!BranchConditionFields.Contains(key, StringComparer.OrdinalIgnoreCase) &&
                    !BranchTargetFields.Contains(key, StringComparer.OrdinalIgnoreCase))
                    throw new WorkflowYamlStructureException(AppendPath(branchPath, key), $"Unknown branch field '{key}'.", code: "unknown_field");
            }

            var condition = ReadBranchAlias(branch, BranchConditionFields, branchPath, "condition");
            if (!conditions.Add(condition))
                throw StructureError(branchPath, $"Duplicate branch condition '{condition}'.");
            ReadBranchAlias(branch, BranchTargetFields, branchPath, "target");
        }
    }

    private static string ReadBranchAlias(YamlMappingNode mapping, string[] aliases, string path, string name)
    {
        string? result = null;
        foreach (var entry in mapping.Children)
        {
            var key = ReadKey(entry.Key, path);
            if (!aliases.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;
            var value = ReadRequiredScalar(entry.Value, AppendPath(path, key));
            if (result is not null)
                throw StructureError(path, $"A branch must declare exactly one {name} field.");
            result = value;
        }
        return result ?? throw StructureError(path, $"A branch is missing its {name}.");
    }

    private static void ValidateBranchTarget(YamlNode node, string path)
    {
        if (node is not YamlMappingNode mapping)
        {
            ReadRequiredScalar(node, path);
            return;
        }
        ValidateMappingKeys(mapping, path);
        foreach (var entry in mapping.Children)
        {
            var key = ReadKey(entry.Key, path);
            if (!BranchTargetFields.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new WorkflowYamlStructureException(AppendPath(path, key), $"Unknown branch target field '{key}'.", code: "unknown_field");
        }
        ReadBranchAlias(mapping, BranchTargetFields, path, "target");
    }

    private static string ReadRequiredScalar(YamlNode node, string path)
    {
        if (node is not YamlScalarNode scalar || IsNullNode(node) || string.IsNullOrWhiteSpace(scalar.Value))
            throw StructureError(path, "Expected a non-empty scalar value.");
        return scalar.Value;
    }

    private static void ValidateMappingKeys(YamlMappingNode mapping, string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in mapping.Children)
        {
            var key = ReadKey(entry.Key, path);
            if (!keys.Add(key))
                throw StructureError(AppendPath(path, key), $"Duplicate mapping key '{key}'.");
        }
    }

    private static string ReadKey(YamlNode node, string path)
    {
        ReadRequiredScalar(node, path);
        return ((YamlScalarNode)node).Value!;
    }

    private static bool IsNullNode(YamlNode node) =>
        node is YamlScalarNode scalar &&
        (scalar.Tag == "tag:yaml.org,2002:null" ||
         ((scalar.Tag.IsEmpty || scalar.Tag.IsNonSpecific) && scalar.Style == YamlDotNet.Core.ScalarStyle.Plain &&
          (scalar.Value is null or "" or "~" or "null" or "Null" or "NULL")));

    private static string AppendPath(string path, string key) =>
        $"{path}/{key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";

    private static WorkflowYamlStructureException StructureError(string path, string message) =>
        new(string.IsNullOrEmpty(path) ? "/" : path, message);

    // YamlDotNet's default null reader also consumes plain !!str null/~ scalars.
    // Keep mapping consistent with the original AST's explicit non-null tags.
    private sealed class ExplicitTagAwareNullNodeDeserializer(INodeDeserializer inner) : INodeDeserializer
    {
        public bool Deserialize(
            IParser parser,
            Type expectedType,
            Func<IParser, Type, object?> nestedObjectDeserializer,
            out object? value,
            ObjectDeserializer rootDeserializer)
        {
            if (parser.Accept<Scalar>(out var scalar) &&
                !scalar.Tag.IsEmpty && !scalar.Tag.IsNonSpecific &&
                scalar.Tag != "tag:yaml.org,2002:null")
            {
                value = null;
                return false;
            }

            return inner.Deserialize(parser, expectedType, nestedObjectDeserializer, out value, rootDeserializer);
        }
    }
}
