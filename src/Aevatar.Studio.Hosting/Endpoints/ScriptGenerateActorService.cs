using System.Text;
using Aevatar.AI.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Aevatar.Studio.Application.Scripts.Contracts;
namespace Aevatar.Studio.Hosting.Endpoints;

internal interface IScriptGenerateService
{
    Task<ScriptGenerateResult> GenerateAsync(
        ScriptGenerateRequest request,
        Func<string, CancellationToken, Task>? onReasoning,
        Func<ScriptGenerateProgress, CancellationToken, Task>? onProgress,
        CancellationToken ct);
}

internal sealed class ScriptGenerateService : IScriptGenerateService
{
    private const string SessionName = "app-script-generator";

    private readonly AppAuthoringChatSessionFactory _chatSessionFactory;
    private readonly ScriptGenerateOrchestrator _orchestrator;
    private readonly ScriptGeneratePromptCatalog _prompts;

    public ScriptGenerateService(
        AppAuthoringChatSessionFactory chatSessionFactory,
        ScriptGenerateOrchestrator orchestrator,
        ScriptGeneratePromptCatalog prompts)
    {
        _chatSessionFactory = chatSessionFactory ?? throw new ArgumentNullException(nameof(chatSessionFactory));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    }

    public Task<ScriptGenerateResult> GenerateAsync(
        ScriptGenerateRequest request,
        Func<string, CancellationToken, Task>? onReasoning,
        Func<ScriptGenerateProgress, CancellationToken, Task>? onProgress,
        CancellationToken ct)
    {
        var session = _chatSessionFactory.Create(_prompts.CreateConfig(), SessionName, ct);
        return _orchestrator.GenerateAsync(
            request,
            (prompt, metadata, token) => session.GenerateWithReasoningAsync(
                prompt,
                BuildRequestId(),
                metadata,
                onReasoning,
                token),
            onProgress,
            ct);
    }

    private static string BuildRequestId() => Guid.NewGuid().ToString("N");
}

internal sealed class ScriptGenerateActorService
{
    internal const string ActorId = "app-script-generator:default";

    private readonly IScriptGenerateService _generateService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ScriptGenerateActorService(IScriptGenerateService generateService)
    {
        _generateService = generateService ?? throw new ArgumentNullException(nameof(generateService));
    }

    public Task EnsureInitializedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<ScriptGenerateResult> GenerateAsync(
        ScriptGenerateRequest request,
        Func<string, CancellationToken, Task>? onReasoning,
        Func<ScriptGenerateProgress, CancellationToken, Task>? onProgress,
        CancellationToken ct)
    {
        if (onProgress != null)
        {
            await onProgress(
                new ScriptGenerateProgress(
                    ScriptGenerateProgressStage.Starting,
                    0,
                    "Starting Ask AI script generation..."),
                ct);
        }

        await EnsureInitializedAsync(ct);
        if (onProgress != null && _gate.CurrentCount == 0)
        {
            await onProgress(
                new ScriptGenerateProgress(
                    ScriptGenerateProgressStage.Queued,
                    0,
                    "Another Ask AI request is running. Waiting for the generator..."),
                ct);
        }

        await _gate.WaitAsync(ct);
        try
        {
            return await _generateService.GenerateAsync(
                request,
                onReasoning,
                onProgress,
                ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class ScriptGenerateActorBootstrapHostedService : IHostedService
{
    private readonly ScriptGenerateActorService _service;

    public ScriptGenerateActorBootstrapHostedService(ScriptGenerateActorService service)
    {
        _service = service;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _service.EnsureInitializedAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}

internal sealed class ScriptGeneratePromptCatalog
{
    private readonly ILogger<ScriptGeneratePromptCatalog> _logger;

    public ScriptGeneratePromptCatalog(ILogger<ScriptGeneratePromptCatalog> logger)
    {
        _logger = logger;
        SystemPrompt = BuildSystemPrompt();
    }

    public string SystemPrompt { get; }

    public AIAgentConfig CreateConfig() => new()
    {
        SystemPrompt = SystemPrompt,
        Temperature = 0.1,
        MaxTokens = 4096,
        MaxToolRounds = 1,
        MaxHistoryMessages = 12,
        StreamBufferCapacity = 256,
    };

    private string BuildSystemPrompt()
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("You are ScriptGenerateGAgent for aevatar app.");
            builder.AppendLine("Author and repair Aevatar script packages and ScriptBehavior source.");
            builder.AppendLine("Return only the payload shape requested by the caller. Do not wrap it in markdown fences. Do not explain.");
            builder.AppendLine("The source must compile with the aevatar scripting compiler.");
            builder.AppendLine("When current source is provided, treat the task as an edit and preserve unrelated sections.");
            builder.AppendLine("If compilation diagnostics are provided, fix every listed issue before returning.");
            builder.AppendLine("Prefer a single public sealed class that derives from ScriptBehavior<AppScriptReadModel, AppScriptReadModel>.");
            builder.AppendLine("Use AppScriptCommand as the only inbound command contract.");
            builder.AppendLine("Do not introduce alternate command message types. If structured input is needed, parse it from AppScriptCommand.Input.");
            builder.AppendLine($"Write AppScriptReadModel fields named: {AppScriptProtocol.InputField}, {AppScriptProtocol.OutputField}, {AppScriptProtocol.StatusField}, {AppScriptProtocol.LastCommandIdField}, {AppScriptProtocol.NotesField}.");
            builder.AppendLine("Do not use query handlers. Keep behavior command/event/project-state focused.");
            builder.AppendLine();
            builder.AppendLine("Reference template:");
            builder.AppendLine(BuiltInTemplate.Trim());
            return builder.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to build script generation prompt.");
            return BuiltInTemplate;
        }
    }

    private const string BuiltInTemplate =
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Aevatar.Scripting.Abstractions;
        using Aevatar.Scripting.Abstractions.Behaviors;
        using Aevatar.Studio.Application.Scripts.Contracts;

        public sealed class DraftBehavior : ScriptBehavior<AppScriptReadModel, AppScriptReadModel>
        {
            protected override void Configure(IScriptBehaviorBuilder<AppScriptReadModel, AppScriptReadModel> builder)
            {
                builder
                    .OnCommand<AppScriptCommand>(HandleAsync)
                    .OnEvent<AppScriptUpdated>(
                        apply: static (_, evt, _) => evt.Current == null ? new AppScriptReadModel() : evt.Current.Clone())
                    .ProjectState(static (state, _) => state == null ? new AppScriptReadModel() : state.Clone());
            }

            private static Task HandleAsync(
                AppScriptCommand input,
                ScriptCommandContext<AppScriptReadModel> context,
                CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();

                var commandId = context.CommandId ?? input?.CommandId ?? string.Empty;
                var text = input?.Input ?? string.Empty;
                var current = AppScriptProtocol.CreateState(
                    text,
                    text.Trim().ToUpperInvariant(),
                    "ok",
                    commandId,
                    new[]
                    {
                        "trimmed",
                        "uppercased",
                    });

                context.Emit(new AppScriptUpdated
                {
                    CommandId = commandId,
                    Current = current,
                });
                return Task.CompletedTask;
            }
        }
        """;
}
