using System.Net;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdServiceInstanceClientTests
{
    private const string ReadyKey = """
        {"id":"us-personal","slug":"github","catalog_service_id":"catalog-github",
         "catalog_service_slug":"api-github","is_active":true,"connected":true,"status":"active",
         "credential_source":{"type":"personal"}}
        """;

    [Fact]
    public void AddNyxIdApiAccess_Configuration_BindsClientCredentialsAndSkillCreationTemplates()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyx.test",
                ["Aevatar:NyxId:ClientId"] = "nyx-client",
                ["Aevatar:NyxId:ClientSecret"] = "nyx-secret",
                ["Aevatar:NyxId:ClientCredentialsScope"] = "ornn.publish",
                ["Aevatar:NyxId:RecommendedSkillCreationTemplates:0:CatalogServiceSlug"] = "api-github",
                ["Aevatar:NyxId:RecommendedSkillCreationTemplates:0:SkillName"] = "github-service-default",
                ["Aevatar:NyxId:RecommendedSkillCreationTemplates:0:Description"] = "GitHub service default skill",
                ["Aevatar:NyxId:RecommendedSkillCreationTemplates:0:InstructionsMarkdown"] = "Use exact GitHub service tools.",
                ["Aevatar:NyxId:RecommendedSkillCreationTemplates:0:ToolList:0"] = "nyxop_list_repositories",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddNyxIdApiAccess(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<NyxIdToolOptions>();
        options.ClientId.Should().Be("nyx-client");
        options.ClientSecret.Should().Be("nyx-secret");
        options.ClientCredentialsScope.Should().Be("ornn.publish");
        var template = options.RecommendedSkillCreationTemplates.Should().ContainSingle().Subject;
        template.SkillName.Should().Be("github-service-default");
        template.ToolList.Should().ContainSingle().Which.Should().Be("nyxop_list_repositories");
    }

    [Fact]
    public async Task ReadAsync_UserAndOrganizationCredentials_ReadKeysExecutionInventory()
    {
        var handler = new InventoryHandler();
        handler.KeysByToken["user-token"] = Keys(ReadyKey);
        handler.KeysByToken["org-token"] = Keys(ReadyKey
            .Replace("us-personal", "us-org", StringComparison.Ordinal)
            .Replace("\"type\":\"personal\"", "\"type\":\"org\",\"allowed\":true", StringComparison.Ordinal));

        var inventory = await CreateReader(handler).ReadAsync("user-token", "org-token");

        inventory.Instances.Select(instance => instance.UserServiceId).Should().Equal("us-org", "us-personal");
        inventory.Instances.Select(instance => instance.AccessTokenSource).Should().Equal(
            NyxIdServiceAccessTokenSource.Organization, NyxIdServiceAccessTokenSource.User);
        inventory.Instances.Should().OnlyContain(instance => instance.CatalogServiceSlug == "api-github");
        handler.Requests.Should().Equal(
            ("/api/v1/keys", "user-token"), ("/api/v1/keys", "org-token"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{unsafe-provider-secret")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"services\":[]}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"keys\":null}")]
    [InlineData("{\"keys\":{}}")]
    [InlineData("{\"keys\":[null]}")]
    public async Task ReadAsync_MalformedKeysEnvelope_ThrowsBoundedContractError(string response)
    {
        var handler = new InventoryHandler();
        handler.KeysByToken["user-token"] = response;

        var act = () => CreateReader(handler).ReadAsync("user-token", organizationToken: null);

        var error = await act.Should().ThrowAsync<NyxIdServiceInventoryContractException>()
            .WithMessage("NYXID_SERVICE_INVENTORY_CONTRACT_INVALID");
        error.Which.InnerException.Should().BeNull();
        error.Which.ToString().Should().NotContain("unsafe-provider-secret").And.NotContain("user-token");
    }

    [Theory]
    [InlineData("\"connected\":true,", "")]
    [InlineData("\"connected\":true", "\"connected\":\"true\"")]
    [InlineData("\"status\":\"active\",", "")]
    [InlineData("\"status\":\"active\"", "\"status\":null")]
    [InlineData("\"status\":\"active\"", "\"status\":\"new-unknown-status\"")]
    [InlineData("\"is_active\":true,", "")]
    [InlineData("\"is_active\":true", "\"is_active\":\"true\"")]
    [InlineData("\"connected\":true", "\"connected\":true,\"node_id\":\"node-alpha\"")]
    [InlineData("\"connected\":true", "\"connected\":true,\"node_id\":42")]
    [InlineData("\"connected\":true", "\"connected\":true,\"node_status\":\"online\"")]
    public async Task ReadAsync_MissingOrInvalidReadiness_ThrowsInsteadOfReturningPartialInventory(
        string oldValue, string newValue)
    {
        var handler = new InventoryHandler();
        var malformedKey = ReadyKey.Replace("us-personal", "us-malformed", StringComparison.Ordinal)
            .Replace(oldValue, newValue, StringComparison.Ordinal);
        handler.KeysByToken["user-token"] = Keys(ReadyKey, malformedKey);

        var act = () => CreateReader(handler).ReadAsync("user-token", organizationToken: null);

        await act.Should().ThrowAsync<NyxIdServiceInventoryContractException>()
            .WithMessage("NYXID_SERVICE_INVENTORY_CONTRACT_INVALID");
    }

    [Fact]
    public async Task ReadAsync_RecommendedSkillRefs_MapsExactOrnnReferenceWithoutCatalogBackfill()
    {
        var handler = new InventoryHandler();
        handler.KeysByToken["user-token"] = Keys("""
            {"id":"us-personal","slug":"calendar","catalog_service_id":"catalog-calendar",
             "catalog_service_slug":"api-calendar","is_active":true,"connected":true,"status":"active",
             "credential_source":{"type":"personal"},
             "recommended_skill_refs":[{
               "source":"ornn",
               "skill_id":"11111111-1111-1111-1111-111111111111",
               "literal_version":"1.2",
               "manifest_digest":"sha256:000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f",
               "display_name":"Calendar Reader",
               "recommendation_name":"read-calendar-events",
               "revision":"rev-1"
             }]}
            """);

        var result = await CreateReader(handler).ReadAsync("user-token", organizationToken: null);

        var skillRef = result.Instances.Should().ContainSingle().Subject.RecommendedSkillRefs
            .Should().ContainSingle().Subject;
        skillRef.Source.Should().Be(NyxIdRecommendedSkillSource.Ornn);
        skillRef.SkillId.Should().Be("11111111-1111-1111-1111-111111111111");
        skillRef.LiteralVersion.Should().Be("1.2");
        skillRef.ManifestDigest.Should().StartWith("sha256:");
        skillRef.RecommendationName.Should().Be("read-calendar-events");
        skillRef.Revision.Should().Be("rev-1");

        var catalogEntry = result.RecommendedSkillCatalog.Should().ContainSingle().Subject;
        catalogEntry.UserServiceId.Should().Be("us-personal");
        catalogEntry.ServiceSlug.Should().Be("calendar");
        catalogEntry.ServiceLabel.Should().Be("calendar");
        catalogEntry.Title.Should().Be("Calendar Reader");
        catalogEntry.TaskSummary.Should().Contain("Calendar Reader").And.Contain("calendar");
        catalogEntry.SkillRef.SkillId.Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public async Task ReadAsync_MissingRecommendedSkillRefs_CreatesRefsByServiceType()
    {
        var handler = new InventoryHandler();
        handler.KeysByToken["user-token"] = Keys(ReadyKey);
        var creator = new RecordingRecommendedSkillRefCreator([
            new NyxIdRecommendedSkillRef
            {
                Source = NyxIdRecommendedSkillSource.Ornn,
                SkillId = "22222222-2222-2222-2222-222222222222",
                LiteralVersion = "2.0",
                ManifestDigest = "sha256:" + new string('2', 64),
                DisplayName = "GitHub Operator",
                RecommendationName = "github-service-default",
                Revision = "created-rev-1",
            },
        ]);

        var result = await CreateReader(handler, recommendedSkillRefCreator: creator)
            .ReadAsync("user-token", organizationToken: null);

        creator.RequestedServiceIds.Should().Equal("us-personal");
        var skillRef = result.Instances.Should().ContainSingle().Subject.RecommendedSkillRefs
            .Should().ContainSingle().Subject;
        skillRef.Source.Should().Be(NyxIdRecommendedSkillSource.Ornn);
        skillRef.SkillId.Should().Be("22222222-2222-2222-2222-222222222222");
        skillRef.LiteralVersion.Should().Be("2.0");
        skillRef.ManifestDigest.Should().Be("sha256:" + new string('2', 64));
        skillRef.RecommendationName.Should().Be("github-service-default");
        skillRef.Revision.Should().Be("created-rev-1");
    }

    [Fact]
    public async Task ReadAsync_ExistingRecommendedSkillRefs_DoNotCreateRefs()
    {
        var handler = new InventoryHandler();
        handler.KeysByToken["user-token"] = Keys("""
            {"id":"us-personal","slug":"github","catalog_service_id":"catalog-github",
             "catalog_service_slug":"api-github","is_active":true,"connected":true,"status":"active",
             "credential_source":{"type":"personal"},
             "recommended_skill_refs":[{
               "source":"ornn",
               "skill_id":"11111111-1111-1111-1111-111111111111",
               "literal_version":"1.2",
               "manifest_digest":"sha256:000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
             }]}
            """);
        var creator = new RecordingRecommendedSkillRefCreator([]);

        var result = await CreateReader(handler, recommendedSkillRefCreator: creator)
            .ReadAsync("user-token", organizationToken: null);

        creator.RequestedServiceIds.Should().BeEmpty();
        result.Instances.Should().ContainSingle().Subject.RecommendedSkillRefs
            .Should().ContainSingle().Subject.SkillId.Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public async Task ReadAsync_GenuineEmptyKeys_ReturnsEmptyInventory()
    {
        var handler = new InventoryHandler();

        var result = await CreateReader(handler).ReadAsync("user-token", "user-token");

        result.Instances.Should().BeEmpty();
        handler.Requests.Should().Equal(("/api/v1/keys", "user-token"));
    }

    [Theory]
    [InlineData("expired", true, null)]
    [InlineData("revoked", true, null)]
    [InlineData("failed", true, null)]
    [InlineData("refresh_failed", true, null)]
    [InlineData("pending_auth", true, null)]
    [InlineData("active", false, null)]
    [InlineData("active", true, "offline")]
    [InlineData("active", true, "draining")]
    [InlineData("active", true, "unknown")]
    [InlineData("active", true, "inaccessible")]
    public async Task ReadAsync_KnownNonExecutableReadiness_RemainsVisibleWithoutBeingExecutable(
        string status, bool connected, string? nodeStatus)
    {
        var handler = new InventoryHandler();
        var key = ReadyKey.Replace("\"status\":\"active\"", $"\"status\":\"{status}\"", StringComparison.Ordinal)
            .Replace("\"connected\":true", $"\"connected\":{connected.ToString().ToLowerInvariant()}", StringComparison.Ordinal);
        if (nodeStatus is not null)
            key = key.Replace("\"is_active\":true", $"\"is_active\":true,\"node_id\":\"node-alpha\",\"node_status\":\"{nodeStatus}\"", StringComparison.Ordinal);
        handler.KeysByToken["user-token"] = Keys(key);

        var result = await CreateReader(handler).ReadAsync("user-token", organizationToken: null);

        var instance = result.Instances.Should().ContainSingle().Subject;
        NyxIdServiceInstanceClient.IsCallerExecutable(instance).Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_InactiveOrForbiddenKeys_AreExcludedWithoutContractFailure()
    {
        var handler = new InventoryHandler();
        handler.KeysByToken["user-token"] = Keys(
            ReadyKey.Replace("\"is_active\":true", "\"is_active\":false", StringComparison.Ordinal),
            ReadyKey.Replace("us-personal", "us-forbidden", StringComparison.Ordinal)
                .Replace("\"type\":\"personal\"", "\"type\":\"org\",\"allowed\":false", StringComparison.Ordinal));

        var result = await CreateReader(handler).ReadAsync("user-token", organizationToken: null);

        result.Instances.Should().BeEmpty();
    }

    private static NyxIdConnectedServiceInventoryReader CreateReader(
        InventoryHandler handler,
        NyxIdToolOptions? options = null,
        INyxIdRecommendedSkillRefCreator? recommendedSkillRefCreator = null)
    {
        options ??= new NyxIdToolOptions { BaseUrl = "https://nyx.test" };
        return new NyxIdConnectedServiceInventoryReader(new NyxIdServiceInstanceClient(
            new NyxIdApiClient(options, new HttpClient(handler)),
            recommendedSkillRefCreator));
    }

    private static string Keys(params string[] keys) => $$"""{"keys":[{{string.Join(',', keys)}}]}""";

    private sealed class RecordingRecommendedSkillRefCreator(
        IReadOnlyList<NyxIdRecommendedSkillRef> refs) : INyxIdRecommendedSkillRefCreator
    {
        public List<string> RequestedServiceIds { get; } = [];

        public Task<IReadOnlyList<NyxIdRecommendedSkillRef>> CreateRecommendedSkillRefsAsync(
            NyxIdServiceInstance instance,
            CancellationToken ct)
        {
            RequestedServiceIds.Add(instance.UserServiceId);
            return Task.FromResult(refs);
        }
    }

    private sealed class InventoryHandler : HttpMessageHandler
    {
        public Dictionary<string, string> KeysByToken { get; } = new(StringComparer.Ordinal);
        public List<(string Path, string Token)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            Requests.Add((path, token));
            var response = path switch
            {
                "/api/v1/keys" => KeysByToken.GetValueOrDefault(token, "{\"keys\":[]}"),
                // The management surface has routing identity but no execution readiness.
                "/api/v1/user-services" => """
                    {"services":[{"id":"us-management-only","slug":"github","is_active":true,
                    "credential_source":{"type":"personal"}}]}
                    """,
                _ => throw new InvalidOperationException("unexpected_inventory_route"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            });
        }
    }
}
