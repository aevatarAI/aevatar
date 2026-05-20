using Aevatar.ChatRouting.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatRouting;
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
            OwnerScope = ToChatRouteCallerScope(ChatRouteResolverTests.CallerScope()),
            DefaultTarget = ChatRouteResolverTests.ForwardToModelAction("default-model"),
        };
        document.Rules.Add(new ChatRouteRule
        {
            RuleId = "lark-daily",
            Priority = 10,
            Match = new ChatRouteMatch { Channel = "lark", CommandName = "/daily" },
            Action = ChatRouteResolverTests.ForwardToModelAction("daily-model"),
        });
        var port = new ChatRoutePolicyQueryPort(new FakePolicyReader([document]));
        var resolver = new ChatRouteResolver(new StaticFallbackProvider("fallback-model"));

        var snapshot = await port.LookupForCallerAsync(ChatRouteResolverTests.CallerScope());
        var decision = resolver.Resolve(snapshot, new ChatRouteInput
        {
            Channel = "lark",
            CommandName = "/daily",
        });

        snapshot.Should().NotBeNull();
        decision.UsedFallback.Should().BeFalse();
        decision.MatchedRuleId.Should().Be("lark-daily");
        decision.Action.ForwardToModel.ModelName.Should().Be("daily-model");
    }

    [Fact]
    public async Task LookupForCallerAsync_DifferentCaller_ReturnsNull()
    {
        var document = new ChatRoutePolicyCurrentStateDocument
        {
            OwnerScope = ToChatRouteCallerScope(OwnerScope.ForChannel("user-2", "lark", "bot-1", "sender-2")),
            DefaultTarget = ChatRouteResolverTests.ForwardToModelAction("other-model"),
        };
        var port = new ChatRoutePolicyQueryPort(new FakePolicyReader([document]));

        var snapshot = await port.LookupForCallerAsync(ChatRouteResolverTests.CallerScope());

        snapshot.Should().BeNull();
    }

    private static ChatRouteCallerScope ToChatRouteCallerScope(OwnerScope scope) =>
        new()
        {
            NyxUserId = scope.NyxUserId,
            Platform = scope.Platform,
            RegistrationScopeId = scope.RegistrationScopeId,
            SenderId = scope.SenderId,
        };

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
