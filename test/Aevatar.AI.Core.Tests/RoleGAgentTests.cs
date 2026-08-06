using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests;

public class RoleGAgentTests
{
    [Fact]
    public void PendingApprovalToolContext_ShouldScrubCredentialsAndOwnedExternalControlKeys()
    {
        var previous = AgentToolRequestContext.Current;
        AgentToolRequestContext.Current = null;
        try
        {
            var pending = new PendingToolApprovalState
            {
                RequestId = "approval-1",
                SessionId = "session-1",
                ToolName = "danger",
                ToolCallId = "tool-call-1",
                ArgumentsJson = "{}",
                IsDestructive = true,
                ToolContext = (AgentToolExecutionContext.Empty with
                {
                    Request = new AgentToolRequestIdentity("approval-1", "tool-call-1"),
                    Credentials = new AgentToolCredentials("typed-token", "typed-org", "typed-sender"),
                    Caller = new AgentToolCallerContext("scope-1", "owner-1", "response-1"),
                    Routing = new LLMRequestRoutingContext("model-1", "route-1", 3, "memory-1"),
                    ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [LLMRequestMetadataKeys.RequestId] = "metadata-request",
                        [LLMRequestMetadataKeys.CallId] = "metadata-call",
                        [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
                        ["trace-id"] = "trace-1",
                    },
                }).ToPayload(),
            };

            var context = ResolvePendingToolContext(pending);
            using (AgentToolContextScope.Push(context))
            {
                AgentToolRequestContext.Current.Should().NotBeNull();
                AgentToolRequestContext.RequestId.Should().Be("approval-1");
                AgentToolRequestContext.CallId.Should().Be("tool-call-1");
                AgentToolRequestContext.ScopeId.Should().Be("scope-1");
                AgentToolRequestContext.OwnerSubject.Should().Be("owner-1");
                AgentToolRequestContext.ResponseId.Should().Be("response-1");
                AgentToolRequestContext.NyxIdAccessToken.Should().BeNull();
                AgentToolRequestContext.ModelOverride.Should().Be("model-1");
                AgentToolRequestContext.Current!.ExternalMetadata.Should().ContainKey("trace-id").WhoseValue.Should().Be("trace-1");
                AgentToolRequestContext.Current.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
            }
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public void TurnToolContext_ShouldCarryStableRequestAndActorExecutionOwner()
    {
        var request = new ChatRequestEvent
        {
            SessionId = "session-1",
        };
        var context = ResolveTurnToolContext(
            request,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity(null, null),
            },
            "role-agent-1");

        context.Request.RequestId.Should().Be("session-1");
        context.Request.CallId.Should().BeNull();
        context.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        context.ExecutionOwner.OwnerId.Should().Be("role-agent-1");
    }

    [Fact]
    public void ProductionSources_ShouldNotCallAgentToolExecutionContextMapperFromMetadata()
    {
        var root = FindRepoRoot();
        var productionFiles = new[] { "src", "agents" }
            .Select(path => Path.Combine(root, path))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            .Where(path => !IsGeneratedOrBuildOutput(path))
            .Where(path => !Path.GetFileName(path).Equals("AgentToolExecutionContextMapper.cs", StringComparison.Ordinal))
            .ToArray();

        productionFiles.Should().NotBeEmpty();
        var offenders = productionFiles
            .Where(path => StripSingleLineComments(File.ReadAllText(path))
                .Contains("AgentToolExecutionContextMapper.FromMetadata(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        offenders.Should().BeEmpty();
    }

    private static AgentToolExecutionContext ResolvePendingToolContext(PendingToolApprovalState pending)
    {
        var method = typeof(RoleGAgent).GetMethod(
            "ResolvePendingToolContext",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        var result = (AgentToolExecutionContext?)method!.Invoke(null, [pending]);
        result.Should().NotBeNull();
        return result!;
    }

    private static AgentToolExecutionContext ResolveTurnToolContext(
        ChatRequestEvent request,
        AgentToolExecutionContext context,
        string actorId)
    {
        var method = typeof(RoleGAgent).GetMethod(
            "ResolveTurnToolContext",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        var result = (AgentToolExecutionContext?)method!.Invoke(null, [request, context, actorId]);
        result.Should().NotBeNull();
        return result!;
    }

    private static string FindRepoFile(string relativePath)
    {
        var root = FindRepoRoot();
        var candidate = Path.Combine(root, relativePath);
        if (File.Exists(candidate))
            return candidate;

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static bool IsGeneratedOrBuildOutput(string path)
    {
        var normalized = path.Replace(Path.DirectorySeparatorChar, '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal)
               || normalized.Contains("/obj/", StringComparison.Ordinal)
               || normalized.EndsWith(".g.cs", StringComparison.Ordinal)
               || normalized.EndsWith(".Designer.cs", StringComparison.Ordinal);
    }

    private static string StripSingleLineComments(string source)
    {
        var lines = source.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var commentIndex = lines[index].IndexOf("//", StringComparison.Ordinal);
            if (commentIndex >= 0)
            {
                lines[index] = lines[index][..commentIndex];
            }
        }

        return string.Join('\n', lines);
    }
}
