using System.Text.RegularExpressions;
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

    [Fact]
    public async Task WorkflowLlmModules_ShouldOnlyUseLlmInvocationCompletedEventAsCompletionDriver()
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
            var executableSource = StripComments(await File.ReadAllTextAsync(modulePath));

            executableSource.Should().Contain("WorkflowLlmInvocationCompletedEvent.Descriptor", Path.GetFileName(modulePath));
            executableSource.Should().NotContain("WorkflowRoleReplyRecordedEvent.Descriptor", Path.GetFileName(modulePath));
            executableSource.Should().NotContain("RoleChatSessionCompletedEvent.Descriptor", Path.GetFileName(modulePath));
            executableSource.Should().NotContain("TextMessageEndEvent.Descriptor", Path.GetFileName(modulePath));
            executableSource.Should().NotContain("ChatResponseEvent.Descriptor", Path.GetFileName(modulePath));
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
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }
}
