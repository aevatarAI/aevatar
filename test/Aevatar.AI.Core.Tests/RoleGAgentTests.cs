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
    public void PendingApprovalMetadataControlKeys_DoNotPopulateToolRequestContext()
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
            };
            pending.Metadata.Add(LLMRequestMetadataKeys.RequestId, "metadata-request");
            pending.Metadata.Add(LLMRequestMetadataKeys.CallId, "metadata-call");
            pending.Metadata.Add(LLMRequestMetadataKeys.ScopeId, "metadata-scope");
            pending.Metadata.Add(LLMRequestMetadataKeys.OwnerSubject, "metadata-owner");
            pending.Metadata.Add(LLMRequestMetadataKeys.ResponseId, "metadata-response");
            pending.Metadata.Add(LLMRequestMetadataKeys.NyxIdAccessToken, "metadata-token");
            pending.Metadata.Add(LLMRequestMetadataKeys.ModelOverride, "metadata-model");
            pending.Metadata.Add("channel.platform", "metadata-platform");
            pending.Metadata.Add("channel.sender_id", "metadata-sender");

            var context = ResolvePendingToolContext(pending);
            using (AgentToolContextScope.Push(context))
            {
                AgentToolRequestContext.Current.Should().NotBeNull();
                AgentToolRequestContext.RequestId.Should().BeNull();
                AgentToolRequestContext.CallId.Should().BeNull();
                AgentToolRequestContext.ScopeId.Should().BeNull();
                AgentToolRequestContext.OwnerSubject.Should().BeNull();
                AgentToolRequestContext.ResponseId.Should().BeNull();
                AgentToolRequestContext.NyxIdAccessToken.Should().BeNull();
                AgentToolRequestContext.ModelOverride.Should().BeNull();
                AgentToolRequestContext.ChannelPlatform.Should().BeNull();
                AgentToolRequestContext.ChannelSenderId.Should().BeNull();
            }
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public void RoleGAgentSource_DoesNotUseLegacyMetadataContextMapper()
    {
        var source = File.ReadAllText(FindRepoFile("src/Aevatar.AI.Core/RoleGAgent.cs"));

        source.Should().NotContain("AgentToolExecutionContextMapper.FromMetadata");
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

    private static string FindRepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
