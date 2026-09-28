using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.AI.ToolProviders.Skills;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class SkillBoundaryDiagnosticsTests
{
    [Fact]
    public void SkillPayloadDiagnostics_ShouldSummarizeGenericSkillFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SKILL.md"] = "root",
            ["references/guide.md"] = "guide",
            ["assets/large.txt"] = "large-content",
        };

        var summary = SkillPayloadDiagnostics.Summarize(files);

        summary.FileCount.Should().Be(3);
        summary.EmptyFileCount.Should().Be(0);
        summary.TotalFileBytes.Should().BeGreaterThan(0);
        summary.RootSkillBytes.Should().Be(4);
        summary.LargestFileBytes.Should().BeGreaterThan(0);
        summary.FileTreeSha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void SkillPayloadDiagnostics_ShouldCountEmptyGenericFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SKILL.md"] = "root",
            ["references/guide.md"] = string.Empty,
        };

        var summary = SkillPayloadDiagnostics.Summarize(files);

        summary.FileCount.Should().Be(2);
        summary.EmptyFileCount.Should().Be(1);
    }

    [Fact]
    public async Task UseSkillTool_ShouldLogAssemblyBoundaryWithoutSkillBody()
    {
        var logger = new RecordingLogger<UseSkillTool>();
        var catalog = new LocalSkillCatalog();
        catalog.Register(new SkillDefinition
        {
            Name = "generic-skill",
            Description = "onboarding",
            Version = "2.2",
            Instructions = "Use the onboarding flow.",
            AssociatedFiles = new Dictionary<string, string>
            {
                ["references/guide.md"] = "do-not-log-this-skill-body",
                ["assets/example.txt"] = "example",
            },
        });
        var tool = new UseSkillTool(catalog, logger: logger);

        var result = await tool.ExecuteAsync("{\"skill\":\"generic-skill\"}");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("loaded").GetBoolean().Should().BeTrue();
        logger.Output.Should().Contain("assembled_result_bytes=");
        logger.Output.Should().Contain("file_tree_sha256=");
        logger.Output.Should().Contain("associated_file_count=2");
        logger.Output.Should().NotContain("do-not-log-this-skill-body");
    }

    [Fact]
    public async Task RemoteSkillFetch_ShouldLogRawAndParsedGenericBoundariesWithoutBody()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("""
            {
              "data": {
                "name": "generic-skill",
                "version": "1.0",
                "description": "generic",
                "files": {
                  "SKILL.md": "root instructions",
                  "docs/guide.md": "do-not-log-this-remote-body"
                }
              }
            }
            """);
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var options = new OrnnOptions { NyxIdSlug = "ornn" };
        var clientLogger = new RecordingLogger<OrnnSkillClient>();
        var fetcherLogger = new RecordingLogger<OrnnRemoteSkillFetcher>();
        var client = new OrnnSkillClient(options, nyxClient, logger: clientLogger);
        var fetcher = new OrnnRemoteSkillFetcher(client, fetcherLogger);

        var skill = await fetcher.FetchSkillAsync("caller-token", "generic-skill");

        skill.Should().NotBeNull();
        skill!.Version.Should().Be("1.0");
        clientLogger.Output.Should().Contain("raw_response_bytes=");
        clientLogger.Output.Should().Contain("remote_file_count=2");
        fetcherLogger.Output.Should().Contain("associated_file_count=1");
        fetcherLogger.Output.Should().Contain("file_tree_sha256=");
        clientLogger.Output.Should().NotContain("do-not-log-this-remote-body");
        fetcherLogger.Output.Should().NotContain("do-not-log-this-remote-body");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public string Output => string.Join('\n', Messages);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
