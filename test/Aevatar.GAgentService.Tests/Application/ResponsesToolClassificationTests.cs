using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesToolClassificationTests
{
    private static readonly ResponsesToolProviderContext ToolProviderContext = new(
        AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext("scope-1", "owner-1", "response-1"),
            Channel = new AgentToolChannelContext("ApiKey", null, "scope-1", null, null),
        });

    [Fact]
    public async Task ClassifyAsync_ShouldSubstituteForwardAndDeduplicateAdditiveTools()
    {
        var substitute = new RecordingTool("web_search", """{"type":"object","properties":{"q":{"type":"string"}}}""", "{}");
        var duplicateSubstitute = substitute;
        var additive = new RecordingTool("aevatar_todo_write", """{"type":"object"}""", "{}");
        var duplicateAdditive = additive;
        var customAdditive = new RecordingTool("custom_additive", """{"type":"object"}""", "{}");
        var logger = new RecordingLogger();

        var result = await ResponsesToolClassifier.ClassifyAsync(
            [
                new ResponsesApplicationToolDeclaration("web_search", "client search", """{"type":"object"}""", "mismatch"),
                new ResponsesApplicationToolDeclaration("client_tool", "client tool", """{"type":"object"}""", "client-hash"),
            ],
            [
                new RecordingResponsesToolProvider(
                    [substitute, duplicateSubstitute],
                    [additive, duplicateAdditive, customAdditive]),
            ],
            ToolProviderContext,
            logger);

        result.ForwardedTools.Should().ContainSingle(x => x.Name == "client_tool");
        result.SubstitutedToolNames.Should().ContainSingle("web_search");
        result.AdditiveToolNames.Should().Equal("aevatar_todo_write", "custom_additive");
        result.OwnedToolNames.Should().Equal("web_search", "aevatar_todo_write", "custom_additive");
        result.EffectiveTools.Select(static tool => tool.Name)
            .Should().Equal("web_search", "client_tool", "aevatar_todo_write", "custom_additive");
        logger.Messages.Should().Contain(message =>
            message.Contains("schema differs", StringComparison.Ordinal));
        result.EffectiveTools.Single(tool => tool.Name == "client_tool").IsReadOnly.Should().BeTrue();
        await ((Func<Task>)(() => result.EffectiveTools.Single(tool => tool.Name == "client_tool").ExecuteAsync("{}")))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be executed by the client*");
    }

    [Fact]
    public async Task ClassifyAsync_WhenDifferentExactToolsShareAName_ShouldFailClosed()
    {
        var provider = new RecordingResponsesToolProvider(
            [],
            [
                new RecordingTool("use_skill", """{"type":"object"}""", "{}"),
                new RecordingTool("Use_Skill", """{"type":"object","properties":{"id":{"type":"string"}}}""", "{}"),
            ]);

        var act = () => ResponsesToolClassifier.ClassifyAsync(
                [],
                [provider],
                ToolProviderContext,
                new RecordingLogger())
            .AsTask();

        var exception = await act.Should().ThrowAsync<AgentToolDiscoveryException>();
        exception.Which.Failure.Code.Should().Be(AgentToolDiscoveryFailureCode.ToolNameCollision);
        exception.Which.Failure.ToolName.Should().Be("use_skill");
    }

    [Fact]
    public async Task ClassifyAsync_ShouldTreatClientDeclaredAdditiveNameAsOwned()
    {
        var logger = new RecordingLogger();

        var result = await ResponsesToolClassifier.ClassifyAsync(
            [
                new ResponsesApplicationToolDeclaration("use_skill", "client tool", """{"type":"object"}""", "client-hash"),
            ],
            [
                new RecordingResponsesToolProvider(
                    [],
                    [
                        new RecordingTool("use_skill", """{"type":"object"}""", "{}"),
                        new RecordingTool("ornn_search_skills", """{"type":"object"}""", "{}"),
                    ]),
            ],
            ToolProviderContext,
            logger);

        result.ForwardedTools.Should().BeEmpty();
        result.EffectiveTools.Select(static tool => tool.Name)
            .Should().Equal("use_skill", "ornn_search_skills");
        result.SubstitutedToolNames.Should().BeEmpty();
        result.AdditiveToolNames.Should().Equal("use_skill", "ornn_search_skills");
        result.OwnedToolNames.Should().Equal("use_skill", "ornn_search_skills");
        logger.Messages.Should().NotContain(message =>
            message.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClassifyAsync_WhenProviderDiscoveryFails_ShouldContinueWithOtherProviders()
    {
        var logger = new RecordingLogger();

        var result = await ResponsesToolClassifier.ClassifyAsync(
            [
                new ResponsesApplicationToolDeclaration("client_tool", "client tool", """{"type":"object"}""", "client-hash"),
            ],
            [
                new FaultingResponsesToolProvider(),
                new RecordingResponsesToolProvider(
                    [new RecordingTool("client_tool", """{"type":"object"}""", "{}")],
                    [new RecordingTool("use_skill", """{"type":"object"}""", "{}")]),
            ],
            ToolProviderContext,
            logger);

        result.ForwardedTools.Should().BeEmpty();
        result.SubstitutedToolNames.Should().ContainSingle("client_tool");
        result.AdditiveToolNames.Should().ContainSingle("use_skill");
        result.OwnedToolNames.Should().Equal("client_tool", "use_skill");
        result.EffectiveTools.Select(static tool => tool.Name)
            .Should().Equal("client_tool", "use_skill");
        logger.Messages.Should().Contain(message =>
            message.Contains("substitute tool discovery failed", StringComparison.Ordinal));
        logger.Messages.Should().Contain(message =>
            message.Contains("additive tool discovery failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClassifyAsync_WhenCallerCancels_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => ResponsesToolClassifier.ClassifyAsync(
                [],
                [new RecordingResponsesToolProvider([], [])],
                ToolProviderContext,
                new RecordingLogger(),
                cts.Token)
            .AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ClassifyAsync_WithRestrictedEmptyCatalog_ShouldForwardCallerToolAndIgnoreAdditives()
    {
        var result = await ResponsesToolClassifier.ClassifyAsync(
            [
                new ResponsesApplicationToolDeclaration(
                    "client_tool",
                    "client tool",
                    "{\"type\":\"object\"}",
                    "client-hash"),
            ],
            [
                new RecordingResponsesToolProvider(
                    [],
                    [new RecordingTool("use_skill", "{}", "{}")]),
            ],
            ToolProviderContext,
            new RecordingLogger(),
            RestrictedEmptyCatalog());

        result.ForwardedTools.Should().ContainSingle().Which.Name.Should().Be("client_tool");
        result.OwnedToolNames.Should().BeEmpty();
        result.AdditiveToolNames.Should().BeEmpty();
        result.OwnedCatalog.Should().NotBeNull();
        result.OwnedCatalog!.Proof.ToolCount.Should().Be(0);
    }

    [Fact]
    public async Task ClassifyAsync_WithFrozenCanonicalWebCatalog_ShouldAdaptPascalAliasAtBoundaryOnly()
    {
        var canonical = new RecordingTool("web_search", "{\"type\":\"object\"}", "{}");
        var alias = new RecordingTool("WebSearch", "{\"type\":\"object\"}", "{}");
        var catalog = Catalog(canonical);

        var result = await ResponsesToolClassifier.ClassifyAsync(
            [
                new ResponsesApplicationToolDeclaration(
                    "WebSearch",
                    "client search",
                    "{\"type\":\"object\"}",
                    ResponsesToolSchemaHasher.Compute(alias.ParametersSchema)),
            ],
            [new RecordingResponsesToolProvider([alias], [])],
            ToolProviderContext,
            new RecordingLogger(),
            catalog);

        result.ForwardedTools.Should().BeEmpty();
        result.SubstitutedToolNames.Should().ContainSingle("WebSearch");
        result.OwnedToolNames.Should().ContainSingle("WebSearch");
        result.EffectiveTools.Should().ContainSingle().Which.Should().BeSameAs(alias);
        result.OwnedCatalog!.Proof.ToolDescriptors.Should().ContainSingle()
            .Which.Name.Should().Be("websearch");
        result.OwnedCatalog.Proof.ToolDescriptors[0].Origin
            .Should().Be(AgentTurnToolOrigin.ResponsesState);
        result.OwnedCatalog.Proof.AssertMatchesExactTools([alias]);
    }

    [Fact]
    public async Task ClassifyAsync_WhenAliasAndCanonicalBothMapToOneOwnedTool_ShouldFailClosed()
    {
        var canonical = new RecordingTool("web_search", "{}", "{}");
        var alias = new RecordingTool("WebSearch", "{}", "{}");

        var act = () => ResponsesToolClassifier.ClassifyAsync(
                [
                    new ResponsesApplicationToolDeclaration("WebSearch", "alias", "{}", "hash-a"),
                    new ResponsesApplicationToolDeclaration("web_search", "canonical", "{}", "hash-b"),
                ],
                [new RecordingResponsesToolProvider([alias], [])],
                ToolProviderContext,
                new RecordingLogger(),
                Catalog(canonical))
            .AsTask();

        var exception = await act.Should().ThrowAsync<AgentTurnToolCatalogException>();
        exception.Which.Failure.Code.Should().Be(AgentTurnToolCatalogFailureCode.ToolNameCollision);
    }

    [Fact]
    public void ResponsesForwardedTool_ShouldExposeDeclarationAndRejectNull()
    {
        ((Action)(() => new ResponsesForwardedTool(null!)))
            .Should().Throw<ArgumentNullException>();

        var declaration = new ResponsesApplicationToolDeclaration(
            "client_tool",
            "client description",
            """{"type":"object"}""",
            "schema-hash");

        var tool = new ResponsesForwardedTool(declaration);

        tool.Name.Should().Be("client_tool");
        tool.Description.Should().Be("client description");
        tool.ParametersSchema.Should().Be("""{"type":"object"}""");
        tool.SchemaHash.Should().Be("schema-hash");
        tool.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task ResponsesToolProvider_DefaultMethods_ShouldReturnEmptyLists()
    {
        IResponsesToolProvider provider = new EmptyResponsesToolProvider();

        (await provider.GetSubstituteToolsAsync(ToolProviderContext)).Should().BeEmpty();
        (await provider.GetAdditiveToolsAsync(ToolProviderContext)).Should().BeEmpty();
    }

    [Fact]
    public async Task PublicMethods_ShouldRejectNullArguments()
    {
        await ((Func<Task>)(async () => await ResponsesToolClassifier.ClassifyAsync(null!, [], ToolProviderContext, new RecordingLogger())))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(async () => await ResponsesToolClassifier.ClassifyAsync([], null!, ToolProviderContext, new RecordingLogger())))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(async () => await ResponsesToolClassifier.ClassifyAsync([], [], null!, new RecordingLogger())))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(async () => await ResponsesToolClassifier.ClassifyAsync([], [], ToolProviderContext, null!)))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    private sealed class RecordingTool(
        string name,
        string parametersSchema,
        string resultJson) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => $"{Name} description";

        public string ParametersSchema { get; } = parametersSchema;

        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(resultJson);
    }

    private static AgentTurnToolCatalog RestrictedEmptyCatalog() =>
        new(
            [],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null,
            diagnostics: null);

    private static AgentTurnToolCatalog Catalog(params IAgentTool[] tools) =>
        new(
            tools.Select(static tool => tool.Name),
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: "web",
            candidateIntentId: "web",
            diagnostics: null,
            exactTools: tools);

    private sealed class RecordingResponsesToolProvider(
        IReadOnlyList<IAgentTool> substituteTools,
        IReadOnlyList<IAgentTool> additiveTools) : IResponsesToolProvider
    {
        public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(substituteTools);

        public ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(additiveTools);
    }

    private sealed class FaultingResponsesToolProvider : IResponsesToolProvider
    {
        public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromException<IReadOnlyList<IAgentTool>>(
                new InvalidOperationException("substitute discovery failed"));

        public ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromException<IReadOnlyList<IAgentTool>>(
                new InvalidOperationException("additive discovery failed"));
    }

    private sealed class EmptyResponsesToolProvider : IResponsesToolProvider;

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
