using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRuntimeSourceRegressionTests
{
    [Fact]
    public void Channel_runtime_must_not_reintroduce_process_local_io_queue_or_lease_shell()
    {
        var source = ReadRepositorySources(
            "agents/Aevatar.GAgents.Channel.Runtime",
            "agents/Aevatar.GAgents.NyxidChat");

        source.Should().NotContain("LongRunningBusinessIoExecutor",
            "deleted per iter107/cluster-1 - actor-owned operation state + self-continuation");
        source.Should().NotContain("Channel<LongRunningBusinessIoWorkItem>",
            "deleted per iter107/cluster-1 - no process-local IO work queue");
        source.Should().NotContain("IDisposableProviderIoLease",
            "no-op lease abstraction deleted per Phase 8 r1 reject");
        source.Should().NotContain("DisposableProviderIoLeaseFactory",
            "no-op lease abstraction deleted per Phase 8 r1 reject");
    }

    [Fact]
    public void Startup_projection_activation_must_not_reintroduce_delay_backed_retry_loop()
    {
        foreach (var relativeFile in new[]
                 {
                     "agents/Aevatar.GAgents.Channel.Runtime/ChannelBotRegistrationStartupService.cs",
                     "agents/Aevatar.GAgents.Device/DeviceRegistrationStartupService.cs",
                     "agents/Aevatar.GAgents.Scheduled/UserAgentCatalogStartupService.cs",
                 })
        {
            var source = ReadRepositoryFile(relativeFile);

            source.Should().NotContain("await Task." + "Delay(",
                $"{relativeFile} should dispatch one activation attempt; retry/backoff belongs to actor/runtime scheduling infrastructure");
            source.Should().NotContain("MaxRetries",
                $"{relativeFile} should not own projection activation retry state in the hosted service");
        }
    }

    [Fact]
    public void Lark_new_message_post_must_stay_behind_outbound_dispatcher()
    {
        var repositoryRoot = GetRepositoryRoot();
        var bypasses = Directory.EnumerateFiles(Path.Combine(repositoryRoot, "agents"), "*.cs", SearchOption.AllDirectories)
            .Where(static file => !file.EndsWith("LarkOutboundDispatcher.cs", StringComparison.Ordinal))
            .Select(file => (File: Path.GetRelativePath(repositoryRoot, file), Source: StripComments(File.ReadAllText(file, Encoding.UTF8))))
            .Where(static item =>
                item.Source.Contains("open-apis/im/v1/messages?receive_id_type=", StringComparison.Ordinal) ||
                item.Source.Contains("open-apis/im/v1/messages?receive_id_type", StringComparison.Ordinal))
            .Select(static item => item.File)
            .ToArray();

        bypasses.Should().BeEmpty(
            "new Lark message POST, message_id parsing, and 230002 fallback belong to LarkOutboundDispatcher");
    }

    [Fact]
    public void Channel_conversation_turn_runner_must_not_depend_on_lark_outbound_transport()
    {
        var source = ReadRepositoryFile("agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs");
        foreach (var token in new[]
                 {
                     "Aevatar.GAgents.Platform.Lark",
                     "ILarkOutboundDispatcher",
                     "LarkOutboundDispatcher",
                     "LarkSendNewMessageRequest",
                     "LarkConversationTargets",
                     "LarkTextMessageSegmenter",
                 })
        {
            source.Should().NotContain(token,
                "the turn runner must delegate outbound Lark delivery to channel/platform delivery ports");
        }
    }

    [Fact]
    public void Authoring_lark_must_not_own_outbound_delivery_orchestration()
    {
        var source = ReadRepositorySources("agents/Aevatar.GAgents.Authoring.Lark");
        foreach (var token in new[]
                 {
                     "FeishuCardOutboundMessageSender",
                     "ILarkOutboundDispatcher",
                     "LarkOutboundDispatcher",
                     "LarkSendNewMessageRequest",
                     "IUserAgentDeliveryTargetReader",
                 })
        {
            source.Should().NotContain(token,
                "Authoring.Lark owns card/content authoring only; delivery orchestration belongs to channel/platform adapters");
        }
    }

    [Fact]
    public void Workflow_and_ai_human_interaction_delivery_must_not_depend_on_lark_notification_sender()
    {
        var repositoryRoot = GetRepositoryRoot();
        var forbiddenTokens = new[]
        {
            "FeishuCardNotificationPort",
            "FeishuCardOutboundMessageSender",
            "LarkMessageComposer",
            "LarkSendNewMessageRequest",
            "open-apis/im/v1/messages",
        };
        var roots = new[]
        {
            Path.Combine(repositoryRoot, "src/workflow"),
            Path.Combine(repositoryRoot, "src/Aevatar.Foundation.Abstractions/HumanInteraction"),
        };
        var hits = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(static file =>
                (file.EndsWith(".cs", StringComparison.Ordinal) ||
                 file.EndsWith(".proto", StringComparison.Ordinal)) &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (File: Path.GetRelativePath(repositoryRoot, file), Source: ReadPolicySurface(file)))
            .SelectMany(item => forbiddenTokens
                .Where(token => item.Source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{item.File}: {token}"))
            .ToArray();

        hits.Should().BeEmpty(
            "workflow/AI human-interaction delivery must depend on channel-neutral notification ports and intents, not Lark sender APIs");
    }

    [Fact]
    public void Workflow_core_notify_boundary_must_not_reintroduce_channel_or_raw_lark_tokens()
    {
        var repositoryRoot = GetRepositoryRoot();
        var guardSource = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools/ci/architecture_guards.sh"),
            Encoding.UTF8);
        foreach (var token in new[]
                 {
                     "Aevatar\\.GAgents\\.Channel\\.Abstractions",
                     "MessageContent",
                     "LarkMessageComposer",
                     "open-apis/im/v1/messages",
                     "raw card JSON",
                 })
        {
            guardSource.Should().Contain(token, "FI-006 architecture guard must keep the Workflow Core/Lark boundary token '{0}'", token);
        }

        var forbiddenPatterns = new[]
        {
            "Aevatar.GAgents.Channel.Abstractions",
            "MessageContent",
            "LarkMessageComposer",
            "open-apis/im/v1/messages",
            "interactive_card",
            "raw Lark",
            "raw card JSON",
        };
        var workflowCoreRoot = Path.Combine(repositoryRoot, "src/workflow/Aevatar.Workflow.Core");
        var hits = Directory.EnumerateFiles(workflowCoreRoot, "*.*", SearchOption.AllDirectories)
            .Where(static file =>
                (file.EndsWith(".cs", StringComparison.Ordinal) ||
                 file.EndsWith(".proto", StringComparison.Ordinal)) &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (File: Path.GetRelativePath(repositoryRoot, file), Source: ReadPolicySurface(file)))
            .SelectMany(item => forbiddenPatterns
                .Where(token => item.Source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{item.File}: {token}"))
            .ToArray();

        hits.Should().BeEmpty(
            "Workflow Core may publish typed notification events, but channel rendering and raw Lark card payloads belong at the channel boundary");
    }

    [Fact]
    public void Retired_nyx_relay_agent_builder_flow_must_not_reappear_in_source_or_tests()
    {
        var repositoryRoot = GetRepositoryRoot();
        var token = "NyxRelay" + "AgentBuilderFlow";
        var hits = Directory.EnumerateFiles(repositoryRoot, "*.*", SearchOption.AllDirectories)
            .Where(static file =>
                (file.EndsWith(".cs", StringComparison.Ordinal) ||
                 file.EndsWith(".proto", StringComparison.Ordinal) ||
                 file.EndsWith(".csproj", StringComparison.Ordinal)) &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (File: Path.GetRelativePath(repositoryRoot, file), Source: ReadPolicySurface(file)))
            .Where(item => item.Source.Contains(token, StringComparison.Ordinal))
            .Select(static item => item.File)
            .ToArray();

        hits.Should().BeEmpty("the retired relay builder flow was deleted in #1306 and must not re-enter production code or tests");
    }

    [Fact]
    public void Production_skill_routing_must_not_reintroduce_concrete_daily_skill_hardcoding()
    {
        // Refactor (iter1/cluster-issue1553): Old pattern: production code and prompts knew
        // concrete `/daily` -> `chrono-ai-daily` routing. New principle: skill commands use
        // generic discovery; specific skill names may appear only in tests/docs fixtures.
        var repositoryRoot = GetRepositoryRoot();
        var forbiddenTokens = new[]
        {
            "/daily",
            "chrono-ai-daily",
            "DailySkillName",
            "TryBuildDailySkillInvocationPrompt",
        };
        var hits = Directory.EnumerateFiles(repositoryRoot, "*.*", SearchOption.AllDirectories)
            .Where(static file =>
                (file.EndsWith(".cs", StringComparison.Ordinal) ||
                 file.EndsWith(".proto", StringComparison.Ordinal) ||
                 file.EndsWith(".md", StringComparison.Ordinal)) &&
                (file.Contains($"{Path.DirectorySeparatorChar}agents{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                 file.Contains($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (File: Path.GetRelativePath(repositoryRoot, file), Source: ReadPolicySurface(file)))
            .SelectMany(item => forbiddenTokens
                .Where(token => item.Source.Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(token => $"{item.File}: {token}"))
            .ToArray();

        hits.Should().BeEmpty(
            "production code, prompts, route contracts, type names, and field names must not hardcode concrete skill names; use generic skill discovery");
    }

    [Fact]
    public void Conversation_actor_must_not_switch_on_lark_card_reply_operation_step_payload_cases()
    {
        var actorSource = StripComments(ReadRepositoryFile(
            "agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs"));

        actorSource.Should().NotContain("PayloadOneofCase.LarkCard");
        actorSource.Should().NotContain("ExecuteLarkCardOperationStepAsync");
        actorSource.Should().NotContain("HandleLlmReplyCardStreamChunkAsync");
        actorSource.Should().NotContain("HandleLarkCardOperationCompletedAsync");
        actorSource.Should().NotContain("HandleLarkCardOperationTimeoutFiredAsync");
    }

    [Fact]
    public void Reply_streaming_renderers_own_typed_step_payload_recognition()
    {
        var rendererSource = StripComments(ReadRepositoryFile(
            "agents/Aevatar.GAgents.Channel.Runtime/Conversation/ReplyStreaming/NyxRelayTextReplyStreamRenderer.cs")) +
                             StripComments(ReadRepositoryFile(
                                 "agents/Aevatar.GAgents.Channel.Runtime/Conversation/ReplyStreaming/LarkCardReplyStreamRenderer.cs"));

        rendererSource.Should().Contain("PayloadCase");
        rendererSource.Should().Contain("PayloadOneofCase.NyxRelayText");
        rendererSource.Should().Contain("PayloadOneofCase.LarkCard");
    }

    private static string ReadRepositorySources(params string[] relativeDirectories)
    {
        var repositoryRoot = GetRepositoryRoot();
        var builder = new StringBuilder();
        foreach (var relativeDirectory in relativeDirectories)
        {
            var directory = Path.Combine(repositoryRoot, relativeDirectory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                         .Where(static file =>
                             file.EndsWith(".cs", StringComparison.Ordinal) ||
                             file.EndsWith(".proto", StringComparison.Ordinal)))
            {
                builder.AppendLine(File.ReadAllText(file, Encoding.UTF8));
            }
        }

        return builder.ToString();
    }

    private static string ReadRepositoryFile(string relativeFile)
    {
        var repositoryRoot = GetRepositoryRoot();
        return File.ReadAllText(Path.Combine(repositoryRoot, relativeFile), Encoding.UTF8);
    }

    private static string StripComments(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string ReadPolicySurface(string file)
    {
        var source = File.ReadAllText(file, Encoding.UTF8);
        return file.EndsWith(".md", StringComparison.Ordinal)
            ? source
            : StripComments(source);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
