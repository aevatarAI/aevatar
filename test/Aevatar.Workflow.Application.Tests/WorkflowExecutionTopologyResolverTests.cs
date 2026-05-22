using System.Text;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowExecutionTopologyResolverTests
{
    [Fact]
    public void WorkflowReportTopologySource_ShouldNotReintroduceRuntimeChildrenSideRead()
    {
        var repositoryRoot = ResolveRepositoryRoot();
        var removedSideReadFiles = new[]
        {
            "src/workflow/Aevatar.Workflow.Application/Orchestration/IWorkflowExecutionTopologyResolver.cs",
            "src/workflow/Aevatar.Workflow.Application/Orchestration/WorkflowExecutionTopologyResolver.cs",
        };
        var productionFiles = new[]
        {
            "src/workflow/Aevatar.Workflow.Application/DependencyInjection/ServiceCollectionExtensions.cs",
            "src/workflow/Aevatar.Workflow.Application.Abstractions/Queries/WorkflowExecutionQueryModels.cs",
            "src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionArtifactMaterializationSupport.cs",
            "src/workflow/Aevatar.Workflow.Projection/ReadModels/WorkflowExecutionReadModelMapper.cs",
            "src/workflow/Aevatar.Workflow.Projection/ReadModels/WorkflowRunReadModels.Partial.cs",
        };
        var forbiddenLiveCodeTokens = new[]
        {
            "IWorkflowExecutionTopologyResolver",
            "ActorRuntimeWorkflowExecutionTopologyResolver",
            "RuntimeSnapshot",
            "GetChildrenIdsAsync",
        };

        foreach (var relativePath in removedSideReadFiles)
        {
            File.Exists(Path.Combine(repositoryRoot, relativePath))
                .Should().BeFalse($"{relativePath} was the runtime topology side-read surface");
        }

        foreach (var relativePath in productionFiles)
        {
            var source = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            var sourceWithoutComments = StripCSharpComments(source);

            foreach (var forbiddenToken in forbiddenLiveCodeTokens)
            {
                sourceWithoutComments.Should().NotContain(
                    forbiddenToken,
                    $"{relativePath} must keep workflow report topology on committed projection facts");
            }
        }
    }

    private static string ResolveRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "aevatar.slnx")))
                return current;

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        throw new InvalidOperationException("Could not resolve repository root.");
    }

    private static string StripCSharpComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inVerbatimString = false;
        var inChar = false;

        for (var i = 0; i < source.Length; i++)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                    result.Append(current);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (!inString && !inChar && current == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (!inString && !inChar && current == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            result.Append(current);

            if (inString)
            {
                if (inVerbatimString && current == '"' && next == '"')
                {
                    result.Append(next);
                    i++;
                    continue;
                }

                if (current == '"' && (inVerbatimString || !IsEscaped(source, i)))
                {
                    inString = false;
                    inVerbatimString = false;
                }

                continue;
            }

            if (inChar)
            {
                if (current == '\'' && !IsEscaped(source, i))
                    inChar = false;

                continue;
            }

            if (current == '"' && !IsEscaped(source, i))
            {
                inString = true;
                inVerbatimString = i > 0 && source[i - 1] == '@';
                continue;
            }

            if (current == '\'' && !IsEscaped(source, i))
                inChar = true;
        }

        return result.ToString();
    }

    private static bool IsEscaped(string source, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && source[i] == '\\'; i--)
            slashCount++;

        return slashCount % 2 == 1;
    }
}
