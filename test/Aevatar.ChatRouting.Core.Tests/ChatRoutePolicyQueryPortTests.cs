using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;

namespace Aevatar.ChatRouting.Core.Tests;

public sealed class ChatRoutePolicyQueryPortTests
{
    [Fact]
    public async Task LookupForCallerAsync_MissingDocument_ReturnsNull()
    {
        var reader = new FakePolicyReader([]);
        var port = new ChatRoutePolicyQueryPort(reader);

        var snapshot = await port.LookupForCallerAsync(ChatRouteResolverTests.CallerScope());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task LookupForCallerAsync_FakeReadmodelThenResolver_EndToEndUsesPolicy()
    {
        var document = new ChatRoutePolicyCurrentStateDocument
        {
            Id = "chat-route-policy:bot-1",
            ActorId = "chat-route-policy:bot-1",
            OwnerScope = ChatRouteResolverTests.CallerScope().Clone(),
            DefaultTarget = ChatRouteResolverTests.ForwardToModelAction("default-model"),
        };
        document.Rules.Add(new ChatRouteRule
        {
            RuleId = "lark-summary",
            Priority = 10,
            Match = new ChatRouteMatch { Channel = "lark", CommandName = "/summary" },
            Action = ChatRouteResolverTests.ForwardToModelAction("summary-model"),
        });
        var port = new ChatRoutePolicyQueryPort(new FakePolicyReader([document]));
        var resolver = new ChatRouteResolver(new StaticFallbackProvider("fallback-model"));

        var snapshot = await port.LookupForCallerAsync(ChatRouteResolverTests.CallerScope());
        var decision = resolver.Resolve(snapshot, new ChatRouteInput
        {
            Channel = "lark",
            CommandName = "/summary",
        });

        snapshot.Should().NotBeNull();
        decision.UsedFallback.Should().BeFalse();
        decision.MatchedRuleId.Should().Be("lark-summary");
        decision.Action.ForwardToModel.ModelName.Should().Be("summary-model");
    }

    [Fact]
    public async Task LookupForCallerAsync_DifferentCaller_ReturnsNull()
    {
        var document = new ChatRoutePolicyCurrentStateDocument
        {
            OwnerScope = OwnerScope.ForChannel("user-2", "lark", "bot-1", "sender-2"),
            DefaultTarget = ChatRouteResolverTests.ForwardToModelAction("other-model"),
        };
        var port = new ChatRoutePolicyQueryPort(new FakePolicyReader([document]));

        var snapshot = await port.LookupForCallerAsync(ChatRouteResolverTests.CallerScope());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task LookupForCallerAsync_ChannelPolicyFallsBackToScopeOnlyPolicy()
    {
        var caller = OwnerScope.ForChannel("user-1", "lark", "bot-1", "sender-1");
        var scopeOnlyDocument = new ChatRoutePolicyCurrentStateDocument
        {
            OwnerScope = OwnerScope.ForChannel(string.Empty, "lark", "bot-1", string.Empty),
            DefaultTarget = ChatRouteResolverTests.ForwardToModelAction("scope-default-model"),
        };
        var port = new ChatRoutePolicyQueryPort(new FakePolicyReader([scopeOnlyDocument]));

        var snapshot = await port.LookupForCallerAsync(caller);

        snapshot.Should().NotBeNull();
        snapshot!.DefaultTarget.ForwardToModel.ModelName.Should().Be("scope-default-model");
    }

    [Fact]
    public async Task LookupForCallerAsync_SpecificChannelPolicyWinsBeforeScopeOnlyFallback()
    {
        var caller = OwnerScope.ForChannel("user-1", "lark", "bot-1", "sender-1");
        var scopeOnlyDocument = new ChatRoutePolicyCurrentStateDocument
        {
            OwnerScope = OwnerScope.ForChannel(string.Empty, "lark", "bot-1", string.Empty),
            DefaultTarget = ChatRouteResolverTests.ForwardToModelAction("scope-default-model"),
        };
        var specificDocument = new ChatRoutePolicyCurrentStateDocument
        {
            OwnerScope = caller.Clone(),
            DefaultTarget = ChatRouteResolverTests.ForwardToModelAction("specific-model"),
        };
        var port = new ChatRoutePolicyQueryPort(new FakePolicyReader([scopeOnlyDocument, specificDocument]));

        var snapshot = await port.LookupForCallerAsync(caller);

        snapshot.Should().NotBeNull();
        snapshot!.DefaultTarget.ForwardToModel.ModelName.Should().Be("specific-model");
    }

    private sealed class StaticFallbackProvider : IChatRouteFallbackProvider
    {
        private readonly string _modelName;

        public StaticFallbackProvider(string modelName)
        {
            _modelName = modelName;
        }

        public ChatRouteDecision GetFallbackDecision() =>
            new()
            {
                Action = ChatRouteResolverTests.ForwardToModelAction(_modelName),
                MatchedRuleId = string.Empty,
                UsedFallback = true,
            };
    }

    private sealed class FakePolicyReader : IProjectionDocumentReader<ChatRoutePolicyCurrentStateDocument, string>
    {
        private readonly IReadOnlyList<ChatRoutePolicyCurrentStateDocument> _documents;

        public FakePolicyReader(IReadOnlyList<ChatRoutePolicyCurrentStateDocument> documents)
        {
            _documents = documents;
        }

        public Task<ChatRoutePolicyCurrentStateDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<ChatRoutePolicyCurrentStateDocument?>(null);

        public Task<ProjectionDocumentQueryResult<ChatRoutePolicyCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var items = _documents
                .Where(document => query.Filters.All(filter => Matches(document, filter)))
                .Take(query.Take)
                .ToArray();
            return Task.FromResult(new ProjectionDocumentQueryResult<ChatRoutePolicyCurrentStateDocument>
            {
                Items = items,
            });
        }

        private static bool Matches(ChatRoutePolicyCurrentStateDocument document, ProjectionDocumentFilter filter)
        {
            var expected = filter.Value.RawValue as string ?? string.Empty;
            var actual = filter.FieldPath switch
            {
                "OwnerScope.NyxUserId" => document.OwnerScope?.NyxUserId ?? string.Empty,
                "OwnerScope.Platform" => document.OwnerScope?.Platform ?? string.Empty,
                "OwnerScope.RegistrationScopeId" => document.OwnerScope?.RegistrationScopeId ?? string.Empty,
                "OwnerScope.SenderId" => document.OwnerScope?.SenderId ?? string.Empty,
                _ => string.Empty,
            };

            return filter.Operator == ProjectionDocumentFilterOperator.Eq &&
                   string.Equals(actual, expected, StringComparison.Ordinal);
        }
    }
}
