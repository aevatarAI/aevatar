using System.Text;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WorkflowModuleCompletionSourceRegressionTests
{
    [Fact]
    public async Task WorkflowLlmModules_ShouldNotHandlePresentationFrameCompletionInExecutableSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var modulePaths = new[]
        {
            Path.Combine(repositoryRoot, "src", "workflow", "Aevatar.Workflow.Core", "Modules", "LLMCallModule.cs"),
            Path.Combine(repositoryRoot, "src", "workflow", "Aevatar.Workflow.Core", "Modules", "EvaluateModule.cs"),
            Path.Combine(repositoryRoot, "src", "workflow", "Aevatar.Workflow.Core", "Modules", "ReflectModule.cs"),
        };

        foreach (var modulePath in modulePaths)
        {
            File.Exists(modulePath).Should().BeTrue("the regression guard must read the workflow module source file");

            var executableSource = StripComments(await File.ReadAllTextAsync(modulePath));

            executableSource.Should().NotContain("TextMessageEndEvent.Descriptor", Path.GetFileName(modulePath));
            executableSource.Should().NotContain("ChatResponseEvent.Descriptor", Path.GetFileName(modulePath));
            executableSource.Should().NotContain("HandleTextMessageEndAsync", Path.GetFileName(modulePath));
            executableSource.Should().NotContain("HandleChatResponseAsync", Path.GetFileName(modulePath));
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

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
