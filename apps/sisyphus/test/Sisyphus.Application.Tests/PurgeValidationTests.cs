using FluentAssertions;
using Sisyphus.Application.Models.Upload;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Tests;

public class PurgeValidationTests
{
    // ─── Node Purge Validation ───

    [Fact]
    public void ValidateNodePurge_ValidResult_NoErrors()
    {
        var result = new NodePurgeResult
        {
            BlueNodes =
            [
                new() { TempId = "b0", Type = "theorem", Abstract = "A theorem", Body = "Full body" },
                new() { TempId = "b1", Type = "proof", Abstract = "A proof", Body = "Proof body" },
            ],
            BlueEdges =
            [
                new() { Source = "b1", Target = "b0", EdgeType = "proves" },
            ],
        };

        var errors = UploadPipelineService.ValidateNodePurgeResult(result);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateNodePurge_EmptyBlueNodes_ReturnsError()
    {
        var result = new NodePurgeResult { BlueNodes = [] };

        var errors = UploadPipelineService.ValidateNodePurgeResult(result);
        errors.Should().ContainSingle().Which.Should().Contain("blue_nodes must not be empty");
    }

    [Fact]
    public void ValidateNodePurge_MissingTempId_ReturnsError()
    {
        var result = new NodePurgeResult
        {
            BlueNodes = [new() { TempId = "", Type = "theorem", Abstract = "abc", Body = "body" }],
        };

        var errors = UploadPipelineService.ValidateNodePurgeResult(result);
        errors.Should().Contain(e => e.Contains("temp_id"));
    }

    [Fact]
    public void ValidateNodePurge_InvalidType_ReturnsError()
    {
        var result = new NodePurgeResult
        {
            BlueNodes = [new() { TempId = "b0", Type = "invalid", Abstract = "abc", Body = "body" }],
        };

        var errors = UploadPipelineService.ValidateNodePurgeResult(result);
        errors.Should().Contain(e => e.Contains("Invalid type"));
    }

    [Fact]
    public void ValidateNodePurge_EmptyAbstract_ReturnsError()
    {
        var result = new NodePurgeResult
        {
            BlueNodes = [new() { TempId = "b0", Type = "theorem", Abstract = "", Body = "body" }],
        };

        var errors = UploadPipelineService.ValidateNodePurgeResult(result);
        errors.Should().Contain(e => e.Contains("abstract"));
    }

    [Fact]
    public void ValidateNodePurge_EmptyBody_ReturnsError()
    {
        var result = new NodePurgeResult
        {
            BlueNodes = [new() { TempId = "b0", Type = "theorem", Abstract = "abc", Body = "" }],
        };

        var errors = UploadPipelineService.ValidateNodePurgeResult(result);
        errors.Should().Contain(e => e.Contains("body"));
    }

    [Fact]
    public void ValidateNodePurge_EdgeRefsMissingTempId_ReturnsError()
    {
        var result = new NodePurgeResult
        {
            BlueNodes = [new() { TempId = "b0", Type = "theorem", Abstract = "abc", Body = "body" }],
            BlueEdges = [new() { Source = "b0", Target = "b99", EdgeType = "references" }],
        };

        var errors = UploadPipelineService.ValidateNodePurgeResult(result);
        errors.Should().Contain(e => e.Contains("b99"));
    }

    [Fact]
    public void ValidateNodePurge_InvalidEdgeType_ReturnsError()
    {
        var result = new NodePurgeResult
        {
            BlueNodes =
            [
                new() { TempId = "b0", Type = "theorem", Abstract = "abc", Body = "body" },
                new() { TempId = "b1", Type = "proof", Abstract = "abc", Body = "body" },
            ],
            BlueEdges = [new() { Source = "b1", Target = "b0", EdgeType = "invalid" }],
        };

        var errors = UploadPipelineService.ValidateNodePurgeResult(result);
        errors.Should().Contain(e => e.Contains("Invalid edge_type"));
    }

    [Theory]
    [InlineData("theorem")]
    [InlineData("lemma")]
    [InlineData("definition")]
    [InlineData("proof")]
    [InlineData("corollary")]
    [InlineData("conjecture")]
    [InlineData("proposition")]
    [InlineData("remark")]
    public void ValidateNodePurge_AllValidTypes_Accepted(string type)
    {
        var result = new NodePurgeResult
        {
            BlueNodes = [new() { TempId = "b0", Type = type, Abstract = "abc", Body = "body" }],
        };

        var errors = UploadPipelineService.ValidateNodePurgeResult(result);
        errors.Should().BeEmpty();
    }

    // ─── Edge Purge Validation ───

    [Fact]
    public void ValidateEdgePurge_ValidResult_NoErrors()
    {
        var validUuids = new HashSet<string> { "uuid-1", "uuid-2" };
        var result = new EdgePurgeResult
        {
            BlueEdges = [new() { SourceId = "uuid-1", TargetId = "uuid-2", EdgeType = "references" }],
        };

        var errors = UploadPipelineService.ValidateEdgePurgeResult(result, validUuids);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateEdgePurge_InvalidSourceUuid_ReturnsError()
    {
        var validUuids = new HashSet<string> { "uuid-1", "uuid-2" };
        var result = new EdgePurgeResult
        {
            BlueEdges = [new() { SourceId = "nonexistent", TargetId = "uuid-2", EdgeType = "references" }],
        };

        var errors = UploadPipelineService.ValidateEdgePurgeResult(result, validUuids);
        errors.Should().Contain(e => e.Contains("source_id"));
    }

    [Fact]
    public void ValidateEdgePurge_InvalidTargetUuid_ReturnsError()
    {
        var validUuids = new HashSet<string> { "uuid-1" };
        var result = new EdgePurgeResult
        {
            BlueEdges = [new() { SourceId = "uuid-1", TargetId = "missing", EdgeType = "proves" }],
        };

        var errors = UploadPipelineService.ValidateEdgePurgeResult(result, validUuids);
        errors.Should().Contain(e => e.Contains("target_id"));
    }

    [Fact]
    public void ValidateEdgePurge_InvalidEdgeType_ReturnsError()
    {
        var validUuids = new HashSet<string> { "uuid-1", "uuid-2" };
        var result = new EdgePurgeResult
        {
            BlueEdges = [new() { SourceId = "uuid-1", TargetId = "uuid-2", EdgeType = "unknown" }],
        };

        var errors = UploadPipelineService.ValidateEdgePurgeResult(result, validUuids);
        errors.Should().Contain(e => e.Contains("edge_type"));
    }

    [Fact]
    public void ValidateEdgePurge_EmptyResult_NoErrors()
    {
        var validUuids = new HashSet<string> { "uuid-1" };
        var result = new EdgePurgeResult { BlueEdges = [] };

        var errors = UploadPipelineService.ValidateEdgePurgeResult(result, validUuids);
        errors.Should().BeEmpty();
    }
}
