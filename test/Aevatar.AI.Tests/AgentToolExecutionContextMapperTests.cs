using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AgentToolExecutionContextMapperTests
{
    [Fact]
    public void FromRequest_WhenTypedFieldsAndLegacyMetadataOverlap_ShouldUseOnlyTypedControlAndScrubMetadata()
    {
        var request = new LLMRequest
        {
            Messages = [],
            RequestId = "typed-request",
            CallerContext = new LLMRequestCallerContext(
                ScopeId: "typed-scope",
                OwnerSubject: "typed-owner",
                ResponseId: "typed-response",
                Credentials: new LLMRequestCallerCredentials("typed-access")),
            RoutingContext = new LLMRequestRoutingContext(
                ModelOverride: "typed-model",
                NyxIdRoutePreference: "typed-route",
                MaxToolRoundsOverride: 9,
                UserMemoryPrompt: "typed-memory"),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.RequestId] = "legacy-request",
                [LLMRequestMetadataKeys.CallId] = "legacy-call",
                [LLMRequestMetadataKeys.ScopeId] = "legacy-scope",
                [LLMRequestMetadataKeys.OwnerSubject] = "legacy-owner",
                [LLMRequestMetadataKeys.ResponseId] = "legacy-response",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "legacy-access",
                [LLMRequestMetadataKeys.NyxIdOrgToken] = "legacy-org",
                [LLMRequestMetadataKeys.ModelOverride] = "legacy-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "legacy-route",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "4",
                [LLMRequestMetadataKeys.UserMemoryPrompt] = "legacy-memory",
                ["external-trace"] = "trace-1",
            },
        };

        var context = AgentToolExecutionContextMapper.FromRequest(request);

        context.Request.RequestId.Should().Be("typed-request");
        context.Request.CallId.Should().BeNull();
        context.Caller.ScopeId.Should().Be("typed-scope");
        context.Caller.OwnerSubject.Should().Be("typed-owner");
        context.Caller.ResponseId.Should().Be("typed-response");
        context.Credentials.NyxIdAccessToken.Should().Be("typed-access");
        context.Credentials.NyxIdOrgToken.Should().BeNull();
        context.Routing.ModelOverride.Should().Be("typed-model");
        context.Routing.NyxIdRoutePreference.Should().Be("typed-route");
        context.Routing.MaxToolRoundsOverride.Should().Be(9);
        context.Routing.UserMemoryPrompt.Should().Be("typed-memory");
        context.ExternalMetadata.Should().ContainSingle();
        context.ExternalMetadata["external-trace"].Should().Be("trace-1");
    }

    [Fact]
    public void FromRequest_WhenOnlyMetadataContainsOwnedControlKeys_ShouldNotPromoteThemToControlContext()
    {
        var request = new LLMRequest
        {
            Messages = [],
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.RequestId] = "legacy-request",
                [LLMRequestMetadataKeys.CallId] = "legacy-call",
                [LLMRequestMetadataKeys.ScopeId] = "legacy-scope",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "legacy-access",
                [LLMRequestMetadataKeys.NyxIdOrgToken] = "legacy-org",
                [LLMRequestMetadataKeys.ModelOverride] = "legacy-model",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = "legacy-route",
                [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "4",
                ["external-trace"] = "trace-1",
            },
        };

        var context = AgentToolExecutionContextMapper.FromRequest(request);

        context.Request.RequestId.Should().BeNull();
        context.Request.CallId.Should().BeNull();
        context.Caller.ScopeId.Should().BeNull();
        context.Credentials.NyxIdAccessToken.Should().BeNull();
        context.Credentials.NyxIdOrgToken.Should().BeNull();
        context.Routing.ModelOverride.Should().BeNull();
        context.Routing.NyxIdRoutePreference.Should().BeNull();
        context.Routing.MaxToolRoundsOverride.Should().BeNull();
        context.ExternalMetadata.Should().ContainSingle();
        context.ExternalMetadata["external-trace"].Should().Be("trace-1");
    }

    [Fact]
    public void FromRequest_WhenToolContextIsProvided_ShouldReturnTypedContextAndIgnoreMetadataFallback()
    {
        var typedContext = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("typed-request", "typed-call"),
            Credentials = new AgentToolCredentials("typed-token", null, null),
            ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["typed-note"] = "kept",
            },
        };
        var request = new LLMRequest
        {
            Messages = [],
            ToolContext = typedContext,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.RequestId] = "metadata-request",
                [LLMRequestMetadataKeys.CallId] = "metadata-call",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
                ["external-trace"] = "trace-1",
            },
        };

        var context = AgentToolExecutionContextMapper.FromRequest(request);

        context.Should().BeSameAs(typedContext);
        context.Request.RequestId.Should().Be("typed-request");
        context.Request.CallId.Should().Be("typed-call");
        context.Credentials.NyxIdAccessToken.Should().Be("typed-token");
        context.ExternalMetadata.Should().ContainSingle("typed-note", "kept");
        context.ExternalMetadata.Should().NotContainKey("external-trace");
    }

    [Fact]
    public void FromMetadata_WhenChannelCanonicalKeysAreAbsent_ShouldMapLegacyAliases()
    {
        var context = AgentToolExecutionContextMapper.FromMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["platform"] = "lark",
            ["sender_id"] = "ou-legacy",
            ["registration_scope_id"] = "scope-legacy",
            ["message_id"] = "msg-legacy",
            ["platform_message_id"] = "platform-msg-legacy",
        });

        context.Channel.Platform.Should().Be("lark");
        context.Channel.SenderId.Should().Be("ou-legacy");
        context.Channel.RegistrationScopeId.Should().Be("scope-legacy");
        context.Channel.MessageId.Should().Be("msg-legacy");
        context.Channel.PlatformMessageId.Should().Be("platform-msg-legacy");
    }

    [Fact]
    public void FromMetadata_WhenLarkAliasesArePresent_ShouldMapSenderAndMessageFallbacks()
    {
        var context = AgentToolExecutionContextMapper.FromMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lark.open_id"] = "ou-lark",
            ["lark.message_id"] = "msg-lark",
        });

        context.Channel.SenderId.Should().Be("ou-lark");
        context.Channel.MessageId.Should().Be("msg-lark");
    }

    [Fact]
    public void PayloadRoundTrip_ShouldPreserveTypedContextAndStripOwnedControlKeys()
    {
        var context = new AgentToolExecutionContext(
            new AgentToolRequestIdentity(" request-1 ", " call-1 "),
            new AgentToolCredentials(" access-1 ", " org-1 ", " sender-access-1 "),
            new AgentToolCallerContext(" scope-1 ", " owner-1 ", " response-1 "),
            new AgentToolChannelContext(" telegram ", " sender-1 ", " registration-1 ", " message-1 ", " platform-message-1 "),
            new AgentToolSenderBindingContext(" binding-1 "),
            new LLMRequestRoutingContext(" model-1 ", " route-1 ", 7, " memory-1 "),
            new AgentToolConnectedServicesContext("""{"service":"telegram"}"""),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["external-trace"] = "trace-1",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "legacy-token",
                ["telegram.chat_id"] = "10001",
            });

        var payload = context.ToPayload();
        var copy = AgentToolExecutionContextMapper.FromPayload(
            AgentToolExecutionContextPayload.Parser.ParseFrom(payload.ToByteArray()));

        copy.Request.RequestId.Should().Be("request-1");
        copy.Request.CallId.Should().Be("call-1");
        copy.Credentials.NyxIdAccessToken.Should().Be("access-1");
        copy.Credentials.NyxIdOrgToken.Should().Be("org-1");
        copy.Credentials.SenderNyxIdAccessToken.Should().Be("sender-access-1");
        copy.Caller.ScopeId.Should().Be("scope-1");
        copy.Caller.OwnerSubject.Should().Be("owner-1");
        copy.Caller.ResponseId.Should().Be("response-1");
        copy.Channel.Platform.Should().Be("telegram");
        copy.Channel.SenderId.Should().Be("sender-1");
        copy.Channel.RegistrationScopeId.Should().Be("registration-1");
        copy.Channel.MessageId.Should().Be("message-1");
        copy.Channel.PlatformMessageId.Should().Be("platform-message-1");
        copy.SenderBinding.BindingId.Should().Be("binding-1");
        copy.Routing.ModelOverride.Should().Be("model-1");
        copy.Routing.NyxIdRoutePreference.Should().Be("route-1");
        copy.Routing.MaxToolRoundsOverride.Should().Be(7);
        copy.Routing.UserMemoryPrompt.Should().Be("memory-1");
        copy.ConnectedServices.ContextJson.Should().Be("""{"service":"telegram"}""");
        copy.ExternalMetadata.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("external-trace", "trace-1"));
    }

    [Fact]
    public void FromPayload_WhenPayloadIsNull_ShouldReturnEmptyContext()
    {
        AgentToolExecutionContextMapper.FromPayload(null).Should().Be(AgentToolExecutionContext.Empty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    public void FromMetadata_WhenMaxToolRoundsOverrideIsInvalid_ShouldLeaveTypedOverrideUnset(string maxRounds)
    {
        var context = AgentToolExecutionContextMapper.FromMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.MaxToolRoundsOverride] = maxRounds,
        });

        context.Routing.MaxToolRoundsOverride.Should().BeNull();
    }

    [Fact]
    public void ScopeDispose_WhenNestedScopesAreUsed_ShouldRestoreOuterContext()
    {
        var outer = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("outer-request", "outer-call"),
        };
        var inner = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("inner-request", "inner-call"),
        };

        using (AgentToolContextScope.Push(outer))
        {
            AgentToolRequestContext.Current.Should().BeSameAs(outer);

            using (AgentToolContextScope.Push(inner))
            {
                AgentToolRequestContext.Current.Should().BeSameAs(inner);
            }

            AgentToolRequestContext.Current.Should().BeSameAs(outer);
        }

        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public void ProductionSources_ShouldNotUseLegacyToolMetadataControlShims()
    {
        var repositoryRoot = FindRepositoryRoot();
        var files = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repositoryRoot, "agents"), "*.cs", SearchOption.AllDirectories))
            .Where(static path => !IsGeneratedFile(path))
            .Where(static path => !path.EndsWith(
                Path.Combine("ToolProviders", "AgentToolRequestContext.cs"),
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        files.Should().NotBeEmpty();

        var source = string.Join(
            Environment.NewLine,
            files.Select(path => StripComments(File.ReadAllText(path))));

        source.Should().NotContain("AgentToolRequestContext.CurrentMetadata");
        source.Should().NotContain("AgentToolRequestContext.TryGet(");
        source.Should().NotContain(".ToLegacyMetadata(");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

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
                        inString = inVerbatimString = false;
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
                builder.Append(next);
                i++;
                inString = inVerbatimString = true;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '\'')
                inChar = true;
        }

        return builder.ToString();
    }
}
