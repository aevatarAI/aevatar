using System.Text;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowExecutionRuntimeContextSourceRegressionTests
{
    [Fact]
    public void WorkflowCoreExecutionSources_ShouldNotReintroduceGenericExecutionItemsApi()
    {
        var repositoryRoot = FindRepositoryRoot();
        var files = Directory
            .EnumerateFiles(
                Path.Combine(repositoryRoot, "src", "workflow", "Aevatar.Workflow.Core"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !IsGeneratedFile(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        files.Should().NotBeEmpty();

        var source = string.Join(
            Environment.NewLine,
            files.Select(path => StripComments(File.ReadAllText(path))));

        source.Should().NotContain("IWorkflowExecutionItemsContext");
        source.Should().NotContain("WorkflowExecutionItemsAccess");
        source.Should().NotContain("TryGetExecutionItem");
        source.Should().NotContain("SetExecutionItem");
        source.Should().NotContain("RemoveExecutionItem");
        source.Should().NotContain("_executionItems");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src", "workflow", "Aevatar.Workflow.Core")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

    private static bool IsGeneratedFile(string path) =>
        path.EndsWith(".g.cs", StringComparison.Ordinal) ||
        path.EndsWith(".Designer.cs", StringComparison.Ordinal);

    private static string StripComments(string source)
    {
        var builder = new StringBuilder(source.Length);
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
                    builder.Append(current);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                    builder.Append(' ');
                }
                else if (current == '\n')
                {
                    builder.Append(current);
                }

                continue;
            }

            if (!inString && !inChar && current == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                builder.Append(' ');
                continue;
            }

            if (!inString && !inChar && current == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                builder.Append(' ');
                continue;
            }

            builder.Append(current);

            if (inString)
            {
                if (inVerbatimString)
                {
                    if (current == '"' && next == '"')
                    {
                        builder.Append(next);
                        i++;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                        inVerbatimString = false;
                    }

                    continue;
                }

                if (current == '\\' && next != '\0')
                {
                    builder.Append(next);
                    i++;
                    continue;
                }

                if (current == '"')
                    inString = false;

                continue;
            }

            if (inChar)
            {
                if (current == '\\' && next != '\0')
                {
                    builder.Append(next);
                    i++;
                    continue;
                }

                if (current == '\'')
                    inChar = false;

                continue;
            }

            if (current == '@' && next == '"')
            {
                inString = true;
                inVerbatimString = true;
                builder.Append(next);
                i++;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                inVerbatimString = false;
                continue;
            }

            if (current == '\'')
                inChar = true;
        }

        return builder.ToString();
    }
}
