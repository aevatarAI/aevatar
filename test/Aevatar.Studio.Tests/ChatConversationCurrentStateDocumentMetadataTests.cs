using Aevatar.Studio.Projection.Metadata;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ChatConversationCurrentStateDocumentMetadataTests
{
    [Fact]
    public void MetadataProvider_ShouldMapListQueryFieldsAsSortableAndFilterableTypes()
    {
        var metadata = new ChatConversationCurrentStateDocumentMetadataProvider().Metadata;

        metadata.IndexName.Should().Be("studio-chat-conversation");
        metadata.Mappings.Should().ContainKey("dynamic").WhoseValue.Should().Be(true);

        var properties = GetProperties(metadata.Mappings);
        FieldType(properties, "scope_id").Should().Be("keyword");
        FieldType(properties, "conversation_id").Should().Be("keyword");
        FieldType(properties, "title").Should().Be("keyword");
        FieldType(properties, "deleted").Should().Be("boolean");
        FieldType(properties, "created_at_ms").Should().Be("long");
        FieldType(properties, "updated_at_ms").Should().Be("long");
        FieldType(properties, "message_count").Should().Be("integer");
        FieldType(properties, "state_version").Should().Be("long");
        FieldType(properties, "updated_at").Should().Be("date");
    }

    private static IReadOnlyDictionary<string, object?> GetProperties(
        IReadOnlyDictionary<string, object?> mappings)
    {
        mappings.Should().ContainKey("properties");
        var properties = mappings["properties"].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        return properties;
    }

    private static string? FieldType(
        IReadOnlyDictionary<string, object?> properties,
        string fieldName)
    {
        properties.Should().ContainKey(fieldName);
        var fieldMapping = properties[fieldName].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>().Subject;
        fieldMapping.Should().ContainKey("type");
        return fieldMapping["type"] as string;
    }
}
