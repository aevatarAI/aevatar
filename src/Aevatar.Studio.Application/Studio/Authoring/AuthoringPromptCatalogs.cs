using System.Text;
using Aevatar.Configuration;
using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Application.Studio.Authoring;

// Refactor (iter21/cluster-001):
//   Old pattern: Host prompt catalogs named fake GAgents and coupled profile defaults to Host type tokens.
//   New principle: Application owns authoring profile prompts by workflow/script preview kind, without fake actor identity.
public sealed class WorkflowAuthoringPromptCatalog
{
    private const string SkillRelativePath = ".cursor/skills/aevatar-workflow-yaml/SKILL.md";
    private const string AuthorableRootFieldsToken = "{{workflow_authorable_root_fields}}";
    private const string UnsupportedDialectRootFieldsToken = "{{workflow_unsupported_dialect_root_fields}}";
    private readonly ILogger<WorkflowAuthoringPromptCatalog> _logger;
    private readonly WorkflowCompatibilityProfile _profile;

    public WorkflowAuthoringPromptCatalog(
        ILogger<WorkflowAuthoringPromptCatalog> logger,
        WorkflowCompatibilityProfile? profile = null)
    {
        _logger = logger;
        _profile = profile ?? WorkflowCompatibilityProfile.AevatarV1;
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
                _logger.LogWarning(ex, "Failed to load workflow authoring skill from {Path}", candidate);
            }
        }

        return BuildBuiltInSkillFallback();
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
            catch (ArgumentException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }
            catch (PathTooLongException)
            {
                continue;
            }

            if (seen.Add(fullPath))
                yield return fullPath;
        }
    }

    private string BuildSystemPrompt(string skillMarkdown)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You author and repair Aevatar workflow YAML for Studio Ask AI preview.");
        builder.AppendLine("Return workflow YAML only. Do not wrap it in markdown fences. Do not explain.");
        builder.AppendLine("The YAML must be accepted by the aevatar workflow editor.");
        builder.AppendLine($"The only supported top-level fields are: {_profile.FormatRootFields()}.");
        builder.AppendLine($"Do not emit top-level fields from other workflow dialects, including {_profile.FormatRejectedDialectRootFields()}.");
        builder.AppendLine("When current YAML is provided, treat the task as an edit and preserve unrelated sections.");
        builder.AppendLine("If validation feedback is provided, fix every listed issue before returning.");
        builder.AppendLine("Use snake_case keys and author parameters as strings unless the schema requires another shape.");
        builder.AppendLine();
        builder.AppendLine("Reference:");
        builder.AppendLine(RenderSkillMarkdown(skillMarkdown).Trim());
        return builder.ToString().Trim();
    }

    private string RenderSkillMarkdown(string skillMarkdown) =>
        skillMarkdown
            .Replace(AuthorableRootFieldsToken, _profile.FormatRootFields(), StringComparison.Ordinal)
            .Replace(UnsupportedDialectRootFieldsToken, _profile.FormatRejectedDialectRootFields(), StringComparison.Ordinal);

    private string BuildBuiltInSkillFallback() =>
        $"""
         name: aevatar-workflow-yaml
         description: Author Aevatar workflow YAML.

         Canonical shape:
         - top-level keys: {_profile.FormatRootFields()}
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

public sealed class ScriptAuthoringPromptCatalog
{
    private readonly ILogger<ScriptAuthoringPromptCatalog> _logger;

    public ScriptAuthoringPromptCatalog(ILogger<ScriptAuthoringPromptCatalog> logger)
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
            builder.AppendLine("You author and repair Aevatar script packages and ScriptBehavior source for Studio Ask AI preview.");
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
            _logger.LogWarning(ex, "Failed to build script generation prompt.");
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
