using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionDocumentValueProtoEnumTests
{
    [Fact]
    public void FromProtoEnum_ShouldCarryTheProtobufJsonValueName()
    {
        var value = ProjectionDocumentValue.FromProtoEnum(NullValue.NullValue);

        value.Kind.Should().Be(ProjectionDocumentValueKind.String);
        value.RawValue.Should().Be("NULL_VALUE");
    }

    [Fact]
    public void ResolveProtoEnumName_ShouldFallBackToMemberNameForNonProtoEnums()
    {
        ProjectionDocumentValue.ResolveProtoEnumName(ProjectionDocumentFilterOperator.ContainsText)
            .Should().Be(nameof(ProjectionDocumentFilterOperator.ContainsText));
    }
}
