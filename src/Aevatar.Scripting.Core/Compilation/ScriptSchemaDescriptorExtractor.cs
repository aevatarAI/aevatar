using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Schema;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using System.Security.Cryptography;

namespace Aevatar.Scripting.Core.Compilation;

internal static class ScriptSchemaDescriptorExtractor
{
    public static bool TryExtractFromDescriptor(
        ScriptBehaviorDescriptor? descriptor,
        out ScriptSchemaDescriptorExtraction extraction)
    {
        if (descriptor == null)
        {
            extraction = ScriptSchemaDescriptorExtraction.Empty;
            return false;
        }

        return TryExtractFromReadModelDescriptor(descriptor.ReadModelDescriptor, out extraction);
    }

    public static bool TryExtractFromReadModelDescriptor(
        MessageDescriptor? readModelDescriptor,
        out ScriptSchemaDescriptorExtraction extraction)
    {
        if (readModelDescriptor == null)
        {
            extraction = ScriptSchemaDescriptorExtraction.Empty;
            return false;
        }

        var readModelOptions = readModelDescriptor.GetOptions();
        if (readModelOptions == null ||
            !readModelOptions.HasExtension(ScriptingSchemaOptionsExtensions.ScriptingReadModel))
        {
            extraction = ScriptSchemaDescriptorExtraction.Empty;
            return false;
        }

        var options = readModelOptions.GetExtension(ScriptingSchemaOptionsExtensions.ScriptingReadModel)
            ?? new ScriptingReadModelOptions();
        var schemaSpec = new ScriptReadModelSchemaSpec
        {
            SchemaId = options.SchemaId ?? string.Empty,
            SchemaVersion = options.SchemaVersion ?? string.Empty,
        };

        foreach (var field in EnumerateDeclaredFields(readModelDescriptor, prefix: string.Empty))
            schemaSpec.Fields.Add(field);

        foreach (var index in options.DocumentIndexes)
        {
            var spec = new ScriptReadModelIndexSpec
            {
                Name = index.Name ?? string.Empty,
                Unique = index.Unique,
                Provider = string.IsNullOrWhiteSpace(index.Provider) ? "document" : index.Provider,
            };
            spec.Paths.Add(index.Paths.Where(static path => !string.IsNullOrWhiteSpace(path)));
            schemaSpec.Indexes.Add(spec);
        }

        foreach (var relation in options.GraphRelations)
        {
            schemaSpec.Relations.Add(new ScriptReadModelRelationSpec
            {
                Name = relation.Name ?? string.Empty,
                SourcePath = relation.SourcePath ?? string.Empty,
                TargetSchemaId = relation.TargetSchemaId ?? string.Empty,
                TargetPath = relation.TargetPath ?? string.Empty,
                Cardinality = relation.Cardinality ?? string.Empty,
                Provider = string.IsNullOrWhiteSpace(relation.Provider) ? "graph" : relation.Provider,
            });
        }

        var schemaPayload = Any.Pack(schemaSpec);
        extraction = new ScriptSchemaDescriptorExtraction(
            readModelDescriptor,
            schemaSpec,
            schemaPayload,
            ComputeSha256(schemaSpec.ToByteArray()),
            schemaSpec.SchemaVersion,
            ResolveStoreCapabilities(options));
        return true;
    }

