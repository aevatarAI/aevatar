using System.Text;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed class ScriptGenerateActorService
{
    internal const string ActorId = "app-script-generator:default";

    private readonly IActorRuntime _runtime;
    private readonly ScriptGenerateOrchestrator _orchestrator;
    private readonly ILogger<ScriptGenerateActorService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ScriptGenerateActorService(
        IActorRuntime runtime,
        ScriptGenerateOrchestrator orchestrator,
        ILogger<ScriptGenerateActorService> logger)
    {
        _runtime = runtime;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (await _runtime.ExistsAsync(ActorId))
            return;

        try
        {
            _ = await _runtime.CreateAsync<ScriptGenerateGAgent>(ActorId, ct);
            _logger.LogInformation("ScriptGenerateGAgent actor initialized. actorId={ActorId}", ActorId);
        }
        catch (InvalidOperationException)
        {
            if (!await _runtime.ExistsAsync(ActorId))
                throw;
        }
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
            var actor = await _runtime.GetAsync(ActorId)
                ?? throw new InvalidOperationException("Script generator actor is unavailable.");
            if (actor.Agent is not ScriptGenerateGAgent agent)
            {
                throw new InvalidOperationException(
                    "Script generator requires the in-memory actor runtime. The current runtime returned a proxy actor.");
            }

            agent.ResetConversation();
            return await _orchestrator.GenerateAsync(
                request,
                (prompt, metadata, token) => agent.GenerateWithReasoningAsync(
                    prompt,
                    BuildRequestId(),
                    metadata,
                    onReasoning,
                    token),
                onProgress,
                ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string BuildRequestId() => Guid.NewGuid().ToString("N");
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

    private string BuildSystemPrompt()
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("You are ScriptGenerateGAgent for aevatar app.");
            builder.AppendLine("Author and repair Aevatar ScriptBehavior C# source.");
            builder.AppendLine("Return C# source only. Do not wrap it in markdown fences. Do not explain.");
            builder.AppendLine("The source must compile with the aevatar scripting compiler.");
            builder.AppendLine("When current source is provided, treat the task as an edit and preserve unrelated sections.");
            builder.AppendLine("If compilation diagnostics are provided, fix every listed issue before returning.");
            builder.AppendLine("Prefer a single public sealed class that derives from ScriptBehavior<AppScriptReadModel, AppScriptReadModel>.");
            builder.AppendLine("Use AppScriptCommand as the command input unless the user explicitly asks for a richer contract.");
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
        using Aevatar.Tools.Cli.Hosting;

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
