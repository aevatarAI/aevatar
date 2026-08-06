using System.Text;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

/// <summary>
/// Drift guard for the shipped repo workflow definition: the rendered step arguments
/// must satisfy the current codex_exec tool admission contract (issue #3148 regressed
/// silently because nothing exercised the yaml against the tool after the typed-target
/// refactor).
/// </summary>
public sealed class CodexExecuteWorkflowDefinitionContractTests
{
    [Fact]
    public async Task CodexExecuteYaml_RenderedArguments_AreAdmittedByCodexExecTool()
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "workflows", "codex_execute.yaml"));
        var workflow = new WorkflowParser().Parse(yaml);
        var step = workflow.Steps.Should().ContainSingle().Subject;
        step.Parameters["tool"].Should().Be("codex_exec");

        const string prompt = "Fix the failing test'; echo \"$(id)\"\n保留这些字符";
        var arguments = new WorkflowExpressionEvaluator().Evaluate(
            step.Parameters["arguments"],
            new Dictionary<string, string>
            {
                ["input.service"] = "codex-node",
                ["input.principal"] = "runner",
                ["input.prompt"] = prompt,
            });

        var executor = new RecordingSshExecutor();
        var tool = new NyxIdCodexExecTool(executor, new NyxIdToolOptions());
        var result = await tool.ExecuteAsync(arguments);

        result.Should().NotContain("\"error\"");
        executor.Request.Should().NotBeNull();
        executor.Request!.Service.Should().Be("codex-node");
        executor.Request.Principal.Should().Be("runner");
        executor.Request.TimeoutSecs.Should().Be(300);
        DecodePrompt(executor.Request.Command).Should().Be(prompt);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull("the test must run inside the aevatar repository checkout");
        return directory!.FullName;
    }

    private static string DecodePrompt(string command)
    {
        const string prefix = "p='";
        var start = command.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = command.IndexOf("';", start, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(prefix.Length);
        end.Should().BeGreaterThan(start);
        return Encoding.UTF8.GetString(Convert.FromBase64String(command[start..end]));
    }

    private sealed class RecordingSshExecutor : INyxIdSshCommandExecutor
    {
        public NyxIdSshCommandRequest? Request { get; private set; }

        public Task<string> ExecuteAsync(
            NyxIdSshCommandRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult("""{"exit_code":0,"stdout":"done","stderr":"","timed_out":false}""");
        }
    }
}
