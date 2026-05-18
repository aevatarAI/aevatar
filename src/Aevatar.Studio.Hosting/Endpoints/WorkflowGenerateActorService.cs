using System.Text;
using Aevatar.AI.Core;
using Aevatar.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aevatar.Studio.Application.Scripts.Contracts;
namespace Aevatar.Studio.Hosting.Endpoints;

internal interface IWorkflowGenerateService
{
    Task<WorkflowGenerateResult> GenerateAsync(
        WorkflowGenerateRequest request,
        Func<string, CancellationToken, Task>? onReasoning,
        Func<WorkflowGenerateProgress, CancellationToken, Task>? onProgress,
        CancellationToken ct);
}

internal sealed class WorkflowGenerateService : IWorkflowGenerateService
{
    private const string SessionName = "app-workflow-generator";

    private readonly AppAuthoringChatSessionFactory _chatSessionFactory;
    private readonly WorkflowGenerateOrchestrator _orchestrator;
    private readonly WorkflowGeneratePromptCatalog _prompts;

    public WorkflowGenerateService(
        AppAuthoringChatSessionFactory chatSessionFactory,
        WorkflowGenerateOrchestrator orchestrator,
        WorkflowGeneratePromptCatalog prompts)
    {
        _chatSessionFactory = chatSessionFactory ?? throw new ArgumentNullException(nameof(chatSessionFactory));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    }

    public Task<WorkflowGenerateResult> GenerateAsync(
        WorkflowGenerateRequest request,
        Func<string, CancellationToken, Task>? onReasoning,
        Func<WorkflowGenerateProgress, CancellationToken, Task>? onProgress,
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

internal sealed class WorkflowGenerateActorService
{
    internal const string ActorId = "app-workflow-generator:default";

    private readonly IWorkflowGenerateService _generateService;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkflowGenerateActorService(IWorkflowGenerateService generateService)
    {
        _generateService = generateService ?? throw new ArgumentNullException(nameof(generateService));
    }

    public Task EnsureInitializedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<WorkflowGenerateResult> GenerateAsync(
        WorkflowGenerateRequest request,
        Func<string, CancellationToken, Task>? onReasoning,
        Func<WorkflowGenerateProgress, CancellationToken, Task>? onProgress,
        CancellationToken ct)
    {
        if (onProgress != null)
        {
            await onProgress(
                new WorkflowGenerateProgress(
                    WorkflowGenerateProgressStage.Starting,
                    0,
                    "Starting Ask AI workflow generation..."),
                ct);
        }

        await EnsureInitializedAsync(ct);
        if (onProgress != null && _gate.CurrentCount == 0)
        {
            await onProgress(
                new WorkflowGenerateProgress(
                    WorkflowGenerateProgressStage.Queued,
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

internal sealed class WorkflowGenerateActorBootstrapHostedService : IHostedService
{
    private readonly WorkflowGenerateActorService _service;

    public WorkflowGenerateActorBootstrapHostedService(WorkflowGenerateActorService service)
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

internal sealed class WorkflowGeneratePromptCatalog
{
    private const string SkillRelativePath = ".cursor/skills/aevatar-workflow-yaml/SKILL.md";
    private readonly ILogger<WorkflowGeneratePromptCatalog> _logger;

    public WorkflowGeneratePromptCatalog(ILogger<WorkflowGeneratePromptCatalog> logger)
    {
        _logger = logger;
        SystemPrompt = BuildSystemPrompt(LoadSkillMarkdown());
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

    private string LoadSkillMarkdown()
    {
        foreach (var candidate in EnumerateSkillCandidates())
        {
            try
            {
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load workflow authoring skill from {Path}", candidate);
            }
        }

        return BuiltInSkillFallback;
    }

    private static IEnumerable<string> EnumerateSkillCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     AevatarPaths.RepoRoot,
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory,
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(root, SkillRelativePath));
            }
            catch
            {
                continue;
            }

            if (seen.Add(fullPath))
                yield return fullPath;
        }
    }

    private static string BuildSystemPrompt(string skillMarkdown)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are WorkflowGenerateGAgent for aevatar app.");
        builder.AppendLine("Author and repair Aevatar workflow YAML.");
        builder.AppendLine("Return workflow YAML only. Do not wrap it in markdown fences. Do not explain.");
        builder.AppendLine("The YAML must be accepted by the aevatar workflow editor.");
        builder.AppendLine("When current YAML is provided, treat the task as an edit and preserve unrelated sections.");
        builder.AppendLine("If validation feedback is provided, fix every listed issue before returning.");
        builder.AppendLine("Use snake_case keys and author parameters as strings unless the schema requires another shape.");
        builder.AppendLine();
        builder.AppendLine("Reference:");
        builder.AppendLine(skillMarkdown.Trim());
        return builder.ToString().Trim();
    }

    private const string BuiltInSkillFallback = """
name: aevatar-workflow-yaml
description: Author Aevatar workflow YAML.

Canonical shape:
- top-level keys: name, description, configuration, roles, steps
- configuration.closed_world_mode is optional
- roles[*] can define id, name, system_prompt, provider, model, connectors
- steps[*] must define id, type and optional target_role, parameters, next, branches

Critical rules:
- return a complete workflow YAML document
- use snake_case keys
- keep step ids unique
- keep role ids unique
- when in doubt, prefer canonical primitive names
- keep parameter values as strings in authoring
""";
}
