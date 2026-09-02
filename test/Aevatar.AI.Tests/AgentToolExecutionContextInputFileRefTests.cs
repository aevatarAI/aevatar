using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AgentToolExecutionContextInputFileRefTests
{
    [Fact]
    public void AgentToolExecutionContext_ShouldRoundTripInputFileRefs()
    {
        var payload = (AgentToolExecutionContext.Empty with
        {
            InputFileRefs =
            [
                new Aevatar.AI.Abstractions.ChatFileRef
                {
                    FileId = "file-1",
                    ArtifactId = "workflow-file://file-1",
                    SourceKind = Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource,
                    SourceMessageId = "om_1",
                    SourceResourceKey = "file_key_1",
                    FileName = "invoice.pdf",
                    MediaType = "application/pdf",
                    SizeBytes = 1234,
                    Sha256 = "sha-1",
                    CreatedAtUnixMs = 10,
                    ExpiresAtUnixMs = 20,
                    OwnerRunId = "run-1",
                    OwnerScopeId = "scope-1",
                },
            ],
        }).ToPayload();

        var copy = AgentToolExecutionContextMapper.FromPayload(
            AgentToolExecutionContextPayload.Parser.ParseFrom(payload.ToByteArray()));

        var fileRef = copy.InputFileRefs.Should().ContainSingle().Subject;
        fileRef.FileId.Should().Be("file-1");
        fileRef.ArtifactId.Should().Be("workflow-file://file-1");
        fileRef.SourceKind.Should().Be(Aevatar.AI.Abstractions.ChatFileSourceKind.ConnectedServiceResource);
        fileRef.SourceMessageId.Should().Be("om_1");
        fileRef.SourceResourceKey.Should().Be("file_key_1");
        fileRef.FileName.Should().Be("invoice.pdf");
        fileRef.MediaType.Should().Be("application/pdf");
        fileRef.SizeBytes.Should().Be(1234);
        fileRef.Sha256.Should().Be("sha-1");
        fileRef.CreatedAtUnixMs.Should().Be(10);
        fileRef.ExpiresAtUnixMs.Should().Be(20);
        fileRef.OwnerRunId.Should().Be("run-1");
        fileRef.OwnerScopeId.Should().Be("scope-1");
    }
}
