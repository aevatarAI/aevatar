using Aevatar.AI.ToolProviders.Scripting;
using Aevatar.AI.ToolProviders.Scripting.Ports;
using Aevatar.AI.ToolProviders.Scripting.Tools;
using FluentAssertions;
using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class ScriptCompileToolTests
{
    [Fact]
    public async Task ExecuteAsync_ForwardsEntryBehaviorTypeNameAndReturnsFileDiagnostics()
    {
        var adapter = new RecordingCompilationAdapter();
        var tool = new ScriptCompileTool(adapter, new ScriptingToolOptions());

        var output = await tool.ExecuteAsync("""
            {
              "script_id": "scripted-main",
              "source_files": {
                "scripts/Main.cs": "public sealed class MainBehavior {}"
              },
              "proto_files": {
                "scripts/contract.proto": "syntax = \"proto3\";"
              },
              "entry_behavior_type_name": "MainBehavior"
            }
            """);

        adapter.Requests.Should().ContainSingle();
        var request = adapter.Requests[0];
        request.ScriptId.Should().Be("scripted-main");
        request.SourceFiles.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>(
                "scripts/Main.cs",
                "public sealed class MainBehavior {}"));
        request.ProtoFiles.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>(
                "scripts/contract.proto",
                "syntax = \"proto3\";"));
        request.EntryBehaviorTypeName.Should().Be("MainBehavior");

        using var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("diagnostics")[0].GetString()
            .Should().Be("scripts/Main.cs(1,1): error CS1002: ; expected");
    }

    private sealed class RecordingCompilationAdapter : IScriptToolCompilationAdapter
    {
        public List<ScriptCompilationRequest> Requests { get; } = [];

        public Task<ScriptCompilationResult> CompileAsync(
            ScriptCompilationRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ScriptCompilationResult
            {
                Success = false,
                ScriptId = request.ScriptId,
                Diagnostics = ["scripts/Main.cs(1,1): error CS1002: ; expected"],
            });
        }
    }
}
