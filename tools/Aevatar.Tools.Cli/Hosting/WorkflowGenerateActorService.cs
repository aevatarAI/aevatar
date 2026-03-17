using System.Text;
using Aevatar.AI.Core;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.Configurations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Tools.Cli.Hosting;

internal sealed class WorkflowGenerateActorService
{
    internal const string ActorId = "app-workflow-generator:default";

    private readonly IActorRuntime _runtime;
    private readonly WorkflowGenerateOrchestrator _orchestrator;
    private readonly ILogger<WorkflowGenerateActorService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkflowGenerateActorService(
        IActorRuntime runtime,
        WorkflowGenerateOrchestrator orchestrator,
        ILogger<WorkflowGenerateActorService> logger)
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
            _ = await _runtime.CreateAsync<WorkflowGenerateGAgent>(ActorId, ct);
            _logger.LogInformation("WorkflowGenerateGAgent actor initialized. actorId={ActorId}", ActorId);
        }
        catch (InvalidOperationException)
        {
            if (!await _runtime.ExistsAsync(ActorId))
                throw;

            // Concurrent bootstrap already created the actor.
        }
    }

    public async Task<WorkflowGenerateResult> GenerateAsync(
        WorkflowGenerateRequest request,
        Func<string, CancellationToken, Task>? onReasoning,
        CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var actor = await _runtime.GetAsync(ActorId)
                ?? throw new InvalidOperationException("Workflow generator actor is unavailable.");
            if (actor.Agent is not WorkflowGenerateGAgent agent)
            {
                throw new InvalidOperationException(
                    "Workflow generator requires the in-memory actor runtime. The current runtime returned a proxy actor.");
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
                ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string BuildRequestId() => Guid.NewGuid().ToString("N");
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

internal sealed class WorkflowGenerateAgentDefaultsProvider : IAgentClassDefaultsProvider<AIAgentConfig>
{
    private readonly AgentClassDefaultsSnapshot<AIAgentConfig> _generatorDefaults;
    private readonly AgentClassDefaultsSnapshot<AIAgentConfig> _emptyDefaults = new(new AIAgentConfig(), 0);

    public WorkflowGenerateAgentDefaultsProvider(WorkflowGeneratePromptCatalog prompts)
    {
        _generatorDefaults = new AgentClassDefaultsSnapshot<AIAgentConfig>(
            new AIAgentConfig
            {
                SystemPrompt = prompts.SystemPrompt,
                Temperature = 0.1,
                MaxTokens = 4096,
                MaxToolRounds = 1,
                MaxHistoryMessages = 12,
                StreamBufferCapacity = 256,
            },
            1);
    }

    public ValueTask<AgentClassDefaultsSnapshot<AIAgentConfig>> GetSnapshotAsync(
        Type agentType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentType);
        ct.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            agentType == typeof(WorkflowGenerateGAgent)
                ? _generatorDefaults
                : _emptyDefaults);
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