    private static IEnumerable<ScriptReadModelFieldSpec> EnumerateDeclaredFields(
        MessageDescriptor descriptor,
        string prefix)
    {
        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            var segment = field.IsRepeated ? field.Name + "[]" : field.Name;
            var path = string.IsNullOrWhiteSpace(prefix)
                ? segment
                : prefix + "." + segment;

            if (field.FieldType == FieldType.Message &&
                field.MessageType != null &&
                !IsLeafMessage(field.MessageType))
            {
                if (field.IsRepeated)
                {
                    throw new InvalidOperationException(
                        $"Read model field `{descriptor.FullName}.{field.Name}` is a repeated message. " +
                        "Repeated message traversal is not supported in script read model materialization.");
                }

                foreach (var nested in EnumerateDeclaredFields(field.MessageType, path))
                    yield return nested;
                continue;
            }

            var fieldOptions = field.GetOptions();
            var declaredOptions = fieldOptions != null &&
                                  fieldOptions.HasExtension(ScriptingSchemaOptionsExtensions.ScriptingField)
                ? fieldOptions.GetExtension(ScriptingSchemaOptionsExtensions.ScriptingField)
                : null;
            yield return new ScriptReadModelFieldSpec
            {
                Name = path,
                Type = ResolveStorageType(field, declaredOptions),
                Path = path,
                Nullable = declaredOptions?.Nullable ?? false,
            };
        }
    }

    private static string ResolveStorageType(
        FieldDescriptor field,
        ScriptingFieldOptions? declaredOptions)
    {
        if (!string.IsNullOrWhiteSpace(declaredOptions?.StorageType))
            return declaredOptions.StorageType;

        var inferred = field.FieldType switch
        {
            FieldType.String => "keyword",
            FieldType.Bool => "boolean",
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => "int32",
            FieldType.UInt32 or FieldType.Fixed32 => "int64",
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => "int64",
            FieldType.UInt64 or FieldType.Fixed64 => "int64",
            FieldType.Float or FieldType.Double => "double",
            FieldType.Enum => "keyword",
            FieldType.Bytes => "keyword",
            FieldType.Message when field.MessageType != null => ResolveLeafMessageStorageType(field.MessageType),
            _ => "keyword",
        };

        return field.IsRepeated ? inferred + "[]" : inferred;
    }

    private static string ResolveLeafMessageStorageType(MessageDescriptor descriptor)
    {
        return descriptor.FullName switch
        {
            "google.protobuf.Timestamp" => "timestamp",
            "google.protobuf.StringValue" => "keyword",
            "google.protobuf.BoolValue" => "boolean",
            "google.protobuf.Int32Value" => "int32",
            "google.protobuf.Int64Value" => "int64",
            "google.protobuf.UInt32Value" => "int64",
            "google.protobuf.UInt64Value" => "int64",
            "google.protobuf.DoubleValue" => "double",
            "google.protobuf.FloatValue" => "double",
            "google.protobuf.BytesValue" => "keyword",
            _ => "keyword",
        };
    }

    private static bool IsLeafMessage(MessageDescriptor descriptor)
    {
        return descriptor.FullName switch
        {
            "google.protobuf.Timestamp" => true,
            "google.protobuf.StringValue" => true,
            "google.protobuf.BoolValue" => true,
            "google.protobuf.Int32Value" => true,
            "google.protobuf.Int64Value" => true,
            "google.protobuf.UInt32Value" => true,
            "google.protobuf.UInt64Value" => true,
            "google.protobuf.DoubleValue" => true,
            "google.protobuf.FloatValue" => true,
            "google.protobuf.BytesValue" => true,
            _ => false,
        };
    }

    private static IReadOnlyList<string> ResolveStoreCapabilities(ScriptingReadModelOptions options)
    {
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var storeKind in options.StoreKinds)
        {
            if (!string.IsNullOrWhiteSpace(storeKind))
                providers.Add(storeKind.Trim());
        }

        foreach (var index in options.DocumentIndexes)
        {
            if (!string.IsNullOrWhiteSpace(index.Provider))
                providers.Add(index.Provider.Trim());
        }

        foreach (var relation in options.GraphRelations)
        {
            if (!string.IsNullOrWhiteSpace(relation.Provider))
                providers.Add(relation.Provider.Trim());
        }

        return providers.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ComputeSha256(byte[] content)
    {
        var bytes = SHA256.HashData(content ?? Array.Empty<byte>());
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed record ScriptSchemaDescriptorExtraction(
    MessageDescriptor ReadModelDescriptor,
    ScriptReadModelSchemaSpec SchemaSpec,
    Any SchemaPayload,
    string SchemaHash,
    string SchemaVersion,
    IReadOnlyList<string> StoreCapabilities)
{
    public bool RequiresDocumentStore => SchemaSpec.Fields.Count > 0 || SchemaSpec.Indexes.Count > 0;

    public bool RequiresGraphStore => SchemaSpec.Relations.Count > 0;

    public static ScriptSchemaDescriptorExtraction Empty { get; } = new(
        StringValue.Descriptor,
        new ScriptReadModelSchemaSpec(),
        Any.Pack(new Empty()),
        string.Empty,
        string.Empty,
        Array.Empty<string>());
}
